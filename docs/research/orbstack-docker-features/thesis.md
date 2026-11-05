---
title: OrbStack Docker features for Dockyard
summary: Eleven new must-haves plus two existing backlog items, separated from nice-to-haves and skips, with the evidence and limits behind each.
covers: [DOK-2, DOK-5, DOK-7, DOK-8, DOK-9, DOK-10, DOK-11, DOK-12, DOK-13, DOK-14, DOK-15, DOK-16, DOK-17]
---

# OrbStack Docker features for Dockyard

## 1. The question

**Which Docker-related OrbStack features must Dockyard have?**

This research was not asked about Kubernetes or VM features (Linux machines, isolated machines, USB, sound, VM limits), about speed, or about how to build anything. Speed claims were not tested, and no benchmark exists here.

"Must-have" is a judgement, not something a source can settle. This programme used a stated test, so a reader can disagree with a specific call:

- **Workflow test:** an ordinary Docker workflow that works on OrbStack fails on Dockyard, needs manual setup, or is unsafe by default.
- **Differentiator test:** OrbStack promotes the feature against Docker Desktop, it is on OrbStack's free tier, there is evidence beyond OrbStack's own marketing that people notice it, and the plain Docker CLI offers no comparable route.

A feature passing either test is a must-have. A useful feature that passes neither is a nice-to-have. A feature with no demand signal, a poor fit or a real risk is a skip.

## 2. The answer

Dockyard already matches OrbStack on the Docker basics, so the must-haves are in two groups. Eight are compatibility gaps that make an ordinary workflow fail or need manual setup: `host.docker.internal`, `localhost` on Windows, a default socket for tools on macOS, registry logins in the OS keychain, `--net host` on macOS, engine configuration, import from Docker Desktop, and following the host's VPN, DNS and proxy. Five are what sets OrbStack apart: container domains, automatic HTTPS, volume file access, and the existing `DOK-2` tray actions and `DOK-5` container files. Debug Shell, OrbStack's only paid Docker feature, is a nice-to-have. The OrbStack side was largely observed on version 2.2.5, but Dockyard was read and not run, and nothing shows that domains and HTTPS can work the same way on Windows and Linux, because OrbStack only runs on macOS. VPN and DNS on Dockyard are untested, not known to be broken.

## 3. What the evidence establishes

### Must-have

