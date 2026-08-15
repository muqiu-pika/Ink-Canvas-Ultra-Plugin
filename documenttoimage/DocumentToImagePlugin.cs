using Ink_Canvas.Plugins;
using Ink_Canvas.Plugins.DocumentToImage.Converters;
using Ink_Canvas.Plugins.DocumentToImage.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Ink_Canvas.Plugins.DocumentToImage
{
    /// <summary>
    /// 文档转照片插件：将 Word / Excel / PDF 文件转为图片并插入 ICU 照片列表。
    /// 通过注册 document-open 路由响应主程序的「导入文档」按钮和命令行打开。
    /// 使用开源 NPOI 与 PdfiumViewer，无需安装 Microsoft Office。
    /// </summary>
    public class DocumentToImagePlugin : IPlugin
    {
        private IPluginHost _host;
        private string _pluginDirectory;
        private ResolveEventHandler _assemblyResolveHandler;
        private readonly Dictionary<string, FileSystemWatcher> _watchers = new Dictionary<string, FileSystemWatcher>();
        private readonly Dictionary<string, Timer> _debounceTimers = new Dictionary<string, Timer>();
        private readonly object _watcherLock = new object();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool SetDllDirectory(string lpPathName);

        public PluginManifest Manifest { get; } = new PluginManifest
        {
            Id = "ink-canvas.document-to-image",
            Name = "文档转照片",
            Version = "2.2.0",
            Author = "muqiu",
            Description = "将 Word / Excel / PDF 文件转换为图片并添加到照片列表，支持本地转换缓存与同名修改检测（无需 Office）",
            EntryAssembly = "DocumentToImagePlugin.dll",
            EntryClass = "Ink_Canvas.Plugins.DocumentToImage.DocumentToImagePlugin",
            MinHostVersion = "7.0.0"
        };

        public void Initialize(IPluginHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));

            // ICU 使用 Assembly.Load(byte[]) 加载插件主 DLL，导致 .NET 不会自动探测插件目录中的依赖。
            // 注册 AssemblyResolve 事件，手动从插件安装目录加载 NPOI、PdfiumViewer 等依赖 DLL。
            _pluginDirectory = FindPluginDirectory();
            _assemblyResolveHandler = CurrentDomain_AssemblyResolve;
            AppDomain.CurrentDomain.AssemblyResolve += _assemblyResolveHandler;

            // PdfiumViewer 默认从应用程序目录探测 x86\pdfium.dll，但插件通过 Assembly.Load(byte[]) 加载后，
            // pdfium.dll 实际位于插件安装目录。通过多种方式确保 PdfiumViewer 能找到原生 DLL。
            try
            {
                if (!string.IsNullOrEmpty(_pluginDirectory))
                {
                    string pdfiumDir = Path.Combine(_pluginDirectory, "x86");
                    string pdfiumPath = Path.Combine(pdfiumDir, "pdfium.dll");
                    if (File.Exists(pdfiumPath))
                    {
                        // 1. 将插件 x86 目录加入进程 DLL 搜索路径
                        SetDllDirectory(pdfiumDir);
                        // 2. 预加载 pdfium.dll，使 PdfiumViewer 的 NativeMethods 初始化时可直接使用
                        LoadLibrary(pdfiumPath);
                        // 3. 注册解析器作为兜底
                        PdfiumViewer.PdfiumResolver.Resolve += (sender, e) =>
                        {
                            if (File.Exists(pdfiumPath))
                            {
                                e.PdfiumFileName = pdfiumPath;
                            }
                        };
                    }
                    else
                    {
                        _host.Log($"未找到 pdfium.dll: {pdfiumPath}", PluginLogLevel.Warning);
                    }
                }
            }
            catch (Exception ex)
            {
                _host.Log($"初始化 PdfiumViewer 原生 DLL 失败: {ex.Message}", PluginLogLevel.Error);
            }

            _host.RegisterRouteHandler("document-open", OnDocumentOpen);
            _host.RegisterRouteHandler("document-watch", OnDocumentWatch);
            _host.Log("文档转照片插件已加载", PluginLogLevel.Info);
        }

        public void Shutdown()
        {
            lock (_watcherLock)
            {
                foreach (var timer in _debounceTimers.Values)
                {
                    timer?.Change(Timeout.Infinite, Timeout.Infinite);
                    timer?.Dispose();
                }
                _debounceTimers.Clear();

                foreach (var watcher in _watchers.Values)
                {
                    watcher.Changed -= OnFileChanged;
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                _watchers.Clear();
            }

            if (_assemblyResolveHandler != null)
                AppDomain.CurrentDomain.AssemblyResolve -= _assemblyResolveHandler;
            _host?.UnregisterRouteHandler("document-open");
            _host?.UnregisterRouteHandler("document-watch");
            _host?.Log("文档转照片插件已卸载", PluginLogLevel.Info);
        }

        /// <summary>
        /// 在 ICU Plugins 目录下查找本插件的安装目录。
        /// 通过查找包含 DocumentToImagePlugin.dll 与 plugin.icplugin 的子目录确定。
        /// </summary>
        private string FindPluginDirectory()
        {
            try
            {
                string root = _host?.PluginDirectory;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                    return null;

                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (File.Exists(Path.Combine(dir, "DocumentToImagePlugin.dll")) &&
                        File.Exists(Path.Combine(dir, "plugin.icplugin")))
                    {
                        return dir;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 当 .NET 无法解析本插件的依赖程序集时，从插件安装目录加载对应 DLL。
        /// </summary>
        private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string name = new AssemblyName(args.Name).Name;
                if (string.IsNullOrEmpty(_pluginDirectory))
                    return null;

                string path = Path.Combine(_pluginDirectory, name + ".dll");
                if (File.Exists(path))
                    return Assembly.LoadFrom(path);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 处理 document-open 路由。参数应为文档的完整路径。
        /// </summary>
        private bool OnDocumentOpen(object parameter)
        {
            string filePath = parameter as string;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                _host?.ShowNotification("未找到要导入的文档");
                return false;
            }

            int dpi = _host?.PhotoClarityDpi ?? 150;
            ConversionProgressWindow progressWindow = null;

            // 在主线程创建进度窗口，确保 Owner 与 Dispatcher 正确
            _host?.MainWindow?.Dispatcher.Invoke(() =>
            {
                progressWindow = new ConversionProgressWindow(_host.MainWindow);
                progressWindow.Show();
            });

            var progress = new Progress<ConversionProgress>(p => progressWindow?.Report(p));

            // XPS 与 WPF 文档渲染需要 STA 线程，在线程池（MTA）中会报 DocumentReference 等异常。
            // 创建一个后台 STA 线程执行转换，避免阻塞主窗口 UI。
            var conversionThread = new Thread(() => ConvertAndAddAsync(filePath, dpi, progress, progressWindow))
            {
                IsBackground = true,
                Name = "DocumentToImageConversion"
            };
            conversionThread.SetApartmentState(ApartmentState.STA);
            conversionThread.Start();
            return true;
        }

        /// <summary>
        /// 处理 document-watch 路由。主程序从本地缓存直接加载文档照片后调用，
        /// 仅为该文档附加文件修改监视（不触发转换），以便文档被外部修改时自动刷新照片。
        /// </summary>
        private bool OnDocumentWatch(object parameter)
        {
            string filePath = parameter as string;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;
            EnsureFileWatcher(filePath);
            return true;
        }

        private void ConvertAndAddAsync(string filePath, int dpi, IProgress<ConversionProgress> progress, ConversionProgressWindow progressWindow)
        {
            bool succeeded = false;
            string tempDir = null;
            try
            {
                // 若照片列表（内存）中已有该文档照片，则直接复用，避免重复转换。
                // 磁盘缓存有效性与同名文档修改检测由主程序在触发本路由前完成：
                // 能走到这里说明确实需要（重新）转换，转换结果会替换本地缓存中的原照片。
                if (_host?.HasCapturedPhotoForFile(filePath) == true)
                {
                    _host?.MainWindow?.Dispatcher.Invoke(() =>
                    {
                        progressWindow?.SetDone("该文档照片已存在于照片列表");
                    });
                    ShowNotification("该文档照片已存在，未重新生成");
                    succeeded = true;
                    return;
                }

                List<string> pageFiles = ConvertDocumentToFiles(filePath, dpi, progress, out tempDir);
                if (pageFiles == null || pageFiles.Count == 0)
                {
                    _host?.MainWindow?.Dispatcher.Invoke(() => progressWindow?.SetError("未生成任何照片"));
                    ShowNotification("文档转图片失败，未生成任何照片");
                    return;
                }

                // 逐块加载、传给主程序、释放，避免所有分块同时驻留内存导致 OOM
                int totalChunks = pageFiles.Count;
                _host?.MainWindow?.Dispatcher.Invoke(() =>
                {
                    for (int i = 0; i < pageFiles.Count; i++)
                    {
                        string chunkFilePath = pageFiles.Count == 1
                            ? filePath
                            : $"{filePath}#{i}";

                        // 逐块加载，每块仅有一个 BitmapImage 驻留
                        BitmapImage chunkImage = ImageConcatenator.LoadBitmapImageFromFile(pageFiles[i]);
                        try
                        {
                            _host?.AddCapturedPhoto(chunkImage, chunkFilePath);
                        }
                        finally
                        {
                            // 主程序内部已 SaveBitmapImageToPhotoFile 落盘并 ReleaseImageMemory，
                            // 此处再主动释放 BitmapImage 引用，帮助 GC 回收
                            chunkImage = null;
                        }
                    }

                    if (totalChunks == 1)
                    {
                        progressWindow?.SetDone($"已生成 1 张文档照片");
                        ShowNotification("已生成文档照片，已放入照片列表");
                    }
                    else
                    {
                        progressWindow?.SetDone($"文档较大，已拆分为 {totalChunks} 张分块，已放入照片列表");
                        ShowNotification($"已将文档拆分为 {totalChunks} 张分块，已放入照片列表");
                    }
                });

                succeeded = true;
                EnsureFileWatcher(filePath);
            }
            catch (Exception ex)
            {
                _host?.Log($"文档转图片失败 [{filePath}]: {ex}", PluginLogLevel.Error);
                _host?.MainWindow?.Dispatcher.Invoke(() => progressWindow?.SetError(ex.Message));
                ShowNotification($"文档导入失败: {ex.Message}");
            }
            finally
            {
                // 清理临时目录
                try { if (tempDir != null && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                // 主动触发 GC 回收转换过程中产生的临时内存
                try { GC.Collect(); GC.WaitForPendingFinalizers(); } catch { }

                // 成功时短暂停留，失败时停留更久方便查看错误
                _host?.MainWindow?.Dispatcher.Invoke(async () =>
                {
                    if (progressWindow != null)
                    {
                        await Task.Delay(succeeded ? 600 : 2500);
                        progressWindow.Close();
                    }
                });
            }
        }

        private List<string> ConvertDocumentToFiles(string filePath, int dpi, IProgress<ConversionProgress> progress, out string tempDir)
        {
            tempDir = null;
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            // 创建临时目录，渲染每页为独立 PNG 文件写入磁盘
            tempDir = Path.Combine(Path.GetTempPath(), $"icu_doc_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                List<string> pageFiles;

                if (ext == ".docx")
                {
                    pageFiles = WordToImageConverter.ConvertToFiles(filePath, dpi, tempDir, progress);
                }
                else if (ext == ".xls" || ext == ".xlsx")
                {
                    pageFiles = ExcelToImageConverter.ConvertToFiles(filePath, dpi, tempDir, progress);
                }
                else if (ext == ".pdf")
                {
                    pageFiles = PdfToImageConverter.ConvertToFiles(filePath, dpi, tempDir, progress);
                }
                else
                {
                    throw new NotSupportedException($"不支持的文件格式: {ext}");
                }

                if (pageFiles == null || pageFiles.Count == 0)
                    return pageFiles;

                progress?.Report(new ConversionProgress
                {
                    FileName = Path.GetFileName(filePath),
                    Message = "正在加载分块图片..."
                });

                // 不再把多页拼成一张长照片：每个渲染分块（pageFile）直接作为一块照片返回，
                // 由主程序前端按块序号拼接成完整长图。此处仅返回文件路径，
                // 由调用方逐块加载、逐块传给主程序并释放，避免所有分块同时驻留内存导致 OOM。
                return pageFiles;
            }
            catch
            {
                // 失败时立即清理临时目录
                try { if (tempDir != null && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                throw;
            }
        }

        private void EnsureFileWatcher(string filePath)
        {
            lock (_watcherLock)
            {
                if (_watchers.ContainsKey(filePath))
                    return;

                string directory = Path.GetDirectoryName(filePath);
                string fileName = Path.GetFileName(filePath);
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                    return;

                var watcher = new FileSystemWatcher(directory, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Changed += OnFileChanged;
                watcher.EnableRaisingEvents = true;
                _watchers[filePath] = watcher;
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            string filePath = e.FullPath;
            lock (_watcherLock)
            {
                if (!_debounceTimers.TryGetValue(filePath, out var timer))
                {
                    timer = new Timer(state => OnDebounceElapsed((string)state), filePath, Timeout.Infinite, Timeout.Infinite);
                    _debounceTimers[filePath] = timer;
                }
                timer.Change(1500, Timeout.Infinite);
            }
        }

        private void OnDebounceElapsed(string filePath)
        {
            lock (_watcherLock)
            {
                if (!_watchers.ContainsKey(filePath))
                    return;
            }

            if (!File.Exists(filePath))
                return;

            var refreshThread = new Thread(() => RefreshDocumentAsync(filePath))
            {
                IsBackground = true,
                Name = "DocumentToImageRefresh"
            };
            refreshThread.SetApartmentState(ApartmentState.STA);
            refreshThread.Start();
        }

        private void RefreshDocumentAsync(string filePath)
        {
            string tempDir = null;
            try
            {
                int dpi = _host?.PhotoClarityDpi ?? 150;
                List<string> pageFiles = ConvertDocumentToFiles(filePath, dpi, progress: null, out tempDir);
                if (pageFiles == null || pageFiles.Count == 0)
                    return;

                _host?.MainWindow?.Dispatcher.Invoke(() =>
                {
                    // 逐块更新：分块照片的来源标识为「文档路径#块序号」，与转换时保持一致。
                    // 逐块加载、更新、释放，避免所有分块同时驻留内存导致 OOM。
                    for (int i = 0; i < pageFiles.Count; i++)
                    {
                        string chunkFilePath = pageFiles.Count == 1
                            ? filePath
                            : $"{filePath}#{i}";

                        BitmapImage chunkImage = ImageConcatenator.LoadBitmapImageFromFile(pageFiles[i]);
                        try
                        {
                            _host?.UpdateCapturedPhoto(chunkFilePath, chunkImage);
                            _host?.ReplaceDocumentImageOnCanvas(chunkFilePath, chunkImage);
                        }
                        finally
                        {
                            chunkImage = null;
                        }
                    }
                    _host?.ShowNotification("文档已更新，已重新生成照片");
                });
            }
            catch (Exception ex)
            {
                _host?.Log($"文档自动刷新失败 [{filePath}]: {ex}", PluginLogLevel.Error);
            }
            finally
            {
                // 清理临时目录
                try { if (tempDir != null && Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                // 释放转换过程中产生的位图内存
                try { GC.Collect(); GC.WaitForPendingFinalizers(); } catch { }
            }
        }

        private void ShowNotification(string message)
        {
            try
            {
                if (_host?.MainWindow?.Dispatcher != null)
                {
                    _host.MainWindow.Dispatcher.Invoke(() => _host.ShowNotification(message));
                }
                else
                {
                    _host?.ShowNotification(message);
                }
            }
            catch { }
        }
    }
}
