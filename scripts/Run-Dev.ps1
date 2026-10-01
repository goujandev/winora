param([switch]$BuildOnly, [switch]$TestTiling)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $workspace '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$executable = Join-Path $workspace 'src/Winora/bin/Development/net10.0-windows/win-x64/Winora.Dev.exe'

# Close only this workspace's previous dev window so its files can be rebuilt.
foreach ($process in Get-Process -Name Winora.Dev -ErrorAction SilentlyContinue) {
    if ([string]::Equals($process.Path, $executable, [StringComparison]::OrdinalIgnoreCase)) {
        if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(5000)) {
            throw 'Close the Winora Dev window, then run this command again.'
        }
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
