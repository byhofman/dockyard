<#
  Drives the wizard's real window through UI Automation, as the owner would: opens it on an install without the
  dashboard, sees the versions, adds the dashboard and removes it again. Windows PowerShell 5.1 has UI Automation.
    wizard-window.ps1 -Exe <Dockyard.exe> -Screenshots <dir> -Dashboard <path of the dashboard, wildcards allowed> [-LookOnly <name>]
#>
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Screenshots, [Parameter(Mandatory)][string]$Dashboard, [string]$LookOnly)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
$auto = [System.Windows.Automation.AutomationElement]
$tree = [System.Windows.Automation.TreeScope]::Descendants

function Shot($name) {
    New-Item -ItemType Directory -Force $Screenshots | Out-Null
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    [System.Drawing.Graphics]::FromImage($bitmap).CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save((Join-Path $Screenshots "$name.png"))
    $bitmap.Dispose()
}

function Wait-For($what, [scriptblock]$condition, $seconds = 120) {
    $until = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $until) {
        $value = & $condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 500
    }
    Shot 'timeout'
    throw "FAIL waiting for $what"
}

function Find-Named($window, $name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty, $name)
    $window.FindFirst($tree, $condition)
}

function Click($window, $name) {
    $button = Wait-For "the $name button" { Find-Named $window $name }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$process = Start-Process $Exe -PassThru
try {
    $window = Wait-For 'the wizard window' {
        $process.Refresh()
        if ($process.HasExited) { throw "FAIL the wizard exited with $($process.ExitCode)" }
        if ($process.MainWindowHandle -ne 0) { $auto::FromHandle($process.MainWindowHandle) }
    }
    Write-Host '  ok   the wizard window opens'

    Wait-For 'the manage view' { Find-Named $window 'MANAGE' } | Out-Null
    Write-Host '  ok   it shows the manage view on an existing install'
    Wait-For 'the Dashboard row' { Find-Named $window 'Dashboard' } | Out-Null
    Write-Host '  ok   it lists the dashboard'
    Start-Sleep 8
    Shot ($(if ($LookOnly) { $LookOnly } else { 'manage' }))
    if ($LookOnly) { return }

    if (Test-Path $Dashboard) { throw 'FAIL expected to start without the dashboard' }
    Click $window 'Add'
    Wait-For 'the dashboard to arrive' { Test-Path $Dashboard } | Out-Null
    Wait-For 'the Remove button' { Find-Named $window 'Remove' } | Out-Null
    Shot 'added'
    Write-Host '  ok   Add installs the skipped dashboard'

    Click $window 'Remove'
    Wait-For 'the dashboard to go' { -not (Test-Path $Dashboard) } | Out-Null
    Wait-For 'the Add button' { Find-Named $window 'Add' } | Out-Null
    Shot 'removed'
    Write-Host '  ok   Remove takes it away again'
} finally {
    if (-not $process.HasExited) { $process.Kill() }
}
