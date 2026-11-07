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
# Either name: the one-liner test runs the release that is out, and the rename reaches it with the next release.
dash() { for f in "$app_dir/Dockyard Dashboard" "$app_dir/Dockyard.Dashboard"; do [ -e "$f" ] && { echo "$f"; return; }; done; echo "$app_dir/Dockyard Dashboard"; }
marker=/etc/dockyard/installed

echo "== wrong arguments are refused"
rc=0; run --silent --typo >/dev/null 2>&1 || rc=$?
[ "$rc" -eq 2 ] && ok "exit 2" || fail "--typo exits $rc"
check "and nothing was installed" missing "$marker"

echo "== first install, no dashboard"
run --silent --no-dashboard --no-start
check "app in place" test -x "$app_dir/Dockyard"
check "no dashboard" missing "$(dash)"
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
  # Its packaged config path: AppArmor keeps tinyproxy from reading one anywhere else.
  printf 'User tinyproxy\nGroup tinyproxy\nPort 18888\nListen 127.0.0.1\nTimeout 60\nAllow 127.0.0.1\nLogFile "/var/log/tinyproxy/tinyproxy.log"\nLogLevel Connect\nMaxClients 20\n' | sudo tee /etc/tinyproxy/tinyproxy.conf >/dev/null
  sudo systemctl start tinyproxy
  HTTP_PROXY=http://127.0.0.1:18888 HTTPS_PROXY=http://127.0.0.1:18888 run --silent --no-start >/dev/null
  grep -q 'http://198.19.250.254:18888' /etc/docker/daemon.json && ok "daemon.json has the proxy, through the agent" || fail "daemon.json: $(cat /etc/docker/daemon.json)"
  docker version >/dev/null; sleep 2
  docker pull -q busybox:1.37 >/dev/null || fail "pull through the proxy"
  sudo grep -q 'CONNECT.*docker' /var/log/tinyproxy/tinyproxy.log && ok "the pull went through the proxy" || fail "not in the proxy's log: $(sudo tail -5 /var/log/tinyproxy/tinyproxy.log)"
  run --silent --no-start >/dev/null
  if grep -q 18888 /etc/docker/daemon.json; then fail "the proxy stayed after the machine stopped using it"; else ok "gone once the machine stops using it"; fi
  sudo systemctl stop tinyproxy || true
  # This section restarts the engine more often in a minute than systemd allows; nobody else does that.
  sudo systemctl reset-failed docker.service docker.socket || true
  sudo systemctl start docker.socket
  docker version >/dev/null || fail "engine: $(systemctl status --no-pager docker.socket docker.service 2>&1 | tail -20)"
  docker image rm busybox:1.37 >/dev/null 2>&1 || true
fi

