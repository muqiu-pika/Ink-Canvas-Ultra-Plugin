# DocumentToImage plugin build & pack script
#
# Usage:
#   pwsh -File pack.ps1
#   .\pack.ps1
#
# Steps:
#   1. dotnet build DocumentToImagePlugin.csproj
#   2. Collect plugin.icplugin + DocumentToImagePlugin.dll + dependencies
#   3. Pack into documenttoimage.icplugin (ZIP)

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here

Write-Host "==> Building documenttoimage plugin ($Configuration)" -ForegroundColor Cyan

# 1) Build plugin assembly
Push-Location $here
try {
    & dotnet build DocumentToImagePlugin.csproj -c $Configuration
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
$dllSrc = Join-Path $here "bin\DocumentToImagePlugin.dll"
if (-not (Test-Path $dllSrc)) {
    # Fallback to Debug
    $dllSrc = Join-Path $here "bin\Debug\DocumentToImagePlugin.dll"
}
if (-not (Test-Path $dllSrc)) {
    throw "DocumentToImagePlugin.dll not found after build"
}
Copy-Item -Force $dllSrc $staging

# Copy plugin-specific dependencies (do NOT bundle host DLLs like iNKORE/Newtonsoft.Json/AForge/Office)
$deps = @(
    "NPOI.dll",
    "NPOI.OOXML.dll",
    "NPOI.OpenXml4Net.dll",
    "NPOI.OpenXmlFormats.dll",
    "ICSharpCode.SharpZipLib.dll",
    "BouncyCastle.Crypto.dll",
    "PdfiumViewer.dll"
)
$binDir = Split-Path -Parent $dllSrc
foreach ($dep in $deps) {
    $src = Join-Path $binDir $dep
    if (Test-Path $src) {
        Copy-Item -Force $src $staging
    }
    else {
        Write-Warning "Dependency not found: $dep"
    }
}

# Copy native pdfium DLL (x86)
$pdfiumSrc = Join-Path $binDir "x86\pdfium.dll"
if (Test-Path $pdfiumSrc) {
    $x86Dir = Join-Path $staging "x86"
    New-Item -ItemType Directory -Force -Path $x86Dir | Out-Null
    Copy-Item -Force $pdfiumSrc $x86Dir
}
else {
    throw "pdfium.dll not found. Ensure PdfiumViewer.Native.x86.no_v8-no_xfa package restored."
}

# 3) Create .icplugin (ZIP) at repo root
$packagePath = Join-Path $repoRoot "documenttoimage.icplugin"
if (Test-Path $packagePath) { Remove-Item -Force $packagePath }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $packagePath)

Write-Host "==> Package created: $packagePath" -ForegroundColor Green
