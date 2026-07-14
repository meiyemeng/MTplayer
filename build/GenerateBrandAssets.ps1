param(
    [string]$AssetDirectory = (Join-Path $PSScriptRoot '..\src\WebHtv.Desktop\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$headerSource = Join-Path $AssetDirectory 'logo-header.png'
$headerTarget = Join-Path $AssetDirectory 'logo-header-transparent.png'
$iconSource = Join-Path $AssetDirectory 'icon-source.png'
$iconPreview = Join-Path $AssetDirectory 'mtplayer-icon.png'
$iconTarget = Join-Path $AssetDirectory 'mtplayer.ico'

function New-RoundedRectanglePath([System.Drawing.RectangleF]$rectangle, [float]$radius) {
    $diameter = $radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($rectangle.X, $rectangle.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($rectangle.Right - $diameter, $rectangle.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($rectangle.Right - $diameter, $rectangle.Bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($rectangle.X, $rectangle.Bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

# Remove only the neutral near-black backing plate. Red glow and white typography remain opaque.
$source = [System.Drawing.Bitmap]::new($headerSource)
$header = [System.Drawing.Bitmap]::new($source.Width, $source.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
for ($y = 0; $y -lt $source.Height; $y++) {
    for ($x = 0; $x -lt $source.Width; $x++) {
        $pixel = $source.GetPixel($x, $y)
        $maximum = [Math]::Max($pixel.R, [Math]::Max($pixel.G, $pixel.B))
        $neutral = [Math]::Abs([int]$pixel.R - [int]$pixel.G) -lt 18 -and [Math]::Abs([int]$pixel.R - [int]$pixel.B) -lt 18
        $alpha = $pixel.A
        if ($neutral -and $maximum -le 48) { $alpha = 0 }
        elseif ($neutral -and $maximum -lt 90) { $alpha = [int](255 * (($maximum - 48) / 42.0)) }
        $header.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($alpha, $pixel.R, $pixel.G, $pixel.B))
    }
}
$header.Save($headerTarget, [System.Drawing.Imaging.ImageFormat]::Png)
$header.Dispose()
$source.Dispose()

$canvas = [System.Drawing.Bitmap]::new(512, 512, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($canvas)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$graphics.Clear([System.Drawing.Color]::Transparent)

$shapeRectangle = [System.Drawing.RectangleF]::new(18, 18, 476, 476)
$shape = New-RoundedRectanglePath $shapeRectangle 92
$background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.PointF]::new(20, 20),
    [System.Drawing.PointF]::new(492, 492),
    [System.Drawing.Color]::FromArgb(255, 22, 27, 32),
    [System.Drawing.Color]::FromArgb(255, 4, 7, 10))
$graphics.FillPath($background, $shape)

$graphics.SetClip($shape)
$sourceIcon = [System.Drawing.Image]::FromFile($iconSource)
$graphics.DrawImage($sourceIcon, [System.Drawing.RectangleF]::new(54, 74, 404, 342))

$glossPath = New-RoundedRectanglePath ([System.Drawing.RectangleF]::new(20, 20, 472, 230)) 88
$gloss = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
    [System.Drawing.PointF]::new(0, 20),
    [System.Drawing.PointF]::new(0, 250),
    [System.Drawing.Color]::FromArgb(46, 255, 255, 255),
    [System.Drawing.Color]::FromArgb(0, 255, 255, 255))
$graphics.FillPath($gloss, $glossPath)
$graphics.ResetClip()

$borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(150, 227, 29, 43), 4)
$graphics.DrawPath($borderPen, $shape)
$innerPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(55, 255, 255, 255), 2)
$innerShape = New-RoundedRectanglePath ([System.Drawing.RectangleF]::new(24, 24, 464, 464)) 86
$graphics.DrawPath($innerPen, $innerShape)

$canvas.Save($iconPreview, [System.Drawing.Imaging.ImageFormat]::Png)

$sourceIcon.Dispose(); $innerShape.Dispose(); $innerPen.Dispose(); $borderPen.Dispose()
$gloss.Dispose(); $glossPath.Dispose(); $background.Dispose(); $shape.Dispose(); $graphics.Dispose()

$sizes = @(256, 128, 64, 48, 32, 24, 16)
$frames = New-Object System.Collections.Generic.List[byte[]]
foreach ($size in $sizes) {
    $frame = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($frame)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($canvas, 0, 0, $size, $size)
    $stream = [System.IO.MemoryStream]::new()
    $frame.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($stream.ToArray())
    $stream.Dispose(); $g.Dispose(); $frame.Dispose()
}
$canvas.Dispose()

$file = [System.IO.File]::Open($iconTarget, [System.IO.FileMode]::Create)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset = 6 + (16 * $frames.Count)
for ($index = 0; $index -lt $frames.Count; $index++) {
    $size = $sizes[$index]
    $writer.Write([byte]($(if ($size -eq 256) { 0 } else { $size })))
    $writer.Write([byte]($(if ($size -eq 256) { 0 } else { $size })))
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$index].Length)
    $writer.Write([uint32]$offset)
    $offset += $frames[$index].Length
}
foreach ($frameBytes in $frames) { $writer.Write($frameBytes) }
$writer.Dispose(); $file.Dispose()

Write-Output "Generated $headerTarget"
Write-Output "Generated $iconPreview"
Write-Output "Generated $iconTarget"
