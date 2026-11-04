# Dockyard macOS VM (sub-project C)

Linux containers on macOS through a lightweight VM on Apple's Virtualization.framework, modelled on OrbStack's approach: fast boot, memory handed back to macOS, bind mounts at native paths, ports on localhost, nothing custom in Docker's own networking. Shared values are in `2026-11-04-dockyard-architecture.md`; depends on sub-project B (`2026-11-04-dockyard-platform-hosts-design.md`) for `IEngineHost`, the `dockyard-linux` image and the macOS tray.

## Scope

In:

- `dockyard-vm`: Swift helper that runs the VM.
- `dockyard-agent`: small guest agent inside the VM.
- aarch64 build of the `dockyard-linux` image plus kernel and initramfs.
- `MacHost`: `IEngineHost` for macOS, docker CLI and plugins on the Mac, LaunchAgent autostart.

Out: Intel Macs, macOS 13 and older, Kubernetes, container IP/domain access from the Mac (only published ports), signing/notarization and the .dmg (D).

## Requirements

- Apple silicon, macOS 14 or later.
- Rosetta installed for amd64 images (`softwareupdate --install-rosetta`, offered during setup when missing).

## Components

```
macos/DockyardVM/             Swift package
  Sources/dockyard-vm/        VM configuration, lifecycle, control socket, docker socket + port proxies, balloon loop
  Tests/                      swift test: config building, proxy framing, balloon policy
src/Dockyard.Agent/           .NET native AOT, linux-musl-arm64; runs inside the VM
src/Dockyard.Host/Mac/        MacHost, VmProcess, CliInstaller, LaunchAgent
image/                        extended: aarch64 rootfs, kernel/initramfs extraction, root disk image
```

## Kernel

- Alpine's `linux-virt` package (same Alpine release as the image), so Alpine maintains it and ships its security fixes. No kernel of our own to maintain.
- CI extracts the kernel, decompresses it to the uncompressed arm64 `Image` that `VZLinuxBootLoader` needs, and builds an initramfs with the modules needed to reach the root disk (`virtio_blk`, `ext4`, `virtiofs`, `vsock`, `virtio_balloon`, `overlay`, netfilter).
- Kernel and initramfs ship with the root disk image as one versioned Linux engine asset.
- If boot time misses the budget, the kernel can be replaced by an own LTS build with a pinned config; only the kernel asset changes.

## dockyard-vm (Swift helper)

- Signed with the `com.apple.security.virtualization` entitlement, in `Dockyard.app/Contents/MacOS/`.
- Started by the tray as a child process; the VM lives in this process. Quitting the tray sends a stop: the guest gets an ACPI-style stop request through the agent, dockerd shuts down, the VM stops; after 20 seconds it is force-stopped.
- Control socket `~/.dockyard/run/vm.sock`, newline-delimited JSON: `status` (state, uptime, memory), `stop`, `config` (applies on next start).
- VM configuration:

| Device | Configuration |
|---|---|
| Boot | `VZLinuxBootLoader` with kernel, initramfs, `console=hvc0 root=/dev/vda ro quiet` |
| Root disk | read-only `root.img` (ext4, aarch64 `dockyard-linux`) inside the app bundle |
| Data disk | `~/.dockyard/data.img`, sparse raw ext4, 64 GB max, mounted at `/var/lib/docker` |
| Network | `VZNATNetworkDeviceAttachment` (one NIC) |
| File sharing | virtiofs shares `/Users`, `/Volumes`, `/private/tmp`, `/private/var/folders`, mounted at the same paths in the guest |
| Rosetta | `VZLinuxRosettaDirectoryShare`, registered via binfmt_misc so amd64 images run |
| Other | virtio-vsock, traditional memory balloon, entropy, serial console to `~/.dockyard/logs/vm-console.log` |

- CPU: all cores by default. Memory: maximum `min(half of physical RAM, 8 GB)` by default. Both configurable in Settings.

## dockyard-agent (guest)

Started by `dockyard-init` before dockerd. Talks to the helper over vsock.

- **Docker socket:** vsock port 2375 → `/var/run/docker.sock`, one vsock stream per client connection.
- **Ports:** follows `/events` and container inspect; reports published ports (protocol, host IP, host port) to the helper whenever containers start or stop; vsock port 2376 carries forwarded TCP/UDP streams, which the agent connects to on `127.0.0.1:<port>` inside the guest.
- **Memory:** reports `MemTotal`/`MemAvailable` every 5 seconds.
- **Clock:** on a wake notification from the helper, sets the guest clock from the host time.
- **Disk:** `fstrim /var/lib/docker` every hour and after image prunes, so the sparse data disk shrinks.

