using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Ink_Canvas.Plugins;

namespace Ink_Canvas.Plugins.PPTVideoEnhance
{
    /// <summary>
    /// PPT 视频增强插件（移植自 icc 的「PPT 内视频区域模式自动切换」，并按新需求重构）。
    ///
    /// 行为：
    ///  - 放映 PowerPoint / WPS 时自动识别当前幻灯片视频控件屏幕区域。
    ///  - 鼠标移入视频区域 → 切换为"鼠标/穿透输入"（可直接操作视频播放器），浮动栏显示保持不变，仅弹消息提示框。
    ///  - 视频区域左下角显示透明切换按钮：自动模式下显示"笔"图标，点击→锁定批注模式（强制批注输入、关闭自动切换，浮动栏不变）；
    ///    锁定模式下显示"播放"图标，点击→恢复自动切换。
    ///  - 无视频区域 / 退出放映 → 隐藏按钮并恢复批注输入，浮动栏不变。
    ///
    /// 关键点：不直接调用宿主 CursorIcon_Click / PenIcon_Click（它们会顺带改浮动栏），
    /// 而是用 GetInkCanvas() + FindName 取得宿主视觉元素，仅切换"输入穿透/批注"相关属性，
    /// 刻意跳过浮动栏动画/子面板等副作用，从而做到"浮动栏状态不变"。
    /// </summary>
    public class PPTVideoEnhancePlugin : IPlugin
    {
        public PluginManifest Manifest { get; private set; }

        private IPluginHost _host;
        private ComContext _com;
        private PptVideoDetector _detector;
        private DispatcherTimer _pollTimer;
        private Dispatcher _uiDispatcher;
        private SettingsStore _settings;

        private System.Windows.Controls.InkCanvas _inkCanvas;
        private Panel _mainGrid;
        private UIElement _gridBackgroundCoverHolder;
        private UIElement _gridInkCanvasSelectionCover;
        private System.Windows.Controls.Canvas _overlay;   // 覆盖层（置于 Main_Grid 之上，承载按钮，保证笔/触摸输入正确路由）
        private string _lastMaskCache;      // 穿透命中掩码几何缓存，避免每轮轮询重复重建
        private SwipeSuppressor _swipe;       // 防误翻页低层钩子（鼠标模式下拦截视频区域外的触摸翻页）
        private DispatcherTimer _swipeGraceTimer; // 进入鼠标模式后的"防误翻页宽限窗口"计时器
        private const int SwipeGraceMs = 300; // 自动切换进入鼠标模式后，仅头 300ms 拦截区域外触摸翻页

        private bool _inputPassthrough;     // 当前输入是否为穿透（鼠标）模式
        private bool _passthroughStarted;   // 是否已进入穿透并保存宿主输入状态快照
        private bool _lockedAnnotation;      // 是否锁定批注（关闭自动切换）
        private bool _wasSlideShowActive;

        // 防误翻页：离开穿透时延迟/等待松开再恢复批注，避免切换瞬间鼠标点击被判定为点击 PPT 导致翻页。
        private long _pendingExitTick;      // 计划离开穿透（恢复批注）的时间戳；-1=等待鼠标松开；0=无计划

        // 进入穿透前宿主输入状态快照：离开视频时恢复宿主原模式（鼠标/批注/选择等），
        // 避免"无脑切回批注"导致宿主界面与浮动栏图标不一致。
        private bool _savedInkHitTest;
        private InkCanvasEditingMode _savedEditingMode;
        private Brush _savedMainGridBackground;
        private Visibility _savedBgCoverHolder;
        private Visibility _savedSelectionCover;

        // 探测器返回的 VideoRegion 坐标是物理屏幕像素（GetWindowRect/GetCursorPos）。
        // 按钮定位统一交给 _overlay.PointFromScreen（它内部用 TransformFromDevice 自动做物理→逻辑转换），
        // 无需再手动乘 DPI 比例，否则会造成双重转换导致按钮偏移。

        private readonly Dictionary<int, PenToggleButton> _buttons = new Dictionary<int, PenToggleButton>();

