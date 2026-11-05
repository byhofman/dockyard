#!/bin/sh
# check-release-assets.sh <version> <asset names, whitespace separated>: fails when something the one-liners,
# the wizard or a tray that updates itself downloads is missing from a release.
set -eu
version="$1"; assets="$2"
missing=0
for name in \
  SHA256SUMS \
  Dockyard.exe \
  Dockyard.Dashboard.zip \
  "dockyard-linux-$version-x86_64.tar.gz" \
  Dockyard-linux-x64 \
  "Dockyard-$version-linux-x64.tar.gz" \
  "dockyard_${version}_amd64.deb" \
  "dockyard-dashboard_${version}_amd64.deb" \
  "dockyard-$version-1.x86_64.rpm" \
  "dockyard-dashboard-$version-1.x86_64.rpm" \
  "Dockyard-$version-macos.zip" \
  "Dockyard-$version-macos-engine-only.zip" \
  "Dockyard-$version-macos.dmg"; do
  case " $(echo "$assets" | tr '\n' ' ') " in
    *" $name "*) ;;
    *) echo "Missing release asset: $name" >&2; missing=1 ;;
  esac
done
[ "$missing" -eq 0 ] && echo "All release assets are there for $version"
exit "$missing"
