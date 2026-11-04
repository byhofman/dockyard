<#
  Builds the dockyard-linux image.

    pwsh image/build.ps1 [-Arch x86_64|aarch64] [-Version 1.2.3] [-Context linux] [-Vm]

  Output: out/dockyard-linux-<version>-<arch>.tar.gz (rootfs, the WSL distro on Windows), or with -Vm
  out/dockyard-vm-<version>-<arch>.tar.gz (kernel, initramfs, root.img and dockyard-release for the macOS VM).
#>
param(
    [ValidateSet('x86_64', 'aarch64')][string]$Arch = 'x86_64',
    [string]$Version = '0.0.0',
    [string]$Context = '',
    [switch]$Vm
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$versions = @{}
Get-Content (Join-Path $PSScriptRoot 'versions.env') | Where-Object { $_ -match '^\s*(\w+)=(.+)$' } | ForEach-Object { $versions[$Matches[1]] = $Matches[2].Trim() }

$platform = if ($Arch -eq 'x86_64') { 'linux/amd64' } else { 'linux/arm64' }
$name = if ($Vm) { 'dockyard-vm' } else { 'dockyard-linux' }
$target = if ($Vm) { 'vm-export' } else { 'export' }
$out = Join-Path $root 'out'
New-Item -ItemType Directory -Force $out | Out-Null
$tar = Join-Path $out "$name-$Version-$Arch.tar"

$docker = @('docker')
if ($Context) { $docker += '--context', $Context }
& $docker[0] @($docker[1..9] | Where-Object { $_ }) buildx build $root -f (Join-Path $PSScriptRoot 'Dockerfile') --platform $platform --target $target `
    --build-arg "ALPINE=$($versions.ALPINE)" --build-arg "DOCKER=$($versions.DOCKER)" `
    --build-arg "BUILDX=$($versions.BUILDX)" --build-arg "COMPOSE=$($versions.COMPOSE)" `
    --build-arg "IMAGE_VERSION=$Version" --output "type=tar,dest=$tar"
if ($LASTEXITCODE -ne 0) { throw "docker buildx build failed ($LASTEXITCODE)" }

$gz = "$tar.gz"
$in = [IO.File]::OpenRead($tar)
$outStream = [IO.File]::Create($gz)
$zip = [IO.Compression.GZipStream]::new($outStream, [IO.Compression.CompressionLevel]::Optimal)
$in.CopyTo($zip)
$zip.Dispose(); $outStream.Dispose(); $in.Dispose()
Remove-Item $tar
Write-Host ("{0} ({1:N1} MB)" -f $gz, ((Get-Item $gz).Length / 1MB))
