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
- Without an index (a network that blocks the download) the Toolbox says so; `--assets` installs without it.
- The Toolbox's release is the one marked latest; the other components publish with `--latest=false`. So
  `releases/latest` stays the Toolbox's, which is what the one-liners look at. They accept only `toolbox/*` tags.

## The CLI

- Its own executable, `dockyard` (`dockyard.exe` on Windows), trimmed and self-contained.
- `cluster …` runs in the CLI: it needs kind, the docker CLI and the kubeconfig, which it reads itself.
- Every other command (`setup`, `component`, `settings`, `engine`, `config`, `https`, `update`, `uninstall`, `import`)
  calls the install library, `Dockyard.Setup`, in the CLI's own process. It is also the elevated worker on Windows.
- Windows: in `Program Files\Dockyard\bin`, which is on the machine PATH. Linux: `/usr/bin/dockyard`. macOS: inside the app,
  linked from `/usr/local/bin/dockyard` by the Toolbox's setup.

## Pipelines

One workflow, `release.yml`. Its `plan` job reads the ref and turns on only the jobs a component needs:

| Ref | Builds and tests | Releases |
|---|---|---|
| pull request, `main` | everything | nothing |
| `toolbox/<version>` | the Toolbox for Windows, Linux (tarball, deb, rpm) and macOS, with the wizard end-to-end tests | the Toolbox, marked latest |
| `dashboard/<version>` | the dashboard for Windows, Linux and macOS | the dashboard |
| `cli/<version>` | the CLI for Windows, Linux and macOS | the CLI |
| `engine/<version>` | the WSL image and the macOS VM images, with their boot tests | the engine |
| `v<version>` | everything, as for a pull request | all four at `<version>`, in order: engine, dashboard, CLI, Toolbox |

A single component's release writes `SHA256SUMS`, publishes the release as a draft, checks its assets, makes it public and
updates `components.json`. A `v` tag does the same for each component in turn (`scripts/release-component.sh`): it creates
`<component>/<version>` at the tagged commit with the `v` tag's message as its notes, and uploads only that component's
assets, as `check-release-assets.sh --list` names them. A component already released is skipped, so rerunning a `v` release
that stopped halfway carries on. Use a `v` tag when everything changed together, and a component tag for a fix to one part.

## Not in this step

- On macOS the dashboard and the CLI still ship inside the Toolbox's app bundle (the CLI as `dockyard-cli`, since the
  default file system cannot tell `dockyard` from `Dockyard`), and the Linux Toolbox package and tarball carry the CLI as
  well. Downloading them on their own there needs the app bundle and the packages split, which is its own piece of work.
  On Windows the CLI and the dashboard come from their own releases.
- Releasing for the first time: `engine/…` first (the Toolbox's macOS app and its end-to-end tests take the latest
  engine), then `dashboard/…` and `cli/…`, then `toolbox/…`.
- The services move into containers in the next step (proxy, DNS), see `docs/design/dockyard-services.md`.
