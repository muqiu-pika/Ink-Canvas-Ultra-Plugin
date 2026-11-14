using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Ink_Canvas.Plugins;

namespace Ink_Canvas.Plugins.FunctionGraph
{
    /// <summary>
    /// 一次成形的画布放置控制器（对齐宿主「图形 / 图库」拖拽放置的交互）：
    /// ① 按下 = 坐标系原点（元素中心，固定不动）；
    /// ② 拖动 = 确定图像半宽/半高（等价于把右上角拖到 (xMax, yMax) 落点）；
    /// ③ 松手 = 提交元素并记一次撤销。
    ///
    /// 输入处理参照 MW_InkGallery.cs / MW_LaserPointer.cs 的成熟做法：
    ///  - 用单一 PlacementSource 记录本次拖拽的输入源，触摸提升(promotion)出的
    ///    Stylus/Mouse 事件被吞掉，不再出现「一次触摸触发两次按下」；
    ///  - CaptureMouse / CaptureStylus / CaptureTouch 保证指针移出画布仍能收到 Move/Up；
    ///  - 轻量虚线框预览（复用同一元素，只改尺寸/位置），不再每帧重建整幅函数图像；
    ///  - Esc、窗口失焦、捕获丢失、放置完成后的残留提升事件都有兜底；
    ///  - 结束时无条件恢复 inkCanvas 原本的 EditingMode。
    /// </summary>
    internal sealed class CanvasPlacementController
    {
        private enum PlacementSource { None, Mouse, Stylus, Touch }

        private const double MinElementSize = 40;      // 元素最小尺寸（px），太小视为误触
        private const double SuppressMilliseconds = 350;

        private readonly IPluginHost _host;
        private readonly FunctionGraphRequest _request;

        private InkCanvas _canvas;
        private InkCanvasEditingMode _previousEditingMode;
        private bool _finished;

        private PlacementSource _source = PlacementSource.None;
        private TouchDevice _activeTouchDevice;
        private Point _origin;
        private FrameworkElement _preview;
        private Rectangle _previewOutline;
        private TextBlock _previewLabel;
        private bool _releasingCaptures;
        private DateTime _suppressUntil = DateTime.MinValue;

        private Color _themeForeground = Color.FromRgb(0x33, 0x33, 0x33);
        private double _dpiScale = 1;

        public CanvasPlacementController(IPluginHost host, FunctionGraphRequest request)
        {
            _host = host;
            _request = request;
        }

        public void Start()
        {
            _canvas = _host.GetInkCanvas();
            if (_canvas == null)
            {
                _host.Log("函数绘图：画布不可用", PluginLogLevel.Error);
                return;
            }

            try
            {
                var fg = _canvas.TryFindResource("PopupWindowForeground")
                         ?? _canvas.TryFindResource("FloatBarForeground");
                if (fg is SolidColorBrush solid) _themeForeground = solid.Color;
            }
            catch { }

            try
            {
                _dpiScale = Math.Max(1.0, VisualTreeHelper.GetDpi(_canvas).DpiScaleX);
            }
            catch { }

            _previousEditingMode = _canvas.EditingMode;
            _canvas.EditingMode = InkCanvasEditingMode.None;

            _canvas.PreviewStylusDown += OnStylusDown;
            _canvas.PreviewStylusMove += OnStylusMove;
            _canvas.PreviewStylusUp += OnStylusUp;
            _canvas.PreviewTouchDown += OnTouchDown;
            _canvas.PreviewTouchMove += OnTouchMove;
            _canvas.PreviewTouchUp += OnTouchUp;
            _canvas.PreviewMouseDown += OnMouseDown;
            _canvas.PreviewMouseMove += OnMouseMove;
            _canvas.PreviewMouseUp += OnMouseUp;
            _canvas.LostMouseCapture += OnCaptureLost;
            _canvas.LostStylusCapture += OnCaptureLost;
            _canvas.TouchLeave += OnCaptureLost;

            if (_host.MainWindow != null)
            {
                _host.MainWindow.PreviewKeyDown += OnPreviewKeyDown;
                _host.MainWindow.Deactivated += OnWindowDeactivated;
            }

            _host.ShowNotification("函数绘图：按住并拖动一次即可放置（按下点为原点，Esc 取消）");
        }

        public void Cancel()
        {
            Finish(true);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Cancel();
        }

        private void OnWindowDeactivated(object sender, EventArgs e)
        {
            // 切走窗口时若还在拖动，直接收尾，避免输入钩子残留
            if (!_finished) Finish(true);
        }

