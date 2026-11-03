# Dockyard

Docker on Windows without Docker Desktop. Linux and Windows containers behind one `docker` CLI, both engines started only when you need them, and one place to see and apply updates.

One self-contained exe (`Dockyard.exe`, .NET 10 + Avalonia, nothing to install first) is the installer, the tray app and the dashboard.

| Context | Engine | Endpoint |
|---|---|---|
| `linux` (default) | Docker CE inside a WSL distro (default `Ubuntu-24.04`) | `tcp://127.0.0.1:2375` |
| `win` | standalone `dockerd.exe` Windows service (ltsc2025 images, process isolation) | `npipe:////./pipe/docker_windows` |

## Requirements

- Windows 11 with WSL 2. Mirrored networking recommended (`[wsl2] networkingMode=mirrored` in `~\.wslconfig`).
- A WSL distro with systemd, launched once so its user exists (`winget install Canonical.Ubuntu.2404`).
- `gh` logged in, for Dockyard's own update check (the repo is private).

## Install

Download `Dockyard.exe` from the latest release and run it:

```pwsh
gh release download -R byhofman/dockyard -p Dockyard.exe -D "$env:USERPROFILE\Downloads" --clobber
& "$env:USERPROFILE\Downloads\Dockyard.exe"
```

Pick the WSL distro, click **Install / Update** and approve the administrator prompt.

Administrator step:

- enables the Windows `Containers` feature
- installs the static Docker binaries to `C:\Program Files\Docker` (machine PATH) plus the compose and buildx CLI plugins
- registers the `docker` service: manual start, `docker-users` group on the pipe, start/stop rights for your account
- copies Dockyard to `C:\Program Files\Dockyard\Dockyard.exe`

User step:

- installs Docker CE in the WSL distro (download.docker.com, listening on `127.0.0.1:2375`, not autostarted)
- creates the `linux` and `win` contexts and pins Testcontainers to the Linux engine (`~\.testcontainers.properties`)
- autostarts Dockyard at logon (HKCU Run)

## Using it

Dockyard sits in the tray. The icon is the Docker whale with a badge for the active context (`L` or `W`); the whale is blue when that engine runs, gray when stopped and amber while busy. Right-click to switch context; click to open the dashboard:

- **Linux / Windows** (top right): switch the docker context. This starts that engine and, by default, stops the other one.
- **Engines**: status, endpoint and Start/Stop per engine.
- **Updates**: installed vs latest for the Windows engine and CLI, compose, buildx, Docker CE in WSL and Dockyard itself, with per-component **Update** and **Update all**. Checked when the dashboard opens (at most every 6 hours) or on **Check for updates**; Docker CE in WSL is only checked automatically while the Linux engine runs, because checking starts the distro.
- **Settings**: stop the other engine when switching, start the active engine at logon, uninstall.

Settings live in `%APPDATA%\Dockyard\settings.json`. Run `Dockyard.exe --setup` from Program Files to reopen the installer.

The Linux engine runs while Dockyard holds a hidden `wsl.exe` session open. On stop (or quit) WSL shuts the distro down about a minute later, unless another WSL session is open. Inside WSL, `docker.socket` still starts the engine on first use.

## Without Dockyard running

```pwsh
docker context use win      # global, persists
docker context use linux
docker --context win ...    # one command
$env:DOCKER_CONTEXT = "win" # this shell only
```

Start an engine first: `Start-Service docker` or `wsl -d Ubuntu-24.04 -u root -- systemctl start docker`. Don't set `DOCKER_HOST`: it overrides the contexts.

## Build

```pwsh
dotnet publish src/Dockyard/Dockyard.csproj -c Release -o out
```

Produces a trimmed, self-contained single `out/Dockyard.exe` (~38 MB; about 50 MB private memory while idle in the tray). Pushing a `v*` tag builds it and attaches it to a GitHub release.

## Gotchas

- Use `127.0.0.1`, not `localhost`, for published Linux container ports. Windows resolves `localhost` to `::1` first, which mirrored networking doesn't forward to WSL, so the connection hangs.
- Defender ASR's prevalence/age rule (event 1121, "Windows cannot access the specified device, path or file") blocks new, rarely seen binaries. Every build of `Dockyard.exe` is new to it, so exclude the installed copy and wherever you run the installer from, by path (a hash changes with every build). Elevated:

  ```pwsh
  Add-MpPreference -AttackSurfaceReductionOnlyExclusions `
    "$env:USERPROFILE\Downloads\Dockyard.exe",
    "C:\Program Files\Dockyard\Dockyard.exe"
  ```

  On an Intune-managed device this only works when local admin merge is allowed; otherwise add the paths to the Intune ASR policy. For the same reason Dockyard only offers Docker, compose and buildx releases that are at least 14 days old.
- Defender Controlled Folder Access blocks `dockerd.exe` from unpacking Windows image layers (`hcsshim::ProcessBaseLayer ... Access is denied`, event 1127). Setup adds a local allow entry; if Intune manages the list and overrides it, add `C:\Program Files\Docker\dockerd.exe` there.
- `docker-users` membership applies after you sign out and back in; until then the `win` context needs an elevated shell.

## License

MIT. `src/Dockyard/Assets/docker.ico` comes from [docker/cli](https://github.com/docker/cli/blob/master/cmd/docker/internal/assets/docker.ico) (Apache-2.0); the Docker logo is a trademark of Docker, Inc.
