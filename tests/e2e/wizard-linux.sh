#!/bin/sh
# End to end on Linux: WIZARD=<the wizard binary> sh wizard-linux.sh [--container]
# or ONELINER=<url of install.sh> to go through the one-liner. --container: no systemd, running as root.
set -eu
container=0; [ "${1:-}" = --container ] && container=1

run() {
  if [ -n "${ONELINER:-}" ]; then curl -fsSL "$ONELINER" | sh -s -- "$@"; else "$WIZARD" "$@"; fi
}
ok() { echo "  ok   $1"; }
fail() { echo "  FAIL $1" >&2; exit 1; }
check() { desc="$1"; shift; if "$@"; then ok "$desc"; else fail "$desc"; fi; }
missing() { [ ! -e "$1" ]; }

if [ "$container" -eq 1 ]; then
  . /etc/os-release
  case "$ID" in
    arch) pacman -Sy --noconfirm --needed tar gzip >/dev/null; app_dir="$HOME/.local/lib/dockyard" ;;
    *) app_dir=/usr/lib/dockyard ;;
  esac
else
  app_dir=/usr/lib/dockyard
fi
dashboard="$app_dir/Dockyard.Dashboard"
marker=/etc/dockyard/installed

echo "== wrong arguments are refused"
rc=0; run --silent --typo >/dev/null 2>&1 || rc=$?
[ "$rc" -eq 2 ] && ok "exit 2" || fail "--typo exits $rc"
check "and nothing was installed" missing "$marker"

echo "== first install, no dashboard"
run --silent --no-dashboard --no-start
check "app in place" test -x "$app_dir/Dockyard"
check "no dashboard" missing "$dashboard"
check "setup ran" test -f "$marker"
check "docker installed" command -v dockerd
if [ "$container" -eq 0 ]; then
  check "tray autostarts" test -f "$HOME/.config/autostart/dockyard.desktop"
  grep -q "Exec=\"$app_dir/Dockyard\" --tray" "$HOME/.config/autostart/dockyard.desktop" && ok "autostart points at the installed app" || fail "autostart target"

  echo "== docker works in this session, without signing out"
  sudo systemctl stop docker.service docker.socket
  sudo systemctl start docker.socket
  getfacl -p /run/docker.sock | grep -q "^user:$(id -un):rw" && ok "ACL on a recreated socket" || fail "ACL on the socket"
  docker volume create dockyard-e2e >/dev/null
  ok "marker volume created"
fi

if [ "$container" -eq 0 ] && [ -n "${WIZARD:-}" ]; then
  echo "== host.docker.internal reaches a service on this machine's loopback"
  grep -q 198.19.250.1 /etc/docker/daemon.json && ok "daemon.json names the agent" || fail "daemon.json: $(cat /etc/docker/daemon.json)"
  docker version >/dev/null
  sleep 2
  systemctl is-active --quiet dockyard-agent.service && ok "the agent runs with the engine" || fail "agent: $(systemctl status --no-pager dockyard-agent.service 2>&1 | tail -5)"
  d="$(mktemp -d)"; echo from-the-host > "$d/marker"
  python3 -m http.server 18081 --bind 127.0.0.1 -d "$d" >/dev/null 2>&1 &
  service=$!
  sleep 1
  for args in "" "--network host"; do
    reply="$(docker run --rm $args alpine:3.24 wget -qO- -T 15 http://host.docker.internal:18081/marker || true)"
    [ "$reply" = from-the-host ] && ok "reached (${args:-default network})" || fail "host.docker.internal with '$args': '$reply'"
  done
  kill "$service"; rm -rf "$d"
  docker run --rm alpine:3.24 nslookup dl-cdn.alpinelinux.org >/dev/null && ok "other names still resolve" || fail "DNS"
fi

