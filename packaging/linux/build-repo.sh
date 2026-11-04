#!/bin/sh
# build-repo.sh <packages-dir> <out-dir>: apt and dnf repositories for the .deb/.rpm files, signed with the
# GPG key in the keyring (import REPO_GPG_KEY first). Layout: <out>/apt, <out>/rpm, <out>/dockyard.asc.
set -eu
pkgs="$1"; out="$2"
key="$(gpg --list-secret-keys --with-colons | awk -F: '/^sec/ { print $5; exit }')"
[ -n "$key" ] || { echo "no signing key in the GPG keyring" >&2; exit 1; }
sign() { gpg --batch --yes ${REPO_GPG_PASSPHRASE:+--pinentry-mode loopback --passphrase "$REPO_GPG_PASSPHRASE"} --local-user "$key" "$@"; }

mkdir -p "$out/apt/pool/main" "$out/apt/dists/stable/main/binary-amd64" "$out/rpm"
cp "$pkgs"/*.deb "$out/apt/pool/main/"
cp "$pkgs"/*.rpm "$out/rpm/"
gpg --armor --export "$key" > "$out/dockyard.asc"

cd "$out/apt"
apt-ftparchive packages pool > dists/stable/main/binary-amd64/Packages
gzip -kf dists/stable/main/binary-amd64/Packages
apt-ftparchive -o APT::FTPArchive::Release::Origin=Dockyard -o APT::FTPArchive::Release::Label=Dockyard \
  -o APT::FTPArchive::Release::Suite=stable -o APT::FTPArchive::Release::Codename=stable \
  -o APT::FTPArchive::Release::Architectures=amd64 -o APT::FTPArchive::Release::Components=main \
  release dists/stable > dists/stable/Release
sign --clearsign -o dists/stable/InRelease dists/stable/Release
sign --armor --detach-sign -o dists/stable/Release.gpg dists/stable/Release

cd "$out/rpm"
createrepo_c --quiet .
sign --armor --detach-sign -o repodata/repomd.xml.asc repodata/repomd.xml
cat > dockyard.repo <<REPO
[dockyard]
name=Dockyard
baseurl=${DOCKYARD_REPO_URL:-https://byhofman.github.io/dockyard}/rpm
enabled=1
gpgcheck=0
repo_gpgcheck=1
gpgkey=${DOCKYARD_REPO_URL:-https://byhofman.github.io/dockyard}/dockyard.asc
REPO
echo "repository in $out"
