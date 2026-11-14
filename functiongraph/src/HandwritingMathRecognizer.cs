using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Ink;

namespace Ink_Canvas.Plugins.FunctionGraph
{
    /// <summary>单个符号的候选：符号文本 + 归一化后的 Chamfer 距离（越小越像）。</summary>
    internal sealed class SymbolCandidate
    {
        public string Symbol;
        public double Score;
    }

    /// <summary>一个已切分的符号组：当前选中的符号、Top-N 候选、置信度，以及在手写区的包围盒。</summary>
    internal sealed class RecognizedSymbol
    {
        public string Chosen;
        public List<SymbolCandidate> Candidates = new List<SymbolCandidate>();
        public double Confidence = 1;
        public Rect Bounds;

        /// <summary>由语法约束解码器改写过（UI 用蓝色区分「自动纠正」与「不确定」）。</summary>
        public bool WasAutoCorrected;

        public bool IsUncertain
        {
            get { return Chosen == "?" || Confidence < 0.42; }
        }
    }

    /// <summary>一次识别的完整结果，供窗口做 Word 墨迹公式式的「选择和更正」。</summary>
    internal sealed class RecognitionResult
    {
        public List<RecognizedSymbol> Symbols = new List<RecognizedSymbol>();
        public string Detail = "";

        public bool HasContent
        {
            get { return Symbols.Count > 0; }
        }

        public int UncertainCount
        {
            get { return Symbols.Count(s => s.IsUncertain); }
        }

        /// <summary>把当前各符号的选择拼成表达式（函数词合并 + 隐式乘号补全）。</summary>
        public string BuildExpression()
        {
            var tokens = Symbols.Select(s => s.Chosen).ToList();
            return HandwritingMathRecognizer.AssembleExpression(tokens);
        }

        /// <summary>整体替换某个符号（候选纠正），返回新的表达式文本。</summary>
        public string Replace(int index, string symbol)
        {
            if (index < 0 || index >= Symbols.Count) return BuildExpression();
            var item = Symbols[index];
            item.Chosen = symbol;
            item.Confidence = 1;
            item.Candidates.Clear();
            item.Candidates.Add(new SymbolCandidate { Symbol = symbol, Score = 0 });
            return BuildExpression();
        }
    }

    /// <summary>
    /// 受限数学符号手写识别器（自实现，无第三方依赖）。
    ///
    /// 为什么不用 InkAnalyzer：本机实测（recognition.log）在 default / zh-CN / en-US / en-GB /
    /// zh-SG 五种语言下，Analyze 只产出 WritingRegion/Paragraph/Line，RecognizedString 全为空，
    /// Win8+ 暂存手写引擎不向 .NET InkAnalyzer 暴露文字识别。因此文字识别完全由本类承担。
    ///
    /// v2 相比 v1 的关键修正（准确度问题的根因）：
    ///  1) v1 对「每一笔」独立归一化到 1x1，等于抹掉了笔画之间的相对位置：
    ///     "=" 的两横会叠成一条线、"x" 的两笔都变成同一条斜线、"i" 的点与竖无法区分。
    ///     v2 改为「整组统一归一化 + 栅格化 + 双向 Chamfer 距离」，保留二维字形结构，
    ///     同时天然免疫笔画数差异、笔顺差异与连笔。
    ///  2) v1 分组阈值 max(18, avgHeight*0.45) 里的 18px 是硬编码，与书写尺寸/DPI 无关。
    ///     v2 用全局字符高度的 70 分位数自适应计算间隙。
    ///  3) v1 模板缺 a/b/e/p/q 等字母，sqrt/sin/cos/tan/exp/pi/abs 等函数词永远拼不出来。
    ///     v2 补齐全部小写字母并加入常见手写变体。
    ///  4) v1 只输出一个字符串，错了只能整框重打。v2 输出每个符号的 Top-N 候选 + 置信度，
    ///     窗口据此提供「选择和更正」，对齐 Word 墨迹公式的纠错体验。
    ///  5) 增加上标（x²）、分数线（(a)/(b)）两类二维结构的有限解析。
    /// </summary>
    internal static class HandwritingMathRecognizer
    {
        private const int GridSize = 28;
        private const int CandidateCount = 6;
        private const double MaxAcceptableScore = 0.50;   // 归一化 Chamfer 距离上限，超过视为未识别

        // 预构建的模板（栅格图 + 距离变换图），静态初始化一次
        private static readonly TemplateEntry[] Templates = BuildTemplates();

