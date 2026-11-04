# Dockyard Dashboard Shell + Containers Implementation Plan (A2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. This plan specifies files, interfaces and test cases per task; the code is written during execution against the real `Dockyard.Engine` API from A1.

**Goal:** A separate `Dockyard.Dashboard.exe` (Avalonia, native AOT) with the three-column shell and a complete Containers section, plus the minimal tray hook that opens it.

**Architecture:** `EngineSession` owns one `DockerClient` for the active context, loads the container list, follows `/events` with backoff reconnect and feeds a `ContainerStore`. View models are plain `INotifyPropertyChanged` classes; all logic that decides something (grouping, event routing, stats selection, exec command lines, log ring buffer) lives in small pure classes with unit tests. The tray and the dashboard talk over a local command pipe (`dockyard-tray`), which works on Windows (named pipe) and on Linux/macOS (.NET maps it to a unix socket), so B can keep it.

**Tech Stack:** .NET 10, Avalonia 12.1.5 (compiled bindings), native AOT, xUnit v3 on Microsoft.Testing.Platform.

Spec: `docs/superpowers/specs/2026-11-04-dockyard-dashboard-design.md` (Layout, Containers, Data flow, Errors, Performance budgets).

## Global Constraints

- `Dockyard.Dashboard` is `net10.0-windows` for now (B makes it cross-platform), `PublishAot=true`, `TreatWarningsAsErrors=true`, compiled bindings only.
- Light/dark follows the OS (`RequestedThemeVariant="Default"`), colours from the existing tray palette with a light variant.
- Nothing polls the engine; stats only stream while the window is open; closing the window exits the process.
- Same comment and commit rules as A1 (no narrating comments, no model attribution).

## File Structure

```
src/Dockyard.Engine/Api/Containers.cs                 + FindContainerAsync(id) (filters by id)
src/Dockyard.Dashboard/Dockyard.Dashboard.csproj
src/Dockyard.Dashboard/Program.cs, App.axaml(.cs)      theme resources (light + dark), styles
src/Dockyard.Dashboard/Shell/MainWindow.axaml(.cs)     sidebar | list | detail, top bar, toasts, keyboard
src/Dockyard.Dashboard/Shell/ShellViewModel.cs         sections, search, context, connection state, toasts
src/Dockyard.Dashboard/Shell/TrayClient.cs             sends commands to the tray pipe
src/Dockyard.Dashboard/Shell/ContextWatcher.cs         watches ~/.docker/config.json for currentContext changes
src/Dockyard.Dashboard/Shell/Observable.cs             INotifyPropertyChanged base + AsyncCommand
src/Dockyard.Dashboard/Store/EngineSession.cs          client, initial load, event loop with backoff
src/Dockyard.Dashboard/Store/EventRouter.cs            DockerEvent -> store action (pure)
src/Dockyard.Dashboard/Store/ContainerStore.cs         keyed container items + grouped rows (pure)
src/Dockyard.Dashboard/Features/Containers/*           list view, detail pane, logs, inspect, stats
src/Dockyard.Dashboard/Features/Containers/LogBuffer.cs ring buffer + filter (pure)
src/Dockyard.Dashboard/Features/Containers/StatsSelection.cs which containers to stream (pure)
src/Dockyard.Dashboard/Features/Containers/ExecCommand.cs terminal + docker exec command line (pure)
src/Dockyard.Dashboard/Controls/Sparkline.cs           60-sample line chart
src/Dockyard/Core/TrayCommands.cs                      pipe server in the tray: show, settings, start, switch
src/Dockyard/App.axaml.cs, Program.cs                  Open dashboard item; forward args to the running tray
tests/Dockyard.Dashboard.Tests/                        unit tests for the pure classes
```

## Tasks

### Task 1: Engine `FindContainerAsync`
- Produces: `Task<ContainerSummary?> FindContainerAsync(string id, CancellationToken ct = default)` → `containers/json?all=1&filters={"id":["<id>"]}`, first match or null.
- Test (Engine.Tests/ContainersTests): path and filter; empty array → null.

### Task 2: Dashboard project, theme and empty shell
- Create the project (Avalonia 12.1.5, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, reference `Dockyard.Engine`), `app.manifest` copied from the tray, icon `docker.ico` + `whale-blue.png` linked from `src/Dockyard/Assets`.
- `MainWindow`: three-column grid (sidebar 180px, list *, detail 420px), top bar with search box, L/W segment (only when contexts `linux` and `win` exist), gear button; below 1000px the detail column collapses and the pane shows as an overlay.
- Test project `tests/Dockyard.Dashboard.Tests` referencing the dashboard; added to `Dockyard.slnx`.
- Verify: `dotnet publish src/Dockyard.Dashboard -c Release -r win-x64` succeeds with 0 warnings (AOT gate), window opens.

