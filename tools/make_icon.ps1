Add-Type -AssemblyName System.Drawing

# 路径一律相对脚本位置，仓库放到任何机器都能跑。
# 注意：封面素材（图标原图）不入库，需自备：
#   <项目根>\封面素材\超级资源管理器程序图标.jpg
$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root "封面素材\超级资源管理器程序图标.jpg"
$outDir = $root
$icoPath = Join-Path $outDir "app.ico"
$prevPath = Join-Path $outDir "封面素材\图标预览.png"
$stripPath = Join-Path $outDir "封面素材\小尺寸锐化核对.png"

$img = [System.Drawing.Image]::FromFile($src)

# 保留整张图全部元素（含气泡框），等比缩放到方形画布，不做裁切
$sq = New-Object System.Drawing.Bitmap 1024, 1024
$g = [System.Drawing.Graphics]::FromImage($sq)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
$g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$g.DrawImage($img, 0, 0, 1024, 1024)
$g.Dispose()
$img.Dispose()

function Clamp([double]$v) {
    if ($v -lt 0) { return 0 }
    if ($v -gt 255) { return 255 }
    return [int][Math]::Round($v)
}

# 小尺寸缩放后边缘发虚，用反锐化掩膜提亮轮廓，让 16-96px 的图标更清楚
function Sharpen([System.Drawing.Bitmap]$bmp, [double]$amount) {
    $w = $bmp.Width
    $h = $bmp.Height
    if ($w -lt 3 -or $h -lt 3) { return }

    $srcBmp = New-Object System.Drawing.Bitmap $w, $h
    $gs = [System.Drawing.Graphics]::FromImage($srcBmp)
    $gs.DrawImage($bmp, 0, 0, $w, $h)
    $gs.Dispose()

    for ($y = 1; $y -lt $h - 1; $y++) {
        for ($x = 1; $x -lt $w - 1; $x++) {
            $c = $srcBmp.GetPixel($x, $y)
            $u = $srcBmp.GetPixel($x, $y - 1)
            $d = $srcBmp.GetPixel($x, $y + 1)
            $l = $srcBmp.GetPixel($x - 1, $y)
            $r = $srcBmp.GetPixel($x + 1, $y)

            $br = ($u.R + $d.R + $l.R + $r.R) / 4.0
            $bg = ($u.G + $d.G + $l.G + $r.G) / 4.0
            $bb = ($u.B + $d.B + $l.B + $r.B) / 4.0

            $nr = Clamp($c.R + $amount * ($c.R - $br))
            $ng = Clamp($c.G + $amount * ($c.G - $bg))
            $nb = Clamp($c.B + $amount * ($c.B - $bb))

            $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($c.A, $nr, $ng, $nb))
        }
    }
    $srcBmp.Dispose()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$frames = @()
$preview = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $gb = [System.Drawing.Graphics]::FromImage($bmp)
    $gb.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $gb.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $gb.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $gb.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $gb.DrawImage($sq, 0, 0, $s, $s)
    $gb.Dispose()

    if ($s -le 96) { Sharpen $bmp 0.75 }

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @($s, $ms.ToArray())

    if ($s -le 64) { $preview += , @($s, $bmp) }
    else { $bmp.Dispose() }
}

$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter $fs

# ICONDIR
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$frames.Count)

$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]
    $data = $f[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $bw.Write($f[1]) }
$bw.Flush()
$bw.Close()

# 预览图
$sq.Save($prevPath, [System.Drawing.Imaging.ImageFormat]::Png)

# 小尺寸放大 6 倍拼成核对条，便于看清锐化效果
$scale = 6
$padX = 12
$totalW = 0
foreach ($p in $preview) { $totalW += $p[1].Width * $scale + $padX }
$stripH = 64 * $scale + $padX * 2
$strip = New-Object System.Drawing.Bitmap $totalW, $stripH
$sg = [System.Drawing.Graphics]::FromImage($strip)
$sg.Clear([System.Drawing.Color]::FromArgb(25, 25, 25))
$sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
$sg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
$x = $padX
foreach ($p in $preview) {
    $b = $p[1]
    $sg.DrawImage($b, $x, $padX, $b.Width * $scale, $b.Height * $scale)
    $x += $b.Width * $scale + $padX
}
$sg.Dispose()
$strip.Save($stripPath, [System.Drawing.Imaging.ImageFormat]::Png)

foreach ($p in $preview) { $p[1].Dispose() }
$strip.Dispose()
$sq.Dispose()

"ico: $icoPath"
"frames: " + ($sizes -join ",")
"size: " + (Get-Item $icoPath).Length
