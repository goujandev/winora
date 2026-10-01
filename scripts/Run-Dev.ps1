param([switch]$BuildOnly, [switch]$TestTiling)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $workspace '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$executable = Join-Path $workspace 'src/Winora/bin/Development/net10.0-windows/win-x64/Winora.Dev.exe'

function Get-WorkspaceDevProcesses {
    foreach ($candidate in Get-Process -Name Winora.Dev -ErrorAction SilentlyContinue) {
        try {
            if (-not $candidate.HasExited -and
                [string]::Equals($candidate.Path, $executable, [StringComparison]::OrdinalIgnoreCase)) {
                $candidate
            } else { $candidate.Dispose() }
        } catch {
            # The process may have exited while its identity was being checked.
            $candidate.Dispose()
        }
    }
}

# Close GUI windows first. Live tiling tests need time to restore windows and
# signal their background helper before the development files can be rebuilt.
$windowShutdown = [Diagnostics.Stopwatch]::StartNew()
foreach ($process in @(Get-WorkspaceDevProcesses)) {
    try {
        if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) { continue }
        if (-not $process.CloseMainWindow() -and -not $process.HasExited) {
            throw 'Close this workspace''s Winora Dev window, then run this command again.'
        }
        $remaining = [int][Math]::Max(0, 30000 - $windowShutdown.ElapsedMilliseconds)
        if (-not $process.WaitForExit($remaining)) {
            throw 'Winora Dev is still stopping its tiling test. Let window restoration finish, then run this command again.'
        }
    } finally { $process.Dispose() }
}

# Headless workers use the same apphost but have no window to close. Wait for
# their normal exit; never send CloseMainWindow or forcibly terminate them.
$helperShutdown = [Diagnostics.Stopwatch]::StartNew()
while ($true) {
    $helpers = @(Get-WorkspaceDevProcesses)
    if ($helpers.Count -eq 0) { break }
    if ($helperShutdown.ElapsedMilliseconds -ge 15000) {
        $remainingHelperId = $helpers[0].Id
        foreach ($helper in $helpers) { $helper.Dispose() }
        throw "Winora Dev's background helper (PID $remainingHelperId) is still running. Close the live tiling test and let its cleanup finish before retrying."
    }
    foreach ($process in $helpers) {
        try {
            if ($process.HasExited) { continue }
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                throw 'A Winora Dev window opened during cleanup. Close it, then run this command again.'
            }
            $remaining = [int][Math]::Max(0, 15000 - $helperShutdown.ElapsedMilliseconds)
            if (-not $process.WaitForExit($remaining)) {
                throw "Winora Dev's background helper (PID $($process.Id)) is still running. Close the live tiling test and let its cleanup finish before retrying."
            }
        } finally { $process.Dispose() }
    }
}
$previousHome = $env:DOTNET_CLI_HOME
$previousRoot = $env:DOTNET_ROOT
try {
    $env:DOTNET_CLI_HOME = Join-Path $workspace '.tools/dotnet-home'
    $env:DOTNET_ROOT = Split-Path $dotnet -Parent
    & $dotnet build (Join-Path $workspace 'src/Winora/Winora.csproj') -c Development
    if ($LASTEXITCODE -ne 0) { throw 'Winora Dev build failed.' }
    if (-not $BuildOnly) {
        if ($TestTiling) { Start-Process -FilePath $executable -ArgumentList '--test-tiling' -WorkingDirectory $workspace | Out-Null }
        else { Start-Process -FilePath $executable -WorkingDirectory $workspace | Out-Null }
    }
    Write-Output "Winora Dev: $executable"
} finally {
    $env:DOTNET_CLI_HOME = $previousHome
    $env:DOTNET_ROOT = $previousRoot
}
