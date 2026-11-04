# Dockyard Platform Hosts Implementation Plan (B)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. This plan specifies files, interfaces and verification per task.

**Goal:** One host abstraction for Windows, Linux and macOS; the Windows Linux engine moves to the dedicated Alpine `dockyard` WSL distro; a Linux host; tray and dashboard build for `win-x64`, `linux-x64` and `osx-arm64`.

Spec: `docs/superpowers/specs/2026-11-04-dockyard-platform-hosts-design.md`.

## Global Constraints

- Never touch the user's running engines while testing: WSL tests use a throwaway distro (`dockyard-test`) on port 23750; the existing `linux` context (Ubuntu, 2375) and `win` service stay as they are.
- Pinned component versions live in `image/versions.env` and follow the 14-day minimum release age.
- Same comment/commit rules as before.

## Tasks

### B1: `dockyard-linux` image
- `image/versions.env` (ALPINE, DOCKER, BUILDX, COMPOSE), `image/Dockerfile` (Alpine + `iptables ip6tables ca-certificates e2fsprogs` + Docker static binaries + buildx/compose plugins + `/etc/wsl.conf` + `docker` group), `image/rootfs/usr/local/sbin/dockyard-init`, `image/rootfs/usr/local/sbin/dockyard-update`, `image/build.ps1` → `out/dockyard-linux-<version>-<arch>.tar.gz` via `docker build --output type=tar`.
- `dockyard-init`: cgroup2 if missing, clean stale socket, `exec dockerd -H unix:///var/run/docker.sock -H tcp://${DOCKYARD_TCP:-127.0.0.1:2375} --tls=false`.
- `dockyard-update <docker.tgz> [plugins-dir]`: refuses while dockerd runs, replaces `/usr/local/bin` docker binaries and plugins, updates `DOCKER_VERSION` in `/etc/dockyard-release`.
- Verify: `image/test.ps1` imports the tarball as an image, runs `dockyard-init` privileged, `docker run --rm alpine echo ok` inside, SIGTERM exits cleanly; then the same rootfs as WSL distro `dockyard-test` on port 23750 from Windows.

### B2: `Dockyard.Host` + WindowsHost on the dedicated distro
- `src/Dockyard.Host` (net10.0): `ICommandRunner`/`ProcessRunner` (from `Shell`), `IEngineHost` (engines, running state, start/stop, active engine, use context, components installed/latest, release resources), `EngineInfo`, `ComponentInfo`, `Release`.
- `Windows/WindowsHost`: `linux` engine = `dockyard` distro kept alive by `wsl -d dockyard -u root -- /usr/local/sbin/dockyard-init`, stop = `pkill -TERM dockerd` + wait + release; `win` engine unchanged; components Engine, Compose, Buildx, LinuxEngine (`dockyard-update` with the static tarball + plugins), App.
- `Windows/WslDistro`: import from tarball into `%LOCALAPPDATA%\Dockyard\wsl`, exists/version/run as root.
- Migration (`UserSetup.Install`): stop and disable Docker in the old `settings.Distro` (`systemctl stop docker.service docker.socket containerd.service; systemctl disable docker.socket`), keep packages and images, log the `docker save | docker load` hint, drop `Distro` from settings; setup UI no longer asks for a distro; the installer downloads `dockyard-linux-<version>-x86_64.tar.gz` from the release (or uses one next to the installer).
- Tray uses `IEngineHost` for engine cards, start/stop, switching and the component list.
- Tests (`tests/Dockyard.Host.Tests`, fake runner): WSL start/stop/update/migration command sequences, import command, release version parsing.

### B3: LinuxHost
- `Linux/OsRelease` parsing (`ID`, `ID_LIKE`, `VERSION_CODENAME`), `Linux/LinuxHost` (engine `linux`, context `default`, `systemctl start/stop`, `is-active`), embedded `setup-linux.sh` run through `pkexec` (Docker repo + packages for apt/dnf, `docker.service` disabled + `docker.socket` enabled, user in `docker`, polkit rule `/etc/polkit-1/rules.d/50-dockyard.rules`), uninstall script.
- Tests: os-release parsing (Ubuntu, Debian, Fedora, RHEL, Arch), polkit rule text; `setup-linux.sh --packages-only` run in `ubuntu:24.04`, `debian:12`, `fedora:41` containers installs `dockerd`, `docker buildx version`, `docker compose version`.

### B4: Cross-platform builds and tray
- Tray and dashboard `net10.0` (Windows code `[SupportedOSPlatform("windows")]` and guarded), RID per platform; `MacHost` placeholder (uses the active context, no engine management) until C.
- Settings directory per OS, autostart (`HKCU Run` / `~/.config/autostart/dockyard.desktop` / LaunchAgent in C), single instance via the existing pipe protocol (works on all three).
- Linux tray via DBus StatusNotifierItem; without a tray host Dockyard opens its window.
- CI matrix: `windows-latest` (tests, tray, dashboard AOT), `ubuntu-latest` (tests, linux-x64 tray + dashboard AOT, image tarball), `macos-14` (osx-arm64 builds).
- Verify locally: linux-x64 AOT builds in the SDK container with `-warnaserror`; the dashboard runs under WSLg against a Linux engine.
