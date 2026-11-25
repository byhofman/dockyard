#!/bin/sh
# component-assets.sh <component> <dir> <pattern>...: downloads the assets matching the patterns from the component's latest
# release, as components.json on the release tagged index names it. Needs gh and jq, and GH_TOKEN.
set -eu
component="$1"; dir="$2"; shift 2
repo="${GITHUB_REPOSITORY:-byhofman/dockyard}"
index="$(mktemp)"
gh release download index --repo "$repo" --pattern components.json --output "$index" --clobber
tag="$(jq -r --arg c "$component" '.[$c].tag // empty' "$index")"
rm -f "$index"
[ -n "$tag" ] || { echo "components.json names no $component release; release $component first" >&2; exit 1; }
mkdir -p "$dir"
for pattern in "$@"; do
  gh release download "$tag" --repo "$repo" --pattern "$pattern" --dir "$dir" --clobber
done
echo "$tag"
