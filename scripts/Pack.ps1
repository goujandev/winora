param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0',
    [string]$RepositoryUrl = '',
    [string]$OutputDirectory = '',
    [string]$PackageId = 'Winora',
    [switch]$SelfContained
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $workspace 'artifacts/releases' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ($RepositoryUrl -and $RepositoryUrl -notmatch '^https://github\.com/[^/\s]+/[^/\s]+/?$') {
    throw 'RepositoryUrl must be an HTTPS GitHub repository URL.'
}
$dotnet = Join-Path $workspace '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$vpk = Join-Path $workspace '.tools/vpk-1.2.0/vpk.exe'
if (-not (Test-Path -LiteralPath $vpk)) { $vpk = (Get-Command vpk -ErrorAction Stop).Source }
$env:DOTNET_ROOT = Split-Path $dotnet -Parent
$publishDirectory = Join-Path $workspace ("artifacts/publish/$PackageId-$Version-" + [guid]::NewGuid().ToString('N'))
& (Join-Path $PSScriptRoot 'Build-TrayAgent.ps1')
& $dotnet publish (Join-Path $workspace 'src/Winora/Winora.csproj') -c Release -r win-x64 --self-contained $SelfContained.IsPresent.ToString().ToLowerInvariant() "-p:Version=$Version" -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
@{ RepositoryUrl = $(if ($RepositoryUrl) { $RepositoryUrl.TrimEnd('/') } else { $null }) } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publishDirectory 'release.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $workspace 'THIRD_PARTY_NOTICES.txt') -Destination $publishDirectory
Copy-Item -LiteralPath (Join-Path $workspace 'licenses') -Destination $publishDirectory -Recurse
$packageTitle = if ($PackageId -eq 'Winora.UpdateTest') { 'Winora Update Check' } else { 'Winora' }
$packArguments = @('pack', '--packId', $PackageId, '--packVersion', $Version, '--packDir', $publishDirectory,
    '--mainExe', 'Winora.exe', '--packTitle', $packageTitle, '--packAuthors', 'goujan', '--runtime', 'win-x64',
    '--icon', (Join-Path $workspace 'src/Winora/Assets/Winora.ico'),
    '--outputDir', $OutputDirectory, '--shortcuts', 'StartMenuRoot', '--noPortable', '--skip-updates')
if (-not $SelfContained) { $packArguments += @('--framework', 'net10.0-x64-desktop') }
& $vpk @packArguments
if ($LASTEXITCODE -ne 0) { throw 'Installer packaging failed.' }
Get-ChildItem -LiteralPath $OutputDirectory -File | Select-Object Name,Length
