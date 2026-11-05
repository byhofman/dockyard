# 3. Which gaps break a workflow, and which do people value

**Deelvraag:** Which gaps break an ordinary workflow, and which do people value?
**Bar:** `market` for the baseline and demand, `technical` for browser and Docker rules.
**Passes run 2026-11-05:** currency (each page's date is recorded where one is shown) and verification (third-party comparisons are treated as positioning only).

**How demand was searched:** OrbStack's GitHub tracker, sorted by thumbs-up over all issues. Queries for labels `enhancement` and `feature` returned nothing, because the tracker uses `t/feature`, and the `-label:bug` filter excluded nothing for the same reason. So the top 25 is every issue, bugs included. The Hacker News search (Algolia, recent comments) produced nothing usable. Reddit and X were not searched.

#### F-03-01 · Docker Desktop also provides several of these, which makes them a baseline

| | |
|---|---|
| **Claim** | Docker Desktop resolves `host.docker.internal` automatically, supports SSH agent forwarding through a special path, honours system or manual proxy settings, sets up a credential store, and offers host networking from 4.34 as an opt-in setting. |
| **Bar / Grade** | `market` · **documented** |
| **Sources** | [Networking how-tos](https://docs.docker.com/desktop/features/networking/networking-how-tos/), [Networking](https://docs.docker.com/desktop/features/networking/), [Host driver](https://docs.docker.com/engine/network/drivers/host/), [docker login](https://docs.docker.com/reference/cli/docker/login/), [docker run](https://docs.docker.com/reference/cli/docker/container/run/) — read: `full`, via a summarising fetch tool; quotes checked against that output only |
| **Population / Scope** | Current Docker docs, read 2026-11-05. |
| **Licenses** | Calling these features a baseline for anyone leaving Docker Desktop. |
| **Does NOT support** | That they work identically across the two products. |
| **Requirements** | `DOK-7`, `DOK-10`, `DOK-11`, `DOK-14` |
| **Open** | None. |

#### F-03-02 · Without a helper, `docker login` stores credentials in a readable form

| | |
|---|---|
| **Claim** | Docker's docs say that with no credential store, credentials are kept base64-encoded in `config.json`, and that an external store is more secure. |
| **Bar / Grade** | `technical` · **normative** (vendor reference) |
| **Sources** | [docker login](https://docs.docker.com/reference/cli/docker/login/) — read: `full`, via a summarising fetch tool |
| **Population / Scope** | Current docs, read 2026-11-05. |
| **Licenses** | Treating the absence of a store as a security gap, not a convenience gap. |
| **Does NOT support** | That Dockyard installs currently write this on every OS; see F-02-06. |
| **Requirements** | `DOK-10` |
| **Open** | None. |

#### F-03-03 · Plain HTTP on a `.local` name is not a secure context

| | |
|---|---|
| **Claim** | Browsers treat HTTPS and loopback hosts, including `localhost` and `*.localhost`, as secure. Other plain HTTP origins are not. OrbStack gives this as its reason for automatic HTTPS. |
| **Bar / Grade** | `technical` · **normative** (MDN, last modified 14 Sep 2026); OrbStack's stated reason is `market` · **claimed** |
| **Sources** | [MDN: Secure contexts](https://developer.mozilla.org/en-US/docs/Web/Security/Secure_Contexts), [OrbStack 1.1 announcement](https://orbstack.dev/blog/orbstack-1.1-https) — read: `full`, via a summarising fetch tool |
| **Population / Scope** | Browser rules as documented on 2026-11-05. |
| **Licenses** | Saying that domains on a non-loopback name without HTTPS lose secure-context browser APIs. |
| **Does NOT support** | That HTTPS is required for every possible naming scheme: a `*.localhost` name is a secure context over HTTP. |
| **Requirements** | `DOK-15`, `DOK-16` |
| **Open** | The naming choice, at prototype or build. |

#### F-03-04 · OrbStack presents domains, HTTPS and file access as what sets it apart from Docker Desktop

| | |
|---|---|
| **Claim** | Its comparison marks container domains, automatic HTTPS, volume and image file access, and containers in the menu bar as OrbStack yes, Docker Desktop no. Colima also lacks domains, HTTPS and file access. |
| **Bar / Grade** | `market` · **claimed** |
| **Sources** | [vs Docker Desktop, January 2025](https://docs.orbstack.dev/compare/docker-desktop), [vs Colima, undated](https://docs.orbstack.dev/compare/colima) — read: `full` |
| **Population / Scope** | A vendor's own, partly stale comparison. |
| **Licenses** | A statement about OrbStack's positioning only. |
| **Does NOT support** | Any statement about what Docker Desktop does today, or that users choose OrbStack for these. |
| **Requirements** | `DOK-2`, `DOK-5`, `DOK-15`, `DOK-16`, `DOK-17` |
| **Open** | None. |

#### F-03-05 · The public tracker shows some demand for these features, and much more for things outside this question

| | |
|---|---|
| **Claim** | Thumbs-up on 2026-11-05: direct container IP (#33) 39, per-domain ports for SSL (#1165) 34, domains to LAN (#814) 31, container stats (#379) 30, `orb.local` broken after a macOS update (#1984) 29, menu bar (#14) 23. Far larger: Podman (#88) 570, custom VM images (#11) 413, Kubernetes (#1) 390, `docker sandbox` (#2295) 237, Docker MCP Gateway (#2056) 97. No Debug Shell, checkpoint or SSH agent issue reached the top 25. |
| **Bar / Grade** | `market` · **documented** (public tracker counts) |
| **Sources** | [orbstack/orbstack issues](https://github.com/orbstack/orbstack/issues), via `gh api search/issues` — read: titles and counts only |
| **Population / Scope** | Users who file or upvote issues, over the tracker's lifetime. |
| **Licenses** | Saying people asked for or care about direct IP, domain routing and the menu bar, and that domains can break. |
| **Does NOT support** | A ranking of feature value. Reactions measure requests, so features that shipped in the first place and work well collect none. Absence from the top 25 does not show low use. |
| **Requirements** | `DOK-2`, `DOK-15`, `DOK-16` |
| **Open** | A user survey would be better evidence. The largest requests are not OrbStack features, so they sit outside this question. |

#### F-03-06 · Domain routing has a visible maintenance cost

| | |
|---|---|
| **Claim** | Release notes in 2026 repeatedly fix it: domains pointing to a recreated container's old IP (v2.2.4), `compose run` containers taking over domains (v2.2.2), CNAME resolution (v2.2.0), and domains breaking after a Compose restart ("potential fix", v2.1.2). |
| **Bar / Grade** | `market` · **documented** |
| **Sources** | [Release notes](https://docs.orbstack.dev/release-notes) — read: `full` for v2.1.2 to v2.2.5 |
| **Population / Scope** | A mature product with a custom network stack, macOS only. |
| **Licenses** | Saying this feature keeps costing effort after it ships. |
| **Does NOT support** | A cost estimate for Dockyard. |
| **Requirements** | `DOK-15` |
| **Open** | None. |

#### F-03-07 · Independent write-ups put speed first and mention these features second

| | |
|---|---|
| **Claim** | A mid-2026 comparison lists automatic local domains and VPN-friendly networking as extras after speed and resource use, and notes that OrbStack is Mac-only. It reports no measurements of its own. |
| **Bar / Grade** | `market` · **claimed** |
| **Sources** | [Sliplane, about June 2026](https://sliplane.io/blog/orbstack-vs-docker) — read: `full`, via a summarising fetch tool; the author's company sells hosting. Search-result excerpts only, so not relied on: [The New Stack](https://thenewstack.io/orbstack-a-deep-dive-for-container-and-kubernetes-development/), [Arcjet on DEV](https://dev.to/david-arcjet/secure-local-nodejs-dev-servers-with-orbstack-10lo) — read: `secondary` |
| **Population / Scope** | One vendor-adjacent blog. |
| **Licenses** | Saying these features are publicly noticed. |
| **Does NOT support** | Any statement about behaviour, or that speed itself has been compared here. |
| **Requirements** | `DOK-14`, `DOK-15` |
| **Open** | Independent benchmarks; out of scope here. |
