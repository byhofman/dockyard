# Dockyard

Docker without Docker Desktop, on Windows, macOS and Linux. The engines start only when you need them, and there is one place to see and apply updates.

## Install

Run one line and follow the wizard. It sets up everything, including what Docker needs from your system.

**Windows 11**, in PowerShell:

```pwsh
irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1 | iex
```

**macOS** (Apple silicon, macOS 14 or newer):

```sh
curl -fsSL https://raw.githubusercontent.com/byhofman/dockyard/main/install-mac.sh | sh
```

**Linux** (x86_64):

```sh
curl -fsSL https://raw.githubusercontent.com/byhofman/dockyard/main/install.sh | sh
```

The line only downloads the installer, checks its checksum and starts it. Your system may ask for an administrator's approval during setup, and if Windows has to restart, Dockyard carries on by itself afterwards.

Run the same line again at any time. On an existing install the wizard shows what is installed and what is new, and updates it. Your images, containers and settings stay.

### The dashboard is optional

The wizard asks whether you want the dashboard. Change your mind whenever you like: **Dashboard** in the list of components has **Add** or **Remove**, both in the wizard and in **Engines and settings…** in the tray.

### Without a window

Add arguments to the same line:

```pwsh
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1))) --silent --no-dashboard
```

```sh
curl -fsSL https://raw.githubusercontent.com/byhofman/dockyard/main/install.sh | sh -s -- --silent --no-dashboard
```

| Argument | Meaning |
|---|---|
| `--silent` | No window: install, or update what is installed. Progress goes to the console. Needs no display. |
| `--dashboard` | Add the dashboard, also on an existing install. |
| `--no-dashboard` | Leave the dashboard out, or remove it from an existing install. |
| `--no-start` | Do not start Dockyard afterwards. |
| `--silent --import` | Copy images, volumes and containers from Docker Desktop, which keeps them. **Import** in the tray window does the same. |
| `--silent --uninstall` | Remove Dockyard. Images and containers stay. |
| `--silent --uninstall --remove-data` | Remove Dockyard with its images and containers. |

Without `--dashboard` or `--no-dashboard`, a new install gets the dashboard and an existing install keeps what it has.

The exit code is `0` when done, `1` on failure with the reason on stderr, `2` for wrong arguments, and `3010` when Windows has to restart to finish. A silent install still needs administrator rights at some point: on Windows run it elevated to skip the prompt, on Linux run it as root or with passwordless sudo. On macOS it shows no prompt at all: without passwordless sudo it leaves out the `docker` entry on the PATH and Rosetta, says so, and running the wizard later adds them.

### Without the one-liner

Each release has `Dockyard.exe` for Windows, `Dockyard-<version>-macos.dmg` for macOS and `Dockyard-linux-x64` for Linux. They are the same wizard. On macOS, a browser download needs **System Settings → Privacy & Security → Open Anyway** once, because the app has no Developer ID yet; the one-liner does not.

## What it sets up

**Windows.** WSL if it is missing, the Windows `Containers` feature, Docker's static binaries in `C:\Program Files\Docker` with compose and buildx, the `docker` service started on demand, a small `dockyard` WSL distro for Linux containers, the `linux` and `win` docker contexts, Defender exclusions for Dockyard, mirrored WSL networking unless you chose otherwise in `.wslconfig`, and autostart at logon.

