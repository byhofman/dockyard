# Dockyard dashboard (sub-project A)

Engine API client and the optional dashboard for containers, compose, images, volumes, networks and disk usage. Shared values and the overall split are in `2026-11-04-dockyard-architecture.md`.

## Scope

In:

- `Dockyard.Engine`: Docker Engine API client library.
- `Dockyard.Dashboard`: separate executable with the dashboard window.
- Tray changes, kept minimal:
  - an **Open dashboard** item (and click action) that starts `Dockyard.Dashboard.exe` when it is installed next to `Dockyard.exe`; without it, click keeps opening the existing window;
  - `--settings` and `--start <linux|win>` arguments, forwarded to the running tray instance (single instance), so the dashboard can open settings and start an engine;
  - move to native AOT.
- Windows only (win-x64), against both the `linux` and `win` contexts.

Out (other sub-projects or later):

- Linux/macOS hosts, tray on other OSes (B), macOS VM (C), installers with the engine-only / full choice (D).
- Tray popover with container quick actions. The tray stays minimal.
- Embedded terminal, compose editor, file browser inside containers, registry login UI (`docker login` on the CLI keeps working, the same config is used).

## Projects

```
src/Dockyard.Engine/              net10.0, no UI or Windows dependency
  Transport/                      SocketsHttpHandler with a ConnectCallback for npipe, unix socket and tcp
  Api/                            Containers, Images, Networks, Volumes, System (df, prune), Events, Logs, Stats, Exec probe
  Models/                         records + one JsonSerializerContext
  DockerContext.cs                reads ~/.docker/config.json and contexts/meta to resolve the active endpoint
src/Dockyard.Dashboard/           net10.0-windows (for now), native AOT, Avalonia
  Store/                          observable per-resource stores fed by list calls + /events
  Features/Containers|Compose|Images|Volumes|Networks|Disk/   one view + view model per feature
  Shell/                          main window, sidebar, top bar, toasts, terminal launcher
src/Dockyard/                     existing tray + setup app; gains "Open dashboard"; moves to native AOT
tests/Dockyard.Engine.Tests/      xUnit: unit (stub handler, loopback listeners) + integration (real engines)
tests/Dockyard.Dashboard.Tests/   xUnit: store, grouping, sampling rules
```

The existing Engines / Updates / Settings window stays in the tray exe, because engine-only installs need it. The dashboard has a gear button in its top bar that starts `Dockyard.exe --settings`, which opens that window in the running tray instance (single-instance forwarding) or starts it.

## Engine client

- One `DockerClient` per endpoint. Methods are async and cancellable. Request/response bodies use the source-generated JSON context; no reflection.
- Engine API version negotiated once per connection via `/_ping` (`API-Version` header), then used as the path prefix.
- Errors map to `DockerException` with the HTTP status and the engine's `message`; `NotFoundException` (404) and `ConflictException` (409) as subclasses.
- Streams:
  - `/events`: `IAsyncEnumerable<DockerEvent>`, newline-delimited JSON.
  - Logs: the 8-byte-header multiplexed stream, demultiplexed into `(stream, line)`; raw stream when the container has a TTY.
  - Stats: `stream=true` JSON stream; CPU% and memory computed the same way the CLI does (`docker stats`).
  - Image pull: progress JSON stream, aggregated per layer.
- Connections: `SocketsHttpHandler` pools connections per endpoint and handles chunked/keep-alive; long-lived streams hold their own connection. No upgrade/hijack is needed because exec runs in an external terminal.

## Dashboard behaviour

### Layout

- Three columns: sidebar, list, detail pane. Below ~1000px window width the detail pane is an overlay over the list.
- The detail pane is resizable with a splitter (and the compose output/logs area inside it); sizes persist in `dashboard.json`. Container logs can take over the whole pane.
- Sidebar: Containers, Images, Volumes, Networks, Disk, each with an icon. Actions use icons (Fluent UI System Icons, Regular, MIT) with a label where space allows and a tooltip otherwise.
- Inputs, combo boxes and buttons share one height (32px), radius and border style, with an accent focus border; light and dark variants.
- Top bar: search (Ctrl+K filters the current list), the L/W context switch (Windows only; switching changes the docker context, the view follows), gear (settings in the tray app).
- Light/dark follows the OS.
- Keyboard: Up/Down move, Enter opens details, `S` start/stop, `R` restart, `E` exec, `L` logs, Del remove (with confirmation).

### Containers