        private sealed class TemplateEntry
        {
            public string Symbol;
            public float[,] Distance;
            public List<Point> InkCells = new List<Point>();
        }

        /// <summary>识别入口。</summary>
        public static RecognitionResult Recognize(StrokeCollection strokes, out string detail)
        {
            var result = new RecognitionResult();
            detail = "";
            try
            {
                var strokePoints = new List<List<Point>>();
                foreach (Stroke stroke in strokes)
                {
                    var points = stroke.StylusPoints.Select(sp => new Point(sp.X, sp.Y)).ToList();
                    if (points.Count > 0) strokePoints.Add(points);
                }
                if (strokePoints.Count == 0) return result;

                // 单笔极短的孤立点：当作小数点/乘号点，不当作噪声丢掉
                double charHeight = EstimateCharHeight(strokePoints);
                var groups = GroupIntoSymbols(strokePoints, charHeight);
                if (groups.Count == 0) return result;

                // 分数线：一条明显长于字符高度的水平笔画，且上下都有内容 → (上)/(下)
                var fraction = TryBuildFraction(groups, charHeight);
                if (fraction != null)
                {
                    var symbol = new RecognizedSymbol
                    {
                        Chosen = fraction,
                        Confidence = 1,
                        Bounds = UnionBounds(groups)
                    };
                    symbol.Candidates.Add(new SymbolCandidate { Symbol = fraction, Score = 0 });
                    result.Symbols.Add(symbol);
                    detail = "识别为分式结构";
                    return result;
                }

                double baselineCenter = EstimateBaselineCenter(groups, charHeight);
                double previousRight = double.MinValue;
                bool firstGroup = true;
                foreach (var group in groups)
                {
                    var bounds = GetBounds(group.SelectMany(g => g).ToList());
                    var classified = ClassifyGroup(group);
                    string bestSymbol = classified.Count > 0 ? classified[0].Symbol : "?";

                    // 手写 x²：偏上且明显小于主字符高的「数字/字母」视为上标，自动补 ^。
                    // 必须排除 ^ . - + 这类本来就小的运算符，否则写 x^2 会被补成 x^^2。
                    bool isLetterOrDigit = bestSymbol.Length == 1 && char.IsLetterOrDigit(bestSymbol[0]);
                    bool isSuperscript = !firstGroup
                                         && charHeight > 0
                                         && isLetterOrDigit
                                         && bounds.Height < charHeight * 0.62
                                         && (bounds.Top + bounds.Height / 2) < baselineCenter - charHeight * 0.22
                                         && (bounds.Left - previousRight) < charHeight * 0.6;

                    if (isSuperscript) result.Symbols.Add(FixedSymbol("^", bounds));

                    var item = new RecognizedSymbol { Bounds = bounds };
                    item.Candidates = classified;
                    if (classified.Count == 0 || classified[0].Score > MaxAcceptableScore)
                    {
                        item.Chosen = "?";
                        item.Confidence = 0;
                    }
                    else
                    {
                        item.Chosen = classified[0].Symbol;
                        double best = classified[0].Score;
                        double second = classified.Count > 1 ? classified[1].Score : best + 0.2;
                        // 与第二名的间隔越大越可信
                        item.Confidence = Math.Max(0, Math.Min(1, 0.3 + (second - best) * 5 + (MaxAcceptableScore - best)));
                    }
                    result.Symbols.Add(item);
                    previousRight = bounds.Right;
                    firstGroup = false;
                }

                // 整式级纠错：用「能否解析成 f(x)」约束候选组合，替代逐条硬编码规则。
                // 纯托管、零系统依赖，Win7 SP1 ~ Win11 行为一致。
                var decoded = ExpressionConstrainedDecoder.Decode(result);
                // 兜底：解码器没找到更优解、且括号仍不成对时，再做一次配对修复
                BalanceBrackets(result.Symbols);

                detail = result.Symbols.Count + " 个符号，待确认 " + result.UncertainCount + " 个"
                         + (decoded.Applied ? "，语法纠错 " + decoded.ChangedCount + " 处" : "");
            }
            catch (Exception ex)
            {
                detail = "识别异常：" + ex.Message;
            }
            return result;
        }

        private static RecognizedSymbol FixedSymbol(string text, Rect bounds)
        {
            var item = new RecognizedSymbol { Chosen = text, Confidence = 1, Bounds = bounds };
            item.Candidates.Add(new SymbolCandidate { Symbol = text, Score = 0 });
            return item;
        }

