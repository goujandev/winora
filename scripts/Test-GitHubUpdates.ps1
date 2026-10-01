param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$BaselineVersion = '0.1.0',
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$ExpectedVersion = '0.1.1'
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
if (Test-Path 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/Winora') {
    throw 'Winora is already installed. Run this check on a PC without a Winora installation.'
}
$testDirectory = Join-Path $workspace ('artifacts/github-update-check-' + [guid]::NewGuid().ToString('N'))
$installation = Join-Path $testDirectory 'installed'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$setup = Join-Path $testDirectory 'Setup.exe'
$beforePath = Join-Path $testDirectory 'before.json'
$updatePath = Join-Path $testDirectory 'update.json'
$afterPath = Join-Path $testDirectory 'after.json'
$savedSource = $env:WINORA_UPDATE_SOURCE
$savedProbe = $env:WINORA_STARTUP_PROBE
# The installed app must use its baked public GitHub feed, without a token.
$env:WINORA_UPDATE_SOURCE = $null
$env:WINORA_STARTUP_PROBE = $beforePath
try {
    Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/goujandev/winora/releases/download/v$BaselineVersion/Winora-win-Setup.exe" -OutFile $setup
    $process = Start-Process -FilePath $setup -ArgumentList '--silent','--installto',"`"$installation`"",'--log',"`"$(Join-Path $testDirectory 'setup.log')`"" -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(120000)) { $process.Kill(); throw 'Baseline installation timed out.' }
    if ($process.ExitCode -ne 0) { throw 'Baseline installation failed.' }
    $launcher = Join-Path $installation 'Winora.exe'
    function RunProbe([string]$path, [bool]$apply = $false) {
        $arguments = @('--update-probe', "`"$path`"")
        if ($apply) { $arguments += '--apply-update' }
        $probe = Start-Process -FilePath $launcher -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if (-not $probe.WaitForExit(60000)) { $probe.Kill(); throw 'GitHub update check timed out.' }
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path -LiteralPath $path) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
        if (-not (Test-Path -LiteralPath $path)) { throw 'The installed app did not produce an update report.' }
        return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    }
    $before = RunProbe $beforePath
    if ($before.Version -ne $BaselineVersion -or -not $before.IsInstalled -or -not $before.HasUpdate) {
        throw 'The baseline app did not discover and download the newer GitHub release.'
    }
    $update = RunProbe $updatePath $true
    if (-not $update.HasUpdate) { throw 'No GitHub update was available to apply.' }
    $installedDll = Join-Path $installation 'current/Winora.dll'
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        Start-Sleep -Milliseconds 250
        $version = if (Test-Path -LiteralPath $installedDll) { [Diagnostics.FileVersionInfo]::GetVersionInfo($installedDll).FileVersion } else { '' }
    } while ($version -notlike "$ExpectedVersion*" -and [DateTime]::UtcNow -lt $deadline)
    if ($version -notlike "$ExpectedVersion*") { throw 'The installed version did not advance to the expected GitHub release.' }
    $after = RunProbe $afterPath
    if ($after.Version -ne $ExpectedVersion -or -not $after.IsInstalled -or $after.HasUpdate) { throw 'Final GitHub update verification failed.' }
    Write-Output "PASS: installed $BaselineVersion from GitHub, discovered and applied $ExpectedVersion through the public feed, and verified the installed version."
    Write-Output "Reports: $testDirectory"
} finally {
    $env:WINORA_UPDATE_SOURCE = $savedSource
    $env:WINORA_STARTUP_PROBE = $savedProbe
    $updater = Join-Path $installation 'Update.exe'
    if (Test-Path -LiteralPath $updater) {
        $process = Start-Process -FilePath $updater -ArgumentList 'uninstall','--silent' -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { Write-Warning 'The isolated GitHub test installation needs manual cleanup.' }
    }
}
