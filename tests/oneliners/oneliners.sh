#!/bin/sh
# The one-liners only fetch the dockyard command, check it and run `dockyard setup`. Fake curl, a fake dockyard, and a
# PATH with a trap for everything an install would run, so a script that does more than its job fails here.
set -eu
root="$(cd "$(dirname "$0")/../.." && pwd)"
pass() { echo "  ok   $1"; }
fail() { echo "  FAIL $1" >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
bin="$work/bin"; assets="$work/assets"; mkdir -p "$bin" "$assets"

for tool in apt-get apt dpkg dnf rpm yum zypper pacman sudo pkexec dockerd osascript open launchctl systemctl usermod wsl ditto unzip tar; do
  printf '#!/bin/sh\necho "%s $*" >> "%s/forbidden"\nexit 0\n' "$tool" "$work" > "$bin/$tool"
  chmod +x "$bin/$tool"
done

# Serves components.json from the index release and the other files from the release the URL names, here cli/9.9.9 or
# cli/1.2.3 (DOCKYARD_VERSION); any other release is a 404.
cat > "$bin/curl" <<CURL
#!/bin/sh
echo "curl \$*" >> "$work/curl.log"
output=""; url=""
while [ \$# -gt 0 ]; do
  case "\$1" in
    -o) output="\$2"; shift ;;
    -fsSL) ;;
    *) url="\$1" ;;
  esac
  shift
done
case "\$url" in
  */releases/download/index/components.json) file="$assets/components.json" ;;
  */releases/download/cli/9.9.9/*|*/releases/download/cli/1.2.3/*) file="$assets/\${url##*/}" ;;
  *) file="" ;;
esac
[ -n "\$file" ] && [ -f "\$file" ] || { echo "curl: 404 \$url" >&2; exit 22; }
if [ -n "\$output" ]; then cp "\$file" "\$output"; else cat "\$file"; fi
CURL
chmod +x "$bin/curl"

# Stands in for uname so that both scripts run on any machine.
cat > "$bin/uname" <<UNAME
#!/bin/sh
case "\$1" in
  -s) echo "\$FAKE_OS" ;;
  -m) echo "\$FAKE_ARCH" ;;
esac
UNAME
chmod +x "$bin/uname"

command -v sha256sum >/dev/null 2>&1 || {
  printf '#!/bin/sh\nexec shasum -a 256 "$@"\n' > "$bin/sha256sum"; chmod +x "$bin/sha256sum"; }
command -v shasum >/dev/null 2>&1 || {
  printf '#!/bin/sh\nshift 2\nexec sha256sum "$@"\n' > "$bin/shasum"; chmod +x "$bin/shasum"; }

fake_dockyard() {
  cat > "$1" <<DOCKYARD
#!/bin/sh
printf '%s\n' "\$@" > "$work/dockyard-args"
echo ran >> "$work/dockyard-runs"
exit \${FAKE_DOCKYARD_EXIT:-0}
DOCKYARD
  chmod +x "$1"
}

sum() { (cd "$assets" && sha256sum "$1" 2>/dev/null || shasum -a 256 "$1"); }

fake_dockyard "$assets/dockyard-linux-x64"
fake_dockyard "$assets/dockyard-macos-arm64"
# Every other asset of a release, so that a script that takes more than the command is caught.
for other in dockyard-tray-linux-x64 Dockyard-9.9.9-macos.zip "dockyard_9.9.9_amd64.deb"; do echo other > "$assets/$other"; done
cat > "$assets/components.json" <<'JSON'
{
  "engine": { "version": "9.9.0", "tag": "engine/9.9.0" },
  "tray": { "version": "9.8.0", "tag": "tray/9.8.0" },
  "dashboard": { "version": "9.9.1", "tag": "dashboard/9.9.1", "requires": { "cli": "9.9.9" } },
  "cli": { "version": "9.9.9", "tag": "cli/9.9.9" }
}
JSON

reset() { rm -f "$work/forbidden" "$work/dockyard-args" "$work/dockyard-runs" "$work/curl.log"; }

good_sums() {
  { sum dockyard-linux-x64; sum dockyard-macos-arm64; sum dockyard-tray-linux-x64; sum Dockyard-9.9.9-macos.zip; } > "$assets/SHA256SUMS"
}

