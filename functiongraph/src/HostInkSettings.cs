using System;
using System.Globalization;
using System.Reflection;

namespace Ink_Canvas.Plugins.FunctionGraph
{
    /// <summary>
    /// 宿主「特殊屏幕模式 / 触摸大小 / 画笔粗细」设置的只读快照。
    ///
    /// 为什么用反射：插件 DLL 以 Private=false 复用宿主已加载的程序集，但不应与宿主内部类型
    /// 形成编译期强绑定；宿主字段改名、旧版本缺字段、或插件在探针/设计器中被单独实例化时，
    /// 都能静默回落到内置默认值，不会让「函数绘图」窗口打不开。
    ///
    /// 取值与宿主 MainWindow_TouchDown / MW_Settings 完全同源：
    ///   IsSpecialScreen / TouchMultiplier / BoundsWidth(按笔尖或手指模式) / ThresholdValue /
    ///   EraserSize 以及 Canvas.InkWidth。手写框用 BoundsWidth 系列过滤手掌级误触，
    ///   用 InkWidth 跟随画笔粗细，特殊屏幕下再适度加粗并放大手写区。
    /// </summary>
    internal sealed class HostInkSettings
    {
        public bool IsSpecialScreen;
        public double TouchMultiplier = 0.25;
        public double BoundsWidth = 30;
        public double EraserThreshold = 2.5;
        public double EraserSizeFactor = 0.8;
        public double InkWidth = 2.5;
        public bool IsNibMode;

        /// <summary>是否为特殊屏幕且已配置有效参数（用于 UI 放大与笔迹加粗）。</summary>
        public bool UseSpecialScreenLayout
        {
            get { return IsSpecialScreen && TouchMultiplier > 0; }
        }

        public static HostInkSettings Read(IPluginHost host)
        {
            var result = new HostInkSettings();
            try
            {
                object settings = FindHostSettings(host);
                object advanced = Get(settings, "Advanced");
                object startup = Get(settings, "Startup");
                object canvasSettings = Get(settings, "Canvas");

                result.IsNibMode = ReadBool(Get(startup, "IsEnableNibMode"), false);
                result.IsSpecialScreen = ReadBool(Get(advanced, "IsSpecialScreen"), false);
                result.TouchMultiplier = ReadDouble(Get(advanced, "TouchMultiplier"), 0.25);

                // 与主程序 MainWindow_TouchDown 一致：按当前输入模式挑选 BoundsWidth 三件套
                if (result.IsNibMode)
                {
                    result.BoundsWidth = ReadDouble(Get(advanced, "NibModeBoundsWidth"), 10);
                    result.EraserThreshold = ReadDouble(Get(advanced, "NibModeBoundsWidthThresholdValue"), 2.5);
                    result.EraserSizeFactor = ReadDouble(Get(advanced, "NibModeBoundsWidthEraserSize"), 0.8);
                }
                else
                {
                    result.BoundsWidth = ReadDouble(Get(advanced, "FingerModeBoundsWidth"), 30);
                    result.EraserThreshold = ReadDouble(Get(advanced, "FingerModeBoundsWidthThresholdValue"), 2.5);
                    result.EraserSizeFactor = ReadDouble(Get(advanced, "FingerModeBoundsWidthEraserSize"), 0.8);
                }

                double inkWidth = ReadDouble(Get(canvasSettings, "InkWidth"), 2.5);
                result.InkWidth = inkWidth > 0 ? inkWidth : 2.5;

                // 宿主画布的 DefaultDrawingAttributes 是用户「墨迹选项」里调过粗细后的实时值，
                // 优先于设置文件里的初值（设置文件只在启动加载时写入一次）。
                try
                {
                    var inkCanvas = host == null ? null : host.GetInkCanvas();
                    if (inkCanvas != null && inkCanvas.DefaultDrawingAttributes != null)
                    {
                        double liveWidth = inkCanvas.DefaultDrawingAttributes.Width;
                        if (liveWidth > 0) result.InkWidth = liveWidth;
                    }
                }
                catch { }
            }
            catch
            {
                // 读取失败一律回落默认值，绝不因为宿主版本差异中断手写窗口
            }

            if (result.BoundsWidth <= 0) result.BoundsWidth = 30;
            if (result.EraserThreshold <= 0) result.EraserThreshold = 2.5;
            if (result.EraserSizeFactor <= 0) result.EraserSizeFactor = 0.8;
            if (result.TouchMultiplier <= 0) result.TouchMultiplier = 0.25;
            return result;
        }

        /// <summary>手写框笔迹宽度：跟随宿主画笔，特殊屏幕下加粗到手指可见。</summary>
        public double GetInkStrokeWidth()
        {
            double width = InkWidth;
            if (UseSpecialScreenLayout)
            {
                // 大屏/红外屏用手指书写时细笔迹几乎看不见，抬到至少 4px；
                // TouchMultiplier 越小说明屏幕报出的触点越大，笔迹同步略粗。
                double boost = 1.0 + (1.0 - Math.Min(1.0, TouchMultiplier)) * 0.8;
                width = Math.Max(width * boost, 4.0);
            }
            return Math.Min(Math.Max(width, 1.5), 12.0);
        }

        /// <summary>触摸点宽度是否属于「手掌级误触」，应吞掉不落笔。与主程序判定同源。</summary>
        public bool IsPalmLikeTouch(double boundsWidth)
        {
            double effective = Math.Max(0, boundsWidth);
            // 与主程序一致：特殊屏幕下用倍数修正驱动虚报的触点尺寸
            if (IsSpecialScreen) effective *= TouchMultiplier;
            double threshold = BoundsWidth * Math.Max(1.0, EraserThreshold) * EraserSizeFactor;
            return effective > Math.Max(BoundsWidth, threshold);
        }

        private static object FindHostSettings(IPluginHost host)
        {
            try
            {
                var window = host == null ? null : host.MainWindow;
                if (window == null) return null;
                var assembly = window.GetType().Assembly;
                var mainWindowType = assembly.GetType("Ink_Canvas.MainWindow", false);
                if (mainWindowType == null) return null;

                const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
                var field = mainWindowType.GetField("Settings", flags);
                if (field != null) return field.GetValue(null);
                var property = mainWindowType.GetProperty("Settings", flags);
                if (property != null) return property.GetValue(null, null);
            }
            catch { }
            return null;
        }

        private static object Get(object owner, string name)
        {
            if (owner == null) return null;
            try
            {
                var type = owner.GetType();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
                var property = type.GetProperty(name, flags);
                if (property != null) return property.GetValue(owner, null);
                var field = type.GetField(name, flags);
                if (field != null) return field.GetValue(owner);
            }
            catch { }
            return null;
        }

        private static double ReadDouble(object value, double fallback)
        {
            if (value == null) return fallback;
            try
            {
                if (value is double || value is float || value is int || value is long || value is decimal)
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture);
                double parsed;
                if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    return parsed;
            }
            catch { }
            return fallback;
        }

        private static bool ReadBool(object value, bool fallback)
        {
            if (value is bool) return (bool)value;
            return fallback;
        }
    }
}
