using Ink_Canvas.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Ink_Canvas.Plugins.ToolbarReorder
{
    /// <summary>
    /// 工具栏按钮排序插件：
    /// - 通过宿主 IPluginHost 的 GetReorderableToolbarGroups / ApplyToolbarOrder / ResetToolbarPlacement API，
    ///   调整浮动工具栏 / 白板工具栏的功能按钮顺序。
    /// - 在插件工坊注册一个设置面板（上移 / 下移 / 恢复默认）。
    /// - 配置持久化到插件目录 toolbarconfig.json；禁用/卸载插件时恢复软件默认布局。
    /// </summary>
    public class ToolbarReorderPlugin : IPlugin
    {
        private IPluginHost _host;
        private string _pluginDirectory;

        // placement -> 用户自定义顺序（有序 id）
        private readonly Dictionary<string, List<string>> _order = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // placement -> 首次记录的默认顺序（跨重启有效，供「恢复默认」使用）
        private readonly Dictionary<string, List<string>> _defaults = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        public PluginManifest Manifest { get; } = new PluginManifest
        {
            Id = "ink-canvas.toolbar-reorder",
            Name = "工具栏按钮排序",
            Version = "1.0.0",
            Author = "muqiu",
            Description = "自定义浮动工具栏 / 白板工具栏的按钮顺序（在插件工坊点击「设置」展开配置）",
            EntryAssembly = "ToolbarReorderPlugin.dll",
            EntryClass = "Ink_Canvas.Plugins.ToolbarReorder.ToolbarReorderPlugin",
            MinHostVersion = "26.9.1"
        };

        public void Initialize(IPluginHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _pluginDirectory = FindPluginDirectory();

            LoadConfig();

            // 注册插件工坊里的折叠设置面板
            try { _host.RegisterSettingsPanel(BuildSettingsPanelBody); } catch { }

            // 窗口显示后再重放已保存顺序（Initialize 发生在 MainWindow 构造阶段，窗口尚未显示）
            try
            {
                var w = host.MainWindow;
                if (w.IsLoaded) ApplySavedOrder();
                else w.Loaded += (s, e) => ApplySavedOrder();
            }
            catch { }
        }

        public void Shutdown()
        {
            // 禁用/卸载本插件时，恢复软件默认布局（对所有可排序分组）
            try
            {
                var groups = _host?.GetReorderableToolbarGroups();
                if (groups != null)
                    foreach (var g in groups)
                        try { _host?.ResetToolbarPlacement(g.Placement); } catch { }
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
                        if (File.Exists(Path.Combine(dir, "ToolbarReorderPlugin.dll")) &&
                            File.Exists(Path.Combine(dir, "plugin.icplugin")))
                        {
                            return dir;
                        }
                    }
                }
            }
            catch { }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "toolbarreorder");
        }

        // ===== 配置持久化 =====

        private string ConfigPath => Path.Combine(_pluginDirectory, "toolbarconfig.json");

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var serializer = new JavaScriptSerializer();
                var list = serializer.Deserialize<List<ToolbarConfigEntry>>(File.ReadAllText(ConfigPath));
                _order.Clear();
                _defaults.Clear();
                if (list != null)
                {
                    foreach (var item in list)
                    {
                        if (string.IsNullOrWhiteSpace(item.Placement)) continue;
                        if (item.OrderedIds != null) _order[item.Placement] = item.OrderedIds;
                        if (item.DefaultIds != null) _defaults[item.Placement] = item.DefaultIds;
                    }
                }
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                var list = new List<ToolbarConfigEntry>();
                foreach (var kv in _order)
                {
                    _defaults.TryGetValue(kv.Key, out var def);
                    list.Add(new ToolbarConfigEntry
                    {
                        Placement = kv.Key,
                        OrderedIds = kv.Value.ToList(),
                        DefaultIds = def == null ? null : def.ToList()
                    });
                }
                if (!Directory.Exists(_pluginDirectory)) Directory.CreateDirectory(_pluginDirectory);
                File.WriteAllText(ConfigPath, serializer.Serialize(list));
            }
            catch { }
        }

        private class ToolbarConfigEntry
        {
            public string Placement { get; set; }
            public List<string> OrderedIds { get; set; }
            public List<string> DefaultIds { get; set; }
        }

        /// <summary>返回某分组的默认顺序（优先用首次记录的默认，否则回退到组内默认顺序）。</summary>
        private List<string> DefaultsFor(string placement, ToolbarReorderGroup group)
        {
            if (_defaults.TryGetValue(placement, out var def) && def != null && def.Count > 0)
                return def;
            return group.Items.OrderBy(i => i.DefaultIndex).Select(i => i.Id).ToList();
        }

        // ===== 应用保存顺序 =====

        private void ApplySavedOrder()
        {
            foreach (var kv in _order)
            {
                try { _host?.ApplyToolbarOrder(kv.Key, kv.Value); } catch { }
            }
        }

        // ===== 设置面板 UI =====

        private UIElement BuildSettingsPanelBody()
        {
            var root = new StackPanel { MinWidth = 360, MaxWidth = 780 };

            var header = new TextBlock
            {
                Text = "调整浮动工具栏 / 白板工具栏的功能按钮顺序：点击「上移 / 下移」调整，「恢复默认」还原。顺序会保存并随启动自动应用；禁用或卸载本插件后恢复默认布局。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TryBrush("SettingsPageAnnotationForeground", Brushes.Gray)
            };
            root.Children.Add(header);

            List<ToolbarReorderGroup> groups = new List<ToolbarReorderGroup>();
            try { groups = _host?.GetReorderableToolbarGroups().ToList() ?? new List<ToolbarReorderGroup>(); } catch { }
            _groups = groups;

            if (groups.Count == 0)
            {
                root.Children.Add(new TextBlock { Text = "（当前没有可排序的工具栏）", Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) });
                return root;
            }

            foreach (var group in groups)
                BuildGroup(root, group);

            return root;
        }

        private void BuildGroup(StackPanel root, ToolbarReorderGroup group)
        {
            var placement = group.Placement;

            // 该组当前顺序（首次按默认）；同时记录首次的默认顺序（跨重启有效）
            if (!_order.TryGetValue(placement, out var current) || current == null)
            {
                current = group.Items.OrderBy(i => i.DefaultIndex).Select(i => i.Id).ToList();
                _order[placement] = current;
                if (!_defaults.TryGetValue(placement, out var d) || d == null)
                    _defaults[placement] = current.ToList();
            }

            // 组标题 + 恢复默认
            var groupHeader = new DockPanel { Margin = new Thickness(0, 14, 0, 4) };
            var title = new TextBlock
            {
                Text = group.Name,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            };
            DockPanel.SetDock(title, Dock.Left);
            groupHeader.Children.Add(title);

            var resetBtn = MakeActionButton("恢复默认", () =>
            {
                var defaults = DefaultsFor(placement, group);
                try { _host?.ApplyToolbarOrder(placement, defaults); } catch { }
                _order[placement] = defaults.ToList();
                SaveConfig();
                RefreshGroup(root, group);
            });
            DockPanel.SetDock(resetBtn, Dock.Right);
            groupHeader.Children.Add(resetBtn);
            root.Children.Add(groupHeader);

            // 确认顺序与当前组一致（防止安装/升级后按钮集合变化）
            var groupIds = group.Items.Select(i => i.Id).ToHashSet();
            current = _order[placement].Where(id => groupIds.Contains(id)).ToList();
            // 补齐新增的按钮到末尾
            foreach (var item in group.Items.OrderBy(i => i.DefaultIndex))
                if (!current.Contains(item.Id)) current.Add(item.Id);
            _order[placement] = current;

            foreach (var item in group.Items)
                root.Children.Add(BuildRow(group, placement, item));
        }

        private Border BuildRow(ToolbarReorderGroup group, string placement, ToolbarReorderItem item)
        {
            var row = new Border
            {
                Margin = new Thickness(0, 2, 0, 2),
                Padding = new Thickness(2, 2, 2, 2),
                Tag = placement + "|" + item.Id
            };
            RenderRow(row, group, placement, item);
            return row;
        }

        private void RenderRow(Border row, ToolbarReorderGroup group, string placement, ToolbarReorderItem item)
        {
            var grid = new Grid();
            for (int i = 0; i < 4; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = item.DisplayName ?? item.Id,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(nameText, 0);
            grid.Children.Add(nameText);

            // 当前序号
            var positions = Positions(placement);
            int pos = positions.IndexOf(item.Id);

            var posText = new TextBlock
            {
                Text = pos >= 0 ? "第 " + (pos + 1) + " 位" : "",
                FontSize = 12,
                Margin = new Thickness(12, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.SteelBlue
            };
            Grid.SetColumn(posText, 1);
            grid.Children.Add(posText);

            var upBtn = MakeActionButton(pos <= 0 ? "-" : "上移", () =>
            {
                Move(placement, item.Id, -1);
                RebuildAfterMove(row);
            });
            Grid.SetColumn(upBtn, 2);
            grid.Children.Add(upBtn);

            var downBtn = MakeActionButton(pos < 0 || pos >= positions.Count - 1 ? "-" : "下移", () =>
            {
                Move(placement, item.Id, 1);
                RebuildAfterMove(row);
            });
            Grid.SetColumn(downBtn, 3);
            grid.Children.Add(downBtn);

            row.Child = grid;
        }

        /// <summary>移动后重建整个设置面板，确保所有行的「第 N 位」重新计算，避免位置重复。</summary>
        private void RebuildAfterMove(Border row)
        {
            try
            {
                if (row?.Parent is StackPanel sp) RebuildAll(sp);
            }
            catch { }
        }

        private void Move(string placement, string id, int delta)
        {
            var positions = Positions(placement);
            int idx = positions.IndexOf(id);
            if (idx < 0) return;
            int target = idx + delta;
            if (target < 0 || target >= positions.Count) return;

            positions.RemoveAt(idx);
            positions.Insert(target, id);

            // 立即应用到主界面并持久化
            bool ok = false;
            try { ok = _host?.ApplyToolbarOrder(placement, positions) ?? false; } catch { }
            if (ok) SaveConfig();
        }

        private List<string> Positions(string placement)
        {
            if (_order.TryGetValue(placement, out var list) && list != null) return list;
            return new List<string>();
        }

        private void RefreshGroup(StackPanel root, ToolbarReorderGroup group)
        {
            // 简单安全：全部重建一次
            RebuildAll(root);
        }

        private List<ToolbarReorderGroup> _groups = new List<ToolbarReorderGroup>();

        private void RebuildAll(StackPanel root)
        {
            try { _groups = _host?.GetReorderableToolbarGroups().ToList() ?? new List<ToolbarReorderGroup>(); } catch { _groups = new List<ToolbarReorderGroup>(); }
            root.Children.Clear();
            if (_groups.Count == 0)
            {
                root.Children.Add(new TextBlock { Text = "（当前没有可排序的工具栏）", Foreground = Brushes.Gray });
                return;
            }
            var header = new TextBlock
            {
                Text = "调整浮动工具栏 / 白板工具栏的功能按钮顺序：点击「上移 / 下移」调整，「恢复默认」还原。顺序会保存并随启动自动应用；禁用或卸载本插件后恢复默认布局。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TryBrush("SettingsPageAnnotationForeground", Brushes.Gray)
            };
            root.Children.Add(header);
            foreach (var g in _groups) BuildGroup(root, g);
        }

        private static Button MakeActionButton(string text, Action onClick)
        {
            var btn = new Button { Content = text, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private static Brush TryBrush(string resourceKey, Brush fallback)
        {
            try
            {
                var app = Application.Current;
                if (app != null && app.TryFindResource(resourceKey) is Brush b) return b;
            }
            catch { }
            return fallback;
        }
    }
}