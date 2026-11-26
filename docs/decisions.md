# Decisions

Non-obvious choices, newest first.

## 2026-11-26: No compatibility with earlier Dockyard versions

Owner 2026-11-26: Dockyard is prerelease v0 with one user, so nothing copes with an install, file, name, setting or release from an earlier version; an earlier install is reinstalled. Gone: the old Toolbox and dashboard names (`Dockyard.exe`, `Dockyard Dashboard`, `Dockyard.Dashboard`) and the processes, files and Defender exclusions under them; `DockerTray` (its folder, settings and Run key); the `tcp://127.0.0.1:2375` context, client proxy and Testcontainers line; the Docker CE engine in the user's own distro and the `Distro` setting; `.dockyard.test` (Windows NRPT rule, macOS resolver); `dockyard.cmd` and the app folder on the PATH; the `dockyard-agent` unit; `~/.dockyard/bin` in `/etc/paths.d`; the registry mirror, insecure registry and address pool settings from before `daemon.json` could be edited; replacing a CA from before the IP and usage limits; agents from before the container domains switch; `--engine-only`; a dashboard without a manifest; and, for releases, the fall back to the Toolbox's release when `components.json` does not name a component, the dashboard's assets on the Toolbox's release for 0.8.x, and `v*` tags in the one-liners. Behaviour with other software (DNS servers in `daemon.json`, another Docker app's socket, a winget copy the user installed, Docker Desktop import) stays.

## 2026-11-26: The dashboard stays up on an error nothing caught, and writes it down

DOK-52 ("frequent crashes on Windows", the dashboard only). Every button ran through an `async void` with nothing around it, and the dashboard had no handler for unhandled errors, so one escaping exception ended the process and left no trace. Commands now catch what they throw; an error on the UI thread that nothing caught is written to `%LOCALAPPDATA%\Dockyard\logs\dashboard-errors.log` (`~/.local/share` on Linux, `~/Library/Application Support` on macOS) and shown as an error toast, and the dashboard keeps running. That is safe here because the dashboard only reads from and asks the engine: there is no half-written state that ending the process would protect. Errors on other threads cannot be stopped from ending it, but they are written down first. The pipe thread that receives "show" and "quit" caught only `IOException`; on Windows a pipe name another process still holds throws `UnauthorizedAccessException`, which ended the dashboard, so it now waits and tries again on any failure. Without a crash report from the owner's machine this fixes the classes of crash the code had, not one confirmed crash: the log is there for the next one.

## 2026-11-26: Helm charts are pulled once into a lasting cache and installed from the file

DOK-54. Each add-on ran `helm upgrade --install --repo` against a temporary repository cache, so every create or edit downloaded each repository's whole `index.yaml` again (several megabytes for some), one chart after another, and helm prints nothing until it is done. Charts are pinned by version, so a downloaded chart never changes: `helm pull` puts it in `<app data>/Dockyard/charts/<chart>-<version>.tgz`, pulled into a folder of its own and moved in whole so a CLI and a dashboard fetching at once never see half a file, and the install reads the file. A create downloads every missing chart at once while `kind create` makes the nodes; a failed download there is retried by the install, which then says why. Each install's line says which of how many it is.

## 2026-11-26: A cluster's logs read each node's TTY setting

DOK-51. kind starts its nodes with a TTY, so Docker sends their logs as one raw stream rather than multiplexed by stream. The cluster's Logs tab and its side-by-side button assumed multiplexed, so nothing parsed and the error was swallowed. Each node is inspected first, as a single container's logs and Compose already did, and a log reader that fails now says so in the pane instead of staying empty.

## 2026-11-24: On macOS the trust setting goes where a person can approve it

DOK-20, owner 2026-11-24 ("you can make it fit right?"). As the 2026-11-13 entry found, changing a trust setting waits for an approval dialog, even as root and without a screen. So `UntrustCa` now knows whether it runs silent. From the window and the wizard, the administrator's setting (`security remove-trusted-cert -d`) and the user's (`remove-trusted-cert`) go before the certificate, each only when `dump-trust-settings` lists the CA, so nothing asks where there is nothing to remove, and each within two minutes. If one stays because nobody approved, nothing else is removed and the error says it can be tried again. A silent uninstall or `--trust-https` replacement leaves the setting, as before, and its log says so: it names a certificate and key that are gone. The silent macOS end-to-end run covers that path; the asking path is covered by unit tests, since it needs a person.

## 2026-11-24: Setup hands its --assets folder to the installed app

Since the one window, setup on an install of the same or a newer version hands over to the installed app's window. That app was not started with `--assets`, so **Add** downloaded the dashboard from the GitHub release, which a tag's end-to-end run does not have yet (it publishes after the tests), and the v0.8.0 build failed there. The Settings command on the tray's pipe now carries setup's folder (`settings <full path>`), and the tray uses it for installs and updates from then on. The pipe is for this user alone and the folder was given to setup explicitly, so nothing new is trusted.

## 2026-11-24: Shared storage for kind is a folder on the engine, chosen per cluster when it is made

DOK-35, owner 2026-11-24 ("storage enablement should be per kind cluster"). The plan was an NFS server container with the NFS CSI driver; it needed NFS in the engine's kernel, unverified on the Mac VM.

- **A folder every node mounts instead:** a cluster made with **Shared storage** has `/var/lib/dockyard/shared` on the engine mounted at `/var/dockyard-shared` in every node (kind `extraMounts`). Nodes are containers on one engine, so that is one filesystem for all of them, on Windows, Linux and the Mac alike, with no NFS at all.
- **A second local-path provisioner offers it:** `dockyard-shared-provisioner` in `local-path-storage`, with kind's own provisioner and helper images (read from kind's deployment, so nothing is pulled and they follow kind's version) and `sharedFileSystemPath`, as the storage class `shared` (`ReadWriteMany`, `Immediate`). It is applied with the control plane's own kubectl, so it does not need kubectl on the host.
- **A claim's folder is `<namespace>/<name>/files`** (the provisioner wants a folder below namespace/name), so clusters made with shared storage see the same files for the same claim. **Retain**, so deleting a claim in one cluster does not delete files another cluster may use.
- **Only when a cluster is made:** kind cannot add a mount to a running node. The cluster's details say whether it has it; on Windows, **Open the shared folder** opens it through `\\wsl.localhost`.
- **Load balancers only:** the cloud provider runs with `--enable-default-ingress=false --gateway-channel=disabled`, so it installs no Gateway API definitions or ingress controller in the clusters (owner 2026-11-24). A label carries the image and these flags, so a controller set up otherwise is replaced. Each load balancer's IP on the kind network shows in Clusters next to its service, as kubectl shows it under EXTERNAL-IP.
- **Load balancers stay one switch for all clusters:** cloud-provider-kind has no setting to leave a cluster out (checked in its `--help` and its controller, which serves every cluster `kind` lists, without leader election), and two of them would fight over the same services.
- Checked on the owner's laptop: `SharedStorageEngineTests` makes a two-node cluster through Dockyard's create, and pods on both nodes write to one claim whose files are in the engine's folder.

## 2026-11-24: Search finds everything, and the dashboard keeps running when closed

Owner feedback (2026-11-24). Search filtered only the section on screen; it now also lists what matches in every section in a dropdown under the box (a few of each, grouped by kind in the sidebar's order), and opening one shows its section with that row picked, unfolding its Compose project. Each section offers its rows through `ISearchable.Find` and `Reveal`, with the same words its own filter matches.

Closing the dashboard's window hides it; it stays connected, so opening it from the tray is instant. Quit in the tray's menu ends it too.

## 2026-11-24: Setup only installs or updates; the installed app's window manages, and the tray finds updates itself

Owner feedback (2026-11-24): three UIs felt like too many, the tray should notice and install updates itself, and show what it updates to. Setup and the tray were already one exe and one window, in two modes, but both showed the full manage view, so running the downloaded exe again gave a second window managing the same install.

- **Setup on an install as new as itself hands over:** it opens the installed app's window (through the tray's pipe, or by starting the tray) and exits. Only on an older install does it open, with just **Update to <version>**. A branch build is 0.0.0, which installs over any release, so it hands over only to an install of itself. `--setup` still opens it. If the installed window does not come up within 20 s, setup opens as before.
- **After installing or updating, setup closes and the installed window opens,** unless Windows has to restart. Two processes no longer manage one install.
- **The tray checks every six hours by itself,** not only when its window opens, without the busy state that would block its menu; after a failed check it tries again in 30 minutes. An update shows as a blue dot on the tray icon and an **Update Dockyard to <version>** item at the top of the menu, which opens the window and updates everything that has an update.
- **Buttons name the version:** **Update to 0.7.1**, and **What's new** links Dockyard's and the dashboard's release notes. The other components are pinned per release, so their version comes with Dockyard's.
- **Window titles differ:** "Dockyard Setup", "Dockyard" and "Dockyard Dashboard".

## 2026-11-23: kind's LoadBalancer services get an address through cloud-provider-kind in a container

DOK-34, owner 2026-11-23. kind has no load balancer of its own, so a `type: LoadBalancer` service stayed pending.

- **The official cloud-provider-kind, as one container per engine** (`dockyard-cloud-provider-kind`), pinned by digest (v0.12.0, 2026-10-02, past the 14 days of the pin rule), on the `kind` network with the engine's socket, `--restart unless-stopped`, labelled `dockyard.helper` so the container list leaves it out. The dashboard starts it while the engine has kind clusters and the **Load balancers** switch in Clusters is on (the default), and removes it when the last cluster goes or the switch goes off. A label carries the pinned image, so a Dockyard release with a newer pin replaces it. Clusters made with the kind CLI while the dashboard is closed get it the next time the dashboard is open.
- **`--enable-lb-port-mapping` on every platform:** each service gets an Envoy container whose ports are published on the host, so the address is `127.0.0.1:<port>` on Windows (through 2026-11-23's loopback rule on mirrored WSL, the tray's relay with NAT), on the Mac and on Linux alike, and the dashboard shows the same thing everywhere. On a Linux host the service's own IP on the kind network works too.
- **Published like any `docker run -p`:** cloud-provider-kind publishes on all addresses, as Docker Desktop users also get. On Windows the Hyper-V firewall keeps that to this computer; on the Mac and on Linux it is reachable from the network like any published port.
- **Where they show:** the Envoy containers carry `io.x-k8s.cloud-provider-kind.cluster` and `…loadbalancer.name` (`<cluster>/<namespace>/<service>`). They are kept out of the container list and listed under their cluster in Clusters, with each port as a link. Deleting a cluster from the dashboard removes its proxies, which the cloud provider leaves once the cluster is gone.
- **Checked on the owner's laptop** (Windows, mirrored WSL, kind v1.37): an nginx deployment exposed as LoadBalancer got `172.18.0.4` and answered from Windows on `127.0.0.1:<port>`; everything was removed afterwards.

## 2026-11-23: A running container's folder is listed through its shell, everything else is read once

The owner found browsing files slow. Listing a folder through the archive API returns its whole subtree: opening `/` in a container streamed its full filesystem (over 1 GB for an SQL Server image) on every click, and the same for every folder below.

- **A running Linux container:** one `docker exec` per folder runs a fixed script with `sh`, one `stat -c` over the folder's entries, a loop of shell tests for folders and `readlink` for links. The folder goes in as an argument (`sh -c <script> sh <folder>`), never into the script. About 0.45 s per folder on the owner's laptop (the docker CLI starting included), however large the tree below it.
- **Otherwise** (stopped containers, images without `sh` or `stat`, volumes through their holder, Windows containers): the archive of the whole tree is read once into memory, as images already were (2026-11-17), and every folder after that comes from there. A change made from the dashboard or **Refresh** reads it again. A failed shell listing switches the view to this for the rest of its life, until Refresh.
- **The browser's look:** breadcrumbs with an up button, refresh, upload and download as icon buttons; a filter for the folder; column headers; folder and file icons; relative dates with the exact time as a tooltip; links show their target under their name; the open file gets its own header with size, Save, download and close, below a splitter. Disabled icon buttons are dimmed instead of a grey box.

## 2026-11-23: Every list picks many rows, and every question is one modal dialog

Owner feedback round (2026-11-23): no multi-select or bulk actions; deleting from a row opened the detail to confirm; the always-visible name fields for creating were unexpected; switching engines asked nothing and showed nothing.

- **Selection:** every list (containers, images, volumes, networks, clusters) picks rows the way Explorer and Finder do: click for one, Ctrl/Cmd+click to add one, Shift+click for a range, Ctrl+A for all, plus a checkbox per row and one in the header for all. One picked row shows its detail; more than one shows a selection bar with what can be done to all of them (containers: start, stop, restart, remove; elsewhere remove or delete). A picked compose project stands for its containers. Picks survive the reloads every engine event causes, by key.
- **One dialog for every question and every create:** over the whole window, Enter confirms and Escape cancels. Confirming runs the action inside the dialog with its progress; a failure stays there to read or retry; **Hide** lets a long one go on with its result as a toast. Bulk actions go on past failures and name them together. The row's own delete button, Delete on the list and the detail's button all ask the same dialog, so nothing opens the detail to confirm. Pull, create network and create cluster are dialogs too; the inline fields are gone.
- **Images on the Windows engine:** removing worked through the API all along (checked on the owner's laptop), but a Windows image of several GB takes a long time to remove, and the old confirm bar only showed a muted line. The dialog now says so up front, shows progress, and offers **Force** for an image that is tagged elsewhere or used by stopped containers.
- **Switching engines** asks first and says whether the other engine stops (the tray's own setting, read from its file). Then it shows progress until the dashboard is connected to the new engine. The tray reports a failure back on the dashboard's pipe (`switch-failed <engine> <reason>`), and after 5 minutes the dialog gives up and points at Engines and settings.
- **kind clusters** can be created with 0 to 3 worker nodes, through a kind config.
- **Rows have readable names** for UI Automation and screen readers (they read as the record's `ToString` before).
## 2026-11-23: The tray and wizard are published ReadyToRun

The owner found the wizard and the tray window slow to open. Measured on the owner's Windows laptop, from process start to window: the dashboard (native AOT) 0.3 s, the wizard 2.2 to 4.7 s. Trimming takes the precompiled code out of the runtime's own assemblies, so the single-file app JIT-compiled CoreLib and Avalonia on every launch.

- **`PublishReadyToRun`:** 0.75 to 1.1 s to the window (1.8 s the first time, while Defender scans the new file). The Windows exe grows from 42 to 69 MB, next to the 125 MB engine image the wizard downloads anyway. Private memory with the window open is unchanged (46 → 43 MB).
- **Rejected, native AOT as for the dashboard:** Avalonia's Skia, HarfBuzz and ANGLE libraries then sit next to the exe, and the one-liners download the wizard as one file.
- **Rejected, `EnableCompressionInSingleFile`:** it brings the exe to 34 MB at the same speed, but decompressed assemblies cannot be mapped from the file: +21 MB private memory, past the tray's 60 MB budget.
## 2026-11-23: With mirrored WSL, Windows' loopback is let past Docker's loopback guard

The owner's kind cluster answered nothing on `https://127.0.0.1:<port>` from Windows (`kubectl get nodes` timed out), and neither did any other published port, while a plain listener in the distro did. With mirrored networking, a connection to `127.0.0.1` from Windows reaches the engine's Linux on the interface `loopback0`, not on `lo`. Docker 28 and later drop traffic for ports published on `127.0.0.1` that does not come in on `lo` (a raw-table rule per port), which is every kind API server. Ports published on all addresses are DNATed to the container instead of going to docker-proxy, with `127.0.0.1` as the source, which the bridge drops.

- **dockyard-agent inserts two rules at its start, on mirrored WSL only:** `raw PREROUTING -i loopback0 -d 127.0.0.0/8 -j ACCEPT` and `nat PREROUTING -i loopback0 -d 127.0.0.0/8 -j RETURN`. Such a connection then reaches docker-proxy on `127.0.0.1:<port>`, as one from inside the distro does. Docker appends its per-port raw rules, so the inserted rule stays ahead of them, also for containers started later.
- **Only `loopback0`:** that interface carries Windows' own loopback and nothing from the LAN, so Docker's guard keeps working on the real interfaces.
- **NAT networking is unchanged:** there the tray relays ports itself (DOK-8).
- **Checked on the owner's laptop** (WSL 2.7.14, Docker 29.9.2) by adding the two rules by hand: `127.0.0.1` ports, all-address ports and the kind cluster all answered from Windows, and `kubectl get nodes` listed the node.

## 2026-11-21: No dotted or dashed dividers

The owner's rule for every byhofman app (2026-11-20): "Dotted divider remove that applies to all apps!" Rows and sections are separated by spacing and grouping; where a line is needed it is a solid hairline in the surface tone. A sweep of the dashboard, the wizard, the tray window and the install scripts found no dotted or dashed line of any kind, so nothing changed on screen.

## 2026-11-19: The logo's colours carry the accent and the selection, and the wizard follows the system theme

DOK-27, the November restyle round (owner: "polish, the same as Vaultbar got"). This supersedes 2026-11-15's "the logo's blue as the accent, nothing more". The accent stays the deep end of the logo container's gradient (`#1B74C9`). The logo now also shows in four more places. The light stop (`#5DB7FF`) is the dark theme's links and brand icons. The crane's colour, `#8CCAF7` on the dark icon and `#1B5E9E` on the light one, is the selected sidebar item's text and icon. A running compose project's icon is the brand blue. The tray's running icon is `#2E86DA`, a blue on the container's gradient, in place of Docker's `#2496ED`. Selection is a brand-tinted wash with weight, never an outline. The wizard's active engine card loses its accent border for the same wash.

The wizard and the tray window were dark-only (`RequestedThemeVariant="Dark"`). They now follow the system theme like the dashboard, with the same tokens, so the two apps look like one product in either theme. All colours are tokens in the two `App.axaml` files. `BrandingTests` checks four things: the logo ties, AA for every text and surface pair in both themes, 3:1 for icons, and that no view has a colour literal. `TrayIconTests` checks that the tray colours stand out at 3:1 on a light and a dark menu bar. See `docs/design/restyle-2026-11.md`.

## 2026-11-17: An image's files come from a container of it that never runs, read once into a tree

DOK-36 (numbered DOK-30 at first). Opening Files on an image creates a container of it that is never started: its entrypoint names a path that does not exist, its root is read-only (not on the Windows engine, which refuses that), it has no network, and it carries `dockyard.helper` so the lists hide it. Closing the files, picking another image or quitting removes it. The archive API reads it like any container's.

- **Read once:** an image never changes, so the first listing reads the archive of `/` once, keeps only the tar headers as a tree (`FileTree`), and every folder after that comes from memory, with each folder's size the sum of what is under it. Reading a file or saving it to the computer asks the archive API for that one path.
- **Cost:** the first listing streams the image's whole uncompressed filesystem through the engine's socket, with a progress line. Measured on an M1 with OrbStack: `postgres:17-alpine` (416 MB, 3,896 paths) in 2.2 s. Memory is one small record per path. Creating the container is metadata and an empty writable layer, gone with it.
- **Rejected:** reading layers through `images/{name}/get` (the whole compressed image, plus applying whiteouts across layers ourselves: the same bytes and more code), and per-folder reads as for containers (opening `/` alone already reads everything, and again on every return to it).

## 2026-11-17: A volume opens read-only, and its folder is opened only where this computer can really reach it

DOK-31, DOK-32. The volume's holder (an empty image, never started, `dockyard.helper`) now mounts it `:ro` with no network, so looking can never change it. DOK-17's editing stays: **Allow changes** opens it again with a writable mount. Import also reads Docker Desktop's volumes through read-only holders now. The empty image the holders use gets a tag of its own each time (`dockyard-empty:<random>`), so removing it after one holder can never take it from under another being made at the same moment (it did, between two test runs on CI). A reload of the list (any container starting or stopping rebuilds its rows) no longer closes open files, for volumes or images; only picking another one does.

Where a volume's data is, per platform, and what the button does:

- **Windows, Dockyard's Linux engine (WSL):** in the `dockyard` distro's own disk at `/var/lib/docker/volumes/<name>/_data`. Windows reaches it as `\\wsl.localhost\dockyard\var\lib\docker\volumes\<name>\_data`; the distro's default user is root (`image/wsl/etc/wsl.conf`), so Explorer can read and write it. **Open in Explorer** opens that. Checked for real in the Windows end-to-end run: the path the button names exists and holds what the engine wrote.
- **Windows, the Windows engine:** `C:\ProgramData\docker\volumes\<name>\_data` on the host's own disk, open to Administrators and SYSTEM only. Explorer would offer "Continue", which adds the user to the folder's access rules for good, so Dockyard first checks the folder can be read as the user and otherwise says it is the engine's own folder and offers Export a copy. Not run on a real machine.
- **Linux, the local engine:** the folder is on the host, normally under `/var/lib/docker`, which belongs to root and cannot be listed by others, also not by members of the `docker` group. **Open folder** is offered, checks readability first and otherwise says the folder belongs to root and offers Export a copy. With rootless Docker the folder is under the user's home (`~/.local/share/docker/volumes`) and opens. Opening a file manager as root (pkexec, `admin://`) was rejected: a root file manager on Docker's data is a larger risk than a copy, and only some desktops have it.
- **macOS:** the data is on the ext4 disk of Dockyard's Linux VM. Nothing of the VM is shared to the Mac (2026-11-06: no privileged file server), so there is no host path, and the panel says so in plain words. **Export a copy…** is the way to Finder.
- **Another machine** (a TCP endpoint, a context that is not Dockyard's WSL engine) or **a driver other than `local`:** no folder; Export a copy.

**Export a copy** asks for a folder, makes a new one in it named after the volume (`name (2)` when taken), writes the volume's files there from a read-only holder and opens it in Finder, Explorer or the file manager. Only plain files and folders are written, never over an existing file, and only inside the new folder; links, devices and any path with `..` are left out and counted.

**No traversal:** the path handed to the OS is only built from a mountpoint of exactly the engine's shape, `<absolute root>/volumes/<name>/_data`, with a name that passes Docker's own rule (`[a-zA-Z0-9][a-zA-Z0-9_.-]*`) and no `.`, `..`, empty or `:` segment anywhere. Anything else gives no button. The folder is opened as a folder (Windows shell, `open`, `xdg-open` with the path as one argument), never through a command line.

## 2026-11-17: A terminal into an image is a throwaway container, and a watcher removes it when the window closes

DOK-33. **Terminal** on an image first looks for `/bin/bash`, then `/bin/sh`, through `HEAD` on the archive API of a never-started container of the image, so an image without a shell gets a message in the dashboard and no window. Windows images use `cmd.exe`, which every Windows base image has. Then it creates a container with that shell as entrypoint, a TTY, stdin, `AutoRemove` and a `dockyard.image-terminal` label (shown in the container list, not hidden: it is the user's), and opens the same terminal app as a container's Terminal button, attached with `docker start -ai`.

`AutoRemove` covers typing `exit`. It does not cover closing the window: the engine keeps a TTY container running when its client goes away, and sends no event for it (checked on Docker 29.8: `create, attach, start`, nothing after the client is killed). So the terminal runs a small wrapper with a watcher that outlives the window:

- **macOS and Linux:** `sh -c` starts a subshell that ignores SIGHUP and polls the terminal's process with `kill -0`, then execs `docker start -ai`. Closing the window hangs up docker; the watcher removes the container with `docker rm -f`.
- **Windows:** PowerShell starts a hidden PowerShell of its own (its own console, so closing the window does not take it), which waits for the process that started it and then removes the container. The scripts go as `-EncodedCommand`, because Windows Terminal splits its own arguments at `;`.

Tested by killing the terminal's process while the shell runs and waiting for the container to be gone: on macOS against a real engine locally, on CI's Docker on Linux, and on Dockyard's WSL engine in the Windows end-to-end run (there the PowerShell window is killed). Rejected: a dashboard-side sweep (it cannot tell an open terminal from an abandoned one) and a read-only root for this container (it is the user's shell, thrown away afterwards; the read-only rule is for the helpers).

## 2026-11-17: The dashboard's prototype is its real views drawn headless over fictive data

The dashboard is Avalonia and has no `yarn design` mode (2026-11-06). New screens are prototyped as `PrototypeScreens`: the real views and view models over fake file sources and fictive images and volumes, drawn by Avalonia's headless platform with Skia. Every test run draws them; with `DOCKYARD_SCREENSHOTS=<folder>` they are saved as PNGs to look at. The dashboard tests now render with Skia instead of headless drawing for that.

## 2026-11-15: Downloads are watched from the cluster, and the app stays silent

DOK-29. The download page and the release downloads are probed by Alloy's blackbox exporter in gitops, with a `DockyardDownloadDown` alert in Grafana (gitops decision 41, gitops#185): the website's Dockyard page, the `releases/latest` redirect the one-liners resolve, the three assets with fixed names by HEAD, `SHA256SUMS` listing all twelve assets of `scripts/check-release-assets.sh`, and the three installers. Dockyard itself sends nothing; no telemetry was added. DOK-29 stays open until that PR is merged and the probes report, which waits on the owner (merging gitops deploys).

## 2026-11-15: The brand hint is the logo's blue as the accent, nothing more (superseded 2026-11-19)

DOK-25, "Dockyard's logo colour as a small hint in the interface". The logo already sits in the wizard's and the dashboard's header, so the hint is colour: the accent (primary buttons, the selected sidebar item, focus rings, links, Avalonia's system accent) moves from a generic `#1D63ED` to `#1B74C9`, the deep end of the gradient on the logo's container. Dark links take the light end, `#5DB7FF`; the selection fill is a dark tint of the same hue. Status colours, surfaces and text stay as they were, so the interface still reads as a quiet tool, not a branded one. White on `#1B74C9` is 4.8:1. A test ties both apps' accent to `branding/app-icon/icon.svg`, so a new logo colour cannot leave the interface behind. The design pass (DOK-27) is separate and not part of this.
## 2026-11-15: The dashboard writes the host's kubeconfig itself, and kind only its own file

DOK-28. Creating a cluster in the dashboard puts its context, cluster and user (all `kind-<name>`) into the host's kubeconfig and makes the context current; deleting it takes exactly those out. `kind create` already merged into `~/.kube/config`, but with a plain overwrite: a crash or a full disk mid-write leaves a truncated file, and nothing is kept of what was there.

- **kind gets `--kubeconfig` pointing at a throwaway file** on create and delete, so it never writes the user's file. `HostKubeconfig` merges the entries kind wrote there, and on delete removes `kind-<name>` from every file in the list, as `kind delete` does. Use context does the same merge from `kind get kubeconfig`, so it also puts a cluster back whose entries went missing.
- **Which file:** kubectl's and kind's rule. `$KUBECONFIG` is a list; new entries go into the first file that exists, or the last when none does. Without it, `~/.kube/config`. A file that is a link stays a link and the file it points at changes.
- **Never corrupt it.** The file is changed under `<file>.lock`, the lock client-go and kind take, waiting up to 5 seconds and then saying so; it is never broken. The new text is parsed back before it goes anywhere, written to a temporary file in the same folder with the old file's mode (0600 for a new one), flushed to disk and renamed over the old one. On Windows `ReplaceFile` does the swap, which carries the old file's ACL over. A file that is not valid YAML, holds several documents, or has `clusters`, `users` or `contexts` that are not lists is left byte for byte, with an error naming the file.
- **Backup:** before Dockyard's first change to a file, a copy goes to `<file>.dockyard-backup` with the same mode or ACL. It is never overwritten, so it stays the file as it was before Dockyard.
- **current-context:** the new cluster's context becomes current, as `kind create` did. The context that was current before is kept in that context's `extensions` (`name: dockyard`, `previous-context`), the place minikube keeps its own data and which kubectl keeps. Deleting the cluster while its context is current goes back to that one if it still exists in any file of the list, and otherwise leaves none current, as kind does, rather than pointing at a context that is gone. A state file elsewhere was rejected: it would drift from the kubeconfig the moment anything else edited it.
- **Comments in the file are lost**, as with every `kubectl config` command and with kind. Everything else is kept: unknown fields, extensions, exec plugins, quoting (a namespace `"007"` stays a string).
- **YamlDotNet** reads and writes the file (its representation model only, which is trim and AOT safe; the dashboard publishes with `-warnaserror`). It adds about 1.1 MB to the 22.5 MB dashboard on macOS. A hand-written YAML reader was rejected: the file is the user's and the job is to never damage it.
- **Reachable from the host:** the server in the entries is `https://127.0.0.1:<port>`, the port kind published for the API server. On macOS the VM's port forwarder already carries ports published on `127.0.0.1` to the Mac's loopback, and on Windows the tray carries them to Windows' loopback (DOK-8). No address is rewritten.
- **Tests:** unit tests on all three CI systems on temporary files (merge, remove back to the identical file, previous context, KUBECONFIG lists, mode, Windows ACL, links, broken files, locks, backup). A real cluster made through the same code on CI's Docker on Linux and on Dockyard's WSL engine in the Windows end-to-end run, against a temporary kubeconfig, calls the API server from the host with the entry's client certificate verified against the entry's CA, then deletes it and checks the file is what it was. macOS runners cannot boot the VM, so there it is the unit tests only.

## 2026-11-13: DOK-20 stays open: macOS asks a person before any trust setting goes

Tried in the refactor pass: the user's setting with `security remove-trusted-cert`, the system's by exporting the admin trust settings as root, deleting our SHA-1 entry with PlistBuddy and importing them back. The macOS end-to-end run hung at uninstall and was cancelled after 12 minutes, as `remove-trusted-cert` did before: macOS waits for an authorization dialog for trust-setting changes, also as root and without a screen. So the change was taken back. What is left is removing it only where a person can answer the dialog (the tray window and the wizard window), which needs uninstall to know whether it runs silent; the entry left after a silent uninstall names a certificate and key that are already gone.

## 2026-11-13: Security audit (feedback round 1): every download pinned, the Linux engine behind this user's pipe

The owner asked for a full audit. What changed and why:

- **Every third-party binary is pinned with its SHA-256,** in `image/versions.env` and `image/versions.sha256`, which the image build reads too: Docker's static builds (they publish no checksums), Compose, Buildx, the credential helpers and kind. Before, all but kind followed upstream's newest release after 14 days without any check, and the image build piped `wget` into `tar`. A Dockyard release moves the versions; the pins are the newest stable releases at least 14 days old, for Defender's prevalence rule. Updates on a Linux host still come from Docker's signed repository; the pin only says when one is offered.
- **The Linux engine on Windows is no longer an open TCP port.** `tcp://127.0.0.1:2375` reached root on the engine, and through it the user's files on C:, for every user of the PC and for any web page by DNS rebinding. dockerd now listens on its unix socket only; dockyard-agent serves it to Windows, with mirrored networking on loopback and with NAT on WSL's address for its gateway alone, and only after the connection names a 256-bit token. The tray makes the token once in `%LOCALAPPDATA%\Dockyard\engine.token`, writes it to the distro (`/etc/dockyard/windows.token`, root only) before each engine start, and serves `\\.\pipe\dockyard_linux` and `dockyard_linux_agent` for the current user alone. The pipes are message mode, as dockerd's own: docker's half-close on a pipe is an empty message, which the relay passes on, so exec and attach keep their output. The agent API (2379) moved behind the same token. The engine on Windows now always needs the tray, which it did with NAT networking already. A program that took the pipe name first makes the engine not start, rather than being used.
- **WSL interop is off in the engine's distro.** Nothing there starts Windows programs, and with it on a privileged container could run any .exe as the user, DPAPI included.
- **host.docker.internal refuses Docker's and Dockyard's own ports on every platform** (2353, 2354, 2375 to 2383, and 15353 on the Mac), not only 2375 and 2378 on WSL.
- **Files next to the installer are no longer taken by themselves.** The installer usually sits in Downloads, where any web page can leave a `dockyard-linux-9.9.9-x86_64.tar.gz`, which was imported as the engine or installed as root. `--assets <folder>` asks for it; the end-to-end tests and `install.ps1 -Build` pass it. The tray's own update still installs the dashboard from the folder it downloaded and checked it into.
- **The HTTPS CA excludes every IP address and is for web servers only** (serverAuth EKU; `-p ssl` on macOS). A name constraint only limits the name forms it lists, so it could still vouch for any IP address, code or mail. A CA from before is replaced, which asks for trust once more.
- **macOS: `/etc/paths.d` names `/usr/local/dockyard/bin`,** root's, with one script per command that runs this user's copy for this user alone. It named `~/.dockyard/bin`, which every account's shells, root's too, then ran from a folder this user can change. The socket daemon's plist goes into the administrator script itself instead of through a file of the user's.
- **Windows: Defender ASR exclusions only for Program Files.** The installer excluded itself wherever it ran, also after uninstall; uninstall now takes that and the one-liner's path back.
- **Elevated steps take nothing from a place the user can change:** the engine zip and the dashboard download go to Program Files, the zip's entries must stay in `C:\Program Files\Docker`, the worker's log must be the very file the caller made, and paths in PowerShell and shell scripts are quoted whatever they hold.
- **Smaller limits:** the agent reads lines of at most 64 KiB; the Mac keeps at most 256 UDP senders per published port and lets quiet ones go after 3 minutes; the VM's port 80 and 443 answer the Mac alone; compose files from container labels are only used when they exist on this machine and are not network paths.
- **CI:** actions pinned to commits, read-only token except for publishing the release, `goreleaser/nfpm` pinned by digest, Dependabot to keep the pins current.

Not changed, on purpose: `alpine` and the .NET SDK image are official images by tag, so they get their security fixes; signing waits on the owner (NEEDS-OWNER).

## 2026-11-12: With WSL's NAT networking, Dockyard carries 127.0.0.1:2375 itself

kind on the Windows runner failed at "Writing configuration": it reads a file through `docker exec`, and the output arrived empty. Every `docker exec`, `docker attach` and `docker run` without `-d` printed nothing from Windows, while the same exec inside WSL, also over TCP on its own `127.0.0.1:2375`, worked. The listener on Windows' `127.0.0.1:2375` was `wslrelay`, WSL's localhost relay for NAT networking. The docker CLI half-closes the connection once stdin is done, and the engine only writes the output after that; the relay loses what comes after the half-close. Pulls and other plain requests were never affected, which is why nothing caught it.

- **dockerd no longer listens on WSL's loopback with NAT networking,** so `wslrelay` does not take the port. dockyard-agent serves the engine on WSL's own address, port 2375, and accepts only the default gateway, which is Windows. A container reaching that address is refused, like `host.docker.internal:2375` already was.
- **The tray, or the wizard while it runs the engine, listens on `127.0.0.1:2375` and relays to it,** passing half-closes on. The endpoint, contexts and `.testcontainers.properties` stay as they are.
- **Mirrored networking is unchanged:** loopback is shared for real there, and the WSL interface `loopback0` is how both the agent and dockyard-init tell. `DOCKYARD_TCP` still asks for the old listener explicitly.
- **The engine now needs a Dockyard process on Windows with NAT networking,** which it already did in practice: the tray's `wsl.exe` keeps the distro alive. A tray that starts while an engine runs finds it and relays again, rather than starting a second dockyard-init that would take the running engine's socket away.
- **Tests:** the agent's listener and the Windows relay each pass an exchange where the engine answers only after the client half-closed, and the Windows end-to-end run now checks `docker exec`, an attached `docker run` and stdin through `docker exec -i`, and starts the engine through the tray as a user does.

## 2026-11-12: kind is pinned with its checksums, installed by default, and the dashboard reads clusters from the engine

`DOK-22`, the owner's "basic kind support" (feedback round 1).

- **Pinned, unlike Compose and Buildx.** Those follow upstream's newest release after 14 days and are downloaded without a checksum; kind publishes its checksums next to the binary, so only a hash reviewed into `Kind.cs` catches a replaced file. A Dockyard release moves kind, which costs one line per platform. v0.33.0, Kubernetes 1.37 by default. The checksums were checked against our own download of each binary. That Compose, Buildx and the credential helpers are not checksummed is left as it was here and named for the audit.
- **Installed by default, as every optional component is.** A new install gets everything (2026-11-06); kind is a 10 MB file that runs nothing until a cluster is created. Existing installs get it on their next setup run or update, and `--without kind` or **Remove** keeps it out.
- **Where:** next to `docker` on Windows and macOS, in folders Dockyard owns. On Linux `/usr/local/bin`, through the setup script as root, with a marker in `/etc/dockyard`: a kind there without the marker is the user's and is never replaced or removed. GitHub's Ubuntu runner has such a kind, which the end-to-end test removes first.
- **The CLI is kind.** Dockyard has no command of its own to wrap, and a wrapper would only rename kind's verbs. kind follows the docker context; on Windows that has to be `linux`.
- **The dashboard reads clusters from the engine's containers** by kind's labels (`io.x-k8s.kind.cluster`, `.role`), so it updates live from events and needs kind only to create, delete and get a kubeconfig. It runs kind with `DOCKER_CONTEXT` set to the context it shows and the docker CLI's folder first on `PATH`, since an app started from the menu bar has no shell `PATH`.
- **Use context runs `kind export kubeconfig`,** which changes `~/.kube/config` the way `kind create` already does (replaced 2026-11-15: Dockyard writes the kubeconfig itself, see DOK-28). Nothing else of the user's Kubernetes setup is touched. kubectl is not installed: the owner asked for kind, and any kubectl works with the context.
- **Verified in CI only, never on the owner's machine:** the VM image test creates a cluster on the VM kernel the Mac uses (x86_64 build of the same Alpine kernel), and the Linux and Windows end-to-end runs create one, reach its API server on `127.0.0.1` and delete it. The Mac end-to-end runs cannot boot the VM, so on macOS only install and removal are checked there.

## 2026-11-07: A published port on macOS also listens on IPv6

`DOK-21`. Only the Mac lacked it: Windows already relays `[::1]` to the published port (`LoopbackForwarder`), and on a Linux host Docker itself binds both families. The helper on the Mac listened on 127.0.0.1 or 0.0.0.0 alone, so `http://[::1]:8080` and clients that try IPv6 first without falling back got a refusal. It now also listens on `::1` for a loopback publish and on `::` for a wildcard one, IPv6-only so the two sit side by side, for TCP and UDP. If something else already holds the IPv6 side it keeps it, the helper says so once, and IPv4 forwards as before. The fix was small, so nothing is documented as a limit.

## 2026-11-07: Uninstall on a Linux host takes the agent's rules and addresses away, matched by what is Dockyard's

`DOK-19`. Uninstall stopped the agent but left `DOCKYARD-INPUT`, the nat redirects and DNAT rules, the firewall openings on container interfaces, and 198.19.250.1 and .254 on `lo` until a reboot. The setup script now removes them after stopping the agent and before restarting Docker. It finds them in `iptables -S` by what only Dockyard uses (its 198.19.250.0/24 range, 127.77.0.1, its chain, and the three ports of the firewall opening) rather than asking the installed agent, because the binary on disk can be older than the uninstaller and a restarted older agent may have stacked copies. A test runs the script against the agent's own rule list with stand-ins for `iptables` and `ip`, so a rule added later without a matching pattern fails there, and the Linux end-to-end run checks a real machine after uninstall.

## 2026-11-07: The window is given its settings, so tests never share the user's file

`DOK-18`. Every `MainViewModel` loaded and saved the real `settings.json`, so the wizard and component tests, which xUnit runs in parallel, read and wrote one file at the same time. On Windows a write while another test reads it fails with a sharing violation, `Run` turned that into the status line, and the row never updated, which is why only Windows failed. The tests also changed the settings of whoever ran them. `Settings` now remembers the file it was loaded from and saves there, and the view model takes one; the app passes none and gets the user's file as before, and each test gets its own in a temporary folder. Nothing in the app changes how or where it saves.

## 2026-11-07: Releases are read from github.com, not the API

Anonymous API calls are limited to 60 an hour per address, shared by everyone behind one office network or VPN, and an install or update failed on it. Nothing in Dockyard calls the API now.

- **Dockyard's own releases:** the `releases/latest` redirect gives the tag, and `SHA256SUMS` gives both the asset names and their hashes.
- **Compose, Buildx and the credential helpers:** `releases.atom` gives the tags and dates the 14-day wait needs, which the redirect cannot. It shows the last 10 releases and has no prerelease flag, so a tag with a suffix counts as a prerelease. Asset names are fixed templates with the tag in them, because Buildx's `checksums.txt` lists only some of its assets.
- **A 403 or 429 from any of these** is "could not check right now" on the update row and a plain sentence in an install. github.com has limits of its own, so this can still happen, only far less often.

`UseSystemResourceKeys` is off for the app and wizard: it removed .NET's message texts to save size, so every framework error reached the user as a key such as `net_http_message_not_success_statuscode_reason`.

## 2026-11-06: The CA's key stays off what containers can read, with the name constraint still as the limit

A privileged container on the engine's Linux can read `/Users` on a Mac and `/mnt/c` on Windows, so a restricted file there is readable. The owner has not decided otherwise, so the name constraint stays what limits the damage, and the key moved where no manual step is needed:

| Host | Where | What a container sees |
|---|---|---|
| macOS | Login keychain, written over stdin to `security`. Nothing outside `/Users` is writable without root | The keychain file, encrypted with the login password |
| Windows | `ca.key.dpapi`, DPAPI for the current user | Ciphertext, of no use without the user's logon |
| Linux host | Still a file, mode 0600 | A privileged container is root on the machine; no store holds against that |

- **It does not help against code running as this user:** that can ask the keychain or DPAPI just as Dockyard does.
- **A store that cannot answer is not "no key".** A locked keychain must not make a new CA and ask for trust again, so reading throws, and turning HTTPS on then reports it with plain HTTP still working. The tray only reads the key when a certificate is due.
- **No fallback to a file:** that would quietly undo this.
- **No migration:** no release ever wrote the old file.

## 2026-11-06: Findings from the DOK stack review

- **Firewalls:** a redirected packet arrives addressed to the bridge, so ufw's default deny dropped `host.docker.internal` and domains. The agent accepts those three ports on container interfaces only, ahead of ufw. They refuse whatever was not redirected to them.
- **A proxy only on Windows' loopback with NAT networking** was written into the engine's config, and no pull worked. That was a regression on such a PC, so it is now probed from WSL before every engine start. If out of reach the engine goes direct and the wizard says so.
- **The NRPT rule follows the Linux engine component.** Removing that row now asks for administrator approval, before anything changes.
- **Approval from another administrator's account:** the elevated step gets the certificate itself, since it cannot read this user's owner-only folder. It only adds a root carrying exactly the name constraint, since the value comes from a less trusted process.
- **Logins stay in the OS store on uninstall, and the wizard names them.** They are not erased: other Docker apps read the same entries, and a password cannot be got back.

## 2026-11-06: Any component can be removed, except what the rest stands on

The owner asked for every component to be a row with Add and Remove, in the wizard and with `--with` and `--without`.

| Required | Why |
|---|---|
| Dockyard | It runs everything; Uninstall is how it goes |
| Docker CLI (Windows, macOS) | Both engines, Compose and Buildx are used through it. It used to be one row with the Windows engine, so it now has its own row and can be installed from the same zip without the engine |
| The engine on macOS | It is the only one there |
| Docker on Linux | Dockyard manages it but does not own it: the packages stay the user's, also after uninstalling Dockyard |

- **The last engine cannot go**, on Windows, where there are two. Dockyard does nothing without one, and Uninstall exists.
- **Removing an engine keeps its images.** The WSL distro and `ProgramData\\docker` stay, the context goes, and the docker command moves to the engine that is left. Deleting them takes `--remove-data`, for the Linux engine. Silently destroying images because a row was clicked is not worth one button less.
- **What was removed stays removed** across reruns and updates, in `RemovedComponents`, as the dashboard already did by its files.
- **A refused silent request exits 2 before anything changes.**
- **Compose and Buildx being gone is not a broken install:** the dashboard's Compose actions then report the failure, and the row says it uses them.

## 2026-11-06: The dashboard's version is shown and updated on its own, and the release stays one

The dashboard has its own row with versions and **Update**, and one can be newer than the app. It is still built from the same tag: separate release trains would mean a second tag series and asset set for something the same commit always builds. On Linux its package depends on the exact version of the app's, so updating it updates both, and the row says so.

The installed version comes from the file on Windows, and from `--version` on macOS and Linux. A dashboard from before this has no `--version` and would open its window, so it is never asked: it came with the app's version.

## 2026-11-06: The process is called Dockyard Dashboard, and only the published file changed name

What Task Manager's Details tab, Activity Monitor and `ps` show comes from the file name, and Task Manager's Processes tab from the file description, so both changed. The assembly stays `Dockyard.Dashboard`, since `avares://` addresses go by assembly name and a space would have to survive all of them. The release asset also stays `Dockyard.Dashboard.zip`: scripts and trays already out there ask for it by name.

- **Linux truncates:** `/proc/<pid>/comm`, `ps`, `top` show `Dockyard Dashbo`, 15 characters. The kernel does that, and no name of this length avoids it.
- **The old name is still found and stopped:** wizard, update, uninstall and Remove look for both, and installing removes the old file and whatever the old manifest listed.
- **A v0.2.1 tray updating itself** runs the old installer code, which unpacks the new name next to the old file. The tray finds either, and the next wizard run removes the old file, because the tray cannot write to Program Files by itself.
- **Defender exclusions go by path,** so an app update now also adds the new path.
## 2026-11-06: The local CA's key stays on the host, and only for dockyard.test

`DOK-16`. A CA in the trust store is a risk to everything the browser trusts, so it is built to be worth little if taken:

- **Name constrained, as a critical extension,** to `dockyard.test`, with path length 0. macOS, Windows, Chrome and Firefox all enforce this; a stolen key cannot sign `www.bank.com`.
- **Its key is never given to the engine's Linux.** The agent gets a 90 day wildcard leaf, kept in memory only and handed over by the tray after each engine start, so an agent without a tray serves HTTP only.
- **Off by default.** It is a button with the explanation next to it, and declining leaves plain HTTP working. Silent setups only do it with `--trust-https`, and then only with what needs no prompt.
- **Which store:** Windows uses the machine's, through the wizard's UAC prompt, because the current user's store raises its own dialog. macOS uses the login keychain from a window, and the system keychain from a silent setup with sudo. Linux uses the system's plus the user's NSS database where one exists; a snap or flatpak browser keeps its own and will warn.
- **Removal:** turning it off and uninstalling both remove the trust and delete the key. Windows removes by subject name, so nothing stays even if the key folder is already gone.
- **Where the key is:** see the entry on keeping it off the shares.

## 2026-11-06: Domains resolve per place, and no port 80 is taken on the host

`*.dockyard.test` (`.test` is reserved, so it can never be a real name) is answered by the agent, which also proxies HTTP by `Host` header to the container. The name resolves to something different from each place, because no single address works from all of them:

| Asked from | Answer | Why |
|---|---|---|
| A container | 198.19.250.1 | Ports 80 and 443 there redirect to the proxy |
| The Mac | The VM's own address, through `/etc/resolver` and a DNS relay on 127.0.0.1:15353 | Needs no root process on the Mac and no port on its loopback. The MAC address is now fixed so the lease stays |
| Windows | 127.77.0.1, through an NRPT rule, then a relay in the tray to WSL's loopback | Only loopback is shared with WSL. All of 127/8 is loopback on Windows, so this cannot clash with `127.0.0.1:80` |
| A Linux host | 127.77.0.1, through a systemd-resolved routing domain to port 2353 | iptables redirects 80 and 443 on that address: binding them would make every later `-p 80:80` fail, since Linux refuses a wildcard bind next to a specific one |

- **Names are flat, `project-service.dockyard.test`, not `service.project.dockyard.test`.** A wildcard certificate covers one label, so deeper names would need a certificate per project and the CA key online.
- **Port choice:** one chosen in the dashboard, then the `dockyard.port` label (`dev.orbstack.http-port` also counts), then what actually listens in the container, then what it exposes. Choices are kept by container name, because Compose replaces containers when their configuration changes.
- **Redirected listeners are open on all interfaces and refuse anything not redirected from port 80 or 443,** which is what keeps them off the LAN on a Linux host and mirrored WSL.
- **macOS 15 and later may ask for Local Network access for a browser** reaching the VM's address. The other route is a root daemon holding a loopback alias, and that is a larger thing to install than one prompt.

## 2026-11-06: Tray actions are a native menu, not a popover window

`DOK-2` says popover. A window in the tray process would load the UI stack it otherwise never touches until the settings window opens, and this app competes on memory. The native menu gives the same actions: start, stop, restart, open the first published port. It lists at most 20 containers, refreshes on the tray's existing 10 second tick while the engine runs, and is only rebuilt when the list changed. A menu replaced while open would close on the user.

## 2026-11-06: Files in containers and volumes go through the archive API, in the dashboard

`DOK-5` and `DOK-17` share one file browser built on `GET`/`PUT .../archive`. This needs no shell or `ls` in the image, works on stopped containers, and needs nothing new in the VM, so it costs no memory while unused.

- **Listing reads the whole subtree**, since a tar cannot skip a folder. Only headers are kept, but a huge folder takes as long as reading it. Nothing lists until the Files tab is opened.
- **No delete.** The archive API has none, and deleting through `exec` would not work in a stopped container or an image without a shell. Edit, upload and download cover what the backlog asks for.
- **OrbStack mounts these at `~/OrbStack`; this does not.** On macOS that takes a privileged file server, and it competes on memory. Windows gets **Open in Explorer** through `\\wsl.localhost`, which costs nothing. So on macOS and Linux "reachable from the host" means through the dashboard.
- **A volume is held by a container that never starts**, on an empty image created on the spot, labelled `dockyard.helper` and hidden from the list.
- **Screens were not prototyped first:** this app is Avalonia with no `yarn design` prototype mode, so the build skill's prototype step has nothing to run on. The Files tab follows the existing panels.

## 2026-11-06: Import copies through both engines' APIs

Docker Desktop's disk image format is its own and changes, so the import never reads it: both engines stay up and data goes over their APIs, unbuffered. This covers anything an engine holds and needs no extra space beyond the target.

- **Containers are committed first** and arrive stopped. Pulling their image again would lose what changed inside them, and starting them could clash on ports with what still runs in Docker Desktop.
- **Volumes go through a holder container that is never started**, on an empty image made on the spot, so nothing is pulled and no container runs in the source.
- **A name already in the target is skipped and reported, never overwritten.** Networks get a fresh subnet, since containers find each other by name and the source's range may clash.
- **Tested against a second engine in CI, not against Docker Desktop itself**, which no runner has. Starting Docker Desktop and its socket paths are unverified until a real machine runs it.

## 2026-11-06: The proxy is read at each engine start, and daemon.json is rendered by the app

On macOS and in WSL the app renders the engine's `daemon.json` at every start (macOS through a read-only virtiofs share, WSL by copying it in), so nothing the user would have to edit lives in the engine. On a Linux host the file belongs to the system's Docker, so it is merged, and a `proxies` block somebody else wrote stays.

- **Containers get the proxy through the CLI's `proxies` in `~/.docker/config.json`, keyed by Dockyard's endpoint.** A `default` entry would also change what happens in OrbStack's or Docker Desktop's contexts.
- **A proxy on the host's loopback is rewritten to 198.19.250.254**, the host address only the engine's Linux has, so corporate agents listening on `127.0.0.1` work.
- **A change while the engine runs applies at its next start**, not at once: restarting by itself would stop the user's containers.
- **PAC files are not followed:** the engine takes one fixed proxy. Following one would need a proxy of our own that evaluates JavaScript, and that is memory this app is competing on.
- **No VPN code exists on purpose.** On macOS the VM's traffic goes through vmnet NAT and the Mac's routes, and DNS through the Mac's resolver; WSL mirrored networking and DNS tunnelling do the same. CI cannot run a VPN, so this is unverified until tried on a real one.

## 2026-11-06: Shared Docker paths are only taken when free

`/var/run/docker.sock` and `credsStore` in `~/.docker/config.json` are shared with every other Docker app.

- **The socket:** a link another app made stays, even dangling while that app is stopped, because OrbStack and Docker Desktop recreate and rely on theirs. The cost is that tools that ignore contexts keep using that app. A small LaunchDaemon recreates the link at boot since macOS clears `/var/run`; `~/.docker/run/docker.sock` needs no administrator. `.testcontainers.properties` is only written when the system socket is free, or Testcontainers would silently leave the other app.
- **`credsStore`:** set only when unset. Existing plaintext logins move to the store before they leave the file, and if one fails the file is untouched. Uninstall takes back only a store that Dockyard set (a marker file says so), since a store whose helper is gone breaks every `docker login` and pull.
- **macOS needs the PATH entry first:** docker finds the helper through the PATH, and a store it cannot find would also break `docker login` from other apps. So without administrator approval `docker login` stays as it was until the wizard runs with approval.
- **Linux without a session bus or a working secret service keeps Docker's default.** There is no OS store to go to, and setting one makes `docker login` fail. The wizard says so.

## 2026-11-06: Host and name services live in dockyard-agent, on addresses only the engine's Linux has

`host.docker.internal` works without `--add-host` by making the agent the DNS server of every container (daemon.json `dns`), which also gives one place for domains later. The other options were dnsmasq, one more process and config file for no gain, or `--add-host` at each `docker run`, which is not automatic.

- **198.19.250.1 (DNS) and .254 (the host)** sit on `lo` inside the engine's Linux. They are in the benchmark range, and nothing outside that Linux ever sees them, so they cannot clash with a VPN.
- **The host is reached through its loopback, not through the VM's NAT gateway.** Most dev servers bind `127.0.0.1` or `::1`, which the gateway cannot reach, and macOS's firewall can block the gateway. iptables redirects to the agent, which reads `SO_ORIGINAL_DST`, opens a vsock connection, and the helper dials loopback on the Mac, IPv4 then IPv6. As on Docker Desktop and OrbStack, this gives containers the host's loopback services; UDP is not carried.
- **A connection not redirected from the host address is dropped**, and `DOCKYARD-INPUT` drops anything for these addresses that did not come from `lo`, `docker+` or `br-+`, because on a Linux host or mirrored WSL the LAN is one interface away. A container on a bridge renamed by `com.docker.network.bridge.name` does not get these services.
- **On WSL the app carries the agent, since the image is only imported once.** `Dockyard.exe` embeds a tarball of the agent and scripts and lays it over the distro before each start when its hash changed; `dockyard-update` still only replaces Docker. The agent is a static musl binary so it also runs on a glibc host. It exits with dockerd so it does not keep WSL running. `2375` is refused from containers: through `host.docker.internal` it would be the unauthenticated engine.
- **With NAT only the gateway reaches Windows**, for services listening beyond loopback and past its firewall. Mirrored networking is what reaches `127.0.0.1` there. It is written after the distro is imported, as in v0.2.1, so it applies from the next WSL start. It is left out on Windows Server: on Server 2025 mirrored mode gave WSL no network interfaces at all and cut Windows off from `127.0.0.1:2375`.
- **All other names go to whatever `/etc/resolv.conf` says at that moment**, read per query, so a VPN that changes DNS after the engine started takes effect without a restart.

## 2026-11-06: A window waits for the display instead of catching the crash

On a locked Mac, Avalonia's native code aborts at startup with error -6661, so no managed handler can catch it. The wizard and dashboard check the session first (`CGSSessionScreenIsLocked`, console) and wait up to 10 minutes, then exit quietly; the tray waits as long as it takes, because the login item can start before the unlock. The engine does not start until then, which only matters for a tray started on a locked screen.

Uninstall removes every bundle with Dockyard's id that it can find (where the login item points, both Applications folders, `.new` and `.old` leftovers), because the wizard from a one-liner only knew `~/Applications`. A bundle with another id is never touched.

## 2026-11-05: The wizard owns the install, the one-liners only fetch it

The owner found install logic in `install.ps1` and manual steps in the README. All install logic is now in `Dockyard` itself, one binary with three faces: the wizard window, `--silent`, and the tray. The scripts download it, check `SHA256SUMS` and launch it, passing their arguments through.

- **Rerun:** without `--dashboard` or `--no-dashboard` an existing install keeps what it has. A skipped dashboard must not come back by itself.
- **Updating from the wizard always runs the full install**, not "replace the app". Going from v0.1.0 needs the `dockyard` distro, and replacing the exe alone would leave a tray with no engine.
- **The installed Windows tray can now replace itself:** it renames the running exe aside. Earlier builds copied over their own running exe and failed with a sharing violation (reproduced on a Windows runner), so those installs can only update through the one-liner.
- **`Downloads\Dockyard.exe` stays the Windows download path:** a Defender ASR exclusion goes by path, and machines that already excluded this one should keep working.
- **No sign-out:** `daemon.json`'s `group` names the user next to `docker-users`, and Linux gets an ACL on the socket. Both take effect at once; group membership does not. The name goes in only if it resolves, because dockerd refuses to start on one that does not.
- **`.wslconfig`:** mirrored networking is only added when `networkingMode` is unset, on a marked line that uninstall takes back. WSL is not shut down to apply it, since that would kill the user's other distros.
- **macOS silent shows no dialog:** without passwordless sudo the PATH entry and Rosetta are skipped with a message. The wizard window asks through the normal administrator prompt.
- **Release assets arrive through a draft.** A draft is invisible to `releases/latest`, so nobody can see a release without its assets. The release skill's manual `gh release create` is not run here: it would create the release before the pipeline, which then cannot.
- **Two PRs around the release:** the wizard first, then the tag, then the new scripts. The old scripts work against the new release, and the new scripts need its assets, so main's scripts never point at assets that do not exist.

## 2026-11-05: macOS start always runs prepare, the data disk is only formatted when it reads as zeros

`MacHost.Start` runs `dockyard-vm prepare` before every boot. `vm.json` holds absolute kernel paths and the root image holds the kernel's modules, so without it a moved app does not boot and a self-updated app boots a new kernel on old modules. Prepare only copies when the bundled release changed.

The VM init formats `/dev/vdb` only if its first 64 KiB are zero, and powers off if the disk does not mount. A filesystem probe that misreads a disk would wipe the user's images, and continuing without the disk would put them on the root image, which every update replaces.

## 2026-11-05: OrbStack must-haves came from a stated test

The owner asked which Docker-related OrbStack features Dockyard must have. No source can settle "must", so the [thesis](research/orbstack-docker-features/thesis.md) states a test: a workflow that fails, needs manual setup or is unsafe by default, or a differentiator with evidence beyond OrbStack's own marketing. `DOK-7` to `DOK-17` were added on that basis, and `DOK-2` and `DOK-5` were kept as must-haves. Backlog scope otherwise did not change.

The research standard says a thesis never decides what to build. The owner's brief asked for the classification and the backlog items, so the brief wins for this run. The PR is left for the owner's review, not merged. Mechanism, naming and per-OS design stay open until the slice is prototyped and built.

## 2026-11-13: Sidebar items carry their section's name for automation

The Windows end-to-end run's "Clusters" and "Images" screenshots showed the containers list. The dashboard switched sections fine when clicked; the automation never clicked. A sidebar item's automation name was the `Section` record's `ToString`, so UI Automation's search for "Clusters" found the text inside the item, which has no select pattern, and the script skipped silently. Screen readers read the same record text. Items are now named after their section, a headless test selects each one through its automation peer on every CI platform, and the e2e script selects the list item and fails when it is missing or not selected.

## 2026-11-13: DNS servers in a Linux host's daemon.json become the agent's upstream

The Linux end-to-end run failed once on "daemon.json names the agent". It was not a race: some of GitHub's runners (those provisioned with the hosted compute agent's DNS resolver) boot with `/etc/docker/daemon.json` set to `{"dns": ["168.63.129.16"]}`, written before the job starts; most boot with no file. Dockyard treated any `dns` it had not written as the owner's choice and left it, so on those machines containers silently had no `host.docker.internal` and no `dockyard.test` names. A probe of 30 runners found the file on one.

- **Dockyard now takes the `dns` key and keeps the servers it held** in `/etc/dockyard/dns-upstreams` (resolv.conf form). The agent asks those servers, not `/etc/resolv.conf`, for every name that is not Dockyard's, so containers resolve other names through the same servers as before and also get Dockyard's names. The agent runs in the host's network namespace and as root, so it reaches whatever server the containers could.
- **Uninstall puts those servers back** into `daemon.json`. A reinstall over a file that already names the agent keeps the saved servers; one over a file without `dns` removes them.
- **Rejected: leaving the setting and telling the user**, the old behaviour. It made the product's core feature depend on a file some provisioners write without the user knowing, and the message was easy to miss in a silent install.
- **The e2e starts every run from a daemon.json naming the runner's DNS server**, and the later fresh install from none, so both kinds of runner test both paths.

## 2026-11-13: Each host is a thin IEngineHost over classes that each do one thing

DOK-23. `MacHost`, `LinuxHost`, `WindowsHost` and `MachineSetup` were 450 to 650 lines each, mixing install and uninstall, the running engine and the trust store. They are now classes of their own, and the host only wires `IEngineHost` to them:

- **macOS:** `MacVm` (the helper process, its images, the daemon.json it boots with), `MacComponents` (CLI, plugins, kind, dashboard, self-update), `MacSetup` (bundle, administrator steps, context, sockets, login item), `MacTrust`.
- **Linux:** `LinuxRoot` (getting root, and setup-linux.sh), `LinuxComponents` (Dockyard's packages or tarball, Docker's packages, plugins, kind), `LinuxSetup` (agent and daemon.json, autostart, credential store), `LinuxTrust`. The engine is three systemctl calls and stays in the host.
- **Windows:** `WslEngine` (the distro's keep-alive and the pipes), `WindowsServiceEngine` (the Windows engine's service), `WindowsTrayServices` (published ports, domain relay), `WindowsSetup` (machine steps in or out of elevation). `MachineSetup` keeps only the order of install, update and uninstall, over `MachineEngine`, `MachineApps` and `MachineTrust`.

Separate classes, not partial classes: a partial class keeps one set of fields that every part can touch, which is the coupling the split is meant to remove. No behaviour changed. The code moved as it was, and the unit tests and the three e2e runs are the check.

## 2026-11-25: Container names move to dockyard.internal, without an alias

The owner chose `.internal`, the name space ICANN reserved for private use, over `.test`, which is meant for testing. `*.dockyard.test` stops answering in 0.9.0: no alias. Setup takes out the old NRPT rule on Windows and `/etc/resolver/dockyard.test` on macOS, and the local CA is replaced, because its name constraint names the zone. The machine is asked to trust the new one once.

## 2026-11-25: All installation logic is in the Toolbox; the one-liners only fetch it

The owner: the Toolbox is meant for installing all components and features, and the scripts the one-liners download stay minimal. Each script finds the release, downloads the Toolbox, checks it against `SHA256SUMS` and starts it with its arguments. `install.ps1` lost `-Build` (`gh run download` and `--assets` do the same by hand), the `-EngineOnly` and `-NoStart` aliases, the clean-up of earlier downloads and the Defender exclusion it added when the Toolbox was blocked; it now says how to allow it. DNS routing rules and trusting the CA stay in the Toolbox's setup, not in dockyard-host, which only runs what has to run while the engine does.
