using Ink_Canvas.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ink_Canvas.Plugins.FrameToggle
{
    /// <summary>
    /// 窗口边框开关插件：
    /// - 控制 ICU 各功能窗口是否显示 Windows 原生边框（标题栏 + 可调整大小的外框）。
    /// - 通过 Win32 SetWindowLongPtr(GWL_STYLE) 开关 WS_CAPTION / WS_THICKFRAME，
    ///   而不是改 WPF 的 WindowStyle —— 后者会触发 HWND 重建导致闪烁，
    ///   且对 AllowsTransparency 窗口会直接抛异常。
    /// - 仅作用于「原本就带 Windows 边框」的窗口（以首次见到的原始样式为准），
    ///   因此全屏画板 MainWindow、截图遮罩这类 WindowStyle=None 的窗口天然不受影响。
    /// - 显式排除：设置（MW_Settings）、插件工坊（PluginWorkshopWindow）。
    /// - 隐藏边框后仍保留 WS_SYSMENU，可用 Alt+Space 调出「移动/大小/关闭」兜底。
    /// - 在插件工坊注册设置面板：总开关 + 每个窗口单独指定（跟随/隐藏/显示）。
    /// - 配置持久化到插件目录 frametoggle.json；禁用/卸载时恢复所有窗口原始边框。
    /// </summary>
    public class FrameTogglePlugin : IPlugin
    {
        private IPluginHost _host;
        private string _pluginDirectory;
        private DispatcherTimer _timer;

        // 总开关：true = 隐藏边框
        private bool _hideAll;
        // 每窗口覆盖：类型名 -> true 隐藏 / false 显示；不含该项表示跟随总开关
        private readonly Dictionary<string, bool> _perWindow =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // 运行时发现的、带 Windows 边框的窗口类型名（供设置面板列出）
        private readonly HashSet<string> _discovered =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 窗口原始 GWL_STYLE 快照（弱引用，窗口关闭后可被回收）
        private readonly ConditionalWeakTable<Window, StyleBox> _originalStyle =
            new ConditionalWeakTable<Window, StyleBox>();

        // 窗口 -> 命中的内容规则 Key（"" 表示未命中，缓存后不再重复遍历视觉树）
        private readonly ConditionalWeakTable<Window, RuleBox> _matchedRule =
            new ConditionalWeakTable<Window, RuleBox>();

        private sealed class StyleBox { public int Value; }
        private sealed class RuleBox { public string Value; }

        public PluginManifest Manifest { get; } = new PluginManifest
        {
            Id = "ink-canvas.frame-toggle",
            Name = "窗口边框开关",
            Version = "1.0.0",
            Author = "muqiu",
            Description = "控制 ICU 各功能窗口是否显示 Windows 原生边框（标题栏 + 外框）。在插件工坊「设置」中可一键总开关，也可为每个窗口单独指定；仅作用于原本带边框的窗口，不影响全屏画板与截图遮罩，隐藏后仍可用 Alt+Space 调出系统菜单。",
            EntryAssembly = "FrameTogglePlugin.dll",
            EntryClass = "Ink_Canvas.Plugins.FrameToggle.FrameTogglePlugin",
            MinHostVersion = "26.9.5"
        };

        // ===== Win32 =====

        private const int GWL_STYLE = -16;
        private const int WS_CAPTION = 0x00C00000;     // WS_BORDER | WS_DLGFRAME
        private const int WS_THICKFRAME = 0x00040000;  // 可调整大小的外框
        private const int WS_SYSMENU = 0x00080000;     // 保留：Alt+Space 系统菜单

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern IntPtr GetWindowLongPtr32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern IntPtr SetWindowLongPtr32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        private static long GetStyle(IntPtr hWnd)
        {
            return IntPtr.Size == 8
                ? GetWindowLongPtr64(hWnd, GWL_STYLE).ToInt64()
                : GetWindowLongPtr32(hWnd, GWL_STYLE).ToInt64();
        }

        private static void SetStyle(IntPtr hWnd, int style)
        {
            var v = new IntPtr(style);
            if (IntPtr.Size == 8) SetWindowLongPtr64(hWnd, GWL_STYLE, v);
            else SetWindowLongPtr32(hWnd, GWL_STYLE, v);
        }

        // ===== 窗口筛选 =====

        /// <summary>已知窗口类型的中文显示名（面板以此为基础列出）。</summary>
        private static readonly Dictionary<string, string> DisplayNames =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "CountdownTimerWindow", "倒计时" },
                { "RandWindow", "随机点名 / 抽奖" },
                { "NamesInputWindow", "名单导入" },
                { "ChangeLogWindow", "更新日志" },
                { "InitialSetupWindow", "初始化设置向导" },
                { "OperatingGuideWindow", "快捷键指南" },
                { "YesOrNoNotificationWindow", "确认提示框" },
                { "ScreenshotInsertOptionWindow", "截图插入选项" },
            };

        /// <summary>
        /// 按「窗口内容文本」细分的规则（优先于类型级设置）。
        /// 场景：PPT 的三个提示都不是独立窗口类，全是 YesOrNoNotificationWindow 的实例，
        /// 且窗口 Title 完全相同（都是「确认操作 - Ink Canvas Ultra」），
        /// 唯一能区分它们的是内容区那个 Name="Label" 的提示语，故按内容关键词匹配。
        /// </summary>
        private static readonly List<TitleRule> ContentRules = new List<TitleRule>
            {
                new TitleRule { Keyword = "是否立即跳转", Display = "PPT 自动跳转提示" },
                new TitleRule { Keyword = "自动播放", Display = "PPT 自动播放取消提示" },
                new TitleRule { Keyword = "隐藏的幻灯片", Display = "PPT 隐藏幻灯片提示" },
            };

        private sealed class TitleRule
        {
            public string Keyword;
            public string Display;
            /// <summary>设置项 key：用 @ 前缀与类型名区分开。</summary>
            public string Key => "@" + Keyword;
        }

        /// <summary>
        /// 显式排除的类型：
        /// - MW_Settings（设置）、PluginWorkshopWindow（插件工坊）：用户要求，必须保留边框以便操作。
        /// - MainWindow（全屏画板）、ScreenshotSelectorWindow（全屏截图遮罩）：
        ///   二者本身是 WindowStyle=None，会被「无原始边框」规则过滤掉，这里再显式兜底，
        ///   避免将来它们被改成带边框后误伤画板坐标/截图区域。
        /// </summary>
        private static readonly HashSet<string> ExcludedTypes =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "MW_Settings",
                "PluginWorkshopWindow",
                "MainWindow",
                "ScreenshotSelectorWindow",
            };

        /// <summary>
        /// 计算某窗口是否应隐藏边框。优先级：内容规则 &gt; 窗口类型 &gt; 总开关。
        /// </summary>
        private bool ResolveHide(Window w, string typeName)
        {
            bool v;
            string ruleKey = MatchRule(w);
            if (!string.IsNullOrEmpty(ruleKey) && _perWindow.TryGetValue(ruleKey, out v)) return v;
            if (_perWindow.TryGetValue(typeName, out v)) return v;
            return _hideAll;
        }

        /// <summary>
        /// 返回窗口命中的内容规则 Key，未命中返回 ""。
        /// 匹配文本 = 窗口 Title + 内容区 Name="Label" 的文本（即 YesOrNoNotificationWindow 的提示语）。
        /// 结果按窗口缓存，避免每次扫描都遍历视觉树。
        /// </summary>
        private string MatchRule(Window w)
        {
            RuleBox box;
            _matchedRule.TryGetValue(w, out box);
            if (box != null && box.Value != null) return box.Value;

            string text = w.Title ?? string.Empty;
            try
            {
                var label = FindVisualChild<TextBlock>(w, "Label");
                if (label != null) text = text + " " + (label.Text ?? string.Empty);
            }
            catch { }

            string hit = string.Empty;
            foreach (var rule in ContentRules)
            {
                if (text.IndexOf(rule.Keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    hit = rule.Key;
                    break;
                }
            }

            try
            {
                if (box == null) _matchedRule.Add(w, new RuleBox { Value = hit });
                else box.Value = hit;
            }
            catch { }

            return hit;
        }

        /// <summary>在视觉树中按 Name 查找指定类型的子元素。</summary>
        private static T FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            if (parent == null) return null;
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var typed = child as T;
                if (typed != null && typed.Name == name) return typed;
                var found = FindVisualChild<T>(child, name);
                if (found != null) return found;
            }
            return null;
        }

        // ===== 生命周期 =====

        public void Initialize(IPluginHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _pluginDirectory = FindPluginDirectory();

            LoadConfig();

            try { _host.RegisterSettingsPanel(BuildSettingsPanelBody); }
            catch { }

            // 宿主窗口显示后再开始扫描（Initialize 发生在 MainWindow 构造阶段，窗口尚未显示）
            try
            {
                var mw = host.MainWindow;
                Action start = () => { EnsureTimer(); ScanAndApply(); };
                if (mw != null && mw.IsLoaded)
                    Application.Current.Dispatcher.BeginInvoke(start);
                else if (mw != null)
                    mw.Loaded += (s, e) => Application.Current.Dispatcher.BeginInvoke(start);
                else
                    Application.Current.Dispatcher.BeginInvoke(start);
            }
            catch { }
        }

        public void Shutdown()
        {
            try { if (_timer != null) { _timer.Stop(); _timer = null; } }
            catch { }

            // 禁用/卸载时恢复当前已打开窗口的原始边框
            try
            {
                var app = Application.Current;
                if (app != null)
                    foreach (Window w in app.Windows)
                        try { Restore(w); }
                        catch { }
            }
            catch { }

            _host = null;
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
                        if (File.Exists(Path.Combine(dir, "FrameTogglePlugin.dll")) &&
                            File.Exists(Path.Combine(dir, "plugin.icplugin")))
                            return dir;
                    }
                }
            }
            catch { }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "frametoggle");
        }

        // ===== 扫描与应用 =====

        private void EnsureTimer()
        {
            if (_timer != null) return;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _timer.Tick += (s, e) => ScanAndApply();
            _timer.Start();
        }

        private void ScanAndApply()
        {
            var app = Application.Current;
            if (app == null) return;
            foreach (Window w in app.Windows)
            {
                try { ApplyTo(w); }
                catch { }
            }
        }

        private void ApplyTo(Window w)
        {
            if (w == null) return;

            var typeName = w.GetType().Name;
            if (ExcludedTypes.Contains(typeName)) return;

            // 用 PresentationSource 取 HWND：不会像 WindowInteropHelper.Handle 那样
            // 为尚未显示的窗口强制创建句柄（避免提前物化/副作用）。
            var source = PresentationSource.FromVisual(w) as HwndSource;
            if (source == null) return;
            var hwnd = source.Handle;
            if (hwnd == IntPtr.Zero) return;

            int cur = unchecked((int)GetStyle(hwnd));

            // 首次见到该窗口：记录原始样式
            StyleBox box;
            if (!_originalStyle.TryGetValue(w, out box))
            {
                box = new StyleBox { Value = cur };
                _originalStyle.Add(w, box);
            }
            int orig = box.Value;

            // 仅作用于原本就带 Windows 边框的窗口（全屏画板/截图遮罩等天然被排除）
            if ((orig & (WS_CAPTION | WS_THICKFRAME)) == 0) return;

            _discovered.Add(typeName);

            bool hide = ResolveHide(w, typeName);

            int target;
            if (hide)
            {
                // 移除标题栏与可调边框，其余位保持原样
                target = cur & ~(WS_CAPTION | WS_THICKFRAME);
            }
            else
            {
                // 按「原始样式」恢复边框位：只补回原本有的，不额外添加
                // （否则 NoResize 窗口会被误加上 WS_THICKFRAME 变成可拉伸）
                target = cur | (orig & (WS_CAPTION | WS_THICKFRAME));
            }
            target |= WS_SYSMENU;   // 兜底：即使无标题栏，Alt+Space 仍可移动/关闭

            if (target == cur) return;

            SetStyle(hwnd, target);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        /// <summary>把窗口恢复为首次记录的原始样式（用于关闭开关 / 卸载插件）。</summary>
        private void Restore(Window w)
        {
            if (w == null) return;

            StyleBox box;
            if (!_originalStyle.TryGetValue(w, out box)) return;

            var source = PresentationSource.FromVisual(w) as HwndSource;
            if (source == null) return;
            var hwnd = source.Handle;
            if (hwnd == IntPtr.Zero) return;

            int cur = unchecked((int)GetStyle(hwnd));
            // 恢复成原始样式，但保留系统菜单兜底
            int target = box.Value | WS_SYSMENU;
            if (target == cur) return;

            SetStyle(hwnd, target);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        // ===== 配置持久化 =====

        private string ConfigPath => Path.Combine(_pluginDirectory, "frametoggle.json");

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var data = new JavaScriptSerializer().Deserialize<ConfigData>(File.ReadAllText(ConfigPath));
                if (data == null) return;
                _hideAll = data.HideAll;
                _perWindow.Clear();
                if (data.PerWindow != null)
                    foreach (var kv in data.PerWindow)
                        if (!string.IsNullOrWhiteSpace(kv.Key)) _perWindow[kv.Key] = kv.Value;
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                var data = new ConfigData
                {
                    HideAll = _hideAll,
                    PerWindow = _perWindow.ToDictionary(k => k.Key, v => v.Value)
                };
                if (!Directory.Exists(_pluginDirectory)) Directory.CreateDirectory(_pluginDirectory);
                File.WriteAllText(ConfigPath, new JavaScriptSerializer().Serialize(data));
            }
            catch { }
        }

        private class ConfigData
        {
            public bool HideAll { get; set; }
            public Dictionary<string, bool> PerWindow { get; set; }
        }

        // ===== 设置面板 UI =====

        private UIElement BuildSettingsPanelBody()
        {
            var root = new StackPanel { MinWidth = 360, MaxWidth = 780 };

            root.Children.Add(MakeNote(
                "控制 ICU 各功能窗口是否显示 Windows 原生边框（标题栏 + 外框）。" +
                "仅作用于原本带边框的窗口，全屏画板与截图遮罩不受影响；" +
                "「设置」和「插件工坊」始终保留边框。隐藏边框后仍可按 Alt+Space 调出系统菜单进行移动/关闭。"));

            // 总开关
            var master = new CheckBox
            {
                Content = "隐藏所有受管窗口的 Windows 边框",
                IsChecked = _hideAll,
                Margin = new Thickness(0, 10, 0, 0),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            };
            master.Checked += (s, e) => { _hideAll = true; SaveConfig(); ScanAndApply(); };
            master.Unchecked += (s, e) => { _hideAll = false; SaveConfig(); ScanAndApply(); };
            root.Children.Add(master);

            root.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 6) });
            root.Children.Add(MakeNote("为每个窗口单独指定（优先于总开关），新打开的窗口会自动应用："));

            // 每窗口单独设置：预置类型 + 运行时发现的类型
            var names = new List<string>();
            foreach (var k in DisplayNames.Keys) names.Add(k);
            foreach (var k in _discovered) if (!names.Contains(k, StringComparer.OrdinalIgnoreCase)) names.Add(k);
            names.RemoveAll(n => ExcludedTypes.Contains(n));
            names.Sort(StringComparer.OrdinalIgnoreCase);

            if (names.Count == 0)
                root.Children.Add(MakeNote("（尚未发现可管理的窗口，打开任意功能窗口后会自动出现）"));

            foreach (var typeName in names)
            {
                string display;
                if (!DisplayNames.TryGetValue(typeName, out display) || string.IsNullOrEmpty(display))
                    display = typeName;
                root.Children.Add(BuildRow(display, typeName));
            }

            // 按提示内容细分（优先于类型级）
            root.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 6) });
            root.Children.Add(MakeNote(
                "按提示内容细分（优先于上面的类型设置）：PPT 的三个提示都是「确认提示框」的实例，" +
                "窗口标题也完全相同，只有在这里才能单独控制它们。"));
            foreach (var rule in ContentRules)
                root.Children.Add(BuildRow(rule.Display, rule.Key));

            // 立即刷新一次，让面板打开时就应用最新状态
            try { Application.Current.Dispatcher.BeginInvoke((Action)ScanAndApply); }
            catch { }

            return root;
        }

        /// <summary>构建一行「显示名 + 三态下拉」。key 为设置项标识（类型名，或 @关键字）。</summary>
        private UIElement BuildRow(string display, string key)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = display,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            var combo = new ComboBox
            {
                Width = 120,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            combo.Items.Add("跟随总开关");
            combo.Items.Add("隐藏边框");
            combo.Items.Add("显示边框");
            bool overridden;
            combo.SelectedIndex = _perWindow.TryGetValue(key, out overridden) ? (overridden ? 1 : 2) : 0;

            combo.SelectionChanged += (s, e) =>
            {
                switch (combo.SelectedIndex)
                {
                    case 1: _perWindow[key] = true; break;
                    case 2: _perWindow[key] = false; break;
                    default: _perWindow.Remove(key); break;
                }
                SaveConfig();
                ScanAndApply();
            };

            Grid.SetColumn(combo, 1);
            grid.Children.Add(combo);
            return grid;
        }

        private static TextBlock MakeNote(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TryBrush("SettingsPageAnnotationForeground", System.Windows.Media.Brushes.Gray)
            };
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
    }
}