- Grouped by compose project (`com.docker.compose.project` label), standalone containers below.
- Row: status dot, name, image, published ports (click opens `http://127.0.0.1:<port>`), CPU% and memory.
- Stats are only streamed while the window is open. Above 50 running containers, only for rows that are visible.
- Detail tabs:
  - **Logs** (default): follow, search, timestamps toggle, stderr colored, ring buffer of the last 10,000 lines, virtualized.
  - **Inspect**: env, ports, mounts, networks, labels, command, restart policy, with copy buttons.
  - **Stats**: CPU and memory sparklines for the last 60 seconds.
- Actions: start, stop, restart, remove (confirm; force when running), exec.
- **Exec** launches the user's terminal: Windows Terminal (`wt.exe`) when present, otherwise a console window. Linux containers: `docker --context <ctx> exec -it <id> sh -c "command -v bash >/dev/null && exec bash || exec sh"`. Windows containers: `powershell`, falling back to `cmd` when the image has no PowerShell.

### Compose (inside Containers, like Docker Desktop)

- A compose project is an expandable row in the container list (chevron, `running/total`, path); its containers are indented under it. Projects and standalone containers are sorted together by name. Hover actions on the row: start/stop, restart, down.
- Selecting the project row shows the project detail: actions, services, command output and merged logs.

- Projects from container labels, plus a recent list of compose files opened through **Open compose file…** (path + project name, stored in `%APPDATA%\Dockyard\dashboard.json`), so a project shows even when it has no containers.
- Project actions: up (`-d`), stop, restart, down, pull, merged logs. Service actions: start, stop, restart, logs.
- `up`, `pull` and `down` run `docker compose -p <project> -f <file>` (or `--project-directory` from the `com.docker.compose.project.working_dir` label) as a process; output streams into a progress area at the bottom of the detail pane.

### Images, Volumes, Networks, Disk

- **Images**: repository:tag, size, age, in use by. Pull (name input, layer progress), remove (confirm, force when tagged multiple times), prune dangling.
- **Volumes**: name, size (from `system df`), used by. Remove, prune unused.
- **Networks**: name, driver, subnet, connected containers. Create (name, driver), remove, connect/disconnect a container.
- **Disk**: `system df` totals for images, containers, volumes and build cache; prune per category; **Clean everything unused** with a confirmation that lists what will be removed.

### Data flow

1. Window opens: resolve the endpoint from the active context, `/_ping`, one list call per resource, subscribe to `/events`.
2. Each event updates only the affected item (re-inspect that one object); no full reloads.
3. Context switch: cancel all streams, clear the stores, repeat step 1 for the new endpoint.
4. Window closes: the process exits. Nothing of the dashboard stays in memory.

### Errors and empty states

- Engine not reachable: the list area shows the reason and one **Start engine** button (asks the tray via `Dockyard.exe --start <engine>`).
- Failed action: toast with the engine's error message. No silent retries of user actions.
- Event stream drops: reconnect with backoff (0.5s doubling to 10s), full reload on reconnect, small "reconnecting" indicator in the top bar.

## Performance budgets

Acceptance criteria, measured on the development laptop with the Linux engine running:

| Metric | Budget |
|---|---|
| Dashboard window visible after tray click | < 300 ms |
| Container list populated, 200 containers | < 500 ms |
| CPU, dashboard open and idle | < 1% |
| CPU, tray only | 0% (no timers that touch the engine) |
| Private memory, tray | < 60 MB |
| Private memory, dashboard open | < 120 MB |

## Testing

- `Dockyard.Engine.Tests`:
  - Unit: transport against loopback tcp/unix/npipe listeners with canned responses; API calls against a stub `HttpMessageHandler`; log demultiplexing, event parsing, stats calculation, error mapping, context resolution from a temp `~/.docker`.
  - Integration (trait `Engine`, skipped when the endpoint is unreachable): Linux engine at `tcp://127.0.0.1:2375` with `alpine`, Windows engine via npipe with `nanoserver`. Create, start, read logs, stop, remove, and assert the matching events arrive.
- `Dockyard.Dashboard.Tests`: store applies events correctly, compose grouping, stats sampling rule (visible rows above 50).
- AOT gate: CI publishes both executables with `PublishAot=true` and trim/AOT warnings (IL2xxx, IL3xxx) as errors.
- A benchmark script (`scripts/measure.ps1`) measures window-ready time and private memory against the budgets.

## Delivery

- Branch `ui-rework`, small commits per feature.
- Release keeps the `v*` tag flow; the release gets `Dockyard.exe` and `Dockyard.Dashboard.exe`. Until D exists, the installer installs both.
- README: new dashboard section, updated build instructions.
