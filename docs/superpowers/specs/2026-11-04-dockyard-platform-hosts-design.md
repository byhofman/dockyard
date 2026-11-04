# Dockyard platform hosts (sub-project B)

Puts every platform behind one host abstraction, replaces the user's WSL distro with a dedicated Dockyard Linux image, adds the Linux host, and builds the tray and dashboard for Windows, Linux and macOS. Shared values are in `2026-11-04-dockyard-architecture.md`; depends on sub-project A (`2026-11-04-dockyard-dashboard-design.md`).

## Scope

In:

- `Dockyard.Host` library with `IEngineHost`, `IElevation` and `ICommandRunner`.
- `WindowsHost`: the existing Windows code moved behind the interface; Linux engine on the dedicated `dockyard` WSL distro.
- `LinuxHost`: system `dockerd`, installed when missing, made on-demand.
- `dockyard-linux` image: Alpine rootfs with Docker's static binaries, built in CI, used by WSL now and by the macOS VM in C.
- Tray and dashboard built for `win-x64`, `linux-x64` and `osx-arm64` (macOS against any existing docker context until C).

Out: macOS host and VM (C), installers/packages and the engine-only / full choice (D), `linux-arm64` and `win-arm64` builds (later).

## Projects

Implementation note: the host abstraction lives in the tray project (`src/Dockyard/Hosts/`) instead of a separate `Dockyard.Host` library. The tray is its only consumer and the Windows update and elevation code it wraps already lives there; `tests/Dockyard.Tests` references the tray directly.

```
src/Dockyard.Host/            net10.0
  IEngineHost.cs              engines, state, start/stop, install/uninstall, update components
  IElevation.cs               UAC (Windows), pkexec (Linux), osascript admin prompt (macOS, C)
  ICommandRunner.cs           process execution; faked in tests
  Windows/                    WindowsHost, WslDistro, WindowsService (existing Engines/MachineSetup/UserSetup code moved here)
  Linux/                      LinuxHost, OsRelease, PackageInstaller (apt, dnf), Polkit
src/Dockyard/                 tray + setup/settings window; net10.0, per-RID native AOT
src/Dockyard.Dashboard/       net10.0, per-RID native AOT
image/                        dockyard-linux build (Dockerfile + dockyard-init + dockyard-update)
tests/Dockyard.Host.Tests/
```

Windows-only APIs (`ServiceController`, named pipe P/Invoke, registry) stay in `Windows/` and are marked `[SupportedOSPlatform("windows")]`; the host is chosen once at startup from `OperatingSystem`.

## Host interface

```csharp
interface IEngineHost
{
    IReadOnlyList<EngineInfo> Engines { get; }          // id ("linux"/"win"), display name, context name, endpoint
    Task<EngineState> GetState(string engineId);        // Stopped, Starting, Running, Stopping, NotInstalled
    Task Start(string engineId);
    Task Stop(string engineId);
    Task UseContext(string engineId);
    Task Install(IProgress<string> log, InstallOptions options);
    Task Uninstall(IProgress<string> log);
    IReadOnlyList<ComponentInfo> Components { get; }    // what the Updates screen lists for this platform
    Task<Version?> Installed(string componentId);
    Task<Release?> Latest(string componentId);
    Task Update(IProgress<string> log, IReadOnlyList<string> componentIds);
    void ReleaseResources();                            // keep-alive sessions etc. on quit
}
```

The tray's view models only talk to `IEngineHost`; nothing in the UI references WSL, services or systemd.

## dockyard-linux image

- Base: Alpine minirootfs (pinned version), plus `iptables`, `ip6tables`, `ca-certificates`, `e2fsprogs`.
- Docker: official static binaries from `download.docker.com/linux/static/stable/<arch>/` (`dockerd`, `containerd`, `containerd-shim-runc-v2`, `runc`, `docker-proxy`, `docker-init`, `docker`), and the buildx and compose plugins from their GitHub releases, in `/usr/local/bin` and `/usr/local/lib/docker/cli-plugins`. The same 14-day minimum release age as the Windows engine.
- `/usr/local/sbin/dockyard-init`: mounts cgroup2 if absent, sets up `/var/run`, starts `dockerd -H unix:///var/run/docker.sock -H tcp://127.0.0.1:2375 --tls=false` in the foreground, and forwards SIGTERM so dockerd shuts down cleanly. No init system.
- `/usr/local/sbin/dockyard-update <tarball> <plugins-dir>`: stops dockerd if running, replaces the binaries, writes the new version, restarts if it was running. `/var/lib/docker` is never touched and the image is never re-imported.
- `/etc/dockyard-release`: image version and Docker version.
- Built in CI as `dockyard-linux-<version>-<arch>.tar.gz` (x86_64 now; aarch64 added with C) and attached to releases.

## WindowsHost

