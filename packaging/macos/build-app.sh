#!/bin/sh
# build-app.sh <version> <dist> <out>: Dockyard.app from the published files in <dist> (Dockyard, Dockyard.Dashboard
# and its dylibs, dockyard-vm, vm/), ad-hoc signed, zipped as Dockyard-<version>-macos.zip and -macos-engine-only.zip.
set -eu
version="$1"; dist="$2"; out="$3"
here="$(cd "$(dirname "$0")" && pwd)"; root="$here/../.."
work="$(mktemp -d)"

bundle() {
  app="$1/Dockyard.app"; with_dashboard="$2"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  sed "s/VERSION/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"
  cp "$dist/Dockyard" "$dist/dockyard-vm" "$app/Contents/MacOS/"
  if [ "$with_dashboard" = 1 ]; then
    cp "$dist/Dockyard.Dashboard" "$dist"/*.dylib "$app/Contents/MacOS/"
  fi
  cp -R "$dist/vm" "$app/Contents/Resources/vm"
  cp "$work/Dockyard.icns" "$app/Contents/Resources/Dockyard.icns"

  for f in "$app"/Contents/MacOS/*; do
    [ "$(basename "$f")" = dockyard-vm ] && continue
    codesign --force --sign - "$f"
  done
  codesign --force --sign - --entitlements "$root/macos/DockyardVM/dockyard-vm.entitlements" "$app/Contents/MacOS/dockyard-vm"
  codesign --force --sign - "$app"
  codesign --verify --strict "$app"
}

iconset="$work/Dockyard.iconset"; mkdir -p "$iconset"
for size in 16 32 64 128 256 512; do
  sips -z $size $size "$root/src/Dockyard/Assets/whale-blue.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$work/Dockyard.icns"

mkdir -p "$out" "$work/full" "$work/engine"
bundle "$work/full" 1
bundle "$work/engine" 0
ditto -c -k --keepParent "$work/full/Dockyard.app" "$out/Dockyard-$version-macos.zip"
ditto -c -k --keepParent "$work/engine/Dockyard.app" "$out/Dockyard-$version-macos-engine-only.zip"
codesign -d --entitlements - "$work/full/Dockyard.app/Contents/MacOS/dockyard-vm" | grep -q com.apple.security.virtualization
ls -la "$out"
