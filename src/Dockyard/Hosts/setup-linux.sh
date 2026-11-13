#!/bin/sh
# Dockyard setup for Linux, run as root (pkexec): setup-linux.sh install <user> [agent] [daemon.json] [dns-upstreams] | uninstall "" [daemon.json] | packages
set -eu

action="${1:?usage: setup-linux.sh install <user> | uninstall | packages}"
user="${2:-}"
agent="${3:-}"
daemon_json="${3:-}"
[ "$action" = install ] && daemon_json="${4:-}"
kept_dns="${5:-}"
kind_file="${3:-}"
kind_bin=/usr/local/bin/kind
kind_marker=/etc/dockyard/kind
rule=/etc/polkit-1/rules.d/50-dockyard.rules
socket_dropin_dir=/etc/systemd/system/docker.socket.d

[ -f /etc/os-release ] && . /etc/os-release
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

# A kind someone else put in /usr/local/bin stays theirs: Dockyard replaces and removes only the one it installed.
add_kind() {
  if [ -e "$kind_bin" ] && [ ! -f "$kind_marker" ]; then
    echo "$kind_bin was not installed by Dockyard and stays as it is"
    return 0
  fi
  install -m 0755 "${kind_file:?add-kind needs the file}" "$kind_bin"
  mkdir -p /etc/dockyard
  : > "$kind_marker"
  echo "Installed $kind_bin"
}

remove_kind() {
  [ -f "$kind_marker" ] || return 0
  rm -f "$kind_bin" "$kind_marker"
  echo "Removed $kind_bin"
}

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
RuntimeDirectory=dockyard
Restart=on-failure

[Install]
WantedBy=docker.service
EOF
  systemctl daemon-reload
  systemctl enable dockyard-agent.service >/dev/null 2>&1
  if systemctl is-active --quiet docker.service; then systemctl restart dockyard-agent.service; fi
}

# systemd-resolved sends one name space to one server and leaves the rest alone. Without it there is no such thing to
# hook into, and rewriting the machine's only resolver for this would be a poor trade: the wizard says what is missing.
route_domains() {
  [ -d /run/systemd/resolve ] || { echo "No systemd-resolved: container domains do not resolve by name here"; return 0; }
  mkdir -p /etc/systemd/resolved.conf.d
  printf '[Resolve]\nDNS=127.0.0.1:2353\nDomains=~dockyard.test\n' > /etc/systemd/resolved.conf.d/dockyard.conf
  systemctl restart systemd-resolved
  echo "This machine asks Dockyard about names under dockyard.test"
}

# The agent's addresses on lo and its rules outlive it until a reboot. They are found by what only Dockyard uses: its
# address range, its loopback address, its chain and its ports. So an older agent's rules go too, and duplicates as well.
remove_network() {
  for address in 198.19.250.1 198.19.250.254; do
    ip addr del "$address/32" dev lo 2>/dev/null || true
  done
  command -v iptables >/dev/null 2>&1 || return 0
  for table in filter nat; do
    iptables -w -t "$table" -S 2>/dev/null \
      | grep -E '^-A ' | grep -v '^-A DOCKYARD-INPUT ' \
      | grep -E '198\.19\.250\.|127\.77\.0\.1|DOCKYARD-INPUT|--dports 2378,2380,2381 ' \
      | while read -r line; do
          # shellcheck disable=SC2086
          set -- $line
          shift
          iptables -w -t "$table" -D "$@" || true
        done
  done
  iptables -w -t filter -F DOCKYARD-INPUT 2>/dev/null || true
  iptables -w -t filter -X DOCKYARD-INPUT 2>/dev/null || true
}

trust_ca() {
  if [ -d /usr/local/share/ca-certificates ] && command -v update-ca-certificates >/dev/null 2>&1; then
    install -m 0644 "$agent" /usr/local/share/ca-certificates/dockyard-local-ca.crt
    update-ca-certificates >/dev/null
  elif command -v update-ca-trust >/dev/null 2>&1; then
    # Arch has this command too, with its anchors elsewhere.
    anchors=/etc/pki/ca-trust/source/anchors
    [ -d "$anchors" ] || anchors=/etc/ca-certificates/trust-source/anchors
    install -d "$anchors"
    install -m 0644 "$agent" "$anchors/dockyard-local-ca.crt"
    update-ca-trust
  elif command -v trust >/dev/null 2>&1; then
    trust anchor --store "$agent"
    # What takes it out again needs the certificate itself.
    install -d /etc/dockyard
    install -m 0644 "$agent" /etc/dockyard/local-ca.crt
  else
    echo "This distribution has no trust store Dockyard knows how to change" >&2
    exit 5
  fi
  echo "This machine trusts Dockyard's certificate, for names under dockyard.test only"
}