        /// <summary>
        /// 括号配对纠错：手写括号与 c / t / j 字形高度混淆，单靠字形分不开，
        /// 但表达式里 () 必然成对；缺哪边时，从该侧挑「候选里含对应括号」的符号改回来。
        /// 这是 Word 墨迹公式同款的语言层纠错思路，不改动用户可见的候选列表。
        /// </summary>
        private static void BalanceBrackets(List<RecognizedSymbol> symbols)
        {
            int open = symbols.Count(s => s.Chosen == "(");
            int close = symbols.Count(s => s.Chosen == ")");
            if (open == close) return;

            if (close > open)
            {
                int lastClose = symbols.FindLastIndex(s => s.Chosen == ")");
                int start = lastClose < 0 ? symbols.Count - 1 : lastClose - 1;
                for (int i = start; i >= 0 && open < close; i--)
                {
                    var item = symbols[i];
                    if (item.Chosen == "(" || item.Chosen == ")") continue;
                    if (!item.Candidates.Any(c => c.Symbol == "(")) continue;
                    item.Chosen = "(";
                    item.Confidence = Math.Max(item.Confidence, 0.6);
                    open++;
                }
            }
            else
            {
                int firstOpen = symbols.FindIndex(s => s.Chosen == "(");
                int start = firstOpen < 0 ? 0 : firstOpen + 1;
                for (int i = start; i < symbols.Count && close < open; i++)
                {
                    var item = symbols[i];
                    if (item.Chosen == "(" || item.Chosen == ")") continue;
                    if (!item.Candidates.Any(c => c.Symbol == ")")) continue;
                    item.Chosen = ")";
                    item.Confidence = Math.Max(item.Confidence, 0.6);
                    close++;
                }
            }
        }

        // ===== 1. 切分 =====

        /// <summary>用笔画高度的 70 分位数估计主字符高度，排除极短点画与超长分数线。</summary>
        private static double EstimateCharHeight(List<List<Point>> strokes)
        {
            var heights = strokes.Select(s => GetBounds(s).Height).Where(h => h > 1).OrderBy(h => h).ToList();
            if (heights.Count == 0) return 40;
            int index = (int)Math.Floor((heights.Count - 1) * 0.7);
            return Math.Max(8, heights[index]);
        }

        private static double EstimateBaselineCenter(List<List<List<Point>>> groups, double charHeight)
        {
            var normal = groups
                .Select(g => GetBounds(g.SelectMany(x => x).ToList()))
                .Where(b => b.Height >= charHeight * 0.62)
                .ToList();
            if (normal.Count == 0) return 0;
            return normal.Average(b => b.Top + b.Height / 2);
        }

        private static Rect GetBounds(List<Point> points)
        {
            if (points == null || points.Count == 0) return new Rect(0, 0, 0, 0);
            double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
            double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
            return new Rect(minX, minY, Math.Max(0, maxX - minX), Math.Max(0, maxY - minY));
        }

        private static Rect UnionBounds(List<List<List<Point>>> groups)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var group in groups)
            {
                var b = GetBounds(group.SelectMany(g => g).ToList());
                if (b.Width == 0 && b.Height == 0) continue;
                minX = Math.Min(minX, b.Left); minY = Math.Min(minY, b.Top);
                maxX = Math.Max(maxX, b.Right); maxY = Math.Max(maxY, b.Bottom);
            }
            if (minX == double.MaxValue) return new Rect(0, 0, 0, 0);
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// 按 x 区间重叠 / 自适应间隙把笔画聚成符号组。
        /// 间隙 = 0.30 × 主字符高度，不再使用与尺寸无关的 18px 常量。
        /// </summary>
        private static List<List<List<Point>>> GroupIntoSymbols(List<List<Point>> strokes, double charHeight)
        {
            var ordered = strokes
                .Select(pts => new { pts, bounds = GetBounds(pts) })
                .OrderBy(item => item.bounds.Left)
                .ToList();

            double gap = Math.Max(3, charHeight * 0.22);
            var groups = new List<List<List<Point>>>();
            var groupBounds = new List<Rect>();

            foreach (var item in ordered)
            {
                if (groups.Count > 0)
                {
                    int last = groups.Count - 1;
                    Rect current = groupBounds[last];
                    double overlap = Math.Min(current.Right, item.bounds.Right) - Math.Max(current.Left, item.bounds.Left);
                    double minWidth = Math.Min(Math.Max(current.Width, 1), Math.Max(item.bounds.Width, 1));
                    bool xOverlap = overlap > minWidth * 0.30;              // "=" "x" "i" 多笔合并
                    bool closeGap = item.bounds.Left - current.Right <= gap; // 相邻字符间隔
                    if (xOverlap || closeGap)
                    {
                        groups[last].Add(item.pts);
                        groupBounds[last] = Rect.Union(current, item.bounds);
                        continue;
                    }
                }
                groups.Add(new List<List<Point>> { item.pts });
                groupBounds.Add(item.bounds);
            }
            return groups;
        }

