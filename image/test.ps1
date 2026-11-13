<#
  Smoke-tests a dockyard-linux tarball in a privileged container: dockyard-init starts dockerd,
  a container runs inside it, buildx and compose answer, and SIGTERM stops dockerd cleanly.

    pwsh image/test.ps1 -Tarball out/dockyard-linux-0.0.0-x86_64.tar.gz [-Context linux]
#>
param(
    [Parameter(Mandatory)][string]$Tarball,
    [string]$Context = ''
)

$ErrorActionPreference = 'Stop'
$ctx = if ($Context) { @('--context', $Context) } else { @() }
function D { & docker @ctx @args; if ($LASTEXITCODE -ne 0) { throw "docker $($args -join ' ') failed ($LASTEXITCODE)" } }

$image = 'dockyard-linux-test:latest'
& docker @ctx import (Resolve-Path $Tarball).Path $image | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'docker import failed' }

$name = "dockyard-linux-test-$PID"
D run -d --privileged -v /var/lib/docker --name $name $image /usr/local/sbin/dockyard-init | Out-Null
try {
    $ready = $false
    foreach ($i in 1..60) {
        & docker @ctx exec $name docker version --format '{{.Server.Version}}' 2>$null | Set-Variable v
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep 1
    }
    if (-not $ready) { D logs $name; throw 'dockerd did not become ready' }
    Write-Host "dockerd $v ready"

    $echo = (D exec $name docker run --rm alpine:3.24 echo ok).Trim()
    if ($echo -ne 'ok') { throw "container output was '$echo'" }
    Write-Host 'container runs'

    D exec -d $name /usr/local/sbin/dockyard-init
    Start-Sleep 3
    $again = (D exec $name docker version --format '{{.Server.Version}}' 2>&1 | Out-String).Trim()
    if ($again -ne "$v".Trim()) { D logs --tail 20 $name; throw "a second dockyard-init took the running engine away (got '$again')" }
    Write-Host 'a second start leaves the running engine alone'

    D exec $name docker buildx version
    D exec $name docker compose version
    D exec $name cat /etc/dockyard-release

    D exec $name sh -c 'kill -TERM $(pidof dockerd)'
    $exit = (D wait $name).Trim()
    if ($exit -ne '0') { D logs --tail 20 $name; throw "dockerd exited with $exit after SIGTERM" }
    Write-Host 'clean shutdown on SIGTERM'
}
finally {
    & docker @ctx rm -f -v $name 2>$null | Out-Null
    & docker @ctx rmi $image 2>$null | Out-Null
}
exit 0
