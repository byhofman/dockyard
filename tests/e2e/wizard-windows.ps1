<#
  End to end on a Windows machine with WSL 2, as an administrator:
    wizard-windows.ps1 -Wizard <Dockyard.exe with Dockyard.Dashboard.zip and the image next to it>
    wizard-windows.ps1 -OneLiner <url of install.ps1>
  Starts from an install of v0.1.0, the state of the owner's PC.
#>
param([string]$Wizard, [string]$OneLiner, [string]$Screenshots = (Join-Path $PSScriptRoot '../../screenshots'))

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$appDir = Join-Path $env:ProgramFiles 'Dockyard'
$appExe = Join-Path $appDir 'Dockyard.exe'
$dashboard = Join-Path $appDir 'Dockyard.Dashboard.exe'
$docker = Join-Path $env:ProgramFiles 'Docker\docker.exe'

function Ok($text) { Write-Host "  ok   $text" }
function Check($text, $condition) { if ($condition) { Ok $text } else { throw "FAIL $text" } }
function Section($text) { Write-Host "== $text" -ForegroundColor Cyan }

function Run {
    if ($OneLiner) {
        & ([scriptblock]::Create((Invoke-RestMethod $OneLiner))) @args
        return $global:LASTEXITCODE
    }
    $out = New-TemporaryFile; $err = New-TemporaryFile
    $p = Start-Process (Resolve-Path $Wizard) -ArgumentList $args -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err
    Get-Content $out, $err | Write-Host
    return $p.ExitCode
}

function Window {
    param([string]$LookOnly)
    $exe = if ($OneLiner) { Join-Path $env:USERPROFILE 'Downloads\Dockyard.exe' } else { (Resolve-Path $Wizard).Path }
    powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'wizard-window.ps1') -Exe $exe -Screenshots $Screenshots -Dashboard $dashboard $(if ($LookOnly) { '-LookOnly', $LookOnly })
    if ($LASTEXITCODE -ne 0) { throw 'FAIL the wizard window' }
}

function Linux-Engine {
    Start-Process wsl.exe -ArgumentList '-d dockyard -u root -- /usr/local/sbin/dockyard-init' -WindowStyle Hidden | Out-Null
    foreach ($i in 1..60) {
        & $docker --context linux version *> $null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep 2
    }
    throw 'FAIL the Linux engine did not start'
}

Section 'wrong arguments are refused'
if ($OneLiner) {
    $failed = $false
    try { Run --silent --typo | Out-Null } catch { $failed = $true; Ok "the one-liner failed: $_" }
    Check 'the one-liner reports the failure' $failed
} else {
    Check 'exit 2' ((Run --silent --typo) -eq 2)
}

Section 'over v0.1.0, as the owner has it: update, the skipped dashboard stays skipped'
New-Item -ItemType Directory -Force $appDir | Out-Null
Invoke-WebRequest https://github.com/byhofman/dockyard/releases/download/v0.1.0/Dockyard.exe -OutFile $appExe
Check 'really v0.1.0 to start from' ((Get-Item $appExe).VersionInfo.ProductVersion -like '0.1.0*')
New-Item -ItemType Directory -Force "$env:APPDATA\Dockyard" | Out-Null
'{"StopOtherOnSwitch":false,"StartActiveAtLogon":false}' | Set-Content "$env:APPDATA\Dockyard\settings.json"
if (-not $OneLiner) { Window -LookOnly 'manage-over-v0.1.0' }
$code = Run --silent --no-start
Check "exit 0 (was $code)" ($code -eq 0)
Check 'no longer v0.1.0' ((Get-Item $appExe).VersionInfo.ProductVersion -notlike '0.1.0*')
Check 'no dashboard' (-not (Test-Path $dashboard))
Check 'settings kept' ((Get-Content "$env:APPDATA\Dockyard\settings.json" -Raw) -match '"StopOtherOnSwitch":\s*false')
$distros = (wsl.exe -l -q | Out-String) -replace "`0", ''
Check 'the dockyard distro is there' ($distros -match 'dockyard')
Check 'docker contexts' ((& $docker context ls -q) -contains 'linux')
Check 'mirrored networking set' ((Get-Content "$env:USERPROFILE\.wslconfig" -Raw) -match 'networkingMode=mirrored')

Section 'the Linux engine works and holds data'
Linux-Engine
& $docker --context linux volume create dockyard-e2e | Out-Null
Check 'marker volume created' ($LASTEXITCODE -eq 0)