        /// <summary>
        /// 有限二维结构：出现一条长度 ≥ 1.5×字符高度的近似水平长笔画，且上下两侧都有内容时，
        /// 分别递归识别上下两部分，输出 (分子)/(分母)。
        /// </summary>
        private static string TryBuildFraction(List<List<List<Point>>> groups, double charHeight)
        {
            // 展平全部笔画，找出分数线候选
            var flat = groups.SelectMany(g => g).ToList();
            List<Point> bar = null;
            foreach (var stroke in flat)
            {
                var b = GetBounds(stroke);
                if (b.Width >= charHeight * 1.5 && b.Height <= Math.Max(4, charHeight * 0.22))
                {
                    if (bar == null || b.Width > GetBounds(bar).Width) bar = stroke;
                }
            }
            if (bar == null) return null;
            double barY = (GetBounds(bar).Top + GetBounds(bar).Bottom) / 2;

            var above = flat.Where(s => s != bar && (GetBounds(s).Top + GetBounds(s).Bottom) / 2 < barY - charHeight * 0.12).ToList();
            var below = flat.Where(s => s != bar && (GetBounds(s).Top + GetBounds(s).Bottom) / 2 > barY + charHeight * 0.12).ToList();
            if (above.Count == 0 || below.Count == 0) return null;

            string numerator = RecognizeFlat(above, charHeight);
            string denominator = RecognizeFlat(below, charHeight);
            if (string.IsNullOrEmpty(numerator) || string.IsNullOrEmpty(denominator)) return null;
            return "(" + numerator + ")/(" + denominator + ")";
        }

        /// <summary>对一组已展平的笔画做一次不含二维结构的识别，返回拼装后的字符串。</summary>
        private static string RecognizeFlat(List<List<Point>> strokes, double charHeight)
        {
            var groups = GroupIntoSymbols(strokes, charHeight);
            var tokens = new List<string>();
            foreach (var group in groups)
            {
                var candidates = ClassifyGroup(group);
                tokens.Add(candidates.Count == 0 || candidates[0].Score > MaxAcceptableScore
                    ? "?" : candidates[0].Symbol);
            }
            return AssembleExpression(tokens);
        }

        // ===== 2. 分类：整组归一化 + 栅格 Chamfer =====

        /// <summary>一个符号组与全部模板比对，返回按符号去重后的 Top-N 候选。</summary>
        private static List<SymbolCandidate> ClassifyGroup(List<List<Point>> group)
        {
            var normalized = NormalizeGroup(group);
            var bestBySymbol = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // 手写常见轻微倾斜，做有限角度搜索（不做全角度，避免把 6/9、+ / x 旋混）
            double[] angles = { -0.28, -0.14, 0, 0.14, 0.28 };
            foreach (double angle in angles)
            {
                var rotated = Rotate(normalized, angle);
                bool[,] ink = Rasterize(rotated);
                float[,] distance = DistanceMap(ink);
                var cells = InkCells(ink);
                if (cells.Count == 0) continue;

                foreach (var template in Templates)
                {
                    double score = Chamfer(cells, template.Distance, template.InkCells, distance);
                    double existing;
                    if (!bestBySymbol.TryGetValue(template.Symbol, out existing) || score < existing)
                        bestBySymbol[template.Symbol] = score;
                }
            }

            return bestBySymbol
                .OrderBy(pair => pair.Value)
                .Take(CandidateCount)
                .Select(pair => new SymbolCandidate { Symbol = pair.Key, Score = pair.Value })
                .ToList();
        }

        /// <summary>整组统一归一化：保持笔画间相对位置与宽高比，居中到 [0,1] 方框。</summary>
        private static List<List<Point>> NormalizeGroup(List<List<Point>> strokes)
        {
            var all = strokes.SelectMany(s => s).ToList();
            var bounds = GetBounds(all);
            double size = Math.Max(Math.Max(bounds.Width, bounds.Height), 1e-6);
            double cx = bounds.Left + bounds.Width / 2;
            double cy = bounds.Top + bounds.Height / 2;

            var result = new List<List<Point>>();
            foreach (var stroke in strokes)
            {
                var scaled = new List<Point>();
                foreach (var point in stroke)
                    scaled.Add(new Point(0.5 + (point.X - cx) / size, 0.5 + (point.Y - cy) / size));
                result.Add(scaled);
            }
            return result;
        }