        public void Initialize(IPluginHost host)
        {
            _host = host;

            Manifest = new PluginManifest
            {
                Id = "ink-canvas.ppt-video-enhance",
                Name = "PPT视频增强",
                Version = "1.1.6",
                Author = "muqiu",
                Description = "放映 PPT / WPS 时自动识别幻灯片视频控件区域，鼠标移入视频即切换为鼠标/穿透模式（可直接操作播放），浮动栏显示保持不变；视频左下角提供透明笔/播放切换按钮，点击锁定批注或恢复自动。支持微软 PowerPoint 与 WPS 演示。"
            };

            _settings = new SettingsStore(_host.PluginDirectory);
            _settings.Load();

            _uiDispatcher = _host.MainWindow?.Dispatcher;

            // 获取宿主画布与关键视觉元素（仅用于切换"输入模式"，不改动浮动栏）
            _uiDispatcher?.Invoke(() =>
            {
                _inkCanvas = _host.GetInkCanvas();
                _mainGrid = _host.MainWindow?.FindName("Main_Grid") as Panel;
                _gridBackgroundCoverHolder = _host.MainWindow?.FindName("GridBackgroundCoverHolder") as UIElement;
                _gridInkCanvasSelectionCover = _host.MainWindow?.FindName("GridInkCanvasSelectionCover") as UIElement;

                // 在 Main_Grid 之上创建覆盖层 Canvas，用于承载按钮。覆盖层本身背景为空（不拦截输入），
                // 只有按钮本身可命中，从而整屏仍可书写批注，仅按钮处可点击。
                if (_mainGrid != null)
                {
                    _overlay = new System.Windows.Controls.Canvas
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Stretch
                    };
                    Panel.SetZIndex(_overlay, 9999);
                    _mainGrid.Children.Add(_overlay);
                }
            });

            _com = new ComContext();
            _detector = new PptVideoDetector(_com);
            _detector.Start();

            // 防误翻页：在鼠标/穿透模式下，用低层钩子拦截"视频区域外"的触摸手势，防止误触 PPT/WPS 翻页。
            _swipe = new SwipeSuppressor(_host, () => _detector?.GetRegions(), _settings.EnterMarginPx);
            _swipe.Start();

