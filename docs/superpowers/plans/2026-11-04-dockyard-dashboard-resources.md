# Dockyard Dashboard Resources Implementation Plan (A3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. This plan specifies files, interfaces and test cases; code is written against the A1/A2 code base.

**Goal:** Compose, Images, Volumes, Networks and Disk sections in the dashboard, completing the spec's feature set.

**Architecture:** Every section is a view model implementing `ISearchable`, fed by `EngineSession` (`Connected` → full load, `ResourceChanged` → debounced reload of that resource; container changes recompute derived data such as "in use" and compose projects). Decisions live in pure classes with tests: `ComposeCatalog` (projects from labels + recent files), `ComposeCli` (argument lists), `ImageUsage`, `DiskSummary`, `NetworkDrivers`. Compose commands run the `docker compose` CLI and stream output into the detail pane.

Spec: `docs/superpowers/specs/2026-11-04-dockyard-dashboard-design.md` (Compose; Images, Volumes, Networks, Disk).

## Global Constraints
Same as A2. Destructive actions (remove, prune, down, clean everything) always confirm inline first and name what will be removed.

## Tasks

### Task 1: Engine — mounts in container summaries
- `ContainerSummary.Mounts : List<MountPoint>?` (already returned by `containers/json`). Test: parsed from list JSON.

### Task 2: Compose logic (pure)
- `ComposeCatalog.Build(IEnumerable<ContainerItem>, IEnumerable<RecentCompose>) -> List<ComposeProject>`; project from `com.docker.compose.project`, working dir from `…project.working_dir`, config files from `…project.config_files` (comma-separated), services from `…service`; recents without containers appear with 0/0; a recent matches a project by config file path (case-insensitive on Windows) or project name.
- `ComposeCli.Args(string context, ComposeProject p, params string[] verb) -> string[]`: `--context <ctx> compose -p <name> [-f <file>]… [--project-directory <dir>] <verb>`; `up`/`pull` require config files (`CanUp`).
- `ComposeCli.DefaultProjectName(string composeFile)`: directory name lowercased, only `[a-z0-9_-]`.
- `DashboardSettings` (`RecentCompose { Path, Project }`, max 10, newest first) stored in `<ApplicationData>/Dockyard/dashboard.json` via source-generated JSON.
- Tests: grouping, recents merge, args for up/down with and without files, project name normalisation, recents ordering/dedupe/cap.

### Task 3: Compose section
- List: project name, `running/total`, path; detail: services (state, start/stop/restart via engine API on the service's containers), project actions Up, Stop, Restart, Down (confirm), Pull, merged logs (prefixed with service name), output area streaming the CLI.
- **Open compose file…** (file picker) → `up -d`, then added to recents.

### Task 4: Images section
- `ImageRow` (repository:tag or `<none>`, short id, size, created age, in use by N containers); untagged images listed once per id; multiple tags listed per tag.
- Detail: tags, id, size, created, containers using it; Remove (confirm; force when tagged more than once or used by stopped containers).
- Pull: text box + Pull button, progress bar from `PullProgress`, errors as toast.
- Prune dangling (confirm).
- Test `ImageUsage.Rows(images, containers)`: tag expansion, `<none>`, in-use counting by ImageID.

### Task 5: Volumes section
- List: name (anonymous volumes shortened), driver, size (from `system df`, loaded after the list), used by (container names from summary mounts). Detail: mountpoint, labels, containers; Remove (confirm; disabled while in use), Prune unused (confirm).
- Test: used-by mapping from mounts.

### Task 6: Networks section
- List: name, driver, subnet, number of containers. Detail (inspect): connected containers with Disconnect, Connect a container (combo of containers not attached), Remove (not for built-ins `bridge`, `host`, `none`, `nat`), Create (name + driver; drivers from `NetworkDrivers.For(isWindows)`: Linux `bridge, macvlan, ipvlan`; Windows `nat, transparent, l2bridge`), Prune unused.
- Tests: built-in detection, drivers per OS.

### Task 7: Disk section
- `DiskSummary.From(SystemDf, IEnumerable<ContainerItem>)` → four categories (Images, Containers, Volumes, Build cache) with total and reclaimable bytes: images not used by any container, stopped containers' writable layers, volumes with RefCount 0, build cache not in use.
- Prune per category and **Clean everything unused** (confirm listing the categories and reclaimable sizes): containers, images (all unused), volumes, networks, build cache.
- Tests: summary maths.

### Task 8: Wire-up and verification
- Sidebar order: Containers, Compose, Images, Volumes, Networks, Disk; search filters the current section; debounced reloads (300 ms) on resource events.
- Manual run against the Linux engine with a compose project (two services), pull/remove an image, create/connect/remove a network, prune; screenshots of each section.
