# Gera Hertzo\Assets\hertzo.ico (e uma prévia em PNG) — identidade "ficha de telemetria":
# placa preta quente, marcas de corte nos cantos, linha de pulso laranja-sinal e marcador quadrado cor osso.
# Cada tamanho é desenhado separadamente: os pequenos (16–32 px) perdem os detalhes finos para ficar nítidos.
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\Hertzo\Assets\hertzo.ico'),
    [string]$Preview = ''
)

Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256

$orange = [System.Drawing.Color]::FromArgb(255, 255, 91, 31)   # #FF5B1F
$bone   = [System.Drawing.Color]::FromArgb(255, 237, 230, 214) # #EDE6D6
$top    = [System.Drawing.Color]::FromArgb(255, 38, 33, 28)
$bottom = [System.Drawing.Color]::FromArgb(255, 13, 12, 10)    # #0D0C0A

function Rounded([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 256.0
    $small = $s -le 32

    # placa
    $inset = [single]([Math]::Max(0.5, 8 * $k))
    $tile = Rounded $inset $inset ([single]($s - 2 * $inset)) ([single]($s - 2 * $inset)) ([single]([Math]::Max(2, 50 * $k)))
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, $s), $top, $bottom)
    $g.FillPath($grad, $tile)
    if (-not $small) {
        $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(46, 237, 230, 214)), ([single]([Math]::Max(1, 2 * $k)))
        $g.DrawPath($edge, $tile)
    }

    # marcas de corte (só a partir de 40 px)
    if ($s -ge 40) {
        $mk = [System.Drawing.Color]::FromArgb(150, 237, 230, 214)
        $b = New-Object System.Drawing.SolidBrush $mk
        $i = 40 * $k; $a = 30 * $k; $t = [Math]::Max(1.6, 7 * $k)
        foreach ($c in @(@($i, $i, 1, 1), @(($s - $i), $i, -1, 1), @($i, ($s - $i), 1, -1), @(($s - $i), ($s - $i), -1, -1))) {
            $cx = $c[0]; $cy = $c[1]; $dx = $c[2]; $dy = $c[3]
            $hx = if ($dx -gt 0) { $cx } else { $cx - $a }
            $vy = if ($dy -gt 0) { $cy } else { $cy - $a }
            $hy = if ($dy -gt 0) { $cy } else { $cy - $t }
            $vx = if ($dx -gt 0) { $cx } else { $cx - $t }
            $g.FillRectangle($b, [single]$hx, [single]$hy, [single]$a, [single]$t)
            $g.FillRectangle($b, [single]$vx, [single]$vy, [single]$t, [single]$a)
        }
    }

    # linha de pulso: base, pequena onda, pico alto, vale fundo, volta à base
    if ($small) {
        $pts = @(@(0, 138), @(80, 138), @(102, 106), @(122, 138), @(140, 54), @(166, 204), @(184, 138), @(200, 138))
        $w = [Math]::Max(2.2, 30 * $k)
    } else {
        $pts = @(@(0, 140), @(88, 140), @(106, 112), @(122, 140), @(142, 76), @(166, 188), @(182, 140), @(196, 140))
        $w = 17 * $k
    }
    $pf = foreach ($p in $pts) { New-Object System.Drawing.PointF ([single]($p[0] * $k)), ([single]($p[1] * $k)) }

    $pen = New-Object System.Drawing.Pen $orange, ([single]$w)
    $pen.LineJoin = 'Miter'; $pen.MiterLimit = 12
    # a linha fica dentro da placa e "entra" pela borda esquerda, como um sinal chegando no instrumento
    $g.SetClip($tile)
    $pen.StartCap = 'Flat'; $pen.EndCap = 'Flat'
    $g.DrawLines($pen, [System.Drawing.PointF[]]$pf)

    # marcador quadrado no fim da linha (o "ponto" do osciloscópio)
    $last = $pts[$pts.Count - 1]
    $m = if ($small) { [Math]::Max(3, 40 * $k) } else { 30 * $k }
    $mx = ($last[0] + 22) * $k - $m / 2
    if ($small) { $mx = ($last[0] + 20) * $k - $m / 2 }
    $my = $last[1] * $k - $m / 2
    $g.ResetClip()
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $bone), [single]$mx, [single]$my, [single]$m, [single]$m)

    $g.Dispose()
    return $bmp
}

$pngs = @()
$bitmaps = @{}
foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    $bitmaps[$s] = $bmp
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , @($s, $ms.ToArray())
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $data = $p[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
$bw.Close()
Write-Host "Ícone gerado em $Out"

# prévia: todos os tamanhos em tamanho real, sobre fundo escuro e claro, e o 256 ampliado
if ($Preview) {
    $W = 980; $H = 420
    $pv = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($pv)
    $g.InterpolationMode = 'NearestNeighbor'
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 32, 32))), 0, 0, $W, $H / 2)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 243, 243, 243))), 0, $H / 2, $W, $H / 2)
    $g.DrawImage($bitmaps[256], 24, 82, 256, 256)
    $x = 310
    foreach ($s in @(128, 64, 48, 32, 24, 20, 16)) {
        $g.DrawImage($bitmaps[$s], $x, [int](105 - $s / 2), $s, $s)
        $g.DrawImage($bitmaps[$s], $x, [int](315 - $s / 2), $s, $s)
        $x += $s + 26
    }
    # 16 e 32 ampliados 4x (pixels nítidos) para conferir a legibilidade
    $g.DrawImage($bitmaps[16], 760, 40, 64, 64)
    $g.DrawImage($bitmaps[32], 850, 40, 128, 128)
    $g.DrawImage($bitmaps[16], 760, 250, 64, 64)
    $g.DrawImage($bitmaps[24], 850, 250, 96, 96)
    $g.Dispose()
    $pv.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Prévia em $Preview"
}
