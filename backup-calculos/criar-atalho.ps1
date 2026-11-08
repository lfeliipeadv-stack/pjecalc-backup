# Cria o atalho "Backup PJe-Calc" na Area de Trabalho, apontando para backup-calculos.bat
$bat   = Join-Path $PSScriptRoot 'backup-calculos.bat'
$icone = Join-Path (Split-Path -Parent $PSScriptRoot) 'icone_calc.ico'
$atalho = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Backup PJe-Calc.lnk'

$sh  = New-Object -ComObject WScript.Shell
$lnk = $sh.CreateShortcut($atalho)
$lnk.TargetPath       = $bat
$lnk.WorkingDirectory = $PSScriptRoot
if (Test-Path $icone) { $lnk.IconLocation = "$icone,0" }
$lnk.Description = 'Exporta todos os calculos do PJe-Calc em .PJC para a pasta de backup'
$lnk.Save()

Write-Host "Atalho criado: $atalho"
