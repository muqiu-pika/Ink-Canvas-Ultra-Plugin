using Ink_Canvas.Plugins;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Web.Script.Serialization;

namespace Ink_Canvas.Plugins.HotkeyCustomizer
{
    /// <summary>
    /// 自定义快捷键插件：
    /// - 通过宿主 IPluginHost 的 Get/Set/Reset 快捷键 API，重绑定软件自带快捷键。
    /// - 在插件工坊注册一个可折叠的设置面板（设置图标展开/折叠），用户在此自定义按键。
    /// - 配置持久化到插件目录 hotkeyconfig.json；禁用/卸载插件时恢复为软件默认按键。
    /// </summary>
    public class HotkeyCustomizerPlugin : IPlugin
    {
        private IPluginHost _host;
        private string _pluginDirectory;

        // 动作 -> 用户自定义组合键（空表示禁用该动作；不存在表示使用默认）
        private readonly Dictionary<string, string> _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 设置面板捕获按键状态：采用低层键盘钩子(WH_KEYBOARD_LL)读取物理按键，
// 从而可靠捕获 Alt/Ctrl/Shift（这些修饰键的 WPF 事件常被系统吞掉）。
        private bool _capturing;
        private string _captureActionId;
        private Border _captureRow;
        // 本次会话累计的主（非修饰）键；一个会话只能有一个主键
        private Key _mainKey;
        private int _mainKeyCount;
        // 累计的修饰键掩码（Alt/Ctrl/Shift/，早松手也计入）
        private ModifierKeys _captureModifiers;
        private TextBlock _captureHint;

        // 低层键盘钩子
        private LowLevelKeyboardProc _hookProc;
        private IntPtr _hookHandle;
        private readonly HashSet<int> _downVks = new HashSet<int>(); // 当前仍按住的物理vk（去重，容纳自动重复）

        public PluginManifest Manifest { get; } = new PluginManifest
        {
            Id = "ink-canvas.hotkey-customizer",
            Name = "自定义快捷键",
            Version = "1.0.0",
            Author = "muqiu",
            Description = "自定义软件自带的快捷键（在插件工坊点击「设置」图标展开配置）",
            EntryAssembly = "HotkeyCustomizerPlugin.dll",
            EntryClass = "Ink_Canvas.Plugins.HotkeyCustomizer.HotkeyCustomizerPlugin",
            MinHostVersion = "8.0.2"
        };

        public void Initialize(IPluginHost host)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _pluginDirectory = FindPluginDirectory();

            // 加载并应用已保存的自定义按键
            LoadConfig();
            ApplyOverrides();

            // 注册插件工坊里的折叠设置面板
            try { _host.RegisterSettingsPanel(BuildSettingsPanelBody); } catch { }
        }

        public void Shutdown()
        {
            // 防御：卸载钩子并恢复被挂起的快捷键，避免一并失效
            UninstallHook();
            try { _host?.ResumeHotkeys(); } catch { }
            // 禁用/卸载本插件时，恢复软件默认按键
            try { _host?.ResetAllHotkeys(); } catch { }
            _host = null;
        }

