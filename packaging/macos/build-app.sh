#!/bin/sh
# build-app.sh <version> <dist> <out>: Dockyard.app from the published files in <dist> (dockyard-tray and the Avalonia
# and Skia dylibs, dockyard-dashboard, dockyard-cli, dockyard-host, vm/), ad-hoc signed (no certificate needed), as
# Dockyard-<version>-macos.zip, -macos-engine-only.zip and Dockyard-<version>-macos.dmg. They are assets of the CLI's
# release: dockyard setup puts the bundle in place and links /usr/local/bin/dockyard to its dockyard-cli.
set -eu
version="$1"; dist="$2"; out="$3"
here="$(cd "$(dirname "$0")" && pwd)"; root="$here/../.."
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

bundle() {
  app="$1/Dockyard.app"; with_dashboard="$2"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  sed "s/VERSION/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"
  # Inside the bundle the tray and the dashboard keep the app's name. The tray links the dylibs too, so every edition has
  # them. The CLI stays dockyard-cli: the default file system cannot tell dockyard from Dockyard.
  cp "$dist/dockyard-tray" "$app/Contents/MacOS/Dockyard"
  cp "$dist/dockyard-host" "$dist/dockyard-cli" "$dist"/*.dylib "$app/Contents/MacOS/"
  if [ "$with_dashboard" = 1 ]; then
    cp "$dist/dockyard-dashboard" "$app/Contents/MacOS/Dockyard Dashboard"
  fi
  cp -R "$dist/vm" "$app/Contents/Resources/vm"
  cp "$work/Dockyard.icns" "$app/Contents/Resources/Dockyard.icns"

  for f in "$app"/Contents/MacOS/*; do
    [ "$(basename "$f")" = dockyard-host ] && continue
    codesign --force --sign - "$f"
  done
  codesign --force --sign - --entitlements "$root/macos/DockyardVM/dockyard-host.entitlements" "$app/Contents/MacOS/dockyard-host"
  codesign --force --sign - "$app"
  codesign --verify --strict "$app"
}

iconset="$work/Dockyard.iconset"; mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  cp "$root/branding/app-icon/png/icon-$size.png" "$iconset/icon_${size}x${size}.png"
  cp "$root/branding/app-icon/png/icon-$((size * 2)).png" "$iconset/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$iconset" -o "$work/Dockyard.icns"

mkdir -p "$out" "$work/full" "$work/engine"
bundle "$work/full" 1
bundle "$work/engine" 0
ditto -c -k --keepParent "$work/full/Dockyard.app" "$out/Dockyard-$version-macos.zip"
ditto -c -k --keepParent "$work/engine/Dockyard.app" "$out/Dockyard-$version-macos-engine-only.zip"

# Disk image for manual download: Dockyard.app next to an Applications link.
mkdir -p "$work/dmg"
cp -R "$work/full/Dockyard.app" "$work/dmg/"
ln -s /Applications "$work/dmg/Applications"
# hdiutil fails intermittently on CI runners ("Resource busy"), so it gets a few attempts.
for attempt in 1 2 3 4 5; do
  if hdiutil create -volname Dockyard -srcfolder "$work/dmg" -format UDZO -ov "$out/Dockyard-$version-macos.dmg"; then break; fi
  [ "$attempt" -eq 5 ] && exit 1
  sleep 5
done
codesign -d --entitlements - "$work/full/Dockyard.app/Contents/MacOS/dockyard-host" | grep -q com.apple.security.virtualization
ls -la "$out"
