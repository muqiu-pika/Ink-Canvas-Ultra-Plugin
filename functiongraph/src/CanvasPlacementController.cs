using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ink_Canvas.Plugins;
namespace Ink_Canvas.Plugins.FunctionGraph
{
    // 宿主程序集的 Ink_Canvas 命名空间里有一个名为 Canvas 的设置类，
    // 在最内层命名空间声明别名以指向 WPF 的 Canvas（内层别名优先于外层命名空间成员）
    using Canvas = System.Windows.Controls.Canvas;

    /// <summary>
    /// 两段式画布放置控制器：
    /// ① 在画布上单击选中原点（坐标系中心，固定不动）；
    /// ② 按住拖动确定图像范围——被控制的是图像的右上角（等价于直接映射 (xMax, yMax) 落点），
    ///    元素为以原点为中心的对称矩形（不整体平移）。
    /// 拖动期间显示半透明预览，松手才真正插入并经 CommitElementInsertHistory 记一次撤销。
    /// 放置期间临时接管 inkCanvas 输入（Preview 隧道事件标记 Handled），结束后恢复原 EditingMode。
    /// </summary>
    internal sealed class CanvasPlacementController
    {
        private const double MinElementSize = 40;      // 元素最小尺寸（px），太小视为误触
        private const double PreviewOpacity = 0.55;

        private readonly IPluginHost _host;
        private readonly FunctionGraphRequest _request;

        private InkCanvas _canvas;
        private InkCanvasEditingMode _previousEditingMode;
        private bool _finished;

        private bool _originPlaced;
        private Point _origin;
        private bool _dragging;
        private Canvas _preview;

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
                if (fg is SolidColorBrush solid)
                {
                    _themeForeground = solid.Color;
                }
            }
            catch { }

            try
            {
                _dpiScale = Math.Max(1.0, VisualTreeHelper.GetDpi(_canvas).DpiScaleX);
            }
            catch { }

            _previousEditingMode = _canvas.EditingMode;
            _canvas.EditingMode = InkCanvasEditingMode.None;

            _canvas.PreviewMouseDown += OnMouseDown;
            _canvas.PreviewMouseMove += OnMouseMove;
            _canvas.PreviewMouseUp += OnMouseUp;
            _canvas.PreviewTouchDown += OnTouchDown;
            _canvas.PreviewTouchMove += OnTouchMove;
            _canvas.PreviewTouchUp += OnTouchUp;

            if (_host.MainWindow != null)
            {
                _host.MainWindow.PreviewKeyDown += OnPreviewKeyDown;
            }

            _host.ShowNotification("函数绘图：单击选中原点（Esc 取消）");
        }

        public void Cancel()
        {
            Finish(true);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Cancel();
            }
        }

        private Point GetPosition(MouseEventArgs e)
        {
            return e.GetPosition(_canvas);
        }

        private Point GetPosition(TouchEventArgs e)
        {
            return e.GetTouchPoint(_canvas).Position;
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e) { HandleDown(GetPosition(e), e); }
        private void OnMouseMove(object sender, MouseEventArgs e) { HandleMove(GetPosition(e), e); }
        private void OnMouseUp(object sender, MouseButtonEventArgs e) { HandleUp(GetPosition(e), e); }

        private void OnTouchDown(object sender, TouchEventArgs e) { HandleDown(GetPosition(e), e); }
        private void OnTouchMove(object sender, TouchEventArgs e) { HandleMove(GetPosition(e), e); }
        private void OnTouchUp(object sender, TouchEventArgs e) { HandleUp(GetPosition(e), e); }

        private void HandleDown(Point p, InputEventArgs e)
        {
            e.Handled = true;

            if (!_originPlaced)
            {
                _origin = p;
                _originPlaced = true;
                _host.ShowNotification("函数绘图：拖动确定图像范围（右上角）");
                return;
            }

            // 已有原点：本次按下开始拖动
            _dragging = true;
            UpdatePreview(p);
        }

        private void HandleMove(Point p, InputEventArgs e)
        {
            if (_dragging)
            {
                e.Handled = true;
                UpdatePreview(p);
            }
        }

        private void HandleUp(Point p, InputEventArgs e)
        {
            if (!_dragging) return;
            e.Handled = true;
            _dragging = false;
            Commit(p);
        }

        /// <summary>预览元素矩形：原点为中心、右上角跟随光标的对称矩形（负半轴镜像扩展）。</summary>
        private Rect ComputeRect(Point topRight)
        {
            double right = Math.Max(_origin.X, topRight.X);
            double top = Math.Min(_origin.Y, topRight.Y);
            double width = Math.Abs(topRight.X - _origin.X) * 2;
            double height = Math.Abs(_origin.Y - topRight.Y) * 2;
            double left = right - width;
            double topEdge = top;
            return new Rect(left, topEdge, Math.Max(width, 1), Math.Max(height, 1));
        }

        private void UpdatePreview(Point topRight)
        {
            RemovePreview();
            Rect rect = ComputeRect(topRight);
            if (rect.Width < 4 || rect.Height < 4) return;

            var request = new FunctionGraphRequest
            {
                ExpressionText = _request.ExpressionText,
                XMax = _request.XMax,
                YMax = _request.YMax,
                ShowTicks = false
            };
            var element = FunctionPlotRenderer.Build(request, rect.Width, rect.Height, _themeForeground, _dpiScale, isPreview: true);
            element.Opacity = PreviewOpacity;

            _preview = element as Canvas ?? new Canvas { Width = rect.Width, Height = rect.Height };
            _preview.Children.Add(element);
            Panel.SetZIndex(_preview, 9998);
            InkCanvas.SetLeft(_preview, rect.Left);
            InkCanvas.SetTop(_preview, rect.Top);
            _canvas.Children.Add(_preview);
        }

        private void RemovePreview()
        {
            if (_preview != null && _canvas.Children.Contains(_preview))
            {
                _canvas.Children.Remove(_preview);
            }
            _preview = null;
        }

        private void Commit(Point topRight)
        {
            RemovePreview();
            Rect rect = ComputeRect(topRight);
            if (rect.Width < MinElementSize || rect.Height < MinElementSize)
            {
                _host.ShowNotification("函数绘图：范围太小，已取消（重新点击工具可再来）");
                Finish(false);
                return;
            }

            try
            {
                var element = FunctionPlotRenderer.Build(_request, rect.Width, rect.Height, _themeForeground, _dpiScale, isPreview: false);
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

            Finish(false);
        }

        private void Finish(bool restoreMode)
        {
            if (_finished) return;
            _finished = true;

            try { RemovePreview(); } catch { }

            try
            {
                _canvas.PreviewMouseDown -= OnMouseDown;
                _canvas.PreviewMouseMove -= OnMouseMove;
                _canvas.PreviewMouseUp -= OnMouseUp;
                _canvas.PreviewTouchDown -= OnTouchDown;
                _canvas.PreviewTouchMove -= OnTouchMove;
                _canvas.PreviewTouchUp -= OnTouchUp;
            }
            catch { }

            try
            {
                if (_host.MainWindow != null)
                {
                    _host.MainWindow.PreviewKeyDown -= OnPreviewKeyDown;
                }
            }
            catch { }

            try
            {
                if (_canvas.EditingMode == InkCanvasEditingMode.None)
                {
                    _canvas.EditingMode = _previousEditingMode;
                }
            }
            catch { }

            if (restoreMode)
            {
                _host.ShowNotification("函数绘图：已取消");
            }
        }
    }
}
