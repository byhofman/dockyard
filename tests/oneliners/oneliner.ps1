# install.ps1 only fetches, checks and starts the installer. Fake downloads and a fake Dockyard.exe, on a Windows runner.
$ErrorActionPreference = 'Stop'
$script = Get-Content (Join-Path $PSScriptRoot '../../install.ps1') -Raw

function Ok($text) { Write-Host "  ok   $text" }
function Fail($text) { throw "FAIL $text" }

$work = New-Item -ItemType Directory (Join-Path $env:TEMP ([guid]::NewGuid()))
$env:USERPROFILE_REAL = $env:USERPROFILE
$env:USERPROFILE = $work.FullName
$env:DOCKYARD_VERSION = '9.9.9'

# A small real exe that records its arguments, compiled here so the script's Start-Process is the real one.
$fake = Join-Path $work 'fake.exe'
$record = Join-Path $work 'args.txt'
Add-Type -OutputAssembly $fake -OutputType ConsoleApplication -TypeDefinition @"
public static class P { public static int Main(string[] a) {
  System.IO.File.WriteAllText(@"$record", string.Join(" ", a));
  return a.Length > 0 && a[0] == "--fail" ? 7 : a.Length > 0 && a[0] == "--reboot" ? 3010 : 0;
} }
"@
$hash = (Get-FileHash $fake -Algorithm SHA256).Hash

$global:sums = "$hash  Dockyard.exe`n"
$global:requests = @()
function global:Invoke-WebRequest {
    param([Parameter(Position = 0)][string]$Uri, [string]$OutFile, [switch]$UseBasicParsing)
    $global:requests += $Uri
    if ($Uri -like '*/SHA256SUMS') { Set-Content $OutFile $global:sums -NoNewline; return }
    Copy-Item $fake $OutFile
}

function Run { & ([scriptblock]::Create($script)) @args }

Write-Host '== passes the arguments through and installs nothing itself'
Run --silent --no-dashboard --no-start | Out-Null
if ((Get-Content $record -Raw) -ne '--silent --no-dashboard --no-start') { Fail "arguments were: $(Get-Content $record -Raw)" }
Ok 'arguments'
if ($global:requests.Count -ne 2) { Fail "downloads: $($global:requests -join ', ')" }
Ok 'downloads only the installer and its checksums'
foreach ($word in 'Add-MpPreference.*Dockyard\.Dashboard', 'wsl(\.exe)? --', '--install', 'Program ?Files') {
    if ($script -match $word) { Fail "the script still does install work: $word" }
}
Ok 'no install logic in the script'

Write-Host '== earlier option names still work'
Run -EngineOnly -NoStart | Out-Null
if ((Get-Content $record -Raw) -ne '--no-dashboard --no-start') { Fail "arguments were: $(Get-Content $record -Raw)" }
Ok '-EngineOnly and -NoStart'

Write-Host '== a failing installer is a failure, a reboot is not'
$threw = $false; try { Run --fail | Out-Null } catch { $threw = $true }
if (-not $threw) { Fail 'exit code 7 did not throw' }
Ok 'exit code 7 throws'
Run --reboot | Out-Null
if ($global:LASTEXITCODE -ne 3010) { Fail "LASTEXITCODE is $global:LASTEXITCODE" }
Ok '3010 passes as the exit code'

Write-Host '== a bad checksum stops it'
Remove-Item $record
$global:sums = "$('0' * 64)  Dockyard.exe`n"
$threw = $false; try { Run --silent | Out-Null } catch { $threw = $true }
if (-not $threw) { Fail 'no failure on a bad checksum' }
if (Test-Path $record) { Fail 'started a file with a bad checksum' }
if (Test-Path (Join-Path $work 'Downloads\Dockyard.exe')) { Fail 'left the file behind' }
Ok 'not started, not left behind'

Write-Host '== checksums without the installer stop it'
$global:sums = "$hash  other.exe`n"
$threw = $false; try { Run --silent | Out-Null } catch { $threw = $true }
if (-not $threw -or (Test-Path $record)) { Fail 'started without a listed checksum' }
Ok 'not started'

$env:USERPROFILE = $env:USERPROFILE_REAL
Write-Host 'All good.' -ForegroundColor Green
