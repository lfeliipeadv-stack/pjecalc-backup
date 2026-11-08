@echo off
rem PJe-Calc Backup - versao para o CMD.
rem   Dois cliques: abre os menus.
rem   Arrastar arquivos .PJC (ou uma pasta) sobre este arquivo: abre a restauracao deles.
rem   Arrastar o .zip de um backup do banco: abre a restauracao do banco.
rem   Parametros (ex.: --backup) sao repassados ao programa. Veja --ajuda.
chcp 65001 >nul
title PJe-Calc Backup
set "EXE=%~dp0PJeCalcBackup-Console.exe"
if not exist "%EXE%" set "EXE=%LOCALAPPDATA%\Programs\PJe-Calc Backup\PJeCalcBackup-Console.exe"
if not exist "%EXE%" goto naoachou
"%EXE%" %*
set "CODIGO=%errorlevel%"
rem codigo 2 = erro inesperado: deixa a mensagem na tela
if "%CODIGO%"=="2" pause
exit /b %CODIGO%

:naoachou
echo.
echo  Nao encontrei o PJeCalcBackup-Console.exe.
echo  Baixe o pacote (.zip) em https://github.com/lfeliipeadv-stack/pjecalc-backup/releases/latest
echo  e coloque o PJeCalcBackup-Console.exe na mesma pasta deste arquivo.
echo.
pause
exit /b 1