        private string FindPluginDirectory()
        {
            try
            {
                string root = _host?.PluginDirectory;
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                {
                    // 插件通过 Assembly.Load(byte[]) 加载，Location 为空，按特征文件定位自身目录
                    foreach (var dir in Directory.GetDirectories(root))
                    {
                        if (File.Exists(Path.Combine(dir, "HotkeyCustomizerPlugin.dll")) &&
                            File.Exists(Path.Combine(dir, "plugin.icplugin")))
                        {
                            return dir;
                        }
                    }
                }
            }
            catch { }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "hotkeycustomize");
        }

        // ===== 配置持久化 =====

        private string ConfigPath => Path.Combine(_pluginDirectory, "hotkeyconfig.json");

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                var serializer = new JavaScriptSerializer();
                var list = serializer.Deserialize<List<HotkeyItem>>(File.ReadAllText(ConfigPath));
                _overrides.Clear();
                if (list != null)
                {
                    foreach (var item in list)
                        if (!string.IsNullOrWhiteSpace(item.Id))
                            _overrides[item.Id] = item.Combo ?? string.Empty;
                }
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                var list = _overrides.Select(kv => new HotkeyItem { Id = kv.Key, Combo = kv.Value }).ToList();
                File.WriteAllText(ConfigPath, serializer.Serialize(list));
            }
            catch { }
        }

        private void ApplyOverrides()
        {
            try
            {
                foreach (var kv in _overrides)
                {
                    try { _host?.SetHotkey(kv.Key, kv.Value); } catch { }
                }
            }
            catch { }
        }

        private class HotkeyItem
        {
            public string Id { get; set; }
            public string Combo { get; set; }
        }

        // ===== 设置面板 UI =====

        private UIElement BuildSettingsPanelBody()
        {
            var root = new StackPanel { MinWidth = 340, MaxWidth = 760, Focusable = true };

            // 顶部说明 + 全部恢复默认
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var headerText = new TextBlock
            {
                Text = "自定义软件自带快捷键：点击某一行「设置」，再按下新的组合键（需搭配 Ctrl / Shift / Alt 等修饰键）。「禁用」关闭该快捷键；禁用或卸载本插件后全部恢复默认。",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TryBrush("SettingsPageAnnotationForeground", Brushes.Gray)
            };
            DockPanel.SetDock(headerText, Dock.Left);
            header.Children.Add(headerText);

            var resetAllBtn = new Button
            {
                Content = "全部恢复默认",
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(10, 2, 10, 2)
            };
            resetAllBtn.Click += (s, e) =>
            {
                EndCaptureIfAny(); // 若正在捕获，先结束并恢复快捷键
                try { _host?.ResetAllHotkeys(); } catch { }
                _overrides.Clear();
                SaveConfig();
                RefreshAllRows(root);
                ShowCaptureHint("已全部恢复为默认按键");
            };
            DockPanel.SetDock(resetAllBtn, Dock.Right);
            header.Children.Add(resetAllBtn);
            root.Children.Add(header);

            // 捕获提示
            _captureHint = new TextBlock
            {
                Text = string.Empty,
                FontSize = 12,
                Foreground = Brushes.Orange,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(_captureHint);

            var actions = new List<HotkeyActionInfo>();
            try { actions = _host?.GetHotkeyActions().ToList() ?? new List<HotkeyActionInfo>(); } catch { }

            if (actions.Count == 0)
            {
                root.Children.Add(new TextBlock { Text = "（当前没有可自定义的快捷键动作）", Foreground = Brushes.Gray });
                return root;
            }

            foreach (var act in actions)
                root.Children.Add(BuildRow(act));

            return root;
        }

        private Border BuildRow(HotkeyActionInfo act)
        {
            var row = new Border
            {
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(2, 2, 2, 2),
                Tag = act.Id
            };
            RenderRow(row, act);
            return row;
        }

        private void RenderRow(Border row, HotkeyActionInfo act)
        {
            var grid = new Grid();
            for (int i = 0; i < 5; i++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            // 左：动作名 + 说明
            var infoPanel = new StackPanel { Orientation = Orientation.Vertical };
            infoPanel.Children.Add(new TextBlock { Text = act.Name ?? act.Id, FontSize = 13, FontWeight = FontWeights.SemiBold });
            if (!string.IsNullOrWhiteSpace(act.Description))
                infoPanel.Children.Add(new TextBlock { Text = act.Description, FontSize = 11, Foreground = Brushes.Gray });
            Grid.SetColumn(infoPanel, 0);
            grid.Children.Add(infoPanel);

            // 当前组合键
            string display;
            if (_overrides.TryGetValue(act.Id, out var userCombo))
            {
                display = string.IsNullOrWhiteSpace(userCombo) ? "已禁用  (已自定义)" : userCombo + "  (已自定义)";
            }
            else
            {
                display = string.IsNullOrWhiteSpace(act.Combo)
                    ? "已禁用"
                    : (string.Equals(act.Combo, act.DefaultCombo, StringComparison.Ordinal) ? act.Combo + "  (默认)" : act.Combo);
            }

            var comboText = new TextBlock
            {
                Text = display,
                FontSize = 13,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.SteelBlue
            };
            Grid.SetColumn(comboText, 1);
            grid.Children.Add(comboText);

            // 设置
            var setBtn = MakeActionButton("设置", () =>
            {
                BeginCapture(act.Id, row);
            });
            Grid.SetColumn(setBtn, 2);
            grid.Children.Add(setBtn);

            // 禁用
            var disableBtn = MakeActionButton("禁用", () =>
            {
                EndCaptureIfAny(); // 若正在捕获，先结束并恢复快捷键
                try { _host?.SetHotkey(act.Id, string.Empty); } catch { }
                _overrides[act.Id] = string.Empty;
                SaveConfig();
                ReloadRow(row, act.Id);
                ShowCaptureHint($"「{act.Name}」已禁用");
            });
            Grid.SetColumn(disableBtn, 3);
            grid.Children.Add(disableBtn);

            // 恢复默认
            var resetBtn = MakeActionButton("恢复默认", () =>
            {
                EndCaptureIfAny(); // 若正在捕获，先结束并恢复快捷键
                try { _host?.ResetHotkey(act.Id); } catch { }
                _overrides.Remove(act.Id);
                SaveConfig();
                ReloadRow(row, act.Id);
                ShowCaptureHint($"「{act.Name}」已恢复默认");
            });
            Grid.SetColumn(resetBtn, 4);
            grid.Children.Add(resetBtn);

            row.Child = grid;
        }

        private static Button MakeActionButton(string text, Action onClick)
        {
            var btn = new Button { Content = text, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
            btn.Click += (s, e) => onClick();
            return btn;
        }

        // ===== 低层键盘钩子：可靠捕获物理按键（含 Alt/Ctrl/Shift） =====

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;
        private const int VK_CONTROL = 0x11;
        private const int VK_SHIFT = 0x10;
        private const int VK_MENU = 0x12;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        // 部分键盘区分左右：LowLevel 钩子可能上报这些左/右独立 vk
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_CAPITAL = 0x14;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        /// <summary>开始捕获：安装低层键盘钩子并挂起宿主全部快捷键。</summary>
        private void BeginCapture(string actionId, Border row)
        {
            EndCaptureIfAny(); // 若上一轮仍在捕获，先结束并恢复快捷键
            _capturing = true;
            _captureActionId = actionId;
            _captureRow = row;
            _mainKey = Key.None;
            _mainKeyCount = 0;
            _captureModifiers = 0;
            _downVks.Clear();

            // 挂起全部快捷键：捕获期间按下与现有键冲突的键不会执行对应功能
            try { _host?.SuspendHotkeys(); } catch { }

            InstallHook();

            ShowCaptureHint($"按住「{CaptureName(actionId)}」的新组合键（需 2-3 个键）开始记录，全部松开后自动保存；Esc 取消...");
        }

        private string CaptureName(string actionId)
        {
            try { return _host?.GetHotkeyActions()?.FirstOrDefault(x => string.Equals(x.Id, actionId, StringComparison.OrdinalIgnoreCase))?.Name ?? actionId; } catch { }
            return actionId;
        }

        private void InstallHook()
        {
            try
            {
                if (_hookHandle != IntPtr.Zero) return;
                _hookProc = HookCallback;
                using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
                using (var curModule = curProcess.MainModule)
                {
                    _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
                        GetModuleHandle(curModule.ModuleName), 0);
                }
            }
            catch { }
        }

        private void UninstallHook()
        {
            try { if (_hookHandle != IntPtr.Zero) { UnhookWindowsHookEx(_hookHandle); _hookHandle = IntPtr.Zero; } }
            catch { }
            _hookProc = null;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && lParam != IntPtr.Zero)
            {
                int msg = wParam.ToInt32();
                bool isDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                bool isUp = (msg == WM_KEYUP || msg == WM_SYSKEYUP);
                if (isDown || isUp)
                {
                    var kbd = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                    OnHookKey((int)kbd.vkCode, isDown, isUp);
                }
            }
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private void OnHookKey(int vk, bool isDown, bool isUp)
        {
            if (!_capturing) return;

            // Esc 取消（单按，尚未记录任何主键）
            if (vk == VK_ESCAPE)
            {
                if (_mainKeyCount == 0 && _captureModifiers == 0)
                {
                    EndCaptureIfAny();
                    ShowCaptureHint("已取消设置。");
                    return;
                }
                return;
            }

            // 忽略 CapsLock 触发，避免误记
            if (vk == VK_CAPITAL) return;

            if (isDown)
            {
                _downVks.Add(vk); // 去重：自动重复的 KEYDOWN 不产生重复条目

                if (IsModifierVk(vk))
                {
                    // 修饰键直接计入掩码（Alt/Ctrl/Shift/Win），无需映射为某个 Key
                    _captureModifiers |= ModifierFromVk(vk);
                }
                else
                {
                    Key key = ToKey(vk);
                    if (_mainKey == Key.None) _mainKey = key;
                    if (key != Key.None && key != _mainKey) _mainKeyCount++;
                    else if (key == _mainKey) _mainKeyCount = 1;
                }
                UpdateCaptureHint();
            }
            else if (isUp)
            {
                _downVks.Remove(vk);

                // 直到没有任何物理按键被按住，本次记录才结束
                if (_downVks.Count == 0)
                    CommitCapture();
                else
                    UpdateCaptureHint();
            }
        }

        private static bool IsModifierVk(int vk)
        {
            switch (vk)
            {
                case VK_CONTROL:
                case VK_LCONTROL:
                case VK_RCONTROL:
                case VK_SHIFT:
                case VK_LSHIFT:
                case VK_RSHIFT:
                case VK_MENU:
                case VK_LMENU:
                case VK_RMENU:
                case VK_LWIN:
                case VK_RWIN:
                    return true;
                default:
                    return false;
            }
        }

        private static ModifierKeys ModifierFromVk(int vk)
        {
            switch (vk)
            {
                case VK_CONTROL:
                case VK_LCONTROL:
                case VK_RCONTROL:
                    return ModifierKeys.Control;
                case VK_SHIFT:
                case VK_LSHIFT:
                case VK_RSHIFT:
                    return ModifierKeys.Shift;
                case VK_MENU:
                case VK_LMENU:
                case VK_RMENU:
                    return ModifierKeys.Alt;
                case VK_LWIN:
                case VK_RWIN:
                    return ModifierKeys.Windows;
                default:
                    return 0;
            }
        }

        private static Key ToKey(int vk)
        {
            try { return KeyInterop.KeyFromVirtualKey(vk); } catch { return Key.None; }
        }

        private static ModifierKeys ModifierFromKey(Key key)
        {
            if (key == Key.LeftCtrl || key == Key.RightCtrl) return ModifierKeys.Control;
            if (key == Key.LeftShift || key == Key.RightShift) return ModifierKeys.Shift;
            if (key == Key.LeftAlt || key == Key.RightAlt) return ModifierKeys.Alt;
            if (key == Key.LWin || key == Key.RWin) return ModifierKeys.Windows;
            return 0;
        }

        private void UpdateCaptureHint()
        {
            if (_captureHint == null || !_capturing) return;
            if (_captureModifiers == 0 && _mainKey == Key.None)
            {
                ShowCaptureHint("按住组合键开始记录（需 2-3 个键；全部松开后自动保存）...");
                return;
            }
            ShowCaptureHint("正在记录：" + FormatHeldKeys() + "　——　继续按住更多键，全部松开后保存");
        }

        // 显示本次会话累计按下的组合（修饰键 + 主键），用于反馈
        private string FormatHeldKeys()
        {
            var parts = new List<string>();
            if ((_captureModifiers & ModifierKeys.Control) != 0 && !parts.Contains("Ctrl")) parts.Add("Ctrl");
            if ((_captureModifiers & ModifierKeys.Shift) != 0 && !parts.Contains("Shift")) parts.Add("Shift");
            if ((_captureModifiers & ModifierKeys.Alt) != 0 && !parts.Contains("Alt")) parts.Add("Alt");
            if ((_captureModifiers & ModifierKeys.Windows) != 0 && !parts.Contains("Win")) parts.Add("Win");
            if (_mainKey != Key.None) parts.Add(_mainKey.ToString());
            return parts.Count == 0 ? "…" : string.Join(" + ", parts);
        }

        private void CommitCapture()
        {
            _capturing = false;
            string actionId = _captureActionId;
            Border row = _captureRow;
            _captureRow = null;
            _captureActionId = null;

            UninstallHook();
            try { _host?.ResumeHotkeys(); } catch { } // 无论成败，先恢复挂起的快捷键

            // 自定义按键必须 2-3 个键：1 个主键 + 1-2 个修饰键
            if (_mainKeyCount > 1)
            {
                ShowCaptureHint("组合键只能有一个主按键（字母/数字/功能键），请重新设置。");
                return;
            }
            if (_mainKey == Key.None)
            {
                ShowCaptureHint("未记录到主按键（请按住至少一个字母/数字/功能键）。");
                return;
            }
            if (_captureModifiers == 0)
            {
                ShowCaptureHint("请搭配修饰键（Ctrl / Shift / Alt）并按住一个主键（共需 2-3 个键），例如 Alt + C。");
                return;
            }
            int modCount = CountModifiers(_captureModifiers);
            if (modCount + 1 < 2 || modCount + 1 > 3)
            {
                ShowCaptureHint("自定义按键必须 2-3 个键（修饰键 1-2 个 + 1 个主键），请重新设置。");
                return;
            }

            string combo = FormatCombo(_captureModifiers, _mainKey);

            // 冲突检测：与其他快捷键重复时提示，不写入。
            try
            {
                var conflicts = _host?.GetConflictingHotkeys(actionId, combo) ?? new List<HotkeyActionInfo>();
                if (conflicts.Count > 0)
                {
                    var names = string.Join("、", conflicts.Select(c => c.Name + "（" + c.Combo + "）"));
                    ShowCaptureHint($"设置失败：与「{names}」重复，请换一个组合键。");
                    return;
                }
            }
            catch { }

            bool ok = false;
            try { ok = _host?.SetHotkey(actionId, combo) ?? false; } catch { }

            if (ok)
            {
                _overrides[actionId] = combo;
                SaveConfig();
                ReloadRow(row, actionId);
                ShowCaptureHint($"已设置：{combo}");
            }
            else
            {
                ShowCaptureHint("设置失败：该组合键已被占用或无法注册，请换一个。");
            }
        }

        /// <summary>若正处于捕获状态，则结束捕获（卸载钩子）并恢复被挂起的快捷键。</summary>
        private void EndCaptureIfAny()
        {
            if (!_capturing) return;
            _capturing = false;
            UninstallHook();
            try { _host?.ResumeHotkeys(); } catch { }
        }

        private static int CountModifiers(ModifierKeys mods)
        {
            int n = 0;
            if ((mods & ModifierKeys.Control) != 0) n++;
            if ((mods & ModifierKeys.Shift) != 0) n++;
            if ((mods & ModifierKeys.Alt) != 0) n++;
            if ((mods & ModifierKeys.Windows) != 0) n++;
            return n;
        }

        private void ReloadRow(Border row, string actionId)
        {
            if (row == null) return;
            HotkeyActionInfo latest = null;
            try { latest = _host?.GetHotkeyActions()?.FirstOrDefault(x => string.Equals(x.Id, actionId, StringComparison.OrdinalIgnoreCase)); } catch { }
            if (latest != null) RenderRow(row, latest);
        }

        private void RefreshAllRows(StackPanel root)
        {
            if (root == null) return;
            foreach (var child in root.Children.OfType<UIElement>().ToList())
            {
                if (child is Border b && b.Tag is string id)
                    ReloadRow(b, id);
            }
        }

        private void TryFocusRow(Border row)
        {
            try
            {
                row?.Focus();
                Keyboard.Focus(row);
            }
            catch { }
        }

        private void ShowCaptureHint(string text)
        {
            if (_captureHint != null) _captureHint.Text = text;
        }

        private static bool IsModifierKey(Key key)
        {
            return key == Key.LeftShift || key == Key.RightShift ||
                   key == Key.LeftCtrl || key == Key.RightCtrl ||
                   key == Key.LeftAlt || key == Key.RightAlt ||
                   key == Key.LWin || key == Key.RWin;
        }

        private static string FormatCombo(ModifierKeys mods, Key key)
        {
            var parts = new List<string>();
            if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
            if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((mods & ModifierKeys.Windows) != 0) parts.Add("Win");
            parts.Add(key.ToString());
            return string.Join("+", parts);
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