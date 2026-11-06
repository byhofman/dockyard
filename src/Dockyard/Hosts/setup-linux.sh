#!/bin/sh
# Dockyard setup for Linux, run as root (pkexec): setup-linux.sh install <user> [agent] [daemon.json] | uninstall "" [daemon.json] | packages
set -eu

action="${1:?usage: setup-linux.sh install <user> | uninstall | packages}"
user="${2:-}"
agent="${3:-}"
daemon_json="${3:-}"
[ "$action" = install ] && daemon_json="${4:-}"
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
    pacman) pacman -Sy --noconfirm --needed docker docker-buildx docker-compose ;;
    zypper) zypper --non-interactive install docker docker-buildx docker-compose ;;
    *)
      echo "Dockyard does not know how to install Docker on '${ID:-unknown}'. Install it with your distribution's package manager and run the wizard again." >&2
      exit 4
      ;;
  esac
  echo "Installed $(dockerd --version)"
}

# The tray icon needs a StatusNotifierItem host; Ubuntu ships one, other GNOME desktops have it as a package.
install_tray_host() {
  command -v gnome-shell >/dev/null 2>&1 || return 0
  case "$family" in
    apt) DEBIAN_FRONTEND=noninteractive apt-get install -y -qq gnome-shell-extension-appindicator >/dev/null 2>&1 || true ;;
    dnf) dnf -y -q install gnome-shell-extension-appindicator >/dev/null 2>&1 || true ;;
  esac
}

install_libsecret() {
  command -v gnome-shell >/dev/null 2>&1 || [ -n "${XDG_CURRENT_DESKTOP:-}" ] || return 0
  case "$family" in
    apt) DEBIAN_FRONTEND=noninteractive apt-get install -y -qq libsecret-1-0 >/dev/null 2>&1 || true ;;
    dnf) dnf -y -q install libsecret >/dev/null 2>&1 || true ;;
    pacman) pacman -S --noconfirm --needed libsecret >/dev/null 2>&1 || true ;;
    zypper) zypper --non-interactive install libsecret-1-0 >/dev/null 2>&1 || true ;;
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

# Tied to docker.service in both directions: it needs the engine's socket to be useful, and connecting to that socket
# itself would start an engine that is supposed to start on demand.
install_agent() {
  [ -n "$agent" ] || return 0
  install -d /usr/local/lib/dockyard
  install -m 0755 "$agent" /usr/local/lib/dockyard/dockyard-agent
  has_systemd || return 0
  cat > /etc/systemd/system/dockyard-agent.service <<'EOF'
[Unit]
Description=Dockyard agent: host.docker.internal and names for containers
BindsTo=docker.service
After=docker.service

[Service]
ExecStart=/usr/local/lib/dockyard/dockyard-agent --mode=host
Restart=on-failure

[Install]
WantedBy=docker.service
EOF
  systemctl daemon-reload
  systemctl enable dockyard-agent.service >/dev/null 2>&1
  if systemctl is-active --quiet docker.service; then systemctl restart dockyard-agent.service; fi
}

# A running engine only reads this when it starts, and restarting it would stop the user's containers without a change to apply.
apply_daemon_json() {
  [ -n "$daemon_json" ] && [ -f "$daemon_json" ] || return 0
  if [ -f /etc/docker/daemon.json ] && cmp -s "$daemon_json" /etc/docker/daemon.json; then return 0; fi
  install -d /etc/docker
  install -m 0644 "$daemon_json" /etc/docker/daemon.json
  if has_systemd && systemctl is-active --quiet docker.service; then systemctl restart docker.service; fi
  echo "Updated /etc/docker/daemon.json"
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
  install) install_packages; configure; grant_socket_access; install_tray_host; install_libsecret; install_agent; apply_daemon_json ;;
  uninstall)
    if has_systemd; then systemctl disable --now dockyard-agent.service >/dev/null 2>&1 || true; fi
    rm -f /etc/systemd/system/dockyard-agent.service
    rm -rf /usr/local/lib/dockyard
    apply_daemon_json
    rm -f "$rule"
    rm -rf "$socket_dropin_dir" /etc/dockyard
    rm -f /usr/local/bin/docker-credential-secretservice
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
