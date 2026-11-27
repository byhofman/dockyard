#!/bin/sh
# End to end on Linux: DOCKYARD=<the dockyard command> sh setup-linux.sh [--container], with the release's other files
# next to it, or ONELINER=<url of install.sh> to go through the one-liner. --container: no systemd, running as root.
set -eu
container=0; [ "${1:-}" = --container ] && container=1

# The one-liner runs dockyard setup with its arguments; other commands go to the dockyard it installed.
setup() {
  if [ -n "${ONELINER:-}" ]; then curl -fsSL "$ONELINER" | sh -s -- "$@"; else "$DOCKYARD" setup "$@" --assets "$(dirname "$DOCKYARD")"; fi
}
dockyard() {
  if [ -z "${ONELINER:-}" ]; then "$DOCKYARD" "$@"; elif [ -x /usr/bin/dockyard ]; then /usr/bin/dockyard "$@"; else "$HOME/.local/bin/dockyard" "$@"; fi
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
dash() { echo "$app_dir/dockyard-dashboard"; }
marker=/etc/dockyard/installed

echo "== wrong arguments are refused"
rc=0; setup --silent --typo >/dev/null 2>&1 || rc=$?
[ "$rc" -eq 2 ] && ok "exit 2" || fail "--typo exits $rc"
check "and nothing was installed" missing "$marker"

# GitHub's runner image comes with a kind of its own in /usr/local/bin, which Dockyard would rightly leave alone.
if [ "$container" -eq 0 ] && [ -n "${DOCKYARD:-}" ]; then sudo rm -f /usr/local/bin/kind; fi

if [ "$container" -eq 0 ] && [ -n "${DOCKYARD:-}" ]; then
  # Some of GitHub's runners come with a daemon.json naming their DNS server and some with none: start every run from the
  # first, and the later fresh install from the second.
  upstream="$(awk '$1 == "nameserver" { print $2; exit }' /run/systemd/resolve/resolv.conf)"
  printf '{"dns": ["%s"]}\n' "$upstream" | sudo tee /etc/docker/daemon.json >/dev/null
fi

echo "== first install, no dashboard"
setup --silent --without dashboard --no-start
check "the tray in place" test -x "$app_dir/dockyard-tray"
check "the command in place" test -x "$app_dir/dockyard-cli"
check "no dashboard" missing "$(dash)"
check "setup ran" test -f "$marker"
check "docker installed" command -v dockerd
if [ "$container" -eq 0 ]; then
  check "tray autostarts" test -f "$HOME/.config/autostart/dockyard.desktop"
  grep -q "Exec=\"$app_dir/dockyard-tray\"" "$HOME/.config/autostart/dockyard.desktop" && ok "autostart points at the installed tray" || fail "autostart target"
  check "dockyard is on the PATH" test -x /usr/bin/dockyard

  echo "== docker works in this session, without signing out"
  sudo systemctl stop docker.service docker.socket
  sudo systemctl start docker.socket
  getfacl -p /run/docker.sock | grep -q "^user:$(id -un):rw" && ok "ACL on a recreated socket" || fail "ACL on the socket"
  docker volume create dockyard-e2e >/dev/null
  ok "marker volume created"
fi

if [ "$container" -eq 0 ] && [ -n "${DOCKYARD:-}" ]; then
  echo "== host.docker.internal reaches a service on this machine's loopback"
  grep -q 198.19.250.1 /etc/docker/daemon.json && ok "daemon.json names the agent" || fail "daemon.json: $(cat /etc/docker/daemon.json)"
  grep -qx "nameserver $upstream" /etc/dockyard/dns-upstreams && ok "in place of the server it named, which the agent asks" || fail "kept servers: $(cat /etc/dockyard/dns-upstreams 2>&1)"
  docker version >/dev/null
  sleep 2
  systemctl is-active --quiet dockyard-host.service && ok "the agent runs with the engine" || fail "agent: $(systemctl status --no-pager dockyard-host.service 2>&1 | tail -5)"
  sudo iptables -S DOCKYARD-INPUT >/dev/null 2>&1 && ok "the agent's chain is in place" || fail "no DOCKYARD-INPUT chain"
  ip addr show lo | grep -q 198.19.250.1 && ok "and its address on lo" || fail "lo: $(ip addr show lo)"
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

if [ "$container" -eq 0 ] && [ -n "${DOCKYARD:-}" ]; then
  echo "== the engine and containers follow this machine's proxy, also one on its loopback"
  sudo apt-get install -y -qq tinyproxy >/dev/null
  sudo systemctl stop tinyproxy
  # Its packaged config path: AppArmor keeps tinyproxy from reading one anywhere else.
  printf 'User tinyproxy\nGroup tinyproxy\nPort 18888\nListen 127.0.0.1\nTimeout 60\nAllow 127.0.0.1\nLogFile "/var/log/tinyproxy/tinyproxy.log"\nLogLevel Connect\nMaxClients 20\n' | sudo tee /etc/tinyproxy/tinyproxy.conf >/dev/null
  sudo systemctl start tinyproxy
  HTTP_PROXY=http://127.0.0.1:18888 HTTPS_PROXY=http://127.0.0.1:18888 setup --silent --no-start >/dev/null
  grep -q 'http://198.19.250.254:18888' /etc/docker/daemon.json && ok "daemon.json has the proxy, through the agent" || fail "daemon.json: $(cat /etc/docker/daemon.json)"
  docker version >/dev/null; sleep 2
  docker pull -q busybox:1.37 >/dev/null || fail "pull through the proxy"
  sudo grep -q 'CONNECT.*docker' /var/log/tinyproxy/tinyproxy.log && ok "the pull went through the proxy" || fail "not in the proxy's log: $(sudo tail -5 /var/log/tinyproxy/tinyproxy.log); docker uses: $(docker info --format '{{.HTTPProxy}} {{.HTTPSProxy}}' 2>&1); docker.service since $(systemctl show -p ActiveEnterTimestamp --value docker.service)"
  setup --silent --no-start >/dev/null
  if grep -q 18888 /etc/docker/daemon.json; then fail "the proxy stayed after the machine stopped using it"; else ok "gone once the machine stops using it"; fi
  sudo systemctl stop tinyproxy || true
  # This section restarts the engine more often in a minute than systemd allows; nobody else does that.
  sudo systemctl reset-failed docker.service docker.socket || true
  sudo systemctl start docker.socket
  docker version >/dev/null || fail "engine: $(systemctl status --no-pager docker.socket docker.service 2>&1 | tail -20)"
  docker image rm busybox:1.37 >/dev/null 2>&1 || true
fi

if [ "$container" -eq 0 ] && [ -n "${DOCKYARD:-}" ]; then
  echo "== container domains, without publishing a port"
  docker run -d --name e2e-web nginx:alpine >/dev/null
  docker run -d --name e2e-custom -l dockyard.port=8099 alpine:3.24 sh -c 'while :; do printf "HTTP/1.0 200 OK\r\n\r\nfrom-8099" | nc -l -p 8099 >/dev/null; done' >/dev/null
  sleep 3
  for service in dockyard-dns dockyard-proxy dockyard-etcd; do
    [ "$(docker inspect -f '{{.State.Running}} {{index .Config.Labels "dockyard.service"}}' "$service" 2>/dev/null)" = "true ${service#dockyard-}" ] && ok "$service runs in the Dockyard group" || fail "$service: $(docker ps -a --filter name=$service 2>&1 | tail -1)"
  done
  resolvectl query e2e-web.dockyard.internal | grep -q 127.77.0.1 && ok "the name resolves on this machine" || fail "resolve: $(resolvectl query e2e-web.dockyard.internal 2>&1)"
  i=0; while [ $i -lt 15 ] && [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 http://e2e-web.dockyard.internal/)" != 200 ]; do i=$((i+1)); sleep 1; done
  [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 http://e2e-web.dockyard.internal/)" = 200 ] && ok "http reaches it by name" || fail "http by name"
  # A new name reaches dockyard-proxy through the agent's two-second loop and Traefik's reload.
  i=0; while [ $i -lt 15 ] && [ "$(curl -s --max-time 5 http://e2e-custom.dockyard.internal/)" != from-8099 ]; do i=$((i+1)); sleep 1; done
  [ "$(curl -s --max-time 15 http://e2e-custom.dockyard.internal/)" = from-8099 ] && ok "the label picks the port" || fail "label port: $(curl -s -i --max-time 5 http://e2e-custom.dockyard.internal/ 2>&1 | head -3)"
  i=0; while [ $i -lt 10 ] && [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 --resolve gone.dockyard.internal:80:127.77.0.1 http://gone.dockyard.internal/)" != 404 ]; do i=$((i+1)); sleep 1; done
  [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 --resolve gone.dockyard.internal:80:127.77.0.1 http://gone.dockyard.internal/)" = 404 ] && ok "a name nothing answers to says so" || fail "unknown name: $(curl -s -i --max-time 5 --resolve gone.dockyard.internal:80:127.77.0.1 http://gone.dockyard.internal/ 2>&1 | head -3); proxy: $(docker logs --tail 5 dockyard-proxy 2>&1)"
  [ "$(docker run --rm alpine:3.24 wget -qO- -T 10 http://e2e-custom.dockyard.internal/)" = from-8099 ] && ok "the name works inside containers too" || fail "from a container"
  if ss -ltn | grep -E ':(80|443|53)[[:space:]]' | grep -vE '127\.0\.0\.5[34]' >/dev/null; then fail "Dockyard holds port 80, 443 or 53: $(ss -ltn | grep -E ':(80|443|53)[[:space:]]')"; else ok "no port 80, 443 or 53 taken on this machine"; fi
  docker run -d --name e2e-published -p 80:80 nginx:alpine >/dev/null && ok "a container can still publish port 80" || fail "port 80 cannot be published"
  [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 http://e2e-web.dockyard.internal/)" = 200 ] && ok "and domains keep working next to it" || fail "domain next to a published port 80"
  docker rm -f e2e-published >/dev/null

  echo "  agent RSS $(awk '/VmRSS/{print $2}' /proc/$(pidof dockyard-agent)/status) kB, for the record"

  echo "== trusted HTTPS"
  [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 https://e2e-web.dockyard.internal/ || true)" = 000 ] && ok "without trusting, no HTTPS" || fail "HTTPS before it was turned on"
  setup --silent --no-start --trust-https
  # Setup restarted the agent and the resolver, and the agent looks containers up again when it starts.
  i=0; while [ $i -lt 20 ] && [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 https://e2e-web.dockyard.internal/ || true)" != 200 ]; do i=$((i+1)); sleep 1; done
  [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 https://e2e-web.dockyard.internal/)" = 200 ] && ok "https is trusted by the system" || fail "https: $(curl -v --max-time 15 https://e2e-web.dockyard.internal/ 2>&1 | tail -5)"
  key="$HOME/.config/Dockyard/ca/ca.key"
  [ "$(stat -c %a "$key")" = 600 ] && ok "the key is the owner's only" || fail "key mode $(stat -c %a "$key")"
  openssl x509 -in "$HOME/.config/Dockyard/ca/ca.pem" -noout -text | grep -A2 'Name Constraints: critical' | grep -q 'DNS:dockyard.internal' && ok "the CA is name constrained" || fail "name constraint"
  rogue="$(mktemp -d)"
  openssl req -new -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -keyout "$rogue/k" -out "$rogue/r" -subj /CN=www.example.com 2>/dev/null
  printf 'subjectAltName=DNS:www.example.com\n' > "$rogue/x"
  openssl x509 -req -in "$rogue/r" -CA "$HOME/.config/Dockyard/ca/ca.pem" -CAkey "$key" -CAcreateserial -days 2 -extfile "$rogue/x" -out "$rogue/c" 2>/dev/null
  if openssl verify -CAfile "$HOME/.config/Dockyard/ca/ca.pem" -x509_strict "$rogue/c" >/dev/null 2>&1; then fail "the CA vouched for another name"; else ok "a certificate for another name does not validate, even signed with the real key"; fi
  rm -rf "$rogue"
  docker rm -f e2e-web e2e-custom >/dev/null
fi

if [ "$container" -eq 0 ] && [ -n "${DOCKYARD:-}" ]; then
  echo "== kubectl and helm: from their apt or dnf repositories"
  for tool in kubectl helm; do
    if command -v dpkg-query >/dev/null 2>&1; then
      dpkg-query -W -f '${db:Status-Status}' "$tool" 2>/dev/null | grep -qx installed && ok "$tool is a package" || fail "$tool package"
    else
      rpm -q "$tool" >/dev/null && ok "$tool is a package" || fail "$tool package"
    fi
    check "and not a copy of Dockyard's" missing "/etc/dockyard/$tool"
  done
  /usr/bin/kubectl version --client >/dev/null && ok "kubectl runs" || fail "kubectl version"
  /usr/bin/helm version --short >/dev/null && ok "helm runs" || fail "helm version"

  echo "== kind: installed, and a cluster from create to delete"
  check "kind is on the PATH" test -x /usr/local/bin/kind
  check "marked as Dockyard's" test -f /etc/dockyard/kind
  kind version | grep -q '^kind v' && ok "it runs: $(kind version)" || fail "kind version"
  kind create cluster --name e2e --wait 180s || fail "kind create cluster"
  kind get clusters | grep -qx e2e && ok "kind lists it" || fail "kind get clusters: $(kind get clusters 2>&1)"
  docker ps --filter label=io.x-k8s.kind.cluster=e2e --format '{{.Names}}' | grep -qx e2e-control-plane && ok "its node is a container with kind's labels" || fail "node labels"
  server="$(kind get kubeconfig --name e2e | sed -n 's/.*server: //p')"
  case "$server" in https://127.0.0.1:*) ok "kubeconfig points at $server" ;; *) fail "kubeconfig server: '$server'" ;; esac
  [ "$(curl -sk --max-time 10 "$server/livez")" = ok ] && ok "the API server answers there" || fail "API server at $server"
  grep -q 'current-context: kind-e2e' "$HOME/.kube/config" && ok "the context is kind-e2e" || fail "context: $(grep current-context "$HOME/.kube/config")"
  kind delete cluster --name e2e
  [ -z "$(docker ps -aq --filter label=io.x-k8s.kind.cluster=e2e)" ] && ok "deleting leaves no node" || fail "nodes left after delete"

  echo "== kind can go and come back, and someone else's kind is left alone"
  setup --silent --no-start --without kind
  check "kind is gone" missing /usr/local/bin/kind
  check "and its marker" missing /etc/dockyard/kind
  setup --silent --no-start
  check "it stays gone on a rerun" missing /usr/local/bin/kind
  printf '#!/bin/sh\necho mine\n' | sudo tee /usr/local/bin/kind >/dev/null
  sudo chmod +x /usr/local/bin/kind
  setup --silent --no-start --with kind
  [ "$(/usr/local/bin/kind)" = mine ] && ok "a kind Dockyard did not install stays" || fail "the user's kind was replaced"
  sudo rm /usr/local/bin/kind
  setup --silent --no-start --with kind
  check "it is back" test -f /etc/dockyard/kind
  kind version >/dev/null && ok "and runs" || fail "kind after --with kind"
fi

echo "== rerun keeps the choice and the data"
setup --silent --no-start
check "still no dashboard" missing "$(dash)"
if [ "$container" -eq 0 ]; then
  docker volume inspect dockyard-e2e >/dev/null && ok "marker volume kept" || fail "marker volume gone"
fi

echo "== add the dashboard"
setup --silent --with dashboard --no-start
check "dashboard added" test -x "$(dash)"
check "with its libraries" test -f "$app_dir/libSkiaSharp.so"

if [ "$container" -eq 0 ] && [ -n "${DOCKYARD:-}" ]; then
  echo "== the process name"
  check "the file name" test -x "$app_dir/dockyard-dashboard"
  echo "  for the record, /proc comm of that file name is 15 characters: $(printf 'dockyard-dashboard' | cut -c1-15)"
fi

if [ "$container" -eq 0 ] && [ -n "${DOCKYARD:-}" ]; then
  echo "== any component can go and come back, and required ones refuse"
  setup --silent --no-start --without compose
  # By the package: this runner also has a compose of its own outside any package.
  if dpkg -s docker-compose-plugin 2>/dev/null | grep -q 'Status: install ok'; then fail "compose is still there"; else ok "compose is gone"; fi
  setup --silent --no-start
  if dpkg -s docker-compose-plugin 2>/dev/null | grep -q 'Status: install ok'; then fail "a rerun brought compose back"; else ok "it stays gone on a rerun"; fi
  setup --silent --no-start --with compose
  dpkg -s docker-compose-plugin 2>/dev/null | grep -q 'Status: install ok' && ok "it is back" || fail "compose did not come back"
  rc=0; setup --silent --no-start --without linux-engine >/dev/null 2>&1 || rc=$?
  [ "$rc" -eq 2 ] && ok "the engine is required here" || fail "--without linux-engine exits $rc"
fi

echo "== rerun keeps it"
setup --silent --no-start
check "dashboard kept" test -x "$(dash)"

echo "== remove the dashboard"
setup --silent --without dashboard --no-start
check "dashboard removed" missing "$(dash)"
check "the tray stays" test -x "$app_dir/dockyard-tray"
check "and the libraries it links" test -f "$app_dir/libSkiaSharp.so"

echo "== uninstall keeps docker's data"
dockyard uninstall
check "the tray removed" missing "$app_dir/dockyard-tray"
check "the command removed" missing "$app_dir/dockyard-cli"
check "setup undone" missing "$marker"
if [ "$container" -eq 0 ]; then
  check "the agent is gone" missing /etc/systemd/system/dockyard-host.service
  [ -n "${DOCKYARD:-}" ] && check "kind is gone" missing /usr/local/bin/kind
  check "the CA is out of the trust store" missing /usr/local/share/ca-certificates/dockyard-local-ca.crt
  if grep -rl "Dockyard Local CA" /etc/ssl/certs >/dev/null 2>&1; then fail "the CA stayed in the system's bundle"; else ok "and out of the system's bundle"; fi
  check "the key is gone" missing "$HOME/.config/Dockyard/ca/ca.key"
  check "names no longer route to Dockyard" missing /etc/systemd/resolved.conf.d/dockyard.conf
  if [ -f /etc/docker/daemon.json ] && grep -q 198.19.250.1 /etc/docker/daemon.json; then fail "daemon.json still names the agent"; else ok "daemon.json no longer names the agent"; fi
  if [ -n "${DOCKYARD:-}" ]; then
    grep -q "\"$upstream\"" /etc/docker/daemon.json && ok "and names its own server again" || fail "daemon.json: $(cat /etc/docker/daemon.json)"
  fi
  if ip addr show lo | grep -q 198.19.250.; then fail "lo kept the agent's addresses: $(ip addr show lo)"; else ok "lo no longer has the agent's addresses"; fi
  left="$( { sudo iptables -S; sudo iptables -t nat -S; } | grep -E '198\.19\.250\.|127\.77\.0\.1|DOCKYARD-INPUT|--dports 2378,2380,2381 ' || true)"
  [ -z "$left" ] && ok "no rule of the agent's is left" || fail "rules left after uninstall: $left"
fi
if [ "$container" -eq 0 ]; then
  sudo systemctl reset-failed docker.socket docker.service || true
  sudo systemctl restart docker.socket docker.service || sudo systemctl status --no-pager docker.socket docker.service || true
  docker volume inspect dockyard-e2e >/dev/null && ok "marker volume kept" || fail "marker volume gone"
  docker volume rm dockyard-e2e >/dev/null
fi

if [ "$container" -eq 0 ] && [ -n "${DOCKYARD:-}" ]; then sudo rm -f /etc/docker/daemon.json; fi
echo "== a fresh install gets the dashboard"
setup --silent --no-start
check "dashboard there" test -x "$(dash)"
if [ "$container" -eq 0 ] && [ -n "${DOCKYARD:-}" ]; then
  grep -q 198.19.250.1 /etc/docker/daemon.json && ok "a daemon.json of its own names the agent" || fail "daemon.json: $(cat /etc/docker/daemon.json 2>&1)"
  check "with no servers kept" missing /etc/dockyard/dns-upstreams
fi

if [ "$container" -eq 1 ] && [ "$ID" != arch ]; then
  echo "== removed again, silent without root has no way up"
  dockyard uninstall
  useradd -m dockyard-e2e 2>/dev/null || true
  if [ -n "${ONELINER:-}" ]; then as_user="curl -fsSL $ONELINER | sh -s -- --silent --no-start"; else as_user="$DOCKYARD setup --silent --no-start --assets $(dirname "$DOCKYARD")"; fi
  out="$(su dockyard-e2e -c "$as_user" 2>&1)" && fail "exited 0 without root"
  echo "$out" | grep -q "needs root" && ok "non-zero with a clear message" || fail "message was: $out"
fi

if [ "$container" -eq 0 ] && [ -n "${DOCKYARD:-}" ]; then
  echo "== the dashboard on its own, without the dockyard command"
  dockyard uninstall
  unpack="$(mktemp -d)"
  tar -xzf "$(ls "$(dirname "$DOCKYARD")"/dockyard-dashboard-*-linux-x64.tar.gz)" -C "$unpack"
  "$unpack/dockyard-dashboard/dockyard-dashboard" --setup --silent --no-start --without cli
  check "the dashboard in place" test -x /usr/lib/dockyard-dashboard/dockyard-dashboard
  check "with its own tray" test -x /usr/lib/dockyard-dashboard/dockyard-tray
  check "no dockyard command" missing /usr/bin/dockyard
  check "setup ran" test -f "$marker"
  rm -rf "$unpack"
fi
echo "All good."