        private void OnCaptureLost(object sender, EventArgs e)
        {
            // 主动释放捕获时 WPF 会同步派发 Lost*Capture，不能把它误判成「拖拽中断」
            if (_releasingCaptures || _finished || _source == PlacementSource.None) return;
            Finish(true);
        }

        // ===== 输入入口：单一输入源去重 =====

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (DateTime.Now < _suppressUntil) { e.Handled = true; return; }
            HandleDown(PlacementSource.Mouse, e.GetPosition(_canvas), null, e);
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            HandleMove(PlacementSource.Mouse, e.GetPosition(_canvas), null, e);
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (_source == PlacementSource.None && DateTime.Now < _suppressUntil) { e.Handled = true; return; }
            HandleUp(PlacementSource.Mouse, e.GetPosition(_canvas), null, e);
        }

        private void OnStylusDown(object sender, StylusDownEventArgs e)
        {
            HandleDown(PlacementSource.Stylus, e.GetPosition(_canvas), null, e);
        }

        private void OnStylusMove(object sender, StylusEventArgs e)
        {
            HandleMove(PlacementSource.Stylus, e.GetPosition(_canvas), null, e);
        }

        private void OnStylusUp(object sender, StylusEventArgs e)
        {
            HandleUp(PlacementSource.Stylus, e.GetPosition(_canvas), null, e);
        }

        private void OnTouchDown(object sender, TouchEventArgs e)
        {
            HandleDown(PlacementSource.Touch, e.GetTouchPoint(_canvas).Position, e.TouchDevice, e);
        }

        private void OnTouchMove(object sender, TouchEventArgs e)
        {
            if (!ReferenceEquals(e.TouchDevice, _activeTouchDevice)) return;
            HandleMove(PlacementSource.Touch, e.GetTouchPoint(_canvas).Position, e.TouchDevice, e);
        }

        private void OnTouchUp(object sender, TouchEventArgs e)
        {
            if (_source == PlacementSource.Touch && !ReferenceEquals(e.TouchDevice, _activeTouchDevice)) return;
            HandleUp(PlacementSource.Touch, e.GetTouchPoint(_canvas).Position, e.TouchDevice, e);
        }

        private void HandleDown(PlacementSource source, Point point, TouchDevice device, InputEventArgs e)
        {
            // 同一次触摸已由 Touch/Stylus 接管时，后续提升出的事件一律吞掉
            if (_source != PlacementSource.None)
            {
                e.Handled = true;
                return;
            }

            _source = source;
            _activeTouchDevice = device;
            _origin = point;

            try
            {
                if (source == PlacementSource.Mouse) _canvas.CaptureMouse();
                else if (source == PlacementSource.Stylus) _canvas.CaptureStylus();
                else if (source == PlacementSource.Touch && device != null) _canvas.CaptureTouch(device);
            }
            catch { }

            EnsurePreview();
            UpdatePreview(point);
            e.Handled = true;
        }

        private void HandleMove(PlacementSource source, Point point, TouchDevice device, InputEventArgs e)
        {
            if (_source != source) return;
            UpdatePreview(point);
            e.Handled = true;
        }

        private void HandleUp(PlacementSource source, Point point, TouchDevice device, InputEventArgs e)
        {
            // 非本次拖拽来源的 Up（触摸提升的 MouseUp 等）不能触发提交，但要吞掉避免冒泡
            if (_source != source)
            {
                if (DateTime.Now < _suppressUntil) e.Handled = true;
                return;
            }

            e.Handled = true;
            Point end = point;
            ReleaseAllCaptures();
            _source = PlacementSource.None;
            _activeTouchDevice = null;
            _suppressUntil = DateTime.Now.AddMilliseconds(SuppressMilliseconds);

            RemovePreview();
            Commit(end);
            Finish(false);
        }

        // ===== 预览 =====

        /// <summary>以原点为中心的对称矩形，任意方向拖动都得到同样的元素范围。</summary>
        private Rect ComputeRect(Point end)
        {
            double halfWidth = Math.Abs(end.X - _origin.X);
            double halfHeight = Math.Abs(end.Y - _origin.Y);
            return new Rect(_origin.X - halfWidth, _origin.Y - halfHeight,
                            Math.Max(halfWidth * 2, 1), Math.Max(halfHeight * 2, 1));
        }

