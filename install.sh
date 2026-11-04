#!/bin/sh
# Dockyard installer for Linux:  curl -fsSL https://raw.githubusercontent.com/byhofman/dockyard/main/install.sh | sh
# Options (after "sh -s --"): --engine-only  (tray and engine without the dashboard)   --no-setup  (packages only)
set -eu
repo="${DOCKYARD_REPO_URL:-https://byhofman.github.io/dockyard}"
engine_only=0; setup=1
for arg in "$@"; do
  case "$arg" in
    --engine-only) engine_only=1 ;;
    --no-setup) setup=0 ;;
  esac
done

sudo=""; [ "$(id -u)" -eq 0 ] || sudo="sudo"
packages="dockyard"; [ $engine_only -eq 1 ] || packages="dockyard dockyard-dashboard"

if command -v apt-get >/dev/null 2>&1; then
  $sudo install -m 0755 -d /etc/apt/keyrings
  curl -fsSL "$repo/dockyard.asc" | $sudo tee /etc/apt/keyrings/dockyard.asc >/dev/null
  echo "deb [signed-by=/etc/apt/keyrings/dockyard.asc] $repo/apt stable main" | $sudo tee /etc/apt/sources.list.d/dockyard.list >/dev/null
  $sudo apt-get update -qq
  $sudo env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq $packages
elif command -v dnf >/dev/null 2>&1; then
  curl -fsSL "$repo/rpm/dockyard.repo" | $sudo tee /etc/yum.repos.d/dockyard.repo >/dev/null
  $sudo dnf -y -q install $packages
else
  echo "No apt or dnf found. Download Dockyard-<version>-linux-x64.tar.gz from https://github.com/byhofman/dockyard/releases instead." >&2
  exit 1
fi

if [ $setup -eq 1 ] && [ "$(id -u)" -ne 0 ]; then
  dockyard --install
elif [ $setup -eq 1 ]; then
  echo "Installed. Run 'dockyard --install' as your own user (not root) to finish the setup."
fi