| ID | Feature | Dockyard today | Evidence | Reason |
|---|---|---|---|---|
| `DOK-7` | `host.docker.internal` reaches the host | Absent ([F-02-02](findings/02-dockyard-today.md)), `source` | OrbStack `verified` ([F-01-03](findings/01-orbstack-inventory.md)); Docker Desktop `documented` ([F-03-01](findings/03-gaps-and-demand.md)) | Workflow test: both products a user would leave offer it automatically, and tooling and tutorials assume it. |
| `DOK-8` | `localhost` reaches published ports on Windows | Hangs, per the README ([F-02-04](findings/02-dockyard-today.md)), `source` | OrbStack docs: "available on localhost" ([F-01-05](findings/01-orbstack-inventory.md)), `documented` | Workflow test: the most common URL a developer types hangs. |
| `DOK-9` | Tools find the engine without setup on macOS | Manual symlink, and Testcontainers pinned on Windows only ([F-02-05](findings/02-dockyard-today.md)), `source` | OrbStack `verified` ([F-01-03](findings/01-orbstack-inventory.md)) | Workflow test: tools that ignore contexts need manual setup. |
| `DOK-10` | Registry logins go to the OS credential store | Never configured ([F-02-06](findings/02-dockyard-today.md)), `source` | OrbStack `verified` ([F-01-03](findings/01-orbstack-inventory.md)); Docker: base64 without a store ([F-03-02](findings/03-gaps-and-demand.md)), `normative` | Workflow test, unsafe by default. Dockyard is meant to be public, so this matters more than convenience. |
| `DOK-11` | `--net host` servers reachable on macOS | Not forwarded ([F-02-03](findings/02-dockyard-today.md)), `source` | OrbStack `verified` ([F-01-03](findings/01-orbstack-inventory.md)); Docker Desktop opt-in `documented` ([F-03-01](findings/03-gaps-and-demand.md)) | Workflow test: the command succeeds, yet the server cannot be reached. |
| `DOK-12` | Engine configuration on the Linux engine | No supported route on macOS or WSL ([F-02-07](findings/02-dockyard-today.md)), `source` | OrbStack `documented` ([F-01-05](findings/01-orbstack-inventory.md)) | Workflow test: private registries, mirrors and subnet clashes cannot be handled, with no route at all on macOS if the root disk is read-only as specified. |
| `DOK-13` | Import from Docker Desktop | Absent; the README covers only a manual image copy from Dockyard's old engine ([F-02-08](findings/02-dockyard-today.md)), `source` | OrbStack `documented` ([F-01-06](findings/01-orbstack-inventory.md)) | Workflow test: volumes hold data that cannot be pulled again, and switching means moving them by hand. |
| `DOK-14` | Follows the host's VPN, DNS and proxy | Proxy absent, VPN and DNS untested ([F-02-08](findings/02-dockyard-today.md)), `source` | OrbStack `documented` ([F-01-05](findings/01-orbstack-inventory.md)); Docker Desktop proxy `documented` ([F-03-01](findings/03-gaps-and-demand.md)) | Workflow test: behind a proxy or VPN, pulls and builds can fail outright. The README's Intune notes show managed devices are a real audience. |
| `DOK-15` | Zero-config domains for containers and Compose services | Absent ([F-02-08](findings/02-dockyard-today.md)), `source` | OrbStack `verified` ([F-01-01](findings/01-orbstack-inventory.md)); demand `documented` ([F-03-05](findings/03-gaps-and-demand.md)); noted by a third party, `claimed` ([F-03-07](findings/03-gaps-and-demand.md)) | Differentiator test. The maintenance cost is real ([F-03-06](findings/03-gaps-and-demand.md)). |
| `DOK-16` | Automatic trusted HTTPS for those domains | Absent ([F-02-08](findings/02-dockyard-today.md)), `source` | OrbStack `verified` ([F-01-02](findings/01-orbstack-inventory.md)); browser rule `normative` ([F-03-03](findings/03-gaps-and-demand.md)) | Differentiator test. A non-loopback HTTP name loses secure-context browser APIs. |
| `DOK-17` | Files in volumes reachable from the host | Absent, and not on the backlog ([F-02-09](findings/02-dockyard-today.md)), `source` | OrbStack `verified` ([F-01-04](findings/01-orbstack-inventory.md)); positioning `claimed` ([F-03-04](findings/03-gaps-and-demand.md)) | Differentiator test, weakest of the set: independent evidence is thin, and a helper container is a workaround. Kept because volumes on macOS sit inside a VM disk. |
| `DOK-5` (existing) | Files in containers reachable from the host | On the backlog ([F-02-09](findings/02-dockyard-today.md)), `source` | OrbStack `verified` ([F-01-04](findings/01-orbstack-inventory.md)) | Same reason as `DOK-17`. |
| `DOK-2` (existing) | Container actions from the tray | On the backlog ([F-02-09](findings/02-dockyard-today.md)), `source` | OrbStack `documented` ([F-01-07](findings/01-orbstack-inventory.md)); issue #14, 23 thumbs-up ([F-03-05](findings/03-gaps-and-demand.md)) | Differentiator test. |

### Nice-to-have

| Feature | Evidence | Reason |
|---|---|---|
| Debug Shell for distroless containers | `documented`, Pro only ([F-01-08](findings/01-orbstack-inventory.md)) | Dockyard's terminal action needs a shell in the image, so distroless images cannot use it. OrbStack charges for this, so it is not a free-tier expectation, and no issue reached the top 25. |
| Custom domains, wildcards, per-domain ports | Label form `verified` ([F-01-01](findings/01-orbstack-inventory.md)); #1165, 34 thumbs-up ([F-03-05](findings/03-gaps-and-demand.md)) | Extends `DOK-15`, which has to exist first. |
| Direct container IP access | `verified` ([F-01-01](findings/01-orbstack-inventory.md)); #33, 39 thumbs-up ([F-03-05](findings/03-gaps-and-demand.md)) | Real demand, but ports and domains cover the same need. |
| Files in images | `verified` ([F-01-04](findings/01-orbstack-inventory.md)) | Read-only debugging; `docker save` and a throwaway container already work. |
| Volume clone, export and import | `documented`, CLI help observed ([F-01-04](findings/01-orbstack-inventory.md)) | Useful for backups; a helper container can do it. |
| SSH agent forwarding | `documented` ([F-01-09](findings/01-orbstack-inventory.md)), Docker Desktop too ([F-03-01](findings/03-gaps-and-demand.md)) | Matters for running containers that use private Git; no issue reached the top 25. |
| Root CA added inside containers | `verified` ([F-01-02](findings/01-orbstack-inventory.md)) | Only matters once `DOK-16` exists and containers call each other over HTTPS. |
| Build history; JSON log highlighting; Compose log filter by service | `documented` ([F-01-07](findings/01-orbstack-inventory.md)) | Dashboard polish; the CLI and current logs work. |
| Overall resource view (`orb top`) | `documented` ([F-01-09](findings/01-orbstack-inventory.md)) | The dashboard already shows CPU and memory per container ([F-02-01](findings/02-dockyard-today.md)). |
| IPv6 option; containerd image store; SOCKS or custom proxy | `documented` ([F-01-05](findings/01-orbstack-inventory.md), [F-01-09](findings/01-orbstack-inventory.md)) | Niche. `DOK-12` and `DOK-14` may cover part of this. |
| Embedded terminal (`DOK-3`, existing) | Backlog ([F-02-09](findings/02-dockyard-today.md)) | The external terminal works. It stays in scope as a backlog item, unchanged. |

