using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
namespace Ink_Canvas.Plugins.FunctionGraph
{
    // 宿主程序集的 Ink_Canvas 命名空间里有一个名为 Canvas 的设置类，
    // 在最内层命名空间声明别名以指向 WPF 的 Canvas（内层别名优先于外层命名空间成员）
    using Canvas = System.Windows.Controls.Canvas;

    /// <summary>函数绘图的一次请求（窗口确定后交给画布放置控制器）。</summary>
    internal sealed class FunctionGraphRequest
    {
        public string ExpressionText;
        public double XMax;
        public double YMax;
        public bool ShowTicks;
        public bool ShowGrid;
        public string Diag;
    }

    /// <summary>
    /// 函数图像渲染器：坐标系以元素中心为原点（对应画布上单击的"中心"位置），
    /// 两轴独立换算（pxPerUnitX = 半宽/xMax，pxPerUnitY = 半高/yMax）——
    /// x/y 轴最大范围所填数值精确决定可见坐标域 [-xMax, xMax] × [-yMax, yMax]。
    ///
    /// 网格与刻度按各轴像素密度自适应：
    ///  - 主刻度步长自动取 1/2/5×10ⁿ（相邻刻度不小于约 44 物理像素），两轴各自计算；
    ///  - 次网格固定 1 个坐标单位一格，但单位像素小于 8px 时自动省略，避免糊成一片；
    ///  - 「显示网格线」关闭时不画网格（坐标轴保留）；「显示刻度」关闭时不画刻度线与数字。
    /// 曲线按像素步长采样，遇 NaN/∞/越界/渐近线跳变断开，不连成竖线。
    /// 输出元素为 Canvas + Line/Path/TextBlock（冻结画刷、无事件、无 x:Name），
    /// 可被宿主 XamlWriter 快照随白板保存/恢复。
    /// </summary>
    internal static class FunctionPlotRenderer
    {
        public static string BuildMarker = "RENDERER_V2_ADAPTIVE_TICKS";
        /// <param name="themeForeground">解析自宿主主题的实心前景色（跟随创建时主题，可持久化）</param>
        /// <param name="dpiScale">DPI 缩放，用于线宽换算</param>
        public static FrameworkElement Build(
            FunctionGraphRequest request, double width, double height,
            Color themeForeground, double dpiScale, bool isPreview)
        {
            double halfW = width / 2;
            double halfH = height / 2;
            // 两轴独立换算：填写的 x/y 最大范围精确决定可见坐标域，
            // 网格与刻度步长按各轴像素密度自适应（1/2/5×10ⁿ），保证标注不挤
            double pxPerUnitX = halfW / request.XMax;
            double pxPerUnitY = halfH / request.YMax;

            double originX = halfW;   // 元素内坐标：原点在中心
            double originY = halfH;

            var canvas = new Canvas { Width = width, Height = height, Background = Brushes.Transparent };

            Color axisColor = themeForeground;
            Color majorGridColor = Blend(themeForeground, 0.35);
            Color minorGridColor = Blend(themeForeground, 0.18);
            Color tickColor = themeForeground;

            int majorStepX = ComputeMajorStep(pxPerUnitX, dpiScale);
            int majorStepY = ComputeMajorStep(pxPerUnitY, dpiScale);
            bool showMinorGridX = pxPerUnitX >= 8;   // 1 单位小于 8px 时省略该轴次网格
            bool showMinorGridY = pxPerUnitY >= 8;

            double thickness = Math.Max(1, dpiScale);
            double curveThickness = Math.Max(1.5, 2 * dpiScale);

            // 网格：每 1 个坐标单位一格（次网格），主网格按各轴自适应步长；「显示网格线」关闭时不画
            if (request.ShowGrid)
            {
                if (showMinorGridX)
                {
                    for (int gx = (int)Math.Ceiling(-request.XMax); gx <= (int)Math.Floor(request.XMax); gx++)
                    {
                        if (gx == 0 || gx % majorStepX == 0) continue;
                        double px = originX + gx * pxPerUnitX;
                        if (px < 0 || px > width) continue;
                        canvas.Children.Add(new Line
                        {
                            X1 = px, Y1 = 0, X2 = px, Y2 = height,
                            Stroke = MakeBrush(minorGridColor), StrokeThickness = Math.Max(0.5, 0.6 * dpiScale),
                            IsHitTestVisible = false
                        });
                    }
                }
                if (showMinorGridY)
                {
                    for (int gy = (int)Math.Ceiling(-request.YMax); gy <= (int)Math.Floor(request.YMax); gy++)
                    {
                        if (gy == 0 || gy % majorStepY == 0) continue;
                        double py = originY - gy * pxPerUnitY;
                        if (py < 0 || py > height) continue;
                        canvas.Children.Add(new Line
                        {
                            X1 = 0, Y1 = py, X2 = width, Y2 = py,
                            Stroke = MakeBrush(minorGridColor), StrokeThickness = Math.Max(0.5, 0.6 * dpiScale),
                            IsHitTestVisible = false
                        });
                    }
                }

                // 主网格：每 majorStep 个坐标单位一格
                for (int gx = (int)Math.Ceiling(-request.XMax); gx <= (int)Math.Floor(request.XMax); gx++)
                {
                    if (gx == 0 || gx % majorStepX != 0) continue;
                    double px = originX + gx * pxPerUnitX;
                    if (px < 0 || px > width) continue;
                    canvas.Children.Add(new Line
                    {
                        X1 = px, Y1 = 0, X2 = px, Y2 = height,
                        Stroke = MakeBrush(majorGridColor), StrokeThickness = Math.Max(0.8, 0.8 * dpiScale),
                        IsHitTestVisible = false
                    });
                }
                for (int gy = (int)Math.Ceiling(-request.YMax); gy <= (int)Math.Floor(request.YMax); gy++)
                {
                    if (gy == 0 || gy % majorStepY != 0) continue;
                    double py = originY - gy * pxPerUnitY;
                    if (py < 0 || py > height) continue;
                    canvas.Children.Add(new Line
                    {
                        X1 = 0, Y1 = py, X2 = width, Y2 = py,
                        Stroke = MakeBrush(majorGridColor), StrokeThickness = Math.Max(0.8, 0.8 * dpiScale),
                        IsHitTestVisible = false
                    });
                }
            }

            // 坐标轴
            canvas.Children.Add(new Line
            {
                X1 = 0, Y1 = originY, X2 = width, Y2 = originY,
                Stroke = MakeBrush(axisColor), StrokeThickness = Math.Max(1, 1.4 * dpiScale),
                IsHitTestVisible = false
            });
            canvas.Children.Add(new Line
            {
                X1 = originX, Y1 = 0, X2 = originX, Y2 = height,
                Stroke = MakeBrush(axisColor), StrokeThickness = Math.Max(1, 1.4 * dpiScale),
                IsHitTestVisible = false
            });

            // 刻度（可关）：轴上短刻度线 + 主刻度整数数值标注，按各轴步长，避开元素边缘
            if (request.ShowTicks && !isPreview)
            {
            {
                int tickCount = 0;
                double fontSize = Math.Max(8, 10 * dpiScale);
                double tickLength = Math.Max(4, 5 * dpiScale);
                double margin = 12 * dpiScale;

                for (int gx = (int)Math.Ceiling(-request.XMax); gx <= (int)Math.Floor(request.XMax); gx++)
                {
                    if (gx == 0 || gx % majorStepX != 0) continue;
                    double px = originX + gx * pxPerUnitX;
                    if (px < margin || px > width - margin) continue;

                    // x 轴上的刻度短线
                    canvas.Children.Add(new Line
                    {
                        X1 = px, Y1 = originY - tickLength, X2 = px, Y2 = originY + tickLength,
                        Stroke = MakeBrush(tickColor), StrokeThickness = Math.Max(1, 1.1 * dpiScale),
                        IsHitTestVisible = false
                    });

                    var label = new TextBlock
                    {
                        Text = gx.ToString(),
                        FontSize = fontSize,
                        Foreground = MakeBrush(tickColor),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(label, px - fontSize * 0.4);
                    Canvas.SetTop(label, Math.Min(Math.Max(originY + tickLength + 1 * dpiScale, 0), height - fontSize - 2));
                    canvas.Children.Add(label);
                    tickCount++;
                }

                for (int gy = (int)Math.Ceiling(-request.YMax); gy <= (int)Math.Floor(request.YMax); gy++)
                {
                    if (gy == 0 || gy % majorStepY != 0) continue;
                    double py = originY - gy * pxPerUnitY;
                    if (py < margin || py > height - margin) continue;

                    // y 轴上的刻度短线
                    canvas.Children.Add(new Line
                    {
                        X1 = originX - tickLength, Y1 = py, X2 = originX + tickLength, Y2 = py,
                        Stroke = MakeBrush(tickColor), StrokeThickness = Math.Max(1, 1.1 * dpiScale),
                        IsHitTestVisible = false
                    });

                    var label = new TextBlock
                    {
                        Text = gy.ToString(),
                        FontSize = fontSize,
                        Foreground = MakeBrush(tickColor),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(label, Math.Max(originX - tickLength - fontSize * 1.6, 1));
                    Canvas.SetTop(label, py - fontSize / 2);
                    canvas.Children.Add(label);
                    tickCount++;
                }
            }
        }

            // 曲线：像素步长采样
            if (request.ExpressionText != null)
            {
                Func<double, double> f = ExpressionParser.Compile(request.ExpressionText, out string error);
                if (f != null)
                {
                    var curve = new Path
                    {
                        Stroke = MakeBrush(axisColor),
                        StrokeThickness = curveThickness,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        StrokeLineJoin = PenLineJoin.Round,
                        IsHitTestVisible = false
                    };
                    var geometry = new StreamGeometry();
                    using (var ctx = geometry.Open())
                    {
                        bool drawing = false;
                        Point last = default(Point);
                        double lastY = double.NaN;
                        double jumpThreshold = height * 1.5;   // 一个采样步内纵跨超过 1.5 倍高视为渐近线跳变
                        double clipY = request.YMax * 1.02;    // 纵向裁剪边界（数据单位），略放宽避免边界毛刺

                        for (double px = 0; px <= width; px += 1)
                        {
                            double dataX = (px - originX) / pxPerUnitX;
                            double dataY = f(dataX);
                            if (double.IsNaN(dataY) || double.IsInfinity(dataY) || Math.Abs(dataY) > clipY)
                            {
                                drawing = false;
                                lastY = double.NaN;
                                continue;
                            }

                            var point = new Point(px, originY - dataY * pxPerUnitY);
                            if (!drawing)
                            {
                                ctx.BeginFigure(point, false, false);
                                drawing = true;
                            }
                            else if (!double.IsNaN(lastY) && Math.Abs(point.Y - last.Y) > jumpThreshold)
                            {
                                // 渐近线：断开而不是连成竖线
                                ctx.BeginFigure(point, false, false);
                            }
                            else
                            {
                                ctx.LineTo(point, true, false);
                            }
                            last = point;
                            lastY = dataY;
                        }
                    }
                    geometry.Freeze();
                    curve.Data = geometry;
                    canvas.Children.Add(curve);
                }
                else if (!isPreview && error != null)
                {
                    // 表达式无效：放置阶段给出可见提示（元素仍可被选中删除）
                    var warn = new TextBlock
                    {
                        Text = "无法解析表达式：" + error,
                        FontSize = Math.Max(10, 12 * dpiScale),
                        Foreground = MakeBrush(axisColor),
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(warn, 8);
                    Canvas.SetTop(warn, 8);
                    canvas.Children.Add(warn);
                }
            }

            return canvas;
        }

        /// <summary>
        /// 主刻度步长（数据单位）：按像素密度保证相邻主刻度不小于约 44 物理像素，
        /// 步长取 1/2/5×10ⁿ 的"整齐"值，刻度数值标注为整数。
        /// </summary>
        private static int ComputeMajorStep(double pxPerUnit, double dpiScale)
        {
            double targetPx = Math.Max(44, 44 * dpiScale);
            double raw = targetPx / Math.Max(pxPerUnit, 0.0001);
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(raw, 1e-9))));
            double nice = raw <= 1 * magnitude ? magnitude
                        : raw <= 2 * magnitude ? 2 * magnitude
                        : raw <= 5 * magnitude ? 5 * magnitude
                        : 10 * magnitude;
            return Math.Max(1, (int)Math.Ceiling(nice));
        }

        private static SolidColorBrush MakeBrush(Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        /// <summary>主题前景色与透明的混合，作网格弱色。</summary>
        private static Color Blend(Color foreground, double alphaFraction)
        {
            return Color.FromArgb((byte)Math.Round(255 * alphaFraction), foreground.R, foreground.G, foreground.B);
        }
    }
}
