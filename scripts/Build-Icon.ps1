[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
if ([System.Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    throw 'Run with powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File scripts/Build-Icon.ps1.'
}

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$repository = Split-Path $PSScriptRoot -Parent
$brandPath = Join-Path $repository 'src/Winora/Assets/Brand.xaml'
$iconPath = Join-Path $repository 'src/Winora/Assets/Winora.ico'
$previewDirectory = Join-Path $repository 'artifacts/brand'
New-Item -ItemType Directory -Path $previewDirectory -Force | Out-Null

$reader = [System.Xml.XmlReader]::Create($brandPath)
try { $brand = [System.Windows.Markup.XamlReader]::Load($reader) }
finally { $reader.Close() }
$mark = $brand['WinoraMark']
$mark.Freeze()

function Render-Mark([int] $size, [string] $filePath, [bool] $whiteBackground = $false) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $drawing = $visual.RenderOpen()
    if ($whiteBackground) {
        $drawing.DrawRectangle([System.Windows.Media.Brushes]::White, $null, [System.Windows.Rect]::new(0, 0, $size, $size))
    }
    $drawing.DrawImage($mark, [System.Windows.Rect]::new(0, 0, $size, $size))
    $drawing.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    try {
        $encoder.Save($stream)
        $png = $stream.ToArray()
        [System.IO.File]::WriteAllBytes($filePath, $png)
        return ,$png
    }
    finally { $stream.Dispose() }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$frames = foreach ($size in $sizes) {
    $png = Render-Mark $size (Join-Path $previewDirectory "Winora-$size.png")
    [pscustomobject]@{ Size = $size; Bytes = $png }
}

$stream = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter($stream)
try {
    $writer.Write([UInt16] 0)
    $writer.Write([UInt16] 1)
    $writer.Write([UInt16] $frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte] $dimension)
        $writer.Write([byte] $dimension)
        $writer.Write([byte] 0)
        $writer.Write([byte] 0)
        $writer.Write([UInt16] 1)
        $writer.Write([UInt16] 32)
        $writer.Write([UInt32] $frame.Bytes.Length)
        $writer.Write([UInt32] $offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]] $frame.Bytes) }
}
finally { $writer.Dispose() }

$null = Render-Mark 512 (Join-Path $previewDirectory 'Winora-preview.png') $true
Write-Host "Created Winora.ico with $($frames.Count) PNG frames (16-256 px)."
