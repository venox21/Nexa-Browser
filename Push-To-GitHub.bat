@echo off
title Nexa Browser - Push to GitHub
color 0b
echo ========================================================
echo   Nexa Browser - Git Push zu GitHub (main)
echo ========================================================
echo.

echo [1/2] Synchronisiere mit GitHub (fetch)...
git fetch origin main >nul 2>&1

echo [2/2] Lade Aenderungen hoch (git push)...
git push -u origin main
set PUSH_CODE=%ERRORLEVEL%

echo.
if %PUSH_CODE% equ 0 (
    color 0a
    echo ========================================================
    echo   [ERFOLG] Push zu GitHub erfolgreich abgeschlossen!
    echo   Dein Projekt ist auf GitHub auf dem neuesten Stand.
    echo ========================================================
) else (
    color 0c
    echo ========================================================
    echo   [FEHLER] Push fehlgeschlagen (Fehlercode: %PUSH_CODE%).
    echo   Bitte pruefe deine GitHub-Rechte und Internetverbindung.
    echo ========================================================
)
echo.
echo Druecke eine beliebige Taste, um dieses Fenster zu schliessen...
pause >nul