        private static List<List<Point>> Rotate(List<List<Point>> strokes, double angle)
        {
            if (Math.Abs(angle) < 1e-6) return strokes;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            var result = new List<List<Point>>();
            foreach (var stroke in strokes)
            {
                var rotated = new List<Point>();
                foreach (var point in stroke)
                {
                    double dx = point.X - 0.5, dy = point.Y - 0.5;
                    rotated.Add(new Point(0.5 + dx * cos - dy * sin, 0.5 + dx * sin + dy * cos));
                }
                result.Add(rotated);
            }
            return result;
        }

        private static bool[,] Rasterize(List<List<Point>> strokes)
        {
            var grid = new bool[GridSize, GridSize];
            foreach (var stroke in strokes)
            {
                if (stroke.Count == 0) continue;
                for (int i = 0; i < stroke.Count; i++)
                {
                    Point a = stroke[i];
                    Point b = i + 1 < stroke.Count ? stroke[i + 1] : stroke[i];
                    double ax = a.X * (GridSize - 1), ay = a.Y * (GridSize - 1);
                    double bx = b.X * (GridSize - 1), by = b.Y * (GridSize - 1);
                    double length = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                    int steps = Math.Max(1, (int)Math.Ceiling(length * 2));
                    for (int k = 0; k <= steps; k++)
                    {
                        double t = (double)k / steps;
                        int x = (int)Math.Round(ax + (bx - ax) * t);
                        int y = (int)Math.Round(ay + (by - ay) * t);
                        if (x >= 0 && x < GridSize && y >= 0 && y < GridSize) grid[y, x] = true;
                    }
                }
            }
            // 单像素线在 28x28 上过细，做一次 4 邻域膨胀提升对轻微形变的容错
            var dilated = (bool[,])grid.Clone();
            for (int y = 0; y < GridSize; y++)
                for (int x = 0; x < GridSize; x++)
                {
                    if (!grid[y, x]) continue;
                    if (y > 0) dilated[y - 1, x] = true;
                    if (y < GridSize - 1) dilated[y + 1, x] = true;
                    if (x > 0) dilated[y, x - 1] = true;
                    if (x < GridSize - 1) dilated[y, x + 1] = true;
                }
            return dilated;
        }

        private static List<Point> InkCells(bool[,] ink)
        {
            var cells = new List<Point>();
            for (int y = 0; y < GridSize; y++)
                for (int x = 0; x < GridSize; x++)
                    if (ink[y, x]) cells.Add(new Point(x, y));
            return cells;
        }

        /// <summary>两遍 chamfer 距离变换（3-4 近似的 1 / √2 步长）。</summary>
        private static float[,] DistanceMap(bool[,] ink)
        {
            const float inf = 1e6f;
            const float diagonal = 1.41421356f;
            var d = new float[GridSize, GridSize];
            for (int y = 0; y < GridSize; y++)
                for (int x = 0; x < GridSize; x++)
                    d[y, x] = ink[y, x] ? 0 : inf;

            for (int y = 0; y < GridSize; y++)
                for (int x = 0; x < GridSize; x++)
                {
                    float v = d[y, x];
                    if (y > 0)
                    {
                        v = Math.Min(v, d[y - 1, x] + 1);
                        if (x > 0) v = Math.Min(v, d[y - 1, x - 1] + diagonal);
                        if (x < GridSize - 1) v = Math.Min(v, d[y - 1, x + 1] + diagonal);
                    }
                    if (x > 0) v = Math.Min(v, d[y, x - 1] + 1);
                    d[y, x] = v;
                }

            for (int y = GridSize - 1; y >= 0; y--)
                for (int x = GridSize - 1; x >= 0; x--)
                {
                    float v = d[y, x];
                    if (y < GridSize - 1)
                    {
                        v = Math.Min(v, d[y + 1, x] + 1);
                        if (x > 0) v = Math.Min(v, d[y + 1, x - 1] + diagonal);
                        if (x < GridSize - 1) v = Math.Min(v, d[y + 1, x + 1] + diagonal);
                    }
                    if (x < GridSize - 1) v = Math.Min(v, d[y, x + 1] + 1);
                    d[y, x] = v;
                }
            return d;
        }

