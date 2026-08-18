# PPTVideoEnhance plugin build & pack script
#
# Usage:
#   pwsh -File pack.ps1
#   .\pack.ps1 -Configuration Release
#
# Steps:
#   1. dotnet build PPTVideoEnhancePlugin.csproj
#   2. Collect plugin.icplugin + PPTVideoEnhancePlugin.dll
#   3. Pack into pptvideoenhance.icplugin (ZIP)
#
# 说明：插件复用宿主已加载的 Office 互操作 / Newtonsoft.Json 程序集（Private=false），
#       因此除自身 DLL 外不打包任何额外依赖，体积极小。

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here

Write-Host "==> Building pptvideoenhance plugin ($Configuration)" -ForegroundColor Cyan

# 1) Build plugin assembly
Push-Location $here
try {
    & dotnet build PPTVideoEnhancePlugin.csproj -c $Configuration
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

# Copy built DLL
$dllSrc = Join-Path $here "bin\PPTVideoEnhancePlugin.dll"
if (-not (Test-Path $dllSrc)) {
    $dllSrc = Join-Path $here "bin\Debug\PPTVideoEnhancePlugin.dll"
}
if (-not (Test-Path $dllSrc)) {
    throw "PPTVideoEnhancePlugin.dll not found after build"
}
Copy-Item -Force $dllSrc $staging

# 3) Create .icplugin (ZIP) at repo root
$packagePath = Join-Path $repoRoot "pptvideoenhance.icplugin"
if (Test-Path $packagePath) { Remove-Item -Force $packagePath }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $packagePath)

Write-Host "==> Package created: $packagePath" -ForegroundColor Green
