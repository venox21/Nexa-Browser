@echo off
title Nexa Browser - Push to GitHub
echo ========================================================
echo   Nexa Browser - Git Push zu GitHub (main)
echo ========================================================
echo.
git push -u origin main
echo.
if %ERRORLEVEL% equ 0 (
    echo ========================================================
    echo   [ERFOLG] Push zu GitHub erfolgreich abgeschlossen!
    echo ========================================================
) else (
    echo ========================================================
    echo   [FEHLER] Push fehlgeschlagen.
    echo   Bitte ueberpruefe deine GitHub-Rechte fuer
    echo   https://github.com/venox21/Nexa-Browser
    echo ========================================================
)
echo.
pause