- **Linux engine:** `wsl --import dockyard %LOCALAPPDATA%\Dockyard\wsl <tarball> --version 2`. Start: a hidden `wsl.exe -d dockyard -u root -- /usr/local/sbin/dockyard-init` process held by the tray (same keep-alive model as today); ready when `127.0.0.1:2375` answers `/_ping`. Stop: SIGTERM to dockerd via `wsl -d dockyard -u root -- pkill -TERM dockerd`, wait up to 30s, release the session; WSL shuts the distro down after its idle timeout.
- **Windows engine:** unchanged (`dockerd` service, `docker-users`, on-demand, `npipe:////./pipe/docker_windows`).
- **Contexts:** `linux` → `tcp://127.0.0.1:2375`, `win` → `npipe:////./pipe/docker_windows`, as today.
- **Updates screen components:** Windows engine + CLI, compose, buildx, Linux engine (the distro's Docker, via `dockyard-update`), Dockyard. The separate "Docker CE in WSL" component and the distro selection go away.
- **Migration from the current version (user's distro with Docker CE):**
  - detected by `settings.Distro` being set and `docker.socket` enabled in that distro;
  - Dockyard stops `docker.service`, `docker.socket` and `containerd.service` there and disables `docker.socket`, so nothing else listens on 2375;
  - Docker CE stays installed in that distro and images are not moved; the setup log and the README show how to copy images (`docker --context <old> save … | docker --context linux load`);
  - `settings.Distro` is removed.
- Requirement unchanged: WSL 2; mirrored networking recommended. The "launch the distro once" and systemd requirements go away.

## LinuxHost

- **Detection:** `/etc/os-release` `ID` and `ID_LIKE`. `debian`/`ubuntu` → apt; `fedora`/`rhel`/`centos` → dnf; anything else → no package install, a message to install Docker CE, and management of whatever `docker.service` exists.
- **Install (one `pkexec` prompt, running `Dockyard --machine-install`):**
  - add the download.docker.com repository and key; install `docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin` when `dockerd` is missing;
  - `systemctl disable docker.service containerd.service`, `systemctl enable docker.socket`, so the engine starts on the first docker command;
  - add the user to the `docker` group (applies after sign-out);
  - install `/etc/polkit-1/rules.d/50-dockyard.rules`, allowing members of `docker` to start/stop/restart `docker.service`, `docker.socket` and `containerd.service` without a password.
- **Engine:** one engine, id `linux`, context `default` (`unix:///var/run/docker.sock`). Start: `systemctl start docker`; stop: `systemctl stop docker.service docker.socket containerd.service`; state from `systemctl is-active docker.service`.
- **Updates screen components:** Docker engine, compose, buildx (versions from the packages; update through the package manager in one `pkexec` call), Dockyard.
- **Uninstall:** removes the polkit rule, re-enables the distro default (`docker.service` enabled), leaves packages, images and containers in place.

## Tray

Identical, minimal menu on every OS:

- per engine: status and Start/Stop;
- context switch (Windows only; Linux has one engine);
- **Open dashboard** (only when `Dockyard.Dashboard` is installed);
- Settings…;
- "N updates available" (opens the Updates screen; hidden when none);
- Quit.

Icon states as today: whale blue (running), gray (stopped), amber (busy), with the context badge on Windows.

| | Windows | Linux | macOS |
|---|---|---|---|
| Tray | Shell notification icon | DBus StatusNotifierItem | NSStatusItem |
| No tray host | n/a | open the window instead, explain the AppIndicator extension for GNOME | n/a |
| Autostart | `HKCU\...\Run` | `~/.config/autostart/dockyard.desktop` | LaunchAgent (C) |
| Settings | `%APPDATA%\Dockyard` | `~/.config/dockyard` | `~/Library/Application Support/Dockyard` |
| Elevation | UAC (`runas`) | `pkexec` | `osascript` admin prompt (C) |

Single instance per user on every OS (named mutex on Windows, a lock file + unix socket in the settings dir elsewhere); `--settings` and `--start <engine>` are forwarded to the running instance.

## Testing

- **Unit (`Dockyard.Host.Tests`):** a fake `ICommandRunner` asserts the exact command sequences for WSL import/start/stop/migration, systemctl, apt and dnf repository setup, polkit rule content; `/etc/os-release` parsing for Ubuntu, Debian, Fedora, RHEL, Arch (unsupported path).
- **Integration, Windows (this laptop):** import the `dockyard` distro from a CI-built tarball, start, `docker run hello-world`, stop, unregister.
- **Integration, Linux:** a disposable WSL Ubuntu 24.04 distro with systemd, and the CI `ubuntu-latest` runner: install, socket activation, start/stop through polkit as a non-root user in `docker`.
- **Image:** CI builds the rootfs, imports it into a privileged container, runs `dockyard-init`, `docker run hello-world`, sends SIGTERM and checks a clean exit; `dockyard-update` is tested against the previous Docker version.
- **AOT:** all three RIDs published in CI with trim/AOT warnings as errors.

## Delivery

- Branch per sub-project after A lands; small commits.
- Release assets gain `dockyard-linux-<version>-x86_64.tar.gz` and Linux/macOS builds of the tray and dashboard (plain archives until D adds packages).
- README: platform sections for Windows and Linux, migration notes.
