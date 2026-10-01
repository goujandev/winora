param()
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Build-TrayAgent.ps1')
$agent = Join-Path $workspace 'artifacts/native/Winora.TrayAgent.exe'
$testName = [guid]::NewGuid().ToString('N')
$base = "Software\Winora\Tests\$testName"
$root = "HKCU:/$base"
$process = $null
function Wait-Condition([scriptblock]$Condition, [string]$Message) {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if (& $Condition) { Write-Output "PASS: $Message"; return }
        Start-Sleep -Milliseconds 100
    }
    throw "FAIL: $Message"
}
function New-Entry([string]$Name, [bool]$Hidden = $true) {
    New-Item "$root/Entries/$Name" -Force | Out-Null
    if ($Hidden) { New-ItemProperty "$root/Entries/$Name" -Name IsPromoted -Value 0 -PropertyType DWord -Force | Out-Null }
}
try {
    New-Item "$root/Preference" -Force | Out-Null
    New-ItemProperty "$root/Preference" -Name Enabled -Value 1 -PropertyType DWord -Force | Out-Null
    New-Entry 'Existing'
    $process = Start-Process $agent -ArgumentList '--test', $base -WindowStyle Hidden -PassThru
    Wait-Condition { (Get-ItemPropertyValue "$root/Entries/Existing" IsPromoted) -eq 1 } 'Existing hidden entry promoted'
    New-Entry 'NewApp'
    Wait-Condition { (Get-ItemPropertyValue "$root/Entries/NewApp" IsPromoted) -eq 1 } 'New app registration promoted while watcher runs'
    Set-ItemProperty "$root/Entries/Existing" IsPromoted 0
    Wait-Condition { (Get-ItemPropertyValue "$root/Entries/Existing" IsPromoted) -eq 1 } 'Reset visibility restored'
    Remove-ItemProperty "$root/Entries/Existing" IsPromoted
    Wait-Condition { (Get-ItemProperty "$root/Entries/Existing").IsPromoted -eq 1 } 'Deleted visibility value restored'
    New-Entry 'Unconfigured' $false
    Wait-Condition { (Get-ItemProperty "$root/Entries/Unconfigured").IsPromoted -eq 1 } 'Entry without a preference promoted'
    # Replace the entire simulated Explorer preference root.
    Remove-Item "$root/Entries" -Recurse -Force
    New-Entry 'Recreated'
    Wait-Condition { (Get-ItemProperty "$root/Entries/Recreated").IsPromoted -eq 1 } 'Recreated registry root recovered'
    $process.Refresh()
    $cpuBefore = $process.TotalProcessorTime
    Start-Sleep -Seconds 3
    $process.Refresh()
    $cpuMs = ($process.TotalProcessorTime - $cpuBefore).TotalMilliseconds
    Write-Output "Idle CPU over 3s: $cpuMs ms; working set: $($process.WorkingSet64) bytes"
    if ($cpuMs -gt 500) { throw 'Watcher used excessive idle CPU.' }
    $duplicate = Start-Process $agent -ArgumentList '--test', $base -WindowStyle Hidden -PassThru
    if (-not $duplicate.WaitForExit(5000) -or $duplicate.ExitCode -ne 0) { throw 'Duplicate watcher did not exit.' }
    Write-Output 'PASS: Duplicate watcher exits without taking ownership'
    Set-ItemProperty "$root/Preference" Enabled 0
    if (-not $process.WaitForExit(5000) -or $process.ExitCode -ne 0) { throw 'Watcher did not stop gracefully.' }
    New-Entry 'AfterDisable'
    Start-Sleep -Milliseconds 300
    if ((Get-ItemPropertyValue "$root/Entries/AfterDisable" IsPromoted) -ne 0) { throw 'Disabled watcher still changed an entry.' }
    Write-Output 'PASS: Disable stops enforcement; later icons remain untouched'
} finally {
    if (Test-Path "$root/Preference") { Set-ItemProperty "$root/Preference" Enabled 0 }
    if ($process -and -not $process.HasExited) { [void]$process.WaitForExit(5000) }
    # Guard cleanup so only this test's unique registry namespace can be removed.
    if ($base -eq "Software\Winora\Tests\$testName") { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}
