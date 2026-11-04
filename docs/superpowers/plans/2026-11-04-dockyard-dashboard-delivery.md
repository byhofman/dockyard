# Dockyard Dashboard Delivery Implementation Plan (A4)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans.

**Goal:** Ship the dashboard: CI builds and tests everything, the AOT gate fails on any trim/AOT warning, the installer and self-update install the dashboard next to the tray, budgets can be measured, the README describes it.

## Decisions taken during A2/A3

- The tray stays a trimmed single-file exe (JIT): it is also the downloadable installer, and native AOT cannot embed Avalonia's native libraries (Skia, HarfBuzz) into one file.
- The dashboard is native AOT and ships as `Dockyard.Dashboard.zip` (exe + native libraries).
- This laptop has no C++ linker, so the native AOT gate runs in a `mcr.microsoft.com/dotnet/sdk:10.0` Linux container (ILC reports the same trim/AOT warnings for every target) and the Windows AOT exe is built in CI.

## Tasks

1. CI (`.github/workflows/release.yml`): `dotnet test --solution Dockyard.slnx`, publish tray and dashboard with `-warnaserror`, zip the dashboard, upload both, attach both to `v*` releases.
2. Installer: `MachineSetup.InstallApp` extracts `Dockyard.Dashboard.zip` found next to the installer exe into `C:\Program Files\Dockyard\` (zip-slip guarded), stops running dashboards first; `Updates.DownloadApp` fetches the zip of the same tag; `install.ps1` downloads it and adds the ASR exclusion for `Dockyard.Dashboard.exe`.
3. `scripts/measure.ps1`: window-ready time, idle CPU, private memory of dashboard and tray against the budgets.
4. README: dashboard section, keys, build and test instructions, ASR exclusion.
5. Zero warnings in the whole solution (`dotnet build Dockyard.slnx`), all tests green.
