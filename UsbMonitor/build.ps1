# Copyright (c) 2026 @Namiton
# SPDX-License-Identifier: MIT
#
# Build UsbMonitor.exe with the csc.exe bundled in .NET Framework 4.x (no SDK install needed).
# Output: bin\UsbMonitor.exe
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$binDir = Join-Path $src 'bin'
New-Item -ItemType Directory -Force -Path $binDir | Out-Null
$out = Join-Path $binDir 'UsbMonitor.exe'
$ico = Join-Path $binDir 'icon.ico'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

Add-Type -AssemblyName System.Drawing

# App icon: brand indigo (#5E6AD2) rounded square with a white pulse line (multi-size PNG ICO)
function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = $size * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $w = $size - 1
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($w - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($w - $r * 2, $w - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $w - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(94, 106, 210))), $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.5, $size * 0.085))
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $s = $size
    $pts = @(
        (New-Object System.Drawing.PointF ($s * 0.18), ($s * 0.54)),
        (New-Object System.Drawing.PointF ($s * 0.36), ($s * 0.54)),
        (New-Object System.Drawing.PointF ($s * 0.45), ($s * 0.30)),
        (New-Object System.Drawing.PointF ($s * 0.57), ($s * 0.74)),
        (New-Object System.Drawing.PointF ($s * 0.66), ($s * 0.46)),
        (New-Object System.Drawing.PointF ($s * 0.82), ($s * 0.46))
    )
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 256
$pngs = $sizes | ForEach-Object { , (New-IconPng $_) }
$fs = [System.IO.File]::Create($ico)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $pngs[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($d in $pngs) { $bw.Write([byte[]]$d) }
$bw.Close()

& $csc /nologo /target:winexe /optimize+ /codepage:65001 /platform:anycpu `
    "/out:$out" "/win32icon:$ico" "/win32manifest:$(Join-Path $src 'app.manifest')" `
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    (Join-Path $src 'UsbMonitor.cs')
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }
Write-Host "Built: $out"
