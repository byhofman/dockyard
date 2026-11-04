#!/bin/sh
# Dockyard installer for Linux, from the latest GitHub release:
#   curl -fsSL https://raw.githubusercontent.com/byhofman/dockyard/main/install.sh | sh
# Options (after "sh -s --"): --engine-only  (tray and engine without the dashboard)   --no-setup  (packages only)
# Running it again updates Dockyard.
set -eu
repo="${DOCKYARD_REPO:-byhofman/dockyard}"
releases="${DOCKYARD_RELEASES_URL:-https://github.com/$repo/releases}"
engine_only=0; setup=1
for arg in "$@"; do
  case "$arg" in
    --engine-only) engine_only=1 ;;
    --no-setup) setup=0 ;;
  esac
done

sudo=""; [ "$(id -u)" -eq 0 ] || sudo="sudo"
[ "$(uname -m)" = x86_64 ] || { echo "Dockyard for Linux is built for x86_64" >&2; exit 1; }

if [ -z "${DOCKYARD_VERSION:-}" ]; then
  tag="$(curl -fsSL "https://api.github.com/repos/$repo/releases/latest" | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -1)"
  [ -n "$tag" ] || { echo "No Dockyard release found" >&2; exit 1; }
  version="${tag#v}"
else
  version="$DOCKYARD_VERSION"; tag="v$version"
fi

if command -v apt-get >/dev/null 2>&1; then
  files="dockyard_${version}_amd64.deb"; [ $engine_only -eq 1 ] || files="$files dockyard-dashboard_${version}_amd64.deb"
elif command -v dnf >/dev/null 2>&1; then
  files="dockyard-${version}-1.x86_64.rpm"; [ $engine_only -eq 1 ] || files="$files dockyard-dashboard-${version}-1.x86_64.rpm"
else
  echo "No apt or dnf found. Download Dockyard-$version-linux-x64.tar.gz from $releases instead." >&2
  exit 1
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
echo "Downloading Dockyard $version"
curl -fsSL -o "$tmp/SHA256SUMS" "$releases/download/$tag/SHA256SUMS"
for f in $files; do
  curl -fsSL -o "$tmp/$f" "$releases/download/$tag/$f"
  (cd "$tmp" && grep " $f\$" SHA256SUMS | sha256sum -c - >/dev/null) || { echo "Checksum mismatch for $f" >&2; exit 1; }
done

paths=""; for f in $files; do paths="$paths $tmp/$f"; done
chmod a+r "$tmp" $paths
if command -v apt-get >/dev/null 2>&1; then
  # shellcheck disable=SC2086
  $sudo env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq $paths
else
  # shellcheck disable=SC2086
  $sudo dnf -y -q install $paths
fi

if [ $setup -eq 1 ] && [ "$(id -u)" -ne 0 ]; then
  dockyard --install
elif [ $setup -eq 1 ]; then
  echo "Installed. Run 'dockyard --install' as your own user (not root) to finish the setup."
fi
