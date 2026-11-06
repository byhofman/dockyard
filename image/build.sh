#!/bin/sh
# image/build.sh <x86_64|aarch64> <version> [vm|guest]  (same output as build.ps1, for Linux and macOS hosts)
set -eu
arch="${1:-x86_64}"; version="${2:-0.0.0}"; kind="${3:-}"
root="$(cd "$(dirname "$0")/.." && pwd)"
. "$root/image/versions.env"
case "$arch" in x86_64) platform=linux/amd64 ;; aarch64) platform=linux/arm64 ;; *) echo "unknown arch $arch" >&2; exit 2 ;; esac
case "$kind" in
  vm) name=dockyard-vm; target=vm-export ;;
  guest) name=dockyard-guest; target=guest-export ;;
  *) name=dockyard-linux; target="export" ;;
esac
mkdir -p "$root/out"
tar="$root/out/$name-$version-$arch.tar"
docker buildx build "$root" -f "$root/image/Dockerfile" --platform "$platform" --target "$target" \
  --build-arg "ALPINE=$ALPINE" --build-arg "DOCKER=$DOCKER" --build-arg "BUILDX=$BUILDX" --build-arg "COMPOSE=$COMPOSE" \
  --build-arg "IMAGE_VERSION=$version" --output "type=tar,dest=$tar"
gzip -f "$tar"
ls -la "$tar.gz"