| Context | Engine | Endpoint |
|---|---|---|
| `linux` (default) | Docker in the `dockyard` WSL distro (Alpine with Docker's static binaries) | `tcp://127.0.0.1:2375` |
| `win` | `dockerd.exe` Windows service (ltsc2025 images, process isolation) | `npipe:////./pipe/docker_windows` |

**macOS.** Docker's `docker` CLI with compose and buildx, Rosetta for `linux/amd64` images, a small Linux VM on Apple's Virtualization.framework in `~/.dockyard`, the `dockyard` docker context, `~/Applications/Dockyard.app` and a login item. It makes `/var/run/docker.sock` point at the engine unless another Docker app has it, and `docker login` keeps credentials in the Keychain. In the VM: `/Users`, `/Volumes` and the temp folders are shared at the same paths, so `-v $PWD:/app` works; published ports reach `127.0.0.1`, and so do servers in `--network host` containers; memory the guest does not use goes back to macOS.

**Linux.** Docker CE with buildx and compose from Docker's repository on Debian, Ubuntu, Fedora, RHEL and their derivatives, or from the distribution on Arch and openSUSE; the Dockyard packages, or a copy in `~/.local/lib/dockyard` on distributions without apt or dnf; the engine started on demand; docker access for you right away; the tray with its GNOME extension where needed; autostart at login.

## Using it

Dockyard sits in the tray or menu bar. The icon is the Docker whale: blue when the engine runs, gray when stopped, amber while busy; on Windows a badge shows the active context (`L` or `W`). Click to open the dashboard; right-click for the rest:

- **Linux / Windows** (top right, Windows): switch the docker context. This starts that engine and by default stops the other one.
- **Engines**: status, endpoint and Start/Stop per engine.
- **Components**: installed and latest version of everything, with **Update** per component and **Update all**. Dashboard has **Add** or **Remove**. Checked when the window opens, at most every 6 hours, or on **Check for updates**. Updating an engine keeps its images and containers.
- **Settings**: stop the other engine when switching, start the active engine at logon, uninstall.

### Dashboard

It follows the active docker context and updates live from the engine's events. It polls nothing and exits when you close it.

- **Containers**: running and stopped, with ports (click to open `http://127.0.0.1:<port>`) and live CPU and memory. Compose projects are expandable rows. Per container: logs, inspect, graphs, and **Terminal**. A compose project has up, stop, restart, pull, down and merged logs; **Open compose file…** starts a project from a file.
- **Images**: pull with progress, tags, size, which containers use them, remove, prune.
- **Volumes**: size, which containers use them, remove, prune.
- **Networks**: subnet, connected containers, connect, disconnect, create, remove, prune.
- **Disk**: `docker system df` per category; prune per category or **Clean everything unused**.

Keys: `Ctrl+K` (`⌘K` on macOS) search, `S` start/stop, `R` restart, `E` terminal, `L` logs, `Del` remove (with confirmation), `Left`/`Right` collapse or expand a compose project.

## Your network

Containers reach your machine as `host.docker.internal`, including services that only listen on its loopback. The engine and its containers use the proxy your system is set to, also one on your machine's own loopback, read each time the engine starts. Name lookups go through your system's DNS at that moment, and traffic leaves through your machine's routes, so VPNs apply to containers as they do to your other apps.

## Known limits

- A proxy auto-config (PAC) file is not followed: it can choose a different proxy per address, and the engine takes one fixed proxy. Set a fixed proxy in your system's settings if pulls fail behind one.
- On macOS, `/var/run/docker.sock` stays with another Docker app when one already has it, such as OrbStack or Docker Desktop. Tools that ignore docker contexts keep using that app until it gives up the path.
- On Linux without a desktop session there is no OS credential store, so `docker login` keeps Docker's default there.
- On a device whose Defender settings are managed by an organisation (Intune), local exclusions may not count. If Defender blocks Dockyard (event 1121) or `dockerd.exe` (event 1127), your administrator has to allow `C:\Program Files\Dockyard\` and `C:\Program Files\Docker\dockerd.exe`.

## Build

The .NET SDK has to be 10.0.403 or newer: restore fails on older ones because the NativeAOT compiler they select has a known vulnerability (NU1903).

```pwsh
dotnet test --solution Dockyard.slnx
dotnet publish src/Dockyard/Dockyard.csproj -c Release -o out
dotnet publish src/Dockyard.Dashboard/Dockyard.Dashboard.csproj -c Release -r win-x64 -o out/dashboard
```

The dashboard is a native AOT build and needs the platform's C++ tools. Pushing a `v*` tag builds everything, runs the wizard for real on Windows and Linux, and publishes the release from a draft once all assets are there. `-Build` on `install.ps1` fetches the latest build of `main` (needs the `gh` CLI, signed in).

`tests/e2e` runs the wizard or a published one-liner end to end, `tests/oneliners` checks that the scripts only fetch, check and start the installer, `scripts/mac-e2e.sh` checks the VM on a Mac, and `scripts/measure.ps1` checks the dashboard's budgets. Integration tests (`Category=Engine`) run against real engines and are skipped when none is running.

## License

MIT. The dashboard icons are [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) (MIT, Microsoft). `src/Dockyard/Assets/docker.ico` comes from [docker/cli](https://github.com/docker/cli/blob/master/cmd/docker/internal/assets/docker.ico) (Apache-2.0); the Docker logo is a trademark of Docker, Inc.
