<#
.SYNOPSIS
    LoRAMancer Background Auto-Updater
.DESCRIPTION
    Checks version manifest, downloads package delta, terminates running instances,
    replaces application binaries, and relaunches the application.
#>

[CmdletBinding()]
param(
    [string]$ManifestUrl = "https://raw.githubusercontent.com/loramancer/loramancer/main/installer/version.json",
    [string]$InstallPath = "$env:LOCALAPPDATA\LoRAMancer",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

Write-Host "Checking for updates from $ManifestUrl..." -ForegroundColor Cyan

try {
    $manifestJson = Invoke-RestMethod -Uri $ManifestUrl -UseBasicParsing
} catch {
    Write-Warning "Failed to check update server: $_"
    exit 0
}

$latestVersion = $manifestJson.version
$downloadUrl = $manifestJson.downloadUrl
$sha256 = $manifestJson.sha256

$binExe = Join-Path $InstallPath "bin\LoRAMancer.App.exe"
$currentVersion = "0.0.0"
if (Test-Path $binExe) {
    $fileVer = (Get-Item $binExe).VersionInfo.ProductVersion
    if ($fileVer) {
        $currentVersion = $fileVer
    }
}

Write-Host "Current Version: $currentVersion"
Write-Host "Latest Version:  $latestVersion"

if (-not $Force -and ($currentVersion -ge $latestVersion)) {
    Write-Host "LoRAMancer is already up to date." -ForegroundColor Green
    exit 0
}

Write-Host "Update available! Downloading payload from $downloadUrl..." -ForegroundColor Yellow
$tempInstaller = Join-Path $env:TEMP "LoRAMancer-Update.exe"

Invoke-WebRequest -Uri $downloadUrl -OutFile $tempInstaller

# Validate SHA256 if supplied
if ($sha256 -and ($sha256 -ne "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")) {
    $hash = (Get-FileHash -Path $tempInstaller -Algorithm SHA256).Hash
    if ($hash -ne $sha256) {
        Remove-Item $tempInstaller -Force
        throw "Checksum verification failed! Expected: $sha256, Got: $hash"
    }
}

Write-Host "Stopping running instances of LoRAMancer..." -ForegroundColor Cyan
Get-Process -Name "LoRAMancer.App" -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Launching installer in silent update mode..." -ForegroundColor Green
Start-Process -FilePath $tempInstaller -ArgumentList "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS"
