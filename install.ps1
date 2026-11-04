<#
  Dockyard installer:

    irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1 | iex

  With options:

    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1))) -NoStart

  -Build installs the latest build of main instead of the latest release (needs the gh CLI).
  Run it elevated to also add the Defender ASR exclusions and skip the administrator prompt.
#>
param(
    [switch]$Build,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$repo = 'byhofman/dockyard'
$exe = Join-Path $env:USERPROFILE 'Downloads\Dockyard.exe'
$installed = Join-Path $env:ProgramFiles 'Dockyard\Dockyard.exe'
$dashboard = Join-Path $env:ProgramFiles 'Dockyard\Dockyard.Dashboard.exe'
$zip = Join-Path $env:USERPROFILE 'Downloads\Dockyard.Dashboard.zip'
$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }

wsl.exe --status *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'WSL is not installed. Run "wsl --install --no-distribution" (elevated), reboot, then run this again.'
}

Step 'Defender ASR exclusions'
if ($elevated) {
    Add-MpPreference -AttackSurfaceReductionOnlyExclusions $exe, $installed, $dashboard
    Write-Host "Excluded $exe, $installed and $dashboard"
} else {
    Write-Warning 'Not elevated, skipped. If Defender blocks Dockyard (event 1121), run this again from an elevated PowerShell.'
}

Step 'Download Dockyard'
Remove-Item $exe, $zip -Force -ErrorAction SilentlyContinue
if ($Build) {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw '-Build needs the gh CLI (GitHub only serves build artifacts to signed-in users).' }
    $run = gh run list -R $repo -b main -w build -s success -L 1 --json databaseId -q '.[0].databaseId'
    if (-not $run) { throw "No successful build of $repo found." }
    foreach ($artifact in 'windows', 'image') { gh run download $run -R $repo -n $artifact -D (Split-Path $exe) }
    Write-Host "Latest build of main (run $run)"
} else {
    $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'dockyard-install' }
    $asset = $release.assets | Where-Object name -eq 'Dockyard.exe' | Select-Object -First 1
    if (-not $asset) { throw "Release $($release.tag_name) has no Dockyard.exe." }
    Invoke-WebRequest $asset.browser_download_url -OutFile $exe -UseBasicParsing
    $dashboardAsset = $release.assets | Where-Object name -eq 'Dockyard.Dashboard.zip' | Select-Object -First 1
    if ($dashboardAsset) { Invoke-WebRequest $dashboardAsset.browser_download_url -OutFile $zip -UseBasicParsing }
    $imageAsset = $release.assets | Where-Object name -like 'dockyard-linux-*-x86_64.tar.gz' | Select-Object -First 1
    if ($imageAsset) { Invoke-WebRequest $imageAsset.browser_download_url -OutFile (Join-Path (Split-Path $exe) $imageAsset.name) -UseBasicParsing }
    Write-Host "Release $($release.tag_name)"
}

Step 'Install'
$arguments = @('--install')
if ($NoStart) { $arguments += '--no-start' }
$p = Start-Process $exe -ArgumentList $arguments -Wait -NoNewWindow -PassThru

switch ($p.ExitCode) {
    0 { Write-Host "`nDockyard is installed and running in the tray." -ForegroundColor Green }
    3010 { Write-Host "`nDockyard is installed. Reboot to finish enabling Windows containers." -ForegroundColor Yellow }
    default { throw "Install failed (exit code $($p.ExitCode)). See the output above." }
}
