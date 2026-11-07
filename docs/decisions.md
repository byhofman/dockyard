# Decisions

Non-obvious choices, newest first.

## 2026-11-07: A published port on macOS also listens on IPv6

`DOK-21`. Only the Mac lacked it: Windows already relays `[::1]` to the published port (`LoopbackForwarder`), and on a Linux host Docker itself binds both families. The helper on the Mac listened on 127.0.0.1 or 0.0.0.0 alone, so `http://[::1]:8080` and clients that try IPv6 first without falling back got a refusal. It now also listens on `::1` for a loopback publish and on `::` for a wildcard one, IPv6-only so the two sit side by side, for TCP and UDP. If something else already holds the IPv6 side it keeps it, the helper says so once, and IPv4 forwards as before. The fix was small, so nothing is documented as a limit.
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