### Skip

| Feature | Evidence | Reason |
|---|---|---|
| CRIU checkpoint and restore | `documented`, added v2.2.0 ([F-01-09](findings/01-orbstack-inventory.md)) | Experimental in Docker, recent, and no demand signal found. |
| Engine exposed over the network | `documented` ([F-01-09](findings/01-orbstack-inventory.md)) | OrbStack's docs call it "extremely dangerous". |
| Remote Debug Shell | `documented` ([F-01-08](findings/01-orbstack-inventory.md)) | Dockyard is a local desktop tool. |
| 32-bit Arm and Intel emulation | `documented` ([F-01-09](findings/01-orbstack-inventory.md)) | Rare, with no demand signal; amd64 is already covered. |
| Custom icon labels; one-click Supabase | `documented` ([F-01-09](findings/01-orbstack-inventory.md)) | Cosmetic or tied to one product. |
| Organisations, SAML, licence management | `documented` ([F-01-08](findings/01-orbstack-inventory.md)) | These exist to sell seats, and Dockyard is free. |

Podman, `docker sandbox` and Docker MCP Gateway draw large demand on OrbStack's tracker ([F-03-05](findings/03-gaps-and-demand.md)), but they are not OrbStack features, so they are not classified here.

## 4. Limits

**What this does not establish**

- That any must-have works on Windows or Linux. OrbStack only runs on macOS, so its evidence is macOS evidence.
- How Dockyard behaves at runtime. Every Dockyard claim comes from reading the code and README at `b227337`. Absences are search results over named paths.
- How much users value each feature. Reactions count requests, not use, and features that shipped early and work collect few.
- That VPNs fail on Dockyard. `DOK-14` is partly a test item.

**Where this is weak**

- The classification is a judgement from a stated test. `DOK-17`, `DOK-13` and `DOK-11` are the closest calls; a stricter reader could move them to nice-to-have.
- No user survey, Reddit or X search was done. Independent evidence is one vendor-adjacent blog and tracker counts.
- Several Docker and MDN pages were read through a summarising tool, not directly, because of a tool limit.
- The OrbStack probes used a one-request-at-a-time test server, which may explain the early connection failures noted in [F-01-01](findings/01-orbstack-inventory.md) and [F-01-02](findings/01-orbstack-inventory.md).
- A survey showing that switchers do not use domains or HTTPS would move `DOK-15` and `DOK-16` down. A failed Windows spike would change their scope.

## 5. Contradictions

- **"No admin required" versus the socket.** OrbStack's comparison lists no admin required, while its docs create `/var/run/docker.sock` only with admin access. Both hold if the socket is treated as optional.
- **"Zero-config" versus the first request.** The docs describe instant zero-config domains; here the first requests failed before later ones worked. The cause is not established.
- **Host networking on Docker Desktop.** OrbStack's comparison ticks it, and its host-networking page says other macOS providers lack it "out of the box". Docker's docs say it is supported from 4.34 as an opt-in. These fit if "out of the box" means enabled by default.
- **SSH agent "requires special path".** OrbStack's comparison says this of Docker Desktop, but OrbStack's own docs use the same path, and recommend it for 1Password.

## 6. What this licenses

- A build may treat `host.docker.internal` as a baseline expectation: `DOK-7`.
- A build may treat `localhost` on Windows as a defect to close: `DOK-8`.
- A build may treat manual tool setup on macOS as a gap: `DOK-9`.
- A build may treat unencrypted registry logins as a security gap: `DOK-10`.
- A build may treat `--net host` on macOS as a compatibility gap: `DOK-11`.
- A build may treat engine configuration on macOS and WSL as a gap: `DOK-12`.
- A build may claim import from Docker Desktop only for what it actually verifies moved: `DOK-13`.
- A build may claim VPN, DNS and proxy compatibility only after testing it per OS: `DOK-14`.
- A build may treat zero-config domains as competitive parity with OrbStack on macOS: `DOK-15`.
- A build may say non-loopback HTTP names lose secure-context browser APIs: `DOK-16`.
- A build may treat host-side file access as a stated OrbStack differentiator: `DOK-5`, `DOK-17`.
- A build may treat tray container actions as parity with OrbStack: `DOK-2`.
- No build may claim from this research that Dockyard is as fast or as light as OrbStack.

## 7. Sources

See [sources.md](sources.md). Still to obtain: The New Stack and Arcjet articles at full depth, plus any user survey or Reddit and X discussion.
