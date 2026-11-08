# Gera app\src\app.ico (16, 24, 32, 48, 64, 128 e 256 px) desenhando o ícone do zero.
param([string]$Saida = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\app.ico'))
Add-Type -AssemblyName System.Drawing

function Desenhar([int]$t) {
    $bmp = New-Object System.Drawing.Bitmap $t, $t
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # fundo: quadrado arredondado em degradê
    $r = [single]($t * 0.22); $m = [single]([math]::Max(0.5, $t * 0.02)); $w = [single]($t - 2 * $m)
    $fundo = New-Object System.Drawing.Drawing2D.GraphicsPath
    $fundo.AddArc($m, $m, 2*$r, 2*$r, 180, 90)
    $fundo.AddArc($m + $w - 2*$r, $m, 2*$r, 2*$r, 270, 90)
    $fundo.AddArc($m + $w - 2*$r, $m + $w - 2*$r, 2*$r, 2*$r, 0, 90)
    $fundo.AddArc($m, $m + $w - 2*$r, 2*$r, 2*$r, 90, 90)
    $fundo.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF 0,0), (New-Object System.Drawing.PointF $t,$t),
        [System.Drawing.Color]::FromArgb(255, 30, 61, 97), [System.Drawing.Color]::FromArgb(255, 58, 112, 168))
    $g.FillPath($grad, $fundo)

    # escudo (viewBox 24x24, área útil 4..20) com seta para baixo
    $s = $t / 24.0; $ox = 0; $oy = 0
    function P([double]$x, [double]$y) { New-Object System.Drawing.PointF ([single]($x * $s)), ([single]($y * $s)) }
    $esc = New-Object System.Drawing.Drawing2D.GraphicsPath
    $esc.AddLine((P 12 3.2), (P 19.2 6.3))
    $esc.AddLine((P 19.2 6.3), (P 19.2 11))
    $esc.AddBezier((P 19.2 11), (P 19.2 15.6), (P 16.2 19.4), (P 12 20.9))
    $esc.AddBezier((P 12 20.9), (P 7.8 19.4), (P 4.8 15.6), (P 4.8 11))
    $esc.AddLine((P 4.8 11), (P 4.8 6.3))
    $esc.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $esc)

    $seta = New-Object System.Drawing.Drawing2D.GraphicsPath
    $seta.AddPolygon([System.Drawing.PointF[]]@((P 10.9 7.2), (P 13.1 7.2), (P 13.1 12.3), (P 15.3 10.1), (P 16.8 11.6), (P 12 16.4), (P 7.2 11.6), (P 8.7 10.1), (P 10.9 12.3)))
    $azul = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 35, 71, 111))
    $g.FillPath($azul, $seta)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}

$tamanhos = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($t in $tamanhos) { ,(Desenhar $t) }

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$tamanhos.Count)
$offset = 6 + 16 * $tamanhos.Count
for ($i = 0; $i -lt $tamanhos.Count; $i++) {
    $t = $tamanhos[$i]; $d = $pngs[$i]
    $bw.Write([byte]($(if ($t -ge 256) { 0 } else { $t }))); $bw.Write([byte]($(if ($t -ge 256) { 0 } else { $t })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset); $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($Saida, $out.ToArray())
Write-Host "Ícone gerado: $Saida"
