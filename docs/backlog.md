# Backlog

Every requirement, grouped by type. `## Next` is the slice being prototyped or built now.
IDs are permanent: never renumber or reuse one.

## Next

## Images and volumes

- [x] DOK-36 Browse an image's files: a read-only tree with folder sizes, small text files shown, any file saved to the computer (owner 2026-11-17, v1; numbered DOK-30 at first, which "Report an issue" already had)
- [x] DOK-31 Browse a volume's files in the same viewer, read-only unless changes are asked for (owner 2026-11-17, v1)
- [x] DOK-32 Open a volume's folder in the OS file manager where the host can reach it (Explorer through WSL on Windows, the folder on Linux when it is readable), and an honest explanation plus "Export a copy" where it cannot (macOS, root-owned folders, other machines) (owner 2026-11-17, v1)
- [x] DOK-33 A terminal into an image: a temporary container of it with its shell (bash, else sh, a clear message without one), removed when the terminal closes, in the same terminal app as a container's (owner 2026-11-17, v1)

## Install and uninstall

- [x] DOK-19 On a Linux host, the agent's iptables rules and `lo` addresses are gone after uninstall without a reboot (pulled in from Later by the owner, 2026-11-07, SCOPE.md) — v0.4.2
- [x] DOK-20 On macOS, uninstall removes the CA trust setting along with the certificate (pulled in from Later by the owner, 2026-11-07, SCOPE.md; from the window and the wizard, where a person can approve it; a silent uninstall leaves it and says so)
- [ ] DOK-41 A silent install of just the engine and the docker CLI, without the Toolbox, dashboard or tray, for a machine like Windows Server: one line in the README per OS (owner 2026-11-25, a version after 0.9.0)
- [ ] DOK-42 Resource saver: when no container has run for a few minutes, the engine drops Linux's page cache so the WSL VM (vmmemWSL) and the Mac VM give the memory back; optionally the engine stops after a longer idle time, and starts again on the next docker command. Never by changing the user's .wslconfig (owner 2026-11-25, a version after 0.9.0)
- [ ] DOK-43 Declared domains: a domain of your own outside dockyard.internal (such as dev.example.com) that clusters publish to through external-dns and Dockyard answers for, approved once per domain, with a root CA of its own constrained to that domain, and a warning when the domain exists publicly (owner 2026-11-25, a version after 0.9.0 unless pulled in)

## Networking

- [x] DOK-24 On a Linux host whose daemon.json already names DNS servers, containers still get `host.docker.internal` and `dockyard.test` names, and their other lookups keep going to those servers (flaky wizard-linux e2e, 2026-11-13)
- [x] DOK-21 Direct `[::1]` on a published port gets an answer, or the limit is documented if the fix is not small (pulled in from Later by the owner, 2026-11-07, SCOPE.md) — v0.4.2

## Kubernetes

- [x] DOK-22 Basic kind support: kind installed or detected, versions pinned and checked; create, list and delete clusters, get a cluster's kubeconfig and context; clusters in the dashboard and the CLI, on Windows, macOS and Linux (owner, feedback round 1, 2026-11-12)
- [x] DOK-34 LoadBalancer services in kind clusters get an address on 127.0.0.1 through cloud-provider-kind, run by Dockyard while there are clusters, with a switch to turn it off (owner 2026-11-23)
- [x] DOK-35 Shared storage for kind clusters: a `shared` ReadWriteMany storage class over one folder on the engine that every node mounts, chosen per cluster when it is made (owner 2026-11-23, per cluster 2026-11-24)
- [ ] DOK-37 A basic look into a cluster from the dashboard: deployments, pods, services, ingresses, persistent volume claims and persistent volumes, listed per namespace with their state (owner 2026-11-25, a version after 0.9.0)
- [ ] DOK-38 A pod's logs and a terminal into it from the dashboard, the same way as for a Docker container (owner 2026-11-25, a version after 0.9.0)
- [ ] DOK-39 The Helm releases installed in a cluster, with their chart, version and status (owner 2026-11-25, a version after 0.9.0)
- [ ] DOK-40 Images built locally are usable in kind clusters without pushing them anywhere: loaded into a cluster (kind load) from the dashboard and the CLI, or a local registry every cluster pulls from (owner 2026-11-25, a version after 0.9.0)
- [ ] DOK-44 Edit a running cluster: the create dialog's add-ons (metrics-server, Traefik, cert-manager with its issuer, Headlamp), external-dns, NFS storage and the load balancer changed on a running cluster through Helm and manifests, in the right order, with its definition saved; host-folder storage and the kind config greyed out as needing Recreate; dockyard cluster edit for the same; plus the CLI's dashboard-token and a real-cluster test of Headlamp (feat/headlamp) (owner 2026-11-26, next)
- [x] DOK-45 The one-liner opened the installer window, but it offered no choice of what to install: the component checkboxes the wizard shows on a first install should be there whenever the one-liner opens it, also over an existing install, so a run of it is a way to add or leave out components (owner 2026-11-26; done: over an existing install the boxes start from what is there)
- [ ] DOK-46 Measure before rewriting: profile the dashboard (polling loops, stats sampling, docker CLI calls it could make through the API), the trimmed Toolbox's startup and the agent, and fix what costs; a rewrite in Rust was considered and set aside, since what is slow is engine work, pulls and kind, not .NET (owner 2026-11-26)
- [x] DOK-47 Manage a cluster's node containers in the Kubernetes tab itself: a node row is selectable on its own and opens the same detail as in Containers (logs, terminal, files, inspect, stats), with start, stop and restart, so there is no switching to Containers; today only the cluster row selects (owner 2026-11-26; done differently: the Kubernetes tab is gone and clusters and their nodes are rows in Containers)
- [ ] DOK-48 Time-locked scrolling in the log panel: with logs side by side, scrolling one scrolls the others to the same moment by timestamp, so what happened across containers at one time lines up (owner 2026-11-26)

