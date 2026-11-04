# Dockyard

Docker on Windows without Docker Desktop. Linux and Windows containers behind one `docker` CLI, both engines started only when you need them, and one place to see and apply updates.

One self-contained exe (`Dockyard.exe`, .NET 10 + Avalonia, nothing to install first) is the installer, the tray app and the engine settings. The dashboard for containers, compose, images, volumes, networks and disk usage is a separate native (AOT) program, `Dockyard.Dashboard.exe`, that only runs while its window is open.

| Context | Engine | Endpoint |
|---|---|---|
| `linux` (default) | Docker inside Dockyard's own minimal WSL distro `dockyard` (Alpine with Docker's static binaries) | `tcp://127.0.0.1:2375` |
| `win` | standalone `dockerd.exe` Windows service (ltsc2025 images, process isolation) | `npipe:////./pipe/docker_windows` |

## Requirements

- Windows 11 with WSL 2 (`wsl --install --no-distribution`). Mirrored networking recommended (`[wsl2] networkingMode=mirrored` in `~\.wslconfig`). No Linux distro of your own is needed.
## Install

In PowerShell, preferably elevated (then it also adds the Defender exclusions and there is no administrator prompt):

```pwsh
irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1 | iex
```

It downloads the latest release to `Downloads\Dockyard.exe` and installs it silently. Then sign out and back in once (docker-users group).

With options:

```pwsh
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1))) -Build
```

- `-Build`: the latest build of `main` instead of the latest release (needs the `gh` CLI, signed in)
- `-NoStart`: don't start Dockyard afterwards

Running the one-liner again is safe and updates everything that is out of date.

### Manually

```pwsh
Invoke-WebRequest https://github.com/byhofman/dockyard/releases/latest/download/Dockyard.exe -OutFile "$env:USERPROFILE\Downloads\Dockyard.exe"
$p = Start-Process "$env:USERPROFILE\Downloads\Dockyard.exe" -ArgumentList '--install' -Wait -NoNewWindow -PassThru
$p.ExitCode   # 0 = done, 3010 = reboot to finish enabling Windows containers
```

`Dockyard.exe --install` takes `--image <dockyard-linux-*.tar.gz>` and `--no-start` (without `--image` it uses a `dockyard-linux-*-x86_64.tar.gz` next to the installer or downloads the one from the release); `--uninstall` removes everything again (the `dockyard` distro with your Linux images and containers is kept; `wsl --unregister dockyard` removes it). Running `Dockyard.exe` without arguments opens the same setup as a window.

### What it installs

Administrator step:

- enables the Windows `Containers` feature
- installs the static Docker binaries to `C:\Program Files\Docker` (machine PATH) plus the compose and buildx CLI plugins
- registers the `docker` service: manual start, `docker-users` group on the pipe, start/stop rights for your account
- copies Dockyard to `C:\Program Files\Dockyard\Dockyard.exe` and extracts the dashboard (`Dockyard.Dashboard.zip`, next to the installer or from the release) beside it

User step:

