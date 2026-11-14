using System;
using System.Collections.Generic;
using System.Linq;

namespace Ink_Canvas.Plugins.FunctionGraph
{
    /// <summary>一次语法约束解码的结果，供窗口提示「已自动纠正了几处」。</summary>
    internal sealed class DecodeResult
    {
        public bool Applied;
        public int ChangedCount;
        public string Expression;
    }

    /// <summary>
    /// 语法约束的候选束搜索解码器 —— 纯托管、零系统依赖，所有能跑 .NET Framework 4.7.2 的
    /// Windows（Win7 SP1 ~ Win11 / Server）行为完全一致。
    ///
    /// 思路：单符号 Chamfer 匹配只能给出「这个字形最像什么」，无法解决整式级歧义。
    /// 但函数表达式有严格语法 —— 我们手里已经有一个现成的合法性检验器 <see cref="ExpressionParser.Compile"/>。
    /// 于是把每个符号的 Top-N 候选当作搜索空间做 beam search，用「几何分数 + 偏离首选的惩罚 +
    /// 能否被解析成 f(x)」联合打分，自动挑出既像手写、又构成合法表达式的符号组合。
    ///
    /// 典型效果：sin(x) 的左括号被判成 c、x^2 的 ^ 被判成 -、1/x 的 / 被判成 1，
    /// 这些组合在语法上不成立，会被自动替换为候选里能构成合法表达式的那一个。
    /// 这比 v2 里硬编码的 BalanceBrackets 更通用，也不需要任何系统组件或联网。
    /// </summary>
    internal static class ExpressionConstrainedDecoder
    {
        private const int BeamWidth = 20;
        private const int MaxSymbols = 24;      // 超长表达式不做搜索，避免触摸书写时卡顿
        private const double ChangePenalty = 0.09;       // 改动一个符号的代价，防止为凑语法乱改
        private const double PlaceholderPenalty = 0.34;  // "?" 占位强烈不鼓励
        private const double ParseBonus = -0.42;         // 「可解析且含 x」的奖励
        private const double NonUsablePenalty = 0.18;    // 可解析但不含 x（常数式）不算有效函数
        private const double UnparseablePenalty = 0.30;
        private const double FallbackScore = 0.45;

        private sealed class Option
        {
            public string Symbol;
            public double Score;
        }

        private sealed class State
        {
            public string[] Tokens;
            public double Cost;
        }

