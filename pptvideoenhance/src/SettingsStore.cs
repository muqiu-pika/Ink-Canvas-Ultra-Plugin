using System.IO;
using Newtonsoft.Json;

namespace Ink_Canvas.Plugins.PPTVideoEnhance
{
    /// <summary>
    /// 插件设置持久化（写入 PluginDirectory/settings.json）。
    /// 宿主当前版本未提供 IPluginSettings 工坊开关，故自行持久化，并支持手动编辑。
    /// </summary>
    internal sealed class SettingsStore
    {
        private readonly string _file;

        public bool Enabled { get; set; } = true;
        public int PollIntervalMs { get; set; } = 120;
        public double EnterMarginPx { get; set; } = 12;
        public bool OnlyInSlideShow { get; set; } = true;
        public bool ShowNotifications { get; set; } = true;
        public bool EnablePenButton { get; set; } = true;
        public bool SuppressSwipe { get; set; } = true;   // 鼠标模式下拦截视频区域外的触摸翻页

        public SettingsStore(string pluginDir)
        {
            _file = Path.Combine(pluginDir, "settings.json");
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(_file)) return;
                var s = JsonConvert.DeserializeObject<SettingsStore>(File.ReadAllText(_file));
                if (s == null) return;
                Enabled = s.Enabled;
                PollIntervalMs = Clamp(s.PollIntervalMs, 30, 1000);
                EnterMarginPx = Clamp(s.EnterMarginPx, 0, 200);
                OnlyInSlideShow = s.OnlyInSlideShow;
                ShowNotifications = s.ShowNotifications;
                EnablePenButton = s.EnablePenButton;
                SuppressSwipe = s.SuppressSwipe;
            }
            catch { }
        }

        public void Save()
        {
            try { File.WriteAllText(_file, JsonConvert.SerializeObject(this, Formatting.Indented)); }
            catch { }
        }

        private static int Clamp(int v, int min, int max) => v < min ? min : (v > max ? max : v);
        private static double Clamp(double v, double min, double max) => v < min ? min : (v > max ? max : v);
    }
}