        private void EnsurePreview()
        {
            if (_preview != null) return;
            var brush = new SolidColorBrush(_themeForeground);
            brush.Freeze();
            var fill = new SolidColorBrush(Color.FromArgb(0x18, _themeForeground.R, _themeForeground.G, _themeForeground.B));
            fill.Freeze();

            _previewOutline = new Rectangle
            {
                Stroke = brush,
                StrokeThickness = Math.Max(1.5, 1.5 * _dpiScale),
                StrokeDashArray = new DoubleCollection(new[] { 5.0, 3.0 }),
                Fill = fill,
                IsHitTestVisible = false
            };

            _previewLabel = new TextBlock
            {
                Text = "f(x) = " + _request.ExpressionText,
                FontSize = Math.Max(11, 12 * _dpiScale),
                Foreground = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                IsHitTestVisible = false
            };

            var grid = new Grid { IsHitTestVisible = false };
            grid.Children.Add(_previewOutline);
            grid.Children.Add(_previewLabel);
            _preview = grid;

            Panel.SetZIndex(_preview, 9998);
            _canvas.Children.Add(_preview);
        }

        private void UpdatePreview(Point end)
        {
            Rect rect = ComputeRect(end);
            if (_preview == null) return;

            InkCanvas.SetLeft(_preview, rect.Left);
            InkCanvas.SetTop(_preview, rect.Top);
            _preview.Width = rect.Width;
            _preview.Height = rect.Height;
            _preview.Visibility = rect.Width >= 4 && rect.Height >= 4
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RemovePreview()
        {
            try
            {
                if (_preview != null && _canvas != null && _canvas.Children.Contains(_preview))
                    _canvas.Children.Remove(_preview);
            }
            catch { }
            _preview = null;
            _previewOutline = null;
            _previewLabel = null;
        }

        // ===== 提交与收尾 =====

        private void Commit(Point end)
        {
            Rect rect = ComputeRect(end);
            if (rect.Width < MinElementSize || rect.Height < MinElementSize)
            {
                _host.ShowNotification("函数绘图：范围太小，已取消（重新点击工具可再来）");
                return;
            }

            try
            {
                var element = FunctionPlotRenderer.Build(_request, rect.Width, rect.Height,
                                                        _themeForeground, _dpiScale, isPreview: false);
                InkCanvas.SetLeft(element, rect.Left);
                InkCanvas.SetTop(element, rect.Top);
                _canvas.Children.Add(element);
                _host.CommitElementInsertHistory(element);
                _host.ShowNotification("函数图像已插入，可拖动/缩放/删除");
            }
            catch (Exception ex)
            {
                _host.Log("函数绘图：插入图像失败 " + ex.Message, PluginLogLevel.Error);
            }
        }

        private void ReleaseAllCaptures()
        {
            _releasingCaptures = true;
            try { _canvas.ReleaseMouseCapture(); } catch { }
            try { _canvas.ReleaseStylusCapture(); } catch { }
            try
            {
                if (_activeTouchDevice != null) _canvas.ReleaseTouchCapture(_activeTouchDevice);
            }
            catch { }
            _releasingCaptures = false;
        }

        private void Finish(bool cancelled)
        {
            if (_finished) return;
            _finished = true;

            try { ReleaseAllCaptures(); } catch { }
            try { RemovePreview(); } catch { }

            try
            {
                _canvas.PreviewStylusDown -= OnStylusDown;
                _canvas.PreviewStylusMove -= OnStylusMove;
                _canvas.PreviewStylusUp -= OnStylusUp;
                _canvas.PreviewTouchDown -= OnTouchDown;
                _canvas.PreviewTouchMove -= OnTouchMove;
                _canvas.PreviewTouchUp -= OnTouchUp;
                _canvas.PreviewMouseDown -= OnMouseDown;
                _canvas.PreviewMouseMove -= OnMouseMove;
                _canvas.PreviewMouseUp -= OnMouseUp;
                _canvas.LostMouseCapture -= OnCaptureLost;
                _canvas.LostStylusCapture -= OnCaptureLost;
                _canvas.TouchLeave -= OnCaptureLost;
            }
            catch { }

            try
            {
                if (_host.MainWindow != null)
                {
                    _host.MainWindow.PreviewKeyDown -= OnPreviewKeyDown;
                    _host.MainWindow.Deactivated -= OnWindowDeactivated;
                }
            }
            catch { }

            // 无条件恢复：放置期间宿主可能切换过工具，残留 None 会让画布写不上字
            try { _canvas.EditingMode = _previousEditingMode; } catch { }

            _source = PlacementSource.None;
            _activeTouchDevice = null;
            _suppressUntil = DateTime.Now.AddMilliseconds(SuppressMilliseconds);

            if (cancelled) _host.ShowNotification("函数绘图：已取消");
        }
    }
}
