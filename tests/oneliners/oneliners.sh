#!/bin/sh
# The one-liners only fetch, check and start the installer. Fake curl, fake installer, and a PATH with a trap for
# everything an install would run, so a script that does more than its job fails here.
set -eu
root="$(cd "$(dirname "$0")/../.." && pwd)"
pass() { echo "  ok   $1"; }
fail() { echo "  FAIL $1" >&2; exit 1; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
bin="$work/bin"; assets="$work/assets"; mkdir -p "$bin" "$assets"

for tool in apt-get apt dpkg dnf rpm yum zypper pacman sudo pkexec dockerd osascript open launchctl systemctl usermod wsl; do
  printf '#!/bin/sh\necho "%s $*" >> "%s/forbidden"\nexit 0\n' "$tool" "$work" > "$bin/$tool"
  chmod +x "$bin/$tool"
done

cat > "$bin/curl" <<CURL
#!/bin/sh
echo "curl \$*" >> "$work/curl.log"
output=""; url=""; effective=0
while [ \$# -gt 0 ]; do
  case "\$1" in
    -o) output="\$2"; shift ;;
    -w) effective=1; shift ;;
    -fsSLI|-fsSL) ;;
    *) url="\$1" ;;
  esac
  shift
done
if [ \$effective -eq 1 ]; then printf '%s' "https://github.com/byhofman/dockyard/releases/tag/v9.9.9"; exit 0; fi
file="$assets/\${url##*/}"
[ -f "\$file" ] || { echo "curl: 404 \$url" >&2; exit 22; }
cp "\$file" "\$output"
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
command -v ditto >/dev/null 2>&1 || cat > "$bin/ditto" <<DITTO
#!/bin/sh
# ditto -x -k <zip> <dir>: the fake zip is a tarball here.
tar -xf "\$3" -C "\$4"
DITTO
[ -f "$bin/ditto" ] && chmod +x "$bin/ditto"

fake_installer() {
  cat > "$1" <<INSTALLER
#!/bin/sh
printf '%s\n' "\$@" > "$work/installer-args"
echo ran >> "$work/installer-runs"
exit \${FAKE_INSTALLER_EXIT:-0}
INSTALLER
  chmod +x "$1"
}

sum() { (cd "$assets" && sha256sum "$1" 2>/dev/null || shasum -a 256 "$1"); }

fake_installer "$assets/Dockyard-linux-x64"
mkdir -p "$work/app/Dockyard.app/Contents/MacOS"
fake_installer "$work/app/Dockyard.app/Contents/MacOS/Dockyard"
if [ "$(/usr/bin/uname -s)" = Darwin ]; then
  /usr/bin/ditto -c -k --keepParent "$work/app/Dockyard.app" "$assets/Dockyard-9.9.9-macos.zip"
else
  tar -cf "$assets/Dockyard-9.9.9-macos.zip" -C "$work/app" Dockyard.app
fi

reset() { rm -f "$work/forbidden" "$work/installer-args" "$work/installer-runs" "$work/curl.log"; }

good_sums() { { sum Dockyard-linux-x64; sum Dockyard-9.9.9-macos.zip; } > "$assets/SHA256SUMS"; }

scenario() {
  name="$1"; script="$root/$2"; export FAKE_OS="$3" FAKE_ARCH="$4"
  echo "== $name"

  good_sums; reset
  env PATH="$bin:$PATH" sh "$script" --silent --no-dashboard --no-start >/dev/null || fail "$name: exit $?"
  [ "$(cat "$work/installer-runs")" = ran ] && pass "starts the installer once" || fail "$name: installer did not run"
  [ "$(tr '\n' ' ' < "$work/installer-args")" = "--silent --no-dashboard --no-start " ] && pass "passes the arguments through" || fail "$name: arguments were: $(cat "$work/installer-args")"
  [ ! -f "$work/forbidden" ] && pass "installs nothing itself" || fail "$name: ran $(cat "$work/forbidden")"
  [ "$(grep -c '/releases/download/' "$work/curl.log")" -eq 2 ] && pass "downloads only the installer and its checksums" || fail "$name: $(cat "$work/curl.log")"

  good_sums; reset
  rc=0; env PATH="$bin:$PATH" FAKE_INSTALLER_EXIT=7 sh "$script" >/dev/null 2>&1 || rc=$?
  [ "$rc" -eq 7 ] && pass "passes the installer's exit code on" || fail "$name: exit code $rc"

  good_sums; reset
  sed -i.bak 's/^[0-9a-f]\{8\}/00000000/' "$assets/SHA256SUMS"
  rc=0; env PATH="$bin:$PATH" sh "$script" --silent >/dev/null 2>&1 || rc=$?
  [ "$rc" -ne 0 ] && pass "fails on a bad checksum" || fail "$name: exit 0 on a bad checksum"
  [ ! -f "$work/installer-runs" ] && pass "and does not start the installer" || fail "$name: started a file with a bad checksum"

  reset; rm -f "$assets/SHA256SUMS"
  rc=0; env PATH="$bin:$PATH" sh "$script" >/dev/null 2>&1 || rc=$?
  [ "$rc" -ne 0 ] && [ ! -f "$work/installer-runs" ] && pass "fails without checksums, installer not started" || fail "$name: missing SHA256SUMS"
}

scenario "install.sh" install.sh Linux x86_64
scenario "install-mac.sh" install-mac.sh Darwin arm64

echo "== asset names match what a release must have"
for script_asset in Dockyard-linux-x64 Dockyard.exe SHA256SUMS; do
  grep -q "$script_asset" "$root/scripts/check-release-assets.sh" && pass "$script_asset is checked" || fail "$script_asset is not in check-release-assets.sh"
done
grep -q 'Dockyard-$version-macos.zip' "$root/scripts/check-release-assets.sh" && pass "the macOS zip is checked" || fail "macOS zip not in check-release-assets.sh"
echo "All good."
