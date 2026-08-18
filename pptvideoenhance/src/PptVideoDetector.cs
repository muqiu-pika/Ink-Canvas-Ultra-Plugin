using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Office.Interop.PowerPoint;
using Office = Microsoft.Office.Core;

namespace Ink_Canvas.Plugins.PPTVideoEnhance
{
    /// <summary>
    /// 在专用 STA COM 线程上轮询 PowerPoint / WPS 放映窗口，识别当前幻灯片中的视频控件区域，
    /// 并将其左/上/宽/高（磅）转换为屏幕像素矩形，供插件做光标命中测试。
    /// 移植自 icc 的 PPTController.GetSmartRegions / IsVideoShape。
    /// </summary>
    internal sealed class PptVideoDetector : IDisposable
    {
        // MSO / PPT 形状与媒体常量（与 icc 保持一致）
        private const int MsoWebVideo = 26;            // msoWebVideo：在线视频
        private const int MsoMedia = 16;               // msoMedia：多媒体形状
        private const int MsoOLEControlObject = 12;    // msoOLEControlObject：ActiveX 控件
        private const int MsoEmbeddedOLEObject = 7;    // msoEmbeddedOLEObject：嵌入式 OLE
        private const int PpMediaTypeMovie = 3;        // ppMediaTypeMovie：视频
        private const int PpMediaTypeFlash = 15;       // 旧版 Flash，也属视频类

        private readonly ComContext _com;
        private readonly object _lock = new object();
        private List<VideoRegion> _regions = new List<VideoRegion>();
        private bool _slideShowActive;
        private bool _running;
        private DispatcherTimer _refreshTimer;

        public PptVideoDetector(ComContext com)
        {
            _com = com;
        }

        public void Start()
        {
            _running = true;
            _com.Start();
            _com.Invoke(() =>
            {
                _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                _refreshTimer.Tick += (s, e) => Refresh();
                _refreshTimer.Start();
            });
            _com.BeginInvoke((Action)Refresh);
        }

        public void Stop()
        {
            _running = false;
            _com.Invoke(() => { if (_refreshTimer != null) _refreshTimer.Stop(); });
            _com.Stop();
        }

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }

        public IReadOnlyList<VideoRegion> GetRegions()
        {
            lock (_lock) return new List<VideoRegion>(_regions);
        }

        public bool IsSlideShowActive()
        {
            lock (_lock) return _slideShowActive;
        }

        private void Refresh()
        {
            if (!_running) return;
            try
            {
                var regions = new List<VideoRegion>();
                bool active = false;

                // 1) 微软 PowerPoint（主路径，使用 PIA 互操作）
                if (TryDetectPowerPoint(regions)) active = true;

                // 2) WPS 演示（尽力而为，使用 dynamic + 多 ProgID 兜底）
                if (TryDetectWps(regions)) active = true;

                lock (_lock)
                {
                    _regions = regions;
                    _slideShowActive = active;
                }
            }
            catch
            {
                // 任何异常都不应中断 COM 轮询线程
            }
        }

        // ===== PowerPoint =====

        private bool TryDetectPowerPoint(List<VideoRegion> outRegions)
        {
            try
            {
                var app = (Application)Marshal.GetActiveObject("PowerPoint.Application");
                if (app == null) return false;
                return DetectFromApp(app, outRegions);
            }
            catch
            {
                return false;
            }
        }

        private bool DetectFromApp(Application app, List<VideoRegion> outRegions)
        {
            if (app.SlideShowWindows == null || app.SlideShowWindows.Count <= 0) return false;
            SlideShowWindow ssw = app.SlideShowWindows[1];
            if (ssw == null) return false;
            var view = ssw.View;
            if (view == null) return false;
            var slide = view.Slide;
            if (slide == null) return false;

            var hwnd = new IntPtr(ssw.HWND);
            double winLeft, winTop, winW, winH;
            if (!TryGetClientScreenRect(hwnd, out winLeft, out winTop, out winW, out winH)) return false;

            var pres = ssw.Presentation;
            float sw = pres.PageSetup.SlideWidth;   // 磅
            float sh = pres.PageSetup.SlideHeight;  // 磅
            if (sw <= 0 || sh <= 0) return false;

            foreach (Shape shape in slide.Shapes)
            {
                if (!IsVideoShape(shape)) continue;
                MapShapeToScreen(shape.Left, shape.Top, shape.Width, shape.Height,
                    sw, sh, winLeft, winTop, winW, winH, hwnd, outRegions);
            }
            return true;
        }

