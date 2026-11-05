#!/bin/sh
# Dockyard for Linux. This only fetches the installer, checks it and starts it:
#   curl -fsSL https://raw.githubusercontent.com/byhofman/dockyard/main/install.sh | sh
# Arguments go to the installer: ... | sh -s -- --silent --no-dashboard
set -eu
repo=byhofman/dockyard
asset=Dockyard-linux-x64

[ "$(uname -m)" = x86_64 ] || { echo "Dockyard for Linux is built for x86_64" >&2; exit 1; }

if [ -n "${DOCKYARD_VERSION:-}" ]; then
  tag="v$DOCKYARD_VERSION"
else
  # The redirect names one release, so the file and its checksum come from the same one, and it has no API rate limit.
  tag="$(curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/$repo/releases/latest")"
  tag="${tag##*/}"
fi
case "$tag" in
  v[0-9]*) ;;
  *) echo "No Dockyard release found" >&2; exit 1 ;;
esac

dir="$(mktemp -d)"
trap 'rm -rf "$dir"' EXIT
echo "Downloading the Dockyard installer ($tag)"
curl -fsSL -o "$dir/$asset" "https://github.com/$repo/releases/download/$tag/$asset"
curl -fsSL -o "$dir/SHA256SUMS" "https://github.com/$repo/releases/download/$tag/SHA256SUMS"
(cd "$dir" && grep " $asset\$" SHA256SUMS | sha256sum -c - >/dev/null) || { echo "Checksum mismatch for $asset, not starting it" >&2; exit 1; }

chmod +x "$dir/$asset"
"$dir/$asset" "$@"
