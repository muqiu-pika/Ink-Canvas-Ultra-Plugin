using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using Ink_Canvas.Plugins;

namespace Ink_Canvas.Plugins.PPTVideoEnhance
{
    /// <summary>
    /// 防误翻页：低层鼠标钩子（WH_MOUSE_LL）。
    /// 当插件处于"鼠标/穿透模式"时，拦截发往 PowerPoint / WPS 放映窗口的"触摸"手势——
    /// 若触点落在视频区域外，则吞掉整段手势（down / move / up），阻止 PPT/WPS 把滑动或点击当成翻页；
    /// 若触点在视频区域内，则放行（照常操作视频控件）。真实鼠标事件一律放行。
    /// 钩子安装在我们自己进程内的专用 STA 线程（带消息循环），不需要注入 PPT/WPS 进程。
    /// 关键风险：若 PPT/WPS 放映窗口是 touch-aware 且系统抑制了"触摸合成的鼠标事件"，
    /// 则 WH_MOUSE_LL 可能收不到触摸事件，此时该方案失效（需退化为其他路线）。
    /// </summary>
    internal sealed class SwipeSuppressor : IDisposable
    {
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_MOUSEMOVE = 0x0200;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const ulong TOUCH_SIGNATURE = 0xFF515700; // MI_WP_SIGNATURE / MOUSEEVENTF_FROMTOUCH

        private readonly IPluginHost _host;
        private readonly Func<IReadOnlyList<VideoRegion>> _regionProvider;
        private readonly double _marginPx;

        private Thread _thread;
        private Dispatcher _dispatcher;
        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelMouseProc _hookProc;

        private volatile bool _active;
        private volatile bool _suppressing;
        private List<VideoRegion> _regions = new List<VideoRegion>();

        private IntPtr _lastHwnd;
        private bool _lastIsTarget;
        private bool _loggedFirst;

        public SwipeSuppressor(IPluginHost host, Func<IReadOnlyList<VideoRegion>> regionProvider, double marginPx)
        {
            _host = host;
            _regionProvider = regionProvider;
            _marginPx = marginPx;
        }

        /// <summary>是否启用拦截。由插件在"进入/退出鼠标模式"时同步设置（通常 = 当前为穿透模式）。</summary>
        public bool Active
        {
            get => _active;
            set => _active = value;
        }

        public void UpdateRegions(IReadOnlyList<VideoRegion> regions)
        {
            lock (_regions)
            {
                _regions = new List<VideoRegion>(regions);
            }
        }

        public void Start()
        {
            if (_thread != null) return;
            _thread = new Thread(ThreadProc) { IsBackground = true, Name = "PPTVSwipeSuppress" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public void Stop()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            try { _dispatcher?.BeginInvokeShutdown(DispatcherPriority.Send); } catch { }
            try { _thread?.Join(500); } catch { }
            _thread = null;
        }

        private void ThreadProc()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            _hookProc = HookProc;
            _hookId = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, IntPtr.Zero, 0);
            if (_hookId == IntPtr.Zero)
            {
                _host?.Log("防误翻页钩子安装失败（WH_MOUSE_LL）", PluginLogLevel.Error);
                return;
            }
            _host?.Log("防误翻页钩子已安装", PluginLogLevel.Event);
            Dispatcher.Run();
        }

        private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _active)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP || msg == WM_MOUSEMOVE ||
                    msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP)
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    // 仅处理触摸来源（dwExtraInfo 高 24 位为触摸签名），真实鼠标放行
                    if ((info.dwExtraInfo.ToUInt64() & 0xFFFFFF00UL) == TOUCH_SIGNATURE)
                    {
                        IntPtr hwnd = WindowFromPoint(info.pt);
                        if (IsTargetProcess(hwnd))
                        {
                            bool inside = IsInsideAnyRegion(info.pt.x, info.pt.y);
                            if (!inside)
                            {
                                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN)
                                    _suppressing = true;
                                if (_suppressing)
                                {
                                    if (!_loggedFirst)
                                    {
                                        _loggedFirst = true;
                                        _host?.Log("防误翻页：已拦截一次视频区域外的触摸手势", PluginLogLevel.Event);
                                    }
                                    return new IntPtr(1); // 吞掉该事件，阻止 PPT/WPS 翻页
                                }
                            }
                            else
                            {
                                if (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN)
                                    _suppressing = false;
                            }
                            if (msg == WM_LBUTTONUP || msg == WM_RBUTTONUP)
                                _suppressing = false;
                        }
                        else
                        {
                            _suppressing = false;
                        }
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private bool IsInsideAnyRegion(int x, int y)
        {
            List<VideoRegion> regs;
            lock (_regions) regs = _regions;
            double m = _marginPx;
            foreach (var r in regs)
            {
                if (x >= r.Left - m && x <= r.Left + r.Width + m &&
                    y >= r.Top - m && y <= r.Top + r.Height + m)
                    return true;
            }
            return false;
        }

        private bool IsTargetProcess(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (hwnd == _lastHwnd) return _lastIsTarget;
            bool target = false;
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != 0)
                {
                    IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            var sb = new StringBuilder(1024);
                            uint sz = (uint)sb.Capacity;
                            if (QueryFullProcessImageName(h, 0, sb, ref sz))
                            {
                                string name = System.IO.Path.GetFileName(sb.ToString()).ToLowerInvariant();
                                target = name.Contains("powerpnt") || name.Contains("wpp") || name.Contains("wpsoffice");
                            }
                        }
                        finally { CloseHandle(h); }
                    }
                }
            }
            catch { }
            _lastHwnd = hwnd;
            _lastIsTarget = target;
            return target;
        }

        public void Dispose() => Stop();

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(POINT pt);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref uint lpdwSize);
    }
}
