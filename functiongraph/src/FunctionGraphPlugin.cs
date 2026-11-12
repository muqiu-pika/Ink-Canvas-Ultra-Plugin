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

        public void Initialize(IPluginHost host)
        {
            _host = host;
            Manifest = new PluginManifest
            {
                Id = "ink-canvas.function-graph",
                Name = "函数绘图",
                Version = "1.0.0"
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

            border.MouseUp += (s, e) => OpenGraphWindow();
            return border;
        }

        private void OpenGraphWindow()
        {
            try
            {
                var inkCanvas = _host.GetInkCanvas();
                if (inkCanvas == null)
                {
                    _host.ShowNotification("函数绘图：当前画布不可用");
                    return;
                }

                var window = new FunctionGraphWindow();
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
                _host.Log("函数绘图：打开窗口失败 " + ex.Message, PluginLogLevel.Error);
                _host.ShowNotification("函数绘图：打开窗口失败，详见日志");
            }
        }
    }
}
