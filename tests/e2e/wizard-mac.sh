#!/bin/sh
# End to end on a Mac, in a throwaway home that is deleted afterwards: nothing outside it changes.
#   WIZARD_ZIP=<Dockyard-*-macos.zip> sh wizard-mac.sh     the wizard from a build
#   ONELINER=<url of install-mac.sh> sh wizard-mac.sh      the published one-liner
# A sudo that refuses keeps the silent setup from changing /etc or installing Rosetta, whatever this user may do.
# ADMIN=1 leaves sudo alone and checks what needs an administrator: only for a throwaway machine such as a CI runner.
# Add BOOT=1 to also boot the VM and check that a volume survives (1 GiB of memory, about 30 s).
set -eu
ok() { echo "  ok   $1"; }
fail() { echo "  FAIL $1" >&2; exit 1; }
check() { desc="$1"; shift; if "$@"; then ok "$desc"; else fail "$desc"; fi; }
missing() { [ ! -e "$1" ]; }

real_home="$HOME"
# Short: the docker socket lives under it, and unix socket paths have a limit of 104 characters.
home="$(mktemp -d /tmp/dy.XXXXXX)"
export HOME="$home"
unset DOCKER_CONFIG DOCKER_HOST DOCKER_CONTEXT
mkdir -p "$home/shim"
[ -n "${ADMIN:-}" ] || { printf '#!/bin/sh\nexit 1\n' > "$home/shim/sudo"; chmod +x "$home/shim/sudo"; }
export PATH="$home/shim:$PATH"
app="$home/Applications/Dockyard.app"
macos="$app/Contents/MacOS"
docker="$home/.dockyard/bin/docker"

stop_engine() {
  [ -S "$home/.dockyard/run/vm.sock" ] || return 0
  printf 'stop\n' | nc -U "$home/.dockyard/run/vm.sock" >/dev/null 2>&1 || true
  i=0
  while [ -S "$home/.dockyard/run/vm.sock" ] && [ $i -lt 60 ]; do i=$((i+1)); sleep 1; done
}
start_engine() {
  "$macos/dockyard-vm" run --config "$home/.dockyard/vm.json" >/dev/null 2>&1 &
  i=0
  until "$docker" version >/dev/null 2>&1; do
    i=$((i+1)); [ $i -lt 120 ] || fail "the engine did not start"; sleep 1
  done
}
cleanup() {
  stop_engine
  pkill -f "$home/" 2>/dev/null || true
  sleep 1
  rm -rf "$home"
}
trap cleanup EXIT

wizard_dir=""
if [ -n "${WIZARD_ZIP:-}" ]; then
  wizard_dir="$home/wizard"; mkdir -p "$wizard_dir"
  ditto -x -k "$WIZARD_ZIP" "$wizard_dir"
fi
run() {
  if [ -n "${ONELINER:-}" ]; then curl -fsSL "$ONELINER" | sh -s -- "$@"; else "$wizard_dir/Dockyard.app/Contents/MacOS/Dockyard" "$@"; fi
}

fingerprint() {
  cksum "$real_home/.docker/config.json" 2>/dev/null || true
  ls -la /var/run/docker.sock 2>/dev/null || true
  ls -la "$real_home/.dockyard" 2>/dev/null || true
  ls -la "$real_home/Applications" 2>/dev/null | grep -i dockyard || true
  ls /etc/paths.d/
  ls /Library/LaunchDaemons | grep -i dockyard || true
}
before="$(fingerprint)"

echo "== wrong arguments are refused"
rc=0; run --silent --typo >/dev/null 2>&1 || rc=$?
if [ "$rc" -eq 2 ]; then ok "exit 2"; else fail "--typo exits $rc"; fi
check "and nothing was installed" missing "$app"

echo "== first install, no dashboard"
run --silent --no-dashboard --no-start
check "app in place" test -x "$macos/Dockyard"
check "the VM helper too" test -x "$macos/dockyard-vm"
check "no dashboard" missing "$macos/Dockyard Dashboard"
check "no dashboard libraries" sh -c "! ls '$macos'/*.dylib >/dev/null 2>&1"
check "docker CLI" test -x "$docker"
check "VM prepared" test -f "$home/.dockyard/vm.json"
if sed 's#\\/#/#g' "$home/.dockyard/vm.json" | grep -q "$app/Contents/Resources/vm/kernel"; then ok "vm.json points at the installed app, not at the download"; else fail "vm.json: $(cat "$home/.dockyard/vm.json")"; fi
if grep -q "$macos/Dockyard" "$home/Library/LaunchAgents/com.byhofman.dockyard.plist"; then ok "login item points at the installed app"; else fail "login item"; fi
if [ "$("$docker" context show)" = dockyard ]; then ok "docker context selected"; else fail "docker context"; fi
if codesign -d --entitlements - "$macos/dockyard-vm" 2>/dev/null | grep -q com.apple.security.virtualization; then ok "the helper kept its entitlement"; else fail "entitlement"; fi
if "$macos/Dockyard" --version >/dev/null; then ok "the installed app still starts after re-signing"; else fail "--version"; fi

