#!/bin/sh
# Dockyard setup for Linux, run as root (pkexec): setup-linux.sh install <user> | uninstall | packages
set -eu

action="${1:?usage: setup-linux.sh install <user> | uninstall | packages}"
user="${2:-}"
rule=/etc/polkit-1/rules.d/50-dockyard.rules

. /etc/os-release
family=""
for id in ${ID:-} ${ID_LIKE:-}; do
  case "$id" in
    ubuntu|debian) family=apt ;;
    fedora|rhel|centos) family=dnf ;;
  esac
  [ -n "$family" ] && break
done

has_systemd() { [ -d /run/systemd/system ]; }

install_packages() {
  if command -v dockerd >/dev/null 2>&1; then
    echo "Docker is already installed: $(dockerd --version)"
    return 0
  fi
  case "$family" in
    apt)
      repo=debian
      case " ${ID:-} ${ID_LIKE:-} " in *" ubuntu "*) repo=ubuntu ;; esac
      codename="${UBUNTU_CODENAME:-${VERSION_CODENAME:-}}"
      export DEBIAN_FRONTEND=noninteractive
      apt-get update -qq
      apt-get install -y -qq ca-certificates curl >/dev/null
      install -m 0755 -d /etc/apt/keyrings
      curl -fsSL "https://download.docker.com/linux/$repo/gpg" -o /etc/apt/keyrings/docker.asc
      chmod a+r /etc/apt/keyrings/docker.asc
      echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/$repo $codename stable" \
        > /etc/apt/sources.list.d/docker.list
      apt-get update -qq
      apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin >/dev/null
      ;;
    dnf)
      repo=rhel
      [ "${ID:-}" = fedora ] && repo=fedora
      url="https://download.docker.com/linux/$repo/docker-ce.repo"
      dnf -y -q install dnf-plugins-core
      dnf config-manager addrepo --from-repofile="$url" 2>/dev/null || dnf config-manager --add-repo "$url"
      dnf -y -q install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
      ;;
    *)
      echo "Unsupported distribution '${ID:-unknown}': install Docker CE yourself (https://docs.docker.com/engine/install/), then run setup again." >&2
      exit 4
      ;;
  esac
  echo "Installed $(dockerd --version)"
}

configure() {
  getent group docker >/dev/null || groupadd docker
  if [ -n "$user" ]; then
    usermod -aG docker "$user"
    echo "Added $user to the docker group (applies after signing out and back in)"
  fi

  if has_systemd; then
    systemctl disable --now docker.service containerd.service >/dev/null 2>&1 || true
    systemctl enable docker.socket >/dev/null 2>&1
    echo "Docker starts on demand (docker.socket)"
  fi

  mkdir -p "$(dirname "$rule")"
  cat > "$rule" <<'EOF'
// Dockyard: members of the docker group may start and stop the Docker engine.
polkit.addRule(function(action, subject) {
  if (action.id == "org.freedesktop.systemd1.manage-units" && subject.isInGroup("docker")) {
    var unit = action.lookup("unit");
    if (unit == "docker.service" || unit == "docker.socket" || unit == "containerd.service") {
      return polkit.Result.YES;
    }
  }
});
EOF
  echo "Installed $rule"
}

case "$action" in
  packages) install_packages ;;
  install) install_packages; configure ;;
  uninstall)
    rm -f "$rule"
    if has_systemd; then systemctl enable docker.service containerd.service >/dev/null 2>&1 || true; fi
    echo "Removed $rule; Docker packages, images and containers were kept"
    ;;
  *) echo "unknown action: $action" >&2; exit 2 ;;
esac
