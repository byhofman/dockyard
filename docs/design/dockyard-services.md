# Dockyard services and dockyard-host

Status: direction set by the owner 2026-11-25: "the toolbox is just the installer, it should not host any port. These
services for DNS and port proxying should be in containers as much as possible, and if any OS integration is needed, that
should be a separate OS native service providing only the integration part (adapter)."

## Three parts

| Part | Runs as | Does | Never does |
|---|---|---|---|
| **dockyard command**, with the tray and the dashboard | the command, the tray icon and the window the user opens, over `Dockyard.Setup` | installs, updates and removes components and features, including what needs administrator rights once: DNS routing rules (NRPT, `/etc/resolver`, systemd-resolved) and trusting the CA; settings; shows state | listen on a port, relay traffic, hold an engine alive |
| **dockyard-host** | an OS-native background service per OS: a per-user logon service on Windows (WSL is per user), a LaunchAgent on macOS, a systemd user unit on Linux | only what has to run while the engine does: loopback relays into the engine where the OS cannot reach it, the engine's pipes and socket for the docker CLI, keeping the engine running | installing anything, names, routing decisions, certificates |
| **Services** | containers in the Dockyard group, pinned by digest | DNS for `*.dockyard.internal` and what external-dns writes; the proxy for container names and cluster ingress; kind's cloud provider; NFS | anything the OS has to be asked for |

dockyard-host is small, has no UI, and its behaviour is a fixed list of OS operations, so it can be reviewed and run with
the least rights it needs. `dockyard setup` installs and updates it like any other component.

## Where things are today (0.9.0) and where they go

| Today | Goes to |
|---|---|
| `127.77.0.1:53/80/443` relay into the engine, in the installer's process (Windows) | dockyard-host |
| published container ports on Windows' localhost, in the installer's process | dockyard-host |
| the engine's named pipes for docker and the agent API, in the installer's process | dockyard-host |
| the WSL keep-alive (`wsl.exe … dockyard-init`), held by the installer | dockyard-host |
| DNS for `*.dockyard.internal` and the name proxy with TLS, in the engine agent | `dockyard-dns` (CoreDNS + etcd) and `dockyard-proxy` (Traefik) containers |
| a cluster's ingress by `<host>.<cluster>.dockyard.internal` (0.9.0, in the agent) | a Traefik route per cluster in `dockyard-proxy`, TLS passed through |
| Windows bind paths rewritten in the docker API (0.9.0, in the agent) | stays in the engine side, where the docker API passes |
| kind's cloud provider, NFS (0.9.0) | already containers in the Dockyard group |

The agent keeps what only a process in the engine's network can do: reporting ports and memory to the host, the host
proxy for `host.docker.internal`, and the iptables rules that bring container traffic to the services.

## Steps

Done in 0.9.0: step 1 on Windows. `dockyard-host.exe` (src/Dockyard.Host) keeps the WSL engine running, serves its pipes, relays `127.77.0.1` for container domains and forwards published ports; it starts at logon by its own entry, and Dockyard.Setup only asks it over `dockyard-host-<user>`. The dockyard command carries it and installs it until it is released on its own. Still to do in step 1: macOS (the VM process and its relays) and Linux.

Done in 0.9.0: step 2. The agent runs `dockyard-dns` (CoreDNS 1.13.1) and `dockyard-proxy` (Traefik v3.6), pinned by digest, on the engine's own network, labelled `dockyard.service` so they show in the Dockyard group. It writes their configuration to `/var/lib/docker/dockyard/services`, which they reload by themselves:

- **DNS:** an authoritative zone file per view. Containers get `198.19.250.1`, and the host gets `127.77.0.1` (or the VM's address on the Mac). A wildcard per kind cluster covers its ingress hosts. Any other name is NXDOMAIN, and the containers' view forwards the rest upstream.
- **Proxy:** Traefik has a router per container name and a passthrough TCP router per cluster on 443. A catch-all sends what has no route to the agent on port 2384, which says why in plain words.
- **Ports:** iptables now DNATs ports 80 and 443 to `198.19.250.1`, so Traefik listens there and not on every interface.
- **Fallback:** when the images cannot be had, the agent serves names itself as before.

Verified on Windows (WSL); the Mac VM and Linux host run through CI.

Done in 0.9.0 as well:

- **Step 1 on macOS and Linux.**
  - On the Mac, the VM helper is `dockyard-host`, under a LaunchAgent of its own: launchd restarts it after a failure, and it is never stopped by the tray or the dashboard quitting.
  - On Linux, `dockyard-host.service`.
- **Step 3.**
  - `.internal` replaces `.test`, with no alias.
  - The root CA allows one level below it. `dockyard https cluster-issuer <cluster>` issues an intermediate CA, name constrained to `<cluster>.dockyard.internal`.
  - The cert-manager add-on turns that intermediate into the `ClusterIssuer` called `dockyard`.
- **external-dns (step 4, without declared domains).**
  - `dockyard-etcd` runs next to CoreDNS. Every cluster with the load balancer gets external-dns with the CoreDNS provider, under the prefix `/dockyard/<cluster>/`.
  - The agent reads the records into the zones. A record pointing at the cluster's ingress resolves to the proxy, which routes it by Host header and SNI.
  - Records of a stopped or deleted cluster are not answered, and deleting a cluster clears its prefix.

Still open: declared domains outside `dockyard.internal` (step 4's approval per domain and separate constrained root). Any container can write to `dockyard-etcd`; it is reachable from containers only, but a container could publish a name under `dockyard.internal`. A name a container already has always wins.

1. **dockyard-host, Windows first.** A `dockyard-host` executable (a component of its own, released as `host/<semver>`)
   takes over the relays, the pipes and the keep-alive from the installer; Dockyard.Setup talks to it over a local pipe to
   start and stop engines. Started at logon by dockyard-host's own entry, not by the tray. Then the same for macOS and Linux.
2. **`dockyard-dns` and `dockyard-proxy`.** CoreDNS answering `*.dockyard.internal`, Traefik with a file provider the agent writes from the running containers (name, detected port, HTTPS when on;
   a container's own `traefik.*` labels win). dockyard-host's DNS rule points at `dockyard-dns`, its relay at
   `dockyard-proxy`. Images are published as `byhofman/dockyard-*` on Docker Hub from `services/<semver>` tags when one of
   ours is needed; upstream images pinned by digest otherwise.
3. **`.internal` and the new CA**, constrained to `dockyard.internal`; per cluster an intermediate for cert-manager,
   constrained to `*.<cluster>.dockyard.internal`.
4. **external-dns and declared domains**, as agreed earlier (one approval per domain, a separate constrained root,
   a warning when the domain exists publicly).

Each step keeps the previous one working until its replacement is in, and the wizard end-to-end tests cover each OS.