        private static bool IsVideoShape(Shape shape)
        {
            try
            {
                if (shape.Type == Office.MsoShapeType.msoWebVideo) return true;

                if (shape.Type == Office.MsoShapeType.msoMedia)
                {
                    try
                    {
                        int mediaType = (int)(object)shape.MediaType;
                        return mediaType == PpMediaTypeMovie || mediaType == PpMediaTypeFlash;
                    }
                    catch { return true; } // MediaType 读取失败时保守放行
                }

                if (shape.Type == Office.MsoShapeType.msoOLEControlObject)
                {
                    try
                    {
                        string progId = shape.OLEFormat?.ProgID ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(progId))
                        {
                            progId = progId.ToUpperInvariant();
                            if (progId.StartsWith("WMPLAYER.", StringComparison.Ordinal) ||
                                progId.StartsWith("VIDEOLAN.", StringComparison.Ordinal) ||
                                progId.StartsWith("SHOCKWAVEFLASH.", StringComparison.Ordinal) ||
                                progId.StartsWith("REALPLAYER.", StringComparison.Ordinal) ||
                                progId.StartsWith("REALMEDIA.", StringComparison.Ordinal))
                                return true;
                        }
                    }
                    catch { }
                    return false;
                }

                if (shape.Type == Office.MsoShapeType.msoEmbeddedOLEObject)
                {
                    try { if ((int)(object)shape.MediaType == PpMediaTypeMovie) return true; }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        // ===== WPS 演示（dynamic） =====

        private bool TryDetectWps(List<VideoRegion> outRegions)
        {
            string[] progIds = { "KWPP.Application", "wpp.Application", "WPS.Presentation.Application" };
            foreach (var pid in progIds)
            {
                try
                {
                    var wps = Marshal.GetActiveObject(pid);
                    if (wps == null) continue;
                    if (DetectFromWpsDynamic(wps, outRegions)) return true;
                }
                catch { }
            }
            return false;
        }

        private bool DetectFromWpsDynamic(dynamic app, List<VideoRegion> outRegions)
        {
            try
            {
                var ssws = app.SlideShowWindows;
                if (ssws == null || (int)ssws.Count <= 0) return false;
                var ssw = ssws[1];
                if (ssw == null) return false;
                var view = ssw.View;
                if (view == null) return false;
                var slide = view.Slide;
                if (slide == null) return false;

                var hwnd = new IntPtr((int)ssw.HWND);
                double winLeft, winTop, winW, winH;
                if (!TryGetClientScreenRect(hwnd, out winLeft, out winTop, out winW, out winH)) return false;

                var pres = ssw.Presentation;
                double sw = (double)pres.PageSetup.SlideWidth;
                double sh = (double)pres.PageSetup.SlideHeight;
                if (sw <= 0 || sh <= 0) return false;

                foreach (var shape in slide.Shapes)
                {
                    if (!IsVideoShapeDynamic(shape)) continue;
                    MapShapeToScreen((double)shape.Left, (double)shape.Top,
                        (double)shape.Width, (double)shape.Height,
                        sw, sh, winLeft, winTop, winW, winH, hwnd, outRegions);
                }
                return true;
            }
            catch { return false; }
        }

        private static bool IsVideoShapeDynamic(dynamic shape)
        {
            try
            {
                int type = (int)shape.Type;
                if (type == MsoWebVideo) return true;
                if (type == MsoMedia)
                {
                    try { int mt = (int)shape.MediaType; return mt == PpMediaTypeMovie || mt == PpMediaTypeFlash; }
                    catch { return true; }
                }
                if (type == MsoOLEControlObject)
                {
                    try
                    {
                        string progId = (string)shape.OLEFormat.ProgID ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(progId))
                        {
                            progId = progId.ToUpperInvariant();
                            if (progId.StartsWith("WMPLAYER.", StringComparison.Ordinal) ||
                                progId.StartsWith("VIDEOLAN.", StringComparison.Ordinal) ||
                                progId.StartsWith("SHOCKWAVEFLASH.", StringComparison.Ordinal) ||
                                progId.StartsWith("REALPLAYER.", StringComparison.Ordinal) ||
                                progId.StartsWith("REALMEDIA.", StringComparison.Ordinal))
                                return true;
                        }
                    }
                    catch { }
                    return false;
                }
                if (type == MsoEmbeddedOLEObject)
                {
                    try { if ((int)shape.MediaType == PpMediaTypeMovie) return true; }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        // ===== 坐标换算 =====

        /// <summary>
        /// 获取窗口"客户区"在屏幕上的物理像素矩形（左上角 + 宽高）。
        /// 用客户区而非窗口矩形做比例映射，可排除标题栏/边框，使视频区域定位更贴近实际显示。
        /// </summary>
        private static bool TryGetClientScreenRect(IntPtr hwnd, out double left, out double top, out double width, out double height)
        {
            left = top = width = height = 0;
            if (hwnd == IntPtr.Zero) return false;
            RECT client;
            if (!GetClientRect(hwnd, out client)) return false;
            POINT origin;
            origin.X = 0;
            origin.Y = 0;
            if (!ClientToScreen(hwnd, ref origin)) return false;
            left = origin.X;
            top = origin.Y;
            width = client.Right;
            height = client.Bottom;
            return width > 0 && height > 0;
        }

        private static void MapShapeToScreen(double leftPt, double topPt, double widthPt, double heightPt,
            double slideW, double slideH, double winLeft, double winTop, double winW, double winH,
            IntPtr hwnd, List<VideoRegion> outRegions)
        {
            // 形状坐标(磅) → 相对幻灯片的比例 → 屏幕像素矩形。
            // 采用比例映射，天然与 DPI 无关（GetWindowRect / GetCursorPos 同处逻辑像素空间）。
            double fx = leftPt / slideW;
            double fy = topPt / slideH;
            double fw = widthPt / slideW;
            double fh = heightPt / slideH;
            outRegions.Add(new VideoRegion
            {
                Left = winLeft + fx * winW,
                Top = winTop + fy * winH,
                Width = fw * winW,
                Height = fh * winH,
                Hwnd = hwnd
            });
        }

        // ===== P/Invoke =====

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
