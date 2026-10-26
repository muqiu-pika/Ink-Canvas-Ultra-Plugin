using System;
using Ink_Canvas.Plugins;

namespace Ink_Canvas.Plugins.Example
{
    public class ExamplePlugin : IPlugin
    {
        public PluginManifest Manifest { get; private set; }

        private IPluginHost _host;

        public void Initialize(IPluginHost host)
        {
            _host = host;
            Manifest = new PluginManifest
            {
                Id = "ink-canvas.example",
                Name = "示例 plugin",
                Version = "1.0.0"
            };

            _host.Log($"ExamplePlugin 已初始化，目录: {_host.PluginDirectory}", PluginLogLevel.Event);

            _host.ApplicationExiting += OnExiting;
            _host.BoardModeChanged += OnBoardModeChanged;

            // 演示：调用主程序路由
            // _host.TriggerRoute("settings:plugin-workshop");
        }

        public void Shutdown()
        {
            _host?.Log("ExamplePlugin 已关闭", PluginLogLevel.Event);
        }

        private void OnExiting(object sender, EventArgs e)
        {
        }

        private void OnBoardModeChanged(object sender, BoardModeChangedEventArgs e)
        {
            _host?.Log($"白板模式变化: {e.NewMode}", PluginLogLevel.Trace);
        }
    }
}
