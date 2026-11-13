# Gera docs/assets/banner.png (1280x640, serve também como "social preview" do GitHub)
# na identidade "ficha de telemetria": preto quente, osso, um laranja de sinal, marcas de corte e linha de pulso.
param([string]$Out = (Join-Path $PSScriptRoot '..\docs\assets\banner.png'))

Add-Type -AssemblyName System.Drawing
$W = 1280; $H = 640
$bmp = New-Object System.Drawing.Bitmap $W, $H
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.PixelOffsetMode = 'HighQuality'

$ink    = [System.Drawing.Color]::FromArgb(255, 13, 12, 10)
$plate  = [System.Drawing.Color]::FromArgb(255, 22, 20, 17)
$bone   = [System.Drawing.Color]::FromArgb(255, 237, 230, 214)
$mute   = [System.Drawing.Color]::FromArgb(255, 140, 132, 118)
$line   = [System.Drawing.Color]::FromArgb(255, 52, 48, 42)
$orange = [System.Drawing.Color]::FromArgb(255, 255, 91, 31)
function Brush($c) { New-Object System.Drawing.SolidBrush $c }
function Pen($c, $w) { New-Object System.Drawing.Pen $c, ([single]$w) }

$g.Clear($ink)

# grão
$rnd = New-Object System.Random 7
for ($i = 0; $i -lt 26000; $i++) {
    $a = $rnd.Next(6, 22)
    $bmp.SetPixel($rnd.Next(0, $W), $rnd.Next(0, $H), [System.Drawing.Color]::FromArgb(255, 13 + $a, 12 + $a, 10 + $a))
}

# grade fina
$grid = Pen ([System.Drawing.Color]::FromArgb(255, 26, 24, 21)) 1
for ($x = 0; $x -le $W; $x += 64) { $g.DrawLine($grid, $x, 0, $x, $H) }
for ($y = 0; $y -le $H; $y += 64) { $g.DrawLine($grid, 0, $y, $W, $y) }

# marcas de corte
$mk = Pen $mute 2
foreach ($c in @(@(40, 40, 1, 1), @(($W - 40), 40, -1, 1), @(40, ($H - 40), 1, -1), @(($W - 40), ($H - 40), -1, -1))) {
    $g.DrawLine($mk, $c[0], $c[1], $c[0] + 26 * $c[2], $c[1])
    $g.DrawLine($mk, $c[0], $c[1], $c[0], $c[1] + 26 * $c[3])
}

# régua de marcas (topo)
$tick = Pen $line 2
for ($x = 96; $x -lt $W - 96; $x += 10) {
    $h = if ((($x - 96) / 10) % 10 -eq 0) { 16 } else { 8 }
    $g.DrawLine($tick, $x, 92, $x, 92 + $h)
}

# rótulos mono
$mono = New-Object System.Drawing.Font 'Cascadia Mono', 13, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('HZ-01  /  FICHA DE TELEMETRIA', $mono, (Brush $mute), 96, 60)
$g.DrawString('WINDOWS 10/11  ·  OPEN SOURCE  ·  MIT', $mono, (Brush $mute), ($W - 96 - $g.MeasureString('WINDOWS 10/11  ·  OPEN SOURCE  ·  MIT', $mono).Width), 60)

# ícone
# o .ico guarda cada tamanho como PNG: lê direto a entrada de 256 px
$icoBytes = [System.IO.File]::ReadAllBytes((Join-Path $PSScriptRoot '..\Hertzo\Assets\hertzo.ico'))
$count = [BitConverter]::ToUInt16($icoBytes, 4)
for ($i = 0; $i -lt $count; $i++) {
    $e = 6 + 16 * $i
    if ($icoBytes[$e] -eq 0) {
        $size = [BitConverter]::ToInt32($icoBytes, $e + 8); $off = [BitConverter]::ToInt32($icoBytes, $e + 12)
        $ms = New-Object System.IO.MemoryStream (, $icoBytes[$off..($off + $size - 1)])
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.DrawImage([System.Drawing.Image]::FromStream($ms), 96, 170, 150, 150)
    }
}

# nome
$title = New-Object System.Drawing.Font 'Bahnschrift SemiBold Condensed', 170, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('HERTZO', $title, (Brush $bone), 262, 150)

# subtítulo
$sub = New-Object System.Drawing.Font 'Bahnschrift', 30, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('Otimizador para jogos que mostra a prova.', $sub, (Brush $bone), 100, 362)
$sub2 = New-Object System.Drawing.Font 'Bahnschrift', 22, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('Ajustes reversíveis com nível de evidência · medição de FPS · replay de clipes', $sub2, (Brush $mute), 100, 410)

# linha de pulso laranja
$pts = @(@(96, 520), @(820, 520), @(844, 496), @(864, 520), @(892, 466), @(924, 580), @(948, 520), @(1110, 520))
$pf = foreach ($p in $pts) { New-Object System.Drawing.PointF ([single]$p[0]), ([single]$p[1]) }
$pp = Pen $orange 7; $pp.LineJoin = 'Miter'; $pp.MiterLimit = 12
$g.DrawLines($pp, [System.Drawing.PointF[]]$pf)
$g.FillRectangle((Brush $bone), 1126, 509, 22, 22)

# pilares
$pil = New-Object System.Drawing.Font 'Cascadia Mono', 14, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$x = 96
foreach ($t in @('A OTIMIZAR', 'B MEDIR', 'C CAPTURAR', 'D MANTER', 'E PROTEGER')) {
    $g.FillRectangle((Brush $orange), $x, 566, 18, 18)
    $g.DrawString($t.Substring(0, 1), $pil, (Brush $ink), $x + 3, 566)
    $g.DrawString($t.Substring(2), $pil, (Brush $bone), $x + 26, 566)
    $x += 26 + $g.MeasureString($t.Substring(2), $pil).Width + 34
}

$g.Dispose()
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Banner em $Out"
