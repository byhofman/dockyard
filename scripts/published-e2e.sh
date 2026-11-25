#!/bin/sh
# The one-liners install the published release, so they are checked with that release's end-to-end tests; the scripts
# themselves still come from the commit under test.
set -eu
tag="$(curl -fsSLI -o /dev/null -w '%{url_effective}' https://github.com/byhofman/dockyard/releases/latest)"
tag="$(printf '%s' "${tag#*/releases/tag/}" | sed 's#%2[Ff]#/#g')"
git checkout "$tag" -- tests/e2e
echo "tests/e2e from $tag"