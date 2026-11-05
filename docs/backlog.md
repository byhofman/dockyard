# Backlog

Every requirement, grouped by type. `## Next` is the slice being prototyped or built now.
IDs are permanent: never renumber or reuse one.

## Next

## Later

- [ ] DOK-1 `linux-arm64` and `win-arm64` builds
- [ ] DOK-2 Tray popover with container quick actions
- [ ] DOK-3 Embedded terminal in the dashboard
- [ ] DOK-4 Compose editor
- [ ] DOK-5 File browser inside containers
- [ ] DOK-6 Registry login UI (`docker login` on the CLI keeps working)
- [ ] DOK-7 `host.docker.internal` reaches the host from Linux containers
- [ ] DOK-8 `localhost` reaches published Linux container ports on Windows
- [ ] DOK-9 Tools that ignore docker contexts find the engine on macOS without manual setup (`/var/run/docker.sock`, Testcontainers)
- [ ] DOK-10 `docker login` stores registry credentials in the OS credential store
- [ ] DOK-11 Servers in `--net host` containers are reachable from macOS
- [ ] DOK-12 Engine configuration on the Linux engine on macOS and WSL (registry mirrors, insecure registries, address pools)
- [ ] DOK-13 Import containers, images and volumes from Docker Desktop
- [ ] DOK-14 Engine and containers follow the host's VPN, DNS and proxy settings, tested on each OS
- [ ] DOK-15 Zero-config domain names for containers and Compose services, without port numbers, with a way to pick the port when detection is wrong
- [ ] DOK-16 Automatic trusted HTTPS for those domain names
- [ ] DOK-17 Files in volumes can be viewed and edited from the host
