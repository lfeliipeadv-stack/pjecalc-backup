@echo off
title Backup dos calculos do PJe-Calc
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0backup-calculos.ps1" %*
echo.
pause
