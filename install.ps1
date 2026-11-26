<#
  Dockyard for Windows. This only fetches the Dockyard Toolbox, checks it and starts it; the Toolbox does all the rest:

    irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1 | iex

  Arguments go to the Toolbox:

    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1))) --silent --no-dashboard
#>
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    $repo = 'byhofman/dockyard'
    $name = 'Dockyard.exe'
    # A fixed path: Defender ASR exclusions go by path, and machines may already have this one excluded.
    $dir = Join-Path $env:USERPROFILE 'Downloads'
    $exe = Join-Path $dir $name

    if ($env:DOCKYARD_VERSION) {
        $tag = "toolbox/$env:DOCKYARD_VERSION"
    } else {
        # The redirect names one release, so the file and its checksum come from the same one, and it has no API rate limit.
        $request = [Net.WebRequest]::Create("https://github.com/$repo/releases/latest")
        $request.Method = 'HEAD'
        $response = $request.GetResponse()
        $tag = [Uri]::UnescapeDataString(($response.ResponseUri.AbsolutePath -split '/releases/tag/', 2)[-1])
        $response.Dispose()
    }
    if ($tag -notmatch '^toolbox/\d') { throw 'No Dockyard release found.' }

    New-Item -ItemType Directory -Force $dir | Out-Null
    Write-Host "Downloading the Dockyard Toolbox ($tag)"
    $base = "https://github.com/$repo/releases/download/$tag"
    Invoke-WebRequest "$base/$name" -OutFile $exe -UseBasicParsing
    $sumsFile = Join-Path $dir 'Dockyard.SHA256SUMS'
    Invoke-WebRequest "$base/SHA256SUMS" -OutFile $sumsFile -UseBasicParsing
    $sums = Get-Content $sumsFile
    Remove-Item $sumsFile -Force
    $line = $sums | Where-Object { $_.Trim() -match "\s\*?$([regex]::Escape($name))$" } | Select-Object -First 1
    if (-not $line -or (Get-FileHash $exe -Algorithm SHA256).Hash -ne ($line.Trim() -split '\s+')[0]) {
        Remove-Item $exe -Force
        throw "The checksum of $name does not match the one in $tag, not starting it."
    }

    $parameters = @{ FilePath = $exe; Wait = $true; PassThru = $true; NoNewWindow = $true }
    if ($args) { $parameters.ArgumentList = @($args | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) }
    try {
        $process = Start-Process @parameters
    } catch {
        throw "Windows did not let $exe start ($_). Dockyard's builds are not signed, so Defender may block a new one; allow it under Windows Security > Virus & threat protection > Protection history, or ask your administrator on a managed device, then run this again."
    }

    $global:LASTEXITCODE = $process.ExitCode
    switch ($process.ExitCode) {
        0 { }
        3010 { Write-Host 'Restart Windows to finish. Dockyard carries on by itself after the restart.' -ForegroundColor Yellow }
        default { throw "The Dockyard Toolbox failed (exit code $($process.ExitCode)). See the output above." }
    }
} @args
