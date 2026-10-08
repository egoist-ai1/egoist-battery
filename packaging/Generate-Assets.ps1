$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
function New-BatteryArtwork {
    [CmdletBinding(SupportsShouldProcess)]
    param([int]$Width, [int]$Height, [string]$Filename, [bool]$Dark)
    $assetPath = Join-Path $PSScriptRoot $Filename
    if (-not $PSCmdlet.ShouldProcess($assetPath, 'Создать иллюстрацию установщика')) { return }
    $bitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear($(if ($Dark) { [Drawing.ColorTranslator]::FromHtml('#12181C') } else { [Drawing.Color]::White }))
    $color = [Drawing.ColorTranslator]::FromHtml($(if ($Dark) { '#B7F5CC' } else { '#263C30' }))
    $size = if ($Dark) { 106 } else { 46 }
    $left = [float](($Width - $size) / 2)
    $top = [float](($Height - $size * 0.59) / 2)
    $pen = [Drawing.Pen]::new($color, [float]($size * 0.055))
    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $brush = [Drawing.SolidBrush]::new($color)
    try {
        $graphics.DrawRectangle($pen, $left, $top, [float]($size * 0.9), [float]($size * 0.59))
        $graphics.FillRectangle($brush, [float]($left + $size * 0.94), [float]($top + $size * 0.19), [float]($size * 0.07), [float]($size * 0.2))
        foreach ($offset in @(0.14, 0.47)) {
            $graphics.FillRectangle($brush, [float]($left + $size * $offset), [float]($top + $size * 0.14), [float]($size * 0.22), [float]($size * 0.31))
        }
        $bitmap.Save($assetPath, [Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally { $brush.Dispose(); $pen.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
# Размеры стандартных поверхностей Modern UI; исходник остаётся редактируемым.
New-BatteryArtwork 150 57 'header.bmp' $false
New-BatteryArtwork 164 314 'sidebar.bmp' $true