        /// <summary>双向平均最近距离，除以网格边长归一化到 0~2。</summary>
        private static double Chamfer(List<Point> a, float[,] distB, List<Point> b, float[,] distA)
        {
            if (a.Count == 0 || b.Count == 0) return double.MaxValue;
            double sumA = 0, sumB = 0;
            foreach (var point in a) sumA += distB[(int)point.Y, (int)point.X];
            foreach (var point in b) sumB += distA[(int)point.Y, (int)point.X];
            return (sumA / a.Count + sumB / b.Count) / GridSize;
        }

        // ===== 3. 模板（100x100 理想字形，多笔用多段） =====

        private static List<Point> Path(params double[] coords)
        {
            var list = new List<Point>();
            for (int i = 0; i + 1 < coords.Length; i += 2) list.Add(new Point(coords[i], coords[i + 1]));
            return list;
        }

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

        private static TemplateEntry[] BuildTemplates()
        {
            var list = new List<TemplateEntry>();

            void Add(string symbol, params List<Point>[] strokes)
            {
                var normalized = NormalizeGroup(strokes.ToList());
                var ink = Rasterize(normalized);
                list.Add(new TemplateEntry
                {
                    Symbol = symbol,
                    Distance = DistanceMap(ink),
                    InkCells = InkCells(ink)
                });
            }

            // ---- 数字（含常见手写变体）----
            Add("0", Arc(50, 50, 26, 38, -Math.PI / 2, 3 * Math.PI / 2, 26));
            Add("0", Arc(50, 50, 26, 38, -Math.PI / 2, 3 * Math.PI / 2, 26), Path(34, 70, 66, 30));
            Add("1", Path(40, 26, 52, 14, 54, 20, 54, 84));
            Add("1", Path(32, 40, 54, 14, 54, 84), Path(30, 84, 76, 84));
            Add("2", Path(26, 34, 36, 18, 54, 12, 72, 22, 74, 38, 64, 52, 44, 66, 26, 82, 26, 86, 76, 86));
            Add("3", Path(28, 22, 48, 12, 68, 22, 72, 38, 58, 50, 44, 50, 62, 52, 74, 66, 68, 82, 48, 88, 28, 80));
            Add("4", Path(60, 12, 26, 60, 76, 60), Path(60, 12, 60, 88));
            Add("4", Path(58, 14, 28, 56, 28, 88, 74, 88), Path(28, 56, 74, 56));
            Add("5", Path(70, 16, 36, 16, 34, 44, 56, 44, 70, 58, 68, 76, 48, 88, 28, 78));
            Add("6", Path(68, 14, 46, 28, 32, 52, 36, 76, 56, 88, 72, 74, 70, 52, 52, 42, 36, 52));
            Add("7", Path(24, 18, 76, 18, 42, 88));
            Add("7", Path(24, 18, 76, 18, 44, 88), Path(34, 52, 66, 52));
            Add("8", Arc(50, 30, 22, 18, Math.PI / 2, -3 * Math.PI / 2, 22), Arc(50, 70, 27, 20, -Math.PI / 2, 3 * Math.PI / 2, 22));
            Add("9", Path(68, 20, 46, 14, 32, 32, 36, 52, 56, 60, 70, 48, 66, 26, 66, 52, 60, 88));
            // 常见手写变体：平底 2、扁 3、连笔 5、开口 6、带尾 9
            Add("2", Path(26, 36, 34, 20, 54, 14, 72, 24, 70, 42, 56, 54, 26, 84, 76, 84));
            Add("3", Path(28, 24, 46, 16, 64, 24, 62, 44, 46, 50, 64, 56, 68, 74, 48, 86, 28, 78));
            Add("5", Path(68, 16, 34, 16, 34, 42, 58, 40, 70, 54, 66, 74, 46, 84, 28, 74));
            Add("6", Path(66, 14, 42, 32, 34, 58, 42, 80, 62, 82, 72, 64, 64, 48, 46, 46, 36, 56));
            Add("9", Path(66, 18, 46, 16, 34, 32, 38, 52, 58, 56, 68, 42, 64, 24, 64, 54, 52, 86, 36, 92));

            // ---- 小写字母（补齐 sqrt / sin / cos / tan / cot / log / ln / exp / pi / abs 所需字母）----
            Add("a", Arc(48, 56, 24, 22, -Math.PI / 2, 3 * Math.PI / 2, 22), Path(70, 34, 70, 84));
            Add("a", Arc(46, 58, 24, 22, 0, 2 * Math.PI, 24), Path(70, 36, 68, 86));
            Add("b", Path(28, 10, 30, 86), Arc(52, 62, 22, 24, -Math.PI / 2, 3 * Math.PI / 2, 22));
            // c 是左凸弧（开口朝右），此前写成右凸后与 ")" 撞形状，导致 sin(x) 末尾被判成 c
            Add("c", Arc(44, 50, 24, 26, Math.PI - Math.PI / 2.2, Math.PI + Math.PI / 2.2, 20));
            Add("d", Arc(48, 62, 22, 24, -Math.PI / 2, 3 * Math.PI / 2, 22), Path(70, 10, 70, 86));
            Add("e", Path(28, 52, 72, 50, 66, 32, 48, 28, 32, 42, 34, 66, 52, 80, 72, 70));
            Add("e", Path(28, 50, 72, 50), Arc(50, 50, 24, 32, -Math.PI, Math.PI / 2, 20, true));
            Add("f", Path(66, 16, 52, 14, 46, 30, 46, 88), Path(28, 42, 66, 42));
            Add("g", Arc(50, 42, 22, 22, -Math.PI / 2, 3 * Math.PI / 2, 20), Path(72, 42, 70, 82, 48, 96, 28, 80));
            Add("h", Path(28, 10, 30, 86), Path(30, 52, 42, 34, 58, 36, 70, 54, 70, 86));
            Add("i", Path(48, 34, 50, 86));
            Add("i", Path(48, 34, 50, 86), Path(46, 16, 52, 20));
            Add("j", Path(56, 34, 56, 78, 44, 92, 28, 84), Path(54, 16, 60, 20));
            Add("k", Path(28, 10, 30, 86), Path(70, 34, 34, 60), Path(40, 56, 72, 86));
            Add("l", Path(44, 10, 46, 86));
            Add("l", Path(44, 10, 44, 72, 58, 84));
            Add("m", Path(24, 86, 26, 40, 38, 30, 50, 40, 50, 86), Path(50, 40, 62, 30, 74, 40, 74, 86));
            Add("n", Path(26, 86, 28, 40, 42, 30, 58, 38, 72, 54, 72, 86));
            Add("o", Arc(50, 50, 24, 32, -Math.PI / 2, 3 * Math.PI / 2, 24));
            Add("p", Path(28, 24, 30, 96), Arc(52, 52, 22, 22, -Math.PI / 2, 3 * Math.PI / 2, 22));
            Add("q", Arc(48, 46, 22, 24, -Math.PI / 2, 3 * Math.PI / 2, 22), Path(70, 24, 70, 90, 78, 96));
            Add("r", Path(32, 86, 32, 34, 48, 26, 64, 34));
            Add("s", Path(70, 22, 50, 12, 32, 24, 40, 40, 58, 52, 68, 68, 58, 84, 34, 86));
            Add("t", Path(48, 12, 46, 68, 60, 84), Path(26, 40, 72, 40));
            Add("t", Path(48, 12, 46, 68, 60, 84));
            Add("u", Path(26, 26, 30, 66, 44, 84, 62, 76, 70, 44, 70, 26, 70, 84));
            Add("v", Path(24, 20, 50, 88, 76, 20));
            Add("w", Path(20, 20, 34, 88, 50, 46, 66, 88, 80, 20));
            Add("x", Path(26, 22, 74, 84), Path(74, 22, 26, 84));
            Add("x", Path(26, 22, 74, 84, 26, 84, 74, 22));
            Add("y", Path(26, 22, 48, 62, 72, 22), Path(56, 62, 46, 92, 28, 94));
            Add("y", Path(30, 20, 32, 62, 46, 82, 64, 72, 72, 44, 72, 20, 70, 84, 46, 96));
            Add("z", Path(26, 22, 74, 22, 28, 84, 74, 84));
            Add("z", Path(26, 22, 74, 22, 28, 84, 74, 84), Path(44, 50, 60, 50));

            // ---- 运算符 ----
            Add("+", Path(50, 20, 50, 80), Path(20, 50, 80, 50));
            Add("-", Path(18, 50, 82, 50));
            Add("=", Path(20, 34, 80, 34), Path(20, 64, 80, 64));
            Add("/", Path(24, 12, 76, 88));
            Add("*", Path(50, 26, 50, 74), Path(28, 38, 72, 62), Path(72, 38, 28, 62));
            Add("*", Path(48, 44, 52, 48));
            Add("^", Path(26, 50, 50, 22, 74, 50));
            // 括号必须一个是左凸、一个是右凸；此前两个模板都写成右凸弧，
            // 归一化后几乎相同，导致手写 "(" 被判成 t / c（sin(x) → sintxc）。
            Add("(", Arc(34, 50, 30, 38, Math.PI - Math.PI / 2.5, Math.PI + Math.PI / 2.5, 20));
            Add("(", Arc(38, 50, 34, 40, Math.PI - Math.PI / 2.1, Math.PI + Math.PI / 2.1, 20));
            Add(")", Arc(66, 50, 30, 38, -Math.PI / 2.5, Math.PI / 2.5, 20));
            Add(")", Arc(62, 50, 34, 40, -Math.PI / 2.1, Math.PI / 2.1, 20));
            Add("sqrt", Path(20, 50, 38, 70, 52, 54, 84, 16));
            Add("sqrt", Path(20, 50, 38, 70, 52, 54, 84, 16), Path(50, 20, 88, 12));
            // 手写里常把 () 写成带直角的方括号，归一化后与圆弧括号差别明显
            Add("(", Path(70, 12, 44, 12, 44, 88, 70, 88));
            Add(")", Path(30, 12, 56, 12, 56, 88, 30, 88));
            Add(".", Path(48, 80, 52, 84));
            Add(",", Path(48, 78, 52, 84, 46, 92));

            return list.ToArray();
        }

