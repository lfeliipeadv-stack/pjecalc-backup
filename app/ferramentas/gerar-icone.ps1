# Gera app\src\app.ico (16 a 256 px) com o logo do programa, o "Cofre do cálculo": um cofre cuja maçaneta
# é o sinal de igual, em azul-marinho e âmbar. É o mesmo desenho de Tema.Logo, em app\src\Tema.cs.
# Opcional: -Png gera uma imagem PNG do logo (ex.: para o site); -Branca usa a versão invertida (fundo branco)
# e -Redondo, a versão redonda (perfil, redes, prévia de link).
param(
    [string]$Saida = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\app.ico'),
    [string]$Png,
    [int]$TamanhoPng = 256,
    [switch]$Branca,
    [switch]$Redondo
)
Add-Type -AssemblyName PresentationCore, WindowsBase

function Cor([string]$hex) { $b = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($hex)); $b.Freeze(); $b }
function Traco($pincel, [double]$largura) {
    $p = New-Object System.Windows.Media.Pen $pincel, $largura
    $p.StartLineCap = 'Round'; $p.EndLineCap = 'Round'; $p.LineJoin = 'Round'; $p.Freeze(); $p
}
function Ret([double]$x, [double]$y, [double]$l, [double]$a, [double]$r) { New-Object System.Windows.Media.RectangleGeometry (New-Object System.Windows.Rect $x, $y, $l, $a), $r, $r }
function Circ([double]$x, [double]$y, [double]$r) { New-Object System.Windows.Media.EllipseGeometry (New-Object System.Windows.Point $x, $y), $r, $r }
function D($pincel, $traco, $geo) { New-Object System.Windows.Media.GeometryDrawing $pincel, $traco, $geo }

function Logo([bool]$fundoBranco, [bool]$redondo) {
    $marinho = Cor '#12355B'; $ambar = Cor '#F2A900'; $branco = Cor '#FFFFFF'
    $g = New-Object System.Windows.Media.DrawingGroup
    $add = { param($x) [void]$g.Children.Add($x) }
    if (-not $fundoBranco) {
        if ($redondo) { & $add (D $marinho $null (Circ 60 60 60)) } else { & $add (D $marinho $null (Ret 0 0 120 120 26)) }
        & $add (D $branco $null (Ret 22 18 76 78 11))
        & $add (D $branco $null (Ret 30 94 13 8 2)); & $add (D $branco $null (Ret 77 94 13 8 2))
        & $add (D $marinho $null (Ret 25 32 6 12 2)); & $add (D $marinho $null (Ret 25 70 6 12 2))
        & $add (D $marinho $null (Circ 62 57 27))
        & $add (D $null (Traco $ambar 4.5) (Circ 62 57 20))
        & $add (D $ambar $null (Ret 48 47.5 28 8 4)); & $add (D $ambar $null (Ret 48 59.5 28 8 4))
    } else {
        if ($redondo) { & $add (D $branco (Traco $marinho 3) (Circ 60 60 58.5)) } else { & $add (D $branco (Traco $marinho 3) (Ret 1.5 1.5 117 117 25)) }
        & $add (D $marinho $null (Ret 22 18 76 78 11))
        & $add (D $marinho $null (Ret 30 94 13 8 2)); & $add (D $marinho $null (Ret 77 94 13 8 2))
        & $add (D $branco $null (Ret 25 32 6 12 2)); & $add (D $branco $null (Ret 25 70 6 12 2))
        & $add (D $null (Traco $ambar 4.5) (Circ 62 57 27.5))
        & $add (D $branco $null (Circ 62 57 22))
        & $add (D $marinho $null (Ret 48 47.5 28 8 4)); & $add (D $marinho $null (Ret 48 59.5 28 8 4))
    }
    $g.Freeze()
    return $g
}

function Png($desenho, [int]$t, [double]$escala = 1.0) {
    $v = New-Object System.Windows.Media.DrawingVisual
    $dc = $v.RenderOpen()
    # no redondo o cofre encolhe um pouco para caber no círculo
    $s = $t / 120.0
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $s, $s))
    if ($escala -ne 1.0) { $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform $escala, $escala, 60, 60)) }
    $dc.DrawDrawing($desenho)
    if ($escala -ne 1.0) { $dc.Pop() }
    $dc.Pop(); $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $t, $t, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($v)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

if ($Png) {
    if ($Redondo) {
        # fundo redondo inteiro + cofre um pouco menor por cima
        $fundo = Logo ([bool]$Branca) $true
        $cofre = Logo ([bool]$Branca) $false
        $g = New-Object System.Windows.Media.DrawingGroup
        [void]$g.Children.Add($fundo.Children[0])
        $dentro = New-Object System.Windows.Media.DrawingGroup
        $dentro.Transform = New-Object System.Windows.Media.ScaleTransform 0.86, 0.86, 60, 60
        for ($i = 1; $i -lt $cofre.Children.Count; $i++) { [void]$dentro.Children.Add($cofre.Children[$i]) }
        [void]$g.Children.Add($dentro)
        [System.IO.File]::WriteAllBytes($Png, (Png $g $TamanhoPng))
    } else {
        [System.IO.File]::WriteAllBytes($Png, (Png (Logo ([bool]$Branca) $false) $TamanhoPng))
    }
    Write-Host "Imagem gerada: $Png ($TamanhoPng px)"
    return
}

$logo = Logo $false $false
$tamanhos = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($t in $tamanhos) { ,(Png $logo $t) }

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