if [ -n "${BOOT:-}" ]; then
  echo "== the engine boots and holds data"
  sed -i '' -E 's/("memoryMiB" *: *)[0-9]+/\11024/' "$home/.dockyard/vm.json"
  start_engine
  "$docker" volume create dockyard-e2e >/dev/null
  ok "marker volume created"
  stop_engine
fi

if [ -n "${ADMIN:-}" ]; then
  echo "== what needs an administrator"
  check "the PATH entry" test -f /etc/paths.d/dockyard
  if [ "$(readlink /var/run/docker.sock)" = "$home/.dockyard/docker.sock" ]; then ok "/var/run/docker.sock points at Dockyard"; else fail "/var/run/docker.sock: $(ls -la /var/run/docker.sock 2>&1)"; fi
  check "and comes back after a restart" test -f /Library/LaunchDaemons/com.byhofman.dockyard.socket.plist
  sudo rm -f /var/run/docker.sock
  sudo launchctl kickstart -k system/com.byhofman.dockyard.socket
  sleep 1
  if [ "$(readlink /var/run/docker.sock)" = "$home/.dockyard/docker.sock" ]; then ok "the daemon recreates the link"; else fail "the daemon did not recreate the link"; fi
  if grep -q '"credsStore": "osxkeychain"' "$home/.docker/config.json"; then ok "docker login uses the Keychain"; else fail "credsStore: $(cat "$home/.docker/config.json")"; fi
  helper="$home/.dockyard/bin/docker-credential-osxkeychain"
  check "the helper is there" test -x "$helper"
  # A keychain made for this and unlocked, or macOS waits forever for someone to type a password. In the runner's real
  # home, since the keychain search list lives there; perl's alarm is because a hang here should fail, not stall the run.
  limit() { perl -e 'alarm 30; exec @ARGV' "$@"; }
  as_runner() { HOME="$real_home" limit "$@"; }
  as_runner security create-keychain -p e2e "$real_home/e2e.keychain"
  as_runner security list-keychains -d user -s "$real_home/e2e.keychain"
  as_runner security default-keychain -d user -s "$real_home/e2e.keychain"
  as_runner security unlock-keychain -p e2e "$real_home/e2e.keychain"
  as_runner security set-keychain-settings "$real_home/e2e.keychain"
  printf '{"ServerURL":"e2e.dockyard.test","Username":"me","Secret":"s3cret"}' | as_runner "$helper" store
  if printf 'e2e.dockyard.test' | as_runner "$helper" get | grep -q s3cret; then ok "a login round-trips through the Keychain"; else fail "keychain round trip"; fi
  if grep -q s3cret "$home/.docker/config.json"; then fail "the secret reached config.json"; else ok "the secret is not in config.json"; fi
  printf 'e2e.dockyard.test' | as_runner "$helper" erase
fi
check "tools that look in ~/.docker/run find it" test -L "$home/.docker/run/docker.sock"

if [ -n "${ADMIN:-}" ]; then
  echo "== container domains and trusted HTTPS"
  if grep -q 'port 15353' /etc/resolver/dockyard.test; then ok "this Mac asks Dockyard about the name space"; else fail "resolver: $(cat /etc/resolver/dockyard.test 2>&1)"; fi
  # Dockyard uses the login keychain by its path, so this home needs one; made here and unlocked, as a login does.
  mkdir -p "$home/Library/Keychains"
  security create-keychain -p e2e "$home/Library/Keychains/login.keychain-db"
  security unlock-keychain -p e2e "$home/Library/Keychains/login.keychain-db"
  security set-keychain-settings "$home/Library/Keychains/login.keychain-db"
  run --silent --no-start --no-dashboard --trust-https
  ca="$home/.dockyard/ca/ca.pem"
  check "the CA was made" test -f "$ca"
  check "no file holds the key, /Users being shared with the engine" missing "$home/.dockyard/ca/ca.key"
  if security find-generic-password -s "Dockyard Local CA key" "$home/Library/Keychains/login.keychain-db" >/dev/null; then ok "the key is in the keychain"; else fail "the key is not in the keychain"; fi
  if security find-certificate -c "Dockyard Local CA" /Library/Keychains/System.keychain >/dev/null; then ok "the CA is in the trust store"; else fail "not in the trust store"; fi
  if openssl x509 -in "$ca" -noout -text | grep -A2 'Name Constraints: critical' | grep -q 'DNS:dockyard.test'; then ok "the CA is name constrained"; else fail "name constraint"; fi
