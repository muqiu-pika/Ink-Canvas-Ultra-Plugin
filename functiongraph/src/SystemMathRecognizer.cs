using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Threading;
using System.Globalization;
using System.Threading;
// 这两个类型在 System.Runtime.InteropServices 与 .ComTypes 中同名，显式消歧
using INVOKEKIND = System.Runtime.InteropServices.ComTypes.INVOKEKIND;
using DISPPARAMS = System.Runtime.InteropServices.ComTypes.DISPPARAMS;

namespace Ink_Canvas.Plugins.FunctionGraph
{
    /// <summary>
    /// Windows 原生 Math Input Control（即 Word「墨迹公式」使用的同一个识别引擎）调用层。
    ///
    /// 官方编程模型（learn.microsoft.com/windows/win32/tablet/programming-the-math-input-control）：
    ///   CoCreateInstance(CLSID_MathInputControl) → SetOwnerWindow / SetPosition / EnableExtendedButtons
    ///   → Show → 通过 _IMathInputControlEvents::Insert(BSTR mathXml) 接收 MathML → Hide。
    ///
    /// 实现要点：
    ///  - **全部后期绑定**：不引用 micaut.dll / Tlbimp interop，避免 32/64 位 LONG_PTR 差异和额外 DLL；
    ///  - 微软未公开 _IMathInputControlEvents 的 IID 与 DISPID_MICInsert，因此不用硬编码：
    ///    用 IConnectionPointContainer.EnumConnectionPoints 枚举出唯一连接点，
    ///    事件接收器实现 IDispatch + ICustomQueryInterface，按「带一个 BSTR 参数」识别 Insert 事件；
    ///  - 控件是 Win32 窗口且事件回调在 STA 线程派发，**绝不能阻塞 UI 线程等结果**（会死锁），
    ///    因此采用回调式异步会话，窗口关闭/超时/取消都能收尾；
    ///  - 任何一步失败都静默返回 null，由调用方回落到内置 Chamfer + 语法约束识别，
    ///    保证未安装 Math Recognizer 的 Windows 版本功能不残缺。
    /// </summary>
    internal sealed class SystemMathRecognizer : IDisposable
    {
        private static readonly Guid MathInputControlClsid =
            new Guid("C561816C-14D8-4090-830C-98D994B21C7B");

        private static readonly Guid IidIDispatch =
            new Guid("00020400-0000-0000-C000-000000000046");

        private static readonly Guid IidIUnknown =
            new Guid("00000000-0000-0000-C000-000000000046");

        private object _control;
        private IConnectionPoint _connectionPoint;
        private Guid _eventIid;
        private EventSink _sink;
        private int _cookie;
        private bool _finished;
        private DispatcherTimer _timeout;
        private Action<string> _onResult;
        private Action _onCancelled;

        private SystemMathRecognizer() { }

        /// <summary>启动一次原生识别会话；不可用或失败时返回 null，并在 failureReason 给出具体原因。</summary>
        public static SystemMathRecognizer TryStart(IntPtr ownerHandle,
                                                    Action<string> onResult,
                                                    Action onCancelled,
                                                    out string failureReason)
        {
            var session = new SystemMathRecognizer
            {
                _onResult = onResult,
                _onCancelled = onCancelled
            };
            // 面板 UI 资源按线程 UI 语言加载：临时切到 zh-CN，让按钮/提示为中文，
            // Show 完成后恢复原线程文化（失败路径在 finally 中统一恢复）
            var previousUICulture = Thread.CurrentThread.CurrentUICulture;
            bool cultureChanged = false;
            try
            {
                Thread.CurrentThread.CurrentUICulture = new CultureInfo("zh-CN");
                cultureChanged = true;

                if (SystemMathCapability.Status != SystemMathStatus.Available)
                {
                    failureReason = "未检测到系统数学识别组件（micaut.dll / mraut.dll，"
                        + SystemMathCapability.Summary + "）";
                    session.Finish(false);
                    return null;
                }

                Type comType = Type.GetTypeFromCLSID(MathInputControlClsid, false);
                if (comType == null)
                {
                    failureReason = "MathInputControl COM 类（CLSID C561816C-…）未注册";
                    session.Finish(false);
                    return null;
                }

                try
                {
                    session._control = Activator.CreateInstance(comType);
                }
                catch (Exception ex)
                {
                    failureReason = "创建 Math Input Control 失败："
                        + (ex.InnerException != null ? ex.InnerException.Message : ex.Message);
                    session.Finish(false);
                    return null;
                }
                if (session._control == null)
                {
                    failureReason = "创建 Math Input Control 返回空对象";
                    session.Finish(false);
                    return null;
                }

                // SetOwnerWindow 的 LONG_PTR 随进程位数变化：64 位传 long，32 位传 int
                if (ownerHandle != IntPtr.Zero)
                {
                    object handle = IntPtr.Size == 8
                        ? (object)ownerHandle.ToInt64()
                        : (object)ownerHandle.ToInt32();
                    session.Invoke("SetOwnerWindow", handle);
                }

                Rect work = SystemParameters.WorkArea;
                int width = Math.Min(760, Math.Max(480, (int)work.Width - 120));
                int height = Math.Min(460, Math.Max(320, (int)work.Height - 160));
                int left = (int)(work.Left + (work.Width - width) / 2);
                int top = (int)(work.Top + (work.Height - height) / 2);
                session.Invoke("SetPosition", left, top, left + width, top + height);

                try { session.Invoke("SetCaptionText", "函数绘图 · Windows 墨迹公式"); } catch { }
                try { session.Invoke("EnableExtendedButtons", true); } catch { }
                try { session.Invoke("EnableAutoGrow", true); } catch { }

                if (!session.AdviseEvents())
                {
                    failureReason = "连接系统面板事件失败（连接点枚举或 Advise 未成功）";
                    session.Finish(false);
                    return null;
                }

                session.Invoke("Show");

                // 面板窗口已按中文资源创建完毕，恢复线程文化
                Thread.CurrentThread.CurrentUICulture = previousUICulture;
                cultureChanged = false;

                // 兜底：用户既不点插入也不点取消时，避免 COM 会话与窗口一直悬挂
                session._timeout = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
                session._timeout.Tick += (s, e) => session.Finish(true);
                session._timeout.Start();
                failureReason = null;
                return session;
            }
            catch (Exception ex)
            {
                var reason = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                failureReason = "调用系统面板时异常：" + reason;
                session.Finish(false);
                return null;
            }
            finally
            {
                if (cultureChanged)
                    Thread.CurrentThread.CurrentUICulture = previousUICulture;
            }
        }

