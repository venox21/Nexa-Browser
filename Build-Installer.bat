@echo off
title Nexa Browser - Installer Builder
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "Build-Installer.ps1"
pause