            // 轮询在 UI 线程上进行，确保输入切换调用落在正确的线程
            if (_uiDispatcher != null)
            {
                _uiDispatcher.Invoke(() =>
                {
                    _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_settings.PollIntervalMs) };
                    _pollTimer.Tick += PollTick;
                    _pollTimer.Start();

                    // 防误翻页宽限窗口计时器：进入鼠标模式后只拦截头 SwipeGraceMs 毫秒，超时恢复 PPT/WPS 翻页
                    _swipeGraceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SwipeGraceMs) };
                    _swipeGraceTimer.Tick += SwipeGraceElapsed;
                });
            }

            _host.RegisterRouteHandler("ppt-video-enhance:toggle", _ =>
            {
                ToggleLock();
                return true;
            });

            // 挂宿主窗口输入事件：笔/触摸/鼠标落笔时即时校验落笔位置，若在视频区域外且处于穿透状态，
            // 立即恢复批注，让"视频区域外书写的第一笔"即为批注（而不是等 120ms 轮询，导致第一笔丢失）。
            var mainWindow = _host.MainWindow;
            if (mainWindow != null)
            {
                mainWindow.PreviewStylusDown += OnInputDown;
                mainWindow.PreviewTouchDown += OnInputDown;
            }

            _host.ApplicationExiting += OnAppExiting;

            _host.Log("PPT视频增强插件已初始化", PluginLogLevel.Event);
        }

        private void PollTick(object sender, EventArgs e)
        {
            if (!_settings.Enabled) return;

            bool slide = _detector.IsSlideShowActive();
            var regions = _detector.GetRegions();
            _swipe?.UpdateRegions(regions);

            // 穿透期间保持局部穿透掩码与当前视频区域同步（区域移动/换页/窗口尺寸变化时更新）
            if (_inputPassthrough) SetPassthroughBackground();

            if (_settings.OnlyInSlideShow && !slide)
            {
                if (_wasSlideShowActive || _buttons.Count > 0)
                {
                    HideAllButtons();
                    if (_inputPassthrough) ApplyInput(false);
                    _wasSlideShowActive = false;
                }
                return;
            }
            _wasSlideShowActive = slide;

            if (_settings.EnablePenButton) SyncButtons(regions);
            else HideAllButtons();

            // 锁定批注：强制批注输入，不再自动切换
            if (_lockedAnnotation)
            {
                if (_inputPassthrough) ApplyInput(false);
                return;
            }

            // 自动模式：按光标位置自动切换输入
            if (regions.Count == 0)
            {
                if (_inputPassthrough) ApplyInput(false);
                return;
            }

            POINT p;
            if (!GetCursorPos(out p)) return;
            double m = _settings.EnterMarginPx;
            bool inside = false;
            foreach (var r in regions)
            {
                if (p.X >= r.Left - m && p.X <= r.Left + r.Width + m &&
                    p.Y >= r.Top - m && p.Y <= r.Top + r.Height + m)
                {
                    inside = true;
                    break;
                }
            }

            bool desired = inside; // 在视频区域内 → 穿透输入
            if (desired)
            {
                // 鼠标回到视频内：取消任何待执行的"离开穿透"
                _pendingExitTick = 0;
                if (!_inputPassthrough)
                {
                    ApplyInput(true);
                    // 若浮动栏本就处于"鼠标"状态，进入视频区域对用户无实际变化，
                    // 不弹"进入鼠标模式"提示，避免无意义提示。
                    if (_settings.ShowNotifications && !IsHostInMouseMode())
                        _host.ShowNotification("已进入视频区域：鼠标模式（可直接操作视频）");
                }
            }
            else
            {
                if (_inputPassthrough)
                {
                    // 离开视频：延迟/等待松开后再恢复批注，避免切换瞬间的鼠标事件被判定为点击 PPT 导致翻页。
                    // 若鼠标仍按下（可能正在拖动视频进度等），则等其松开后再恢复。
                    if (_pendingExitTick == 0)
                    {
                        _pendingExitTick = IsLeftButtonDown() ? -1 : Environment.TickCount + 120;
                    }
                    else if (_pendingExitTick == -1)
                    {
                        if (!IsLeftButtonDown())
                        {
                            _pendingExitTick = 0;
                            ApplyInput(false);
                            // 浮动栏本为鼠标状态时不提示"恢复批注"，避免无意义提示
                            if (_settings.ShowNotifications && !IsHostInMouseMode())
                                _host.ShowNotification("已离开视频区域：恢复批注输入");
                        }
                    }
                    else if (Environment.TickCount >= _pendingExitTick)
                    {
                        _pendingExitTick = 0;
                        ApplyInput(false);
                        if (_settings.ShowNotifications && !IsHostInMouseMode())
                            _host.ShowNotification("已离开视频区域：恢复批注输入");
                    }
                }
                else
                {
                    _pendingExitTick = 0;
                }
            }
        }

        /// <summary>
        /// 仅切换"输入模式"（穿透 / 非穿透），刻意不触碰浮动栏的任何视觉状态。
        /// 复刻 CursorIcon_Click / PenIcon_Click 的"输入部分"，跳过其浮动栏动画与子面板副作用。
        /// 进入穿透时保存宿主输入状态快照；退出时恢复宿主原样 → 避免"浮动栏是鼠标但界面已是批注"状态漂移。
        /// </summary>
        private void ApplyInput(bool passthrough)
        {
            try
            {
                if (passthrough)
                {
                    // 进入穿透：先保存当前宿主输入状态
                    if (!_passthroughStarted && _inkCanvas != null && _mainGrid != null)
                    {
                        _savedInkHitTest = _inkCanvas.IsHitTestVisible;
                        _savedEditingMode = _inkCanvas.EditingMode;
                        _savedMainGridBackground = _mainGrid.Background;
                        if (_gridBackgroundCoverHolder != null)
                            _savedBgCoverHolder = _gridBackgroundCoverHolder.Visibility;
                        if (_gridInkCanvasSelectionCover != null)
                            _savedSelectionCover = _gridInkCanvasSelectionCover.Visibility;
                        _passthroughStarted = true;
                    }
                    _inkCanvas.Visibility = Visibility.Visible;
                    _inkCanvas.IsHitTestVisible = false;
                    if (_gridBackgroundCoverHolder != null) _gridBackgroundCoverHolder.Visibility = Visibility.Collapsed;
                    try { _inkCanvas.Select(new StrokeCollection()); } catch { }
                    if (_gridInkCanvasSelectionCover != null) _gridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;

                    // 局部穿透：只让视频区域矩形内的窗口像素透明（alpha=0，鼠标/触摸穿透给 PPT），
                    // 区域外保持 alpha=1 可命中拦截 → 落笔在视频区域外时窗口能第一时间收到事件。
                    SetPassthroughBackground();
                }
                else
                {
                    if (_lockedAnnotation)
                    {
                        // 锁定批注：强制设置为批注模式（这是用户点击按钮主动要求的）
                        _inkCanvas.Visibility = Visibility.Visible;
                        _inkCanvas.IsHitTestVisible = true;
                        _inkCanvas.EditingMode = InkCanvasEditingMode.Ink;
                        if (_mainGrid != null) _mainGrid.Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
                        if (_gridBackgroundCoverHolder != null) _gridBackgroundCoverHolder.Visibility = Visibility.Visible;
                        if (_gridInkCanvasSelectionCover != null) _gridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        // 自动模式：恢复进入穿透之前的宿主状态（鼠标/选择/...），保持与浮动栏一致
                        if (!_passthroughStarted) return;

                        if (_inkCanvas != null)
                        {
                            _inkCanvas.Visibility = Visibility.Visible;
                            _inkCanvas.IsHitTestVisible = _savedInkHitTest;
                            _inkCanvas.EditingMode = _savedEditingMode;
                        }
                        if (_mainGrid != null) _mainGrid.Background = _savedMainGridBackground;
                        if (_gridBackgroundCoverHolder != null) _gridBackgroundCoverHolder.Visibility = _savedBgCoverHolder;
                        if (_gridInkCanvasSelectionCover != null) _gridInkCanvasSelectionCover.Visibility = _savedSelectionCover;

                        _lastMaskCache = null;
                        _passthroughStarted = false;
                    }
                }
                _inputPassthrough = passthrough;
                if (_swipe != null)
                {
                    if (passthrough && _settings.SuppressSwipe)
                    {
                        // 进入鼠标/穿透模式：开启"切换后宽限窗口"——窗口内拦截视频区域外的触摸翻页，
                        // 窗口结束后恢复 PPT/WPS 自带翻页（允许正常滑动翻页）。
                        _swipe.Active = true;
                        if (_swipeGraceTimer != null)
                        {
                            _swipeGraceTimer.Stop();
                            _swipeGraceTimer.Start();
                        }
                    }
                    else
                    {
                        _swipe.Active = false;
                        _swipeGraceTimer?.Stop();
                    }
                }
            }
            catch (Exception ex)
            {
                _host.Log($"切换输入模式失败: {ex.Message}", PluginLogLevel.Error);
            }
        }

        /// <summary>
        /// 防误翻页宽限窗口到期：进入鼠标模式已超过 SwipeGraceMs 毫秒，恢复 PPT/WPS 自带的滑动翻页
        /// （视频区域外的触摸不再被拦截）。若窗口期内已离开鼠标模式，Active 早已为 false，此处无副作用。
        /// </summary>
        private void SwipeGraceElapsed(object sender, EventArgs e)
        {
            _swipeGraceTimer?.Stop();
            if (_inputPassthrough && _swipe != null) _swipe.Active = false;
        }

        /// <summary>
        /// 局部穿透：把 Main_Grid 背景设为"视频区域矩形 alpha=0、其余 alpha=1"的 DrawingBrush。
        /// 这样视频区域内鼠标/触摸穿透到 PPT 可操作；视频区域外窗口保持可命中，能收到落笔事件，
        /// 从而在触摸屏上"视频区域外书写的第一笔"能立即被识别为批注。
        /// </summary>
        private void SetPassthroughBackground()
        {
            try
            {
                if (_mainGrid == null) return;
                double w = _mainGrid.ActualWidth;
                double h = _mainGrid.ActualHeight;
                if (w <= 0 || h <= 0) return;

                var regions = _detector?.GetRegions();
                var cache = new System.Text.StringBuilder();
                cache.Append(w.ToString("0.0")).Append('x').Append(h.ToString("0.0"));
                Geometry g = new RectangleGeometry(new Rect(0, 0, w, h));
                if (regions != null)
                {
                    foreach (var r in regions)
                    {
                        var tl = _mainGrid.PointFromScreen(new Point(r.Left, r.Top));
                        var br = _mainGrid.PointFromScreen(new Point(r.Left + r.Width, r.Top + r.Height));
                        var hole = new RectangleGeometry(new Rect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y));
                        g = new CombinedGeometry(GeometryCombineMode.Exclude, g, hole);
                        cache.Append('|').Append(tl.X.ToString("0.0")).Append(',').Append(tl.Y.ToString("0.0"))
                            .Append('-').Append((br.X - tl.X).ToString("0.0")).Append(',').Append((br.Y - tl.Y).ToString("0.0"));
                    }
                }
                string key = cache.ToString();
                if (key == _lastMaskCache && _mainGrid.Background is DrawingBrush) return;

                var brush = new DrawingBrush(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)), null, g))
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                };
                _mainGrid.Background = brush;
                _lastMaskCache = key;
            }
            catch { }
        }

        /// <summary>
        /// 落笔（笔/触摸/鼠标）时即时校验：若在视频区域外且当前处于穿透状态，立即恢复批注，
        /// 让第一笔正常书写，无需等待 120ms 轮询（否则第一笔会因来不及判断而以穿透/鼠标状态丢失）。
        /// </summary>
        private void OnInputDown(object sender, InputEventArgs e)
        {
            if (_lockedAnnotation || !_inputPassthrough) return;
            if (_overlay == null) return;
            try
            {
                Point logical;
                if (e is MouseEventArgs me) logical = me.GetPosition(_overlay);
                else if (e is StylusEventArgs se) logical = se.GetPosition(_overlay);
                else if (e is TouchEventArgs te) logical = te.GetTouchPoint(_overlay).Position;
                else return;
                var physical = _overlay.PointToScreen(logical);
                if (!IsInsideAnyRegion(physical.X, physical.Y))
                {
                    // 落笔在视频区域外：立即恢复批注，让这一笔成为批注笔迹
                    ApplyInput(false);
                }
            }
            catch { }
        }

        private bool IsInsideAnyRegion(double px, double py)
        {
            var regions = _detector?.GetRegions();
            if (regions == null || regions.Count == 0) return false;
            double m = _settings.EnterMarginPx;
            foreach (var r in regions)
            {
                if (px >= r.Left - m && px <= r.Left + r.Width + m &&
                    py >= r.Top - m && py <= r.Top + r.Height + m)
                    return true;
            }
            return false;
        }

        private void ToggleLock()
        {
            _lockedAnnotation = !_lockedAnnotation;
            _uiDispatcher?.Invoke(() =>
            {
                if (_lockedAnnotation)
                {
                    ApplyInput(false);
                    foreach (var b in _buttons.Values) b.SetLocked(true);
                }
                else
                {
                    foreach (var b in _buttons.Values) b.SetLocked(false);
                }
            });
            if (_settings.ShowNotifications)
                _host.ShowNotification(_lockedAnnotation
                    ? "已锁定批注模式（不再自动切换，可直接书写）"
                    : "已恢复自动切换模式");
        }

        private void OnToggleButtonClick() => ToggleLock();

        private static bool IsLeftButtonDown()
        {
            try { return (GetAsyncKeyState(0x01) & 0x8000) != 0; }
            catch { return false; }
        }

        /// <summary>
        /// 判断浮动栏当前是否为"鼠标"状态：宿主切换到鼠标模式时会把墨迹画布
        /// EditingMode 置为 Select（笔模式为 Ink）。
        /// 鼠标状态下"进入/离开视频区域"对用户无实际变化，应抑制相关提示。
        /// </summary>
        private bool IsHostInMouseMode()
        {
            try { return _inkCanvas != null && _inkCanvas.EditingMode == InkCanvasEditingMode.Select; }
            catch { return false; }
        }

        private void SyncButtons(IReadOnlyList<VideoRegion> regions)
        {
            if (_overlay == null) return;

            int n = regions.Count;
            for (int i = 0; i < n; i++)
            {
                PenToggleButton b;
                bool created = false;
                if (!_buttons.TryGetValue(i, out b))
                {
                    b = new PenToggleButton(OnToggleButtonClick);
                    _buttons[i] = b;
                    created = true;
                }
                var r = regions[i];
                var btn = b;
                _uiDispatcher.Invoke(() =>
                {
                    try
                    {
                        if (created)
                        {
                            _overlay.Children.Add(btn);
                            Panel.SetZIndex(btn, 9999);
                        }
                        // 探测器坐标为物理屏幕像素（GetWindowRect）。
                        // PointFromScreen 期望输入设备（物理）像素，内部用 TransformFromDevice
                        // 自动转成覆盖层局部逻辑坐标（同时处理宿主窗口位置与 DPI）。
                        // 注意：此处不能先手动乘 DPI 比例——那会导致物理→逻辑被做两次，DPI≠100% 时按钮偏移。
                        // 换算"视频左下角"这个点，按钮底部贴着该点内缩 6px，即为视频区域左下角。
                        double bx = r.Left;
                        double by = r.Top + r.Height;
                        var local = _overlay.PointFromScreen(new Point(bx, by));
                        double left = local.X + 6;
                        double top = local.Y - PenToggleButton.Size - 6;
                        System.Windows.Controls.Canvas.SetLeft(btn, left);
                        System.Windows.Controls.Canvas.SetTop(btn, top);
                        btn.SetLocked(_lockedAnnotation);
                        if (btn.Visibility != Visibility.Visible) btn.Visibility = Visibility.Visible;
                    }
                    catch { }
                });
            }
            // 移除多余按钮（区域减少时）
            var keys = new List<int>(_buttons.Keys);
            foreach (var key in keys)
            {
                if (key >= n)
                {
                    var b = _buttons[key];
                    _buttons.Remove(key);
                    var btn = b;
                    _uiDispatcher.Invoke(() => { try { _overlay.Children.Remove(btn); } catch { } });
                }
            }
        }

        private void HideAllButtons()
        {
            var overlay = _overlay;
            foreach (var kv in new List<KeyValuePair<int, PenToggleButton>>(_buttons))
            {
                var b = kv.Value;
                _buttons.Remove(kv.Key);
                var btn = b;
                _uiDispatcher.Invoke(() => { try { overlay?.Children.Remove(btn); } catch { } });
            }
        }

        private void OnAppExiting(object sender, EventArgs e) => Shutdown();

        public void Shutdown()
        {
            try
            {
                if (_pollTimer != null)
                {
                    var t = _pollTimer;
                    _uiDispatcher?.Invoke(() => t.Stop());
                }
                _detector?.Stop();
                _com?.Stop();
                _swipe?.Stop();
                _swipeGraceTimer?.Stop();
                try { _uiDispatcher?.Invoke(() => ApplyInput(false)); } catch { }
                HideAllButtons();
                if (_overlay != null)
                {
                    var ov = _overlay;
                    _overlay = null;
                    var grid = _mainGrid;
                    _uiDispatcher?.Invoke(() => { try { grid?.Children.Remove(ov); } catch { } });
                }
                _host?.UnregisterRouteHandler("ppt-video-enhance:toggle");
                var mainWindow = _host?.MainWindow;
                if (mainWindow != null)
                {
                    mainWindow.PreviewStylusDown -= OnInputDown;
                    mainWindow.PreviewTouchDown -= OnInputDown;
                }
                _host?.Log("PPT视频增强插件已关闭", PluginLogLevel.Event);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private struct POINT
        {
            public int X;
            public int Y;
        }
    }
}
