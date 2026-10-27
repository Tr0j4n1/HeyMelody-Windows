# Builds Assets\app.png and Assets\app.ico from HeyMelody's adaptive launcher icon layers
# (mipmap-xxxhdpi/heymelody_app_icon_launcher_{background,foreground}.webp, 432x432 = 108dp).
# Android shows the middle 72dp of an adaptive icon through a mask; this keeps that area under a rounded square.
param([string]$Background, [string]$Foreground, [string]$OutDir = $PSScriptRoot)
Add-Type -AssemblyName PresentationCore, WindowsBase

function Load($path) {
    $fs = [IO.File]::OpenRead($path)
    try {
        $decoder = [System.Windows.Media.Imaging.BitmapDecoder]::Create($fs, [System.Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat, [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
        # The WebP decoder hands back an undeclared format that DrawImage renders black/jagged; normalise to BGRA first.
        $bgra = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap($decoder.Frames[0], [System.Windows.Media.PixelFormats]::Bgra32, $null, 0)
        $bgra.Freeze()
        return $bgra
    } finally { $fs.Close() }
}

function Render([int]$size, $bg, $fg) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    [System.Windows.Media.RenderOptions]::SetBitmapScalingMode($visual, [System.Windows.Media.BitmapScalingMode]::HighQuality)
    $dc = $visual.RenderOpen()
    $radius = $size * 0.225
    $dc.PushClip((New-Object System.Windows.Media.RectangleGeometry((New-Object System.Windows.Rect(0, 0, $size, $size)), $radius, $radius)))
    $full = $size * 108 / 72 # the 108dp canvas drawn so its middle 72dp fills the icon
    $offset = -($full - $size) / 2
    $rect = New-Object System.Windows.Rect($offset, $offset, $full, $full)
    $dc.DrawImage($bg, $rect)
    $dc.DrawImage($fg, $rect)
    $dc.Pop()
    $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    return $bmp
}

function Png($bitmap) {
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

$bg = Load $Background
$fg = Load $Foreground
[IO.File]::WriteAllBytes((Join-Path $OutDir "app.png"), (Png (Render 256 $bg $fg)))

# ICO with PNG-compressed entries (Windows Vista+), one per standard size.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @()
foreach ($s in $sizes) { $images += ,(Png (Render $s $bg $fg)) }
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$dataOffset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s } # 0 means 256 in an ICO directory entry
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$dataOffset)
    $dataOffset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $OutDir "app.ico"), $out.ToArray())
"app.png and app.ico ($($sizes -join ', ') px) written to $OutDir"