- imports the `dockyard` WSL distro into `%LOCALAPPDATA%\Dockyard\wsl` (Alpine with Docker's static binaries, buildx and compose; the engine listens on `127.0.0.1:2375` and only runs while Dockyard starts it)
- upgrading from an earlier Dockyard that used Docker CE in your own distro: stops and disables that engine so it no longer claims port 2375; its packages, images and containers stay there (copy an image with `wsl -d Ubuntu-24.04 -u root -- docker save <image> | docker --context linux load`)
- creates the `linux` and `win` contexts and pins Testcontainers to the Linux engine (`~\.testcontainers.properties`)
- autostarts Dockyard at logon (HKCU Run)

## Using it

Dockyard sits in the tray. The icon is the Docker whale with a badge for the active context (`L` or `W`); the whale is blue when that engine runs, gray when stopped and amber while busy. Click to open the dashboard; right-click to switch context or open **Engines and settings…**:

- **Linux / Windows** (top right): switch the docker context. This starts that engine and, by default, stops the other one.
- **Engines**: status, endpoint and Start/Stop per engine.
- **Updates**: installed vs latest for the Windows engine and CLI, compose, buildx, the Linux engine in the `dockyard` distro and Dockyard itself, with per-component **Update** and **Update all**. Checked when the window opens (at most every 6 hours) or on **Check for updates**; the Linux engine is only checked automatically while it runs, because checking starts the distro. Updating it replaces Docker's binaries in place and leaves `/var/lib/docker` alone.
- **Settings**: stop the other engine when switching, start the active engine at logon, uninstall.

Settings live in `%APPDATA%\Dockyard\settings.json`. Run `Dockyard.exe --setup` from Program Files to reopen the installer.

The Linux engine runs while Dockyard holds a hidden `wsl.exe` session open. On stop (or quit) dockerd gets SIGTERM and WSL shuts the distro down about a minute later.

### Dashboard

The dashboard follows the active docker context (the same one the CLI uses) and updates live from the engine's event stream; it polls nothing and exits when you close it.

- **Containers**: running and stopped containers with ports (click to open `http://127.0.0.1:<port>`) and live CPU/memory. Compose projects are expandable rows with their containers underneath. Per container: logs (follow, filter, timestamps), inspect (env, ports, mounts, networks, labels) and CPU/memory graphs; **Terminal** opens `docker exec -it` in Windows Terminal. A compose project has up, stop, restart, pull, down and merged logs; **Open compose file…** starts a project from a file and remembers it.
- **Images**: pull with progress, tags, size, which containers use them, remove, prune dangling.
- **Volumes**: size, which containers use them, remove, prune unused.
- **Networks**: subnet, connected containers, connect/disconnect, create, remove, prune.
- **Disk**: `docker system df` per category with what can be reclaimed; prune per category or **Clean everything unused**.

Keys: `Ctrl+K` search, `S` start/stop, `R` restart, `E` terminal, `L` logs, `Del` remove (with confirmation), `Left`/`Right` collapse/expand a compose project. Panel sizes and recent compose files live in `%APPDATA%\Dockyard\dashboard.json`.

## Without Dockyard running

```pwsh
docker context use win      # global, persists
docker context use linux
docker --context win ...    # one command
$env:DOCKER_CONTEXT = "win" # this shell only
```

Start an engine first: `Start-Service docker` or `wsl -d dockyard -u root -- /usr/local/sbin/dockyard-init` (keep that window open). Don't set `DOCKER_HOST`: it overrides the contexts.

## Build

```pwsh
dotnet test --solution Dockyard.slnx
dotnet publish src/Dockyard/Dockyard.csproj -c Release -o out
dotnet publish src/Dockyard.Dashboard/Dockyard.Dashboard.csproj -c Release -r win-x64 -o out/dashboard
```

The first publish produces a trimmed, self-contained single `out/Dockyard.exe` (~40 MB; about 50 MB private memory while idle in the tray). The second is a native AOT build and needs the Visual Studio C++ build tools; CI builds it on `windows-latest` and ships it as `Dockyard.Dashboard.zip` (the exe plus Avalonia's native Skia and HarfBuzz libraries), which the installer extracts next to `Dockyard.exe`. Pushing a `v*` tag builds both and attaches them to a GitHub release.

Integration tests (`Category=Engine`) run against the real Linux (`tcp://127.0.0.1:2375`) and Windows engines and are skipped when an engine is not running. `scripts/measure.ps1` checks the dashboard against its budgets (window < 300 ms, idle CPU < 1 %, < 120 MB private memory; tray < 60 MB).

## Gotchas

- Use `127.0.0.1`, not `localhost`, for published Linux container ports. Windows resolves `localhost` to `::1` first, which mirrored networking doesn't forward to WSL, so the connection hangs.
- Defender ASR's prevalence/age rule (event 1121, "Windows cannot access the specified device, path or file") blocks new, rarely seen binaries. Every build of `Dockyard.exe` is new to it, so exclude the installed copy and wherever you run the installer from, by path (a hash changes with every build). Elevated:

  ```pwsh
  Add-MpPreference -AttackSurfaceReductionOnlyExclusions `
    "$env:USERPROFILE\Downloads\Dockyard.exe",
    "C:\Program Files\Dockyard\Dockyard.exe",
    "C:\Program Files\Dockyard\Dockyard.Dashboard.exe"
  ```

  On an Intune-managed device this only works when local admin merge is allowed; otherwise add the paths to the Intune ASR policy. For the same reason Dockyard only offers Docker, compose and buildx releases that are at least 14 days old.
- Defender Controlled Folder Access blocks `dockerd.exe` from unpacking Windows image layers (`hcsshim::ProcessBaseLayer ... Access is denied`, event 1127). Setup adds a local allow entry; if Intune manages the list and overrides it, add `C:\Program Files\Docker\dockerd.exe` there.
- `docker-users` membership applies after you sign out and back in; until then the `win` context needs an elevated shell.

## License

MIT. `src/Dockyard/Assets/docker.ico` comes from [docker/cli](https://github.com/docker/cli/blob/master/cmd/docker/internal/assets/docker.ico) (Apache-2.0); the Docker logo is a trademark of Docker, Inc.
