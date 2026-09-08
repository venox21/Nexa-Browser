# =====================================================================
# Nexa Browser - Winget Manifest Generator
# Generiert die offiziellen Windows Package Manager (Winget) Manifeste
# =====================================================================

param(
    [string]$SetupExePath = ".\NexaSetup.exe",
    [string]$Version = "2.0.1",
    [string]$OutputBaseDir = ".\winget\manifests\v\venox21\NexaBrowser"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $ScriptDir

if (-not (Test-Path $SetupExePath)) {
    Write-Error "Setup executable not found at: $SetupExePath"
}

$fullExePath = Resolve-Path $SetupExePath
$hash = (Get-FileHash -Path $fullExePath -Algorithm SHA256).Hash
Write-Host "NexaSetup.exe SHA256: $hash" -ForegroundColor Cyan

$manifestDir = Join-Path $OutputBaseDir $Version
if (-not (Test-Path $manifestDir)) {
    New-Item -ItemType Directory -Path $manifestDir -Force | Out-Null
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

# 1. Version Manifest
$versionYaml = @"
# Created using Generate-WingetManifest.ps1
PackageIdentifier: venox21.NexaBrowser
PackageVersion: $Version
DefaultLocale: de-DE
ManifestType: version
ManifestVersion: 1.6.0
"@
[System.IO.File]::WriteAllText((Join-Path $manifestDir "venox21.NexaBrowser.yaml"), $versionYaml, $utf8NoBom)

# 2. Installer Manifest
$installerYaml = @"
# Created using Generate-WingetManifest.ps1
PackageIdentifier: venox21.NexaBrowser
PackageVersion: $Version
Platform:
  - Windows.Desktop
MinimumOSVersion: 10.0.17763.0
InstallerType: exe
Scope: user
InstallModes:
  - interactive
  - silent
InstallerSwitches:
  Silent: /silent
  SilentWithProgress: /silent
  Custom: /s
UpgradeBehavior: install
FileExtensions:
  - html
  - htm
  - pdf
Protocols:
  - http
  - https
  - nexa
Installers:
  - Architecture: x64
    InstallerUrl: https://github.com/venox21/Nexa-Browser/releases/download/Installer/NexaSetup.exe
    InstallerSha256: $hash
    ProductCode: NexaBrowser
ManifestType: installer
ManifestVersion: 1.6.0
"@
[System.IO.File]::WriteAllText((Join-Path $manifestDir "venox21.NexaBrowser.installer.yaml"), $installerYaml, $utf8NoBom)

# 3. Default Locale (de-DE)
$localeDeYaml = @"
# Created using Generate-WingetManifest.ps1
PackageIdentifier: venox21.NexaBrowser
PackageVersion: $Version
PackageLocale: de-DE
Publisher: venox21
PublisherUrl: https://github.com/venox21/Nexa-Browser
PublisherSupportUrl: https://github.com/venox21/Nexa-Browser/issues
PackageName: Nexa Browser
PackageUrl: https://github.com/venox21/Nexa-Browser
License: MIT
LicenseUrl: https://github.com/venox21/Nexa-Browser/blob/main/LICENSE
ShortDescription: Moderner, blitzschneller & KI-unterstützter Windows 11 Webbrowser mit integriertem AdBlocker und Turbo-Engine.
Description: Nexa Browser ist ein moderner Webbrowser für Windows 11 mit integriertem AdBlocker & YouTube Shield, Nexa KI-Seitenanalyse, Turbo-DNS Prewarming, Instant-Cache, Spotlight-Schnellsuche und 1-Klick-Lesezeichenimport aus Chrome, Edge und Brave.
Tags:
  - browser
  - web
  - adblock
  - youtube-adblock
  - privacy
  - fluent
  - windows-11
  - ai
ReleaseNotesUrl: https://github.com/venox21/Nexa-Browser/releases/tag/Installer
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@
[System.IO.File]::WriteAllText((Join-Path $manifestDir "venox21.NexaBrowser.locale.de-DE.yaml"), $localeDeYaml, $utf8NoBom)

# 4. English Locale (en-US)
$localeEnYaml = @"
# Created using Generate-WingetManifest.ps1
PackageIdentifier: venox21.NexaBrowser
PackageVersion: $Version
PackageLocale: en-US
Publisher: venox21
PublisherUrl: https://github.com/venox21/Nexa-Browser
PublisherSupportUrl: https://github.com/venox21/Nexa-Browser/issues
PackageName: Nexa Browser
PackageUrl: https://github.com/venox21/Nexa-Browser
License: MIT
LicenseUrl: https://github.com/venox21/Nexa-Browser/blob/main/LICENSE
ShortDescription: Modern, blazing-fast, AI-powered Windows 11 web browser with native adblocking.
Description: Nexa Browser is a modern Windows 11 web browser featuring native ad-blocking, YouTube Shield, AI assistant, Turbo-DNS prewarming, instant cache, and 1-click bookmark import from Chrome, Edge, and Brave.
Tags:
  - browser
  - web
  - adblock
  - privacy
  - fluent
ReleaseNotesUrl: https://github.com/venox21/Nexa-Browser/releases/tag/Installer
ManifestType: locale
ManifestVersion: 1.6.0
"@
[System.IO.File]::WriteAllText((Join-Path $manifestDir "venox21.NexaBrowser.locale.en-US.yaml"), $localeEnYaml, $utf8NoBom)

Write-Host "Winget Manifests erfolgreich generiert in: $manifestDir" -ForegroundColor Green
