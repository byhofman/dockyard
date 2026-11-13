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
# Either name: the one-liner test runs the release that is out, and the rename reaches it with the next release.
$dashboard = Join-Path $appDir 'Dockyard*Dashboard.exe'
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
    # The image and dashboard built next to the wizard, which a build before its release cannot download.
    $p = Start-Process (Resolve-Path $Wizard) -ArgumentList (@($args) + '--assets', (Split-Path (Resolve-Path $Wizard))) -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err
    Get-Content $out, $err | Write-Host
    return $p.ExitCode
}

function Window {
    param([string]$LookOnly)
    $exe = if ($OneLiner) { Join-Path $env:USERPROFILE 'Downloads\Dockyard.exe' } else { (Resolve-Path $Wizard).Path }
    powershell.exe -NoProfile -File (Join-Path $PSScriptRoot 'wizard-window.ps1') -Exe $exe -Assets $(if ($OneLiner) { '' } else { Split-Path $exe }) -Screenshots $Screenshots -Dashboard $dashboard $(if ($LookOnly) { '-LookOnly', $LookOnly })
    if ($LASTEXITCODE -ne 0) { throw 'FAIL the wizard window' }
}

# The way a user starts it, through the tray, which is also what serves the engine's pipe to Windows. Setup stops the
# tray, so this runs again after each one.
function Screen($name) {
    Add-Type -AssemblyName System.Windows.Forms, System.Drawing
    New-Item -ItemType Directory -Force $Screenshots | Out-Null
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    [System.Drawing.Graphics]::FromImage($bitmap).CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save((Join-Path $Screenshots "$name.png"))
    $bitmap.Dispose()
}

function Linux-Engine {
    Start-Process $appExe -ArgumentList '--start', 'linux' | Out-Null
    foreach ($i in 1..60) {
        # Bounded: a pipe whose instances are all taken makes docker wait without end.
        $asking = Start-Process $docker -ArgumentList '--context', 'linux', 'version' -PassThru -NoNewWindow -RedirectStandardOutput (New-TemporaryFile)
        if (-not $asking.WaitForExit(30000)) { $asking.Kill(); Write-Host '  docker version did not return within 30 s'; break }
        if ($asking.ExitCode -eq 0) { return }
        Start-Sleep 2
    }
    Write-Host '--- what WSL says'
    wsl.exe --version | Write-Host
    wsl.exe -d dockyard -u root -- sh -c 'ls /sys/class/net; ip -4 addr; pidof dockerd dockyard-agent; netstat -ltn 2>/dev/null | head -20; docker version --format "{{.Server.Version}}"; ls -l /etc/dockyard; cat /etc/docker/daemon.json' | Write-Host
    Write-Host "pipes: $((Get-ChildItem \\.\pipe\ | Where-Object Name -like 'dockyard*' | ForEach-Object Name) -join ', ')"
    throw 'FAIL the Linux engine did not start'
}