        // ===== 4. 符号序列 → 表达式 =====

        private static readonly string[] FunctionWords =
        {
            "sqrt", "asin", "acos", "atan", "sinh", "cosh", "tanh",
            "sin", "cos", "tan", "cot", "log", "abs", "exp", "arcsin",
            "ln", "pi"
        };

        private static bool IsDigit(string token)
        {
            return token != null && token.Length == 1 && char.IsDigit(token[0]);
        }

        private static bool IsLetter(string token)
        {
            return token != null && token.Length == 1 && char.IsLetter(token[0]);
        }

        /// <summary>合并函数词、补隐式乘号、拼接成可解析表达式。</summary>
        internal static string AssembleExpression(List<string> tokens)
        {
            var merged = new List<string>();
            int i = 0;
            while (i < tokens.Count)
            {
                string matched = null;
                foreach (var word in FunctionWords.OrderByDescending(w => w.Length))
                {
                    if (i + word.Length > tokens.Count) continue;
                    bool ok = true;
                    for (int k = 0; k < word.Length; k++)
                    {
                        if (!string.Equals(tokens[i + k], word[k].ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (ok) { matched = word; break; }
                }
                if (matched != null)
                {
                    merged.Add(matched);
                    i += matched.Length;
                }
                else
                {
                    merged.Add(tokens[i]);
                    i++;
                }
            }

            var sb = new StringBuilder();
            bool sqrtOpen = false;
            for (int n = 0; n < merged.Count; n++)
            {
                string current = merged[n];
                bool isBinaryOperator = current == "+" || current == "-" || current == "*"
                                        || current == "/" || current == "^" || current == ")";
                // 手写 √ 常不带括号，自动补到下一个二元运算符为止：√x+1 → sqrt(x)+1
                if (sqrtOpen && isBinaryOperator)
                {
                    sb.Append(')');
                    sqrtOpen = false;
                }

                if (n > 0)
                {
                    string previous = merged[n - 1];
                    bool prevIsValue = IsDigit(previous) || previous == ")" || previous == "pi" || previous == "e";
                    bool curIsFactor = current == "(" || current == "x" || IsDigit(current)
                                       || FunctionWords.Contains(current, StringComparer.OrdinalIgnoreCase);
                    // 函数词后紧跟 "(" 是函数调用 sin(x)，不能插乘号
                    bool isFunctionCall = FunctionWords.Contains(previous, StringComparer.OrdinalIgnoreCase)
                                          && previous != "pi" && previous != "e" && current == "(";
                    // 相邻数字要连成多位数（手写 "12" 不能被拼成 "1*2"）
                    bool bothDigits = IsDigit(previous) && IsDigit(current);
                    if (prevIsValue && curIsFactor && !isFunctionCall && !bothDigits) sb.Append('*');
                }

                if (current == "sqrt" && (n + 1 >= merged.Count || merged[n + 1] != "("))
                {
                    sb.Append("sqrt(");
                    sqrtOpen = true;
                }
                else
                {
                    sb.Append(current);
                }
            }
            if (sqrtOpen) sb.Append(')');
            return sb.ToString();
        }
    }
}
