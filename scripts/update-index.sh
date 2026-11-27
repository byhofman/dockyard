#!/bin/sh
# update-index.sh <component> <version> [<requires as JSON, e.g. {"cli":"0.9.0"}>]: records a component's release in
# components.json on the release tagged index, which dockyard and the one-liners read to find each component's latest release.
# Workflows run this under one concurrency group, so two releases never write the file at once. Needs gh and jq.
set -eu
component="$1"; version="$2"; requires="${3:-}"
repo="${GITHUB_REPOSITORY:-byhofman/dockyard}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if ! gh release view index --repo "$repo" >/dev/null 2>&1; then
  gh release create index --repo "$repo" --title "Component index" --latest=false \
    --notes "components.json names each Dockyard component's latest release. See docs/decisions.md, "Components release on their own"."
fi
gh release download index --repo "$repo" --pattern components.json --dir "$work" 2>/dev/null || echo '{}' > "$work/components.json"

jq --arg c "$component" --arg v "$version" --arg t "$component/v$version" --argjson r "${requires:-null}" \
  '.[$c] = ({ version: $v, tag: $t } + (if $r == null then {} else { requires: $r } end))' \
  "$work/components.json" > "$work/next.json"
mv "$work/next.json" "$work/components.json"
gh release upload index "$work/components.json" --repo "$repo" --clobber
cat "$work/components.json"
