# Render the app's PNG and multi-resolution Windows icon from the sleeping cat artwork.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$root = Split-Path $PSScriptRoot -Parent
[xml]$art = Get-Content -Raw -LiteralPath "$root/Assets/sleeping-cat.svg"
$paths = foreach ($path in $art.svg.g.path) { [Windows.Media.Geometry]::Parse($path.d) }
$viewBox = [Windows.Rect]::Parse(($art.svg.viewBox -replace ' ', ','))

function Draw-Outline($context, $color, $width) {
    $brush = [Windows.Media.BrushConverter]::new().ConvertFromString($color)
    $pen = [Windows.Media.Pen]::new($brush, $width)
    $pen.StartLineCap = $pen.EndLineCap = [Windows.Media.PenLineCap]::Round
    $pen.LineJoin = [Windows.Media.PenLineJoin]::Round
    foreach ($geometry in $paths) { $context.DrawGeometry($null, $pen, $geometry) }
}

function Render-Cat([int]$width, [int]$height, [switch]$icon) {
    $visual = New-Object Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $scale = $width / $viewBox.Width
    $stroke = [double]::Parse($art.svg.g.'stroke-width', [cultureinfo]::InvariantCulture)
    if ($icon) {
        # Optical sizing and a dark keyline keep the cat legible on either taskbar color.
        $scale *= 0.91
        if ($width -le 24) { $stroke = 6.5 } elseif ($width -le 48) { $stroke = 4.5 }
        $context.PushTransform([Windows.Media.TranslateTransform]::new(
            ($width - $viewBox.Width * $scale) / 2, ($height - $viewBox.Height * $scale) / 2))
    }
    $context.PushTransform([Windows.Media.ScaleTransform]::new($scale, $scale))
    if ($icon) { Draw-Outline $context '#212133' ($stroke + 3.6) }
    Draw-Outline $context $art.svg.g.stroke $stroke
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($width, $height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    # .NET Framework Icon.ToBitmap reads small ICO frames as Windows DIBs.
    if ($icon -and $width -lt 256) {
        $source = [Windows.Media.Imaging.FormatConvertedBitmap]::new($bitmap, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
        $stride = $width * 4
        $pixels = [byte[]]::new($stride * $height)
        $source.CopyPixels($pixels, $stride, 0)
        $maskStride = [int][Math]::Ceiling($width / 32.0) * 4
        $mask = [byte[]]::new($maskStride * $height)
        $stream = New-Object IO.MemoryStream
        $dib = [IO.BinaryWriter]::new($stream)
        foreach ($value in @(40, $width, (2 * $height))) { $dib.Write([int]$value) }
        $dib.Write([uint16]1)
        $dib.Write([uint16]32)
        foreach ($value in @(0, $pixels.Length, 0, 0, 0, 0)) { $dib.Write([int]$value) }
        for ($row = 0; $row -lt $height; $row++) {
            $offset = ($height - 1 - $row) * $stride
            $dib.Write($pixels, $offset, $stride)
            for ($x = 0; $x -lt $width; $x++) {
                if ($pixels[$offset + $x * 4 + 3] -eq 0) {
                    $index = $row * $maskStride + ($x -shr 3)
                    $mask[$index] = $mask[$index] -bor (128 -shr ($x % 8))
                }
            }
        }
        $dib.Write($mask)
        $dib.Flush()
        return ,$stream.ToArray()
    }
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object IO.MemoryStream
    $encoder.Save($stream)
    return ,$stream.ToArray()
}

[IO.File]::WriteAllBytes("$root/Assets/sleeping-cat.png", (Render-Cat $art.svg.width $art.svg.height))
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = @{}
foreach ($size in $sizes) { $frames[$size] = Render-Cat $size $size -icon }
$buffer = New-Object IO.MemoryStream
$writer = [IO.BinaryWriter]::new($buffer)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Length)
$offset = 6 + 16 * $sizes.Length
foreach ($size in $sizes) {
    $writer.Write([byte]($size % 256))
    $writer.Write([byte]($size % 256))
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([int]$frames[$size].Length)
    $writer.Write([int]$offset)
    $offset += $frames[$size].Length
}
foreach ($size in $sizes) { $writer.Write([byte[]]$frames[$size]) }
$writer.Flush()
[IO.File]::WriteAllBytes("$root/start_icon.ico", $buffer.ToArray())
