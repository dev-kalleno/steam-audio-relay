# Draws icon.ico (speaker with sound waves) with System.Drawing. Run: powershell -File make-icon.ps1
param([string]$OutFile = 'icon.ico', [int[]]$Sizes = @(256, 48, 32, 24, 16))
Add-Type -AssemblyName System.Drawing

function Draw([int]$size) {
    $s = $size / 256.0
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded square, blue -> teal
    $r = 56 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d - 1, $size - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 37, 99, 235)), ([System.Drawing.Color]::FromArgb(255, 20, 184, 166)), 45
    $g.FillPath($grad, $path)

    # speaker: box + cone
    $white = [System.Drawing.Brushes]::White
    $g.FillRectangle($white, 46 * $s, 100 * $s, 36 * $s, 56 * $s)
    $cone = @(
        (New-Object System.Drawing.PointF (82 * $s), (100 * $s)),
        (New-Object System.Drawing.PointF (132 * $s), (62 * $s)),
        (New-Object System.Drawing.PointF (132 * $s), (194 * $s)),
        (New-Object System.Drawing.PointF (82 * $s), (156 * $s)))
    $g.FillPolygon($white, $cone)

    # sound waves, two on small sizes so they stay readable
    if ($size -le 32) { $radii = @(42, 82); $w = 22 * $s } else { $radii = @(36, 66, 96); $w = 15 * $s }
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $cx = 140 * $s; $cy = 128 * $s
    foreach ($rad in $radii) {
        $rr = $rad * $s
        $g.DrawArc($pen, ($cx - $rr), ($cy - $rr), (2 * $rr), (2 * $rr), -42, 84)
    }
    $g.Dispose()
    return $bmp
}

# Classic uncompressed bitmap (DIB) entry. .NET's Icon class can't read PNG entries, so only the 256 one is a PNG.
function Dib($bmp, [int]$size) {
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$size); $w.Write([int32]($size * 2))
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]0); $w.Write([uint32]0)
    $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $maskRow = [int]([math]::Ceiling($size / 32.0) * 4)
    $w.Write((New-Object byte[] ($maskRow * $size)))
    return , $ms.ToArray()   # the comma keeps PowerShell from unrolling the byte array into single values
}

$sizes = $Sizes
$pngs = @()
foreach ($sz in $sizes) {
    $bmp = Draw $sz
    if ($sz -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , $ms.ToArray()
        $bmp.Save((Join-Path $PSScriptRoot 'icon-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        $pngs += , ([byte[]](Dib $bmp $sz))
    }
    $bmp.Dispose()
}

# ICO container (image data is PNG for 256, DIB for the rest)
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p) }
$target = if ([System.IO.Path]::IsPathRooted($OutFile)) { $OutFile } else { Join-Path $PSScriptRoot $OutFile }
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
"$target geschrieben ($($out.Length) Bytes)"
