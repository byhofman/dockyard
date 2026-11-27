<#
  Dockyard for Windows. This only fetches the dockyard command, checks it and runs dockyard setup, which does all the rest:

    irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1 | iex

  Arguments go to setup:

    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1))) --silent --without dashboard
#>
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    $repo = 'byhofman/dockyard'
    $name = 'dockyard-windows-x64.exe'
    # A fixed path: Defender ASR exclusions go by path, and machines may already have this one excluded.
    $dir = Join-Path $env:USERPROFILE 'Downloads'
    $exe = Join-Path $dir $name

    if ($env:DOCKYARD_VERSION) {
        $tag = "cli/v$($env:DOCKYARD_VERSION.TrimStart('v'))"
    } else {
        # components.json names each component's latest release, from github.com, which has no API rate limit.
        # GitHub serves release assets as application/octet-stream, which Windows PowerShell 5.1 hands over as bytes.
        $content = (Invoke-WebRequest "https://github.com/$repo/releases/download/index/components.json" -UseBasicParsing).Content
        if ($content -is [byte[]]) { $content = [Text.Encoding]::UTF8.GetString($content) }
        $index = $content | ConvertFrom-Json
        $tag = $index.cli.tag
    }
    if ($tag -notmatch '^cli/v?\d') { throw 'No Dockyard release found.' }

    New-Item -ItemType Directory -Force $dir | Out-Null
    Write-Host "Downloading the dockyard command ($tag)"
    $base = "https://github.com/$repo/releases/download/$tag"
    Invoke-WebRequest "$base/$name" -OutFile $exe -UseBasicParsing
    $sumsFile = Join-Path $dir 'dockyard.SHA256SUMS'
    Invoke-WebRequest "$base/SHA256SUMS" -OutFile $sumsFile -UseBasicParsing
    $sums = Get-Content $sumsFile
    Remove-Item $sumsFile -Force
    $line = $sums | Where-Object { $_.Trim() -match "\s\*?$([regex]::Escape($name))$" } | Select-Object -First 1
    if (-not $line -or (Get-FileHash $exe -Algorithm SHA256).Hash -ne ($line.Trim() -split '\s+')[0]) {
        Remove-Item $exe -Force
        throw "The checksum of $name does not match the one in $tag, not running it."
    }

    $arguments = @('setup') + @($args)
    $parameters = @{ FilePath = $exe; Wait = $true; PassThru = $true; NoNewWindow = $true }
    $parameters.ArgumentList = @($arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } })
    try {
        $process = Start-Process @parameters
    } catch {
        throw "Windows did not let $exe start ($_). Dockyard's builds are not signed, so Defender may block a new one; allow it under Windows Security > Virus & threat protection > Protection history, or ask your administrator on a managed device, then run this again."
    }

    $global:LASTEXITCODE = $process.ExitCode
    switch ($process.ExitCode) {
        0 { }
        3010 { Write-Host 'Restart Windows to finish. Dockyard carries on by itself after the restart.' -ForegroundColor Yellow }
        default { throw "dockyard $($arguments -join ' ') failed (exit code $($process.ExitCode)). See the output above." }
    }
} @args
