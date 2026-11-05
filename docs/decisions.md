# Decisions

Non-obvious choices, newest first.

## 2026-11-05: OrbStack must-haves came from a stated test

The owner asked which Docker-related OrbStack features Dockyard must have. No source can settle "must", so the [thesis](research/orbstack-docker-features/thesis.md) states a test: a workflow that fails, needs manual setup or is unsafe by default, or a differentiator with evidence beyond OrbStack's own marketing. `DOK-7` to `DOK-17` were added on that basis, and `DOK-2` and `DOK-5` were kept as must-haves. Backlog scope otherwise did not change.

The research standard says a thesis never decides what to build and the research skill says not to merge. The owner's brief asked for the classification, the backlog items and a merge once CI is green, so the brief wins for this run. Mechanism, naming and per-OS design stay open until the slice is prototyped and built.