if [ "$container" -eq 0 ] && [ -n "${WIZARD:-}" ]; then
  echo "== the engine and containers follow this machine's proxy, also one on its loopback"
  sudo apt-get install -y -qq tinyproxy >/dev/null
  sudo systemctl stop tinyproxy
  printf 'Port 18888\nListen 127.0.0.1\nTimeout 60\nAllow 127.0.0.1\nLogFile "/tmp/tinyproxy.log"\nLogLevel Connect\nMaxClients 20\n' > /tmp/tinyproxy.conf
  sudo tinyproxy -c /tmp/tinyproxy.conf
  HTTP_PROXY=http://127.0.0.1:18888 HTTPS_PROXY=http://127.0.0.1:18888 run --silent --no-start >/dev/null
  grep -q 'http://198.19.250.254:18888' /etc/docker/daemon.json && ok "daemon.json has the proxy, through the agent" || fail "daemon.json: $(cat /etc/docker/daemon.json)"
  docker version >/dev/null; sleep 2
  docker pull -q busybox:1.37 >/dev/null || fail "pull through the proxy"
  sudo grep -q 'CONNECT.*docker' /tmp/tinyproxy.log && ok "the pull went through the proxy" || fail "not in the proxy's log: $(sudo tail -5 /tmp/tinyproxy.log)"
  run --silent --no-start >/dev/null
  if grep -q 18888 /etc/docker/daemon.json; then fail "the proxy stayed after the machine stopped using it"; else ok "gone once the machine stops using it"; fi
  sudo pkill tinyproxy || true
  docker image rm busybox:1.37 >/dev/null 2>&1 || true
fi

echo "== rerun keeps the choice and the data"
run --silent --no-start
check "still no dashboard" missing "$dashboard"
if [ "$container" -eq 0 ]; then
  docker volume inspect dockyard-e2e >/dev/null && ok "marker volume kept" || fail "marker volume gone"
fi

echo "== add the dashboard"
run --silent --dashboard --no-start
check "dashboard added" test -x "$dashboard"
check "with its libraries" test -f "$app_dir/libSkiaSharp.so"

echo "== rerun keeps it"
run --silent --no-start
check "dashboard kept" test -x "$dashboard"

echo "== remove the dashboard"
run --silent --no-dashboard --no-start
check "dashboard removed" missing "$dashboard"
check "app stays" test -x "$app_dir/Dockyard"

echo "== uninstall keeps docker's data"
run --silent --uninstall
check "app removed" missing "$app_dir/Dockyard"
check "setup undone" missing "$marker"
if [ "$container" -eq 0 ]; then
  check "the agent is gone" missing /etc/systemd/system/dockyard-agent.service
  if [ -f /etc/docker/daemon.json ] && grep -q 198.19.250.1 /etc/docker/daemon.json; then fail "daemon.json still names the agent"; else ok "daemon.json no longer names the agent"; fi
fi
if [ "$container" -eq 0 ]; then
  sudo systemctl reset-failed docker.socket docker.service || true
  sudo systemctl restart docker.socket docker.service || sudo systemctl status --no-pager docker.socket docker.service || true
  docker volume inspect dockyard-e2e >/dev/null && ok "marker volume kept" || fail "marker volume gone"
  docker volume rm dockyard-e2e >/dev/null
fi

echo "== a fresh install gets the dashboard"
run --silent --no-start
check "dashboard there" test -x "$dashboard"

if [ "$container" -eq 1 ] && [ "$ID" != arch ]; then
  echo "== removed again, silent without root has no way up"
  run --silent --uninstall
  useradd -m dockyard-e2e 2>/dev/null || true
  if [ -n "${ONELINER:-}" ]; then as_user="curl -fsSL $ONELINER | sh -s -- --silent --no-start"; else as_user="$WIZARD --silent --no-start"; fi
  out="$(su dockyard-e2e -c "$as_user" 2>&1)" && fail "exited 0 without root"
  echo "$out" | grep -q "needs root" && ok "non-zero with a clear message" || fail "message was: $out"
fi
echo "All good."
