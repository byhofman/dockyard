#!/bin/sh
# check-release-assets.sh <component> <version> <asset names, whitespace separated>: fails when something the one-liners
# or dockyard setup and update download is missing from a component's release.
# <component> is cli, tray, dashboard or engine; all checks a build of everything (a run by hand, or a v tag).
# check-release-assets.sh --list <component> <version>: the names and patterns of what that release carries.
set -euf
list=0
if [ "$1" = --list ]; then list=1; shift; fi
component="$1"; version="$2"; assets="${3:-}"

# The CLI's release also carries what holds the command on Linux and macOS: the packages and tarball with the tray, and
# the app bundle with the tray and the dashboard.
cli() {
  echo SHA256SUMS dockyard-windows-x64.exe dockyard-linux-x64 dockyard-macos-arm64 "Dockyard-$1-linux-x64.tar.gz" \
    "dockyard_$1_amd64.deb" "dockyard-$1-1.x86_64.rpm" "Dockyard-$1-macos.zip" "Dockyard-$1-macos-engine-only.zip" "Dockyard-$1-macos.dmg"
}
tray() { echo SHA256SUMS Dockyard.Tray.zip; }
dashboard() { echo SHA256SUMS Dockyard.Dashboard.zip "dockyard-dashboard_$1_amd64.deb" "dockyard-dashboard-$1-1.x86_64.rpm"; }
engine() { echo SHA256SUMS "dockyard-linux-$1-x86_64.tar.gz" "dockyard-vm-$1-aarch64.tar.gz"; }

case "$component" in
  cli) wanted="$(cli "$version")" ;;
  tray) wanted="$(tray "$version")" ;;
  dashboard) wanted="$(dashboard "$version")" ;;
  engine) wanted="$(engine "$version")" ;;
  all) wanted="$(cli "$version") $(tray "$version") $(dashboard "$version") $(engine "$version")" ;;
  *) echo "Unknown component: $component" >&2; exit 2 ;;
esac
if [ "$list" -eq 1 ]; then echo "$wanted" | tr ' ' '\n' | sort -u; exit 0; fi

listed=" $(echo "$assets" | tr '\n' ' ') "
missing=0
for name in $(echo "$wanted" | tr ' ' '\n' | sort -u); do
  found=0
  for asset in $listed; do
    # shellcheck disable=SC2254
    case "$asset" in $name) found=1; break ;; esac
  done
  [ "$found" -eq 1 ] || { echo "Missing release asset: $name" >&2; missing=1; }
done
[ "$missing" -eq 0 ] && echo "All $component release assets are there for $version"
exit "$missing"
