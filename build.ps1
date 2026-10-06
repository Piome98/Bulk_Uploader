# Bulk Uploader 빌드 스크립트
# Windows에 기본 내장된 .NET Framework 4.x 컴파일러(csc.exe)만 사용하므로 별도 설치가 필요 없습니다.
# 사용법: powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$icon = Join-Path $root 'src\app.ico'
New-Item -ItemType Directory -Force $dist | Out-Null

# 아이콘이 없으면 생성 (PNG를 담은 ICO, 여러 크기)
if (-not (Test-Path $icon)) {
    Add-Type -AssemblyName System.Drawing
    function New-IconPng([int]$s) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::Transparent)
        $k = $s / 256.0
        # 둥근 배경
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $r = 56 * $k; $w = $s - 1
        $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($w - $r, 0, $r, $r, 270, 90)
        $path.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $path.AddArc(0, $w - $r, $r, $r, 90, 90); $path.CloseFigure()
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $s, $s), ([System.Drawing.Color]::FromArgb(59, 130, 246)), ([System.Drawing.Color]::FromArgb(37, 64, 175))
        $g.FillPath($bg, $path)
        # 겹친 문서 3장
        $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
        $shade = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(190, 210, 255))
        $g.FillRectangle($shade, [float](58 * $k), [float](92 * $k), [float](88 * $k), [float](112 * $k))
        $g.FillRectangle($shade, [float](84 * $k), [float](74 * $k), [float](88 * $k), [float](112 * $k))
        $g.FillRectangle($white, [float](110 * $k), [float](56 * $k), [float](88 * $k), [float](112 * $k))
        # 위쪽 화살표
        $arrow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(37, 64, 175))
        $pts = @(
            (New-Object System.Drawing.PointF ([float](154 * $k)), ([float](72 * $k))),
            (New-Object System.Drawing.PointF ([float](184 * $k)), ([float](108 * $k))),
            (New-Object System.Drawing.PointF ([float](165 * $k)), ([float](108 * $k))),
            (New-Object System.Drawing.PointF ([float](165 * $k)), ([float](148 * $k))),
            (New-Object System.Drawing.PointF ([float](143 * $k)), ([float](148 * $k))),
            (New-Object System.Drawing.PointF ([float](143 * $k)), ([float](108 * $k))),
            (New-Object System.Drawing.PointF ([float](124 * $k)), ([float](108 * $k)))
        )
        $g.FillPolygon($arrow, [System.Drawing.PointF[]]$pts)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        return , $ms.ToArray()
    }
    $sizes = 256, 48, 32, 16
    $pngs = foreach ($s in $sizes) { , (New-IconPng $s) }
    $out = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $out
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $d = $pngs[$i]
        $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
        $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset)
        $offset += $d.Length
    }
    foreach ($d in $pngs) { $bw.Write([byte[]]$d) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($icon, $out.ToArray())
    Write-Host "아이콘 생성: $icon"
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

& $csc /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 `
    "/win32icon:$icon" "/out:$(Join-Path $dist 'BulkUploader.exe')" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    (Join-Path $root 'src\BulkUploader.cs')
if ($LASTEXITCODE -ne 0) { throw "컴파일 실패" }
Write-Host "빌드 완료: $(Join-Path $dist 'BulkUploader.exe')"
