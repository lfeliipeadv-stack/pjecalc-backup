# Compila o PJe-Calc Backup usando o compilador C# que já vem no .NET Framework 4.8
# do Windows (não precisa instalar nada). Gera dois programas a partir do mesmo código:
#   saida\PJeCalcBackup.exe          — janela (WPF)
#   saida\PJeCalcBackup-Console.exe  — versão interativa para o CMD
#   powershell -ExecutionPolicy Bypass -File app\compilar.ps1
param([string]$Saida = (Join-Path $PSScriptRoot 'saida'))
$ErrorActionPreference = 'Stop'

$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "Compilador não encontrado: $csc (.NET Framework 4.8)" }
$wpf = Join-Path $fw 'WPF'

$src = Join-Path $PSScriptRoot 'src'
$ico = Join-Path $src 'app.ico'
if (-not (Test-Path $ico)) { & (Join-Path $PSScriptRoot 'ferramentas\gerar-icone.ps1') -Saida $ico }
New-Item -ItemType Directory -Force $Saida | Out-Null

$refs = @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll", "$fw\System.Web.Extensions.dll",
    "$fw\System.IO.Compression.dll", "$fw\System.IO.Compression.FileSystem.dll",
    "$fw\System.Management.dll", "$fw\Microsoft.VisualBasic.dll", "$fw\System.Core.dll", "$fw\System.dll", "$fw\System.Xml.dll"
) | ForEach-Object { "/reference:`"$_`"" }
$fontes = Get-ChildItem $src -Filter *.cs | ForEach-Object { "`"$($_.FullName)`"" }

function Compilar([string]$exe, [string]$alvo, [string]$principal) {
    $a = @('/nologo', "/target:$alvo", '/platform:anycpu', '/optimize+', '/codepage:65001', '/warn:3',
           "/main:$principal", "/out:`"$exe`"", "/win32icon:`"$ico`"",
           "/resource:`"$src\JanelaPrincipal.xaml`",PjeCalcBackup.JanelaPrincipal.xaml",
           "/resource:`"$ico`",PjeCalcBackup.app.ico") + $refs + $fontes
    $saidaCsc = & $csc @a 2>&1
    $saidaCsc | Where-Object { $_ -notmatch 'compiler is provided|no longer the latest|support newer|go\.microsoft' } | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Falha na compilação de $exe (código $LASTEXITCODE)" }
    Write-Host "Compilado: $exe ($([math]::Round((Get-Item $exe).Length/1KB)) KB)"
}

Compilar (Join-Path $Saida 'PJeCalcBackup.exe') 'winexe' 'PjeCalcBackup.Programa'
Compilar (Join-Path $Saida 'PJeCalcBackup-Console.exe') 'exe' 'PjeCalcBackup.ModoConsole'
Copy-Item (Join-Path $PSScriptRoot '..\backup-calculos\backup-calculos.bat') $Saida -Force -ErrorAction SilentlyContinue
