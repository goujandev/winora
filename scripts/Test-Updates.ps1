param([switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$testDirectory = Join-Path $workspace ('artifacts/update-check-' + [guid]::NewGuid().ToString('N'))
$feed = Join-Path $testDirectory 'feed'
$installation = Join-Path $testDirectory 'installed'
$reportBefore = Join-Path $testDirectory 'before.json'
$reportUpdate = Join-Path $testDirectory 'update.json'
$reportAfter = Join-Path $testDirectory 'after.json'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$savedSource = $env:WINORA_UPDATE_SOURCE
$savedProbe = $env:WINORA_STARTUP_PROBE
$env:WINORA_UPDATE_SOURCE = $feed
$env:WINORA_STARTUP_PROBE = $reportBefore
try {
    & (Join-Path $PSScriptRoot 'Pack.ps1') -Version '0.1.0' -PackageId 'Winora.UpdateTest' -OutputDirectory $feed -SelfContained:$SelfContained
    $setup = Join-Path $feed 'Winora.UpdateTest-win-Setup.exe'
    # Do not pass EXE_ARGS: upstream Velopack 1.2.0 has a parser bug for that option.
    $process = Start-Process -FilePath $setup -ArgumentList '--silent','--installto',"`"$installation`"",'--log',"`"$(Join-Path $testDirectory 'setup.log')`"" -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(120000)) { $process.Kill(); throw 'Test installer exceeded its two-minute timeout. See setup.log.' }
    if ($process.ExitCode -ne 0) { throw "Test install failed ($($process.ExitCode))." }
    $launcher = Join-Path $installation 'Winora.exe'
    if (-not (Test-Path -LiteralPath $reportBefore)) {
        $process = Start-Process -FilePath $launcher -ArgumentList '--update-probe',"`"$reportBefore`"" -WindowStyle Hidden -Wait -PassThru
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $reportBefore) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path -LiteralPath $reportBefore)) { throw 'Installed app did not produce the initial update report.' }
    $before = Get-Content -LiteralPath $reportBefore -Raw | ConvertFrom-Json
    if (-not $before.IsInstalled -or $before.Version -ne '0.1.0' -or $before.HasUpdate) { throw 'Initial installed state is incorrect.' }
    & (Join-Path $PSScriptRoot 'Pack.ps1') -Version '0.1.1' -PackageId 'Winora.UpdateTest' -OutputDirectory $feed -SelfContained:$SelfContained
    $process = Start-Process -FilePath $launcher -ArgumentList '--update-probe',"`"$reportUpdate`"",'--apply-update' -WindowStyle Hidden -Wait -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $installedDll = Join-Path $installation 'current/Winora.dll'
    do {
        Start-Sleep -Milliseconds 250
        $version = if (Test-Path -LiteralPath $installedDll) { [Diagnostics.FileVersionInfo]::GetVersionInfo($installedDll).FileVersion } else { '' }
    } while ($version -notlike '0.1.1*' -and [DateTime]::UtcNow -lt $deadline)
    if ($version -notlike '0.1.1*') { throw 'The installed app did not update to 0.1.1.' }
    $process = Start-Process -FilePath $launcher -ArgumentList '--update-probe',"`"$reportAfter`"" -WindowStyle Hidden -Wait -PassThru
    if (-not (Test-Path -LiteralPath $reportAfter)) { throw 'Updated app did not produce a report.' }
    $after = Get-Content -LiteralPath $reportAfter -Raw | ConvertFrom-Json
    $update = Get-Content -LiteralPath $reportUpdate -Raw | ConvertFrom-Json
    if ($after.Version -ne '0.1.1' -or -not $after.IsInstalled -or $after.HasUpdate -or -not $update.HasUpdate) { throw 'Update verification failed.' }
    Write-Output 'PASS: installed 0.1.0, downloaded the update, applied 0.1.1, and verified the installed app is current.'
    Write-Output "Reports: $testDirectory"
} finally {
    $env:WINORA_UPDATE_SOURCE = $savedSource
    $env:WINORA_STARTUP_PROBE = $savedProbe
    $updater = Join-Path $installation 'Update.exe'
    if (Test-Path -LiteralPath $updater) {
        $process = Start-Process -FilePath $updater -ArgumentList 'uninstall','--silent' -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { Write-Warning 'The isolated test installation needs manual cleanup.' }
    }
}
