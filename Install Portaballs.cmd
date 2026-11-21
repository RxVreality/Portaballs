@echo off
rem Double-click to install Portaballs. Everything it does is in tools\install.ps1 (plain text, read it first if you like).
title Portaballs installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install.ps1" %*
echo.
pause