## Host side

- **Docker socket:** the helper listens on `~/.dockyard/docker.sock` and pipes every connection to vsock 2375. Docker context `dockyard` points at `unix://$HOME/.dockyard/docker.sock`.
- **Ports:** for each reported port the helper listens on `127.0.0.1:<port>` (or all interfaces when the container publishes on `0.0.0.0`) and pipes connections to vsock 2376. A port already taken on the Mac is logged and shown in the tray's log, not retried.
- **Memory balloon:** every 5 seconds the helper sets the balloon target so the guest keeps `MemAvailable` between 512 MB and 1 GB of headroom above its usage; free memory beyond that goes back to macOS. Memory pressure inside the guest deflates the balloon immediately.
- **Sleep/wake:** the helper listens for `NSWorkspace.didWakeNotification` and tells the agent to resync the clock.
- **Optional `/var/run/docker.sock`:** a Settings toggle creates a symlink to the Dockyard socket for tools that hard-code it (one admin prompt).

## MacHost

- One engine, id `linux`, context `dockyard`. Start: launch `dockyard-vm`; ready when `/_ping` answers on the socket. Stop: `stop` on the control socket.
- **Install** (one `osascript` admin prompt):
  - docker CLI from `download.docker.com/mac/static/stable/aarch64/`, buildx and compose `darwin-arm64` plugins, into `~/.dockyard/bin` and `~/.docker/cli-plugins`;
  - `/etc/paths.d/dockyard` containing `~/.dockyard/bin` (expanded), so new shells find `docker`;
  - create `~/.dockyard/data.img` and the `dockyard` context, make it current;
  - LaunchAgent `~/Library/LaunchAgents/com.byhofman.dockyard.plist` for tray autostart at login.
- **Updates screen components:** Linux engine (kernel + root image + Docker, one version), docker CLI, compose, buildx, Dockyard.
- **Uninstall:** removes the LaunchAgent, `/etc/paths.d/dockyard`, the context, the CLI; asks whether to delete `~/.dockyard/data.img` (all images and containers).

## Budgets

| Metric | Budget |
|---|---|
| Start → engine answers `/_ping` | < 3 s |
| VM CPU while idle (no containers) | < 0.5% of one core |
| Memory returned to macOS when idle | VM resident memory ≤ guest used memory + 1 GB, within 30 s after load drops |
| Bind-mount read throughput | ≥ 50% of native |

## Testing

- **CI (GitHub macOS runners, no nested virtualization):** `swift test` for config building, proxy framing and the balloon policy; agent unit tests (event → port list, memory parsing); image and kernel build; signing with an ad-hoc identity to check the entitlement is present.
- **End-to-end on the user's Apple-silicon Mac** (manual script `scripts/mac-e2e.sh`, later a self-hosted runner): fresh install, start under budget, `docker run hello-world`, bind mount from `$HOME` (read and write), `-p 8080:80` reachable on `127.0.0.1:8080`, `--platform linux/amd64` image via Rosetta, `docker compose up`, buildx build, sleep/wake clock check, balloon returns memory after a large container exits, stop, uninstall.

## Implementation notes

- The root disk is not read-only inside the bundle: `dockyard-vm prepare` clones `root.img` from the app (APFS copy-on-write) to `~/.dockyard/root.img` whenever the bundled `dockyard-release` changes, and the VM boots it read-write. The signed bundle stays untouched and the data disk is never replaced.
- All vsock connections are opened by the helper (docker 2375, forwards 2376, control 2377), so the host needs no vsock listener; the agent pushes ports and memory over the control connection.
- The `/var/run/docker.sock` symlink is documented as a manual command instead of a Settings toggle for now.
- Verification without a Mac: the x86_64 build of the VM assets boots under QEMU/KVM in CI (`image/test-vm.sh`: API through the agent, container run, port forward with half-close, control messages, poweroff, data persistence); `DockyardVMCore` and its socket proxies are tested with `swift test` on Linux; the Virtualization.framework code builds and is entitlement-signed in the `macos-14` job; `scripts/mac-e2e.sh` covers the rest on a real Mac.
