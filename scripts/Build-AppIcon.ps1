#requires -Version 5.1

[CmdletBinding()]
param(
    [string] $SourcePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$assetsDirectory = Join-Path $repositoryRoot 'src\UsageRemaining.Widget\Assets'
$iconPath = Join-Path $assetsDirectory 'CodexPulse.ico'
if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path $assetsDirectory 'CodexPulse.source.png'
}
$SourcePath = (Resolve-Path -LiteralPath $SourcePath).ProviderPath

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [System.Collections.Generic.List[object]]::new()
$source = [System.Drawing.Image]::FromFile($SourcePath)
try {
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([System.Drawing.Color]::Transparent)
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

                # Fit the complete source inside the frame without cropping or changing its aspect ratio.
                $scale = [Math]::Min($size / [double] $source.Width, $size / [double] $source.Height)
                $width = [Math]::Max(1, [int] [Math]::Round($source.Width * $scale))
                $height = [Math]::Max(1, [int] [Math]::Round($source.Height * $scale))
                $destination = [System.Drawing.Rectangle]::new([int] (($size - $width) / 2), [int] (($size - $height) / 2), $width, $height)
                $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
                try {
                    $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
                    $graphics.DrawImage($source, $destination, 0, 0, $source.Width, $source.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
                }
                finally {
                    $attributes.Dispose()
                }
            }
            finally {
                $graphics.Dispose()
            }

            $pngStream = [System.IO.MemoryStream]::new()
            try {
                $bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
                $frames.Add([pscustomobject] @{ Size = $size; Bytes = $pngStream.ToArray() })
            }
            finally {
                $pngStream.Dispose()
            }
        }
        finally {
            $bitmap.Dispose()
        }
    }
}
finally {
    $source.Dispose()
}

$iconStream = [System.IO.MemoryStream]::new()
try {
    $writer = [System.IO.BinaryWriter]::new($iconStream)
    try {
        $writer.Write([uint16] 0)
        $writer.Write([uint16] 1)
        $writer.Write([uint16] $frames.Count)
        $offset = [uint32] (6 + 16 * $frames.Count)
        foreach ($frame in $frames) {
            # ICO encodes a 256-pixel dimension as zero; each payload is a 32-bit RGBA PNG.
            $dimension = [byte] ($frame.Size % 256)
            $writer.Write($dimension)
            $writer.Write($dimension)
            $writer.Write([byte] 0)
            $writer.Write([byte] 0)
            $writer.Write([uint16] 1)
            $writer.Write([uint16] 32)
            $writer.Write([uint32] $frame.Bytes.Length)
            $writer.Write($offset)
            $offset += [uint32] $frame.Bytes.Length
        }
        foreach ($frame in $frames) {
            $writer.Write([byte[]] $frame.Bytes)
        }
        $writer.Flush()
        [System.IO.File]::WriteAllBytes($iconPath, $iconStream.ToArray())
    }
    finally {
        $writer.Dispose()
    }
}
finally {
    $iconStream.Dispose()
}

Write-Output "Created $iconPath ($($frames.Count) frames: $($sizes -join ', '))."
