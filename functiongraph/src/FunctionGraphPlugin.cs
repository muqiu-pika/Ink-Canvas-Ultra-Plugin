using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Ink_Canvas.Plugins;

namespace Ink_Canvas.Plugins.FunctionGraph
{
    /// <summary>
    /// 函数绘图插件：向「图形」面板注册「函数」工具项（浮动栏 + 白板栏，卸载后消失）。
    /// 点击后弹出手写窗口（自动识别 + 可手动修改），确定后在画布上两段式放置
    /// （单击定原点 → 拖右上角定范围），经宿主扩展点插入函数图像元素。
    /// </summary>
    public class FunctionGraphPlugin : IPlugin
    {
        public PluginManifest Manifest { get; private set; }

        private IPluginHost _host;
        private CanvasPlacementController _placement;
        private DateTime _lastToolActivationUtc = DateTime.MinValue;
        private bool _toolPressing;
        private bool _toolTouchPressing;

        public void Initialize(IPluginHost host)
        {
            _host = host;
            Manifest = new PluginManifest
            {
                Id = "ink-canvas.function-graph",
                Name = "函数绘图",
                Version = "1.3.1"
            };

            try
            {
                _host.RegisterShapeTool("function-graph", 900, BuildToolButton);
                _host.Log("函数绘图插件已注册「图形」面板工具", PluginLogLevel.Event);
            }
            catch (Exception ex)
            {
                _host.Log("函数绘图插件注册图形面板工具失败: " + ex.Message, PluginLogLevel.Error);
            }
        }

        public void Shutdown()
        {
            try
            {
                if (_placement != null)
                {
                    _placement.Cancel();
                    _placement = null;
                }
            }
            catch { }

            try
            {
                _host?.UnregisterShapeTool("function-graph");
            }
            catch { }

            _host?.Log("函数绘图插件已关闭", PluginLogLevel.Event);
        }

        /// <summary>工具按钮：字体「ƒ」样式，前景色随宿主面板主题（继承宿主行级 TextElement.Foreground）。</summary>
        private UIElement BuildToolButton()
        {
            // 用固定 18x30 的 Viewbox 包裹，元素总宽约 20px——与所在行内空位同宽，
            // 避免把该行撑成最宽行（会把面板内容整体缩小、底部留空）
            var text = new TextBlock
            {
                Text = "ƒ",
                FontSize = 30,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
                // Foreground 不设置：由宿主工具行通过 TextElement.Foreground 继承主题色
            };

            var glyph = new Viewbox
            {
                Child = text,
                Width = 18,
                Height = 30
            };

            var border = new Border
            {
                Child = glyph,
                Margin = new Thickness(1, 6, 1, 6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = "函数：手写 f(x) 并在画布上放置函数图像"
            };

            // 与宿主自家图形按钮一致：必须先在同一元素上按下，松开才生效——
            // 否则图形面板展开/收起时，面板开关按钮的"松开"动作恰好落在
            // 本按钮上方（面板末行紧贴面板开关），会导致窗口"自动"打开
            border.MouseDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left) _toolPressing = true;
            };
            border.MouseUp += (s, e) =>
            {
                if (!_toolPressing || e.ChangedButton != MouseButton.Left) return;
                _toolPressing = false;
                OpenGraphWindow();
            };
            border.TouchDown += (s, e) => _toolTouchPressing = true;
            border.TouchUp += (s, e) =>
            {
                e.Handled = true;
                if (!_toolTouchPressing) return;
                _toolTouchPressing = false;
                OpenGraphWindow();
            };
            return border;
        }

        private void OpenGraphWindow()
        {
            // 一次触摸会同时产生 TouchUp 与提升出的 MouseUp；这里做短时抑制，
            // 否则同一个手势会把手写窗口打开两次（第一次关闭后立刻又弹一个）。
            DateTime now = DateTime.UtcNow;
            if (now - _lastToolActivationUtc < TimeSpan.FromMilliseconds(600)) return;
            _lastToolActivationUtc = now;

            try
            {
                // 上一次放置还没结束时再点工具，先安全收尾，避免两套输入钩子并存
                if (_placement != null)
                {
                    _placement.Cancel();
                    _placement = null;
                }

                var inkCanvas = _host.GetInkCanvas();
                if (inkCanvas == null)
                {
                    _host.ShowNotification("函数绘图：当前画布不可用");
                    return;
                }

                var window = new FunctionGraphWindow(_host);
                var owner = _host.MainWindow;
                if (owner != null && owner.IsLoaded)
                {
                    window.Owner = owner;
                }

                var confirmed = window.ShowDialog();
                if (confirmed == true && window.Result != null)
                {
                    _placement = new CanvasPlacementController(_host, window.Result);
                    _placement.Start();
                }
            }
            catch (Exception ex)
            {
                _host.Log("函数绘图：打开窗口失败 " + ex, PluginLogLevel.Error);
                // 窗口打不开多为插件程序集/资源损坏或未正确安装：通知里带上具体原因，
                // 并自动跳转到插件工坊（设置中），便于用户重装/更新本插件
                var reason = ex.Message;
                if (reason.Length > 120) reason = reason.Substring(0, 120) + "…";
                _host.ShowNotification("函数绘图：打开窗口失败（" + reason + "），正在打开插件工坊，可尝试重装本插件");
                try { _host.TriggerRoute("settings:plugin-workshop"); } catch { }
            }
        }
    }
}
