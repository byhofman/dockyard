#!/bin/sh
# Dockyard setup for Linux, run as root (pkexec): setup-linux.sh install <user> | uninstall | packages
set -eu

action="${1:?usage: setup-linux.sh install <user> | uninstall | packages}"
user="${2:-}"
rule=/etc/polkit-1/rules.d/50-dockyard.rules
socket_dropin_dir=/etc/systemd/system/docker.socket.d

. /etc/os-release
family=""
for id in ${ID:-} ${ID_LIKE:-}; do
  case "$id" in
    ubuntu|debian) family=apt ;;
    fedora|rhel|centos) family=dnf ;;
    arch) family=pacman ;;
    opensuse*|suse|sles) family=zypper ;;
  esac
  [ -n "$family" ] && break
done

has_systemd() { [ -d /run/systemd/system ]; }

add_docker_repo() {
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
      ;;
    dnf)
      repo=rhel
      [ "${ID:-}" = fedora ] && repo=fedora
      url="https://download.docker.com/linux/$repo/docker-ce.repo"
      dnf -y -q install dnf-plugins-core
      dnf config-manager addrepo --from-repofile="$url" 2>/dev/null || dnf config-manager --add-repo "$url"
      ;;
  esac
}

install_packages() {
  if command -v dockerd >/dev/null 2>&1; then
    echo "Docker is already installed: $(dockerd --version)"
    return 0
  fi
  case "$family" in
    apt)
      add_docker_repo
      apt-get install -y -qq docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin >/dev/null
      ;;
    dnf)
      add_docker_repo
      dnf -y -q install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
      ;;
    pacman) pacman -Sy --noconfirm --needed docker docker-buildx docker-compose ;;
    zypper) zypper --non-interactive install docker docker-buildx docker-compose ;;
    *)
      echo "Dockyard does not know how to install Docker on '${ID:-unknown}'. Install it with your distribution's package manager and run the wizard again." >&2
      exit 4
      ;;
  esac
  echo "Installed $(dockerd --version)"
}

# Docker may have come from elsewhere than Docker's repository, which is where these plugins live.
add_plugin() {
  case "$family" in
    apt)
      export DEBIAN_FRONTEND=noninteractive
      apt-get update -qq || true
      if ! apt-get install -y -qq "$user" >/dev/null 2>&1; then
        add_docker_repo
        apt-get install -y -qq "$user" >/dev/null
      fi
      ;;
    dnf) dnf -y -q install "$user" || { add_docker_repo; dnf -y -q install "$user"; } ;;
    pacman) pacman -S --noconfirm --needed "$user" ;;
    zypper) zypper --non-interactive install "$user" ;;
    *) echo "Add $user with your distribution's package manager" >&2; exit 4 ;;
  esac
  echo "Added $user"
}

# The tray icon needs a StatusNotifierItem host; Ubuntu ships one, other GNOME desktops have it as a package.
install_tray_host() {
  command -v gnome-shell >/dev/null 2>&1 || return 0
  case "$family" in
    apt) DEBIAN_FRONTEND=noninteractive apt-get install -y -qq gnome-shell-extension-appindicator >/dev/null 2>&1 || true ;;
    dnf) dnf -y -q install gnome-shell-extension-appindicator >/dev/null 2>&1 || true ;;
  esac
}

install_acl() {
  command -v setfacl >/dev/null 2>&1 && return 0
  case "$family" in
    apt) DEBIAN_FRONTEND=noninteractive apt-get install -y -qq acl >/dev/null 2>&1 || true ;;
    dnf) dnf -y -q install acl >/dev/null 2>&1 || true ;;
    pacman) pacman -S --noconfirm --needed acl >/dev/null 2>&1 || true ;;
    zypper) zypper --non-interactive install acl >/dev/null 2>&1 || true ;;
  esac
}

# Group membership only reaches sessions started after it changed. An ACL on the socket lets the user's current
# session use docker right away, without signing out, and the drop-in renews it whenever systemd recreates the socket.
grant_socket_access() {
  [ -n "$user" ] || return 0
  install_acl
  setfacl_bin="$(command -v setfacl 2>/dev/null)" || { echo "setfacl is not available: docker access applies after signing out and back in"; return 0; }
  if has_systemd; then
    mkdir -p "$socket_dropin_dir"
    cat > "$socket_dropin_dir/dockyard.conf" <<EOF
[Socket]
ExecStartPost=-$setfacl_bin -m u:$user:rw /run/docker.sock
EOF
    systemctl daemon-reload
  fi
  if [ -S /run/docker.sock ]; then "$setfacl_bin" -m "u:$user:rw" /run/docker.sock; fi
  echo "$user can use docker right away"
}

configure() {
  getent group docker >/dev/null || groupadd docker
  if [ -n "$user" ]; then
    usermod -aG docker "$user"
    echo "Added $user to the docker group"
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
  mkdir -p /etc/dockyard
  : > /etc/dockyard/installed
}

case "$action" in
  packages) install_packages ;;
  # $2 is the package here.
  add-plugin) add_plugin ;;
  install) install_packages; configure; grant_socket_access; install_tray_host ;;
  uninstall)
    rm -f "$rule"
    rm -rf "$socket_dropin_dir" /etc/dockyard
    if has_systemd; then
      systemctl daemon-reload
      systemctl enable docker.service containerd.service >/dev/null 2>&1 || true
    fi
    # apt fails on a package it no longer knows, as a removed dashboard is once the local .deb is gone.
    case "$family" in
      apt)
        for package in dockyard-dashboard dockyard; do
          if dpkg -s "$package" >/dev/null 2>&1; then DEBIAN_FRONTEND=noninteractive apt-get remove -y -qq "$package" >/dev/null; fi
        done
        ;;
      dnf)
        for package in dockyard-dashboard dockyard; do
          if rpm -q "$package" >/dev/null 2>&1; then dnf -y -q remove "$package"; fi
        done
        ;;
    esac
    echo "Removed Dockyard; Docker packages, images and containers were kept"
    ;;
  *) echo "unknown action: $action" >&2; exit 2 ;;
esac
