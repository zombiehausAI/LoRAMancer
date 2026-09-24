<#
.SYNOPSIS
    LoRAMancer Automated Pipeline: Clean, Test, Build, Package, and Install.
.DESCRIPTION
    Automates the full release cycle for LoRAMancer:
    1. Clean: Removes previous build artifacts, bin/obj caches, and staging directories.
    2. Test: Executes all unit test suites, verifying zero failures before packaging.
    3. Build: Compiles and publishes optimized Release binaries for Windows x64.
    4. Package: Bundles the application, default plugins, manifest, and creates a ZIP distribution package (and Inno Setup EXE if ISCC is installed).
    5. Install: Optional flag (-Install) to immediately deploy and test the installation locally.
.PARAMETER Configuration
    Build configuration (default: Release).
.PARAMETER SkipTests
    Skip running automated unit tests.
.PARAMETER Install
    Immediately install the built package locally for testing.
.PARAMETER InstallPath
    Target installation directory when -Install is specified (default: $env:LOCALAPPDATA\LoRAMancer).
.PARAMETER OutputDir
    Output directory for packaged release artifacts (default: artifacts).
.EXAMPLE
    .\Build-And-Package.ps1
.EXAMPLE
    .\Build-And-Package.ps1 -Install
#>

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Install,
    [string]$InstallPath = "$env:LOCALAPPDATA\LoRAMancer",
    [string]$OutputDir = "artifacts"
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$artifactsPath = Join-Path $repoRoot $OutputDir
$stagingPath = Join-Path $artifactsPath "staging"
$packageAppDir = Join-Path $stagingPath "LoRAMancer"
$appCsproj = Join-Path $repoRoot "src\LoRAMancer.App\LoRAMancer.App.csproj"
$testsCsproj = Join-Path $repoRoot "tests\LoRAMancer.Tests\LoRAMancer.Tests.csproj"
$solutionFile = Join-Path $repoRoot "LoRAMancer.slnx"

# Extract Version from version.json or default
$versionJsonPath = Join-Path $repoRoot "installer\version.json"
$appVersion = "1.0.0"
if (Test-Path $versionJsonPath) {
    try {
        $vJson = Get-Content $versionJsonPath -Raw | ConvertFrom-Json
        if ($vJson.version) {
            $appVersion = $vJson.version
        }
    } catch {
        # Fall back to default
    }
}

Write-Host "===========================================================" -ForegroundColor Magenta
Write-Host "       LoRAMancer Build, Test & Packaging Pipeline         " -ForegroundColor Magenta
Write-Host "       Version: $appVersion | Target: win-x64 ($Configuration)  " -ForegroundColor Magenta
Write-Host "===========================================================" -ForegroundColor Magenta

# -------------------------------------------------------------
# STEP 1: CLEAN
# -------------------------------------------------------------
Write-Host "`n[1/4] Cleaning previous builds and caches..." -ForegroundColor Cyan

# Clean via dotnet
if (Test-Path $solutionFile) {
    Write-Host "  > Running dotnet clean..." -ForegroundColor Gray
    & dotnet clean $solutionFile -c $Configuration --verbosity quiet
}

# Clean artifacts and staging directories
if (Test-Path $artifactsPath) {
    Write-Host "  > Purging $artifactsPath..." -ForegroundColor Gray
    Remove-Item $artifactsPath -Recurse -Force -ErrorAction SilentlyContinue
}

# Clean bin and obj folders in src and tests
Get-ChildItem -Path (Join-Path $repoRoot "src"), (Join-Path $repoRoot "tests") -Include "bin", "obj" -Directory -Recurse | ForEach-Object {
    try {
        Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    } catch {
        # Ignore locked files if any
    }
}

New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
New-Item -ItemType Directory -Path $packageAppDir -Force | Out-Null
Write-Host "  > Clean completed." -ForegroundColor Green

# -------------------------------------------------------------
# STEP 2: TEST
# -------------------------------------------------------------
if (-not $SkipTests) {
    Write-Host "`n[2/4] Executing automated test suite..." -ForegroundColor Cyan
    if (Test-Path $testsCsproj) {
        & dotnet test $testsCsproj -c $Configuration --verbosity normal --nologo
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Test suite failed! Aborting packaging to protect build integrity."
            exit $LASTEXITCODE
        }
        Write-Host "  > All test suites passed successfully." -ForegroundColor Green
    } else {
        Write-Warning "Test project not found at $testsCsproj. Skipping tests."
    }
} else {
    Write-Host "`n[2/4] Skipping tests (-SkipTests specified)." -ForegroundColor Yellow
}

