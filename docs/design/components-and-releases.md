# Components and releases

Status: accepted by the owner 2026-11-25 ("one binary is wrong", "container native is the way to go", "same applies to the
dockyard cli"). Built for 0.9.0. Since 2026-11-27 (DOK-79) neither app is the core: the CLI and the dashboard are separate apps over the same library, and
either installs and removes the other components without the other (see docs/decisions.md). The components are the CLI, the
tray, the dashboard and the engine.

## Why

Everything Dockyard ships came out of one tag (`v*`) and one 40-minute pipeline: a fix to the dashboard rebuilt the WSL
image, the macOS VM and every installer. One installer program also carried the tray, the `dockyard` command and network
services (DNS, the container-domain proxy) inside its own process.

## Components

| Component | What it is | Project / source | Tag |
|---|---|---|---|
| CLI | The `dockyard` command, a terminal app with the whole setup: installs, updates and removes the others, settings, engines, clusters; the elevated worker on Windows | `src/Dockyard.Cli` over `src/Dockyard.Setup` | `cli/<semver>` |
| Tray | An icon and its menu, no window; runs the background duties (update checks, the certificate) while it runs | `src/Dockyard.Tray` | `tray/<semver>` |
| Dashboard | The only window: containers, images, volumes, networks, builds and clusters, and Setup, Tools and Settings; runs the same setup, also silently (`--setup --silent`) | `src/Dockyard.Dashboard` | `dashboard/<semver>` |
| Engine | The Linux engine image (WSL tarball, macOS VM images) | `image/` | `engine/<semver>` |
| Services | Dockyard's own containers (proxy, DNS, NFS), shown in the dashboard's Dockyard group | `services/` | images pinned by digest in `Dockyard.Setup` |

A component's version is its own. The tag names the component and the version; a release carries only that component's
assets and its own `SHA256SUMS`:

| Release | Assets |
|---|---|
| `cli/<v>` | `dockyard-windows-x64.exe`, `dockyard-linux-x64`, `dockyard-macos-arm64`, `dockyard-host-macos-arm64` (the VM helper, signed with its entitlement, for a Mac without the dashboard); and on Linux `dockyard_<v>_amd64.deb` and `dockyard-<v>-1.x86_64.rpm` (the command and a tray) and `Dockyard-<v>-linux-x64.tar.gz` (for distributions without apt or dnf) |
| `tray/<v>` | `Dockyard.Tray.zip` (the tray and its native libraries) |
| `dashboard/<v>` | `Dockyard.Dashboard.zip`; `Dockyard-<v>-macos.zip` and `Dockyard-<v>-macos.dmg` (the app bundle: tray, dashboard, VM helper and VM files, no command); `dockyard-dashboard-<v>-linux-x64.tar.gz`, `dockyard-dashboard_<v>_amd64.deb` and `dockyard-dashboard-<v>-1.x86_64.rpm` (the dashboard and a tray, no dependency on `dockyard`) |
| `engine/<v>` | `dockyard-linux-<v>-x86_64.tar.gz`, `dockyard-vm-<v>-aarch64.tar.gz` |

Each app carries its own payloads: both Windows publishes pass `-p:GuestBundle` and `-p:HostExe`, so either can set up the
engine without the other. On Windows each component is a file of its own. On Linux each app has a package, a tarball and a
folder of its own (`/usr/lib/dockyard`, `/usr/lib/dockyard-dashboard`), each with its own tray and libraries, sharing no file.
On macOS the app bundle is the dashboard's; the CLI is its own app in `~/.dockyard/cli`, and without a bundle it places the
VM helper and VM files in `~/.dockyard`. The tray has a release of its own only on Windows.

## Finding releases: `components.json`

GitHub's `releases/latest` names one release, and the API allows 60 anonymous calls an hour per address, which an office
shares. So one release with the fixed tag `index` carries a single asset, `components.json`:

```json
{
  "cli":       { "version": "0.9.0", "tag": "cli/0.9.0" },
  "tray":      { "version": "0.9.0", "tag": "tray/0.9.0" },
  "dashboard": { "version": "0.9.0", "tag": "dashboard/0.9.0", "requires": { "cli": "0.9.0" } },
  "engine":    { "version": "0.9.0", "tag": "engine/0.9.0" }
}
```

- Read from `https://github.com/byhofman/dockyard/releases/download/index/components.json`: github.com, not the API.
- Every component workflow publishes its release, then updates its own entry. The workflows share one concurrency group
  for the index, so two releases never overwrite each other's entry.
- `requires` names the lowest version of the CLI this one works with. An update is offered only when its requirements are
  met; otherwise the CLI's own update comes first.
- Without an index (a network that blocks the download) setup says so; `--assets` installs without it.
- The one-liners read the index too: they take the `cli` entry's tag (or `cli/$DOCKYARD_VERSION`), download only that
  release's command for their OS and its `SHA256SUMS`, check it and run `dockyard setup <args>`. They accept only `cli/*` tags.
- The CLI's release is the one marked latest; the other components publish with `--latest=false`. Nothing reads
  `releases/latest`: a development build (0.0.0) takes its Dockyard files from the index's `cli` entry.

## The CLI

- Its own executable, `dockyard` (`dockyard.exe` on Windows), trimmed and self-contained.
- Every command (`setup`, `component`, `settings`, `engine`, `config`, `https`, `update`, `uninstall`, `import`, `cluster`)
  runs in the CLI's own process, over the install library `Dockyard.Setup`. The tray and the dashboard use the same library.
  On Windows the installed CLI is also the elevated worker (`--machine-*`).
- Windows: in `Program Files\Dockyard\bin`, which is on the machine PATH. Linux: `/usr/bin/dockyard`. macOS:
  `~/.dockyard/cli/dockyard`, linked from `/usr/local/bin/dockyard` by setup.
- Its Windows build embeds `dockyard-host.exe` and the guest bundle (`-p:HostExe`, `-p:GuestBundle`, which reach
  `Dockyard.Setup` through the publish); the dashboard's build does the same.

## Pipelines

One workflow, `release.yml`, run by a release tag or by hand; there is none on pull requests or `main`. Its `plan` job reads
the ref and turns on only the jobs a component needs:

| Ref | Builds and tests | Releases |
|---|---|---|
| run by hand (`workflow_dispatch`) | everything | nothing |
| `cli/<version>` | the CLI for Windows, Linux and macOS and its Linux packages and tarball, and the dashboard and tray of this commit for the `setup-*` end-to-end tests that drive `dockyard setup --silent` | the CLI, marked latest |
| `tray/<version>` | the tray for Windows | the tray |
| `dashboard/<version>` | the dashboard for Windows, Linux and macOS (the app bundle) | the dashboard |
| `engine/<version>` | the WSL image and the macOS VM images, with their boot tests | the engine |
| `v<version>` | everything, as for a run by hand | all four at `<version>`, in order: engine, tray, dashboard, CLI |

A single component's release writes `SHA256SUMS`, publishes the release as a draft, checks its assets, makes it public and
updates `components.json`. A `v` tag does the same for each component in turn (`scripts/release-component.sh`): it creates
`<component>/<version>` at the tagged commit with the `v` tag's message as its notes, and uploads only that component's
assets, as `check-release-assets.sh --list` names them. A component already released is skipped, so rerunning a `v` release
that stopped halfway carries on. Use a `v` tag when everything changed together, and a component tag for a fix to one part.

## Not in this step

- Releasing for the first time: `engine/…` first (the macOS app and the end-to-end tests take the latest engine), then
  `tray/…` and `dashboard/…`, then `cli/…`.
- The services move into containers in the next step (proxy, DNS), see `docs/design/dockyard-services.md`.
