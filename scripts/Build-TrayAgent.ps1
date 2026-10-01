param([string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
if (-not $OutputPath) { $OutputPath = Join-Path $workspace 'artifacts/native/Winora.TrayAgent.exe' }
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory (Split-Path $OutputPath -Parent) -Force | Out-Null
$zigDirectory = Join-Path $workspace '.tools/zig'
$zig = Join-Path $zigDirectory 'zig-x86_64-windows-0.15.2/zig.exe'
if (-not (Test-Path $zig)) {
    New-Item -ItemType Directory $zigDirectory -Force | Out-Null
    $archive = Join-Path $zigDirectory 'compiler.zip'
    Invoke-WebRequest 'https://ziglang.org/download/0.15.2/zig-x86_64-windows-0.15.2.zip' -OutFile $archive -UseBasicParsing
    if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne '3A0ED1E8799A2F8CE2A6E6290A9FF22E6906F8227865911FB7DDEDC3CC14CB0C') {
        throw 'Native compiler checksum mismatch.'
    }
    Expand-Archive $archive $zigDirectory -Force
}
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $workspace '.tools/zig-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $workspace '.tools/zig-local-cache'
& $zig cc (Join-Path $workspace 'src/Winora.TrayAgent/main.c') -target x86_64-windows-gnu -Os -s -municode '-Wl,--subsystem,windows' -Wall -Wextra -Werror -ladvapi32 -lshell32 -o $OutputPath
if ($LASTEXITCODE -ne 0) { throw 'Native tray agent build failed.' }
Write-Output $OutputPath
