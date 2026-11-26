<#
  Writes the winget manifests for a CLI release (cli/<version>) to packaging/winget/out/<version>. winget installs the
  dockyard command, which sets up everything else:

    pwsh packaging/winget/render.ps1 -Version 1.2.0 -Installer out/dockyard-windows-x64.exe

  The first submission is manual (wingetcreate submit <dir>); later releases are updated by CI with wingetcreate update.
#>
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Installer
)

$ErrorActionPreference = 'Stop'
$id = 'byhofman.Dockyard'
$schema = '1.6.0'
$url = "https://github.com/byhofman/dockyard/releases/download/cli/$Version/dockyard-windows-x64.exe"
$sha = (Get-FileHash $Installer -Algorithm SHA256).Hash
$dir = Join-Path $PSScriptRoot "out/$Version"
New-Item -ItemType Directory -Force $dir | Out-Null

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@ | Set-Content -Encoding utf8NoBOM (Join-Path $dir "$id.yaml")

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
MinimumOSVersion: 10.0.22000.0
InstallerType: exe
Scope: machine
ElevationRequirement: elevatesSelf
InstallModes:
  - silent
  - silentWithProgress
InstallerSwitches:
  Silent: setup --silent --no-start
  SilentWithProgress: setup --silent --no-start
UpgradeBehavior: install
Installers:
  - Architecture: x64
    InstallerUrl: $url
    InstallerSha256: $sha
ManifestType: installer
ManifestVersion: $schema
"@ | Set-Content -Encoding utf8NoBOM (Join-Path $dir "$id.installer.yaml")

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Thomas Hofman
PublisherUrl: https://github.com/byhofman
PackageName: Dockyard
PackageUrl: https://github.com/byhofman/dockyard
License: MIT
LicenseUrl: https://github.com/byhofman/dockyard/blob/main/LICENSE
ShortDescription: Docker on Windows without Docker Desktop, with Linux and Windows engines on demand.
Description: Linux containers in a dedicated WSL distro and Windows containers in the dockerd service behind the normal docker CLI with compose and buildx, the dockyard command that installs and updates them, an optional tray, and an optional dashboard.
Tags:
  - docker
  - containers
  - wsl
  - compose
ManifestType: defaultLocale
ManifestVersion: $schema
"@ | Set-Content -Encoding utf8NoBOM (Join-Path $dir "$id.locale.en-US.yaml")

Write-Host "Manifests in $dir"
