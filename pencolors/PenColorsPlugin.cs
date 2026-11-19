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
    public class PenColorsPlugin : IPlugin
    {
        private IPluginHost _host;
        private string _pluginDirectory;

        /// <summary>槽位索引 -> 自定义颜色（仅记录被改过的槽位）</summary>
        private readonly Dictionary<int, Color> _overrides = new Dictionary<int, Color>();

        /// <summary>三种默认画笔颜色槽位（-1 = 跟随内置默认）：浮动栏/批注、白板亮底、白板暗底</summary>
        private int _desktopIndex = -1;
        private int _boardLightIndex = -1;
        private int _boardDarkIndex = -1;

        /// <summary>设置面板中每行槽位的可视元素（用于改色后即时刷新）</summary>
        private readonly List<SlotRowUi> _slotRows = new List<SlotRowUi>();

        /// <summary>三种默认笔色行的可视元素（色块预览 / 色值 / 恢复默认）</summary>
        private readonly List<DefaultRowUi> _defaultRows = new List<DefaultRowUi>();

        private TextBlock _currentColorText;
        private Border _canvasSwatch;
        private TextBlock _canvasValueText;
        private Button _canvasResetButton;

        private sealed class SlotRowUi
        {
            public int Index;
            public Border Swatch;
            public Button ResetButton;
            public TextBlock ValueText;
            public TextBlock InitialTag;
        }

        private sealed class DefaultRowUi
        {
            public Func<int> GetValue;
            public int BuiltinSlot;
            public string BuiltinName;
            public Border Swatch;
            public TextBlock ValueText;
            public Button ResetButton;
        }

        private sealed class ConfigModel
        {
            public Dictionary<string, string> Overrides { get; set; }
            /// <summary>旧版单值默认色（新版写入浮动栏默认色作兼容别名，读取时用于迁移）</summary>
            public int DefaultIndex { get; set; }
            public int? DesktopDefaultIndex { get; set; }
            public int? BoardLightDefaultIndex { get; set; }
            public int? BoardDarkDefaultIndex { get; set; }
        }

        public PluginManifest Manifest { get; } = new PluginManifest
        {
            Id = "ink-canvas.pen-colors",
            Name = "自定义颜色",
            Version = "1.3.2",
            Author = "muqiu",
            Description = "自定义「墨迹选项」里的画笔与荧光笔颜色，可分别设置浮动栏/批注、白板亮底、白板暗底三种默认笔色，并可设置画布底色。" +
                          "取色使用软件统一风格的颜色选择窗口（支持鼠标与触摸、HEX/RGB/预设色板）。" +
                          "不安装本插件时软件内置的 9 个普通色与 6 个荧光笔色照常可用，" +
                          "画布底色的预设与自定义也不依赖本插件；插件被禁用或卸载后画笔颜色恢复为内置默认。",
            EntryAssembly = "PenColorsPlugin.dll",
            EntryClass = "Ink_Canvas.Plugins.PenColors.PenColorsPlugin",
            MinHostVersion = "26.11.2"
        };

        private string ConfigPath
        {
            get { return Path.Combine(_pluginDirectory ?? string.Empty, "pencolors.json"); }
        }

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
                Action reapply = () => { ApplyOverrides(); ApplyDefaultIndex(); ReapplyCanvasBackgroundFromHost(); };
                if (mw != null && mw.IsLoaded) Application.Current.Dispatcher.BeginInvoke(reapply);
                else if (mw != null) mw.Loaded += (s, e) => Application.Current.Dispatcher.BeginInvoke(reapply);
                else Application.Current.Dispatcher.BeginInvoke(reapply);
            }
            catch { }

            try { _host.PenColorsChanged += Host_PenColorsChanged; } catch { }

            // 主程序侧的画布底色变化（弹窗里点预设、自定义取色、恢复默认）需要同步回落盘配置，
            // 否则重启后底色会退回上一次存盘的值
            try { _host.CanvasBackgroundChanged += Host_CanvasBackgroundChanged; } catch { }
        }

        public void Shutdown()
        {
            try { if (_host != null) _host.PenSlotEditRequested -= Host_PenSlotEditRequested; } catch { }
            try { if (_host != null) _host.PenColorsChanged -= Host_PenColorsChanged; } catch { }
            try { if (_host != null) _host.CanvasBackgroundChanged -= Host_CanvasBackgroundChanged; } catch { }

            // 恢复全部内置颜色：保证插件被禁用/卸载后调色板仍是软件自带的 9 + 6 色
            try { _host?.ResetPenColors(); } catch { }
            try { _host?.UnregisterSettingsPanel(); } catch { }

            _host = null;
        }

        /// <summary>
        /// 主程序侧的画布底色变化（画布弹窗里点预设、自定义取色、恢复默认）。
        /// 底色由主程序自己持久化到 Settings，插件只做面板刷新，不参与存储——
        /// 只有一处数据源，避免插件配置与主设置互相覆盖。
        /// </summary>
        private void Host_CanvasBackgroundChanged(object sender, EventArgs e)
        {
            try { Application.Current.Dispatcher.BeginInvoke((Action)RefreshPanelVisuals); } catch { }
        }

        /// <summary>
        /// 主窗口 Loaded 后让宿主把当前底色重新应用一次。
        /// 启动早期插件的开关/面板尚未就绪，重放一次能保证首屏底色与设置一致。
        /// </summary>
        private void ReapplyCanvasBackgroundFromHost()
        {
            try
            {
                if (_host == null) return;
                if (!_host.IsCanvasBackgroundCustomized) return;
                _host.SetCanvasBackgroundColor(_host.CanvasBackgroundColor);
            }
            catch { }
        }

        /// <summary>画布底色是否已被用户自定义（直接问宿主，插件不再自己存一份）</summary>
        private bool IsHostCanvasCustomized()
        {
            try { return _host != null && _host.IsCanvasBackgroundCustomized; } catch { }
            return false;
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

        private void ApplyOverrides()
        {
            if (_host == null) return;
            foreach (var pair in _overrides)
            {
                try { _host.SetPenColorSlot(pair.Key, pair.Value); } catch { }
            }
        }

        /// <summary>把三种默认色写入宿主（宿主版本过旧时属性不存在，异常静默降级）</summary>
        private void ApplyDefaultIndex()
        {
            if (_host == null) return;
            try { _host.DefaultDesktopPenColorIndex = _desktopIndex; } catch { }
            try { _host.DefaultBoardLightPenColorIndex = _boardLightIndex; } catch { }
            try { _host.DefaultBoardDarkPenColorIndex = _boardDarkIndex; } catch { }
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

        private UIElement BuildSettingsPanelBody()
        {
            _slotRows.Clear();
            _defaultRows.Clear();
            _currentColorText = null;
            _canvasSwatch = null;
            _canvasValueText = null;
            _canvasResetButton = null;

            var root = new StackPanel { MinWidth = 360, MaxWidth = 780 };

            root.Children.Add(MakeNote(
                "在「墨迹选项」中鼠标右键点击、或触摸屏长按某个颜色色块，即可修改该颜色的显示与落笔颜色；" +
                "每行右侧的「选择颜色…」按钮也能直接打开取色器。" +
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

            var defaultRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            defaultRow.Children.Add(new TextBlock
            {
                Text = "默认画笔颜色：",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            root.Children.Add(defaultRow);

            // 三种默认色：浮动栏/批注、白板亮底、白板暗底（-1 = 跟随各自内置默认）
            // 最后一个参数是"跟随内置默认"时对应的内置色槽位（红=1 / 黑=0 / 白=5）
            root.Children.Add(BuildDefaultColorRow(
                "浮动栏/批注", "红", 1,
                () => _desktopIndex, v => _desktopIndex = v));
            root.Children.Add(BuildDefaultColorRow(
                "白板亮底", "黑", 0,
                () => _boardLightIndex, v => _boardLightIndex = v));
            root.Children.Add(BuildDefaultColorRow(
                "白板暗底", "白", 5,
                () => _boardDarkIndex, v => _boardDarkIndex = v));

            root.Children.Add(new Separator { Margin = new Thickness(0, 12, 0, 6) });
            root.Children.Add(MakeSectionTitle("画布底色"));
            root.Children.Add(BuildCanvasBackgroundRow());

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

            try { Application.Current.Dispatcher.BeginInvoke((Action)RefreshPanelVisuals); } catch { }

            return root;
        }

        /// <summary>构建一行"默认色"（色块预览 + 色值 + 选择颜色 + 恢复内置默认）</summary>
        private UIElement BuildDefaultColorRow(string label, string builtinName, int builtinSlot,
            Func<int> getValue, Action<int> setValue)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 5, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            int initialSlot = getValue() >= 0 ? getValue() : builtinSlot;
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80)),
                Background = new SolidColorBrush(ResolveSlotColor(initialSlot)),
                VerticalAlignment = VerticalAlignment.Center
            };

            var labelText = new TextBlock
            {
                Text = label + "：",
                Width = 92,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 4, 0)
            };

            var valueText = new TextBlock
            {
                Text = DefaultColorText(getValue(), builtinName),
                Width = 76,
                FontSize = 12,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var pickButton = new Button
            {
                Content = "选择颜色…",
                Padding = new Thickness(10, 3, 10, 3),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "为「" + label + "」挑选任意颜色（会写入对应色块并设为该项默认笔色）"
            };
            pickButton.Click += (s, e) => OnPickDefaultColor(label, getValue, setValue, builtinSlot);

            var resetButton = new Button
            {
                Content = "恢复默认",
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(6, 0, 0, 0),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = getValue() >= 0
            };
            resetButton.Click += (s, e) => OnResetDefaultColor(getValue, setValue);

            row.Children.Add(swatch);
            row.Children.Add(labelText);
            row.Children.Add(valueText);
            row.Children.Add(pickButton);
            row.Children.Add(resetButton);

            _defaultRows.Add(new DefaultRowUi
            {
                GetValue = getValue,
                BuiltinSlot = builtinSlot,
                BuiltinName = builtinName,
                Swatch = swatch,
                ValueText = valueText,
                ResetButton = resetButton
            });
            return row;
        }

        /// <summary>默认色行的色值文案：跟随内置时显示内置色名，否则显示实际色值</summary>
        private string DefaultColorText(int index, string builtinName)
        {
            return index < 0 ? "内置（" + builtinName + "）" : ColorToHex(ResolveSlotColor(index));
        }

        /// <summary>恢复某项默认笔色为内置默认（并撤销取色时写入的色块覆盖色）</summary>
        private void OnResetDefaultColor(Func<int> getValue, Action<int> setValue)
        {
            try
            {
                int target = getValue();
                if (target >= 0 && _overrides.Remove(target))
                {
                    try { _host?.ResetPenColorSlot(target); } catch { }
                }
                setValue(-1);
                SaveConfig();
                ApplyDefaultIndex();
                RefreshPanelVisuals();
            }
            catch (Exception ex)
            {
                try { _host?.Log("自定义颜色: 恢复默认笔色失败 " + ex, PluginLogLevel.Warning); } catch { }
            }
        }

        /// <summary>
        /// 为某一项默认笔色直接取色。宿主只支持"默认色 = 某个色块"，
        /// 因此取色后写入该色块的覆盖色，并把这一项默认色指向该色块。
        /// </summary>
        private void OnPickDefaultColor(string label, Func<int> getValue, Action<int> setValue, int builtinSlot)
        {
            try
            {
                int target = getValue();
                if (target < 0) target = builtinSlot;

                Color current = ResolveSlotColor(target);
                Color? picked = null;
                bool hostPickerUsable = true;
                try
                {
                    if (_host != null)
                    {
                        picked = _host.PickColor(
                            current,
                            "默认笔迹颜色 · " + label,
                            "选择「" + label + "」的默认笔迹颜色。该颜色会写入「" + GetSlotName(target) +
                            "」色块，并把这一项的默认笔色设为它。");
                    }
                }
                catch (Exception ex)
                {
                    hostPickerUsable = false;
                    try { _host?.Log("自定义颜色: 默认笔色取色器不可用，回退系统取色器 | " + ex.Message, PluginLogLevel.Info); } catch { }
                }

                if (!hostPickerUsable) picked = PickWithSystemDialog(current);
                if (!picked.HasValue) return;

                Color chosen = picked.Value;
                _overrides[target] = chosen;
                SaveConfig();

                bool ok = false;
                try { ok = _host != null && _host.SetPenColorSlot(target, chosen); } catch { }
                if (!ok)
                {
                    try { _host?.ShowNotification("主程序版本过低，无法应用默认笔色（需要 26.11.2 及以上）"); } catch { }
                }

                setValue(target);
                SaveConfig();
                ApplyDefaultIndex();
                RefreshPanelVisuals();
            }
            catch (Exception ex)
            {
                try { _host?.Log("自定义颜色: 设置默认笔色失败 " + ex, PluginLogLevel.Warning); } catch { }
                try { _host?.ShowNotification("设置默认笔色失败：" + ex.Message); } catch { }
            }
        }

        private UIElement BuildCanvasBackgroundRow()
        {
            var wrap = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            _canvasSwatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x80, 0x80, 0x80)),
                Background = new SolidColorBrush(ResolveCanvasBackground()),
                VerticalAlignment = VerticalAlignment.Center
            };

            var label = new TextBlock
            {
                Text = "画布",
                Width = 72,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 4, 0)
            };

            _canvasValueText = new TextBlock
            {
                Text = CanvasBackgroundText(),
                Width = 66,
                FontSize = 12,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var pickButton = new Button
            {
                Content = "选择颜色…",
                Padding = new Thickness(10, 3, 10, 3),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            pickButton.Click += (s, e) => OnPickCanvasBackground();

            _canvasResetButton = new Button
            {
                Content = "恢复默认",
                Padding = new Thickness(10, 3, 10, 3),
                FontSize = 12,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = IsHostCanvasCustomized()
            };
            _canvasResetButton.Click += (s, e) => OnResetCanvasBackground();

            row.Children.Add(_canvasSwatch);
            row.Children.Add(label);
            row.Children.Add(_canvasValueText);
            row.Children.Add(pickButton);
            row.Children.Add(_canvasResetButton);

            wrap.Children.Add(row);
            wrap.Children.Add(MakeNote(
                "画布底色会同时影响方格纸网格线颜色、界面亮暗与默认笔色（后两者按底色亮度自动适配）。" +
                "白板栏「画布」按钮弹出的面板里也能直接选底色。"));
            return wrap;
        }

        private Color ResolveCanvasBackground()
        {
            try
            {
                if (_host != null) return _host.CanvasBackgroundColor;
            }
            catch { }
            return Color.FromRgb(0xF2, 0xF2, 0xF2);
        }

        private string CanvasBackgroundText()
        {
            return IsHostCanvasCustomized() ? ColorToHex(ResolveCanvasBackground()) : "默认";
        }

        private void OnPickCanvasBackground()
        {
            try
            {
                Color current = ResolveCanvasBackground();
                Color? picked = null;
                bool hostPickerUsable = true;
                try
                {
                    if (_host != null)
                    {
                        picked = _host.PickColor(
                            current,
                            "画布颜色",
                            "选择画布底色。网格线、界面明暗与默认笔色会跟随底色亮度自动适配。");
                    }
                }
                catch (Exception ex)
                {
                    hostPickerUsable = false;
                    try { _host?.Log("自定义颜色: 画布底色取色器不可用，回退系统取色器 | " + ex.Message, PluginLogLevel.Info); } catch { }
                }

                if (!hostPickerUsable) picked = PickWithSystemDialog(current);
                if (!picked.HasValue) return;

                // 交给宿主应用与持久化，插件不另存一份
                try { _host?.SetCanvasBackgroundColor(picked.Value); } catch { }
                RefreshPanelVisuals();
            }
            catch (Exception ex)
            {
                try { _host?.Log("自定义颜色: 设置画布底色失败 " + ex, PluginLogLevel.Warning); } catch { }
                try { _host?.ShowNotification("设置画布底色失败：" + ex.Message); } catch { }
            }
        }

        private void OnResetCanvasBackground()
        {
            try { _host?.ResetCanvasBackgroundColor(); } catch { }
            RefreshPanelVisuals();
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

            var pickButton = new Button
            {
                Content = "选择颜色…",
                Padding = new Thickness(10, 3, 10, 3),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            pickButton.Click += (s, e) => OnPickColor(slot.Index);

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
            row.Children.Add(pickButton);
            row.Children.Add(resetButton);

            _slotRows.Add(new SlotRowUi { Index = slot.Index, Swatch = swatch, ResetButton = resetButton, ValueText = valueText, InitialTag = initialTag });
            return row;
        }

        private void OnPickColor(int index)
        {
            // 入口日志：设备上反馈"右键没弹出调色框"时，先看这里有没有出现——
            // 没出现=事件没到插件（宿主版本/手势挂载问题）；出现了=取色器本身打开失败
            try { _host?.Log($"自定义颜色: 打开取色器 slot={index}", PluginLogLevel.Info); } catch { }

            try
            {
                Color current = ResolveSlotColor(index);
                string slotName = GetSlotName(index);

                Color? picked = null;
                bool hostPickerUsable = true;
                try
                {
                    // 软件统一风格的取色窗口：Mica + 圆角卡片 + Fluent 图标，鼠标/触摸均支持，
                    // 与截图选项框、设置页保持同一套视觉；不再弹 Win95 风格的系统调色板
                    if (_host != null)
                    {
                        picked = _host.PickColor(
                            current,
                            "画笔颜色 · " + slotName,
                            "自定义「" + slotName + "」的显示颜色与落笔颜色。亮色/暗色主题下同时生效。");
                    }
                }
                catch (Exception ex)
                {
                    // 旧版宿主接口上没有 PickColor（运行时 MissingMethodException）：
                    // 记录后回退到系统取色器，保证老版本软件上功能不至于完全失效
                    hostPickerUsable = false;
                    picked = null;
                    try { _host?.Log("自定义颜色: 宿主取色窗口不可用，回退系统取色器 | " + ex.Message, PluginLogLevel.Info); } catch { }
                }

                if (!hostPickerUsable) picked = PickWithSystemDialog(current);

                // null = 用户取消（含旧宿主下系统对话框的取消），直接结束
                if (!picked.HasValue) return;

                Color chosen = picked.Value;
                _overrides[index] = chosen;
                SaveConfig();

                bool ok = false;
                try { ok = _host != null && _host.SetPenColorSlot(index, chosen); } catch { }
                if (!ok)
                {
                    try { _host?.ShowNotification("主程序版本过低，无法应用自定义颜色（需要 26.11.1 及以上）"); } catch { }
                }

                RefreshPanelVisuals();
            }
            catch (Exception ex)
            {
                // 失败必须可见：之前只写日志，用户看到的就是"点了没反应"
                try { _host?.Log("自定义颜色: 取色失败 " + ex, PluginLogLevel.Warning); } catch { }
                try { _host?.ShowNotification("取色失败：" + ex.Message); } catch { }
            }
        }

        /// <summary>旧版宿主兜底：系统取色对话框（仅在宿主不支持 PickColor 时使用）</summary>
        private Color? PickWithSystemDialog(Color current)
        {
            try
            {
                using (var dialog = new WinForms.ColorDialog())
                {
                    dialog.FullOpen = true;
                    dialog.AnyColor = true;
                    dialog.Color = Drawing.Color.FromArgb(current.R, current.G, current.B);
                    // 必须给对话框指定宿主主窗口为 owner：浮动栏是 Topmost 窗口，
                    // 无 owner 的系统取色器会开到它后面，用户看到的就是"右键/长按无反应"
                    var owner = GetMainWindowHandle();
                    WinForms.DialogResult result = owner != IntPtr.Zero
                        ? dialog.ShowDialog(new WindowWrapper(owner))
                        : dialog.ShowDialog();
                    if (result != WinForms.DialogResult.OK) return null;

                    return Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
                }
            }
            catch (Exception ex)
            {
                try { _host?.Log("自定义颜色: 系统取色器打开失败 " + ex, PluginLogLevel.Warning); } catch { }
                try { _host?.ShowNotification("取色器打开失败：" + ex.Message); } catch { }
                return null;
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

                if (_canvasSwatch != null)
                {
                    var canvasBrush = new SolidColorBrush(ResolveCanvasBackground());
                    if (canvasBrush.CanFreeze) canvasBrush.Freeze();
                    _canvasSwatch.Background = canvasBrush;
                }
                if (_canvasValueText != null)
                {
                    _canvasValueText.Text = CanvasBackgroundText();
                }
                if (_canvasResetButton != null)
                {
                    _canvasResetButton.IsEnabled = IsHostCanvasCustomized();
                }

                foreach (var row in _defaultRows)
                {
                    int index = row.GetValue();
                    bool following = index < 0;
                    int colorSlot = following ? row.BuiltinSlot : index;
                    if (row.Swatch != null)
                    {
                        var brush = new SolidColorBrush(ResolveSlotColor(colorSlot));
                        if (brush.CanFreeze) brush.Freeze();
                        row.Swatch.Background = brush;
                    }
                    if (row.ValueText != null)
                    {
                        row.ValueText.Text = DefaultColorText(index, row.BuiltinName);
                    }
                    if (row.ResetButton != null)
                    {
                        row.ResetButton.IsEnabled = !following;
                    }
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
                _desktopIndex = model.DesktopDefaultIndex ?? model.DefaultIndex;
                _boardLightIndex = model.BoardLightDefaultIndex ?? model.DefaultIndex;
                _boardDarkIndex = model.BoardDarkDefaultIndex ?? model.DefaultIndex;
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
                    // DefaultIndex 为旧版单值键：写入浮动栏默认色作兼容别名（旧版插件读取仍可用）
                    DefaultIndex = _desktopIndex,
                    DesktopDefaultIndex = _desktopIndex,
                    BoardLightDefaultIndex = _boardLightIndex,
                    BoardDarkDefaultIndex = _boardDarkIndex
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
