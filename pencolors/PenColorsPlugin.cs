using Ink_Canvas.Plugins;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Ink_Canvas.Plugins.PenColors
{
    /// <summary>
    /// 自定义画笔颜色插件：
    /// - 在插件工坊「设置」里修改「墨迹选项」中 9 个普通色 + 6 个荧光笔色的显示/落笔颜色；
    /// - 可指定启动时的默认画笔颜色（写入主设置，不装插件时该设置只影响内置色的初始选中）；
    /// - 颜色修改通过主程序 IPluginHost 的画笔颜色 API 应用（立即刷新色块、当前笔色、
    ///   「批注」按钮的颜色指示与激光笔颜色）；
    /// - 自定义色只存在于运行时：插件被禁用/卸载（Shutdown）时恢复全部内置颜色，
    ///   因此不装插件时内置的 9 + 6 个颜色始终可用。
    /// - 配置持久化在插件目录 pencolors.json（overrides + defaultIndex）。
    /// </summary>
    public class PenColorsPlugin : IPlugin
    {
        private IPluginHost _host;
        private string _pluginDirectory;

        /// <summary>槽位索引 -> 自定义颜色（仅记录被改过的槽位）</summary>
        private readonly Dictionary<int, Color> _overrides = new Dictionary<int, Color>();

        /// <summary>默认画笔颜色槽位（-1 = 跟随内置默认）</summary>
        private int _defaultIndex = -1;

        /// <summary>设置面板中每行槽位的可视元素（用于改色后即时刷新）</summary>
        private readonly List<SlotRowUi> _slotRows = new List<SlotRowUi>();

        private ComboBox _defaultCombo;
        private TextBlock _currentColorText;
        private bool _suppressUiEvents;

        private sealed class SlotRowUi
        {
            public int Index;
            public Border Swatch;
            public Button ResetButton;
            public TextBlock ValueText;
            public TextBlock InitialTag;
        }

        private sealed class ConfigModel
        {
            public Dictionary<string, string> Overrides { get; set; }
            public int DefaultIndex { get; set; }
        }

        public PluginManifest Manifest { get; } = new PluginManifest
        {
            Id = "ink-canvas.pen-colors",
            Name = "自定义画笔颜色",
            Version = "1.0.0",
            Author = "muqiu",
            Description = "自定义「墨迹选项」里的画笔与荧光笔颜色，并可指定启动时的默认画笔颜色。" +
                          "不安装本插件时软件内置的 9 个普通色与 6 个荧光笔色照常可用；" +
                          "插件被禁用或卸载后颜色自动恢复为内置默认。",
            EntryAssembly = "PenColorsPlugin.dll",
            EntryClass = "Ink_Canvas.Plugins.PenColors.PenColorsPlugin",
            MinHostVersion = "26.10.6"
        };

        private string ConfigPath
        {
            get { return Path.Combine(_pluginDirectory ?? string.Empty, "pencolors.json"); }
        }

        // ===== 生命周期 =====

        public void Initialize(IPluginHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _pluginDirectory = string.IsNullOrEmpty(host.PluginDirectory) ? FindPluginDirectory() : host.PluginDirectory;

            LoadConfig();
            ApplyOverrides();
            ApplyDefaultIndex();

            try { _host.RegisterSettingsPanel(BuildSettingsPanelBody); } catch { }

            // 「墨迹选项」色块交互：鼠标右键 / 触摸长按某色块 → 弹取色器修改该槽位颜色
            try
            {
                _host.PenSlotEditRequested += Host_PenSlotEditRequested;
                try { _host.Log("自定义画笔颜色: 已订阅色块改色事件（右键/长按）", PluginLogLevel.Info); } catch { }
            }
            catch (Exception ex)
            {
                // 宿主版本过旧时接口上没有该事件：记日志而不是静默失败，便于排查“右键/长按无反应”
                try { _host.Log("自定义画笔颜色: 订阅色块改色事件失败（需要主程序 26.10.6 及以上）| " + ex.Message, PluginLogLevel.Warning); } catch { }
            }

            // 宿主主窗口加载完成后再应用一次：Initialize 发生在主窗口构造阶段，
            // 此刻浮动栏/调色板控件可能尚未完成首轮刷新，再跑一遍可保证自定义色在首屏就生效。
            try
            {
                var mw = host.MainWindow;
                Action reapply = () => { ApplyOverrides(); ApplyDefaultIndex(); };
                if (mw != null && mw.IsLoaded) Application.Current.Dispatcher.BeginInvoke(reapply);
                else if (mw != null) mw.Loaded += (s, e) => Application.Current.Dispatcher.BeginInvoke(reapply);
                else Application.Current.Dispatcher.BeginInvoke(reapply);
            }
            catch { }

            try { _host.PenColorsChanged += Host_PenColorsChanged; } catch { }
        }

        public void Shutdown()
        {
            try { if (_host != null) _host.PenSlotEditRequested -= Host_PenSlotEditRequested; } catch { }
            try { if (_host != null) _host.PenColorsChanged -= Host_PenColorsChanged; } catch { }

            // 恢复全部内置颜色：保证插件被禁用/卸载后调色板仍是软件自带的 9 + 6 色
            try { _host?.ResetPenColors(); } catch { }
            try { _host?.UnregisterSettingsPanel(); } catch { }

            _host = null;
        }

        private void Host_PenColorsChanged(object sender, EventArgs e)
        {
            // 主程序侧调色板变化（含主题切换/用户点色）时刷新面板显示
            try { Application.Current.Dispatcher.BeginInvoke((Action)RefreshPanelVisuals); } catch { }
        }

        /// <summary>
        /// 「墨迹选项」色块上请求修改颜色（鼠标右键 / 触摸长按）：弹取色器并应用。
        /// 事件由主程序 UI 线程抛出，直接弹模态框即可。
        /// </summary>
        private void Host_PenSlotEditRequested(object sender, PenSlotEditRequestedEventArgs e)
        {
            if (e == null) return;
            try
            {
                OnPickColor(e.SlotIndex);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                try { _host?.Log("自定义画笔颜色: 色块改色失败 " + ex.Message, PluginLogLevel.Warning); } catch { }
            }
        }

        private string FindPluginDirectory()
        {
            try
            {
                var asm = typeof(PenColorsPlugin).Assembly;
                string dir = Path.GetDirectoryName(asm.Location);
                if (!string.IsNullOrEmpty(dir)) return dir;
            }
            catch { }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        // ===== 应用颜色 =====

        private void ApplyOverrides()
        {
            if (_host == null) return;
            foreach (var pair in _overrides)
            {
                try { _host.SetPenColorSlot(pair.Key, pair.Value); } catch { }
            }
        }

        private void ApplyDefaultIndex()
        {
            try { if (_host != null) _host.DefaultPenColorIndex = _defaultIndex; } catch { }
        }

        /// <summary>当前某槽位生效颜色：本地覆盖优先，否则取主程序返回的内置色</summary>
        private Color ResolveSlotColor(int index)
        {
            if (_overrides.TryGetValue(index, out var custom)) return custom;
            try
            {
                var slots = _host?.GetPenColorSlots();
                if (slots != null)
                {
                    foreach (var slot in slots)
                    {
                        if (slot != null && slot.Index == index) return slot.Color;
                    }
                }
            }
            catch { }
            return Colors.Gray;
        }

        private bool IsCustomized(int index)
        {
            return _overrides.ContainsKey(index);
        }

        // ===== 设置面板 =====

        private UIElement BuildSettingsPanelBody()
        {
            _slotRows.Clear();
            _defaultCombo = null;
            _currentColorText = null;

            var root = new StackPanel { MinWidth = 360, MaxWidth = 780 };

            root.Children.Add(MakeNote(
                "在「墨迹选项」中鼠标右键点击、或触摸屏长按某个颜色色块，即可修改该颜色的显示与落笔颜色。" +
                "自定义颜色对亮色/暗色主题同时生效；「恢复」或「全部恢复默认」会回到软件内置配色。" +
                "安装本插件后，「墨迹选项」的深/浅色切换按钮会变为「设置颜色」入口，点击可直接打开本面板。" +
                "禁用或卸载本插件后，颜色自动恢复为内置默认。"));

            _currentColorText = new TextBlock
            {
                Margin = new Thickness(0, 10, 0, 0),
                FontSize = 12,
                Opacity = 0.75
            };
            root.Children.Add(_currentColorText);

            // 默认颜色
            var defaultRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            defaultRow.Children.Add(new TextBlock
            {
                Text = "启动时默认颜色：",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });

            _defaultCombo = new ComboBox { MinWidth = 220, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
            _defaultCombo.Items.Add("跟随内置默认（批注红 / 白板黑或白）");
            foreach (var slot in SafeGetSlots()) _defaultCombo.Items.Add(slot.Name);
            _defaultCombo.SelectedIndex = ComboIndexFromDefaultIndex(_defaultIndex);
            _defaultCombo.SelectionChanged += (s, e) =>
            {
                if (_suppressUiEvents) return;
                int newIndex = DefaultIndexFromComboIndex(_defaultCombo.SelectedIndex);
                _defaultIndex = newIndex;
                SaveConfig();
                ApplyDefaultIndex();
                RefreshPanelVisuals();
            };
            defaultRow.Children.Add(_defaultCombo);
            root.Children.Add(defaultRow);

            root.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 6) });
            root.Children.Add(MakeSectionTitle("画笔颜色"));

            var slotsAll = SafeGetSlots();
            foreach (var slot in slotsAll)
            {
                if (slot == null || slot.IsHighlighter) continue;
                root.Children.Add(BuildSlotRow(slot));
            }

            root.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 6) });
            root.Children.Add(MakeSectionTitle("荧光笔颜色"));

            foreach (var slot in slotsAll)
            {
                if (slot == null || !slot.IsHighlighter) continue;
                root.Children.Add(BuildSlotRow(slot));
            }

            var resetAll = new Button
            {
                Content = "全部恢复默认颜色",
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = 13
            };
            resetAll.Click += (s, e) =>
            {
                _overrides.Clear();
                SaveConfig();
                try { _host?.ResetPenColors(); } catch { }
                RefreshPanelVisuals();
                try { _host?.ShowNotification("画笔颜色已恢复为软件内置默认"); } catch { }
            };
            root.Children.Add(resetAll);

            // 打开面板时刷新一次（主题/当前色可能已变化）
            try { Application.Current.Dispatcher.BeginInvoke((Action)RefreshPanelVisuals); } catch { }

            return root;
        }

        private UIElement BuildSlotRow(PenColorSlotInfo slot)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 3),
                VerticalAlignment = VerticalAlignment.Center
            };

            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80)),
                Background = new SolidColorBrush(ResolveSlotColor(slot.Index)),
                VerticalAlignment = VerticalAlignment.Center
            };

            var label = new TextBlock
            {
                Text = slot.Name,
                Width = 72,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 4, 0)
            };

            // 「初始颜色」标注：仅在该槽位仍使用软件内置配色时显示，自定义后消失
            var initialTag = new TextBlock
            {
                Text = "初始颜色",
                FontSize = 11,
                Padding = new Thickness(4, 1, 4, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x59, 0x59, 0x59)),
                Visibility = IsCustomized(slot.Index) ? Visibility.Collapsed : Visibility.Visible
            };
            try
            {
                initialTag.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80));
            }
            catch { }

            // 当前色值（#RRGGBB）：自定义后随之变化，设置面板里即可看到实际生效的颜色
            var valueText = new TextBlock
            {
                Text = ColorToHex(ResolveSlotColor(slot.Index)),
                Width = 76,
                FontSize = 12,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var resetButton = new Button
            {
                Content = "恢复",
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(6, 0, 0, 0),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = IsCustomized(slot.Index)
            };
            resetButton.Click += (s, e) => OnResetSlot(slot.Index);

            row.Children.Add(swatch);
            row.Children.Add(label);
            row.Children.Add(initialTag);
            row.Children.Add(valueText);
            row.Children.Add(resetButton);

            _slotRows.Add(new SlotRowUi { Index = slot.Index, Swatch = swatch, ResetButton = resetButton, ValueText = valueText, InitialTag = initialTag });
            return row;
        }

        private void OnPickColor(int index)
        {
            // 入口日志：设备上反馈"右键没弹出调色框"时，先看这里有没有出现——
            // 没出现=事件没到插件（宿主版本/手势挂载问题）；出现了=取色器本身打开失败
            try { _host?.Log($"自定义画笔颜色: 打开取色器 slot={index}", PluginLogLevel.Info); } catch { }
            try
            {
                var current = ResolveSlotColor(index);
                using (var dialog = new WinForms.ColorDialog())
                {
                    dialog.FullOpen = true;
                    dialog.AnyColor = true;
                    dialog.Color = Drawing.Color.FromArgb(current.R, current.G, current.B);
                    // 必须给对话框指定宿主主窗口为 owner：浮动栏是 Topmost 窗口，
                    // 无 owner 的系统取色器会开到它后面，用户看到的就是“右键/长按无反应”
                    var owner = GetMainWindowHandle();
                    WinForms.DialogResult result = owner != IntPtr.Zero
                        ? dialog.ShowDialog(new WindowWrapper(owner))
                        : dialog.ShowDialog();
                    if (result != WinForms.DialogResult.OK) return;

                    var picked = Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
                    _overrides[index] = picked;
                    SaveConfig();

                    bool ok = false;
                    try { ok = _host != null && _host.SetPenColorSlot(index, picked); } catch { }
                    if (!ok)
                    {
                        try { _host?.ShowNotification("主程序版本过低，无法应用自定义颜色（需要 26.10.6 及以上）"); } catch { }
                    }

                    RefreshPanelVisuals();
                }
            }
            catch (Exception ex)
            {
                // 失败必须可见：部分精简系统/策略限制会让 ColorDialog 构造直接抛异常，
                // 之前只写日志，用户看到的就是"点了没反应"
                try { _host?.Log("自定义画笔颜色: 取色器打开失败 " + ex, PluginLogLevel.Warning); } catch { }
                try { _host?.ShowNotification("取色器打开失败：" + ex.Message); } catch { }
            }
        }

        /// <summary>宿主主窗口句柄（取色对话框的 owner），失败返回 Zero</summary>
        private IntPtr GetMainWindowHandle()
        {
            try
            {
                var mw = _host?.MainWindow;
                if (mw != null)
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(mw);
                    return helper.Handle;
                }
            }
            catch { }
            return IntPtr.Zero;
        }

        /// <summary>把 WPF 窗口句柄包装成 WinForms 的 IWin32Window，供 ShowDialog(owner) 使用</summary>
        private sealed class WindowWrapper : WinForms.IWin32Window
        {
            private readonly IntPtr _handle;
            public WindowWrapper(IntPtr handle) { _handle = handle; }
            public IntPtr Handle { get { return _handle; } }
        }

        private void OnResetSlot(int index)
        {
            if (_overrides.Remove(index)) SaveConfig();
            try { _host?.ResetPenColorSlot(index); } catch { }
            RefreshPanelVisuals();
        }

        /// <summary>刷新面板内的色块、恢复按钮、默认色下拉与"当前颜色"提示</summary>
        private void RefreshPanelVisuals()
        {
            try
            {
                foreach (var row in _slotRows)
                {
                    if (row.Swatch != null)
                    {
                        var brush = new SolidColorBrush(ResolveSlotColor(row.Index));
                        if (brush.CanFreeze) brush.Freeze();
                        row.Swatch.Background = brush;
                    }
                    if (row.ValueText != null)
                    {
                        row.ValueText.Text = ColorToHex(ResolveSlotColor(row.Index));
                    }
                    if (row.InitialTag != null)
                    {
                        // 自定义后隐藏「初始颜色」标注，恢复内置后重新显示
                        row.InitialTag.Visibility = IsCustomized(row.Index) ? Visibility.Collapsed : Visibility.Visible;
                    }
                    if (row.ResetButton != null)
                    {
                        row.ResetButton.IsEnabled = IsCustomized(row.Index);
                    }
                }

                if (_defaultCombo != null)
                {
                    _suppressUiEvents = true;
                    try { _defaultCombo.SelectedIndex = ComboIndexFromDefaultIndex(_defaultIndex); }
                    finally { _suppressUiEvents = false; }
                }

                if (_currentColorText != null)
                {
                    int current = -1;
                    try { current = _host != null ? _host.CurrentPenColorIndex : -1; } catch { }
                    string name = GetSlotName(current);
                    _currentColorText.Text = current >= 0
                        ? "当前画笔颜色：" + name
                        : "当前画笔颜色：未知";
                }
            }
            catch { }
        }

        // ===== 面板辅助 =====

        private List<PenColorSlotInfo> SafeGetSlots()
        {
            var fallback = new List<PenColorSlotInfo>();
            try
            {
                var slots = _host?.GetPenColorSlots();
                if (slots != null && slots.Count > 0) return slots.Where(s => s != null).ToList();
            }
            catch { }

            // 宿主版本过低 / API 不可用时给出内置槽位占位，保证面板结构完整
            int[] normal = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
            int[] highlighter = { 101, 102, 103, 104, 105, 106 };
            foreach (var i in normal) fallback.Add(new PenColorSlotInfo { Index = i, Name = GetSlotName(i), IsHighlighter = false });
            foreach (var i in highlighter) fallback.Add(new PenColorSlotInfo { Index = i, Name = GetSlotName(i), IsHighlighter = true });
            return fallback;
        }

        private static string GetSlotName(int index)
        {
            switch (index)
            {
                case 0: return "黑色";
                case 1: return "红色";
                case 2: return "绿色";
                case 3: return "蓝色";
                case 4: return "黄色";
                case 5: return "白色";
                case 6: return "粉色";
                case 7: return "青色";
                case 8: return "橙色";
                case 101: return "荧光笔红";
                case 102: return "荧光笔橙";
                case 103: return "荧光笔黄";
                case 104: return "荧光笔青";
                case 105: return "荧光笔蓝";
                case 106: return "荧光笔紫";
                default: return "颜色 " + index;
            }
        }

        private int ComboIndexFromDefaultIndex(int defaultIndex)
        {
            if (defaultIndex < 0) return 0;
            var slots = SafeGetSlots();
            for (int i = 0; i < slots.Count; i++)
            {
                if (slots[i].Index == defaultIndex) return i + 1;
            }
            return 0;
        }

        private int DefaultIndexFromComboIndex(int comboIndex)
        {
            if (comboIndex <= 0) return -1;
            var slots = SafeGetSlots();
            int i = comboIndex - 1;
            return (i >= 0 && i < slots.Count) ? slots[i].Index : -1;
        }

        private static TextBlock MakeNote(string text)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Opacity = 0.75,
                Margin = new Thickness(0, 2, 0, 2)
            };
        }

        private static TextBlock MakeSectionTitle(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 2, 0, 4)
            };
        }

        // ===== 配置读写 =====

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                string json = File.ReadAllText(ConfigPath);
                if (string.IsNullOrWhiteSpace(json)) return;

                var serializer = new JavaScriptSerializer();
                var model = serializer.Deserialize<ConfigModel>(json);
                if (model == null) return;

                _overrides.Clear();
                if (model.Overrides != null)
                {
                    foreach (var pair in model.Overrides)
                    {
                        int index;
                        if (!int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out index)) continue;
                        Color color;
                        if (!TryParseColor(pair.Value, out color)) continue;
                        _overrides[index] = color;
                    }
                }
                _defaultIndex = model.DefaultIndex;
            }
            catch (Exception ex)
            {
                try { _host?.Log("自定义画笔颜色: 读取配置失败 " + ex.Message, PluginLogLevel.Warning); } catch { }
            }
        }

        private void SaveConfig()
        {
            try
            {
                var model = new ConfigModel
                {
                    Overrides = _overrides.ToDictionary(
                        pair => pair.Key.ToString(CultureInfo.InvariantCulture),
                        pair => ColorToHex(pair.Value)),
                    DefaultIndex = _defaultIndex
                };

                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(model);
                if (!string.IsNullOrEmpty(_pluginDirectory) && !Directory.Exists(_pluginDirectory))
                {
                    Directory.CreateDirectory(_pluginDirectory);
                }
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                try { _host?.Log("自定义画笔颜色: 保存配置失败 " + ex.Message, PluginLogLevel.Warning); } catch { }
            }
        }

        private static string ColorToHex(Color color)
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
        }

        private static bool TryParseColor(string text, out Color color)
        {
            color = Colors.Black;
            if (string.IsNullOrWhiteSpace(text)) return false;

            string value = text.Trim().TrimStart('#');
            try
            {
                if (value.Length == 6)
                {
                    byte r = byte.Parse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    byte g = byte.Parse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    byte b = byte.Parse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    color = Color.FromRgb(r, g, b);
                    return true;
                }
                if (value.Length == 8)
                {
                    byte a = byte.Parse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    byte r = byte.Parse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    byte g = byte.Parse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    byte b = byte.Parse(value.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    color = Color.FromArgb(a, r, g, b);
                    return true;
                }
            }
            catch { }
            return false;
        }
    }
}
