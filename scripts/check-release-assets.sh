#!/bin/sh
# check-release-assets.sh <component> <version> <asset names, whitespace separated>: fails when something the one-liners,
# the Toolbox or an installed Toolbox that updates itself downloads is missing from a component's release.
# <component> is toolbox, dashboard, cli or engine; all checks a build of everything (pull requests and main).
set -euf
component="$1"; version="$2"; assets="$3"

toolbox() {
  # The dashboard's assets ride along with the Toolbox's release while 0.8.x Toolboxes, which take the dashboard from
  # the release they update to, are about: any version of them will do.
  echo SHA256SUMS Dockyard.exe Dockyard-linux-x64 "Dockyard-$1-linux-x64.tar.gz" "dockyard_$1_amd64.deb" \
    "dockyard-$1-1.x86_64.rpm" "Dockyard-$1-macos.zip" "Dockyard-$1-macos-engine-only.zip" "Dockyard-$1-macos.dmg" \
    Dockyard.Dashboard.zip 'dockyard-dashboard_*_amd64.deb' 'dockyard-dashboard-*-1.x86_64.rpm'
}
dashboard() { echo SHA256SUMS Dockyard.Dashboard.zip "dockyard-dashboard_$1_amd64.deb" "dockyard-dashboard-$1-1.x86_64.rpm"; }
cli() { echo SHA256SUMS dockyard-windows-x64.exe dockyard-linux-x64 dockyard-macos-arm64; }
engine() { echo SHA256SUMS "dockyard-linux-$1-x86_64.tar.gz" "dockyard-vm-$1-aarch64.tar.gz"; }

case "$component" in
  toolbox) wanted="$(toolbox "$version")" ;;
  dashboard) wanted="$(dashboard "$version")" ;;
  cli) wanted="$(cli "$version")" ;;
  engine) wanted="$(engine "$version")" ;;
  all) wanted="$(toolbox "$version") $(dashboard "$version") $(cli "$version") $(engine "$version")" ;;
  *) echo "Unknown component: $component" >&2; exit 2 ;;
esac

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