if [ "$container" -eq 0 ] && [ -n "${WIZARD:-}" ]; then
  echo "== container domains, without publishing a port"
  docker run -d --name e2e-web nginx:alpine >/dev/null
  docker run -d --name e2e-custom -l dockyard.port=8099 alpine:3.24 sh -c 'while :; do printf "HTTP/1.0 200 OK\r\n\r\nfrom-8099" | nc -l -p 8099 >/dev/null; done' >/dev/null
  sleep 3
  resolvectl query e2e-web.dockyard.test | grep -q 127.77.0.1 && ok "the name resolves on this machine" || fail "resolve: $(resolvectl query e2e-web.dockyard.test 2>&1)"
  [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 http://e2e-web.dockyard.test/)" = 200 ] && ok "http reaches it by name" || fail "http by name"
  [ "$(curl -s --max-time 15 http://e2e-custom.dockyard.test/)" = from-8099 ] && ok "the label picks the port" || fail "label port"
  [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 --resolve gone.dockyard.test:80:127.77.0.1 http://gone.dockyard.test/)" = 404 ] && ok "a name nothing answers to says so" || fail "unknown name"
  [ "$(docker run --rm alpine:3.24 wget -qO- -T 10 http://e2e-custom.dockyard.test/)" = from-8099 ] && ok "the name works inside containers too" || fail "from a container"
  if ss -ltn | grep -E ':(80|443|53)[[:space:]]' | grep -vE '127\.0\.0\.5[34]' >/dev/null; then fail "Dockyard holds port 80, 443 or 53: $(ss -ltn | grep -E ':(80|443|53)[[:space:]]')"; else ok "no port 80, 443 or 53 taken on this machine"; fi
  docker run -d --name e2e-published -p 80:80 nginx:alpine >/dev/null && ok "a container can still publish port 80" || fail "port 80 cannot be published"
  [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 http://e2e-web.dockyard.test/)" = 200 ] && ok "and domains keep working next to it" || fail "domain next to a published port 80"
  docker rm -f e2e-published >/dev/null

  echo "  agent RSS $(awk '/VmRSS/{print $2}' /proc/$(pidof dockyard-agent)/status) kB, for the record"

  echo "== trusted HTTPS"
  [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 https://e2e-web.dockyard.test/ || true)" = 000 ] && ok "without trusting, no HTTPS" || fail "HTTPS before it was turned on"
  run --silent --no-start --trust-https
  # Setup restarted the agent and the resolver, and the agent looks containers up again when it starts.
  i=0; while [ $i -lt 20 ] && [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 https://e2e-web.dockyard.test/ || true)" != 200 ]; do i=$((i+1)); sleep 1; done
  [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 https://e2e-web.dockyard.test/)" = 200 ] && ok "https is trusted by the system" || fail "https: $(curl -v --max-time 15 https://e2e-web.dockyard.test/ 2>&1 | tail -5)"
  key="$HOME/.config/Dockyard/ca/ca.key"
  [ "$(stat -c %a "$key")" = 600 ] && ok "the key is the owner's only" || fail "key mode $(stat -c %a "$key")"
  openssl x509 -in "$HOME/.config/Dockyard/ca/ca.pem" -noout -text | grep -A2 'Name Constraints: critical' | grep -q 'DNS:dockyard.test' && ok "the CA is name constrained" || fail "name constraint"
  rogue="$(mktemp -d)"
  openssl req -new -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -keyout "$rogue/k" -out "$rogue/r" -subj /CN=www.example.com 2>/dev/null
  printf 'subjectAltName=DNS:www.example.com\n' > "$rogue/x"
  openssl x509 -req -in "$rogue/r" -CA "$HOME/.config/Dockyard/ca/ca.pem" -CAkey "$key" -CAcreateserial -days 2 -extfile "$rogue/x" -out "$rogue/c" 2>/dev/null
  if openssl verify -CAfile "$HOME/.config/Dockyard/ca/ca.pem" -x509_strict "$rogue/c" >/dev/null 2>&1; then fail "the CA vouched for another name"; else ok "a certificate for another name does not validate, even signed with the real key"; fi
  rm -rf "$rogue"
  docker rm -f e2e-web e2e-custom >/dev/null
fi

echo "== rerun keeps the choice and the data"
run --silent --no-start
check "still no dashboard" missing "$(dash)"
if [ "$container" -eq 0 ]; then
  docker volume inspect dockyard-e2e >/dev/null && ok "marker volume kept" || fail "marker volume gone"
fi

echo "== add the dashboard"
run --silent --dashboard --no-start
check "dashboard added" test -x "$(dash)"
check "with its libraries" test -f "$app_dir/libSkiaSharp.so"

if [ "$container" -eq 0 ] && [ -n "${WIZARD:-}" ]; then
  echo "== the process name"
  check "the new file name" test -x "$app_dir/Dockyard Dashboard"
  check "no old file" missing "$app_dir/Dockyard.Dashboard"
  echo "  for the record, /proc comm of that file name is 15 characters: $(printf 'Dockyard Dashboard' | cut -c1-15)"
fi

if [ "$container" -eq 0 ] && [ -n "${WIZARD:-}" ]; then
  echo "== any component can go and come back, and required ones refuse"
  run --silent --no-start --without compose
  # By the package: this runner also has a compose of its own outside any package.
  if dpkg -s docker-compose-plugin 2>/dev/null | grep -q 'Status: install ok'; then fail "compose is still there"; else ok "compose is gone"; fi
  run --silent --no-start
  if dpkg -s docker-compose-plugin 2>/dev/null | grep -q 'Status: install ok'; then fail "a rerun brought compose back"; else ok "it stays gone on a rerun"; fi
  run --silent --no-start --with compose
  dpkg -s docker-compose-plugin 2>/dev/null | grep -q 'Status: install ok' && ok "it is back" || fail "compose did not come back"
  rc=0; run --silent --no-start --without linux-engine >/dev/null 2>&1 || rc=$?
  [ "$rc" -eq 2 ] && ok "the engine is required here" || fail "--without linux-engine exits $rc"
fi

echo "== rerun keeps it"
run --silent --no-start
check "dashboard kept" test -x "$(dash)"

echo "== remove the dashboard"
run --silent --no-dashboard --no-start
check "dashboard removed" missing "$(dash)"
check "app stays" test -x "$app_dir/Dockyard"

echo "== uninstall keeps docker's data"
run --silent --uninstall
check "app removed" missing "$app_dir/Dockyard"
check "setup undone" missing "$marker"
if [ "$container" -eq 0 ]; then
  check "the agent is gone" missing /etc/systemd/system/dockyard-agent.service
  check "the CA is out of the trust store" missing /usr/local/share/ca-certificates/dockyard-local-ca.crt
  if grep -rl "Dockyard Local CA" /etc/ssl/certs >/dev/null 2>&1; then fail "the CA stayed in the system's bundle"; else ok "and out of the system's bundle"; fi
  check "the key is gone" missing "$HOME/.config/Dockyard/ca/ca.key"
  check "names no longer route to Dockyard" missing /etc/systemd/resolved.conf.d/dockyard.conf
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
check "dashboard there" test -x "$(dash)"

if [ "$container" -eq 1 ] && [ "$ID" != arch ]; then
  echo "== removed again, silent without root has no way up"
  run --silent --uninstall
  useradd -m dockyard-e2e 2>/dev/null || true
  if [ -n "${ONELINER:-}" ]; then as_user="curl -fsSL $ONELINER | sh -s -- --silent --no-start"; else as_user="$WIZARD --silent --no-start"; fi
  out="$(su dockyard-e2e -c "$as_user" 2>&1)" && fail "exited 0 without root"
  echo "$out" | grep -q "needs root" && ok "non-zero with a clear message" || fail "message was: $out"
fi
echo "All good."
