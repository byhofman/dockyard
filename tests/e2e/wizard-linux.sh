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
  sudo systemctl start docker.socket
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
  out="$(su dockyard-e2e -c "$WIZARD --silent --no-start" 2>&1)" && fail "exited 0 without root"
  echo "$out" | grep -q "needs root" && ok "non-zero with a clear message" || fail "message was: $out"
fi
echo "All good."
