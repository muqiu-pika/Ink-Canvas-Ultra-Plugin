using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Ink_Canvas.Plugins.DocumentToImage
{
    /// <summary>
    ///     Word / Excel / PDF 文件关联管理（插件设置「打开文件时自动启动软件」）。
    ///     打开开关时把对应扩展名的 HKCU「open」命令临时指向 Ink Canvas Ultra；
    ///     关闭开关/插件卸载时恢复覆盖前的原值（无原值则删除我们的覆盖项），
    ///     即回到系统默认打开方式。HKCU 写入无需管理员权限。
    /// </summary>
    internal static class FileAssociationManager
    {
        private static readonly string[] Extensions = { ".doc", ".docx", ".pdf", ".xls", ".xlsx" };

        /// <summary>覆盖前各扩展名的原始 open 命令（会话内记忆；跨重启场景由设置文件持久化）。</summary>
        private static readonly Dictionary<string, string> PreviousCommands =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        /// <summary>把指定扩展名的「打开」动作指到 Ink Canvas Ultra（已是我们自己的关联时不重复覆盖备份）。</summary>
        public static void Apply(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return;
            var commandValue = "\"" + exePath + "\" \"%1\"";
            foreach (var ext in Extensions)
            {
                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext + @"\shell\open\command"))
                    {
                        if (key == null) continue;
                        var previous = key.GetValue(null) as string;
                        if (!string.IsNullOrEmpty(previous) &&
                            previous.IndexOf("Ink Canvas Ultra", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            continue; // 已经是本程序的关联，不覆盖备份原值
                        }
                        if (!string.IsNullOrEmpty(previous))
                        {
                            PreviousCommands[ext] = previous;
                        }
                        key.SetValue(null, commandValue);
                    }
                }
                catch { }
            }
            NotifyShell();
        }

        /// <summary>恢复各扩展名的原始打开方式（无备份则删除覆盖项，回退系统默认解析）。</summary>
        public static void Remove(IEnumerable<KeyValuePair<string, string>> rememberedPrevious = null)
        {
            // 跨重启恢复：调用方可传入持久化的原值表
            if (rememberedPrevious != null)
            {
                foreach (var pair in rememberedPrevious)
                {
                    PreviousCommands[pair.Key] = pair.Value;
                }
            }

            foreach (var ext in Extensions)
            {
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext + @"\shell\open\command", writable: true))
                    {
                        if (key == null) continue;
                        var current = key.GetValue(null) as string;
                        if (string.IsNullOrEmpty(current) ||
                            current.IndexOf("Ink Canvas Ultra", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue; // 非本程序的关联，不动
                        }
                        string previous;
                        PreviousCommands.TryGetValue(ext, out previous);
                        if (!string.IsNullOrEmpty(previous) && LooksLikeSafeCommand(previous))
                        {
                            // settings.ini 是用户可编辑文件：恢复前校验备份值形态，
                            // 防止被塞入任意命令串后经"关闭开关→恢复默认"写入注册表执行
                            key.SetValue(null, previous);
                        }
                        else
                        {
                            key.DeleteValue(null, false);
                        }
                    }
                }
                catch { }
                PreviousCommands.Remove(ext);
            }
            NotifyShell();
        }

        /// <summary>恢复值形态校验：无控制字符、长度合理（超过 2048 视为异常直接丢弃覆盖项）。</summary>
        private static bool LooksLikeSafeCommand(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 2048) return false;
            foreach (var ch in value)
            {
                if (char.IsControl(ch)) return false;
            }
            return true;
        }

        private static void NotifyShell()
        {
            try
            {
                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }
    }
}
