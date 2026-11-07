# Backlog

Every requirement, grouped by type. `## Next` is the slice being prototyped or built now.
IDs are permanent: never renumber or reuse one.

## Next

## Install and uninstall

- [ ] DOK-19 On a Linux host, the agent's iptables rules and `lo` addresses are gone after uninstall without a reboot (pulled in from Later by the owner, 2026-11-07, SCOPE.md)
- [x] DOK-20 On macOS, uninstall removes the CA trust setting along with the certificate (pulled in from Later by the owner, 2026-11-07, SCOPE.md)

## Networking

- [ ] DOK-21 Direct `[::1]` on a published port gets an answer, or the limit is documented if the fix is not small (pulled in from Later by the owner, 2026-11-07, SCOPE.md)

## Quality

- [ ] DOK-18 `ComponentRulesTests.Rows_say_why_something_is_required_and_only_optional_ones_can_change` no longer fails intermittently on Windows (pulled in from Later by the owner, 2026-11-07, SCOPE.md)

## Later

- [ ] DOK-1 `linux-arm64` and `win-arm64` builds
- [x] DOK-2 Tray popover with container quick actions — v0.4.0
- [ ] DOK-3 Embedded terminal in the dashboard
- [ ] DOK-4 Compose editor
- [x] DOK-5 File browser inside containers — v0.4.0
- [ ] DOK-6 Registry login UI (`docker login` on the CLI keeps working)
- [x] DOK-7 `host.docker.internal` reaches the host from Linux containers — v0.4.0
- [x] DOK-8 `localhost` reaches published Linux container ports on Windows — v0.4.0
- [x] DOK-9 Tools that ignore docker contexts find the engine on macOS without manual setup (`/var/run/docker.sock`, Testcontainers) — v0.4.0
- [x] DOK-10 `docker login` stores registry credentials in the OS credential store — v0.4.0
- [x] DOK-11 Servers in `--net host` containers are reachable from macOS — v0.4.0
- [x] DOK-12 Engine configuration on the Linux engine on macOS and WSL (registry mirrors, insecure registries, address pools) — v0.4.0
- [x] DOK-13 Import containers, images and volumes from Docker Desktop — v0.4.0
- [x] DOK-14 Engine and containers follow the host's VPN, DNS and proxy settings, tested on each OS — v0.4.0
- [x] DOK-15 Zero-config domain names for containers and Compose services, without port numbers, with a way to pick the port when detection is wrong — v0.4.0
- [x] DOK-16 Automatic trusted HTTPS for those domain names — v0.4.0
- [x] DOK-17 Files in volumes can be viewed and edited from the host — v0.4.0
