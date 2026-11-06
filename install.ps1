<#
  Dockyard for Windows. This only fetches the installer, checks it and starts it:

    irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1 | iex

  Arguments go to the installer:

    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/byhofman/dockyard/main/install.ps1))) --silent --no-dashboard

  -Build fetches the latest build of main instead of the latest release (needs the gh CLI, signed in).
#>
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    $repo = 'byhofman/dockyard'
    $name = 'Dockyard.exe'
    # A fixed path: Defender ASR exclusions go by path, and machines may already have this one excluded.
    $dir = Join-Path $env:USERPROFILE 'Downloads'
    $exe = Join-Path $dir $name

    $build = $false
    $arguments = foreach ($arg in $args) {
        switch ($arg) {
            '-Build' { $build = $true }
            '-EngineOnly' { '--no-dashboard' }
            '-NoStart' { '--no-start' }
            default { $arg }
        }
    }

    New-Item -ItemType Directory -Force $dir | Out-Null
    # Left by earlier one-liners. The installer prefers files next to it, and these would be old.
    Remove-Item $exe, (Join-Path $dir 'Dockyard.Dashboard.zip'), (Join-Path $dir 'dockyard-linux-*-x86_64.tar.gz') -Force -ErrorAction SilentlyContinue

    if ($build) {
        if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw '-Build needs the gh CLI (GitHub only serves build artifacts to signed-in users).' }
        $run = gh run list -R $repo -b main -w build -s success -L 1 --json databaseId -q '.[0].databaseId'
        if (-not $run) { throw "No successful build of $repo found." }
        foreach ($artifact in 'windows', 'image') { gh run download $run -R $repo -n $artifact -D $dir }
        Write-Host "Latest build of main (run $run)"
    } else {
        if ($env:DOCKYARD_VERSION) {
            $tag = "v$env:DOCKYARD_VERSION"
        } else {
            # The redirect names one release, so the file and its checksum come from the same one, and it has no API rate limit.
            $request = [Net.WebRequest]::Create("https://github.com/$repo/releases/latest")
            $request.Method = 'HEAD'
            $response = $request.GetResponse()
            $tag = $response.ResponseUri.Segments[-1]
            $response.Dispose()
        }
        if ($tag -notmatch '^v\d') { throw 'No Dockyard release found.' }

        Write-Host "Downloading the Dockyard installer ($tag)"
        $base = "https://github.com/$repo/releases/download/$tag"
        Invoke-WebRequest "$base/$name" -OutFile $exe -UseBasicParsing
        $sumsFile = Join-Path $dir 'Dockyard.SHA256SUMS'
        Invoke-WebRequest "$base/SHA256SUMS" -OutFile $sumsFile -UseBasicParsing
        $sums = Get-Content $sumsFile
        Remove-Item $sumsFile -Force
        $line = $sums | Where-Object { $_.Trim() -match "\s\*?$([regex]::Escape($name))$" } | Select-Object -First 1
        if (-not $line) { Remove-Item $exe -Force; throw "SHA256SUMS of $tag does not list $name, not starting it." }
        if ((Get-FileHash $exe -Algorithm SHA256).Hash -ne ($line.Trim() -split '\s+')[0]) {
            Remove-Item $exe -Force
            throw "Checksum mismatch for $name, not starting it."
        }
    }

    function Start-Installer {
        $parameters = @{ FilePath = $exe; Wait = $true; PassThru = $true; NoNewWindow = $true }
        if ($arguments) { $parameters.ArgumentList = @($arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) }
        Start-Process @parameters
    }

    try {
        $process = Start-Installer
    } catch {
        # Defender's prevalence rule blocks a new build before it can ask for anything (event 1121).
        Write-Host 'Windows blocked the installer. Asking for administrator approval to allow it.'
        $allow = "Add-MpPreference -AttackSurfaceReductionOnlyExclusions '$exe'"
        Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $allow
        try {
            $process = Start-Installer
        } catch {
            throw "Windows still blocks $exe ($_). On a managed device, your administrator has to allow this path."
        }
    }

    $global:LASTEXITCODE = $process.ExitCode
    switch ($process.ExitCode) {
        0 { }
        3010 { Write-Host 'Restart Windows to finish. Dockyard carries on by itself after the restart.' -ForegroundColor Yellow }
        default { throw "The Dockyard installer failed (exit code $($process.ExitCode)). See the output above." }
    }
} @args
