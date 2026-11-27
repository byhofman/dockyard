#!/bin/sh
# release-component.sh <component> <version> <notes file> <dir>: publishes one component's release from a build of
# everything (a v<version> tag): its tag <component>/v<version> at this commit, only its own assets from <dir>, the
# checks, and its entry in components.json. A release that is already published keeps its notes and flags and only gets
# its assets replaced, so a rerun carries on where the last one stopped. Needs gh, jq and GH_TOKEN.
set -eu
component="$1"; version="$2"; notes="$3"; dir="$4"
here="$(cd "$(dirname "$0")" && pwd)"
tag="$component/v$version"
repo="${GITHUB_REPOSITORY:-byhofman/dockyard}"

own="$(mktemp -d)"
trap 'rm -rf "$own"' EXIT
for pattern in $(sh "$here/check-release-assets.sh" --list "$component" "$version"); do
  [ "$pattern" = SHA256SUMS ] && continue
  for file in "$dir"/$pattern; do
    [ -f "$file" ] && cp "$file" "$own/"
  done
done
(cd "$own" && sha256sum -- * > SHA256SUMS)

draft="$(gh release view "$tag" --repo "$repo" --json isDraft -q .isDraft 2>/dev/null || echo missing)"
[ "$draft" = missing ] && \
  gh release create "$tag" --repo "$repo" --draft --target "${GITHUB_SHA:?}" --title "$component $version" --notes-file "$notes"
gh release upload "$tag" --repo "$repo" --clobber "$own"/*
sh "$here/check-release-assets.sh" "$component" "$version" "$(gh release view "$tag" --repo "$repo" --json assets -q '.assets[].name')"
if [ "$draft" != false ]; then
  # GitHub has one latest release per repository; it stays with the CLI, whose files the downloads point at.
  case "$version" in
    *-*) flags="--prerelease --latest=false" ;;
    *) if [ "$component" = cli ]; then flags="--prerelease=false --latest"; else flags="--prerelease=false --latest=false"; fi ;;
  esac
  # shellcheck disable=SC2086 # two flags in one word on purpose
  gh release edit "$tag" --repo "$repo" --draft=false $flags
fi
sh "$here/update-index.sh" "$component" "$version"