using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Ink;

namespace Ink_Canvas.Plugins.FunctionGraph
{
    /// <summary>
    /// 受限数学符号手写识别器（自实现，无第三方依赖）。
    ///
    /// 背景：System.Windows.Ink.InkAnalyzer 的文本识别依赖系统传统手写识别器 COM 组件，
    /// 而 Windows 8+ 的新版"暂存"手写引擎（mshwstaging.dll）不再通过该接口暴露，
    /// 多数新系统上 Analyze 只分段不出词（WritingRegion/Paragraph/Line 之下没有 InkWordNode）。
    /// 因此这里实现一套模板匹配识别器（$1 Unistroke 算法的多笔画扩展）：
    /// 1. 笔画按横向位置聚类成"符号组"（x 区间重叠或间隙小归为一组，如 x/+/= 的多笔）；
    /// 2. 每组重采样、旋转/平移/尺度归一化后与理想字形模板做黄金分割搜索匹配；
    /// 3. 输出符号序列，再把相邻字母合并成 sin/cos/tan/log/ln/sqrt/pi 等函数词。
    ///
    /// 算法尺度/旋转不变——特殊屏幕、高 DPI、小尺寸手写均可归一化处理；
    /// 识别结果统一进可编辑文本框，识别置信度低时输出问号占位并提示手动修正。
    /// </summary>
    internal static class HandwritingMathRecognizer
    {
        private const int ResamplePoints = 48;
        private const double MatchThreshold = 0.55;   // $1 归一化平均距离阈值（越小越像）

        /// <summary>识别结果：表达式文本与诊断信息。</summary>
        public static string Recognize(StrokeCollection strokes, out string detail)
        {
            detail = "";
            try
            {
                var strokePts = new List<List<Point>>();
                foreach (Stroke stroke in strokes)
                {
                    var pts = stroke.StylusPoints
                        .Select(sp => new Point(sp.X, sp.Y))
                        .ToList();
                    if (pts.Count >= 1) strokePts.Add(pts);
                }
                if (strokePts.Count == 0) return "";

                // 每笔先重采样，消除点密度差异
                var resampled = strokePts.Select(Resample).ToList();

                // 1) 聚类成符号组
                var groups = GroupIntoSymbols(resampled);
                if (groups.Count == 0) return "";

                // 2) 逐组分类
                var tokens = new List<string>();
                int unknown = 0;
                foreach (var group in groups)
                {
                    var best = ClassifyGroup(group);
                    if (best.Key == "?" || best.Value > MatchThreshold)
                    {
                        unknown++;
                        tokens.Add(null);
                    }
                    else
                    {
                        tokens.Add(best.Key);
                    }
                }

                // 3) 字母合并成函数词
                var expression = AssembleExpression(tokens);
                detail = groups.Count + " 个符号，未识别 " + unknown;
                return expression;
            }
            catch (Exception ex)
            {
                detail = "识别异常：" + ex.Message;
                return "";
            }
        }

        // ===== 1. 笔画聚类 =====

        /// <summary>按 x 区间重叠/间隙把笔画聚类为符号组（书写顺序 = 从左到右）。</summary>
        private static List<List<List<Point>>> GroupIntoSymbols(List<List<Point>> strokes)
        {
            var ordered = strokes
                .Select(pts => new
                {
                    pts,
                    minX = pts.Min(p => p.X),
                    maxX = pts.Max(p => p.X),
                    minY = pts.Min(p => p.Y),
                    maxY = pts.Max(p => p.Y)
                })
                .OrderBy(item => item.minX)
                .ToList();

            double avgHeight = ordered.Count > 0
                ? ordered.Average(item => Math.Max(1, item.maxY - item.minY))
                : 0;
            double gapThreshold = Math.Max(18, avgHeight * 0.45);

            var groups = new List<List<List<Point>>>();
            foreach (var item in ordered)
            {
                if (groups.Count > 0)
                {
                    var current = groups[groups.Count - 1];
                    double currentMaxX = current.SelectMany(g => g).Max(p => p.X);
                    if (item.minX - currentMaxX <= gapThreshold)
                    {
                        current.Add(item.pts);
                        continue;
                    }
                }
                groups.Add(new List<List<Point>> { item.pts });
            }

            return groups;
        }

        // ===== 2. 模板匹配（$1 Unistroke 多笔画扩展） =====