if ($Wizard) {
    Section 'host.docker.internal reaches a service on the Windows loopback'
    Write-Host "WSL interfaces: $((wsl.exe -d dockyard -u root -- ls /sys/class/net | Out-String) -replace "`\s+", ' ')"
    Check 'the agent runs next to the engine' ((wsl.exe -d dockyard -u root -- pidof dockyard-agent | Out-String).Trim().Length -gt 0)
    $service = Start-Job {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 18080)
        $listener.Start()
        while ($true) {
            $client = $listener.AcceptTcpClient()
            $bytes = [Text.Encoding]::ASCII.GetBytes("HTTP/1.0 200 OK`r`n`r`nfrom-windows")
            $client.GetStream().Write($bytes, 0, $bytes.Length)
            $client.Close()
        }
    }
    Start-Sleep 3
    $reply = (& $docker --context linux run --rm alpine:3.24 wget -qO- -T 20 http://host.docker.internal:18080/ | Out-String).Trim()
    Check "the service answers (got '$reply')" ($reply -eq 'from-windows')
    Stop-Job $service; Remove-Job $service -Force
    $engine = (& $docker --context linux run --rm alpine:3.24 sh -c 'wget -qO- -T 5 http://host.docker.internal:2375/version || echo refused' | Out-String).Trim()
    Check "the engine's own port stays out of reach (got '$engine')" ($engine -eq 'refused')
    $name = (& $docker --context linux run --rm alpine:3.24 nslookup dl-cdn.alpinelinux.org | Out-String)
    Check 'other names still resolve' ($name -match 'Address')
}

if ($Wizard) {
    Section 'localhost reaches a published port, not only 127.0.0.1'
    & $docker --context linux run -d --name e2e-localhost -p 18090:80 nginx:alpine | Out-Null
    $tray = Start-Process $appExe -ArgumentList '--tray' -PassThru
    Start-Sleep 8
    $v6 = (curl.exe -s -g -o NUL -w '%{http_code}' --max-time 10 'http://[::1]:18090/' | Out-String).Trim()
    Check "[::1] answers (got $v6)" ($v6 -eq '200')
    $named = (curl.exe -s -o NUL -w '%{http_code}' --max-time 10 'http://localhost:18090/' | Out-String).Trim()
    Check "localhost answers (got $named)" ($named -eq '200')
    Get-Process Dockyard -ErrorAction SilentlyContinue | Stop-Process -Force
    & $docker --context linux rm -f e2e-localhost | Out-Null

    Section "the engine and containers follow Windows' proxy, also one on its loopback"
    python -m pip install --quiet proxy.py
    $proxyLog = Join-Path $env:TEMP 'e2e-proxy.log'
    $proxy = Start-Process python -ArgumentList "-m proxy --hostname 127.0.0.1 --port 18888 --log-file `"$proxyLog`"" -PassThru -WindowStyle Hidden
    Start-Sleep 5
    $settings = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    Set-ItemProperty $settings ProxyEnable 1
    Set-ItemProperty $settings ProxyServer '127.0.0.1:18888'
    try {
        Check 'exit 0' ((Run --silent --no-start) -eq 0)
        wsl.exe -d dockyard -u root -- sh -c 'kill -TERM $(pidof dockerd); while pidof dockerd >/dev/null; do sleep 0.2; done'
        Check 'daemon.json has the proxy, through the agent' ((wsl.exe -d dockyard -u root -- cat /etc/docker/daemon.json | Out-String) -match '198\.19\.250\.254:18888')
        Linux-Engine
        & $docker --context linux pull -q busybox:1.37 | Out-Null
        Check 'the pull succeeds' ($LASTEXITCODE -eq 0)
        Check 'the pull went through the proxy' ((Get-Content $proxyLog -Raw) -match 'docker')
    } finally {
        Set-ItemProperty $settings ProxyEnable 0
        Remove-ItemProperty $settings ProxyServer -ErrorAction SilentlyContinue
        Stop-Process $proxy -Force -ErrorAction SilentlyContinue
    }
    Check 'exit 0 without the proxy' ((Run --silent --no-start) -eq 0)
    wsl.exe -d dockyard -u root -- sh -c 'kill -TERM $(pidof dockerd); while pidof dockerd >/dev/null; do sleep 0.2; done'
    Check 'gone once Windows stops using it' ((wsl.exe -d dockyard -u root -- cat /etc/docker/daemon.json | Out-String) -notmatch '18888')
    Linux-Engine

    Section 'docker login keeps credentials in the Windows credential store'
    Check 'the helper is there' (Test-Path (Join-Path $env:ProgramFiles 'Docker\docker-credential-wincred.exe'))
    Check 'the store is set' ((Get-Content "$env:USERPROFILE\.docker\config.json" -Raw) -match '"credsStore":\s*"wincred"')
    $helper = Join-Path $env:ProgramFiles 'Docker\docker-credential-wincred.exe'
    '{"ServerURL":"e2e.dockyard.test","Username":"me","Secret":"s3cret"}' | & $helper store
    Check 'a login round-trips through the store' (('e2e.dockyard.test' | & $helper get | Out-String) -match 's3cret')
    Check 'the secret is not in config.json' ((Get-Content "$env:USERPROFILE\.docker\config.json" -Raw) -notmatch 's3cret')
    'e2e.dockyard.test' | & $helper erase
}