        /// <summary>对识别结果做整式级纠错，直接改写各符号的 Chosen；失败时原样保留。</summary>
        public static DecodeResult Decode(RecognitionResult result)
        {
            var output = new DecodeResult
            {
                Expression = result == null ? "" : result.BuildExpression()
            };
            if (result == null || result.Symbols == null ||
                result.Symbols.Count < 2 || result.Symbols.Count > MaxSymbols)
                return output;

            int count = result.Symbols.Count;
            var options = new List<Option>[count];
            var original = new string[count];
            for (int i = 0; i < count; i++)
            {
                var symbol = result.Symbols[i];
                original[i] = string.IsNullOrEmpty(symbol.Chosen) ? "?" : symbol.Chosen;
                options[i] = BuildOptions(symbol, original[i]);
            }

            var beam = new List<State> { new State { Tokens = new string[count], Cost = 0 } };
            for (int pos = 0; pos < count; pos++)
            {
                var expanded = new List<State>();
                foreach (var state in beam)
                {
                    foreach (var option in options[pos])
                    {
                        var tokens = (string[])state.Tokens.Clone();
                        tokens[pos] = option.Symbol;
                        if (!IsPrefixViable(tokens, pos + 1)) continue;

                        double cost = state.Cost + option.Score;
                        if (option.Symbol != original[pos]) cost += ChangePenalty;
                        if (option.Symbol == "?") cost += PlaceholderPenalty;
                        expanded.Add(new State { Tokens = tokens, Cost = cost });
                    }
                }
                if (expanded.Count == 0) return output;
                beam = expanded.OrderBy(s => s.Cost).Take(BeamWidth).ToList();
            }

            // 只有到末尾才知道表达式是否合法。函数绘图要求「可解析且含 x」，
            // 否则像 "=" 被改成 "0"、"+" 被改成 "4" 这类常数式会假装成有效纠错。
            State best = null;
            double bestCost = double.MaxValue;
            bool bestUsable = false;
            foreach (var state in beam)
            {
                string expression = HandwritingMathRecognizer.AssembleExpression(state.Tokens.ToList());
                if (string.IsNullOrWhiteSpace(expression)) continue;

                string error;
                bool parseable = ExpressionParser.Compile(expression, out error) != null;
                bool usable = parseable && expression.IndexOf('x') >= 0;
                double cost = state.Cost
                              + (usable ? ParseBonus : (parseable ? NonUsablePenalty : UnparseablePenalty));
                if (cost < bestCost) { bestCost = cost; best = state; bestUsable = usable; }
            }
            // 找不到有效函数组合时保留原样，交给用户用候选条手动修正
            if (best == null || !bestUsable) return output;

            // 基线 = 完全不纠错
            string baselineExpression = HandwritingMathRecognizer.AssembleExpression(original.ToList());
            string baselineError;
            bool baselineParseable = ExpressionParser.Compile(baselineExpression, out baselineError) != null;
            bool baselineUsable = baselineParseable && baselineExpression.IndexOf('x') >= 0;
            double baselineCost = 0;
            for (int i = 0; i < count; i++) baselineCost += ScoreOf(options[i], original[i]);
            baselineCost += baselineUsable ? ParseBonus
                          : (baselineParseable ? NonUsablePenalty : UnparseablePenalty);
            // 基线本来就合法时，必须有实质改进才允许改写
            double margin = baselineUsable ? 0.02 : 0.0;
            if (bestCost >= baselineCost - margin) return output;

            int changed = 0;
            for (int i = 0; i < count; i++)
                if (best.Tokens[i] != original[i]) changed++;
            // 改动比例过高说明是「换了一个式子」，不是纠错，直接放弃
            if (changed > Math.Max(1, count / 3)) return output;

            for (int i = 0; i < count; i++)
            {
                if (best.Tokens[i] == original[i]) continue;
                var symbol = result.Symbols[i];
                symbol.Chosen = best.Tokens[i];
                symbol.WasAutoCorrected = true;
                symbol.Confidence = Math.Max(symbol.Confidence, 0.55);
            }
            output.Applied = true;
            output.ChangedCount = changed;
            output.Expression = result.BuildExpression();
            return output;
        }

        private static List<Option> BuildOptions(RecognizedSymbol symbol, string original)
        {
            var list = new List<Option>();
            double bestScore = symbol.Candidates.Count > 0
                ? symbol.Candidates.Min(c => c.Score)
                : FallbackScore;

            // 首选始终在场（即使它不是候选里的第一名，例如已被上标/括号逻辑改写过）
            list.Add(new Option { Symbol = original, Score = bestScore });
            foreach (var candidate in symbol.Candidates.Take(8))
            {
                if (list.Any(o => o.Symbol == candidate.Symbol)) continue;
                list.Add(new Option { Symbol = candidate.Symbol, Score = candidate.Score });
            }

            // 语法常用符号兜底：Top-N 里没有 "(" / ")" 时，搜索空间里也必须能取到
            foreach (var extra in new[] { "(", ")", "+", "-", "*", "/", "^", "x", "2" })
            {
                if (list.Count >= 11) break;
                if (list.Any(o => o.Symbol == extra)) continue;
                list.Add(new Option { Symbol = extra, Score = FallbackScore });
            }
            return list;
        }

        private static double ScoreOf(List<Option> options, string symbol)
        {
            foreach (var option in options)
                if (option.Symbol == symbol) return option.Score;
            return FallbackScore;
        }

        /// <summary>前缀级剪枝：括号不能先闭后开、不能连续两个二元运算符。</summary>
        private static bool IsPrefixViable(string[] tokens, int length)
        {
            int depth = 0;
            string previous = null;
            for (int i = 0; i < length; i++)
            {
                string current = tokens[i];
                if (current == "(") depth++;
                else if (current == ")")
                {
                    depth--;
                    if (depth < 0) return false;
                }
                if (IsOperator(current) && previous != null && IsOperator(previous)) return false;
                previous = current;
            }
            return true;
        }

        private static bool IsOperator(string token)
        {
            return token == "+" || token == "-" || token == "*" || token == "/" || token == "^";
        }
    }
}
