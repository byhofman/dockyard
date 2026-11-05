# 2. What Dockyard does today

**Deelvraag:** What does Dockyard do today for each of those features?
**Bar:** `technical`.
**Currency pass, run 2026-11-05:** everything below is true of commit `b227337` on `main`. All grades are `source` or the README at that commit.

**Limit that applies to every claim here:** Dockyard was read, not run. The Mac behaviour is untested by this programme. An absence means the search named in the claim found nothing in the listed paths.

#### F-02-01 · Most of the Docker basics are already at parity

| | |
|---|---|
| **Claim** | Dockyard has an engine with Compose and buildx CLIs, a docker context, bind mounts at the same paths, published ports forwarded from the VM, amd64 through Rosetta, a dashboard for containers, Compose, images, volumes, networks and disk, live logs and stats, `docker exec` with a shell in a terminal, and in-app updates. |
| **Bar / Grade** | `technical` · **source** |
| **Sources** | `README.md`, `src/Dockyard.Dashboard/Features/`, `src/Dockyard.Agent/Agent.cs`, `macos/DockyardVM/Sources/dockyard-vm/main.swift` at `b227337` — read: `full` for the README and agent, excerpts for the rest |
| **Population / Scope** | All three desktop platforms per the README; Windows also has a Windows-container engine, which OrbStack does not offer. |
| **Licenses** | Treating these as parity rather than gaps. |
| **Does NOT support** | Equal speed or reliability; no benchmark was run. |
| **Requirements** | *(none)* |
| **Open** | The planned macOS test pass. |

#### F-02-02 · Nothing provides `host.docker.internal`

| | |
|---|---|
| **Claim** | The name does not appear in the repo, and the macOS VM has one NAT network device. |
| **Bar / Grade** | `technical` · **source** |
| **Sources** | Case-insensitive search for `host.docker.internal` and `host-gateway` in `src`, `image`, `macos`, `scripts`, `README.md`: no hits. `main.swift` lines 96-97 — read: `full` |
| **Population / Scope** | `b227337`. |
| **Licenses** | Treating the name as absent on Dockyard's Linux engines. |
| **Does NOT support** | That `--add-host …=host-gateway` reaches the Mac on the VM. It probably reaches the VM rather than the Mac, but that was not tested. |
| **Requirements** | `DOK-7` |
| **Open** | Test on each host OS. |

#### F-02-03 · `--net host` servers are not forwarded on macOS

| | |
|---|---|
| **Claim** | The forward list is built only from each running container's published ports, so a host-network container, which has none, adds no forward. |
| **Bar / Grade** | `technical` · **source** |
| **Sources** | `PublishedPorts.From`, `src/Dockyard.Agent/Protocol.cs` lines 81-89 — read: `full` |
| **Population / Scope** | macOS VM path. The Windows path through WSL mirrored networking was not examined. |
| **Licenses** | Treating macOS host networking as a gap. |
| **Does NOT support** | Any statement about Windows. Linux hosts run Docker natively, so the gap does not apply there. |
| **Requirements** | `DOK-11` |
| **Open** | Check Windows with mirrored networking. |

#### F-02-04 · `localhost` hangs for published Linux ports on Windows

| | |
|---|---|
| **Claim** | The README tells users to use `127.0.0.1`, because Windows resolves `localhost` to `::1` first, mirrored networking does not forward that to WSL, and the connection hangs. The dashboard opens `127.0.0.1` links for the same reason. |
| **Bar / Grade** | `technical` · **source** (README, Gotchas) |
| **Sources** | `README.md` — read: `full` |
| **Population / Scope** | Windows with the Linux engine. macOS listeners are IPv4 only (`Sockets.swift`, about line 206), which should fall back quickly, but that was not tested. |
| **Licenses** | Treating `localhost` on Windows as a known defect. |
| **Does NOT support** | A cause beyond what the README states. |
| **Requirements** | `DOK-8` |
| **Open** | None. |

#### F-02-05 · Tools that ignore docker contexts need manual setup on macOS

| | |
|---|---|
| **Claim** | The README tells macOS users to create the `/var/run/docker.sock` symlink by hand, and Testcontainers is pinned only in the Windows setup path. |
| **Bar / Grade** | `technical` · **source** |
| **Sources** | `README.md` (macOS), `src/Dockyard/Core/UserSetup.cs` line 21, called from `WindowsHost.cs` line 92 — read: `full` |
| **Population / Scope** | `b227337`. The macOS setup already takes one administrator prompt for `/etc/paths.d`. |
| **Licenses** | Treating tool discovery on macOS as a gap. |
| **Does NOT support** | That every tool fails; those that read contexts or `DOCKER_HOST` work. |
| **Requirements** | `DOK-9` |
| **Open** | None. |