        private static readonly KeyValuePair<string, List<List<Point>>>[] Templates = BuildTemplates();

        /// <summary>对一个符号组分类：多笔画组尝试所有笔画顺序（≤2 笔时全排列），取最优匹配。</summary>
        private static KeyValuePair<string, double> ClassifyGroup(List<List<Point>> group)
        {
            KeyValuePair<string, double> best = new KeyValuePair<string, double>('?'.ToString(), double.MaxValue);

            IEnumerable<List<List<Point>>> orders;
            if (group.Count == 1)
            {
                orders = new[] { group };
            }
            else if (group.Count == 2)
            {
                orders = new[] { group, new List<List<Point>> { group[1], group[0] } };
            }
            else
            {
                orders = new[] { group };
            }

            foreach (var order in orders)
            {
                // 各笔独立归一化后拼接（避免笔画间"跳跃线段"干扰几何）
                var combined = new List<Point>();
                foreach (var stroke in order)
                {
                    var normalized = NormalizeStroke(Resample(stroke));
                    if (combined.Count > 0) combined.Add(new Point(double.NaN, double.NaN)); // 笔画分隔标记
                    combined.AddRange(normalized);
                }

                foreach (var template in Templates)
                {
                    double score = GlyphDistance(combined, template.Value);
                    if (score < best.Value)
                    {
                        best = new KeyValuePair<string, double>(template.Key, score);
                    }
                }
            }

            return best;
        }

        /// <summary>$1 距离：两侧归一化后逐点平均距离（跳过分隔标记），经黄金分割搜索最优旋转。</summary>
        private static double GlyphDistance(List<Point> a, List<List<Point>> templateStrokes)
        {
            var b = new List<Point>();
            foreach (var stroke in templateStrokes)
            {
                if (b.Count > 0) b.Add(new Point(double.NaN, double.NaN));
                b.AddRange(NormalizeStroke(Resample(stroke)));
            }

            double bestScore = double.MaxValue;
            double low = -45 * Math.PI / 180;
            double high = 45 * Math.PI / 180;
            double phi = (Math.Sqrt(5) - 1) / 2;

            double r1 = high - phi * (high - low);
            double r2 = low + phi * (high - low);
            double f1 = DistanceAtAngle(a, b, r1);
            double f2 = DistanceAtAngle(a, b, r2);

            for (int i = 0; i < 12; i++)
            {
                if (f1 < f2)
                {
                    high = r2;
                    r2 = r1;
                    f2 = f1;
                    r1 = high - phi * (high - low);
                    f1 = DistanceAtAngle(a, b, r1);
                }
                else
                {
                    low = r1;
                    r1 = r2;
                    f1 = f2;
                    r2 = low + phi * (high - low);
                    f2 = DistanceAtAngle(a, b, r2);
                }
            }
            bestScore = Math.Min(f1, f2);
            return bestScore;
        }

        private static double DistanceAtAngle(List<Point> a, List<Point> b, double angle)
        {
            double rad = angle;
            var rotated = new List<Point>();
            double cx = a.Where(p => !double.IsNaN(p.X)).Average(p => p.X);
            double cy = a.Where(p => !double.IsNaN(p.X)).Average(p => p.Y);
            foreach (var p in a)
            {
                if (double.IsNaN(p.X)) { rotated.Add(p); continue; }
                double dx = p.X - cx, dy = p.Y - cy;
                rotated.Add(new Point(cx + dx * Math.Cos(rad) - dy * Math.Sin(rad),
                                      cy + dx * Math.Sin(rad) + dy * Math.Cos(rad)));
            }
            return AverageDistance(rotated, b);
        }

        private static double AverageDistance(List<Point> a, List<Point> b)
        {
            int n = Math.Min(a.Count, b.Count);
            if (n == 0) return double.MaxValue;
            double sum = 0;
            int used = 0;
            for (int i = 0; i < n; i++)
            {
                if (double.IsNaN(a[i].X) || double.IsNaN(b[i].X)) continue;
                double dx = a[i].X - b[i].X;
                double dy = a[i].Y - b[i].Y;
                sum += Math.Sqrt(dx * dx + dy * dy);
                used++;
            }
            return used == 0 ? double.MaxValue : sum / used;
        }