Section 'the Windows engine: no sign-out needed'
$config = Get-Content 'C:\ProgramData\docker\config\daemon.json' -Raw
Check 'the user is named on the pipe' ($config.Contains(([Security.Principal.WindowsIdentity]::GetCurrent().Name -replace '\\', '\\')))
Start-Service docker
foreach ($i in 1..30) { & $docker --context win version *> $null; if ($LASTEXITCODE -eq 0) { break }; Start-Sleep 2 }
Check 'the win context answers' ($LASTEXITCODE -eq 0)
Stop-Service docker

Section 'a docker command running in some terminal does not stop an update'
$running = Start-Process $docker -ArgumentList '--context linux events' -PassThru -WindowStyle Hidden
Check 'the docker command is running' (-not $running.HasExited)

Section 'rerun keeps the choice and the data'
Check 'exit 0' ((Run --silent --no-start) -eq 0)
Check 'still no dashboard' (-not (Test-Path $dashboard))
Linux-Engine
& $docker --context linux volume inspect dockyard-e2e | Out-Null
Check 'marker volume kept' ($LASTEXITCODE -eq 0)
if (-not $running.HasExited) { $running.Kill() }

Section 'the wizard window adds the skipped dashboard and removes it again'
Window

Section 'add the dashboard'
Check 'exit 0' ((Run --silent --dashboard --no-start) -eq 0)
Check 'dashboard added' (Test-Path $dashboard)
Check 'with its libraries' (Test-Path (Join-Path $appDir 'dashboard.files'))

Section 'rerun keeps it'
Check 'exit 0' ((Run --silent --no-start) -eq 0)
Check 'dashboard kept' (Test-Path $dashboard)

Section 'remove the dashboard'
Check 'exit 0' ((Run --silent --no-dashboard --no-start) -eq 0)
Check 'dashboard removed' (-not (Test-Path $dashboard))
Check 'app stays' (Test-Path $appExe)

Section 'the installed tray can replace itself, which v0.1.0 could not'
$source = Join-Path (New-Item -ItemType Directory (Join-Path $env:TEMP ([guid]::NewGuid()))) 'Dockyard.exe'
Copy-Item $appExe $source
$log = New-TemporaryFile
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$p = Start-Process $appExe -ArgumentList "--machine-update App --source `"$source`" --user-sid $($id.User.Value) --user-name `"$($id.Name)`" --log `"$log`" --keep-pid 0" -Wait -PassThru
Get-Content $log | Write-Host
Check "worker exit 0 (was $($p.ExitCode))" ($p.ExitCode -eq 0)
Get-Process Dockyard -ErrorAction SilentlyContinue | Stop-Process -Force

Section 'uninstall keeps the data'
Check 'exit 0' ((Run --silent --uninstall) -eq 0)
Check 'app removed' (-not (Test-Path $appExe))
$distros = (wsl.exe -l -q | Out-String) -replace "`0", ''
Check 'the dockyard distro is kept' ($distros -match 'dockyard')
Check 'no credential store left in the config' (-not ((Test-Path "$env:USERPROFILE\.docker\config.json") -and ((Get-Content "$env:USERPROFILE\.docker\config.json" -Raw) -match 'credsStore')))
Check 'our .wslconfig line is gone' ((Get-Content "$env:USERPROFILE\.wslconfig" -Raw) -notmatch 'networkingMode=mirrored')

Section 'a fresh install gets the dashboard'
Check 'exit 0' ((Run --silent --no-start) -eq 0)
Check 'dashboard there' (Test-Path $dashboard)
Linux-Engine
& $docker --context linux volume inspect dockyard-e2e | Out-Null
Check 'marker volume back with the distro' ($LASTEXITCODE -eq 0)

Write-Host 'All good.' -ForegroundColor Green
