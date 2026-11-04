# Dockyard Packaging and Updates Implementation Plan (D)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans.

**Goal:** Engine-only and full installs on every OS, winget, a signed apt/dnf repository on GitHub Pages, a macOS app bundle installed by script, self-update, and a release pipeline that publishes all of it.

Spec: `docs/superpowers/specs/2026-11-04-dockyard-packaging-design.md`.

## Constraints

- Secrets the maintainer adds once: `REPO_GPG_KEY`, `REPO_GPG_PASSPHRASE` (Linux repository), `WINGET_TOKEN` (winget PR). Jobs that need a secret are skipped when it is missing, so forks and pull requests still build.
- Creating tags, enabling GitHub Pages and the first winget submission stay manual steps for the maintainer.

## Tasks

- D1 Editions in the tray: `--engine-only` on install, `--add-dashboard` / `--remove-dashboard`, and a Dashboard row in Settings; Windows machine step skips or removes the dashboard; macOS bundle variants carry or omit it; Linux uses the separate package.
- D2 Windows: `install.ps1 -EngineOnly`; winget manifests (`packaging/winget`) generated per release with `wingetcreate` in the publish job.
- D3 Linux: nfpm configs for `dockyard` and `dockyard-dashboard` (.deb/.rpm), `packaging/linux/build-repo.sh` (apt-ftparchive + createrepo_c, GPG-signed), `install.sh`; CI installs the packages from a locally served repo in `ubuntu:24.04`, `debian:12` and `fedora:41`, and lints them.
- D4 macOS: `Dockyard.app` (Info.plist with `LSUIElement`, helper signed with the virtualization entitlement, whole bundle ad-hoc signed), full and engine-only zips, `install-mac.sh` (curl → no quarantine → `~/Applications`), in-app self-update by swapping the bundle after exit.
- D5 Release pipeline: release assets + `SHA256SUMS`, `gh-pages` repository update, winget PR, all on `v*` tags.