untrust_ca() {
  if [ -f /usr/local/share/ca-certificates/dockyard-local-ca.crt ]; then
    rm -f /usr/local/share/ca-certificates/dockyard-local-ca.crt
    update-ca-certificates --fresh >/dev/null
  fi
  for anchors in /etc/pki/ca-trust/source/anchors /etc/ca-certificates/trust-source/anchors; do
    if [ -f "$anchors/dockyard-local-ca.crt" ]; then
      rm -f "$anchors/dockyard-local-ca.crt"
      update-ca-trust
    fi
  done
  if [ -f /etc/dockyard/local-ca.crt ]; then
    trust anchor --remove /etc/dockyard/local-ca.crt
    rm -f /etc/dockyard/local-ca.crt
  fi
}

# The DNS servers daemon.json named before the agent took their place; the agent asks them for containers. An empty
# file means there are none to keep any more.
keep_dns() {
  [ -n "$kept_dns" ] && [ -f "$kept_dns" ] || return 0
  if [ -s "$kept_dns" ]; then install -m 0644 "$kept_dns" /etc/dockyard/dns-upstreams; else rm -f /etc/dockyard/dns-upstreams; fi
}

# A running engine only reads this when it starts. A restart stops the user's containers, so with any running it waits for
# the engine's next start. Uninstall passes "now": what was written names an agent that is gone, and containers would lose DNS.
apply_daemon_json() {
  [ -n "$daemon_json" ] && [ -f "$daemon_json" ] || return 0
  if [ -f /etc/docker/daemon.json ] && cmp -s "$daemon_json" /etc/docker/daemon.json; then return 0; fi
  install -d /etc/docker
  install -m 0644 "$daemon_json" /etc/docker/daemon.json
  echo "Updated /etc/docker/daemon.json"
  has_systemd && systemctl is-active --quiet docker.service || return 0
  if [ "${1:-}" = now ] || [ -z "$(docker ps -q 2>/dev/null)" ]; then
    systemctl restart docker.service
  else
    echo "Containers are running, so this applies the next time Docker starts"
  fi
}

configure() {
  getent group docker >/dev/null || groupadd docker
  if [ -n "$user" ]; then
    usermod -aG docker "$user"
    echo "Added $user to the docker group"
  fi

  if has_systemd; then
    # Not --now: running this again must not stop the containers of an engine that is already running.
    systemctl disable docker.service containerd.service >/dev/null 2>&1 || true
    # Stopping and starting a few times within a minute trips systemd's limit, which stops the socket with it.
    systemctl reset-failed docker.service docker.socket >/dev/null 2>&1 || true
    systemctl enable --now docker.socket >/dev/null 2>&1
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
  trust-ca) trust_ca ;;
  untrust-ca) untrust_ca ;;
  remove-network) remove_network ;;
  # $3 is the checked kind binary here.
  add-kind) add_kind ;;
  remove-kind) remove_kind ;;
  install) install_packages; configure; grant_socket_access; install_tray_host; install_libsecret; install_agent; route_domains; keep_dns; apply_daemon_json ;;
  uninstall)
    untrust_ca
    if has_systemd; then systemctl disable --now dockyard-agent.service >/dev/null 2>&1 || true; fi
    remove_network
    rm -f /etc/systemd/system/dockyard-agent.service
    if [ -f /etc/systemd/resolved.conf.d/dockyard.conf ]; then
      rm -f /etc/systemd/resolved.conf.d/dockyard.conf
      systemctl restart systemd-resolved 2>/dev/null || true
    fi
    rm -rf /usr/local/lib/dockyard
    apply_daemon_json now
    rm -f "$rule"
    remove_kind
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
