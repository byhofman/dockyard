# install.ps1 only fetches the dockyard command, checks it and runs dockyard setup. Fake downloads and a fake dockyard,
# on a Windows runner.
$ErrorActionPreference = 'Stop'
$script = Get-Content (Join-Path $PSScriptRoot '../../install.ps1') -Raw

function Ok($text) { Write-Host "  ok   $text" }
function Fail($text) { throw "FAIL $text" }

$work = New-Item -ItemType Directory (Join-Path $env:TEMP ([guid]::NewGuid()))
$env:USERPROFILE_REAL = $env:USERPROFILE
$env:USERPROFILE = $work.FullName
$asset = 'dockyard-windows-x64.exe'

# A small real exe that records its arguments, compiled here so the script's Start-Process is the real one.
$fake = Join-Path $work 'fake.exe'
$record = Join-Path $work 'args.txt'
$source = Join-Path $work 'fake.cs'
@"
public static class P { public static int Main(string[] a) {
  System.IO.File.WriteAllText(@"$record", string.Join(" ", a));
  return a.Length > 1 && a[1] == "--fail" ? 7 : a.Length > 1 && a[1] == "--reboot" ? 3010 : 0;
} }
"@ | Set-Content $source
# PowerShell 7 cannot compile an exe; Windows PowerShell, on every Windows, can.
powershell.exe -NoProfile -Command "Add-Type -OutputAssembly '$fake' -OutputType ConsoleApplication -TypeDefinition (Get-Content '$source' -Raw)"
if ($LASTEXITCODE -ne 0) { Fail 'could not compile the fake dockyard' }
$hash = (Get-FileHash $fake -Algorithm SHA256).Hash

$global:index = '{ "tray": { "version": "9.8.0", "tag": "tray/9.8.0" }, "cli": { "version": "9.9.9", "tag": "cli/9.9.9" } }'
$global:sums = "$hash  $asset`n"
$global:requests = @()
function global:Invoke-WebRequest {
    param([Parameter(Position = 0)][string]$Uri, [string]$OutFile, [switch]$UseBasicParsing)
    $global:requests += $Uri
    if ($Uri -like '*/releases/download/index/components.json') { return [pscustomobject]@{ Content = $global:index } }
    if ($Uri -notlike '*/releases/download/cli/*') { throw "404 $Uri" }
    if ($Uri -like '*/SHA256SUMS') { Set-Content $OutFile $global:sums -NoNewline; return }
    Copy-Item $fake $OutFile
}

function Run { & ([scriptblock]::Create($script)) @args }

Write-Host '== passes setup and its arguments through and installs nothing itself'
Run setup --silent --without tray --without dashboard | Out-Null
if ((Get-Content $record -Raw) -ne 'setup --silent --without tray --without dashboard') { Fail "arguments were: $(Get-Content $record -Raw)" }
Ok 'arguments'
$expected = @('https://github.com/byhofman/dockyard/releases/download/index/components.json',
    "https://github.com/byhofman/dockyard/releases/download/cli/9.9.9/$asset",
    'https://github.com/byhofman/dockyard/releases/download/cli/9.9.9/SHA256SUMS')
if (($global:requests -join ' ') -ne ($expected -join ' ')) { Fail "downloads: $($global:requests -join ', ')" }
Ok 'reads components.json and downloads only the command and its checksums'
foreach ($word in 'Add-MpPreference', 'wsl(\.exe)? --', '--install', 'Program ?Files', 'gh run', 'tray-windows', 'Dashboard\.zip') {
    if ($script -match $word) { Fail "the script still does install work: $word" }
}
Ok 'no install logic in the script'

Write-Host '== without arguments it runs dockyard setup'
Run | Out-Null
if ((Get-Content $record -Raw) -ne 'setup') { Fail "arguments were: $(Get-Content $record -Raw)" }
Ok 'setup'

Write-Host '== DOCKYARD_VERSION names the command''s release'
$global:requests = @()
$env:DOCKYARD_VERSION = '9.9.9'
Run setup | Out-Null
Remove-Item Env:DOCKYARD_VERSION
if ($global:requests -like '*components.json' -or -not ($global:requests -like '*/cli/9.9.9/*')) { Fail "downloads: $($global:requests -join ', ')" }
Ok 'no index, the cli release named'

Write-Host '== a failing dockyard is a failure, a reboot is not'
$threw = $false; try { Run setup --fail | Out-Null } catch { $threw = $true }
if (-not $threw) { Fail 'exit code 7 did not throw' }
Ok 'exit code 7 throws'
Run setup --reboot | Out-Null
if ($global:LASTEXITCODE -ne 3010) { Fail "LASTEXITCODE is $global:LASTEXITCODE" }
Ok '3010 passes as the exit code'

Write-Host '== a bad checksum stops it'
Remove-Item $record
$global:sums = "$('0' * 64)  $asset`n"
$threw = $false; try { Run setup --silent | Out-Null } catch { $threw = $true }
if (-not $threw) { Fail 'no failure on a bad checksum' }
if (Test-Path $record) { Fail 'ran a file with a bad checksum' }
if (Test-Path (Join-Path $work "Downloads\$asset")) { Fail 'left the file behind' }
Ok 'not run, not left behind'

Write-Host '== checksums without the command stop it'
$global:sums = "$hash  other.exe`n"
$threw = $false; try { Run setup --silent | Out-Null } catch { $threw = $true }
if (-not $threw -or (Test-Path $record)) { Fail 'ran without a listed checksum' }
Ok 'not run'

Write-Host '== an index without the command stops it'
$global:sums = "$hash  $asset`n"
$global:index = '{ "tray": { "version": "9.8.0", "tag": "tray/9.8.0" } }'
$threw = $false; try { Run setup --silent | Out-Null } catch { $threw = $true }
if (-not $threw -or (Test-Path $record)) { Fail 'ran without a cli release' }
Ok 'not run'

$env:USERPROFILE = $env:USERPROFILE_REAL
Write-Host 'All good.' -ForegroundColor Green
# The fake dockyard's last exit code would otherwise become this script's.
exit 0