# What a container printed, from its logs and not through an attached docker run.
function In-Container($command) {
    $id = (& $docker --context linux run -d alpine:3.24 sh -c $command | Out-String).Trim()
    & $docker --context linux wait $id | Out-Null
    $output = (& $docker --context linux logs $id 2>&1 | Out-String).Trim()
    & $docker --context linux rm -f $id | Out-Null
    return $output
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
    Section 'docker exec and attached runs return their output'
    & $docker --context linux run -d --name e2e-exec alpine:3.24 sh -c 'echo exec-works > /marker; sleep 3600' | Out-Null
    $exec = (& $docker --context linux exec e2e-exec cat /marker 2>&1 | Out-String).Trim()
    if ($exec -ne 'exec-works') {
        Write-Host '--- why not'
        Write-Host "inside WSL, on the unix socket: '$((wsl.exe -d dockyard -u root -- docker exec e2e-exec cat /marker | Out-String).Trim())'"
        Write-Host "Windows, stdin held open 3 s: '$((cmd.exe /c "ping -n 4 127.0.0.1 >nul | `"$docker`" --context linux exec -i e2e-exec cat /marker" | Out-String).Trim())'"
        Write-Host "Windows, with a tty: '$((& $docker --context linux exec -t e2e-exec cat /marker | Out-String).Trim())'"
    }
    Check "docker exec returns what the command printed (got '$exec')" ($exec -eq 'exec-works')
    $attached = (& $docker --context linux run --rm alpine:3.24 echo attached 2>&1 | Out-String).Trim()
    Check "an attached docker run prints its output (got '$attached')" ($attached -eq 'attached')
    $piped = ('through-stdin' | & $docker --context linux exec -i e2e-exec cat | Out-String).Trim()
    Check "stdin reaches the command and its output comes back (got '$piped')" ($piped -eq 'through-stdin')
    & $docker --context linux rm -f e2e-exec | Out-Null

    Section 'the engine answers no one without the tray token'
    $gate = ((wsl.exe -d dockyard -u root -- sh -c '[ -d /sys/class/net/loopback0 ] && echo 127.0.0.1 || ip -4 -o addr show eth0 | sed -n "s/.*inet \([0-9.]*\)\/.*/\1/p"') | Out-String).Trim()
    foreach ($target in @('127.0.0.1', $gate) | Select-Object -Unique) {
        $open = $true
        try { Invoke-WebRequest "http://$($target):2375/_ping" -TimeoutSec 5 -UseBasicParsing | Out-Null } catch { $open = $false }
        Check "no docker API on $($target):2375 without the token" (-not $open)
    }
    Check 'dockerd itself listens on no TCP port' (-not ((wsl.exe -d dockyard -u root -- netstat -ltnp | Out-String) -match 'dockerd'))
}

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
    $attached = (& $docker --context linux run --rm alpine:3.24 echo attached 2>&1 | Out-String).Trim()
    Write-Host "docker run prints: '$attached'"
    $reply = In-Container 'wget -qO- -T 20 http://host.docker.internal:18080/ 2>&1'
    if ($reply -ne 'from-windows') {
        Write-Host '--- why not'
        Write-Host (In-Container 'cat /etc/resolv.conf; nslookup host.docker.internal 2>&1; nc -w 5 198.19.250.254 18080 </dev/null; echo nc=$?')
        wsl.exe -d dockyard -u root -- sh -c 'gw=$(ip route | awk "/^default/{print \$3}"); echo gw=$gw; wget -qO- -T 5 http://$gw:18080/; echo; ps | grep dockyard-agent | head -3' | Write-Host
    }
    Check "the service answers (got '$reply')" ($reply -eq 'from-windows')
    Stop-Job $service; Remove-Job $service -Force
    Set-NetFirewallProfile -Profile Domain,Public,Private -Enabled True
    $engine = In-Container 'wget -qO- -T 5 http://host.docker.internal:2375/version 2>/dev/null || echo refused'
    Check "the engine's own port stays out of reach (got '$engine')" ($engine -eq 'refused')
    $name = In-Container 'nslookup dl-cdn.alpinelinux.org 2>&1'
    Check 'other names still resolve' ($name -match 'Address')
}