        /// <summary>等距重采样。</summary>
        private static List<Point> Resample(List<Point> pts)
        {
            var filtered = pts.Where(p => true).ToList();
            if (filtered.Count == 0)
                return filtered;
            if (filtered.Count == 1)
            {
                var list = new List<Point>();
                for (int i = 0; i < ResamplePoints; i++) list.Add(filtered[0]);
                return list;
            }

            double total = 0;
            for (int i = 1; i < filtered.Count; i++)
                total += Dist(filtered[i - 1], filtered[i]);
            if (total <= 0)
            {
                var list = new List<Point>();
                for (int i = 0; i < ResamplePoints; i++) list.Add(filtered[0]);
                return list;
            }

            double interval = total / (ResamplePoints - 1);
            var result = new List<Point> { filtered[0] };
            var src = filtered.Select(p => new Point(p.X, p.Y)).ToList();
            double accumulated = 0;
            for (int i = 1; i < src.Count; i++)
            {
                double d = Dist(src[i - 1], src[i]);
                if (accumulated + d >= interval && d > 0)
                {
                    double t = (interval - accumulated) / d;
                    var q = new Point(src[i - 1].X + (src[i].X - src[i - 1].X) * t,
                                      src[i - 1].Y + (src[i].Y - src[i - 1].Y) * t);
                    result.Add(q);
                    src.Insert(i, q);
                    accumulated = 0;
                }
                else
                {
                    accumulated += d;
                }
            }
            while (result.Count < ResamplePoints)
                result.Add(src[src.Count - 1]);
            return result;
        }

        /// <summary>尺度归一化：缩放到 1x1 方框并以质心为原点（尺度/位置不变 → 特殊屏幕小字也能匹配）。</summary>
        private static List<Point> NormalizeStroke(List<Point> pts)
        {
            var valid = pts.Where(p => !double.IsNaN(p.X)).ToList();
            if (valid.Count == 0) return pts;
            double minX = valid.Min(p => p.X), maxX = valid.Max(p => p.X);
            double minY = valid.Min(p => p.Y), maxY = valid.Max(p => p.Y);
            double size = Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-6);
            double cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;

            var result = new List<Point>();
            foreach (var p in pts)
            {
                if (double.IsNaN(p.X)) { result.Add(p); continue; }
                result.Add(new Point((p.X - cx) / size, (p.Y - cy) / size));
            }
            return result;
        }

