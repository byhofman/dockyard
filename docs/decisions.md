# Decisions

Non-obvious choices, newest first.

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
