# 🌐 Nexa Browser (v2.0)

[![Version](https://img.shields.io/badge/version-2.0.0-6366f1.svg)](https://github.com/venox21/Nexa-Browser/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2011%20%7C%2010%20(x64)-blue.svg)](https://github.com/venox21/Nexa-Browser)
[![Framework](https://img.shields.io/badge/.NET-10.0%20WPF-512bd4.svg)](https://dotnet.microsoft.com/)
[![Engine](https://img.shields.io/badge/engine-Microsoft%20WebView2%20(Chromium)-0078d7.svg)](https://developer.microsoft.com/en-us/microsoft-edge/webview2/)

Ein moderner, blitzschneller und KI-unterstützter Webbrowser für Windows 11 mit nativem Fluent Design, integriertem AdBlocker, Spotlight-Suche und automatischem GitHub-Updater.

---

## ✨ Highlights & Features

### ⚡ Turbo-Performance Engine
- **Instant Back/Forward Navigation:** Native `BackForwardCache`-Unterstützung für verzögerungsfreies Vor- und Zurückblättern.
- **Spekulatives DNS-Prewarming:** Paralleles Vorab-Auflösen von Domains beim Start und beim Tippen in die Adressleiste (Omnibox).
- **Link-Hover Prefetch:** DNS- und TCP-Verbindung werden bereits beim Hovern über Links vorbereitet.
- **500 MB optimierter Disk-Cache & V8-Bytecode Caching:** Häufig besuchte Seiten und Skripte laden bis zu 4-mal schneller.

### 🛡️ Integrierter Shield Pro AdBlocker
- Blockiert Werbung, Tracker, Popups und YouTube-Ads nativ ohne Browser-Plugins.
- High-Performance Domain-Lookup über `FrozenSet` für minimale CPU- und Speicherlast.

### 🤖 Lokale & Cloud KI-Integration
- Integrierter KI-Assistent mit Unterstützung für lokale Modelle (**WebLLM** im Browser, **Ollama**, **LM Studio**) sowie Cloud-Backends (**Google Gemini**, **OpenAI ChatGPT**).
- Seiten zusammenfassen, Fragen stellen, Text übersetzen und Code analysieren direkt aus der Seitenleiste.

### 🔍 Spotlight Schnellsuche (Strg + K)
- Blitzschneller Zugriff auf offene Tabs, Lesezeichen, Chronik, Einstellungen und Aktionen über eine elegante Suchleiste im macOS-/Raycast-Stil.

### 📦 Windows 11 Fluent Installer ([NexaInstaller](file:///c:/Users/Fabian/Desktop/Browser/NexaInstaller))
- **Mica / Acrylic Fluent UI** mit abgerundeten Ecken und Dark Mode.
- **🔄 Update-Modus:** Erkennt bestehende Installationen, schließt laufende Prozesse sicher und überschreibt alte Versionen mit der neuen.
- **🗑️ Deinstallations-Modus:** Löscht auf Knopfdruck Verknüpfungen, Windows-Registrierungseinträge und Programmdateien restlos.
- **🌐 GitHub Auto-Updater:** Prüft automatisch auf neue Releases aus diesem Repository (`venox21/Nexa-Browser`).

---

## 🚀 Schnellstart & Installation

1. Lade die neueste **`NexaSetup.exe`** aus den [Releases](https://github.com/venox21/Nexa-Browser/releases) herunter.
2. Führe `NexaSetup.exe` aus.
3. Klicke auf **Jetzt installieren** (oder **Auf Version 2.0 aktualisieren**, falls du bereits eine ältere Version hast).

---

## 🛠️ Aus dem Quellcode bauen

### Voraussetzungen
- Windows 10 / Windows 11 (64-Bit)
- [.NET 10 SDK](https://dotnet.microsoft.com/download)

### Browser kompilieren
```powershell
dotnet build Browser/Browser.csproj -c Release
```

### Eigenständigen Installer (`NexaSetup.exe`) erstellen
Das mitgelieferte PowerShell-Skript kompiliert den Browser, packt die Binärdateien und baut die Single-File `NexaSetup.exe`:
```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Installer.ps1
```
Alternativ kann einfach per Doppelklick die `Build-Installer.bat` ausgeführt werden.

---

## 📁 Projektstruktur

```
├── Browser/                      # Hauptanwendung (WPF / .NET 10)
│   ├── Assets/                   # Icons, HTML-Startseite, WebView2-Loader
│   ├── Models/                   # Datenmodelle (Tabs, Lesezeichen, Passwörter, etc.)
│   ├── Resources/                # Branding-Konfiguration, WebLLM-Skripte
│   ├── Services/                 # AdBlocker, PerformanceService, Sync, KI, History, etc.)
│   │   └── UpdateService.cs      # GitHub Online-Update Service
│   └── Views/                    # UI-Views (Settings, Spotlight, Task-Manager, UpdateCheckWindow)
├── NexaInstaller/                # Windows 11 Fluent Installer & Uninstaller
│   ├── Assets/                   # Installer-Icons
│   ├── MainWindow.xaml           # Fluent Wizard (Install, Update, Uninstall)
│   └── MainWindow.xaml.cs        # Installations- und Registrierungslogik
├── Build-Installer.ps1           # Automatisiertes Build- & Paketierungsskript
├── Build-Installer.bat           # 1-Klick-Starter für das Build-Skript
├── version.json                  # Zentrales Versions- & Update-Manifest für GitHub
└── README.md                     # Projektdokumentation
```

---

## 📄 Lizenz & Autor

Entwickelt von **Fabian** ([@venox21](https://github.com/venox21)).
Lizenziert unter der MIT-Lizenz.