fi

date > "$home/.dockyard/marker"
data_before="$(ls -i "$home/.dockyard/data.img")"

echo "== rerun keeps the choice and the data"
run --silent --no-start
check "still no dashboard" missing "$macos/Dockyard Dashboard"
check "marker kept" test -f "$home/.dockyard/marker"
if [ "$(ls -i "$home/.dockyard/data.img")" = "$data_before" ]; then ok "the data disk is the same file"; else fail "the data disk was replaced"; fi

echo "== add the dashboard"
run --silent --dashboard --no-start
check "dashboard added" test -x "$macos/Dockyard Dashboard"
check "with its libraries" sh -c "ls '$macos'/*.dylib >/dev/null 2>&1"
check "signature of the dashboard is valid" codesign --verify "$macos/Dockyard Dashboard"

echo "== any component can go and come back, and required ones refuse"
run --silent --dashboard --no-start --without compose
check "compose is gone" missing "$home/.docker/cli-plugins/docker-compose"
run --silent --dashboard --no-start
check "it stays gone on a rerun" missing "$home/.docker/cli-plugins/docker-compose"
run --silent --dashboard --no-start --with compose
check "it is back" test -x "$home/.docker/cli-plugins/docker-compose"
if [ -z "${ONELINER:-}" ]; then
  kind="$home/.dockyard/bin/kind"
  check "kind is next to docker" test -x "$kind"
  "$kind" version | grep -q '^kind v' && ok "it runs: $("$kind" version)" || fail "kind version"
  run --silent --dashboard --no-start --without kind
  check "kind is gone" missing "$kind"
  run --silent --dashboard --no-start
  check "it stays gone on a rerun" missing "$kind"
  run --silent --dashboard --no-start --with kind
  check "it is back" test -x "$kind"
fi
rc=0; run --silent --no-start --without linux-engine >/dev/null 2>&1 || rc=$?
if [ "$rc" -eq 2 ]; then ok "the engine is required here"; else fail "--without linux-engine exits $rc"; fi
if [ "$("$macos/Dockyard Dashboard" --version)" = 0.0.0 ] || "$macos/Dockyard Dashboard" --version >/dev/null; then ok "the dashboard says its version without opening a window"; else fail "dashboard --version"; fi

echo "== remove the dashboard"
run --silent --no-dashboard --no-start
check "dashboard removed" missing "$macos/Dockyard Dashboard"
check "app stays" test -x "$macos/Dockyard"

if [ -n "${BOOT:-}" ]; then
  echo "== the engine still has the data"
  start_engine
  if "$docker" volume inspect dockyard-e2e >/dev/null; then ok "marker volume kept"; else fail "marker volume gone"; fi
  stop_engine
fi

echo "== uninstall keeps the data"
run --silent --uninstall
check "app removed" missing "$app"
check "login item removed" missing "$home/Library/LaunchAgents/com.byhofman.dockyard.plist"
check "data kept" test -f "$home/.dockyard/data.img"
check "the user socket link is gone" missing "$home/.docker/run/docker.sock"
if [ -n "${ADMIN:-}" ]; then
  if security find-generic-password -s "Dockyard Local CA key" "$home/Library/Keychains/login.keychain-db" >/dev/null 2>&1; then fail "the key stayed in the keychain"; else ok "the CA's key is gone, also when the data stays"; fi
fi
if [ -f "$home/.docker/config.json" ] && grep -q credsStore "$home/.docker/config.json"; then fail "credsStore left behind"; else ok "no credential store left in the config"; fi
if [ -n "${ADMIN:-}" ]; then
  check "/var/run/docker.sock is gone" missing /var/run/docker.sock
  check "the daemon is gone" missing /Library/LaunchDaemons/com.byhofman.dockyard.socket.plist
  check "the resolver entry is gone" missing /etc/resolver/dockyard.test
  if security find-certificate -c "Dockyard Local CA" /Library/Keychains/System.keychain >/dev/null 2>&1; then fail "the CA stayed in the trust store"; else ok "the CA is out of the trust store"; fi
  if security dump-trust-settings -d 2>/dev/null | grep -q ": Dockyard Local CA$"; then fail "its trust setting stayed"; else ok "its trust setting is gone too (DOK-20)"; fi
fi

echo "== and --remove-data takes it"
run --silent --no-start
check "a fresh install gets the dashboard" test -x "$macos/Dockyard Dashboard"
run --silent --uninstall --remove-data
check "data removed" missing "$home/.dockyard"

if [ -n "${ADMIN:-}" ]; then echo "All good."; exit 0; fi
if [ "$(fingerprint)" = "$before" ]; then ok "nothing outside the throwaway home changed"; else fail "something outside the throwaway home changed"; fi
echo "All good."
