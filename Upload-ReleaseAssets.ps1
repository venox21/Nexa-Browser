# =====================================================================
# Nexa Browser - GitHub Release Asset Uploader
# =====================================================================

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
Set-Location $ScriptDir

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public class CredReader {
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);
    
    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern void CredFree(IntPtr credentialPtr);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    public static string GetPassword(string target) {
        IntPtr credPtr;
        if (CredRead(target, 1, 0, out credPtr)) {
            var cred = (CREDENTIAL)Marshal.PtrToStructure(credPtr, typeof(CREDENTIAL));
            if (cred.CredentialBlob != IntPtr.Zero && cred.CredentialBlobSize > 0) {
                byte[] bytes = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, bytes, 0, cred.CredentialBlobSize);
                CredFree(credPtr);
                // Windows stores generic credentials as UTF-8 or Unicode
                string s = System.Text.Encoding.UTF8.GetString(bytes).Trim('\0', '\r', '\n', ' ');
                if (s.StartsWith("ghp_") || s.StartsWith("github_pat_") || s.Length >= 20) {
                    return s;
                }
                return System.Text.Encoding.Unicode.GetString(bytes).Trim('\0', '\r', '\n', ' ');
            }
            CredFree(credPtr);
        }
        return null;
    }
}
"@

$token = [CredReader]::GetPassword("git:https://github.com")

if (-not $token) {
    Write-Error "Konnte kein GitHub-Token aus Windows Credential Manager auslesen!"
}

$token = [System.Text.RegularExpressions.Regex]::Replace($token, "[\x00-\x1F\x7F]", "").Trim()

Write-Host "GitHub-Token erfolgreich ermittelt." -ForegroundColor Green

$headers = @{
    "Authorization" = "Bearer $token"
    "Accept" = "application/vnd.github+json"
    "User-Agent" = "Nexa-Release-Uploader"
}

# 1. Hole Release-Informationen
Write-Host "Prüfe GitHub Release 'Installer'..." -ForegroundColor Cyan
$releaseUrl = "https://api.github.com/repos/venox21/Nexa-Browser/releases/tags/Installer"
$release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers -Method Get

$releaseId = $release.id
Write-Host "Release ID: $releaseId" -ForegroundColor White

# 2. Lösche bestehende Assets (NexaSetup.exe & NexaSetup.zip)
foreach ($asset in $release.assets) {
    if ($asset.name -eq "NexaSetup.exe" -or $asset.name -eq "NexaSetup.zip") {
        Write-Host "Lösche altes Asset '$($asset.name)' (ID: $($asset.id))..." -ForegroundColor Yellow
        $delUrl = "https://api.github.com/repos/venox21/Nexa-Browser/releases/assets/$($asset.id)"
        Invoke-RestMethod -Uri $delUrl -Headers $headers -Method Delete
    }
}

# 3. Lade NexaSetup.exe hoch
$exePath = Join-Path $ScriptDir "NexaSetup.exe"
if (Test-Path $exePath) {
    $exeSize = (Get-Item $exePath).Length / 1MB
    Write-Host ("Lade frische NexaSetup.exe hoch ({0:N2} MB)..." -f $exeSize) -ForegroundColor Cyan
    $uploadUrl = "https://uploads.github.com/repos/venox21/Nexa-Browser/releases/$releaseId/assets?name=NexaSetup.exe"
    
    $bytes = [System.IO.File]::ReadAllBytes($exePath)
    $uploadHeaders = @{
        "Authorization" = "Bearer $token"
        "Accept" = "application/vnd.github+json"
        "User-Agent" = "Nexa-Release-Uploader"
        "Content-Type" = "application/octet-stream"
    }
    
    $res = Invoke-RestMethod -Uri $uploadUrl -Headers $uploadHeaders -Method Post -Body $bytes
    Write-Host "NexaSetup.exe erfolgreich hochgeladen! URL: $($res.browser_download_url)" -ForegroundColor Green
}

# 4. Lade NexaSetup.zip hoch
$zipPath = Join-Path $ScriptDir "NexaSetup.zip"
if (Test-Path $zipPath) {
    $zipSize = (Get-Item $zipPath).Length / 1MB
    Write-Host ("Lade frische NexaSetup.zip hoch ({0:N2} MB)..." -f $zipSize) -ForegroundColor Cyan
    $uploadUrl = "https://uploads.github.com/repos/venox21/Nexa-Browser/releases/$releaseId/assets?name=NexaSetup.zip"
    
    $bytes = [System.IO.File]::ReadAllBytes($zipPath)
    $uploadHeaders = @{
        "Authorization" = "Bearer $token"
        "Accept" = "application/vnd.github+json"
        "User-Agent" = "Nexa-Release-Uploader"
        "Content-Type" = "application/zip"
    }
    
    $res = Invoke-RestMethod -Uri $uploadUrl -Headers $uploadHeaders -Method Post -Body $bytes
    Write-Host "NexaSetup.zip erfolgreich hochgeladen! URL: $($res.browser_download_url)" -ForegroundColor Green
}

Write-Host "`nRelease-Assets erfolgreich aktualisiert!" -ForegroundColor Green
