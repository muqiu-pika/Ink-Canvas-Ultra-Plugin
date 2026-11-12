# 将 FunctionGraphPlugin 打包为 functiongraph.icplugin
# .icplugin = ZIP，根目录仅含 plugin.icplugin（清单）+ FunctionGraphPlugin.dll
# 注意：不要打包宿主 / WPF / IACore / IAWinFX 等依赖 DLL
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$dll = Join-Path $here 'bin\FunctionGraphPlugin.dll'
$manifest = Join-Path $here 'plugin.icplugin'
$zip = Join-Path (Split-Path -Parent $here) 'functiongraph.icplugin'

if (-not (Test-Path $dll)) { throw "未找到 $dll，请先执行 dotnet build" }
if (-not (Test-Path $manifest)) { throw "未找到 $manifest" }

$stage = Join-Path $here 'bin\_pack'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

Copy-Item $manifest (Join-Path $stage 'plugin.icplugin')
Copy-Item $dll (Join-Path $stage 'FunctionGraphPlugin.dll')

if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Remove-Item $stage -Recurse -Force

Write-Host "已打包: $zip" -ForegroundColor Green
Write-Host "部署到: Ink Canvas\bin\<配置>\Plugins\ink-canvas.function-graph\ 并在同目录 plugins.json 加 ""ink-canvas.function-graph"": true"
