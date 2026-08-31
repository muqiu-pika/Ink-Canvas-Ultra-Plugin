$here = 'C:\Users\muqiu\Desktop\icu\muqiu-pika\Ink-Canvas-Ultra-Plugin\pptvideoenhance'
$repoRoot = 'C:\Users\muqiu\Desktop\icu\muqiu-pika\Ink-Canvas-Ultra-Plugin'
$log = 'C:\Users\muqiu\Desktop\icu\muqiu-pika\Ink-Canvas-Ultra-Plugin\pack_log.txt'

# 重新打包必然改变 .icplugin 的字节数（zip 内含文件时间戳），
# 若不同步市场目录里的 size / SHA256，ICU 插件工坊会判定文件被篡改而拒绝安装。
# 因此每次打包成功后都重算一次市场元数据。
function Sync-MarketMetadata {
    param($logFile)
    $sync = Join-Path $repoRoot 'tools\update-market-metadata.py'
    if (-not (Test-Path $sync)) {
        "SYNC METADATA SKIPPED: 未找到 $sync" | Out-File -FilePath $logFile -Append -Encoding utf8
        return
    }
    # 优先用 python，其次用 py 启动器
    $pyExe = $null
    $pyArgs = @($sync, '--repo-root', $repoRoot)
    $cmdPython = Get-Command python -ErrorAction SilentlyContinue
    $cmdPy = Get-Command py -ErrorAction SilentlyContinue
    if ($cmdPython) {
        $pyExe = $cmdPython.Source
    } elseif ($cmdPy) {
        $pyExe = $cmdPy.Source
        $pyArgs = @('-3') + $pyArgs
    }

    # 脚本输出含中文，需按 UTF-8 解码外部进程输出，否则日志里会是乱码
    $prevEnc = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $env:PYTHONIOENCODING = 'utf-8'
    try {
        if ($pyExe) {
            $out = & $pyExe @pyArgs 2>&1
        } else {
            $out = "未找到 python / py，请安装 Python 或手动运行 tools\update-market-metadata.py"
        }
        "SYNC METADATA:" | Out-File -FilePath $logFile -Append -Encoding utf8
        ($out | Out-String) | Out-File -FilePath $logFile -Append -Encoding utf8
    } catch {
        "SYNC METADATA ERROR: $($_.Exception.Message)" | Out-File -FilePath $logFile -Append -Encoding utf8
    } finally {
        [Console]::OutputEncoding = $prevEnc
    }
}

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
    Sync-MarketMetadata $log
} catch {
    "ERROR: $($_.Exception.Message)" | Out-File -FilePath $log -Encoding utf8
}
