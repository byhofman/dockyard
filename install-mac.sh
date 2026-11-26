#!/bin/sh
# Dockyard for macOS (Apple silicon, macOS 14+). This only fetches the dockyard command, checks it and runs dockyard setup:
#   curl -fsSL https://raw.githubusercontent.com/byhofman/dockyard/main/install-mac.sh | sh
# Arguments go to setup: ... | sh -s -- --silent --without dashboard
# curl sets no quarantine attribute, so Gatekeeper does not block the ad-hoc signed command.
set -eu
repo=byhofman/dockyard
asset=dockyard-macos-arm64

[ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = arm64 ] || { echo "Dockyard for macOS needs an Apple-silicon Mac" >&2; exit 1; }

if [ -n "${DOCKYARD_VERSION:-}" ]; then
  tag="cli/$DOCKYARD_VERSION"
else
  # components.json names each component's latest release, from github.com, which has no API rate limit.
  tag="$(curl -fsSL "https://github.com/$repo/releases/download/index/components.json" | grep -o '"cli/[0-9][^"]*"' | head -n 1 | tr -d '"')" || tag=""
fi
case "$tag" in
  cli/[0-9]*) ;;
  *) echo "No Dockyard release found" >&2; exit 1 ;;
esac

dir="$(mktemp -d)"
trap 'rm -rf "$dir"' EXIT
echo "Downloading the dockyard command ($tag)"
curl -fsSL -o "$dir/$asset" "https://github.com/$repo/releases/download/$tag/$asset"
curl -fsSL -o "$dir/SHA256SUMS" "https://github.com/$repo/releases/download/$tag/SHA256SUMS"
(cd "$dir" && grep " $asset\$" SHA256SUMS | shasum -a 256 -c - >/dev/null) || { echo "Checksum mismatch for $asset, not running it" >&2; exit 1; }

chmod +x "$dir/$asset"
# Piped into sh, stdin is this script: setup asks on the terminal instead, when there is one.
if [ -t 1 ] && (exec </dev/tty) 2>/dev/null; then
  "$dir/$asset" setup "$@" </dev/tty
else
  "$dir/$asset" setup "$@"
fi