#### F-02-06 · Dockyard configures no credential store

| | |
|---|---|
| **Claim** | The repo never installs or configures a Docker credential helper. |
| **Bar / Grade** | `technical` · **source** |
| **Sources** | Search for `docker-credential`, `credsStore`, `wincred` in `src`, `image`, `macos`, `install*.sh`: no hits. `osxkeychain` in `src`, `image`, `macos`, `scripts`, `README.md`: no hits |
| **Population / Scope** | `b227337`. Whether Docker's static CLI archives bundle a helper was not checked. `install.ps1` was searched too: no hits. |
| **Licenses** | Treating secure credential storage as unaddressed by Dockyard. |
| **Does NOT support** | That credentials are stored unencrypted on every machine; a user can configure a helper themselves. |
| **Requirements** | `DOK-10` |
| **Open** | Confirm what `docker login` writes on each OS after a clean install. |

#### F-02-07 · The Linux engine's configuration has no supported way in

| | |
|---|---|
| **Claim** | No user-facing setting or file changes dockerd's options. `dockyard-init` starts dockerd with fixed flags; the macOS root disk is read-only per the spec; and the only `daemon.json` Dockyard writes is the Windows engine's, generated during setup. |
| **Bar / Grade** | `technical` · **source** |
| **Sources** | `image/rootfs/usr/local/sbin/dockyard-init`, `src/Dockyard/Core/MachineSetup.cs` lines 142-143, `docs/superpowers/specs/2026-11-04-dockyard-macos-vm-design.md`, README Settings section — read: `full` for the init script, README and those lines |
| **Population / Scope** | Linux engine on macOS and WSL. On Linux hosts the user owns `/etc/docker/daemon.json`. |
| **Licenses** | Treating engine configuration as a gap on macOS and WSL. |
| **Does NOT support** | That WSL users have no manual route; root access to the distro exists. A read-only root on the Mac was read in the spec, not observed. |
| **Requirements** | `DOK-12` |
| **Open** | None. |

#### F-02-08 · Domains, HTTPS, direct IPs, Docker Desktop import and proxy handling are absent

| | |
|---|---|
| **Claim** | No code or doc implements container domains, a reverse proxy or local CA, host-to-container-IP access, or import from Docker Desktop; proxy variables are not handled; and the Mac test script has no VPN or DNS step. The only migration is from Dockyard's own legacy engine. |
| **Bar / Grade** | `technical` · **source** |
| **Sources** | Searches over `src`, `image`, `macos`, `README.md`, `scripts`: `HTTP_PROXY`, `SOCKS`, `vpn`, `ipv6`, `SSH_AUTH`, `ssh-agent`, `checkpoint`: no relevant hits. Search of `src/Dockyard` and the installers for `Docker Desktop` and `com.docker.docker`: only an unrelated UI subtitle. `UserSetup.cs` lines 95-109; `scripts/mac-e2e.sh` — read: `full` for the script's step list |
| **Population / Scope** | `b227337`. A search for `domain`, `reverse proxy`, `mkcert` and `.local` matched only Unix domain socket and `AppDomain` code. The README's Gotchas section documents Intune-managed Windows devices, so managed machines are a known audience. |
| **Licenses** | Treating these as absent, and VPN and DNS behaviour as unverified rather than broken. |
| **Does NOT support** | That VPNs fail on Dockyard. VM traffic goes through macOS NAT, and it may simply work. |
| **Requirements** | `DOK-13`, `DOK-14`, `DOK-15`, `DOK-16` |
| **Open** | A VPN and proxy test on each host OS. |

#### F-02-09 · File access and tray actions are partly on the backlog

| | |
|---|---|
| **Claim** | `DOK-2` covers tray quick actions, `DOK-3` an embedded terminal and `DOK-5` files inside containers. No backlog item covers files inside volumes or images. The dashboard's Images view has no build or history feature, and the Volumes view has no backup or clone. |
| **Bar / Grade** | `technical` · **source** |
| **Sources** | `docs/backlog.md`, `README.md` Dashboard section, dashboard design spec — read: `full` |
| **Population / Scope** | `b227337`. |
| **Licenses** | Mapping those OrbStack features onto existing IDs instead of duplicating them. |
| **Does NOT support** | Any change to existing items' scope. |
| **Requirements** | `DOK-2`, `DOK-3`, `DOK-5`, `DOK-17` |
| **Open** | None. |
