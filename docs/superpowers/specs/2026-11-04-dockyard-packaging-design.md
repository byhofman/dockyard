# Dockyard packaging and updates (sub-project D)

Installers, package repositories, self-update and the release pipeline for Windows, Linux and macOS, with the dashboard as an optional add-on everywhere. Shared values are in `2026-11-04-dockyard-architecture.md`; depends on B (`2026-11-04-dockyard-platform-hosts-design.md`) and C (`2026-11-04-dockyard-macos-vm-design.md`).

## Editions

- **Full** (default): engines, CLI, tray, dashboard.
- **Engine-only**: engines, CLI, tray. Chosen with `--engine-only` on any installer, or by installing only the `dockyard` package on Linux.
- The dashboard can be added or removed later: **Settings → Dashboard** on Windows and macOS (`--add-dashboard` / `--remove-dashboard`), the package manager on Linux.
- The tray detects the dashboard by its presence next to the tray executable (Windows, Linux: `/usr/lib/dockyard/`) or inside the app bundle (macOS).

## Release assets

Per `v*` tag:

| Asset | Platform |
|---|---|
| `Dockyard.exe`, `Dockyard.Dashboard.exe` | Windows x64 |
| `dockyard-linux-<version>-x86_64.tar.gz` | WSL distro rootfs |
| `dockyard_<version>_amd64.deb`, `dockyard-dashboard_<version>_amd64.deb` | Debian/Ubuntu |
| `dockyard-<version>.x86_64.rpm`, `dockyard-dashboard-<version>.x86_64.rpm` | Fedora/RHEL |
| `Dockyard-<version>-arm64.zip` (app bundle incl. dashboard, `dockyard-vm`, kernel, root image) | macOS arm64 |
| `Dockyard-<version>-arm64-engine-only.zip` | macOS arm64 without dashboard |
| `SHA256SUMS` | all |

## Windows

- Unchanged entry points: `Dockyard.exe` (self-installing) and `irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1 | iex`.
- `Dockyard.exe --install [--engine-only] [--no-start]`. The installer downloads `Dockyard.Dashboard.exe` (unless engine-only) and the `dockyard-linux` tarball from the same release as itself, verifies them against `SHA256SUMS`, and installs them to `C:\Program Files\Dockyard\`.
- `install.ps1` gains `-EngineOnly`.
- **winget:** package `byhofman.Dockyard`, installer type `exe`, silent switch `--install --no-start`, upgrade behaviour `install`. On each `v*` tag the publish job runs `wingetcreate update` and opens a PR to `microsoft/winget-pkgs` with a PAT stored as the `WINGET_TOKEN` secret.
- Self-update stays in the app (Updates screen → Dockyard), and also updates the dashboard when installed.
- ASR/Defender notes from the README stay.

## Linux

- **Packages** built with nfpm in CI:
  - `dockyard`: `/usr/lib/dockyard/Dockyard`, `/usr/bin/dockyard` symlink, `/usr/share/applications/dockyard.desktop`, icon. No package dependencies; Docker CE is installed by Dockyard's setup (B), because it comes from Docker's own repository. The post-install script prints the next step (`dockyard --install` or start it from the app menu).
  - `dockyard-dashboard`: `/usr/lib/dockyard/Dockyard.Dashboard`; depends on `dockyard` (same version).
- **Repository** on GitHub Pages (`https://byhofman.github.io/dockyard/`), branch `gh-pages`:
  - apt: `deb [signed-by=/etc/apt/keyrings/dockyard.asc] https://byhofman.github.io/dockyard/apt stable main`, metadata from `apt-ftparchive`, `Release` signed (`InRelease` + `Release.gpg`);
  - dnf: `https://byhofman.github.io/dockyard/rpm/`, `createrepo_c` metadata, packages and `repomd.xml` signed;
  - GPG private key and passphrase as the `REPO_GPG_KEY` and `REPO_GPG_PASSPHRASE` secrets; public key published at `/dockyard.asc`.
- **Install script** `install.sh` (`curl -fsSL https://raw.githubusercontent.com/byhofman/dockyard/main/install.sh | sh`): detects apt or dnf, adds the key and repo, installs `dockyard` and (unless `--engine-only`) `dockyard-dashboard`, then runs `dockyard --install`.
- **Updates:** through the package manager. The Updates screen shows the Dockyard component from the repo metadata and updates it with `pkexec apt-get install --only-upgrade dockyard dockyard-dashboard` or `pkexec dnf upgrade dockyard dockyard-dashboard`.

## macOS

Until an Apple Developer ID exists:

- `Dockyard.app` is ad-hoc signed (`codesign --sign -`) with the `com.apple.security.virtualization` entitlement on `dockyard-vm`.
- **Primary install:** `curl -fsSL https://raw.githubusercontent.com/byhofman/dockyard/main/install-mac.sh | sh [-s -- --engine-only]`. It downloads the zip with `curl` (no quarantine attribute, so Gatekeeper does not block it), verifies `SHA256SUMS`, unpacks to `~/Applications/Dockyard.app` and runs `Dockyard --install` (C).
- A manually downloaded zip is quarantined; the README documents **System Settings → Privacy & Security → Open Anyway**.
- No Homebrew cask until notarized (Homebrew is removing casks that fail Gatekeeper).
- **Self-update** in the app: download the zip with `HttpClient` (no quarantine attribute), verify the checksum, replace the bundle from a small updater step after the tray exits, restart.

When a Developer ID exists (switch, no redesign):

- sign with the Developer ID Application certificate and hardened runtime (`APPLE_CERT_P12`, `APPLE_CERT_PASSWORD` secrets);
- notarize and staple with `xcrun notarytool` (`APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD` secrets);
- add a `.dmg`;
- Homebrew (the intended long-term install path on macOS):
  - cask `dockyard` in a new `byhofman/homebrew-tap` repository: `brew install --cask byhofman/tap/dockyard`; the publish job bumps version and checksum on each tag;
  - once the tap is stable and the project meets Homebrew's notability rules, submit the cask to `homebrew/cask` so `brew install --cask dockyard` works without the tap;
  - with a cask installed, Dockyard's self-update is disabled and the Updates screen points to `brew upgrade` (`auto_updates false`).

Homebrew waits for notarization because Homebrew quarantines cask downloads and is removing casks that fail Gatekeeper; an unsigned cask would hit the same Gatekeeper block as a manual download.

## CI

`release.yml` becomes:

| Job | Runner | Produces |
|---|---|---|
| `windows` | `windows-latest` | AOT `Dockyard.exe`, `Dockyard.Dashboard.exe` |
| `linux` | `ubuntu-latest` | AOT binaries, `.deb`, `.rpm` |
| `image` | `ubuntu-latest` (x86_64) and `ubuntu-24.04-arm` (aarch64) | `dockyard-linux` tarballs; aarch64 root image, kernel, initramfs for macOS |
| `macos` | `macos-14` | AOT binaries, `dockyard-vm`, ad-hoc signed bundle, both zips |
| `publish` (tags only) | `ubuntu-latest` | GitHub release + `SHA256SUMS`, `gh-pages` repo update, winget PR |

Pushes to `main` and pull requests run all build jobs and upload artifacts; nothing is published. Creating the tag and enabling Pages stay manual steps for the maintainer.

## Testing

- Package lint: `lintian` for the `.deb`, `rpmlint` for the `.rpm`.
- Install tests in CI containers: `ubuntu:24.04`, `debian:12`, `fedora:41` install from a locally served copy of the repo and check `dockyard --version` and the dashboard dependency.
- Windows: install, `--engine-only`, `--add-dashboard`, upgrade from the previous release, uninstall, on the development laptop.
- macOS: `install-mac.sh` on the user's Mac, including engine-only and self-update from the previous release.
- `winget validate` on the generated manifest before opening the PR.

## Implementation notes

- Release assets as built by CI: `Dockyard.exe`, `Dockyard.Dashboard.zip`, `dockyard-linux-<v>-x86_64.tar.gz` (WSL image), `Dockyard-<v>-linux-x64.tar.gz`, `dockyard_<v>_amd64.deb`, `dockyard-dashboard_<v>_amd64.deb`, `dockyard-<v>-1.x86_64.rpm`, `dockyard-dashboard-<v>-1.x86_64.rpm`, `Dockyard-<v>-macos.zip`, `Dockyard-<v>-macos-engine-only.zip`, `SHA256SUMS`. The aarch64 VM assets are inside the macOS bundle (`Contents/Resources/vm`), not separate assets.
- The rpm repository signs its metadata (`repo_gpgcheck=1`); the rpm files themselves are not signed (`gpgcheck=0`). The apt repository is signed through `InRelease`/`Release.gpg`.
- `pages` keeps every published package under `history/` on `gh-pages` and regenerates the repository from it, so older versions stay installable.
- `winget` tries `wingetcreate update` and falls back to submitting manifests rendered by `packaging/winget/render.ps1` (first release); manifests validate with `winget validate`.
- Linux package lint (`lintian`, `rpmlint`) is not wired in yet; installation is verified instead (signed repository, full and engine-only, in ubuntu:24.04, debian:12 and fedora:41).
