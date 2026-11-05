# Decisions

Non-obvious choices, newest first.

## 2026-11-05: macOS start always runs prepare, the data disk is only formatted when it reads as zeros

`MacHost.Start` runs `dockyard-vm prepare` before every boot. `vm.json` holds absolute kernel paths and the root image holds the kernel's modules, so without it a moved app does not boot and a self-updated app boots a new kernel on old modules. Prepare only copies when the bundled release changed.

The VM init formats `/dev/vdb` only if its first 64 KiB are zero, and powers off if the disk does not mount. A filesystem probe that misreads a disk would wipe the user's images, and continuing without the disk would put them on the root image, which every update replaces.

## 2026-11-05: OrbStack must-haves came from a stated test

The owner asked which Docker-related OrbStack features Dockyard must have. No source can settle "must", so the [thesis](research/orbstack-docker-features/thesis.md) states a test: a workflow that fails, needs manual setup or is unsafe by default, or a differentiator with evidence beyond OrbStack's own marketing. `DOK-7` to `DOK-17` were added on that basis, and `DOK-2` and `DOK-5` were kept as must-haves. Backlog scope otherwise did not change.

The research standard says a thesis never decides what to build. The owner's brief asked for the classification and the backlog items, so the brief wins for this run. The PR is left for the owner's review, not merged. Mechanism, naming and per-OS design stay open until the slice is prototyped and built.
