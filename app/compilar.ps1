# Compila o PJe-Calc Backup em um único .exe usando o compilador C# que já vem
# no .NET Framework 4.8 do Windows (não precisa instalar nada).
#   powershell -ExecutionPolicy Bypass -File app\compilar.ps1
param([string]$Saida = (Join-Path $PSScriptRoot 'saida\PJeCalcBackup.exe'))
$ErrorActionPreference = 'Stop'

$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "Compilador não encontrado: $csc (.NET Framework 4.8)" }
$wpf = Join-Path $fw 'WPF'

$src = Join-Path $PSScriptRoot 'src'
$ico = Join-Path $src 'app.ico'
if (-not (Test-Path $ico)) { & (Join-Path $PSScriptRoot 'ferramentas\gerar-icone.ps1') -Saida $ico }

New-Item -ItemType Directory -Force (Split-Path -Parent $Saida) | Out-Null

$refs = @(
    "$wpf\PresentationFramework.dll", "$wpf\PresentationCore.dll", "$wpf\WindowsBase.dll",
    "$fw\System.Xaml.dll", "$fw\System.Windows.Forms.dll", "$fw\System.Web.Extensions.dll",
    "$fw\System.IO.Compression.dll", "$fw\System.IO.Compression.FileSystem.dll",
    "$fw\System.Management.dll", "$fw\System.Core.dll", "$fw\System.dll", "$fw\System.Xml.dll"
) | ForEach-Object { "/reference:`"$_`"" }

$args = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', '/warn:3',
          "/out:`"$Saida`"", "/win32icon:`"$ico`"",
          "/resource:`"$src\JanelaPrincipal.xaml`",PjeCalcBackup.JanelaPrincipal.xaml",
          "/resource:`"$ico`",PjeCalcBackup.app.ico") + $refs + (Get-ChildItem $src -Filter *.cs | ForEach-Object { "`"$($_.FullName)`"" })

& $csc @args
if ($LASTEXITCODE -ne 0) { throw "Falha na compilação (código $LASTEXITCODE)" }
Write-Host "Compilado: $Saida ($([math]::Round((Get-Item $Saida).Length/1KB)) KB)"
