# HotkeyCustomize plugin build & pack script
#
# Usage:
#   pwsh -File pack.ps1
#
# Steps:
#   1. dotnet build HotkeyCustomizerPlugin.csproj
#   2. Collect plugin.icplugin + HotkeyCustomizerPlugin.dll
#   3. Pack into hotkeycustomize.icplugin (ZIP)

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here

Write-Host "==> Building hotkeycustomize plugin ($Configuration)" -ForegroundColor Cyan

Push-Location $here
try {
    & dotnet build HotkeyCustomizerPlugin.csproj -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed (exit $LASTEXITCODE)"
    }
}
finally {
    Pop-Location
}

$staging = Join-Path $here "obj\staging"
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
New-Item -ItemType Directory -Force -Path $staging | Out-Null

Copy-Item -Force (Join-Path $here "plugin.icplugin") $staging

$dllSrc = Join-Path $here "bin\HotkeyCustomizerPlugin.dll"
if (-not (Test-Path $dllSrc)) {
    $dllSrc = Join-Path $here "bin\Debug\HotkeyCustomizerPlugin.dll"
}
if (-not (Test-Path $dllSrc)) {
    throw "HotkeyCustomizerPlugin.dll not found after build"
}
Copy-Item -Force $dllSrc $staging

$packagePath = Join-Path $repoRoot "hotkeycustomize.icplugin"
if (Test-Path $packagePath) { Remove-Item -Force $packagePath }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $packagePath)

Write-Host "==> Package created: $packagePath" -ForegroundColor Green