<#
  Measures the dashboard against the performance budgets in the dashboard spec.
  Run with the engine of the active docker context running:

    pwsh scripts/measure.ps1 [-Dashboard "C:\Program Files\Dockyard\Dockyard.Dashboard.exe"]
#>
param(
    [string]$Dashboard = (Join-Path $env:ProgramFiles 'Dockyard\Dockyard.Dashboard.exe'),
    [int]$IdleSeconds = 10
)

$ErrorActionPreference = 'Stop'
Get-Process Dockyard.Dashboard -ErrorAction SilentlyContinue | Stop-Process
Start-Sleep -Milliseconds 500

$clock = [Diagnostics.Stopwatch]::StartNew()
$p = Start-Process $Dashboard -PassThru
while ($p.MainWindowHandle -eq 0 -and $clock.ElapsedMilliseconds -lt 15000) {
    Start-Sleep -Milliseconds 10
    $p.Refresh()
}
$windowMs = $clock.ElapsedMilliseconds

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
