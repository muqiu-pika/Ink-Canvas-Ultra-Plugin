$here = 'C:\Users\muqiu\Desktop\icu\muqiu-pika\Ink-Canvas-Ultra-Plugin\pptvideoenhance'
$repoRoot = 'C:\Users\muqiu\Desktop\icu\muqiu-pika\Ink-Canvas-Ultra-Plugin'
$log = 'C:\Users\muqiu\Desktop\icu\muqiu-pika\Ink-Canvas-Ultra-Plugin\pack_log.txt'
try {
    $staging = Join-Path $here ('obj\staging_' + [System.DateTime]::Now.ToString('HHmmssfff'))
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    Copy-Item -Force (Join-Path $here 'plugin.icplugin') $staging
    $dllSrc = Join-Path $here 'bin\PPTVideoEnhancePlugin.dll'
    if (-not (Test-Path $dllSrc)) { throw "DLL not found: $dllSrc" }
    Copy-Item -Force $dllSrc $staging
    $zipPath = Join-Path $staging 'pptvideoenhance.zip'
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zipPath -Force
    $packagePath = Join-Path $repoRoot 'pptvideoenhance.icplugin'
    Copy-Item -Force $zipPath $packagePath
    $pkg = Get-Item $packagePath
    "PACKAGED OK: $($pkg.FullName) $($pkg.Length) bytes $($pkg.LastWriteTime)" | Out-File -FilePath $log -Encoding utf8
    $deployDir = 'C:\Users\muqiu\Desktop\icu\muqiu-pika\Ink-Canvas-Ultra\Ink Canvas\bin\Debug\Plugins\ink-canvas.ppt-video-enhance'
    if (-not (Test-Path $deployDir)) { New-Item -ItemType Directory -Force -Path $deployDir | Out-Null }
    $deployDll = Join-Path $deployDir 'PPTVideoEnhancePlugin.dll'
    Copy-Item -Force $dllSrc $deployDll
    Copy-Item -Force (Join-Path $here 'plugin.icplugin') $deployDir
    $dd = Get-Item $deployDll
    "DEPLOYED DLL: $($dd.FullName) $($dd.Length) bytes $($dd.LastWriteTime)" | Out-File -FilePath $log -Append -Encoding utf8
} catch {
    "ERROR: $($_.Exception.Message)" | Out-File -FilePath $log -Encoding utf8
}
