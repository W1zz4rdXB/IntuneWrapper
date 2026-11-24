# Generates assets/icon.ico (16/24/32/48/64/256 px, PNG-compressed entries).
# Run once; the .ico is committed. Windows PowerShell 5.1 compatible.
param([string]$OutFile = (Join-Path $PSScriptRoot '..\assets\icon.ico'))

Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded blue tile
    $r = [Math]::Max(2, [int]($size * 0.18))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $size - 1
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 103, 184))), $path)

    # White package box
    $penW = [Math]::Max(1.0, $size / 14.0)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $penW
    $pen.LineJoin = 'Round'
    $m = $size * 0.24
    $bx = $m; $by = $size * 0.40; $bw = $size - 2 * $m; $bh = $size * 0.38
    $g.DrawRectangle($pen, [float]$bx, [float]$by, [float]$bw, [float]$bh)
    # Lid line and down arrow into the box
    $cx = $size / 2.0
    $g.DrawLine($pen, [float]$cx, [float]($size * 0.14), [float]$cx, [float]($size * 0.56))
    $g.DrawLine($pen, [float]($cx - $size * 0.12), [float]($size * 0.44), [float]$cx, [float]($size * 0.56))
    $g.DrawLine($pen, [float]($cx + $size * 0.12), [float]($size * 0.44), [float]$cx, [float]($size * 0.56))

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$images = @(foreach ($s in $sizes) { , (New-IconPng $s) })

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $data = $images[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $bw.Write($data) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Resolve-Path (Split-Path $OutFile)).Path + '\' + (Split-Path $OutFile -Leaf), $out.ToArray())
Write-Host "Wrote $OutFile"