if ($Wizard) {
    Section 'localhost reaches a published port, not only 127.0.0.1'
    & $docker --context linux run -d --name e2e-localhost -p 18090:80 nginx:alpine | Out-Null
    function Can-Listen-On-80 {
        try { $web = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Any, 80); $web.Start(); $web.Stop(); return $true } catch { Write-Host "  port 80: $($_.Exception.InnerException.Message)"; return $false }
    }
    $free80 = Can-Listen-On-80
    $tray = Start-Process $appExe -ArgumentList '--tray' -PassThru
    Start-Sleep 8
    $v6 = (curl.exe -s -g -o NUL -w '%{http_code}' --max-time 10 'http://[::1]:18090/' | Out-String).Trim()
    Check "[::1] answers (got $v6)" ($v6 -eq '200')
    $named = (curl.exe -s -o NUL -w '%{http_code}' --max-time 10 'http://localhost:18090/' | Out-String).Trim()
    Check "localhost answers (got $named)" ($named -eq '200')
    # The relay holds 80 and 443 on 127.77.0.1, which must not keep a web server from taking those ports on every address.
    if ($free80) { Check 'a web server can still listen on port 80 next to the tray' (Can-Listen-On-80) }
    else { Write-Host '  something on this runner already holds port 80, so this says nothing here'; netstat -ano | Select-String ':80\s' | Write-Host }
    Get-Process Dockyard -ErrorAction SilentlyContinue | Stop-Process -Force
    & $docker --context linux rm -f e2e-localhost | Out-Null

    Section "a proxy only on Windows' loopback, out of reach with NAT networking, is not given to the engine"
    python -m pip install --quiet proxy.py
    $loopbackProxy = Start-Process python -ArgumentList '-m proxy --hostname 127.0.0.1 --port 18889' -PassThru -WindowStyle Hidden
    Start-Sleep 5
    $internet = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    Set-ItemProperty $internet ProxyEnable 1
    Set-ItemProperty $internet ProxyServer '127.0.0.1:18889'
    try {
        Check 'exit 0' ((Run --silent --no-start) -eq 0)
        wsl.exe -d dockyard -u root -- sh -c 'kill -TERM $(pidof dockerd); while pidof dockerd >/dev/null; do sleep 0.2; done'
        Check 'daemon.json has no proxy' ((wsl.exe -d dockyard -u root -- cat /etc/docker/daemon.json | Out-String) -notmatch '18889')
        Check 'neither has the docker command''s config' ((Get-Content "$env:USERPROFILE\.docker\config.json" -Raw) -notmatch '18889')
        Linux-Engine
        & $docker --context linux pull -q busybox:1.36 | Out-Null
        Check 'pulling still works, directly' ($LASTEXITCODE -eq 0)
        & $docker --context linux image rm busybox:1.36 | Out-Null
    } finally {
        Set-ItemProperty $internet ProxyEnable 0
        Remove-ItemProperty $internet ProxyServer -ErrorAction SilentlyContinue
        Stop-Process $loopbackProxy -Force -ErrorAction SilentlyContinue
    }

    Section "the engine and containers follow Windows' proxy, also one on its loopback"
    $proxyLog = Join-Path $env:TEMP 'e2e-proxy.log'
    $proxy = Start-Process python -ArgumentList "-m proxy --hostname 0.0.0.0 --port 18888 --log-file `"$proxyLog`"" -PassThru -WindowStyle Hidden
    Start-Sleep 5
    # This runner has NAT networking, where only the gateway reaches Windows: a proxy on loopback alone is out of reach
    # there, as for any host service. Mirrored networking reaches loopback too, and no runner has that.
    Set-NetFirewallProfile -Profile Domain,Public,Private -Enabled False
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
        Set-NetFirewallProfile -Profile Domain,Public,Private -Enabled True
    }
    Check 'exit 0 without the proxy' ((Run --silent --no-start) -eq 0)
    wsl.exe -d dockyard -u root -- sh -c 'kill -TERM $(pidof dockerd); while pidof dockerd >/dev/null; do sleep 0.2; done'
    Check 'gone once Windows stops using it' ((wsl.exe -d dockyard -u root -- cat /etc/docker/daemon.json | Out-String) -notmatch '18888')
    Linux-Engine

    Section 'engine configuration from the settings reaches the engine'
    $saved = Get-Content "$env:APPDATA\Dockyard\settings.json" -Raw
    $settings = $saved | ConvertFrom-Json
    $settings | Add-Member -Force RegistryMirrors 'https://mirror.dockyard.test'
    $settings | Add-Member -Force InsecureRegistries 'registry.dockyard.test:5000'
    $settings | Add-Member -Force AddressPools '10.123.0.0/16 24'
    $settings | ConvertTo-Json | Set-Content "$env:APPDATA\Dockyard\settings.json"
    Check 'exit 0' ((Run --silent --no-start) -eq 0)
    wsl.exe -d dockyard -u root -- sh -c 'kill -TERM $(pidof dockerd); while pidof dockerd >/dev/null; do sleep 0.2; done'
    Linux-Engine
    $info = & $docker --context linux info --format '{{json .RegistryConfig}}' | Out-String
    Check 'the mirror applies' ($info -match 'mirror\.dockyard\.test')
    Check 'the insecure registry applies' ($info -match 'registry\.dockyard\.test:5000')
    & $docker --context linux network create e2e-pool | Out-Null
    $subnet = & $docker --context linux network inspect e2e-pool --format '{{(index .IPAM.Config 0).Subnet}}' | Out-String
    Check "a new network comes from the pool (got $subnet)" ($subnet -match '^10\.123\.')
    & $docker --context linux network rm e2e-pool | Out-Null
    $saved | Set-Content "$env:APPDATA\Dockyard\settings.json"

    Section 'container domains and trusted HTTPS, without publishing a port'
    Check 'windows sends the name space to Dockyard' ((Get-DnsClientNrptRule | Where-Object { $_.Namespace -contains '.dockyard.test' }) -ne $null)
    & $docker --context linux run -d --name e2e-web nginx:alpine | Out-Null
    $tray = Start-Process $appExe -ArgumentList '--tray' -PassThru
    Start-Sleep 8
    Check 'the name resolves' ((Resolve-DnsName e2e-web.dockyard.test -Type A -ErrorAction SilentlyContinue).IPAddress -contains '127.77.0.1')
    $code = (curl.exe -s -o NUL -w '%{http_code}' --max-time 15 http://e2e-web.dockyard.test/ | Out-String).Trim()
    Check "http reaches it by name (got $code)" ($code -eq '200')
    Get-Process Dockyard -ErrorAction SilentlyContinue | Stop-Process -Force
    Check 'exit 0 with --trust-https' ((Run --silent --no-start --trust-https) -eq 0)
    $root = Get-ChildItem Cert:\LocalMachine\Root | Where-Object Subject -like '*Dockyard Local CA*'
    Check 'the CA is in the trust store' ($root -ne $null)
    Check 'name constrained' (($root.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.30' }).Critical)
    $tray = Start-Process $appExe -ArgumentList '--tray' -PassThru
    Start-Sleep 15
    # --ssl-no-revoke: this curl asks Windows to check revocation and fails when a private CA publishes no such list, as this
    # one does not need to. Browsers do not fail on that; a certificate that is not trusted still fails here.
    $code = (curl.exe -s --ssl-no-revoke -o NUL -w '%{http_code}' --max-time 15 https://e2e-web.dockyard.test/ | Out-String).Trim()
    if ($code -ne '200') { curl.exe -sS -v --ssl-no-revoke -o NUL --max-time 15 https://e2e-web.dockyard.test/ 2>&1 | Select-Object -Last 15 | Write-Host; Resolve-DnsName e2e-web.dockyard.test -Type A -ErrorAction SilentlyContinue | Out-String | Write-Host }
    Check "https is trusted by Windows (got $code)" ($code -eq '200')
    Get-Process Dockyard -ErrorAction SilentlyContinue | Stop-Process -Force
    $caDir = "$env:APPDATA\Dockyard\ca"
    Check 'no plain key on disk, C: being visible from WSL' (-not (Test-Path "$caDir\ca.key"))
    Check 'what is there is encrypted' ([Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes("$caDir\ca.key.dpapi")) -notmatch 'PRIVATE KEY')

    Section "approving with another administrator's account, which cannot read this user's CA folder"
    Get-ChildItem Cert:\LocalMachine\Root | Where-Object Subject -like '*Dockyard Local CA*' | Remove-Item
    $otherPassword = "E2e-$([guid]::NewGuid())"
    $secret = ConvertTo-SecureString $otherPassword -AsPlainText -Force
    New-LocalUser e2e-admin -Password $secret -PasswordNeverExpires | Out-Null
    Add-LocalGroupMember Administrators e2e-admin
    $shared = New-Item -ItemType Directory (Join-Path $env:ProgramData 'dockyard-e2e')
    icacls $shared /grant 'Everyone:(OI)(CI)F' | Out-Null
    Copy-Item (Resolve-Path $Wizard) $shared
    $me = [Security.Principal.WindowsIdentity]::GetCurrent()
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((Get-Content "$caDir\ca.pem" -Raw)))
    # A scheduled task: it runs as that account with its full administrator token, as a UAC approval does.
    # As Elevation.Run does: the caller makes the log file, and the elevated worker only appends to that very file.
    New-Item -ItemType File "$shared\trust.log" | Out-Null
    $line = "`"$shared\Dockyard.exe`" --machine-trust-ca $encoded --user-sid $($me.User.Value) --user-name `"$($me.Name)`" --log `"$shared\trust.log`" --keep-pid 0"
    Set-Content "$shared\trust.cmd" "$line`r`nexit /b %ERRORLEVEL%" -Encoding Ascii
    $action = New-ScheduledTaskAction -Execute cmd.exe -Argument "/c `"$shared\trust.cmd`""
    Register-ScheduledTask e2e-trust -Action $action -User "$env:COMPUTERNAME\e2e-admin" -Password $otherPassword -RunLevel Highest | Out-Null
    Start-ScheduledTask e2e-trust
    foreach ($i in 1..60) { Start-Sleep 1; if ((Get-ScheduledTask e2e-trust).State -eq 'Ready') { break } }
    $exit = (Get-ScheduledTaskInfo e2e-trust).LastTaskResult
    Unregister-ScheduledTask e2e-trust -Confirm:$false
    if (Test-Path "$shared\trust.log") { Get-Content "$shared\trust.log" | Write-Host }
    Check "exit 0 (was $exit)" ($exit -eq 0)
    Check 'the CA is back in the trust store' ((Get-ChildItem Cert:\LocalMachine\Root | Where-Object Subject -like '*Dockyard Local CA*') -ne $null)
    Remove-Item $shared -Recurse -Force
    Remove-LocalUser e2e-admin
    Linux-Engine
    $removing = Start-Process $docker -ArgumentList '--context', 'linux', 'rm', '-f', 'e2e-web' -PassThru -NoNewWindow
    if (-not $removing.WaitForExit(120000)) {
        Write-Host '--- docker rm hangs; what is there'
        Get-Process Dockyard* -ErrorAction SilentlyContinue | ForEach-Object { "$($_.ProcessName) pid $($_.Id) started $($_.StartTime)" } | Write-Host
        Write-Host "pipes: $((Get-ChildItem \\.\pipe\ | Where-Object Name -like 'dockyard*' | ForEach-Object Name) -join ', ')"
        wsl.exe -d dockyard -u root -- sh -c 'pidof dockerd dockyard-agent; ip -4 -o addr show eth0; netstat -ltn; docker ps -a' | Write-Host
        $removing.Kill()
        throw 'FAIL docker rm through the pipe did not return'
    }

    Section 'memory of the WSL engine, for the record'
    wsl.exe -d dockyard -u root -- sh -c 'for n in dockerd containerd dockyard-agent; do for p in $(pidof $n); do echo "$n RSS $(awk "/VmRSS/{print \$2}" /proc/$p/status) kB"; done; done; grep -E "MemTotal|MemAvailable" /proc/meminfo' | Write-Host

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
Linux-Engine
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