## Quality

- [x] DOK-23 MacHost, LinuxHost, WindowsHost and MachineSetup each split by duty (install and uninstall, the running engine, trust and certificates); each is 450 to 650 lines doing all three (refactor pass, 2026-11-13)
- [x] DOK-18 `ComponentRulesTests.Rows_say_why_something_is_required_and_only_optional_ones_can_change` no longer fails intermittently on Windows (pulled in from Later by the owner, 2026-11-07, SCOPE.md) — v0.4.2

## Later

- [ ] DOK-1 `linux-arm64` and `win-arm64` builds
- [x] DOK-2 Tray popover with container quick actions — v0.4.0
- [ ] DOK-3 Embedded terminal in the dashboard
- [x] DOK-4 Compose editor: a compose project's file in the dashboard, checked as it is typed, saved and applied with `docker compose up -d` — 0.9.0
- [x] DOK-5 File browser inside containers — v0.4.0
- [ ] DOK-6 Registry login UI: manage registries from the dashboard, logging in and out with `docker login`, which keeps working on the CLI (owner asked again 2026-11-25, a version after 0.9.0)
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
- [x] DOK-25 Branding pass: Dockyard's logo colour as a small hint in the interface (owner 2026-11-14: "branding is v1 for every app, as well is feedback")
- [x] ~~DOK-26 Feedback from inside the app: support, a feature request or a bug report through the platform's feedback client (ACC-147), without an account where the app has none, and never sending the user's files (owner 2026-11-14: "branding is v1 for every app, as well is feedback")~~ cut by the owner 2026-11-15: "Dockyard and Vaultbar dont do in app feedback!!! No free tool does"
- [x] DOK-27 The interface made a bit nicer: a light design pass over the dashboard and wizard (spacing, hierarchy, states, the kind clusters view), keeping it featherlight (owner 2026-11-14, v1) — restyle 2026-11, `docs/design/restyle-2026-11.md`
- [x] DOK-28 Creating a kind cluster adds its context, cluster and user to the host's ~/.kube/config (or $KUBECONFIG) automatically, reachable from the host, and deleting the cluster removes exactly those entries again, leaving the rest and a sane current-context; today this is the manual "Use context" button (kind export kubeconfig) (owner 2026-11-14, v1)
- [x] DOK-29 Monitoring (owner 2026-11-14, v1): the download page and the release downloads are watched for being up, with an alert; no telemetry from the app itself — gitops#185: page, `releases/latest`, three assets, `SHA256SUMS` and the three installers probed every two minutes, `DockyardDownloadDown` in Grafana
- [ ] DOK-30 "Report an issue" in the app (help menu and the CLI's help) opens a new GitHub issue on byhofman/dockyard, with an issue template that asks for the version, OS and steps, and the app's version and OS filled in; the README says the same (owner 2026-11-15: "for dockyard and vaultbar people should just raise a github issue"; v1)
