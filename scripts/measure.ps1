<#
  Measures the dashboard against the performance budgets in the dashboard spec.
  Run with the engine of the active docker context running:

    pwsh scripts/measure.ps1 [-Dashboard "C:\Program Files\Dockyard\Dockyard Dashboard.exe"]
#>
param(
    [string]$Dashboard = (Join-Path $env:ProgramFiles 'Dockyard\Dockyard Dashboard.exe'),
    [int]$IdleSeconds = 10
)

$ErrorActionPreference = 'Stop'
Get-Process "Dockyard Dashboard", Dockyard.Dashboard -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 500

# The dashboard reports when its window opened, measured from its own process start (DOCKYARD_TRACE_STARTUP);
# polling from here would add PowerShell's process-start and polling overhead to the number.
$trace = Join-Path ([IO.Path]::GetTempPath()) "dockyard-startup-$PID.txt"
$env:DOCKYARD_TRACE_STARTUP = '1'
$p = Start-Process $Dashboard -PassThru -RedirectStandardError $trace
Remove-Item Env:\DOCKYARD_TRACE_STARTUP
$clock = [Diagnostics.Stopwatch]::StartNew()
$windowMs = $null
while ($null -eq $windowMs -and $clock.ElapsedMilliseconds -lt 15000) {
    Start-Sleep -Milliseconds 50
    $line = Get-Content $trace -ErrorAction SilentlyContinue | Where-Object { $_ -match 'window opened' } | Select-Object -First 1
    if ($line -match 'startup\s+(\d+) ms') { $windowMs = [int]$Matches[1] }
}
if ($null -eq $windowMs) { throw 'The dashboard did not report an opened window within 15 s' }
$p.Refresh()

Start-Sleep 3
$p.Refresh()
$cpuBefore = $p.TotalProcessorTime
Start-Sleep $IdleSeconds
$p.Refresh()
$cpuPercent = ($p.TotalProcessorTime - $cpuBefore).TotalMilliseconds / ($IdleSeconds * 1000) / [Environment]::ProcessorCount * 100
$dashboardMb = $p.PrivateMemorySize64 / 1MB

$tray = Get-Process Dockyard -ErrorAction SilentlyContinue | Select-Object -First 1
$trayMb = if ($tray) { $tray.PrivateMemorySize64 / 1MB } else { $null }

function Row($name, $value, $budget, $ok) {
    [pscustomobject]@{ Metric = $name; Value = $value; Budget = $budget; Result = if ($null -eq $ok) { 'n/a' } elseif ($ok) { 'ok' } else { 'OVER' } }
}

@(
    Row 'Window visible' ("{0} ms" -f $windowMs) '< 300 ms' ($windowMs -lt 300)
    Row 'Idle CPU (dashboard open)' ("{0:N2} %" -f $cpuPercent) '< 1 %' ($cpuPercent -lt 1)
    Row 'Private memory (dashboard)' ("{0:N0} MB" -f $dashboardMb) '< 120 MB' ($dashboardMb -lt 120)
    Row 'Private memory (tray)' $(if ($trayMb) { "{0:N0} MB" -f $trayMb } else { 'not running' }) '< 60 MB' $(if ($trayMb) { $trayMb -lt 60 } else { $null })
) | Format-Table -AutoSize

Stop-Process $p
