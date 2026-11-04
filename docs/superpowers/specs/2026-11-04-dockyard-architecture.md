# Dockyard architecture

Umbrella design for turning Dockyard from a Windows-only engine switcher into a cross-platform Docker distribution with an optional dashboard. Each sub-project below gets its own spec, plan and implementation cycle; this document holds what they share.

## Core values

1. **Fast and lean.** Nothing runs when nothing is in use: engines start on demand, the dashboard process only exists while its window is open, nothing polls while idle.
2. **Cross-platform.** Windows, Linux and macOS, with a tray on all three.
3. **Native Docker only.** Plain `dockerd`, the normal `docker` CLI with buildx and compose, standard docker contexts. No Kubernetes, no custom networking layers, no wrapper CLI.
4. **The dashboard is optional, and fast.** Engine-only installs get the engines, the CLI and the tray. The dashboard (containers, images, compose, volumes, networks) is an extra component. Simplicity and usability come before feature count.
5. **Windows runs both Linux and Windows containers.**

## Components

| Component | Delivered in | Purpose |
|---|---|---|
| Engines + CLI | engine-only and full | `dockerd` per platform, `docker` CLI, buildx and compose plugins, docker contexts |
| `Dockyard` (tray) | engine-only and full | Minimal: engine status, context switch, start/stop engines, update notice, settings and setup window, open dashboard (only when installed), quit |
| `Dockyard.Dashboard` | full only | The dashboard window. Separate executable, started by the tray on demand, exits when its window closes |
| `Dockyard.Engine` | library | Docker Engine API client used by the dashboard (and by the tray for engine status where useful) |

The tray stays small because it ships in every install: no container lists, no logs, no engine API streams. Everything container-related lives in the dashboard.

## Sub-projects

| # | Sub-project | Depends on | Contents |
|---|---|---|---|
| A | Engine client + dashboard | none | `Dockyard.Engine` and `Dockyard.Dashboard`: containers, compose, images, volumes, networks, disk. Windows first. Spec: `2026-11-04-dockyard-dashboard-design.md` |
| B | Platform hosts | A | `IEngineHost` abstraction; current Windows host (WSL Docker CE + `dockerd` service) moved behind it; Linux host on the system `dockerd`; tray on Windows, Linux and macOS; dashboard and tray built for all three |
| C | macOS VM | B | Own Swift helper on Apple Virtualization.framework: minimal Linux VM, virtiofs file sharing, docker socket over vsock to `~/.dockyard/docker.sock`, elastic memory, Rosetta for amd64, automatic localhost port forwarding from container events. Modelled on OrbStack's approach |
| D | Packaging + updates | B, C | Per-OS installers with an engine-only / full choice (exe, .deb/.rpm, signed and notarized .dmg), self-update, CI matrix (`win-x64`, `linux-x64`, `osx-arm64`) |

## Shared decisions

- **Stack:** .NET 10 with native AOT, Avalonia for all UI. No reflection-based serialization; JSON through source-generated contexts.
- **Engine access:** Dockyard's own HTTP/1.1 client over named pipe, unix socket or TCP. Compose has no engine API and goes through the `docker compose` CLI plugin.
- **Endpoint:** always the active docker context from `~/.docker`, exactly what the CLI uses. Dockyard never sets `DOCKER_HOST`.
- **Per platform, one engine endpoint per engine:**
  - Windows: `linux` context (Docker CE in WSL, `tcp://127.0.0.1:2375`) and `win` context (`dockerd` service, `npipe:////./pipe/docker_windows`).
  - Linux: the system `dockerd` (`unix:///var/run/docker.sock`).
  - macOS: the VM's forwarded socket (`unix://~/.dockyard/docker.sock`).
- **Exec:** opens the user's own terminal with `docker exec -it`; no embedded terminal.
