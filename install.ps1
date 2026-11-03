<#
  Dockyard installer:

    irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1 | iex

  With options:

    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1))) -Distro Ubuntu-24.04

  -Build installs the latest build of main instead of the latest release (needs the gh CLI).
  Run it elevated to also add the Defender ASR exclusions and skip the administrator prompt.
#>
param(
    [string]$Distro,
    [switch]$Build,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$repo = 'byhofman/dockyard'
$exe = Join-Path $env:USERPROFILE 'Downloads\Dockyard.exe'
$installed = Join-Path $env:ProgramFiles 'Dockyard\Dockyard.exe'
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

if (-not (wsl.exe -l -q 2>$null)) {
    throw 'No WSL distro found. Install one (winget install Canonical.Ubuntu.2404), launch it once to create your user, then run this again.'
}

Step 'Defender ASR exclusions'
if ($elevated) {
    Add-MpPreference -AttackSurfaceReductionOnlyExclusions $exe, $installed
    Write-Host "Excluded $exe and $installed"
} else {
    Write-Warning 'Not elevated, skipped. If Defender blocks Dockyard (event 1121), run this again from an elevated PowerShell.'
}

Step 'Download Dockyard.exe'
Remove-Item $exe -Force -ErrorAction SilentlyContinue
if ($Build) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw '-Build needs the gh CLI (GitHub only serves build artifacts to signed-in users).' }
    $run = gh run list -R $repo -b main -w build -s success -L 1 --json databaseId -q '.[0].databaseId'
    if (-not $run) { throw "No successful build of $repo found." }
    gh run download $run -R $repo -n Dockyard -D (Split-Path $exe)
    Write-Host "Latest build of main (run $run)"
} else {
    $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'dockyard-install' }
    $asset = $release.assets | Where-Object name -eq 'Dockyard.exe' | Select-Object -First 1
    if (-not $asset) { throw "Release $($release.tag_name) has no Dockyard.exe." }
    Invoke-WebRequest $asset.browser_download_url -OutFile $exe -UseBasicParsing
    Write-Host "Release $($release.tag_name)"
}

Step 'Install'
$arguments = @('--install')
if ($Distro) { $arguments += '--distro', $Distro }
if ($NoStart) { $arguments += '--no-start' }
$p = Start-Process $exe -ArgumentList $arguments -Wait -NoNewWindow -PassThru

switch ($p.ExitCode) {
    0 { Write-Host "`nDockyard is installed and running in the tray. Sign out and back in once so docker-users applies to the Windows engine." -ForegroundColor Green }
    3010 { Write-Host "`nDockyard is installed. Reboot to finish enabling Windows containers." -ForegroundColor Yellow }
    default { throw "Install failed (exit code $($p.ExitCode)). See the output above." }
}