        private object Invoke(string name, params object[] arguments)
        {
            return _control.GetType().InvokeMember(
                name,
                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                null, _control, arguments);
        }

        /// <summary>枚举控件唯一的出接口连接点并挂上 IDispatch 事件接收器。</summary>
        private bool AdviseEvents()
        {
            var container = _control as IConnectionPointContainer;
            if (container == null) return false;

            IEnumConnectionPoints enumerator;
            container.EnumConnectionPoints(out enumerator);
            if (enumerator == null) return false;

            var points = new IConnectionPoint[1];
            IntPtr fetched = Marshal.AllocCoTaskMem(4);
            try
            {
                Marshal.WriteInt32(fetched, 0);
                enumerator.Next(1, points, fetched);
            }
            finally
            {
                Marshal.FreeCoTaskMem(fetched);
                try { Marshal.ReleaseComObject(enumerator); } catch { }
            }

            _connectionPoint = points[0];
            if (_connectionPoint == null) return false;

            // 微软文档只说 _IMathInputControlEvents 继承 IUnknown，未公开其 IID。
            // 连接点自己知道，直接从它取，避免硬编码 GUID 且跨 Windows 版本稳定。
            try { _connectionPoint.GetConnectionInterface(out _eventIid); }
            catch { _eventIid = Guid.Empty; }

            _sink = new EventSink(this, _eventIid);
            _connectionPoint.Advise(_sink, out _cookie);
            return _cookie != 0;
        }

        /// <summary>Insert 事件：拿到 MathML，隐藏控件，回调结果。</summary>
        private void OnInsert(string mathML)
        {
            if (_finished) return;
            _finished = true;
            StopTimer();
            try { Invoke("Hide"); } catch { }

            Action<string> callback = _onResult;
            _onResult = null;
            _onCancelled = null;
            if (callback != null)
            {
                try { callback(mathML); } catch { }
            }
            // 延迟释放：此刻仍在 COM 事件调用栈里，立即 Unadvise/Release 会重入
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(ReleaseResources));
        }

