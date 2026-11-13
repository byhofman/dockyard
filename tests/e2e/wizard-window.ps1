<#
  Drives the wizard's real window through UI Automation, as the owner would: opens it on an install without the
  dashboard, sees the versions, adds the dashboard and removes it again. Windows PowerShell 5.1 has UI Automation.
    wizard-window.ps1 -Exe <Dockyard.exe> -Screenshots <dir> -Dashboard <path of the dashboard, wildcards allowed> [-LookOnly <name>]
#>
param([Parameter(Mandatory)][string]$Exe, [Parameter(Mandatory)][string]$Screenshots, [Parameter(Mandatory)][string]$Dashboard, [string]$LookOnly, [string]$Assets)

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

# By the row's id: every optional row has an Add or a Remove now. A release from before that has only the dashboard's.
function Find-Button($window, $name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($auto::AutomationIdProperty, "$name-Dashboard")
    $byId = $window.FindFirst($tree, $condition)
    if ($byId) { return $byId }
    if ($name -eq 'Check for updates' -or -not (Find-Named $window 'Docker CLI')) { Find-Named $window $name }
}

function Click($window, $name) {
    # A button is there but disabled while the window is busy with what the last click started.
    $button = Wait-For "the $name button" { $b = Find-Button $window $name; if ($b -and $b.Current.IsEnabled) { $b } }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$process = if ($Assets) { Start-Process $Exe -ArgumentList '--assets', $Assets -PassThru } else { Start-Process $Exe -PassThru }
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
    Wait-For 'the Remove button' { Find-Button $window 'Remove' } | Out-Null
    Shot 'added'
    Write-Host '  ok   Add installs the skipped dashboard'

    # A build of this branch is version 0.0.0, so the release that is out is an update for its dashboard; a tagged build is the newest there is.
    $version = (Get-Item $Dashboard).VersionInfo.ProductVersion
    Write-Host "  the dashboard says it is version '$version', the wizard '$((Get-Item $Exe).VersionInfo.ProductVersion)'"
    if (-not $version) { throw 'FAIL the dashboard file has no version, so the wizard cannot show or update it' }
    if ((Get-Item $Exe).VersionInfo.ProductVersion -like '0.0.0*') {
        Click $window 'Check for updates'
        Click $window 'Update'
        Wait-For 'the dashboard to be updated' { (Get-Item $Dashboard).VersionInfo.ProductVersion -notlike '0.0.0*' } | Out-Null
        Wait-For 'the Remove button' { Find-Button $window 'Remove' } | Out-Null
        Shot 'updated'
        Write-Host "  ok   Update brings the dashboard to $((Get-Item $Dashboard).VersionInfo.ProductVersion) and it stays removable"
    }

    Click $window 'Remove'
    Wait-For 'the dashboard to go' { -not (Test-Path $Dashboard) } | Out-Null
    Wait-For 'the Add button' { Find-Button $window 'Add' } | Out-Null
    Shot 'removed'
    Write-Host '  ok   Remove takes it away again'
} finally {
    if (-not $process.HasExited) { $process.Kill() }
}