# -------------------------------------------------------------
# STEP 3: BUILD & PUBLISH
# -------------------------------------------------------------
Write-Host "`n[3/4] Publishing LoRAMancer application ($Configuration)..." -ForegroundColor Cyan

$publishArgs = @(
    "publish",
    $appCsproj,
    "-c", $Configuration,
    "-f", "net10.0-windows10.0.19041.0",
    "-r", "win-x64",
    "--no-self-contained",
    "-o", $packageAppDir
)

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

# Bundle Default Plugins
$pluginsSource = Join-Path $repoRoot "plugins"
$pluginsDest = Join-Path $packageAppDir "plugins"
if (Test-Path $pluginsSource) {
    Write-Host "  > Bundling default plugins..." -ForegroundColor Gray
    New-Item -ItemType Directory -Path $pluginsDest -Force | Out-Null
    Copy-Item "$pluginsSource\*" -Destination $pluginsDest -Recurse -Force
}

# Copy Licensing and Installer Support Files into package
Copy-Item (Join-Path $repoRoot "LICENSE") -Destination $packageAppDir -Force
Copy-Item (Join-Path $repoRoot "README.md") -Destination $packageAppDir -Force
Copy-Item (Join-Path $repoRoot "installer\Install-LoRAMancer.ps1") -Destination $packageAppDir -Force

Write-Host "  > Application published to staging: $packageAppDir" -ForegroundColor Green

# -------------------------------------------------------------
# STEP 4: PACKAGE
# -------------------------------------------------------------
Write-Host "`n[4/4] Creating distribution packages..." -ForegroundColor Cyan

$zipFilename = "LoRAMancer-v$appVersion-win-x64.zip"
$zipPath = Join-Path $artifactsPath $zipFilename

Write-Host "  > Compressing ZIP archive: $zipFilename..." -ForegroundColor Gray
Compress-Archive -Path "$packageAppDir\*" -DestinationPath $zipPath -CompressionLevel Optimal -Force
Write-Host "  > ZIP distribution ready: $zipPath" -ForegroundColor Green

# Generate SHA256 Checksum
$zipHash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
$checksumFile = Join-Path $artifactsPath "checksums.sha256"
"$zipHash  $zipFilename" | Out-File -FilePath $checksumFile -Encoding utf8
Write-Host "  > Checksum (SHA256): $zipHash" -ForegroundColor Gray

# Check for Inno Setup compiler (ISCC.exe)
$isccCandidates = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$isccPath = $isccCandidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($isccPath) {
    Write-Host "  > Found Inno Setup compiler: $isccPath" -ForegroundColor Gray
    Write-Host "  > Building Windows Setup Installer (EXE)..." -ForegroundColor Gray
    $issScript = Join-Path $repoRoot "installer\LoRAMancer.iss"
    if (Test-Path $issScript) {
        & $isccPath "/DMyAppVersion=$appVersion" $issScript
        if ($LASTEXITCODE -eq 0) {
            Write-Host "  > Inno Setup installer compiled successfully!" -ForegroundColor Green
        } else {
            Write-Warning "Inno Setup compilation returned non-zero code ($LASTEXITCODE)."
        }
    }
} else {
    Write-Host "  > Inno Setup compiler (ISCC.exe) not found on system. Standalone ZIP package generated." -ForegroundColor Gray
}

# Clean staging directory
Remove-Item $stagingPath -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n===========================================================" -ForegroundColor Green
Write-Host "             Packaging Pipeline Succeeded!                 " -ForegroundColor Green
Write-Host "  Output Directory: $artifactsPath                         " -ForegroundColor Green
Write-Host "  ZIP Package:      $zipFilename                           " -ForegroundColor Green
Write-Host "===========================================================" -ForegroundColor Green

# -------------------------------------------------------------
# STEP 5: OPTIONAL LOCAL INSTALLATION & TESTING
# -------------------------------------------------------------
if ($Install) {
    Write-Host "`n[*] Starting immediate local installation for testing..." -ForegroundColor Magenta
    $installerScript = Join-Path $repoRoot "installer\Install-LoRAMancer.ps1"
    if (Test-Path $installerScript) {
        & $installerScript -InstallPath $InstallPath -CreateDesktopShortcut
    } else {
        Write-Error "Installer script not found at $installerScript."
        exit 1
    }
} else {
    Write-Host "`nTo install and test this build now, run:" -ForegroundColor Cyan
    Write-Host "  .\Build-And-Package.ps1 -Install" -ForegroundColor White
    Write-Host "or extract and execute:" -ForegroundColor Cyan
    Write-Host "  $zipPath" -ForegroundColor White
}
