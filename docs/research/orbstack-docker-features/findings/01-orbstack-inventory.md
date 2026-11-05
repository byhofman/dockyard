# 1. OrbStack's Docker features today

**Deelvraag:** Which Docker features does OrbStack have today, and which are free?
**Bar:** `market`.
**Verification pass, run 2026-11-05:** every Docker page in the docs sitemap was read, plus 12 months of release notes (latest: v2.2.5, 23 October 2026) and the pricing page. The core behaviours were then observed on OrbStack 2.2.5 on an M1 Mac, using a temporary container from an image already on the machine, removed afterwards.

**Observed versus documented:** F-01-01 to F-01-04 were observed and the rest are documented. Debug Shell was not run, because it needs a Pro licence. The docs footer still says 2024 and the Docker Desktop comparison is dated January 2025, so version-specific statements rely on the release notes instead.

#### F-01-01 · Each container gets a working domain with no port and no `-p`

| | |
|---|---|
| **Claim** | A container with no published port answered at `<name>.orb.local` on port 80 and at its own container IP; a custom domain set by label also answered, and `orb.local` returned a redirect (302). |
| **Bar / Grade** | `market` · **verified** |
| **Sources** | OrbStack 2.2.5, probed 2026-11-05. [Domains](https://docs.orbstack.dev/docker/domains), [Network](https://docs.orbstack.dev/docker/network), [Labels](https://docs.orbstack.dev/docker/labels) — read: `full` |
| **Population / Scope** | OrbStack 2.2.5, macOS, one container serving HTTP on 8080. The `service.project.orb.local` Compose form, wildcards and per-domain ports are documented, not observed. |
| **Licenses** | A statement that OrbStack offers zero-config container domains as a working feature. |
| **Does NOT support** | Instant availability: the first request after start was refused and the next succeeded. The probe's server answered one request at a time, so the cause is not established. Also no evidence for any OS other than macOS. |
| **Requirements** | `DOK-15` |
| **Open** | Whether Windows and Linux hosts can offer the same thing. Closed by a build spike. |

#### F-01-02 · Those domains serve HTTPS with a certificate the host trusts

| | |
|---|---|
| **Claim** | `https://<name>.orb.local` returned 200 with certificate verification passing, issued for that name by "OrbStack Development Root CA". OrbStack's root certificate was also present in the container's certificate directory. |
| **Bar / Grade** | `market` · **verified** |
| **Sources** | OrbStack 2.2.5, probed 2026-11-05. [HTTPS for containers](https://docs.orbstack.dev/features/https) — read: `full` |
| **Population / Scope** | One container on a Mac where the root CA was already installed, so the first-use trust prompt was not observed. The docs state the private key is held encrypted in the macOS keychain; that was not observed. |
| **Licenses** | A statement that OrbStack offers working automatic HTTPS for container domains. |
| **Does NOT support** | Immediate success: the first HTTPS try timed out and the second failed verification on a self-signed chain; only the third passed. This may be first-use certificate setup or the single-request probe server. Node trust inside containers is not established either. |
| **Requirements** | `DOK-16` |
| **Open** | What a safe trust model looks like outside the macOS keychain. |

#### F-01-03 · Host integration works out of the box

| | |
|---|---|
| **Claim** | `host.docker.internal` resolved inside a container, `/var/run/docker.sock` is a symlink to OrbStack's socket, `docker-credential-osxkeychain` ships with the app, and a `--net host` server with no `-p` answered on the Mac's `localhost`. |
| **Bar / Grade** | `market` · **verified** |
| **Sources** | OrbStack 2.2.5, probed 2026-11-05. [Docker containers](https://docs.orbstack.dev/docker/), [Host networking](https://docs.orbstack.dev/docker/host-networking) — read: `full` |
| **Population / Scope** | OrbStack 2.2.5, macOS. Connecting out to a server on the Mac was not tested, only name resolution. The socket symlink needs admin access, per the docs. |
| **Licenses** | A statement that these are OrbStack's defaults. |
| **Does NOT support** | That all of them are unique to OrbStack; see F-03-01. |
| **Requirements** | `DOK-7`, `DOK-9`, `DOK-10`, `DOK-11` |
| **Open** | None. |

#### F-01-04 · Container, image and volume files are reachable from the host

| | |
|---|---|
| **Claim** | `~/OrbStack/docker` holds `containers`, `images` and `volumes`, and a running container's `/etc/alpine-release` was visible through it. |
| **Bar / Grade** | `market` · **verified** |
| **Sources** | OrbStack 2.2.5, probed 2026-11-05. [Container, image, volume files](https://docs.orbstack.dev/features/native-files), [Volumes & mounts](https://docs.orbstack.dev/docker/file-sharing) — read: `full` |
| **Population / Scope** | Listing only. Editing, and volume clone, export and import, are documented, not observed; the volume commands were confirmed in `orbctl docker volume --help`. |
| **Licenses** | A statement that host-side file access is a working OrbStack feature. |
| **Does NOT support** | Anything about which UI form it should take elsewhere. |
| **Requirements** | `DOK-5`, `DOK-17` |
| **Open** | None. |

#### F-01-05 · Network and engine behaviour is documented as following the host

| | |
|---|---|
| **Claim** | The docs state that containers follow the host's VPN, DNS and proxy settings (HTTP, HTTPS and SOCKS), that `daemon.json` can be edited for mirrors, insecure registries and address pools, that IPv6 is an option, and that containers have directly reachable IPs. |
| **Bar / Grade** | `market` · **documented** |
| **Sources** | [Network](https://docs.orbstack.dev/docker/network), [Docker containers](https://docs.orbstack.dev/docker/), [Settings](https://docs.orbstack.dev/settings) — read: `full` |
| **Population / Scope** | Docs read 2026-11-05. VPN and proxy behaviour was not observed. |
| **Licenses** | A statement of what OrbStack says it does. |
| **Does NOT support** | That it works without faults: v2.1.3 and v2.2.5-era notes and open issues record proxy bugs (for example issue #2766). |
| **Requirements** | `DOK-12`, `DOK-14` |
| **Open** | None. |

#### F-01-06 · OrbStack imports from Docker Desktop

| | |
|---|---|
| **Claim** | OrbStack states that migration copies containers, images and volumes from Docker Desktop; the CLI has a migrate command, and 2026 release notes still fix migration bugs. |
| **Bar / Grade** | `market` · **documented** |
| **Sources** | [OrbStack 1.1 announcement, 16 Nov 2023](https://orbstack.dev/blog/orbstack-1.1-https) — read: `full`. [Release notes v2.2.0, v2.2.2](https://docs.orbstack.dev/release-notes) — read: `full`. `orbctl migrate --help` on 2.2.5 |
| **Population / Scope** | The command's help text was observed; no migration was run. |
| **Licenses** | A statement that OrbStack treats migration as a maintained feature. |
| **Does NOT support** | How complete or reliable the migration is. |
| **Requirements** | `DOK-13` |
| **Open** | None. |

#### F-01-07 · The menu bar app and dashboard go beyond listing containers

| | |
|---|---|
| **Claim** | The menu bar starts, stops, restarts and deletes containers and projects, opens logs, terminals and services, and copies details. The app added a Builds tab in v2.2.3, and JSON log highlighting and Compose log filtering by service in v2.2.4. |
| **Bar / Grade** | `market` · **documented** |
| **Sources** | [Menu bar](https://docs.orbstack.dev/menu-bar), [Release notes](https://docs.orbstack.dev/release-notes) — read: `full` |
| **Population / Scope** | Docs read 2026-11-05; the UI was not exercised. |
| **Licenses** | A statement of the feature set. |
| **Does NOT support** | That users rely on each item. |
| **Requirements** | `DOK-2` |
| **Open** | None. |

#### F-01-08 · Debug Shell is the only Docker feature on the paid tier

| | |
|---|---|
| **Claim** | Debug Shell opens a toolbox in any container, including distroless and read-only ones, and requires Pro. Every other Docker feature above is available on the free tier, which is limited to personal use. |
| **Bar / Grade** | `market` · **documented**; the Pro gate on the pricing page alone is **claimed** |
| **Sources** | [Debug Shell](https://docs.orbstack.dev/features/debug), [Pricing](https://orbstack.dev/pricing), [Licensing](https://docs.orbstack.dev/licensing) — read: `full`. `orbctl debug --help`: "Pro only" |
| **Population / Scope** | Help text observed on 2.2.5; the shell itself was not run. |
| **Licenses** | A statement that OrbStack charges for this one Docker feature. |
| **Does NOT support** | That it is valuable because it is paid, or that the free tier is free for commercial use. |
| **Requirements** | *(none: not added to the backlog)* |
| **Open** | None. |

#### F-01-09 · Smaller recent Docker features

| | |
|---|---|
| **Claim** | Also documented: CRIU checkpoint and restore (added v2.2.0), an optional containerd image store, emulation of 32-bit Arm and Intel images, SSH agent forwarding, engine exposure over TCP, remote Debug Shell, custom icon labels, one-click Supabase stacks, `orb top`, and organisations with SAML SSO. SSH agent forwarding uses `/run/host-services/ssh-auth.sock`, which the docs say also suits 1Password, where mounting `$SSH_AUTH_SOCK` does not work. Checkpoints rely on Docker's experimental mode. |
| **Bar / Grade** | `market` · **documented** |
| **Sources** | [Docker containers](https://docs.orbstack.dev/docker/), [Settings](https://docs.orbstack.dev/settings), [Labels](https://docs.orbstack.dev/docker/labels), [Release notes](https://docs.orbstack.dev/release-notes), [Command line & CI](https://docs.orbstack.dev/headless), [Licensing](https://docs.orbstack.dev/licensing) — read: `full` |
| **Population / Scope** | Docs read 2026-11-05; none observed. |
| **Licenses** | A statement that they exist. |
| **Does NOT support** | Any claim about use or demand. The docs themselves call unauthenticated TCP exposure "extremely dangerous". |
| **Requirements** | *(none: not added to the backlog)* |
| **Open** | None. |
