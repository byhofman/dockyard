# Components and releases

Status: accepted by the owner 2026-11-25 ("one binary is wrong", "container native is the way to go", "same applies to the
dockyard cli"). Built for 0.9.0.

## Why

Everything Dockyard ships came out of one tag (`v*`) and one 40-minute pipeline: a fix to the dashboard rebuilt the WSL
image, the macOS VM and every installer. The Toolbox (the installer and manager with the tray icon) also carried things that
are not the Toolbox's: the `dockyard` command, and network services (DNS, the container-domain proxy) running inside its
own process.

## Components

| Component | What it is | Project / source | Tag |
|---|---|---|---|
| Toolbox | Installs, updates and removes the other components; the tray icon; owns the engines (WSL distro, macOS VM, Linux service) | `src/Dockyard` | `toolbox/<semver>` |
| Dashboard | The GUI for containers, images, volumes, networks, builds and clusters | `src/Dockyard.Dashboard` | `dashboard/<semver>` |
| CLI | The `dockyard` command: clusters itself, engines/config/https/update through the Toolbox | `src/Dockyard.Cli` | `cli/<semver>` |
| Engine | The Linux engine image (WSL tarball, macOS VM images) | `image/` | `engine/<semver>` |
| Services | Dockyard's own containers (proxy, DNS, NFS), shown in the dashboard's Dockyard group | `services/` | images pinned by digest in the Toolbox |

A component's version is its own. The tag names the component and the version; a release carries only that component's
assets and its own `SHA256SUMS`.

## Finding releases: `components.json`

GitHub's `releases/latest` names one release, and the API allows 60 anonymous calls an hour per address, which an office
shares. So one release with the fixed tag `index` carries a single asset, `components.json`:

```json
{
  "toolbox":   { "version": "0.9.0", "tag": "toolbox/0.9.0" },
  "dashboard": { "version": "0.9.0", "tag": "dashboard/0.9.0", "requires": { "toolbox": "0.9.0" } },
  "cli":       { "version": "0.9.0", "tag": "cli/0.9.0" },
  "engine":    { "version": "0.9.0", "tag": "engine/0.9.0" }
}
```

- Read from `https://github.com/byhofman/dockyard/releases/download/index/components.json`: github.com, not the API.
- Every component workflow publishes its release, then updates its own entry. The workflows share one concurrency group
  for the index, so two releases never overwrite each other's entry.
- `requires` names the lowest version of another component this one works with. The Toolbox offers an update only when its
  requirements are met, and says which component to update first otherwise.
- Without an index (an old release, or a network that blocks the download) the Toolbox falls back to `releases/latest`.

## Staying compatible with what is installed

- The Toolbox's release is the one marked latest; the other components publish with `--latest=false`. So
  `releases/latest` stays the Toolbox's, which is what installed 0.8.x Toolboxes and the one-liners look at.
- A 0.8.x Toolbox updates its dashboard from the same release as itself. Until no 0.8.x is left, the Toolbox's release also
  carries the dashboard assets of the version `components.json` names at that moment.
- The one-liners take the latest release's Toolbox and accept both `v*` and `toolbox/*` tags.

## The CLI

- Its own executable, `dockyard` (`dockyard.exe` on Windows), trimmed and self-contained.
- `cluster …` runs in the CLI: it needs kind, the docker CLI and the kubeconfig, which it reads itself.
- `engine …`, `config …`, `https …` and `update …` act on what the Toolbox owns, so they go to the Toolbox over its local
  pipe. When the Toolbox is not running, the CLI runs the Toolbox for that one command and passes its output and exit code on.
- Without a command, or with the installer's options (`--silent`, `--with`, …), it hands over to the Toolbox, so
  `dockyard` keeps opening the Toolbox and `dockyard --silent …` keeps installing.
- Windows: in `Program Files\Dockyard\bin`, which is on the machine PATH. Linux: `/usr/bin/dockyard`. macOS: inside the app,
  linked from `/usr/local/bin/dockyard` by the Toolbox's setup.

## Pipelines

| Workflow | Runs on | Does |
|---|---|---|
| `ci.yml` | pull requests, main | builds and tests everything, as before, without publishing |
| `release-toolbox.yml` | `toolbox/*` | Toolbox for Windows, Linux (tarball, deb, rpm) and macOS; the wizard end-to-end tests; marks the release latest |
| `release-dashboard.yml` | `dashboard/*` | the dashboard for Windows, Linux and macOS |
| `release-cli.yml` | `cli/*` | the CLI for Windows, Linux and macOS |
| `release-engine.yml` | `engine/*` | the WSL image and the macOS VM images, with their boot tests |

Each release workflow ends in the same reusable step: write `SHA256SUMS`, publish the release as a draft, check its assets,
make it public, and update `components.json`.

## Not in this step

- On macOS the dashboard and the CLI still ship inside the Toolbox's app bundle (the CLI as `dockyard-cli`, since the
  default file system cannot tell `dockyard` from `Dockyard`), and the Linux Toolbox package and tarball carry the CLI as
  well. Downloading them on their own there needs the app bundle and the packages split, which is its own piece of work.
  On Windows the CLI and the dashboard come from their own releases.
- Releasing for the first time: `engine/…` first (the Toolbox's macOS app and its end-to-end tests take the latest
  engine), then `dashboard/…` and `cli/…`, then `toolbox/…`, which carries the dashboard's assets for 0.8.x.
- The services move into containers in the next step (proxy, DNS), see `docs/design/dockyard-services.md`.
