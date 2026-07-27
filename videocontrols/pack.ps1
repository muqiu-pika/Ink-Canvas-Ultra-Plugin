# videocontrols plugin build & pack script
#
# Usage:
#   pwsh -File pack.ps1
#   .\pack.ps1
#
# Steps:
#   1. dotnet build VideoControlsPlugin.csproj
#   2. Pack plugin.icplugin + VideoControlsPlugin.dll into videocontrols.icplugin (ZIP)

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here

Write-Host "==> Building videocontrols plugin ($Configuration)" -ForegroundColor Cyan

# 1) Build plugin assembly
Push-Location $here
try {
    & dotnet build VideoControlsPlugin.csproj -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed (exit $LASTEXITCODE)"
    }
}
finally {
    Pop-Location
}

# 2) Prepare staging directory
$staging = Join-Path $here "obj\staging"
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

# Copy manifest
Copy-Item -Force (Join-Path $here "plugin.icplugin") $staging

# Copy built DLL (from bin\ for Release)
$dllSrc = Join-Path $here "bin\VideoControlsPlugin.dll"
if (-not (Test-Path $dllSrc)) {
    # Fallback to Debug
    $dllSrc = Join-Path $here "bin\Debug\VideoControlsPlugin.dll"
}
if (-not (Test-Path $dllSrc)) {
    throw "VideoControlsPlugin.dll not found after build"
}
Copy-Item -Force $dllSrc $staging

# 3) Create .icplugin (ZIP) at repo root
$packagePath = Join-Path $repoRoot "videocontrols.icplugin"
if (Test-Path $packagePath) { Remove-Item -Force $packagePath }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $packagePath)

Write-Host "==> Package created: $packagePath" -ForegroundColor Green
