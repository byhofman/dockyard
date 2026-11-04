#!/bin/sh
# Dockyard installer for macOS (Apple silicon, macOS 14+):
#   curl -fsSL https://raw.githubusercontent.com/byhofman/dockyard/main/install-mac.sh | sh
#   ... | sh -s -- --engine-only        without the dashboard
# Downloads with curl (no quarantine attribute, so Gatekeeper does not block the ad-hoc signed app), checks
# SHA256SUMS, installs to ~/Applications/Dockyard.app and runs the Dockyard setup.
set -eu
repo=byhofman/dockyard
asset_suffix="macos"
for arg in "$@"; do [ "$arg" = --engine-only ] && asset_suffix="macos-engine-only"; done

[ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = arm64 ] || { echo "Dockyard for macOS needs an Apple-silicon Mac" >&2; exit 1; }

tag="$(curl -fsSL "https://api.github.com/repos/$repo/releases/latest" | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -1)"
[ -n "$tag" ] || { echo "No Dockyard release found" >&2; exit 1; }
version="${tag#v}"
zip="Dockyard-$version-$asset_suffix.zip"
tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading Dockyard $version"
curl -fsSL -o "$tmp/$zip" "https://github.com/$repo/releases/download/$tag/$zip"
curl -fsSL -o "$tmp/SHA256SUMS" "https://github.com/$repo/releases/download/$tag/SHA256SUMS"
(cd "$tmp" && grep " $zip\$" SHA256SUMS | shasum -a 256 -c -) || { echo "Checksum mismatch for $zip" >&2; exit 1; }

mkdir -p "$HOME/Applications"
pkill -f "Dockyard.app/Contents/MacOS/Dockyard" 2>/dev/null || true
rm -rf "$HOME/Applications/Dockyard.app"
ditto -x -k "$tmp/$zip" "$HOME/Applications"

"$HOME/Applications/Dockyard.app/Contents/MacOS/Dockyard" --install
open "$HOME/Applications/Dockyard.app" --args --tray
echo "Dockyard is installed in ~/Applications and runs in the menu bar."
