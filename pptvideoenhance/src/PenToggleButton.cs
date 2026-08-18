using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ink_Canvas.Plugins.PPTVideoEnhance
{
    /// <summary>
    /// 视频区域左下角的透明切换按钮（笔 / 播放 图标随状态切换）。
    /// 设计为宿主可视化树中的一个普通元素（由插件添加到 Main_Grid 之上的覆盖 Canvas），
    /// 而非独立分层窗口——这样笔/触摸/鼠标输入都能正确路由到按钮，
    /// 避免独立 WS_EX_LAYERED 窗口在批注（笔）模式下收不到触控、点击穿透到下层 InkCanvas 的问题。
    /// </summary>
    internal sealed class PenToggleButton : Grid
    {
        // 整体逻辑像素尺寸（用户要求“再小一点”，由 44 缩到 34）
        public const double Size = 34;

        private readonly Border _frame;
        private readonly Path _icon;
        private readonly Action _onClick;
        private long _lastClickMs;

        // Material Design 24×24 图标路径
        private static readonly Geometry PenGeometry = Geometry.Parse(
            "M20.71 7.04c.39-.39.39-1.02 0-1.41l-2.34-2.34c-.39-.39-1.02-.39-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83zM3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25z");
        private static readonly Geometry PlayGeometry = Geometry.Parse("M8 5v14l11-7z");

        public PenToggleButton(Action onClick)
        {
            _onClick = onClick;

            Width = Size;
            Height = Size;
            HorizontalAlignment = HorizontalAlignment.Left;
            VerticalAlignment = VerticalAlignment.Top;
            // 整块区域可命中（透明但可点），便于鼠标/触摸/笔都能点到
            Background = Brushes.Transparent;
            SnapsToDevicePixels = true;

            _frame = new Border
            {
                Width = Size - 4,
                Height = Size - 4,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(205, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 66, 133, 244)),
                BorderThickness = new Thickness(1.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            _icon = new Path
            {
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                Fill = new SolidColorBrush(Color.FromArgb(255, 31, 41, 51)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Data = PenGeometry
            };

            _frame.Child = _icon;
            Children.Add(_frame);

            // 鼠标 / 触摸 / 笔 三种输入都触发；加 300ms 去抖，避免笔+鼠标合成事件造成重复切换
            PreviewMouseLeftButtonDown += OnPressed;
            TouchDown += OnPressed;
            StylusDown += OnPressed;
        }

        private void OnPressed(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            long now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;
            if (now - _lastClickMs < 300) return;
            _lastClickMs = now;
            _onClick?.Invoke();
        }

        /// <summary>locked=true 时显示"播放"图标（点击恢复自动）；false 时显示"笔"图标（点击锁定批注）。</summary>
        public void SetLocked(bool locked)
        {
            _icon.Data = locked ? PlayGeometry : PenGeometry;
        }
    }
}