        private static double Dist(Point a, Point b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // ===== 3. 模板定义（100x100 理想字形，多笔画符号用多段） =====

        private static List<Point> Path(params double[] coords)
        {
            var list = new List<Point>();
            for (int i = 0; i + 1 < coords.Length; i += 2)
                list.Add(new Point(coords[i], coords[i + 1]));
            return list;
        }

        /// <summary>在两参数间按 t 取贝塞尔样式的弧线采样。</summary>
        private static List<Point> Arc(double cx, double cy, double rx, double ry, double a0, double a1, int n, bool reverse = false)
        {
            var list = new List<Point>();
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                double a = a0 + (a1 - a0) * t;
                list.Add(new Point(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a)));
            }
            if (reverse) list.Reverse();
            return list;
        }

        private static KeyValuePair<string, List<List<Point>>> T(string symbol, params List<Point>[] strokes)
        {
            return new KeyValuePair<string, List<List<Point>>>(symbol, strokes.ToList());
        }

        private static KeyValuePair<string, List<List<Point>>>[] BuildTemplates()
        {
            var templates = new List<KeyValuePair<string, List<List<Point>>>
            >();

            // 数字
            templates.Add(T("0", Arc(50, 50, 28, 40, -Math.PI / 2, 3 * Math.PI / 2, 24)));
            templates.Add(T("1", Path(46, 22, 52, 16, 56, 24, 56, 80, 44, 84)));
            templates.Add(T("2", Path(28, 38, 36, 20, 52, 14, 68, 22, 72, 34, 64, 50, 46, 64, 30, 78, 28, 84, 74, 84)));
            templates.Add(T("3", Path(30, 22, 48, 14, 66, 22, 70, 36, 58, 48, 44, 50, 60, 52, 72, 64, 68, 78, 50, 86, 32, 80)));
            templates.Add(T("4", Path(58, 16, 28, 58, 72, 58), Path(58, 16, 58, 86)));
            templates.Add(T("5", Path(68, 18, 38, 18, 36, 44, 56, 44, 68, 56, 66, 74, 48, 84, 32, 76)));
            templates.Add(T("6", Path(64, 16, 44, 28, 34, 52, 38, 74, 56, 84, 70, 72, 68, 50, 52, 42, 38, 50)));
            templates.Add(T("7", Path(28, 20, 72, 20, 44, 84)));
            templates.Add(T("8", Arc(50, 32, 22, 18, Math.PI / 2, -3 * Math.PI / 2, 24), Arc(50, 68, 28, 20, -Math.PI / 2, 3 * Math.PI / 2, 24)));
            templates.Add(T("9", Path(66, 20, 48, 16, 36, 30, 38, 50, 56, 58, 68, 48, 66, 28, 66, 50, 66, 84)));

            // 字母（数学常用）
            templates.Add(T("x", Path(30, 22, 70, 80), Path(70, 22, 30, 80)));
            templates.Add(T("y", Path(42, 18, 44, 52), Path(44, 52, 28, 88, 64, 62)));
            templates.Add(T("c", Arc(58, 50, 26, 30, -Math.PI / 2.4, Math.PI / 2.4, 20, true)));
            templates.Add(T("o", Arc(50, 50, 24, 32, -Math.PI / 2, 3 * Math.PI / 2, 24)));
            templates.Add(T("s", Path(68, 22, 50, 14, 34, 24, 40, 40, 56, 52, 66, 66, 58, 82, 36, 84)));
            templates.Add(T("n", Path(30, 82, 30, 36, 44, 26, 58, 36, 60, 82)));
            templates.Add(T("t", Path(48, 16, 46, 66, 58, 78)));
            templates.Add(T("l", Path(44, 16, 46, 84)));
            templates.Add(T("i", Path(48, 30, 50, 82)));
            templates.Add(T("g", Arc(52, 44, 22, 24, -Math.PI / 2, 3 * Math.PI / 2, 20), Path(74, 44, 72, 78, 46, 92)));
            templates.Add(T("r", Path(34, 82, 34, 30, 52, 24, 60, 34)));

            // 运算符
            templates.Add(T("+", Path(50, 24, 50, 76), Path(24, 50, 76, 50)));
            templates.Add(T("-", Path(24, 50, 76, 50)));
            templates.Add(T("=", Path(24, 38, 76, 38), Path(24, 62, 76, 62)));
            templates.Add(T("/", Path(28, 16, 72, 84)));
            templates.Add(T("*", Path(50, 30, 50, 70), Path(32, 40, 68, 60), Path(68, 40, 32, 60)));
            templates.Add(T("^", Path(32, 48, 50, 26), Path(50, 26, 68, 48)));
            templates.Add(T("(", Arc(62, 50, 30, 34, -Math.PI / 2.6, Math.PI / 2.6, 20, true)));
            templates.Add(T(")", Arc(38, 50, 30, 34, -Math.PI / 2.6, Math.PI / 2.6, 20)));
            templates.Add(T("√", Path(24, 52, 42, 72, 78, 24)));
            templates.Add(T(".", Path(48, 78, 52, 82)));

            return templates.ToArray();
        }

        // ===== 4. 符号序列 → 表达式 =====

        private static readonly string[] FunctionWords = { "sqrt", "sin", "cos", "tan", "log", "ln" };

        /// <summary>合并相邻字母为函数词，其余符号原样拼接；未识别符号以问号占位。</summary>
        private static string AssembleExpression(List<string> tokens)
        {
            var sb = new StringBuilder();
            int i = 0;
            while (i < tokens.Count)
            {
                var token = tokens[i];
                if (token == null)
                {
                    sb.Append('?');
                    i++;
                    continue;
                }

                // 尝试从当前位置匹配函数词（字母序列）
                bool merged = false;
                foreach (var word in FunctionWords.OrderByDescending(w => w.Length))
                {
                    if (i + word.Length <= tokens.Count)
                    {
                        bool ok = true;
                        for (int k = 0; k < word.Length; k++)
                        {
                            if (!string.Equals(tokens[i + k], word[k].ToString(), StringComparison.OrdinalIgnoreCase))
                            {
                                ok = false;
                                break;
                            }
                        }
                        if (ok)
                        {
                            sb.Append(word);
                            i += word.Length;
                            merged = true;
                            break;
                        }
                    }
                }
                if (merged) continue;

                sb.Append(token);
                i++;
            }
            return sb.ToString();
        }
    }
}