scenario() {
  name="$1"; script="$root/$2"; asset="$3"; export FAKE_OS="$4" FAKE_ARCH="$5"
  echo "== $name"

  good_sums; reset
  env PATH="$bin:$PATH" sh "$script" setup --silent --without tray --without dashboard >/dev/null || fail "$name: exit $?"
  [ "$(cat "$work/dockyard-runs")" = ran ] && pass "runs dockyard once" || fail "$name: dockyard did not run"
  [ "$(tr '\n' ' ' < "$work/dockyard-args")" = "setup --silent --without tray --without dashboard " ] && pass "passes setup and its arguments through" || fail "$name: arguments were: $(cat "$work/dockyard-args")"
  [ ! -f "$work/forbidden" ] && pass "installs nothing itself" || fail "$name: ran $(cat "$work/forbidden")"
  downloads="$(grep -o '/releases/download/[^ ]*' "$work/curl.log" | LC_ALL=C sort | tr '\n' ' ')"
  [ "$downloads" = "/releases/download/cli/9.9.9/SHA256SUMS /releases/download/cli/9.9.9/$asset /releases/download/index/components.json " ] \
    && pass "reads components.json and downloads only the command and its checksums" || fail "$name: downloads were $downloads"

  good_sums; reset
  env PATH="$bin:$PATH" sh "$script" >/dev/null || fail "$name: exit $?"
  [ "$(tr '\n' ' ' < "$work/dockyard-args")" = "setup " ] && pass "without arguments it runs dockyard setup" || fail "$name: arguments were: $(cat "$work/dockyard-args")"

  good_sums; reset
  env PATH="$bin:$PATH" DOCKYARD_VERSION=1.2.3 sh "$script" setup >/dev/null 2>&1 || true
  grep -q '/releases/download/cli/1.2.3/' "$work/curl.log" && ! grep -q 'components.json' "$work/curl.log" \
    && pass "DOCKYARD_VERSION names the command's release" || fail "$name: $(cat "$work/curl.log")"

  good_sums; reset
  rc=0; env PATH="$bin:$PATH" FAKE_DOCKYARD_EXIT=7 sh "$script" >/dev/null 2>&1 || rc=$?
  [ "$rc" -eq 7 ] && pass "passes dockyard's exit code on" || fail "$name: exit code $rc"

  good_sums; reset
  sed -i.bak "s/^[0-9a-f]\{8\}\(.* $asset\)\$/00000000\1/" "$assets/SHA256SUMS"
  rc=0; env PATH="$bin:$PATH" sh "$script" setup --silent >/dev/null 2>&1 || rc=$?
  [ "$rc" -ne 0 ] && pass "fails on a bad checksum" || fail "$name: exit 0 on a bad checksum"
  [ ! -f "$work/dockyard-runs" ] && pass "and does not run dockyard" || fail "$name: ran a file with a bad checksum"

  reset; rm -f "$assets/SHA256SUMS"
  rc=0; env PATH="$bin:$PATH" sh "$script" >/dev/null 2>&1 || rc=$?
  [ "$rc" -ne 0 ] && [ ! -f "$work/dockyard-runs" ] && pass "fails without checksums, dockyard not run" || fail "$name: missing SHA256SUMS"

  good_sums; reset; mv "$assets/components.json" "$assets/components.json.kept"
  rc=0; env PATH="$bin:$PATH" sh "$script" >/dev/null 2>&1 || rc=$?
  mv "$assets/components.json.kept" "$assets/components.json"
  [ "$rc" -ne 0 ] && [ ! -f "$work/dockyard-runs" ] && pass "fails without components.json" || fail "$name: ran without an index"
}

scenario "install.sh" install.sh dockyard-linux-x64 Linux x86_64
scenario "install-mac.sh" install-mac.sh dockyard-macos-arm64 Darwin arm64

echo "== asset names match what the CLI's release must have"
for script_asset in dockyard-linux-x64 dockyard-macos-arm64 dockyard-windows-x64.exe; do
  grep -q "$script_asset" "$root/scripts/check-release-assets.sh" && pass "$script_asset is checked" || fail "$script_asset is not in check-release-assets.sh"
  grep -q "$script_asset" "$root/install.sh" "$root/install-mac.sh" "$root/install.ps1" && pass "$script_asset is what a script downloads" || fail "no script downloads $script_asset"
done
echo "All good."
