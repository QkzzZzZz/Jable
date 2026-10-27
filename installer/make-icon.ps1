# 生成 src\JableLens.App\Assets\app.ico：红色渐变圆角方块 + 白色「J」。
# 字母按字形轮廓的实际边界居中（按字体行高居中会偏，Impact 的 J 尤其明显）。
# 每个尺寸单独绘制（16 ~ 256），PNG 压缩存进 ICO。
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\JableLens.App\Assets\app.ico'))
Add-Type -AssemblyName System.Drawing

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)

    # 圆角方块（小尺寸留 0 边距，大尺寸留一点，像 Windows 11 图标）
    $m = if ($size -ge 48) { $size * 0.06 } else { 0 }
    $r = ($size - 2 * $m) * 0.22
    $rect = New-Object System.Drawing.RectangleF $m, $m, ($size - 2 * $m), ($size - 2 * $m)
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $bg.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $bg.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $bg.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $bg.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $bg.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 0xF2, 0x2B, 0x35)), ([System.Drawing.Color]::FromArgb(255, 0xA8, 0x06, 0x0E)), 60
    $g.FillPath($brush, $bg)

    # 字母：先按大字号生成轮廓，量出真实边界，缩放到方块高度的 58%，再把边界中心对到方块中心
    $font = New-Object System.Drawing.FontFamily 'Impact'
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddString('J', $font, 0, 1000, (New-Object System.Drawing.PointF 0, 0), [System.Drawing.StringFormat]::GenericTypographic)
    $b = $path.GetBounds()
    $scale = ($rect.Height * 0.58) / $b.Height
    $mx = New-Object System.Drawing.Drawing2D.Matrix
    $mx.Translate($rect.X + $rect.Width / 2, $rect.Y + $rect.Height / 2)
    $mx.Scale($scale, $scale)
    $mx.Translate(-($b.X + $b.Width / 2), -($b.Y + $b.Height / 2))
    $path.Transform($mx)
    $g.FillPath([System.Drawing.Brushes]::White, $path)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (Draw $s) }
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()
"写入 $Out"
