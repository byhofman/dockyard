#!/bin/sh
# With a firewall that denies what comes in by default, containers still reach this machine and domains:
#   WIZARD=<the wizard binary> sh firewall-linux.sh
set -eu
ok() { echo "  ok   $1"; }
fail() { echo "  FAIL $1" >&2; exit 1; }

"$WIZARD" --silent --no-start
docker version >/dev/null
sleep 2
d="$(mktemp -d)"; echo from-the-host > "$d/marker"
python3 -m http.server 18081 --bind 127.0.0.1 -d "$d" >/dev/null 2>&1 &
service=$!
docker network create e2e-fw >/dev/null
docker run -d --name e2e-web --network e2e-fw nginx:alpine >/dev/null
sleep 3

reach() {
  for net in bridge e2e-fw; do
    reply="$(docker run --rm --network "$net" alpine:3.24 wget -qO- -T 15 http://host.docker.internal:18081/marker || true)"
    [ "$reply" = from-the-host ] && ok "$1: host.docker.internal on $net" || { diagnose; fail "$1: host.docker.internal on $net: '$reply'"; }
    docker run --rm --network "$net" alpine:3.24 wget -qO /dev/null -T 15 http://e2e-web.dockyard.test/ && ok "$1: a domain on $net" || { diagnose; fail "$1: a domain on $net"; }
  done
}
diagnose() {
  sudo iptables -S INPUT | head -12 || true
  if systemctl is-active --quiet firewalld; then sudo firewall-cmd --get-active-zones || true; fi
}

echo "== ufw, turned on after the agent started"
sudo ufw default deny incoming >/dev/null
sudo ufw --force enable >/dev/null
reach "ufw"
echo "== ufw, already on when the engine starts"
sudo systemctl reset-failed docker.service docker.socket || true
sudo systemctl restart docker.service
docker start e2e-web >/dev/null
sleep 3
reach "ufw before the engine"
if sudo iptables -S INPUT | grep -E 'ACCEPT' | grep -E 'dports .*(2378|2380|2381)' | grep -vE -- '-i (docker\+|br-\+)' >/dev/null; then fail "a rule opens these ports beyond the container interfaces"; else ok "the rules only cover container interfaces"; fi
sudo ufw --force disable >/dev/null

echo "== firewalld, turned on after the agent started"
sudo apt-get install -y -qq firewalld >/dev/null
sudo systemctl start firewalld
sudo firewall-cmd --state
reach "firewalld"
echo "== firewalld, already on when the engine starts"
sudo systemctl reset-failed docker.service docker.socket || true
sudo systemctl restart docker.service
docker start e2e-web >/dev/null
sleep 3
reach "firewalld before the engine"
sudo systemctl disable --now firewalld >/dev/null 2>&1

kill "$service"; rm -rf "$d"
sudo systemctl reset-failed docker.service docker.socket || true
sudo systemctl restart docker.service
docker rm -f e2e-web >/dev/null
docker network rm e2e-fw >/dev/null
"$WIZARD" --silent --uninstall
echo "All good."
