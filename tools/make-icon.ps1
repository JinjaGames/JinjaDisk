# JinjaDisk simgesini (Assets\JinjaDisk.ico) üretir: mor→camgöbeği yuvarlak kare + halka grafik.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\Assets\JinjaDisk.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-Png([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [single]($s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = [single]($s - 1)
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $s, $s), ([System.Drawing.Color]::FromArgb(255, 124, 92, 255)), ([System.Drawing.Color]::FromArgb(255, 34, 211, 238))
    $g.FillPath($br, $path)
    # Halka grafik (disk dağılımı)
    $m = [single]($s * 0.2); $d = [single]($s - 2 * $m)
    $pw = [single][Math]::Max(1.5, $s * 0.12)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110, 255, 255, 255)), $pw
    $g.DrawEllipse($pen, $m, $m, $d, $d)
    $pen2 = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $pw
    $pen2.StartCap = 'Round'; $pen2.EndCap = 'Round'
    $g.DrawArc($pen2, $m, $m, $d, $d, -90, 250)
    $c = [single]($s * 0.5); $dot = [single]($s * 0.12)
    $g.FillEllipse([System.Drawing.Brushes]::White, $c - $dot / 2, $c - $dot / 2, $dot, $dot)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @(); foreach ($s in $sizes) { $pngs += , (New-Png $s) }
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Output "Simge yazıldı: $out"
