# Dockyard macOS VM Implementation Plan (C)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans.

**Goal:** Linux containers on Apple-silicon Macs through a Virtualization.framework VM: Swift helper `dockyard-vm`, guest agent `dockyard-agent`, VM variant of the `dockyard-linux` image, and a real `MacHost`.

Spec: `docs/superpowers/specs/2026-11-04-dockyard-macos-vm-design.md`.

## Verification strategy (no Mac on the build machine)

- VM image: the same Dockerfile stage built for x86_64 and booted with QEMU + KVM in a container (`image/test-vm.ps1`): init mounts, data disk formatting, DHCP, dockerd, and the agent's docker socket proxy (TCP test mode, forwarded by QEMU) answering `/_ping` and running a container.
- Agent: unit tests for /proc/meminfo parsing, published ports from containers, forward headers and UDP framing; vsock is the only untested transport (TCP test mode shares all code above the socket).
- Swift helper: `DockyardVMCore` (settings, balloon policy, port reconciliation, control protocol) builds and tests with `swift test` in the `swift:6.1` Linux container; the Virtualization.framework target builds in CI on `macos-14`.
- End to end on the user's Mac with `scripts/mac-e2e.sh`.

## Decisions taken while planning

- The VM boots read-write from `~/.dockyard/root.img`, an APFS clone of the image in the app bundle (the bundle stays untouched and signed); updates replace the clone, the data disk (`~/.dockyard/data.img`, `/var/lib/docker`) is separate.
- Guest boot uses Alpine's busybox init with `/etc/inittab` → `dockyard-vm-init` (WSL ignores both).
- Control channel: the helper opens all vsock connections to the agent (ports 2375 docker, 2376 forwards, 2377 control), so no listener is needed on the host side.

## Tasks

- C1 VM image: Dockerfile stage `vm` (linux-virt kernel + modules, mkinitfs initramfs, inittab, `dockyard-vm-init`), `image/build-vm.ps1` → `vmlinuz`/`Image`, `initramfs`, `root.img`; `image/test-vm.ps1`.
- C2 Agent: `src/Dockyard.Agent` (.NET AOT, linux-musl), `tests/Dockyard.Agent.Tests`.
- C3 Swift helper: `macos/DockyardVM` package (`DockyardVMCore` + `dockyard-vm`), entitlements, `swift test`.
- C4 MacHost + CLI install + LaunchAgent + `scripts/mac-e2e.sh`; CI jobs for the aarch64 VM assets and the helper.
