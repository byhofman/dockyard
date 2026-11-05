# Sources

Read 2026-11-05 unless noted. The web search and fetch tools hit a session usage limit partway through, so the OrbStack docs were downloaded with `curl` and read as text. Where a source is marked "fetch tool", a summarising tool read it and the original text was not read directly.

## Observed

- OrbStack 2.2.5 (`bf84232`) on an M1 Mac: domains, HTTPS, custom label, `host.docker.internal`, socket, credential helper, `--net host`, container IP, `~/OrbStack/docker`, CLI help. Temporary containers only, removed afterwards.

## OrbStack: read `full`

- https://docs.orbstack.dev/docker/
- https://docs.orbstack.dev/docker/domains
- https://docs.orbstack.dev/docker/network
- https://docs.orbstack.dev/docker/images
- https://docs.orbstack.dev/docker/file-sharing
- https://docs.orbstack.dev/docker/host-networking
- https://docs.orbstack.dev/docker/labels
- https://docs.orbstack.dev/features
- https://docs.orbstack.dev/features/debug
- https://docs.orbstack.dev/features/https
- https://docs.orbstack.dev/features/native-files
- https://docs.orbstack.dev/menu-bar
- https://docs.orbstack.dev/efficiency
- https://docs.orbstack.dev/headless
- https://docs.orbstack.dev/licensing
- https://docs.orbstack.dev/compare/docker-desktop (January 2025)
- https://orbstack.dev/ and https://orbstack.dev/pricing
- https://docs.orbstack.dev/release-notes: v2.1.2 to v2.2.5 only

## OrbStack: read in part

- https://docs.orbstack.dev/settings: matching lines only
- https://docs.orbstack.dev/compare/colima: fetch tool
- https://orbstack.dev/blog/orbstack-1.1-https (16 Nov 2023): fetch tool
- https://github.com/orbstack/orbstack/issues: titles and counts through `gh api`

## Docker and browsers: fetch tool

- https://docs.docker.com/desktop/features/networking/
- https://docs.docker.com/desktop/features/networking/networking-how-tos/
- https://docs.docker.com/engine/network/drivers/host/
- https://docs.docker.com/reference/cli/docker/login/
- https://docs.docker.com/reference/cli/docker/container/run/
- https://developer.mozilla.org/en-US/docs/Web/Security/Secure_Contexts (last modified 14 Sep 2026)

## Third party

- https://sliplane.io/blog/orbstack-vs-docker: fetch tool, positioning only

## Dockyard: commit `b227337`

- `README.md`, `docs/backlog.md`, `docs/superpowers/specs/`
- `src/Dockyard.Agent/`, `src/Dockyard/Core/`, `src/Dockyard/Hosts/`, `src/Dockyard.Dashboard/Features/`
- `macos/DockyardVM/Sources/`, `image/`, `scripts/mac-e2e.sh`

## To obtain

- https://thenewstack.io/orbstack-a-deep-dive-for-container-and-kubernetes-development/: search excerpt only
- https://dev.to/david-arcjet/secure-local-nodejs-dev-servers-with-orbstack-10lo: search excerpt only, reports a Node certificate trust problem
- Reddit and X discussion, and any user survey: not searched
