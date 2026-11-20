using Ink_Canvas.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ink_Canvas.Plugins.PptControlCustomizer
{
    /// <summary>
    /// PPT 控件自定义插件：调整四个翻页控件（下方左/右、侧面左/右）与浮动工具栏的
    /// 缩放、透明度、位移。全部通过公开 WPF API（FindName / 属性写入）实现，不改动宿主。
    ///
    /// 实现要点（均为刻意为之，勿"优化"回去）：
    /// - 位移走 Margin（宿主显示动画 ShowWithScaleFromBottom/Left/Right 会整体覆盖 RenderTransform）；
    /// - 缩放走 LayoutTransform（与 RenderTransform 缩放动画无冲突）；
    /// - 透明度写在内层 Border 上（Viewbox 自身被显示动画体系占用）；
    /// - 每个控件独立 try/catch + 空值防护：任一控件失败不影响其余（避免"初始位置的内容还在"）；
    /// - 挂 Visibility / Opacity 属性监听自愈：宿主重新显示面板或覆写浮动栏透明度时立即按配置重应用。
    /// </summary>
    public class PptControlCustomizerPlugin : IPlugin
    {
        private const string PluginId = "ink-canvas.ppt-control-customizer";
        private const string PluginDllName = "PptControlCustomizerPlugin.dll";

        // 面板索引：0=下方左 1=下方右 2=侧面左 3=侧面右
        private const int PanelCount = 4;

        private IPluginHost _host;
        private string _pluginDirectory;

        // 配置（% 与 px），加载时从 JSON 读入；位移已被拖拽编辑取代（旧 Offset 键不再读取）
        private readonly double[] _scale = { 100, 100, 100, 100 };
        private readonly double[] _opacity = { 40, 40, 40, 40 };
        private double _barOpacity = 100;

        // 解析到的宿主控件
        private Viewbox[] _navBoxes;
        private Border[] _navBorders;
        private UIElement _viewboxFloatingBar;

        // 首次解析时捕获的原始值（禁用/卸载时还原）
        private bool _capturedOriginals;
        private readonly Thickness[] _origMargins = new Thickness[PanelCount];
        private readonly Transform[] _origLayout = new Transform[PanelCount];
        private readonly double[] _origBorderOpacity = { double.NaN, double.NaN, double.NaN, double.NaN };
        private double _origBarOpacity = double.NaN;

        // 复用的缩放变换（避免每次应用都新建）
        private readonly ScaleTransform[] _scaleTransforms = new ScaleTransform[PanelCount];

        // 每面板保存的绝对位置（拖拽编辑保存；null = 未编辑过，定位由宿主管理）与最近已知尺寸
        private readonly double?[] _savedX = new double?[PanelCount];
        private readonly double?[] _savedY = new double?[PanelCount];
        private readonly double[] _lastSize = new double[PanelCount];

        // 属性监听
        private bool _hooked;
        private readonly DependencyPropertyDescriptor[] _visibilityDescriptors = new DependencyPropertyDescriptor[PanelCount];
        private readonly EventHandler[] _visibilityHandlers = new EventHandler[PanelCount];
        private readonly DependencyPropertyDescriptor[] _marginDescriptors = new DependencyPropertyDescriptor[PanelCount];
        private readonly EventHandler[] _marginHandlers = new EventHandler[PanelCount];
        private DependencyPropertyDescriptor _barOpacityDescriptor;
        private EventHandler _barOpacityHandler;
        private bool _suppressBarOpacityHook;

        /// <summary>宿主最近写入的浮动栏场景不透明度（1.0 / 0.95 / 0.8 / 0.75），用户百分比叠加其上。</summary>
        private double _barBaseOpacity = 1.0;

        // 设置面板滑条引用（供「恢复默认」复位）
        private sealed class SliderRef
        {
            public Slider Slider;
            public double Def;
        }
        private readonly List<SliderRef> _panelSliders = new List<SliderRef>();

        public PluginManifest Manifest { get; } = new PluginManifest
        {
            Id = PluginId,
            Name = "PPT 控件自定义",
            Version = "1.1.0",
            Author = "muqiu",
            Description = "自定义 PPT 放映时的四个翻页控件（下方左/右、侧面左/右）与浮动工具栏的缩放、透明度和位置。在插件工坊「设置」或主程序设置页「进入编辑模式」中直接拖拽摆放翻页控件；放映中拖动滑条实时生效；越界自动收拢，禁用插件后恢复默认外观。",
            EntryAssembly = PluginDllName,
            EntryClass = "Ink_Canvas.Plugins.PptControlCustomizer.PptControlCustomizerPlugin",
            MinHostVersion = "26.10.6"
        };

        public void Initialize(IPluginHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _pluginDirectory = FindPluginDirectory();

            LoadConfig();

            try { _host.RegisterSettingsPanel(BuildSettingsPanelBody); }
            catch (Exception ex) { Log("注册设置面板失败 " + ex.Message, PluginLogLevel.Warning); }

            // 主程序设置页「进入编辑模式」按钮通过该路由进入拖拽编辑（插件负责全部实现）
            try { _host.RegisterRouteHandler("ppt-nav-edit-mode", _ => { EnterEditMode(); return true; }); }
            catch (Exception ex) { Log("注册编辑模式路由失败 " + ex.Message, PluginLogLevel.Warning); }

            // 宿主窗口显示后再解析/应用（Initialize 发生在 MainWindow 构造阶段）
            try
            {
                var mw = host.MainWindow;
                Action start = AttachToMainWindow;
                if (mw != null && mw.IsLoaded) Application.Current.Dispatcher.BeginInvoke(start);
                else if (mw != null) mw.Loaded += OnMainWindowLoaded;
                else Application.Current.Dispatcher.BeginInvoke(start);
            }
            catch (Exception ex) { Log("挂接主窗口失败 " + ex.Message, PluginLogLevel.Warning); }
        }

        public void Shutdown()
        {
            try { if (_editMode) ExitEditMode(false); } catch { }
            try { UnhookEvents(); } catch { }
            try { RestoreOriginals(); } catch { }
            try { _host?.UnregisterRouteHandler("ppt-nav-edit-mode"); } catch { }
            try { _host?.UnregisterSettingsPanel(); } catch { }
            _panelSliders.Clear();
            _host = null;
        }

        // ---------- 挂接与解析 ----------

        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            var mw = _host?.MainWindow;
            if (mw != null) mw.Loaded -= OnMainWindowLoaded;
            Application.Current.Dispatcher.BeginInvoke((Action)AttachToMainWindow);
        }

        private void AttachToMainWindow()
        {
            try
            {
                if (!ResolveControls())
                {
                    Log("未能解析宿主控件（MWPPTHost / MWFloatBarHost），功能不可用", PluginLogLevel.Warning);
                    return;
                }
                CaptureOriginalsIfNeeded();
                HookEvents();
                ApplyAll();
            }
            catch (Exception ex) { Log("挂接主窗口异常 " + ex.Message, PluginLogLevel.Warning); }
        }

        /// <summary>通过公开 FindName API 解析宿主控件；任何一步失败都返回 false（不抛异常）。</summary>
        private bool ResolveControls()
        {
            try
            {
                var mw = _host?.MainWindow as FrameworkElement;
                if (mw == null) return false;

                var pptHost = mw.FindName("MWPPTHost") as FrameworkElement;
                if (pptHost == null) return false;

                string[] names =
                {
                    "PPTNavigationBottomLeft", "PPTNavigationBottomRight",
                    "PPTNavigationSidesLeft", "PPTNavigationSidesRight"
                };
                var boxes = new Viewbox[PanelCount];
                var borders = new Border[PanelCount];
                for (int i = 0; i < PanelCount; i++)
                {
                    boxes[i] = pptHost.FindName(names[i]) as Viewbox;
                    borders[i] = boxes[i]?.Child as Border;
                }

                var floatHost = mw.FindName("MWFloatBarHost") as FrameworkElement;
                var bar = floatHost?.FindName("ViewboxFloatingBar") as UIElement;

                if (boxes.Any(b => b == null) || bar == null) return false;

                _navBoxes = boxes;
                _navBorders = borders;
                _viewboxFloatingBar = bar;
                return true;
            }
            catch (Exception ex) { Log("解析宿主控件失败 " + ex.Message, PluginLogLevel.Warning); return false; }
        }

        private bool EnsureResolved()
        {
            if (_navBoxes != null && _navBoxes.All(b => b != null) && _viewboxFloatingBar != null) return true;
            return ResolveControls();
        }

        private void CaptureOriginalsIfNeeded()
        {
            if (_capturedOriginals || _navBoxes == null) return;
            for (int i = 0; i < PanelCount; i++)
            {
                var box = _navBoxes[i];
                if (box == null) continue;
                _origMargins[i] = box.Margin;
                _origLayout[i] = box.LayoutTransform;
                if (_navBorders[i] != null) _origBorderOpacity[i] = _navBorders[i].Opacity;
            }
            var bar = _viewboxFloatingBar as UIElement;
            if (bar != null)
            {
                _origBarOpacity = bar.Opacity;
                _barBaseOpacity = bar.Opacity;
            }
            _capturedOriginals = true;
        }

        // ---------- 属性监听（自愈） ----------

        private void HookEvents()
        {
            if (_hooked || _navBoxes == null) return;
            try
            {
                for (int i = 0; i < PanelCount; i++)
                {
                    var box = _navBoxes[i];
                    if (box == null) continue;
                    int index = i; // 闭包捕获
                    EventHandler handler = (s, e) => OnNavVisibilityChanged(index);
                    _visibilityDescriptors[i] = DependencyPropertyDescriptor.FromProperty(UIElement.VisibilityProperty, typeof(Viewbox));
                    _visibilityDescriptors[i].AddValueChanged(box, handler);
                    _visibilityHandlers[i] = handler;

                    // 宿主覆写 Margin（如放映中切换「位置」下拉）时立即恢复插件位移；
                    // 钩子按 _appliedOffset 比对，自身写入与越界回拉均满足期望值，不会循环触发
                    EventHandler marginHandler = (s, e) => OnNavMarginChanged(index);
                    _marginDescriptors[i] = DependencyPropertyDescriptor.FromProperty(FrameworkElement.MarginProperty, typeof(Viewbox));
                    _marginDescriptors[i].AddValueChanged(box, marginHandler);
                    _marginHandlers[i] = marginHandler;
                }

                var bar = _viewboxFloatingBar as UIElement;
                if (bar != null)
                {
                    _barOpacityDescriptor = DependencyPropertyDescriptor.FromProperty(UIElement.OpacityProperty, typeof(Viewbox));
                    _barOpacityHandler = OnBarOpacityChanged;
                    _barOpacityDescriptor.AddValueChanged(bar, _barOpacityHandler);
                }
                _hooked = true;
            }
            catch (Exception ex) { Log("挂接属性监听失败 " + ex.Message, PluginLogLevel.Warning); }
        }

        private void UnhookEvents()
        {
            if (!_hooked) return;
            try
            {
                for (int i = 0; i < PanelCount; i++)
                {
                    if (_visibilityDescriptors[i] != null && _navBoxes != null && _navBoxes[i] != null)
                        _visibilityDescriptors[i].RemoveValueChanged(_navBoxes[i], _visibilityHandlers[i]);
                    _visibilityDescriptors[i] = null;
                    _visibilityHandlers[i] = null;
                    if (_marginDescriptors[i] != null && _navBoxes != null && _navBoxes[i] != null)
                        _marginDescriptors[i].RemoveValueChanged(_navBoxes[i], _marginHandlers[i]);
                    _marginDescriptors[i] = null;
                    _marginHandlers[i] = null;
                }
                var bar = _viewboxFloatingBar as UIElement;
                if (_barOpacityDescriptor != null && bar != null && _barOpacityHandler != null)
                    _barOpacityDescriptor.RemoveValueChanged(bar, _barOpacityHandler);
                _barOpacityDescriptor = null;
                _barOpacityHandler = null;
            }
            catch { }
            _hooked = false;
        }

        /// <summary>面板从隐藏变可见（放映开始、折叠展开、模式切换等）时重应用一次，消除任何宿主侧的状态覆写。</summary>
        private void OnNavVisibilityChanged(int index)
        {
            if (_editMode) return; // 拖拽编辑中不干预
            try
            {
                var box = _navBoxes != null && index < PanelCount ? _navBoxes[index] : null;
                if (box == null || box.Visibility != Visibility.Visible) return;
                Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
                {
                    ApplyPanel(index);
                    ScheduleBoundsCheck(index);
                }));
            }
            catch { }
        }

        /// <summary>
        /// Margin 被改写时恢复插件定位：仅对已保存绝对位置（拖拽编辑过）的面板生效，
        /// 宿主覆写（如放映开始的摆放）写入的 Margin 与期望值不符时重应用；
        /// 插件自身写入与越界收拢写入的值均等于期望值，因此不会循环触发。
        /// 未编辑过的面板定位由宿主管理，插件不干预。
        /// </summary>
        private void OnNavMarginChanged(int index)
        {
            if (_editMode) return; // 拖拽编辑中不干预
            try
            {
                var box = _navBoxes != null && index < PanelCount ? _navBoxes[index] : null;
                if (box == null || box.Visibility != Visibility.Visible) return;
                if (!_savedX[index].HasValue || !_savedY[index].HasValue) return;
                var expected = new Thickness(_savedX[index].Value, _savedY[index].Value, 0, 0);
                if (box.Margin != expected) ApplyPanel(index);
            }
            catch { }
        }

        /// <summary>宿主覆写浮动栏不透明度（放映开始/结束写入 0.8/0.75/0.95/1）时，立即叠加用户百分比。</summary>
        private void OnBarOpacityChanged(object sender, EventArgs e)
        {
            if (_suppressBarOpacityHook) return;
            try
            {
                var bar = sender as UIElement;
                if (bar == null) return;
                _barBaseOpacity = bar.Opacity;
                ApplyFloatingBarOpacity();
            }
            catch { }
        }

        // ---------- 应用 ----------

        private void ApplyAll()
        {
            if (!EnsureResolved()) return;
            for (int i = 0; i < PanelCount; i++) ApplyPanel(i);
            ApplyFloatingBarOpacity();
        }

        /// <summary>应用单个翻页控件的缩放/透明度/定位。独立 try/catch：单个失败不影响其余控件。</summary>
        private void ApplyPanel(int index)
        {
            try
            {
                var box = _navBoxes != null && index < PanelCount ? _navBoxes[index] : null;
                if (box == null) return;
                var border = index < PanelCount && _navBorders != null ? _navBorders[index] : null;
                if (border == null) border = box.Child as Border;

                double scale = Clamp(_scale[index], 50, 200) / 100.0;
                double opacity = Clamp(_opacity[index], 10, 100) / 100.0;

                // 缩放：LayoutTransform（复用变换实例，值不变不写，避免触发布局抖动）
                var st = _scaleTransforms[index];
                if (st == null)
                {
                    st = new ScaleTransform(scale, scale);
                    _scaleTransforms[index] = st;
                }
                else if (!CloseTo(st.ScaleX, scale) || !CloseTo(st.ScaleY, scale))
                {
                    st.ScaleX = scale;
                    st.ScaleY = scale;
                }
                if (box.LayoutTransform != st) box.LayoutTransform = st;

                // 透明度：内层 Border
                if (border != null && !CloseTo(border.Opacity, opacity)) border.Opacity = opacity;

                // 定位：已保存绝对位置（拖拽编辑过）→ Left/Top + Margin(x,y,0,0)；
                // 未编辑过 → 定位由宿主管理（对齐/Margin 不动），插件只管外观
                if (_savedX[index].HasValue && _savedY[index].HasValue)
                {
                    double size = index < 2 ? box.ActualWidth : box.ActualHeight;
                    if (size > 0) _lastSize[index] = size;
                    double x = ClampToRoom(index, _savedX[index].Value, true);
                    double y = ClampToRoom(index, _savedY[index].Value, false);
                    if (box.HorizontalAlignment != HorizontalAlignment.Left) box.HorizontalAlignment = HorizontalAlignment.Left;
                    if (box.VerticalAlignment != VerticalAlignment.Top) box.VerticalAlignment = VerticalAlignment.Top;
                    var margin = new Thickness(x, y, 0, 0);
                    if (box.Margin != margin) box.Margin = margin;
                }
            }
            catch (Exception ex) { Log("应用控件 #" + index + " 失败 " + ex.Message, PluginLogLevel.Warning); }
        }

        /// <summary>按面板最近已知尺寸把绝对坐标限制在保留 20px 可见的范围内（Left/Top 定位不允许负值）。</summary>
        private double ClampToRoom(int index, double v, bool horizontal)
        {
            double size = _lastSize[index];
            var mw = _host?.MainWindow;
            if (size <= 0 || mw == null) return v;
            double window = horizontal ? mw.ActualWidth : mw.ActualHeight;
            if (window <= 0) return v;
            double room = Math.Max(0, window - size - 20);
            return Math.Max(0, Math.Min(room, v));
        }

        /// <summary>
        /// 布局后的越界收拢：拿到真实渲染尺寸后重算保存的绝对位置，若超出屏幕则
        /// 临时收拢 Margin（保存的配置不变），保证控件至少留 20px 在屏幕内。
        /// </summary>
        private void ScheduleBoundsCheck(int index)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
            {
                try
                {
                    if (_editMode) return; // 拖拽编辑中自行收拢
                    var box = _navBoxes != null && index < PanelCount ? _navBoxes[index] : null;
                    var mw = _host?.MainWindow;
                    if (box == null || mw == null || box.Visibility != Visibility.Visible) return;
                    double size = index < 2 ? box.ActualWidth : box.ActualHeight;
                    double window = index < 2 ? mw.ActualWidth : mw.ActualHeight;
                    if (size <= 0 || window <= 0) return;

                    bool sizeChanged = Math.Abs(_lastSize[index] - size) > 0.5;
                    _lastSize[index] = size;
                    if (!_savedX[index].HasValue || !_savedY[index].HasValue) return;

                    double x = ClampToRoom(index, _savedX[index].Value, true);
                    double y = ClampToRoom(index, _savedY[index].Value, false);
                    if (sizeChanged || Math.Abs(x - _savedX[index].Value) > 0.5 || Math.Abs(y - _savedY[index].Value) > 0.5)
                    {
                        var m = new Thickness(x, y, 0, 0);
                        if (box.Margin != m) box.Margin = m;
                        Log("控件 #" + index + " 位置超出屏幕，已临时收拢显示（保存的配置不变）", PluginLogLevel.Trace);
                    }
                }
                catch { }
            }));
        }

        private void ApplyFloatingBarOpacity()
        {
            var bar = _viewboxFloatingBar as UIElement;
            if (bar == null) return;
            try
            {
                double factor = Clamp(_barOpacity, 10, 100) / 100.0;
                double target = _barBaseOpacity * factor;
                if (CloseTo(bar.Opacity, target)) return;
                _suppressBarOpacityHook = true;
                try { bar.Opacity = target; }
                finally { _suppressBarOpacityHook = false; }
            }
            catch (Exception ex) { Log("应用浮动工具栏不透明度失败 " + ex.Message, PluginLogLevel.Warning); }
        }

        private void RestoreOriginals()
        {
            if (!_capturedOriginals || _navBoxes == null) return;
            for (int i = 0; i < PanelCount; i++)
            {
                try
                {
                    var box = _navBoxes[i];
                    if (box == null) continue;
                    box.Margin = _origMargins[i];
                    box.LayoutTransform = _origLayout[i];
                    var border = _navBorders != null && _navBorders[i] != null ? _navBorders[i] : (box.Child as Border);
                    if (border != null && !double.IsNaN(_origBorderOpacity[i])) border.Opacity = _origBorderOpacity[i];
                }
                catch { }
            }
            try
            {
                var bar = _viewboxFloatingBar as UIElement;
                if (bar != null && !double.IsNaN(_origBarOpacity))
                {
                    _suppressBarOpacityHook = true;
                    try { bar.Opacity = _origBarOpacity; }
                    finally { _suppressBarOpacityHook = false; }
                }
            }
            catch { }
            _capturedOriginals = false;
        }

        // ---------- 拖拽编辑模式 ----------

        private bool _editMode;
        private readonly Visibility[] _preEditVisibility = new Visibility[PanelCount];
        private readonly HorizontalAlignment[] _preEditHAlign = new HorizontalAlignment[PanelCount];
        private readonly VerticalAlignment[] _preEditVAlign = new VerticalAlignment[PanelCount];
        private readonly Thickness[] _preEditMargin = new Thickness[PanelCount];
        private readonly double?[] _preEditSavedX = new double?[PanelCount];
        private readonly double?[] _preEditSavedY = new double?[PanelCount];
        private Window _editToolbar;
        private Point _dragLastPos;
        private int _dragIndex = -1;
        private readonly MouseButtonEventHandler[] _dragDown = new MouseButtonEventHandler[PanelCount];
        private readonly MouseEventHandler[] _dragMove = new MouseEventHandler[PanelCount];
        private readonly MouseButtonEventHandler[] _dragUp = new MouseButtonEventHandler[PanelCount];
        // 编辑期间被本插件最小化的宿主窗口（设置窗口/插件工坊），退出编辑时按原状态恢复
        private readonly List<Window> _minimizedByEdit = new List<Window>();
        private readonly List<WindowState> _preEditWindowStates = new List<WindowState>();
        // 滑条进入编辑前的值（取消时还原）
        private readonly double[] _preEditScale = new double[PanelCount];
        private readonly double[] _preEditOpacity = new double[PanelCount];

        /// <summary>
        /// 进入拖拽编辑模式：强制显示四个翻页控件（统一改为 Left/Top 绝对定位），
        /// 鼠标/触摸拖动摆放，工具条上「保存并退出」或「取消」。随时可进（无需放映）。
        /// </summary>
        private void EnterEditMode()
        {
            if (_editMode) return;
            if (!EnsureResolved())
            {
                _host?.ShowNotification("未能定位翻页控件，无法进入编辑模式");
                return;
            }
            try
            {
                _editMode = true;
                // 捕获滑条进入前的值（取消时还原），并最小化遮挡视野的宿主窗口（保存/取消后恢复）
                for (int i = 0; i < PanelCount; i++)
                {
                    _preEditScale[i] = _scale[i];
                    _preEditOpacity[i] = _opacity[i];
                }
                MinimizeBlockingWindows();
                for (int i = 0; i < PanelCount; i++)
                {
                    var box = _navBoxes[i];
                    if (box == null) continue;
                    int index = i;

                    // 捕获进入前状态（取消时还原）
                    _preEditVisibility[i] = box.Visibility;
                    _preEditHAlign[i] = box.HorizontalAlignment;
                    _preEditVAlign[i] = box.VerticalAlignment;
                    _preEditMargin[i] = box.Margin;
                    _preEditSavedX[i] = _savedX[i];
                    _preEditSavedY[i] = _savedY[i];

                    // 起始位置：已保存值 > 可见面板当前左上角 > 默认角落/居中位
                    double startX, startY;
                    if (_savedX[i].HasValue && _savedY[i].HasValue)
                    {
                        startX = _savedX[i].Value;
                        startY = _savedY[i].Value;
                    }
                    else if (box.Visibility == Visibility.Visible && box.ActualWidth > 0)
                    {
                        try
                        {
                            var topLeft = box.TransformToVisual(_host.MainWindow).Transform(new Point(0, 0));
                            startX = Math.Max(0, topLeft.X);
                            startY = Math.Max(0, topLeft.Y);
                        }
                        catch
                        {
                            var def = ComputeDefaultPanelPosition(i);
                            startX = def.X;
                            startY = def.Y;
                        }
                    }
                    else
                    {
                        var def = ComputeDefaultPanelPosition(i);
                        startX = def.X;
                        startY = def.Y;
                    }
                    _savedX[i] = startX;
                    _savedY[i] = startY;
                    box.HorizontalAlignment = HorizontalAlignment.Left;
                    box.VerticalAlignment = VerticalAlignment.Top;
                    box.Margin = new Thickness(startX, startY, 0, 0);
                    box.Visibility = Visibility.Visible;
                    box.Cursor = Cursors.Hand;
                    AttachDragHandlers(box, index);
                }
                ShowEditToolbar();
                Log("进入拖拽编辑模式", PluginLogLevel.Info);
            }
            catch (Exception ex)
            {
                _editMode = false;
                RestoreBlockingWindows(); // 失败也要把最小化的宿主窗口还回来
                Log("进入编辑模式失败 " + ex.Message, PluginLogLevel.Warning);
                _host?.ShowNotification("进入编辑模式失败");
            }
        }

        /// <summary>未编辑过且面板隐藏时的默认摆放起点（贴近各自默认角落/居中位）。</summary>
        private Point ComputeDefaultPanelPosition(int index)
        {
            var mw = _host?.MainWindow;
            double w = mw?.ActualWidth ?? 0;
            double h = mw?.ActualHeight ?? 0;
            switch (index)
            {
                case 0: return new Point(10, Math.Max(10, h - 90));                            // 下方左
                case 1: return new Point(Math.Max(10, w - 260), Math.Max(10, h - 90));         // 下方右
                case 2: return new Point(10, Math.Max(10, h / 2 - 60));                        // 侧面左
                default: return new Point(Math.Max(10, w - 110), Math.Max(10, h / 2 - 60));    // 侧面右
            }
        }

        private void AttachDragHandlers(Viewbox box, int index)
        {
            int i = index;
            _dragDown[i] = (s, e) => NavEditDragDown(s, e, i);
            _dragMove[i] = (s, e) => NavEditDragMove(s, e, i);
            _dragUp[i] = (s, e) => NavEditDragUp(s, e, i);
            box.PreviewMouseLeftButtonDown += _dragDown[i];
            box.PreviewMouseMove += _dragMove[i];
            box.PreviewMouseLeftButtonUp += _dragUp[i];
        }

        private void DetachDragHandlers()
        {
            if (_navBoxes == null) return;
            for (int i = 0; i < PanelCount; i++)
            {
                var box = _navBoxes[i];
                if (box == null) continue;
                if (_dragDown[i] != null) box.PreviewMouseLeftButtonDown -= _dragDown[i];
                if (_dragMove[i] != null) box.PreviewMouseMove -= _dragMove[i];
                if (_dragUp[i] != null) box.PreviewMouseLeftButtonUp -= _dragUp[i];
                _dragDown[i] = null;
                _dragMove[i] = null;
                _dragUp[i] = null;
                box.Cursor = null;
            }
        }

        // Preview 隧道事件先于内层 Border 的翻页 MouseDown/Up 触发，e.Handled=true 阻止拖拽误触翻页
        private void NavEditDragDown(object sender, MouseButtonEventArgs e, int index)
        {
            if (!_editMode) return;
            var box = sender as UIElement;
            if (box == null) return;
            _dragIndex = index;
            _dragLastPos = e.GetPosition(_host?.MainWindow);
            box.CaptureMouse();
            Log("开始拖拽控件 #" + index + " @ " + _dragLastPos.X.ToString("F0") + "," + _dragLastPos.Y.ToString("F0"), PluginLogLevel.Trace);
            e.Handled = true;
        }

        private void NavEditDragMove(object sender, MouseEventArgs e, int index)
        {
            // 不检查 e.LeftButton：注入的鼠标事件（自动化/部分触摸设备）可能不带按键状态，
            // 以 _dragIndex + CaptureMouse 为准（与宿主浮动栏拖拽的标志位模式一致）
            if (!_editMode || _dragIndex != index) return;
            var box = sender as FrameworkElement;
            var mw = _host?.MainWindow;
            if (box == null || mw == null) return;
            var fe = box as FrameworkElement;
            if (fe != null && fe.ActualWidth > 0) _lastSize[index] = index < 2 ? fe.ActualWidth : fe.ActualHeight;
            var pos = e.GetPosition(mw);
            double x = ClampToRoom(index, _savedX[index].GetValueOrDefault() + (pos.X - _dragLastPos.X), true);
            double y = ClampToRoom(index, _savedY[index].GetValueOrDefault() + (pos.Y - _dragLastPos.Y), false);
            _savedX[index] = x;
            _savedY[index] = y;
            box.Margin = new Thickness(x, y, 0, 0);
            _dragLastPos = pos;
            e.Handled = true;
        }

        private void NavEditDragUp(object sender, MouseButtonEventArgs e, int index)
        {
            if (!_editMode || _dragIndex != index) return;
            (sender as UIElement)?.ReleaseMouseCapture();
            _dragIndex = -1;
            Log("结束拖拽控件 #" + index, PluginLogLevel.Trace);
            e.Handled = true;
        }

        private void ShowEditToolbar()
        {
            var mw = _host?.MainWindow;
            var tb = new Window
            {
                Title = "编辑翻页控件位置与外观",
                Width = 640,
                SizeToContent = SizeToContent.Height,
                ShowInTaskbar = false,
                ResizeMode = ResizeMode.NoResize,
                Background = TryBrush("PopupWindowBackground", Brushes.White)
            };
            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(new TextBlock
            {
                Text = "直接拖动四个翻页控件到想要的位置；下方滑条实时调整每个控件的缩放与透明度。",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = TryBrush("PopupWindowForeground", Brushes.Black)
            });

            // 每控件一行：缩放 / 透明度 滑条，拖动即时生效
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var h1 = new TextBlock { Text = "缩放 (%)", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = TryBrush("PopupWindowForeground", Brushes.Black) };
            Grid.SetRow(h1, 0); Grid.SetColumn(h1, 1);
            var h2 = new TextBlock { Text = "透明度 (%)", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Foreground = TryBrush("PopupWindowForeground", Brushes.Black) };
            Grid.SetRow(h2, 0); Grid.SetColumn(h2, 2);
            grid.Children.Add(h1); grid.Children.Add(h2);

            string[] names = { "下方左", "下方右", "侧面左", "侧面右" };
            for (int i = 0; i < PanelCount; i++)
            {
                int index = i;
                int rowIdx = i + 1;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var lbl = new TextBlock { Text = names[i], FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = TryBrush("PopupWindowForeground", Brushes.Black) };
                Grid.SetRow(lbl, rowIdx); Grid.SetColumn(lbl, 0);

                var sScale = new Slider
                {
                    Minimum = 50,
                    Maximum = 200,
                    Value = Clamp(_scale[index], 50, 200),
                    SmallChange = 1,
                    LargeChange = 10,
                    IsMoveToPointEnabled = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 3, 8, 3)
                };
                Grid.SetRow(sScale, rowIdx); Grid.SetColumn(sScale, 1);
                sScale.ValueChanged += (s, e) =>
                {
                    _scale[index] = Math.Round(sScale.Value);
                    ApplyPanel(index);
                    ScheduleBoundsCheck(index);
                };

                var sOpacity = new Slider
                {
                    Minimum = 10,
                    Maximum = 100,
                    Value = Clamp(_opacity[index], 10, 100),
                    SmallChange = 1,
                    LargeChange = 10,
                    IsMoveToPointEnabled = true,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 3, 8, 3)
                };
                Grid.SetRow(sOpacity, rowIdx); Grid.SetColumn(sOpacity, 2);
                sOpacity.ValueChanged += (s, e) =>
                {
                    _opacity[index] = Math.Round(sOpacity.Value);
                    ApplyPanel(index);
                };

                grid.Children.Add(lbl);
                grid.Children.Add(sScale);
                grid.Children.Add(sOpacity);
            }
            root.Children.Add(grid);

            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var save = new Button { Content = "保存并退出", Padding = new Thickness(15, 4, 15, 4) };
            var cancel = new Button { Content = "取消", Padding = new Thickness(15, 4, 15, 4), Margin = new Thickness(10, 0, 0, 0) };
            save.Click += (s, e) => SaveEditMode();
            cancel.Click += (s, e) => CancelEditMode();
            row.Children.Add(save);
            row.Children.Add(cancel);
            root.Children.Add(row);
            tb.Content = root;

            if (mw != null && mw.IsLoaded) tb.Owner = mw;
            // 纳入宿主统一弹层（Owner/Topmost/点击激活顺序与设置窗口、插件工坊一致）
            try { Ink_Canvas.Helpers.PopupWindowLayerHelper.Register(tb); } catch { }
            tb.WindowStartupLocation = WindowStartupLocation.Manual;
            tb.Left = Math.Max(0, (mw?.ActualWidth ?? 800) / 2 - 320);
            tb.Top = 24;
            tb.Closed += (s, e) => { if (_editMode) CancelEditMode(); }; // 点标题栏 X 一律视为取消
            _editToolbar = tb;
            tb.Show();
        }

        private void SaveEditMode()
        {
            if (!_editMode) return;
            ExitEditMode(true);
        }

        private void CancelEditMode()
        {
            if (!_editMode) return;
            // 还原进入前的保存值与滑条值（拖拽/调参中的修改全部丢弃）
            for (int i = 0; i < PanelCount; i++)
            {
                _savedX[i] = _preEditSavedX[i];
                _savedY[i] = _preEditSavedY[i];
                _scale[i] = _preEditScale[i];
                _opacity[i] = _preEditOpacity[i];
            }
            ExitEditMode(false);
        }

        /// <summary>
        /// 退出编辑模式。save=true 时 _saved 已是拖拽后的最终值，写盘并按宿主开关恢复可见性；
        /// 否则还原进入前状态。随后立即重应用，保证外观一致。
        /// </summary>
        private void ExitEditMode(bool save)
        {
            if (!_editMode) return;
            _editMode = false;
            try { if (_editToolbar != null) _editToolbar.Close(); } catch { }
            _editToolbar = null;
            DetachDragHandlers();
            _dragIndex = -1;

            if (save)
            {
                SaveConfig();
                RestorePanelVisibilityAfterSave();
            }
            else if (_navBoxes != null)
            {
                for (int i = 0; i < PanelCount; i++)
                {
                    var box = _navBoxes[i];
                    if (box == null) continue;
                    box.HorizontalAlignment = _preEditHAlign[i];
                    box.VerticalAlignment = _preEditVAlign[i];
                    box.Margin = _preEditMargin[i];
                    box.Visibility = _preEditVisibility[i];
                }
            }
            for (int i = 0; i < PanelCount; i++) ApplyPanel(i);
            RestoreBlockingWindows();
            Log(save ? "拖拽编辑已保存" : "拖拽编辑已取消", PluginLogLevel.Info);
        }

        /// <summary>
        /// 编辑期间最小化会遮挡视野的宿主窗口（设置窗口、插件工坊），退出编辑时按原 WindowState 恢复。
        /// </summary>
        private void MinimizeBlockingWindows()
        {
            _minimizedByEdit.Clear();
            _preEditWindowStates.Clear();
            try
            {
                var app = Application.Current;
                if (app == null) return;
                foreach (var w in app.Windows.OfType<Window>().ToList())
                {
                    try
                    {
                        bool blocking = w is MW_Settings || w.GetType().Name == "PluginWorkshopWindow";
                        if (!blocking || !w.IsLoaded) continue;
                        if (w.WindowState == WindowState.Minimized) continue; // 已最小化的不动也不恢复
                        _minimizedByEdit.Add(w);
                        _preEditWindowStates.Add(w.WindowState);
                        w.WindowState = WindowState.Minimized;
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void RestoreBlockingWindows()
        {
            for (int i = 0; i < _minimizedByEdit.Count; i++)
            {
                try
                {
                    var w = _minimizedByEdit[i];
                    if (w != null) w.WindowState = _preEditWindowStates[i];
                }
                catch { }
            }
            _minimizedByEdit.Clear();
            _preEditWindowStates.Clear();
        }

        /// <summary>保存后按宿主开关与放映状态恢复可见性（与宿主 Show/Hide 语义一致）。</summary>
        private void RestorePanelVisibilityAfterSave()
        {
            if (_navBoxes == null) return;
            bool inSlideShow = IsSlideShowActive();
            bool showBottom = inSlideShow, showSide = inSlideShow;
            try
            {
                var ppt = MainWindow.Settings.PowerPointSettings;
                showBottom = inSlideShow && ppt.IsShowBottomPPTNavigationPanel;
                showSide = inSlideShow && ppt.IsShowSidePPTNavigationPanel;
            }
            catch { }
            for (int i = 0; i < PanelCount; i++)
            {
                var box = _navBoxes[i];
                if (box == null) continue;
                box.Visibility = (i < 2 ? showBottom : showSide) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private bool IsSlideShowActive()
        {
            try
            {
                var mw = _host?.MainWindow as FrameworkElement;
                if (mw == null) return false;
                var floatHost = mw.FindName("MWFloatBarHost") as FrameworkElement;
                var endBtn = floatHost?.FindName("BtnPPTSlideShowEnd") as UIElement;
                return endBtn != null && endBtn.Visibility == Visibility.Visible;
            }
            catch { return false; }
        }

        // ---------- 设置面板 ----------

        private UIElement BuildSettingsPanelBody()
        {
            _panelSliders.Clear();
            var root = new StackPanel { MinWidth = 460, MaxWidth = 900 };

            root.Children.Add(MakeNote(
                "自定义 PPT 放映时四个翻页控件与浮动工具栏的缩放与透明度，以及翻页控件的位置。" +
                "放映中拖动滑条立即生效；非放映状态下的调整会在下次放映时生效。" +
                "位置通过「进入编辑模式」直接拖拽摆放，越界部分自动收拢。"));

            // 编辑模式入口：与主程序设置页「进入编辑模式」按钮等价
            var editBtn = new Button
            {
                Content = "进入编辑模式（拖拽摆放翻页控件）",
                Padding = new Thickness(15, 4, 15, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0)
            };
            editBtn.Click += (s, e) => EnterEditMode();
            root.Children.Add(editBtn);

            string[] groupNames = { "下方翻页控件（左）", "下方翻页控件（右）", "侧面翻页控件（左）", "侧面翻页控件（右）" };
            for (int i = 0; i < PanelCount; i++)
            {
                int index = i; // 闭包捕获
                root.Children.Add(BuildGroupHeader(groupNames[i]));
                root.Children.Add(BuildSliderRow("缩放 (%)", 50, 200,
                    () => _scale[index], v => _scale[index] = v,
                    () => { ApplyPanel(index); ScheduleBoundsCheck(index); }, "%"));
                root.Children.Add(BuildSliderRow("透明度 (%)", 10, 100,
                    () => _opacity[index], v => _opacity[index] = v,
                    () => { ApplyPanel(index); ScheduleBoundsCheck(index); }, "%"));
            }

            root.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 6) });
            root.Children.Add(BuildGroupHeader("浮动工具栏"));
            root.Children.Add(MakeNote("整体不透明度百分比，叠加在宿主场景值之上（放映中宿主会先调淡浮动栏）；100% 为默认。"));
            root.Children.Add(BuildSliderRow("不透明度 (%)", 10, 100,
                () => _barOpacity, v => _barOpacity = v,
                () => ApplyFloatingBarOpacity(), "%"));

            root.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 6) });
            var reset = new Button
            {
                Content = "恢复默认",
                Padding = new Thickness(15, 4, 15, 4),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            reset.Click += (s, e) =>
            {
                // 复位滑条值 → 触发 ValueChanged → 写配置并应用（与全部默认一致时无变化）
                foreach (var r in _panelSliders)
                {
                    try { if (r.Slider != null) r.Slider.Value = r.Def; } catch { }
                }
            };
            root.Children.Add(reset);

            return root;
        }

        private UIElement BuildSliderRow(string label, double min, double max,
            Func<double> get, Action<double> set, Action apply, string unit)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

            var labelTb = new TextBlock
            {
                Text = label,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(labelTb, 0);
            grid.Children.Add(labelTb);

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                Value = Clamp(get(), min, max),
                SmallChange = 1,
                LargeChange = 10,
                IsMoveToPointEnabled = true,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0)
            };
            Grid.SetColumn(slider, 1);
            grid.Children.Add(slider);

            var valueTb = new TextBlock
            {
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                Text = FormatValue(slider.Value, unit)
            };
            Grid.SetColumn(valueTb, 2);
            grid.Children.Add(valueTb);

            slider.ValueChanged += (s, e) =>
            {
                double v = Math.Round(slider.Value);
                valueTb.Text = FormatValue(v, unit);
                set(v);
                SaveConfig();
                try { apply(); } catch { }
            };

            _panelSliders.Add(new SliderRef { Slider = slider, Def = Clamp(get(), min, max) });
            return grid;
        }

        private static TextBlock BuildGroupHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 2)
            };
        }

        private static TextBlock MakeNote(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TryBrush("SettingsPageAnnotationForeground", Brushes.Gray)
            };
        }

        private static string FormatValue(double v, string unit)
        {
            return v.ToString("F0") + unit;
        }

        private static System.Windows.Media.Brush TryBrush(string resourceKey, System.Windows.Media.Brush fallback)
        {
            try
            {
                var app = Application.Current;
                if (app != null && app.TryFindResource(resourceKey) is System.Windows.Media.Brush b) return b;
            }
            catch { }
            return fallback;
        }

        // ---------- 配置持久化 ----------

        private string ConfigPath => Path.Combine(_pluginDirectory, "ppt-control-customizer.json");

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var data = new JavaScriptSerializer().Deserialize<ConfigData>(File.ReadAllText(ConfigPath));
                if (data == null) return;
                _scale[0] = Sanitize(data.BottomLeftScale, 100, 50, 200);
                _scale[1] = Sanitize(data.BottomRightScale, 100, 50, 200);
                _scale[2] = Sanitize(data.SideLeftScale, 100, 50, 200);
                _scale[3] = Sanitize(data.SideRightScale, 100, 50, 200);
                _opacity[0] = Sanitize(data.BottomLeftOpacity, 40, 10, 100);
                _opacity[1] = Sanitize(data.BottomRightOpacity, 40, 10, 100);
                _opacity[2] = Sanitize(data.SideLeftOpacity, 40, 10, 100);
                _opacity[3] = Sanitize(data.SideRightOpacity, 40, 10, 100);
                _savedX[0] = SanitizeNullable(data.BottomLeftPosX);
                _savedY[0] = SanitizeNullable(data.BottomLeftPosY);
                _savedX[1] = SanitizeNullable(data.BottomRightPosX);
                _savedY[1] = SanitizeNullable(data.BottomRightPosY);
                _savedX[2] = SanitizeNullable(data.SideLeftPosX);
                _savedY[2] = SanitizeNullable(data.SideLeftPosY);
                _savedX[3] = SanitizeNullable(data.SideRightPosX);
                _savedY[3] = SanitizeNullable(data.SideRightPosY);
                _barOpacity = Sanitize(data.FloatingBarOpacity, 100, 10, 100);
            }
            catch (Exception ex) { Log("读取配置失败 " + ex.Message, PluginLogLevel.Warning); }
        }

        private void SaveConfig()
        {
            try
            {
                var data = new ConfigData
                {
                    BottomLeftScale = _scale[0],
                    BottomRightScale = _scale[1],
                    SideLeftScale = _scale[2],
                    SideRightScale = _scale[3],
                    BottomLeftOpacity = _opacity[0],
                    BottomRightOpacity = _opacity[1],
                    SideLeftOpacity = _opacity[2],
                    SideRightOpacity = _opacity[3],
                    BottomLeftPosX = _savedX[0],
                    BottomLeftPosY = _savedY[0],
                    BottomRightPosX = _savedX[1],
                    BottomRightPosY = _savedY[1],
                    SideLeftPosX = _savedX[2],
                    SideLeftPosY = _savedY[2],
                    SideRightPosX = _savedX[3],
                    SideRightPosY = _savedY[3],
                    FloatingBarOpacity = _barOpacity
                };
                if (!Directory.Exists(_pluginDirectory)) Directory.CreateDirectory(_pluginDirectory);
                File.WriteAllText(ConfigPath, new JavaScriptSerializer().Serialize(data));
            }
            catch (Exception ex) { Log("保存配置失败 " + ex.Message, PluginLogLevel.Warning); }
        }

        private class ConfigData
        {
            public double BottomLeftScale { get; set; } = 100;
            public double BottomRightScale { get; set; } = 100;
            public double SideLeftScale { get; set; } = 100;
            public double SideRightScale { get; set; } = 100;
            public double BottomLeftOpacity { get; set; } = 40;
            public double BottomRightOpacity { get; set; } = 40;
            public double SideLeftOpacity { get; set; } = 40;
            public double SideRightOpacity { get; set; } = 40;
            // 拖拽编辑保存的绝对位置（null = 未编辑过，跟随宿主摆放）
            public double? BottomLeftPosX { get; set; }
            public double? BottomLeftPosY { get; set; }
            public double? BottomRightPosX { get; set; }
            public double? BottomRightPosY { get; set; }
            public double? SideLeftPosX { get; set; }
            public double? SideLeftPosY { get; set; }
            public double? SideRightPosX { get; set; }
            public double? SideRightPosY { get; set; }
            public double FloatingBarOpacity { get; set; } = 100;
        }

        private string FindPluginDirectory()
        {
            try
            {
                string root = _host?.PluginDirectory;
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                {
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        if (File.Exists(Path.Combine(dir, PluginDllName)) &&
                            File.Exists(Path.Combine(dir, "plugin.icplugin")))
                            return dir;
                    }
                }
            }
            catch { }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", PluginId);
        }

        // ---------- 小工具 ----------

        private void Log(string message, PluginLogLevel level = PluginLogLevel.Info)
        {
            try { _host?.Log("[PPT控件自定义] " + message, level); } catch { }
        }

        private static double Clamp(double v, double min, double max)
        {
            if (double.IsNaN(v)) return min;
            return Math.Max(min, Math.Min(max, v));
        }

        private static double Sanitize(double v, double def, double min, double max)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return def;
            return Math.Max(min, Math.Min(max, v));
        }

        private static double? SanitizeNullable(double? v)
        {
            if (!v.HasValue || double.IsNaN(v.Value) || double.IsInfinity(v.Value)) return null;
            return v.Value;
        }

        private static bool CloseTo(double a, double b)
        {
            return Math.Abs(a - b) < 0.001;
        }
    }
}
