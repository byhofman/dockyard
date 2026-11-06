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
    Write-Host '--- what WSL says'
    wsl.exe --version | Write-Host
    wsl.exe -d dockyard -u root -- sh -c 'ls /sys/class/net; ip -4 addr; pidof dockerd dockyard-agent; netstat -ltn 2>/dev/null | head -20; wget -qO- -T 3 http://127.0.0.1:2375/_ping; echo; cat /etc/docker/daemon.json' | Write-Host
    Test-NetConnection 127.0.0.1 -Port 2375 | Out-String | Write-Host
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
if ($Wizard) { Check 'no mirrored networking on Windows Server, where it cuts the engine off' (-not ((Test-Path "$env:USERPROFILE\.wslconfig") -and ((Get-Content "$env:USERPROFILE\.wslconfig" -Raw) -match 'networkingMode=mirrored'))) }

Section 'the Linux engine works and holds data'
Linux-Engine
& $docker --context linux volume create dockyard-e2e | Out-Null
Check 'marker volume created' ($LASTEXITCODE -eq 0)

if ($Wizard) {
    Section 'host.docker.internal reaches a service on Windows'
    Write-Host "WSL interfaces: $((wsl.exe -d dockyard -u root -- ls /sys/class/net | Out-String) -replace "`\s+", ' ')"
    Check 'the agent runs next to the engine' ((wsl.exe -d dockyard -u root -- pidof dockyard-agent | Out-String).Trim().Length -gt 0)
    # This runner is Windows Server, so WSL has NAT: there only the gateway reaches Windows, past its firewall. On Windows 11
    # with mirrored networking a service on 127.0.0.1 is reached too, which only a real machine can show.
    Set-NetFirewallProfile -Profile Domain,Public,Private -Enabled False
    $service = Start-Job {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Any, 18080)
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
    if ($reply -ne 'from-windows') {
        Write-Host '--- why not'
        Get-Job | Format-List State, @{n='Error';e={$_.ChildJobs[0].JobStateInfo.Reason}} | Out-String | Write-Host
        (Test-NetConnection 127.0.0.1 -Port 18080).TcpTestSucceeded | Write-Host
        wsl.exe -d dockyard -u root -- sh -c 'cat /proc/net/route; gw=$(ip route | sed -n "s/^default via \([^ ]*\).*/\1/p"); echo gw=$gw; wget -qO- -T 5 http://$gw:18080/; echo; iptables -t nat -S | grep -E "REDIRECT|DOCKER$" | head; iptables -S INPUT | head -5; netstat -ltn | grep 2378' | Write-Host
        & $docker --context linux run --rm alpine:3.24 sh -c 'cat /etc/resolv.conf; nslookup host.docker.internal; wget -S -O- -T 10 http://host.docker.internal:18080/ 2>&1; nc -w 5 198.19.250.254 18080 </dev/null; echo nc=$?' | Write-Host
        wsl.exe -d dockyard -u root -- sh -c 'dmesg | tail -5' | Write-Host
    }
    Check "the service answers (got '$reply')" ($reply -eq 'from-windows')
    Stop-Job $service; Remove-Job $service -Force
    Set-NetFirewallProfile -Profile Domain,Public,Private -Enabled True
    $engine = (& $docker --context linux run --rm alpine:3.24 sh -c 'wget -qO- -T 5 http://host.docker.internal:2375/version || echo refused' | Out-String).Trim()
    Check "the engine's own port stays out of reach (got '$engine')" ($engine -eq 'refused')
    $name = (& $docker --context linux run --rm alpine:3.24 nslookup dl-cdn.alpinelinux.org | Out-String)
    Check 'other names still resolve' ($name -match 'Address')
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
Check 'our .wslconfig line is gone' (-not ((Test-Path "$env:USERPROFILE\.wslconfig") -and ((Get-Content "$env:USERPROFILE\.wslconfig" -Raw) -match 'networkingMode=mirrored')))

Section 'a fresh install gets the dashboard'
Check 'exit 0' ((Run --silent --no-start) -eq 0)
Check 'dashboard there' (Test-Path $dashboard)
Linux-Engine
& $docker --context linux volume inspect dockyard-e2e | Out-Null
Check 'marker volume back with the distro' ($LASTEXITCODE -eq 0)

Write-Host 'All good.' -ForegroundColor Green
