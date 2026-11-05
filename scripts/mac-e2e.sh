#!/bin/sh
# End-to-end check of the Dockyard VM on an Apple-silicon Mac. Run from the unpacked Dockyard folder (or the
# app's Contents/MacOS) after `./Dockyard --install`:  sh scripts/mac-e2e.sh /path/to/Dockyard
set -eu
dir="${1:?usage: mac-e2e.sh <dockyard folder>}"
docker="$HOME/.dockyard/bin/docker --context dockyard"
pass() { printf '  ok   %s\n' "$1"; }
fail() { printf '  FAIL %s\n' "$1"; exit 1; }

echo "== start"
start=$(date +%s)
"$dir/dockyard-vm" run > /tmp/dockyard-vm.log 2>&1 &
vm=$!
until $docker version >/dev/null 2>&1; do
  [ $(( $(date +%s) - start )) -lt 60 ] || fail "engine not ready after 60 s (see /tmp/dockyard-vm.log, ~/.dockyard/logs/vm-console.log)"
  sleep 0.5
done
elapsed=$(( $(date +%s) - start ))
[ $elapsed -le 3 ] && pass "engine ready in ${elapsed}s (budget 3 s)" || echo "  slow engine ready in ${elapsed}s (budget 3 s)"

echo "== containers"
$docker run --rm --privileged --pid=host alpine:3.24 nsenter -t 1 -m -- grep -q '^/dev/vdb /var/lib/docker ' /proc/mounts \
  && pass "docker data on the data disk" || fail "/var/lib/docker is not on the data disk"
[ "$($docker run --rm alpine:3.24 echo hi)" = hi ] && pass "docker run" || fail "docker run"
[ "$($docker run --rm --platform linux/amd64 alpine:3.24 uname -m)" = x86_64 ] && pass "amd64 image via Rosetta" || fail "amd64 image"

echo "== bind mounts"
tmp=$(mktemp -d "$HOME/.dockyard-e2e.XXXX")
echo from-mac > "$tmp/in.txt"
$docker run --rm -v "$tmp:/w" alpine:3.24 sh -c 'cat /w/in.txt > /w/out.txt && echo from-vm >> /w/out.txt'
[ "$(cat "$tmp/out.txt")" = "from-mac
from-vm" ] && pass "read and write through $tmp" || fail "bind mount"
rm -rf "$tmp"
for short in "$(mktemp -d /tmp/dockyard-e2e.XXXX)" "$(mktemp -d)"; do
  echo from-mac > "$short/in.txt"
  [ "$($docker run --rm -v "$short:/w" alpine:3.24 cat /w/in.txt)" = from-mac ] && pass "read through $short" || fail "bind mount of $short"
  rm -rf "$short"
done

echo "== ports"
$docker run -d --name dockyard-e2e -p 8089:80 nginx:alpine >/dev/null
sleep 2
curl -sf http://127.0.0.1:8089 >/dev/null && pass "published port on 127.0.0.1:8089" || fail "port forward"
$docker rm -f dockyard-e2e >/dev/null

echo "== compose and buildx"
cd "$(mktemp -d)"
printf 'services:\n  web:\n    image: nginx:alpine\n' > compose.yaml
$docker compose up -d >/dev/null 2>&1 && $docker compose down >/dev/null 2>&1 && pass "compose up/down" || fail "compose"
printf 'FROM alpine:3.24\nRUN echo built > /built\n' > Dockerfile
$docker buildx build -q -t dockyard-e2e-build . >/dev/null && pass "buildx build" || fail "buildx build"
$docker image rm dockyard-e2e-build >/dev/null

echo "== memory"
rss() { ps -o rss= -p $vm | tr -d ' '; }
before=$(rss)
$docker run --rm alpine:3.24 sh -c 'head -c 1500m /dev/zero | tail > /dev/null'
sleep 30
after=$(rss)
echo "  helper RSS before ${before} kB, 30 s after a 1.5 GB allocation ${after} kB"

echo "== clock"
drift=$(( $($docker run --rm alpine:3.24 date +%s) - $(date +%s) ))
[ ${drift#-} -le 2 ] && pass "guest clock within 2 s of the Mac" || fail "clock drift ${drift}s"
echo "  (sleep the Mac, wake it, and run: $docker run --rm alpine date -u; date -u)"

echo "== stop"
printf 'stop\n' | nc -U "$HOME/.dockyard/run/vm.sock" >/dev/null || true
i=0; while kill -0 $vm 2>/dev/null && [ $i -lt 30 ]; do sleep 1; i=$((i + 1)); done
kill -0 $vm 2>/dev/null && fail "VM still running 30 s after stop" || pass "clean stop in ${i}s"
echo PASS
