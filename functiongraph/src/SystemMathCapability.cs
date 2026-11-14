using System;
using System.Collections.Generic;
using System.IO;

namespace Ink_Canvas.Plugins.FunctionGraph
{
    internal enum SystemMathStatus
    {
        Available,
        Missing,
        Unknown
    }

    /// <summary>
    /// Windows Math Recognizer（Word「墨迹公式」同源引擎）的**只读能力探测**。
    ///
    /// 设计约束：只作为状态提示，**绝不成为功能依赖**。
    ///  - 该组件是 Windows 的 Feature on Demand（Win10 1809+ / Win11 按需安装），
    ///    Win7/8/8.1 内置、Win11 可能被标记为 Superseded 或 DISM 安装失败，
    ///    因此不能假设用户机器一定有；
    ///  - 调用它的 COM 控件还需要事件连接点（未公开的 IID/DISPID）与 32/64 位互操作包装，
    ///    任何失败都可能弹窗或卡住主流程，与「保证大部分 Windows 版本能够运行」冲突。
    ///  - 所以这里只做「文件 + 注册表」级别的只读检查，不实例化 COM、不弹窗、不抛异常。
    ///
    /// 真正的识别由 <see cref="HandwritingMathRecognizer"/> +
    /// <see cref="ExpressionConstrainedDecoder"/> 完成：纯托管、零系统组件、零联网，
    /// Win7 SP1 ~ Windows 11 / Server 行为一致。
    /// </summary>
    internal static class SystemMathCapability
    {
        public const string InstallHint =
            "Dism /online /add-capability /capabilityname:MathRecognizer~~~~0.0.1.0";

        private static readonly Lazy<Detection> Cached =
            new Lazy<Detection>(Detect, true);

        private sealed class Detection
        {
            public SystemMathStatus Status;
            public string Summary;
            public string Hint;
        }

        public static SystemMathStatus Status
        {
            get { return Cached.Value.Status; }
        }

        /// <summary>窗口里显示的一行短状态。</summary>
        public static string Summary
        {
            get { return Cached.Value.Summary; }
        }

        /// <summary>未安装时的安装引导（放到 ToolTip，避免撑高窗口）。</summary>
        public static string Hint
        {
            get { return Cached.Value.Hint; }
        }

        private static Detection Detect()
        {
            try
            {
                foreach (string directory in EnumerateInkDirectories())
                {
                    bool hasControl = File.Exists(Path.Combine(directory, "micaut.dll"));
                    bool hasRecognizer = File.Exists(Path.Combine(directory, "mraut.dll"));
                    if (!hasControl || !hasRecognizer) continue;

                    return new Detection
                    {
                        Status = SystemMathStatus.Available,
                        Summary = "系统数学识别器：已安装",
                        Hint = "已检测到 Math Input Control / Math Recognizer。" +
                               "点击上方按钮即可使用与 Word 墨迹公式同源的识别引擎；" +
                               "内置手写识别始终作为全版本兜底，不依赖该系统组件。"
                    };
                }

                return new Detection
                {
                    Status = SystemMathStatus.Missing,
                    Summary = "系统数学识别器：未安装",
                    Hint = "未检测到 Math Recognizer 可选功能。这不影响本插件使用内置识别。" +
                           "如需启用系统引擎，可在「设置 → 应用 → 可选功能」安装 Math Recognizer，" +
                           "或以管理员身份运行：" + InstallHint
                };
            }
            catch
            {
                return new Detection
                {
                    Status = SystemMathStatus.Unknown,
                    Summary = "系统数学识别器：未知",
                    Hint = "无法读取系统组件状态，已使用内置识别引擎。"
                };
            }
        }

        /// <summary>
        /// 同时覆盖 64/32 位进程的 Common Files 路径。
        /// 32 位进程里 GetFolderPath(CommonProgramFiles) 会指向 Program Files (x86)，
        /// 而系统 Ink 组件也可能只在 64 位目录下，因此两条都查。
        /// </summary>
        private static IEnumerable<string> EnumerateInkDirectories()
        {
            var roots = new List<string>();
            AddRoot(roots, Environment.GetEnvironmentVariable("CommonProgramW6432"));
            AddRoot(roots, Environment.GetEnvironmentVariable("CommonProgramFiles"));
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles));
            AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86));

            foreach (string root in roots)
            {
                string directory = Path.Combine(root, "Microsoft Shared", "Ink");
                if (Directory.Exists(directory)) yield return directory;
            }
        }

        private static void AddRoot(List<string> roots, string root)
        {
            if (string.IsNullOrEmpty(root)) return;
            foreach (string existing in roots)
                if (string.Equals(existing, root, StringComparison.OrdinalIgnoreCase)) return;
            roots.Add(root);
        }
    }
}