### Task 3: ContainerStore + grouping
- `ContainerItem` (observable: Id, Name, Image, State, Status, Ports, Project, Service, CpuPercent, MemoryBytes, IsRunning).
- `ContainerStore.Reset(IEnumerable<ContainerSummary>)`, `Upsert(ContainerSummary)`, `Remove(string id)`, `ApplyUsage(id, ContainerUsage)`, `IReadOnlyList<ContainerRow> Rows` (header rows for compose projects in name order, then "Standalone"), `string Filter` (case-insensitive match on name, image, project), event `RowsChanged`.
- Tests: grouping and order; filter; upsert keeps the same item instance (selection survives); remove; ports text (`8080→80/tcp`).

### Task 4: EventRouter + EngineSession
- `EventRouter.Route(DockerEvent) -> RouteAction` (`None`, `Refresh(id)`, `Remove(id)`, `ReloadAll`): container create/start/restart/die/stop/kill/pause/unpause/rename/health_status/update → Refresh; destroy → Remove; image/volume/network events → `ReloadAll` of that resource kind is A3, here `None`.
- `EngineSession`: `ConnectAsync()` (resolve context, ping, list), `RunEventsAsync(ct)` with coalescing (refreshes per id batched over 100ms) and reconnect backoff 0.5s doubling to 10s, full reload after reconnect; `State` (Connecting, Connected, Reconnecting, Unavailable) and `Error`.
- Tests: routing table; backoff sequence (`Backoff.Next`): 0.5, 1, 2, 4, 8, 10, 10.

### Task 5: Container list view + actions + keyboard
- List with status dot, name, image, ports (click opens `http://127.0.0.1:<port>`), CPU% and memory; header rows non-selectable.
- Actions: start/stop (toggle), restart, remove (flyout confirm; force when running), exec, logs; failures → toast with engine message.
- Keys: Up/Down, Enter (details), `S`, `R`, `E`, `L`, Delete; Ctrl+K focuses search.
- Empty/unavailable state: reason + **Start engine** (sends `start <engine>` to the tray).

### Task 6: ExecCommand + terminal launch
- `ExecCommand.Build(string context, string id, bool windowsContainer, bool hasWindowsTerminal) -> (string File, string[] Args)`:
  - Linux container: `docker --context <ctx> exec -it <id> sh -c "command -v bash >/dev/null && exec bash || exec sh"`.
  - Windows container: `docker --context <ctx> exec -it <id> cmd /c "where powershell >nul 2>&1 && powershell || cmd"`.
  - With Windows Terminal: `wt.exe -w new --title <name> -- docker …`; otherwise `docker …` in a new console window.
- Tests: all four combinations.

### Task 7: Detail pane: Logs
- `LogBuffer(int capacity = 10000)`: `Add(LogLine)`, drops oldest, `Filtered(string search)`, `Count`.
- `LogsViewModel`: follows `LogsAsync(tail 1000, follow)`, appends through a 100ms UI batch, follow toggle (auto-scroll), timestamps toggle, search, stderr coloured; stops when the selection changes or the window closes.
- Tests: ring buffer capacity/drop order; filter case-insensitive.

### Task 8: Detail pane: Inspect + Stats
- Inspect: command, env, ports, mounts, networks, labels, restart policy; copy buttons (clipboard).
- `StatsSelection.Choose(IReadOnlyCollection<string> running, IReadOnlySet<string> visible, int limit = 50)`: all running when ≤ limit, otherwise running ∩ visible.
- `StatsSampler`: keeps one `StatsAsync` stream per chosen container, updates `ContainerStore.ApplyUsage`; visible set from the list's realized containers.
- Stats tab: CPU and memory `Sparkline`s of the last 60 samples.
- Tests: selection rule.

### Task 9: Tray hook
- `TrayCommands` (tray): pipe server `dockyard-tray` (per user), line commands `show`, `settings`, `start <linux|win>`, `switch <linux|win>`; replaces the show event. A second `Dockyard.exe` instance forwards `--settings`, `--start <engine>` or plain show, then exits.
- Tray menu: **Open dashboard** (when `Dockyard.Dashboard.exe` sits next to `Dockyard.exe`); tray click opens the dashboard when present, otherwise the existing window.
- Dashboard: gear → `settings`, L/W switch → `switch <engine>`, Start engine → `start <engine>`; `ContextWatcher` reconnects when `currentContext` changes.
- Test: `TrayCommand.Parse` for valid and invalid lines (in Dashboard.Tests, shared source file linked into both projects: `src/Dockyard.Engine/TrayProtocol.cs` holds the pipe name and parser so both executables use one definition).

### Task 10: Verify budgets by hand
- Publish both executables (`-c Release -r win-x64`), start the tray, click: dashboard visible < 300 ms, list populated; Task Manager private memory dashboard < 120 MB, idle CPU < 1%. Record numbers in the commit message of the last task (A4 adds the scripted measurement).
