#!/bin/sh
# release-component.sh <component> <version> <notes file> <dir>: publishes one component's release from a build of
# everything (a v<version> tag): its tag <component>/<version> at this commit, only its own assets from <dir>, the
# checks, and its entry in components.json. A release that is already published is left as it is, so a rerun carries on
# where the last one stopped. Needs gh, jq and GH_TOKEN.
set -eu
component="$1"; version="$2"; notes="$3"; dir="$4"
here="$(cd "$(dirname "$0")" && pwd)"
tag="$component/$version"
repo="${GITHUB_REPOSITORY:-byhofman/dockyard}"

if [ "$(gh release view "$tag" --repo "$repo" --json isDraft -q .isDraft 2>/dev/null)" = false ]; then
  echo "$tag is already released"
  exit 0
fi

own="$(mktemp -d)"
trap 'rm -rf "$own"' EXIT
for pattern in $(sh "$here/check-release-assets.sh" --list "$component" "$version"); do
  [ "$pattern" = SHA256SUMS ] && continue
  for file in "$dir"/$pattern; do
    [ -f "$file" ] && cp "$file" "$own/"
  done
done
(cd "$own" && sha256sum -- * > SHA256SUMS)

gh release view "$tag" --repo "$repo" >/dev/null 2>&1 || \
  gh release create "$tag" --repo "$repo" --draft --target "${GITHUB_SHA:?}" --title "$component $version" --notes-file "$notes"
gh release upload "$tag" --repo "$repo" --clobber "$own"/*
sh "$here/check-release-assets.sh" "$component" "$version" "$(gh release view "$tag" --repo "$repo" --json assets -q '.assets[].name')"
if [ "$component" = toolbox ]; then latest=--latest; else latest=--latest=false; fi
gh release edit "$tag" --repo "$repo" --draft=false "$latest"
sh "$here/update-index.sh" "$component" "$version"