if ($Wizard) {
    Section 'a dashboard from v0.2.1, also running, is stopped and replaced by the new name'
    $previous = Join-Path (New-Item -ItemType Directory (Join-Path $env:TEMP ([guid]::NewGuid()))) 'Dockyard.exe'
    Invoke-WebRequest https://github.com/byhofman/dockyard/releases/download/v0.2.1/Dockyard.exe -OutFile $previous
    $p = Start-Process $previous -ArgumentList '--silent --dashboard --no-start' -Wait -PassThru
    Check "v0.2.1 installs (exit $($p.ExitCode))" ($p.ExitCode -eq 0)
    $oldExe = Join-Path $appDir 'Dockyard.Dashboard.exe'
    Check 'the old name is there to start from' (Test-Path $oldExe)
    $old = Start-Process $oldExe -PassThru
    Start-Sleep 5
    Check 'exit 0' ((Run --silent --no-start) -eq 0)
    Check 'the old process was stopped' ($old.HasExited)
    Check 'the old file is gone' (-not (Test-Path $oldExe))
    $newExe = Join-Path $appDir 'Dockyard Dashboard.exe'
    Check 'the new file is there' (Test-Path $newExe)
    Check 'the description Task Manager shows' ((Get-Item $newExe).VersionInfo.FileDescription -eq 'Dockyard Dashboard')
    $new = Start-Process $newExe -PassThru
    Start-Sleep 5
    Check 'the process is called Dockyard Dashboard' ((Get-Process -Id $new.Id).ProcessName -eq 'Dockyard Dashboard')
    Check 'exit 0 with it running' ((Run --silent --no-dashboard --no-start) -eq 0)
    Check 'removing it stops it' ($new.HasExited)
}

