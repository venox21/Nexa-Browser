# =====================================================================
# Nexa Browser - Windows 11 Fluent Installer Build Script
# Erstellt eine autarke, eigenständige 'NexaSetup.exe'
# =====================================================================

param(
    [switch]$SkipPublish = $false
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $ScriptDir

Add-Type -AssemblyName System.IO.Compression.FileSystem

Write-Host "======================================================" -ForegroundColor Cyan
Write-Host "  Nexa Browser - Windows 11 Installer Builder         " -ForegroundColor Cyan
Write-Host "======================================================" -ForegroundColor Cyan

# 1. Publish Browser (Release)
$publishDir = Join-Path $ScriptDir "publish"
if (-not $SkipPublish) {
    Write-Host "`n[1/4] Baue & veröffentliche Nexa Browser (Release)..." -ForegroundColor Yellow
    if (Test-Path $publishDir) {
        Remove-Item -Path $publishDir -Recurse -Force
    }

    dotnet publish "$ScriptDir\Browser\Browser.csproj" -c Release -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Fehler beim Veröffentlichen des Browsers!"
    }
} else {
    Write-Host "`n[1/4] Überspringe Browser-Publish (bereits vorhanden)..." -ForegroundColor Gray
}

# Clean unneeded XML docs & PDBs to slim down installer package
Get-ChildItem -Path $publishDir -Filter "*.xml" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $publishDir -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

# 2. Pack files to app.zip
Write-Host "`n[2/4] Komprimiere Browser-Paket nach app.zip..." -ForegroundColor Yellow
$resourcesDir = Join-Path $ScriptDir "NexaInstaller\Resources"
if (-not (Test-Path $resourcesDir)) {
    New-Item -ItemType Directory -Path $resourcesDir -Force | Out-Null
}

$appZip = Join-Path $resourcesDir "app.zip"
if (Test-Path $appZip) {
    Remove-Item -Path $appZip -Force
}

Start-Sleep -Milliseconds 200
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $appZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
$zipSize = (Get-Item $appZip).Length / 1MB
Write-Host ("      -> app.zip erstellt ({0:N2} MB)" -f $zipSize) -ForegroundColor Green

# 3. Build NexaInstaller
Write-Host "`n[3/4] Baue Windows 11 Fluent Installer (Single-File)..." -ForegroundColor Yellow
$distDir = Join-Path $ScriptDir "dist"
if (Test-Path $distDir) {
    Remove-Item -Path $distDir -Recurse -Force
}

dotnet publish "$ScriptDir\NexaInstaller\NexaInstaller.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained false `
    -p:PublishSingleFile=true `
    -o $distDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Fehler beim Erstellen des Installers!"
}

# 4. Copy to Root for convenient access
$setupSource = Join-Path $distDir "NexaSetup.exe"
$setupDest = Join-Path $ScriptDir "NexaSetup.exe"
Copy-Item -Path $setupSource -Destination $setupDest -Force

$setupSize = (Get-Item $setupDest).Length / 1MB

Write-Host "`n======================================================" -ForegroundColor Green
Write-Host "  INSTALLER ERFOLGREICH ERSTELLT! 🎉                 " -ForegroundColor Green
Write-Host "======================================================" -ForegroundColor Green
Write-Host ("  Datei:  {0}" -f $setupDest) -ForegroundColor White
Write-Host ("  Größe:  {0:N2} MB" -f $setupSize) -ForegroundColor White
Write-Host "  Features:" -ForegroundColor Cyan
Write-Host "    - Windows 11 Mica / Fluent Design & Rounded Corners" -ForegroundColor Gray
Write-Host "    - Schritt-für-Schritt Wizard (Express oder Benutzerdefiniert)" -ForegroundColor Gray
Write-Host "    - Startmenü- & Desktop-Icons mit AppUserModelID" -ForegroundColor Gray
Write-Host "    - Vollständiger Uninstaller in Windows 'Apps & Features'" -ForegroundColor Gray
Write-Host "    - Standard-Browser Registrierung für Web & HTML" -ForegroundColor Gray
Write-Host "`nDu kannst 'NexaSetup.exe' jetzt direkt per Doppelklick starten!" -ForegroundColor Yellow
