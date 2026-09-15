[CmdletBinding()]
param(
    [string]$Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LanView.Windows\Assets\LanView.png'),
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\LanView.Windows\Assets\LanView.ico')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Run this icon build with PowerShell 7 on Windows.' }
Add-Type -AssemblyName System.Drawing.Common
$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$outputPath = [IO.Path]::GetFullPath($Output)
if ($sourcePath -eq $outputPath) { throw 'The source image and icon output must be different files.' }
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = [Collections.Generic.List[byte[]]]::new()
$original = [Drawing.Image]::FromFile($sourcePath)
try {
    if ($original.Width -ne $original.Height -or $original.Width -lt 256) {
        throw 'Use a square source image at least 256 pixels wide.'
    }
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $png = [IO.MemoryStream]::new()
        $attributes = [Drawing.Imaging.ImageAttributes]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.DrawImage($original, [Drawing.Rectangle]::new(0, 0, $size, $size), 0, 0,
                $original.Width, $original.Height, [Drawing.GraphicsUnit]::Pixel, $attributes)
            $bitmap.Save($png, [Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($png.ToArray())
        } finally {
            $attributes.Dispose()
            $png.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
} finally { $original.Dispose() }

$buffer = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($buffer)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
    $writer.Flush()
    New-Item -ItemType Directory -Path (Split-Path -Parent $outputPath) -Force | Out-Null
    [IO.File]::WriteAllBytes($outputPath, $buffer.ToArray())
} finally {
    $writer.Dispose()
    $buffer.Dispose()
}
[pscustomobject]@{ Icon = $outputPath; Sizes = ($sizes -join ', '); Bytes = (Get-Item -LiteralPath $outputPath).Length }
