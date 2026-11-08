@echo off
chcp 65001 >nul
title PJe-Calc Backup
mode con: cols=130 lines=42 >nul 2>&1
set "EXE=%~dp0PJeCalcBackup-Console.exe"
if not exist "%EXE%" set "EXE=%LOCALAPPDATA%\Programs\PJe-Calc Backup\PJeCalcBackup-Console.exe"
if not exist "%EXE%" goto naoachou
"%EXE%" %*
goto :eof

:naoachou
echo.
echo  Nao encontrei o PJeCalcBackup-Console.exe.
echo  Baixe em https://github.com/lfeliipeadv-stack/pjecalc-backup/releases/latest
echo  e coloque-o na mesma pasta deste arquivo.
echo.
pause