Section 'add the dashboard'
Check 'exit 0' ((Run --silent --dashboard --no-start) -eq 0)
Check 'dashboard added' (Test-Path $dashboard)
Check 'with its libraries' (Test-Path (Join-Path $appDir 'dashboard.files'))

if ($Wizard) {
    Section 'kind: installed, and a cluster from create to delete on the Linux engine'
    $kind = Join-Path $env:ProgramFiles 'Docker\kind.exe'
    Check 'kind.exe is next to docker.exe' (Test-Path $kind)
    Check "it runs: $(& $kind version)" ($LASTEXITCODE -eq 0)
    & $docker --context linux version *> $null
    if ($LASTEXITCODE -ne 0) { Linux-Engine }
    # As a new terminal has it: the machine PATH has the Docker folder, and kind follows the docker context.
    $env:PATH = (Split-Path $docker) + ';' + $env:PATH
    $env:DOCKER_CONTEXT = 'linux'
    try {
        & $kind create cluster --name e2e --wait 180s
        Check 'kind create cluster' ($LASTEXITCODE -eq 0)
        Check 'kind lists it' ((& $kind get clusters) -contains 'e2e')
        # The real dashboard with a cluster and its containers running, to look at in the run's screenshots.
        $shown = Start-Process (Resolve-Path $dashboard).Path -PassThru
        Start-Sleep 8
        Screen 'dashboard-containers'
        Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
        $auto = [System.Windows.Automation.AutomationElement]
        $until = (Get-Date).AddSeconds(60)
        do {
            $window = $auto::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
                (New-Object System.Windows.Automation.PropertyCondition ($auto::ProcessIdProperty), $shown.Id))
            if (-not $window) { Start-Sleep 1 }
        } until ($window -or (Get-Date) -gt $until)
        Check 'the dashboard window is there to click through' ($null -ne $window)
        # The sidebar's list item itself: the text inside it has the same name but cannot be selected.
        foreach ($section in 'Clusters', 'Images') {
            $item = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition ($auto::ControlTypeProperty), [System.Windows.Automation.ControlType]::ListItem),
                (New-Object System.Windows.Automation.PropertyCondition ($auto::NameProperty), $section))))
            Check "the sidebar has a $section item" ($null -ne $item)
            $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            Start-Sleep 3
            Check "$section is selected" $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
            Screen "dashboard-$($section.ToLower())"
        }
        Stop-Process -Id $shown.Id -Force -ErrorAction SilentlyContinue
        $server = ((& $kind get kubeconfig --name e2e) | Select-String 'server: (.+)$').Matches[0].Groups[1].Value.Trim()
        Check "kubeconfig points at $server" ($server -like 'https://127.0.0.1:*')
        $livez = (Invoke-WebRequest "$server/livez" -SkipCertificateCheck -UseBasicParsing -TimeoutSec 15).Content
        Check 'the API server answers on Windows loopback' ($livez -eq 'ok')
        & $kind delete cluster --name e2e
        Check 'deleting leaves no node' (-not (& $docker --context linux ps -aq --filter label=io.x-k8s.kind.cluster=e2e))
    }
    finally { Remove-Item Env:DOCKER_CONTEXT }
    Check 'exit 0 without kind' ((Run --silent --no-start --without kind) -eq 0)
    Check 'kind is gone' (-not (Test-Path $kind))
    Check 'exit 0 with kind' ((Run --silent --no-start --with kind) -eq 0)
    Check 'it is back' (Test-Path $kind)

    Section 'any component can go and come back, and required ones refuse'
    $compose = Join-Path $env:ProgramFiles 'Docker\cli-plugins\docker-compose.exe'
    Check 'exit 0 without compose' ((Run --silent --no-start --without compose) -eq 0)
    Check 'compose is gone' (-not (Test-Path $compose))
    Check 'exit 0 on a rerun' ((Run --silent --no-start) -eq 0)
    Check 'it stays gone' (-not (Test-Path $compose))
    Check 'exit 0 with compose' ((Run --silent --no-start --with compose) -eq 0)
    Check 'it is back' (Test-Path $compose)
    Check 'the last engine cannot go' ((Run --silent --no-start --without 'windows-engine,linux-engine') -eq 2)
    Check 'exit 0 without the Windows engine' ((Run --silent --no-start --without windows-engine) -eq 0)
    Check 'dockerd is gone' (-not (Test-Path (Join-Path $env:ProgramFiles 'Docker\dockerd.exe')))
    Check 'kind stays' (Test-Path $kind)
    Check 'the docker command stays' (Test-Path $docker)
    Check 'the service is gone' (-not (Get-Service docker -ErrorAction SilentlyContinue))
    Check 'the win context is gone' ((& $docker context ls -q) -notcontains 'win')
    Linux-Engine
    & $docker --context linux volume inspect dockyard-e2e | Out-Null
    Check 'the Linux engine still works' ($LASTEXITCODE -eq 0)
    Check 'exit 0 with the Windows engine' ((Run --silent --no-start --with windows-engine) -eq 0)
    Check 'dockerd is back' (Test-Path (Join-Path $env:ProgramFiles 'Docker\dockerd.exe'))
    Check 'the win context is back' ((& $docker context ls -q) -contains 'win')
    $routed = { (Get-DnsClientNrptRule | Where-Object { $_.Namespace -contains '.dockyard.test' }) -ne $null }
    Check 'exit 0 without the Linux engine' ((Run --silent --no-start --without linux-engine) -eq 0)
    Check 'names no longer go to Dockyard' (-not (& $routed))
    Check 'the linux context is gone' ((& $docker context ls -q) -notcontains 'linux')
    Check 'exit 0 with the Linux engine' ((Run --silent --no-start --with linux-engine) -eq 0)
    Check 'names go to Dockyard again' (& $routed)
    Linux-Engine
    & $docker --context linux volume inspect dockyard-e2e | Out-Null
    Check 'its images and volumes were still there' ($LASTEXITCODE -eq 0)
}

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
if ($Wizard) {
    '{"ServerURL":"e2e.dockyard.test","Username":"me","Secret":"s3cret"}' | & (Join-Path $env:ProgramFiles 'Docker\docker-credential-wincred.exe') store
    $said = New-TemporaryFile
    $p = Start-Process (Resolve-Path $Wizard) -ArgumentList '--silent', '--uninstall' -Wait -PassThru -NoNewWindow -RedirectStandardOutput $said
    Get-Content $said | Write-Host
    Check 'exit 0' ($p.ExitCode -eq 0)
    Check 'it says which login stays in the store' ((Get-Content $said -Raw) -match 'e2e\.dockyard\.test.*Windows Credential Manager')
    cmdkey /delete:e2e.dockyard.test | Out-Null
} else {
    Check 'exit 0' ((Run --silent --uninstall) -eq 0)
}
Check 'app removed' (-not (Test-Path $appExe))
$distros = (wsl.exe -l -q | Out-String) -replace "`0", ''
Check 'the dockyard distro is kept' ($distros -match 'dockyard')
if ($Wizard) {
    Check 'the CA is out of the trust store' ((Get-ChildItem Cert:\LocalMachine\Root | Where-Object Subject -like '*Dockyard Local CA*') -eq $null)
    Check 'and names no longer go to Dockyard' ((Get-DnsClientNrptRule | Where-Object { $_.Namespace -contains '.dockyard.test' }) -eq $null)
}
Check 'no credential store left in the config' (-not ((Test-Path "$env:USERPROFILE\.docker\config.json") -and ((Get-Content "$env:USERPROFILE\.docker\config.json" -Raw) -match 'credsStore')))
Check 'our .wslconfig line is gone' (-not ((Test-Path "$env:USERPROFILE\.wslconfig") -and ((Get-Content "$env:USERPROFILE\.wslconfig" -Raw) -match 'networkingMode=mirrored')))

Section 'a fresh install gets the dashboard'
Check 'exit 0' ((Run --silent --no-start) -eq 0)
Check 'dashboard there' (Test-Path $dashboard)
Linux-Engine
& $docker --context linux volume inspect dockyard-e2e | Out-Null
Check 'marker volume back with the distro' ($LASTEXITCODE -eq 0)

Write-Host 'All good.' -ForegroundColor Green
