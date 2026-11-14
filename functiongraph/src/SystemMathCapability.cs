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
    /// 设计约束：作为手写面板按钮的启用判据与状态提示，**不作为功能依赖**。
    ///  - 该组件是 Windows 的 Feature on Demand（Win10 1809+ / Win11 按需安装），
    ///    可能未安装或安装失败，因此不能假设用户机器一定有；
    ///  - 探测只做「文件」级别的只读检查（micaut.dll + mraut.dll），不实例化 COM、
    ///    不弹窗、不抛异常；Math Input Control 的接口细节（IID/DISPID/事件参数）
    ///    见 <see cref="SystemMathRecognizer"/>（依据 Windows SDK micaut.h 与官方示例）。
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