        /// <summary>Close / Clear 事件或超时：按取消处理。</summary>
        private void OnDismissed()
        {
            if (_finished) return;
            _finished = true;
            StopTimer();
            try { Invoke("Hide"); } catch { }

            Action callback = _onCancelled;
            _onResult = null;
            _onCancelled = null;
            if (callback != null)
            {
                try { callback(); } catch { }
            }
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(ReleaseResources));
        }

        private void StopTimer()
        {
            try
            {
                if (_timeout != null) { _timeout.Stop(); _timeout = null; }
            }
            catch { }
        }

        private void ReleaseResources()
        {
            try
            {
                if (_connectionPoint != null && _cookie != 0)
                    _connectionPoint.Unadvise(_cookie);
            }
            catch { }
            _cookie = 0;
            try
            {
                if (_connectionPoint != null) Marshal.ReleaseComObject(_connectionPoint);
            }
            catch { }
            _connectionPoint = null;
            _sink = null;
            try
            {
                if (_control != null) Marshal.ReleaseComObject(_control);
            }
            catch { }
            _control = null;
        }

        /// <summary>窗口关闭或用户主动取消时收尾（不触发回调）。</summary>
        private void Finish(bool notifyCancel)
        {
            if (_finished)
            {
                // 同样延迟释放：可能正处在 COM 事件回调栈里
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(ReleaseResources));
                return;
            }
            _finished = true;
            StopTimer();
            try { if (_control != null) Invoke("Hide"); } catch { }
            Action callback = _onCancelled;
            _onResult = null;
            _onCancelled = null;
            if (notifyCancel && callback != null)
            {
                try { callback(); } catch { }
            }
            Dispatcher.CurrentDispatcher.BeginInvoke(new Action(ReleaseResources));
        }

        public void Dispose()
        {
            Finish(false);
        }

        /// <summary>
        /// 接收 dispinterface 事件。微软未公开该接口的 IID/DISPID，
        /// 因此对任何 QueryInterface 都返回 IDispatch 指针，由 COM 直接调 IDispatch::Invoke；
        /// Insert 带 1 个 BSTR 参数（MathML），Close/Clear 无参数，据此区分。
        /// </summary>
        [ComVisible(true)]
        [ClassInterface(ClassInterfaceType.None)]
        private sealed class EventSink : IDispatch, ICustomQueryInterface
        {
            private readonly SystemMathRecognizer _owner;
            private readonly Guid _eventIid;
            private IntPtr _dispatchPointer;

            public EventSink(SystemMathRecognizer owner, Guid eventIid)
            {
                _owner = owner;
                _eventIid = eventIid;
            }

            public CustomQueryInterfaceResult GetInterface(ref Guid iid, out IntPtr ppv)
            {
                // 遵守 IUnknown::QueryInterface 规则：只暴露真正支持的接口，
                // 其余必须返回 E_NOINTERFACE（Failed），不能对所有 IID 都返回同一个指针。
                // 事件 dispinterface 的 IID 由 GetConnectionInterface 运行时取得，
                // 它本质上是 IDispatch 派生接口，因此返回 IDispatch 指针即可。
                bool supported = iid == IidIUnknown || iid == IidIDispatch
                                 || (_eventIid != Guid.Empty && iid == _eventIid);
                if (!supported)
                {
                    ppv = IntPtr.Zero;
                    return CustomQueryInterfaceResult.Failed;
                }
                // IUnknown 身份规则：对同一对象的 QueryInterface 必须返回稳定指针，
                // 因此缓存 IDispatch 接口指针，不能每次 QI 都新建一个。
                if (_dispatchPointer == IntPtr.Zero)
                    _dispatchPointer = Marshal.GetComInterfaceForObject(
                        this, typeof(IDispatch), CustomQueryInterfaceMode.Ignore);
                ppv = _dispatchPointer;
                return CustomQueryInterfaceResult.Handled;
            }

            public int GetTypeInfoCount(out int pctinfo) { pctinfo = 0; return 0; }

            public int GetTypeInfo(int iTInfo, int lcid, out IntPtr ppTInfo)
            {
                ppTInfo = IntPtr.Zero;
                return unchecked((int)0x80004001);   // E_NOTIMPL
            }

            public int GetIDsOfNames(ref Guid riid, string[] rgszNames, int cNames,
                                     int lcid, int[] rgDispId)
            {
                return unchecked((int)0x80004001);   // E_NOTIMPL
            }

            public int Invoke(int dispIdMember, ref Guid riid, int lcid, INVOKEKIND wFlags,
                              ref DISPPARAMS pDispParams, IntPtr pvarResult,
                              IntPtr pExcepInfo, IntPtr puArgErr)
            {
                try
                {
                    if (pDispParams.cArgs > 0 && pDispParams.rgvarg != IntPtr.Zero)
                    {
                        object[] args = Marshal.GetObjectsForNativeVariants(
                            pDispParams.rgvarg, pDispParams.cArgs);
                        // Insert 只有 1 个 BSTR；COM 参数逆序排列，args[0] 即 MathML
                        string mathML = args.Length > 0 ? args[0] as string : null;
                        if (!string.IsNullOrWhiteSpace(mathML))
                        {
                            _owner.OnInsert(mathML);
                            return 0;
                        }
                        // Paint 事件带 hdc/坐标/MICUIELEMENT/state 等多个非字符串参数，
                        // 属于自定义绘制（MICUIELEMENT 枚举），本插件不处理，绝不能当作取消。
                        return 0;
                    }
                    // Close / Clear：0 参数，按会话结束处理
                    _owner.OnDismissed();
                }
                catch { }
                return 0;   // S_OK
            }
        }

        /// <summary>IDispatch 的托管声明（IUnknown vtable 顺序）。</summary>
        [ComImport]
        [Guid("00020400-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDispatch
        {
            [PreserveSig] int GetTypeInfoCount(out int pctinfo);
            [PreserveSig] int GetTypeInfo(int iTInfo, int lcid, out IntPtr ppTInfo);
            [PreserveSig] int GetIDsOfNames(ref Guid riid,
                [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 2)] string[] rgszNames,
                int cNames, int lcid, [Out] int[] rgDispId);
            [PreserveSig] int Invoke(int dispIdMember, ref Guid riid, int lcid,
                INVOKEKIND wFlags, ref DISPPARAMS pDispParams,
                IntPtr pvarResult, IntPtr pExcepInfo, IntPtr puArgErr);
        }
    }
}
