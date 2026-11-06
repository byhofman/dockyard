#!/bin/sh
# Imports from a second engine that plays Docker Desktop, on a Linux runner with the wizard already installed:
#   WIZARD=<the wizard binary> sh import-linux.sh
set -eu
ok() { echo "  ok   $1"; }
fail() { echo "  FAIL $1" >&2; exit 1; }
work="$(mktemp -d /tmp/dy-import.XXXXXX)"
sock="$work/source.sock"
sudo dockerd -H "unix://$sock" --data-root "$work/data" --exec-root "$work/exec" --pidfile "$work/pid" --bridge none --iptables=false --ip6tables=false >"$work/log" 2>&1 &
trap 'sudo kill "$(sudo cat "$work/pid")" 2>/dev/null || true; sleep 2; sudo rm -rf "$work"' EXIT
i=0; until sudo docker -H "unix://$sock" version >/dev/null 2>&1; do i=$((i+1)); [ $i -lt 60 ] || { tail -20 "$work/log"; fail "the source engine did not start"; }; sleep 1; done
sudo chmod 666 "$sock"
src="docker -H unix://$sock"

$src pull -q alpine:3.24 >/dev/null
$src tag alpine:3.24 e2e-tagged:1
$src volume create e2e-data >/dev/null
$src network create e2e-net >/dev/null
$src run --name e2e-writer -v e2e-data:/data alpine:3.24 sh -c 'echo from-source > /data/file; mkdir /data/sub; echo nested > /data/sub/two; echo changed > /inside' >/dev/null
$src create --name e2e-app --network e2e-net --network-alias app --restart always -l com.docker.compose.project=e2e -e MARK=1 -v e2e-data:/data alpine:3.24 sleep 1000 >/dev/null

docker version >/dev/null
"$WIZARD" --silent --import-from "unix://$sock"

docker image inspect e2e-tagged:1 >/dev/null && ok "image with its tag" || fail "image"
[ "$(docker run --rm -v e2e-data:/d alpine:3.24 cat /d/file)" = from-source ] && ok "volume contents" || fail "volume contents"
[ "$(docker run --rm -v e2e-data:/d alpine:3.24 cat /d/sub/two)" = nested ] && ok "nested files in the volume" || fail "nested volume contents"
docker network inspect e2e-net >/dev/null && ok "network" || fail "network"
[ "$(docker inspect -f '{{.State.Status}}' e2e-app)" = created ] && ok "container there, not started" || fail "container state"
[ "$(docker inspect -f '{{.HostConfig.RestartPolicy.Name}}' e2e-app)" = always ] && ok "restart policy kept" || fail "restart policy"
[ "$(docker inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' e2e-app)" = e2e ] && ok "compose label kept" || fail "compose label"
docker inspect -f '{{json .NetworkSettings.Networks}}' e2e-app | grep -q e2e-net && ok "still on its network" || fail "network attachment"
[ "$(docker commit e2e-writer e2e-check >/dev/null; docker run --rm --entrypoint cat e2e-check /inside)" = changed ] && ok "what changed inside a container came along" || fail "container changes"

$src container inspect e2e-app >/dev/null && ok "the source still has its container" || fail "source container gone"
[ "$($src volume ls -q | grep -c e2e-data)" = 1 ] && ok "and its volume" || fail "source volume gone"
[ -z "$($src ps -aq --filter label=dockyard.helper=1)" ] && ok "no helper left on the source" || fail "helper left on the source"
if $src images --format '{{.Repository}}' | grep -q dockyard-; then fail "temporary images left on the source"; else ok "no temporary image left on the source"; fi

"$WIZARD" --silent --import-from "unix://$sock" | grep -q "Skipped container e2e-app" && ok "a second import skips what is there" || fail "second import"

docker rm -f e2e-app e2e-writer >/dev/null; docker volume rm e2e-data >/dev/null; docker network rm e2e-net >/dev/null
docker image rm -f e2e-tagged:1 e2e-check dockyard-import/e2e-app:latest dockyard-import/e2e-writer:latest >/dev/null 2>&1 || true
echo "All good."
