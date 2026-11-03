#!/usr/bin/env bash
# Docker CE engine inside the WSL distro. Run as root: bash setup-wsl-docker.sh <linux-user>
set -euo pipefail
user="${1:?linux user required}"

if ! grep -q '^systemd=true' /etc/wsl.conf 2>/dev/null; then
  printf '\n[boot]\nsystemd=true\n' >> /etc/wsl.conf
  echo "systemd enabled in /etc/wsl.conf - run 'wsl --shutdown' and re-run this script"
  exit 2
fi

export DEBIAN_FRONTEND=noninteractive
if ! command -v dockerd >/dev/null; then
  apt-get update
  apt-get install -y ca-certificates curl
  install -m 0755 -d /etc/apt/keyrings
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
  . /etc/os-release
  echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${UBUNTU_CODENAME} stable" \
    > /etc/apt/sources.list.d/docker.list
  apt-get update
  apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
fi

# Windows reaches the engine on localhost:2375 (WSL mirrored networking); loopback only, no TLS.
mkdir -p /etc/systemd/system/docker.service.d
cat > /etc/systemd/system/docker.service.d/override.conf <<'EOF'
[Service]
ExecStart=
ExecStart=/usr/bin/dockerd -H fd:// -H tcp://127.0.0.1:2375 --tls=false --containerd=/run/containerd/containerd.sock
EOF

usermod -aG docker "$user"
systemctl daemon-reload
# On demand: the Docker tray (or docker.socket activation inside WSL) starts the engine.
systemctl disable docker.service containerd.service
systemctl enable docker.socket
systemctl restart docker
docker version --format 'Linux engine: {{.Server.Version}}'
