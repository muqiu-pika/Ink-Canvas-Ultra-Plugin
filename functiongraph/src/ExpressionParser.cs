using System;
using System.Collections.Generic;
using System.Globalization;

namespace Ink_Canvas.Plugins.FunctionGraph
{
    /// <summary>
    /// 受限数学表达式解析器（自实现递归下降，不引入第三方包）。
    /// 支持：x、数字、+ - * / ^、括号、sin/cos/tan/cot/sqrt/abs/log/ln/exp、pi/e，
    /// 以及常见手写/识别产物的归一化（全角符号、× ÷ − √ π、上标 ² ³、隐式乘法 2x/2(x+1)）。
    /// 解析失败通过 out error 返回原因，由调用方提示用户手动修改文本。
    /// </summary>
    internal static class ExpressionParser
    {
        /// <summary>把表达式文本编译成 f(x) 委托；失败返回 null 并给出 error。</summary>
        public static Func<double, double> Compile(string text, out string error)
        {
            error = null;
            try
            {
                string normalized = Normalize(text);
                if (string.IsNullOrEmpty(normalized))
                {
                    error = "表达式为空";
                    return null;
                }

                var tokens = Tokenize(normalized, out error);
                if (tokens == null) return null;

                int pos = 0;
                var root = ParseExpression(tokens, ref pos, out error);
                if (root == null) return null;
                if (pos != tokens.Count)
                {
                    error = "存在多余的字符 \"" + tokens[pos].Text + "\"";
                    return null;
                }

                return x =>
                {
                    try
                    {
                        double v = root(x);
                        if (double.IsNaN(v)) return double.NaN;
                        return v;
                    }
                    catch
                    {
                        return double.NaN;
                    }
                };
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>常见识别结果归一化：去掉 y=/f(x)= 前缀，替换全角与数学符号。</summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            string s = text.Trim();

            // 去掉 "y ="、"f(x)="、"g(x) =" 之类前缀
            int eq = s.IndexOf('=');
            if (eq >= 0 && eq <= 5)
            {
                string left = s.Substring(0, eq);
                if (left.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)
                    || left.Trim().Equals("f", StringComparison.OrdinalIgnoreCase)
                    || left.Trim().Equals("g", StringComparison.OrdinalIgnoreCase)
                    || (left.Contains("(") && left.Contains(")")))
                {
                    s = s.Substring(eq + 1);
                }
            }

            s = s.Replace("×", "*").Replace("·", "*").Replace("⋅", "*")
                 .Replace("÷", "/")
                 .Replace("−", "-").Replace("–", "-").Replace("—", "-")
                 .Replace("＋", "+")
                 .Replace("（", "(").Replace("）", ")")
                 .Replace("，", ",")
                 .Replace("√", "sqrt")
                 .Replace("π", "pi")
                 .Replace("²", "^2")
                 .Replace("³", "^3")
                 .Replace("[", "(").Replace("]", ")")
                 .Replace("{", "(").Replace("}", ")");

            return s;
        }

        private enum TokenType { Number, Identifier, Operator, LeftParen, RightParen }

        private sealed class Token
        {
            public TokenType Type;
            public string Text;
        }

        private static List<Token> Tokenize(string s, out string error)
        {
            error = null;
            var tokens = new List<Token>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    int start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                    tokens.Add(new Token { Type = TokenType.Number, Text = s.Substring(start, i - start) });
                    continue;
                }

                if (char.IsLetter(c))
                {
                    int start = i;
                    while (i < s.Length && (char.IsLetter(s[i]))) i++;
                    tokens.Add(new Token { Type = TokenType.Identifier, Text = s.Substring(start, i - start).ToLowerInvariant() });
                    continue;
                }

                if (c == '(') { tokens.Add(new Token { Type = TokenType.LeftParen, Text = "(" }); i++; continue; }
                if (c == ')') { tokens.Add(new Token { Type = TokenType.RightParen, Text = ")" }); i++; continue; }

                if (c == '+' || c == '-' || c == '*' || c == '/' || c == '^')
                {
                    tokens.Add(new Token { Type = TokenType.Operator, Text = c.ToString() });
                    i++;
                    continue;
                }

                error = "无法识别的字符 \"" + c + "\"";
                return null;
            }

            // 隐式乘法：number/)/x/函数名 与 number/(/x/函数名 相邻时插入 *
            var expanded = new List<Token>();
            for (int k = 0; k < tokens.Count; k++)
            {
                var cur = tokens[k];
                expanded.Add(cur);
                if (k + 1 >= tokens.Count) break;
                var next = tokens[k + 1];
                bool curEndsValue = cur.Type == TokenType.Number
                                    || cur.Type == TokenType.RightParen
                                    || (cur.Type == TokenType.Identifier);
                bool nextStartsValue = next.Type == TokenType.Number
                                       || next.Type == TokenType.LeftParen
                                       || (next.Type == TokenType.Identifier);
                if (curEndsValue && nextStartsValue)
                {
                    // "sin (x)"、"sin x" 等函数名后接括号/参数不加乘号
                    if (cur.Type == TokenType.Identifier && IsFunctionName(cur.Text)) continue;
                    expanded.Add(new Token { Type = TokenType.Operator, Text = "*" });
                }
            }

            return expanded;
        }

        private static bool IsFunctionName(string id)
        {
            switch (id)
            {
                case "sin": case "cos": case "tan": case "cot":
                case "sqrt": case "abs": case "log": case "ln": case "exp":
                    return true;
                default:
                    return false;
            }
        }

        private static Func<double, double> ParseExpression(List<Token> tokens, ref int pos, out string error)
        {
            error = null;
            var left = ParseTerm(tokens, ref pos, out error);
            if (left == null) return null;

            while (pos < tokens.Count)
            {
                var t = tokens[pos];
                if (t.Type == TokenType.Operator && (t.Text == "+" || t.Text == "-"))
                {
                    pos++;
                    var right = ParseTerm(tokens, ref pos, out error);
                    if (right == null) return null;
                    bool add = t.Text == "+";
                    var l = left; var r = right;
                    left = add ? (Func<double, double>)(x => l(x) + r(x)) : (x => l(x) - r(x));
                }
                else
                {
                    break;
                }
            }

            return left;
        }

        private static Func<double, double> ParseTerm(List<Token> tokens, ref int pos, out string error)
        {
            error = null;
            var left = ParseUnary(tokens, ref pos, out error);
            if (left == null) return null;

            while (pos < tokens.Count)
            {
                var t = tokens[pos];
                if (t.Type == TokenType.Operator && (t.Text == "*" || t.Text == "/"))
                {
                    pos++;
                    var right = ParseUnary(tokens, ref pos, out error);
                    if (right == null) return null;
                    bool mul = t.Text == "*";
                    var l = left; var r = right;
                    left = mul ? (Func<double, double>)(x => l(x) * r(x)) : (x => r(x) == 0 ? double.NaN : l(x) / r(x));
                }
                else
                {
                    break;
                }
            }

            return left;
        }

        private static Func<double, double> ParseUnary(List<Token> tokens, ref int pos, out string error)
        {
            error = null;
            if (pos < tokens.Count && tokens[pos].Type == TokenType.Operator
                && (tokens[pos].Text == "-" || tokens[pos].Text == "+"))
            {
                bool neg = tokens[pos].Text == "-";
                pos++;
                var operand = ParseUnary(tokens, ref pos, out error);
                if (operand == null) return null;
                return neg ? (Func<double, double>)(x => -operand(x)) : operand;
            }

            return ParsePower(tokens, ref pos, out error);
        }

        private static Func<double, double> ParsePower(List<Token> tokens, ref int pos, out string error)
        {
            error = null;
            var baseFn = ParsePrimary(tokens, ref pos, out error);
            if (baseFn == null) return null;

            if (pos < tokens.Count && tokens[pos].Type == TokenType.Operator && tokens[pos].Text == "^")
            {
                pos++;
                // 右结合：^ 的指数部分允许再带一元负号
                var expFn = ParseUnary(tokens, ref pos, out error);
                if (expFn == null) return null;
                return x => Math.Pow(baseFn(x), expFn(x));
            }

            return baseFn;
        }

        private static Func<double, double> ParsePrimary(List<Token> tokens, ref int pos, out string error)
        {
            error = null;
            if (pos >= tokens.Count)
            {
                error = "表达式意外结束";
                return null;
            }

            var t = tokens[pos];

            if (t.Type == TokenType.Number)
            {
                double value;
                if (!double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    error = "数字格式错误 \"" + t.Text + "\"";
                    return null;
                }
                pos++;
                return x => value;
            }

            if (t.Type == TokenType.Identifier)
            {
                pos++;
                if (string.Equals(t.Text, "x", StringComparison.Ordinal))
                {
                    return x => x;
                }
                if (string.Equals(t.Text, "pi", StringComparison.Ordinal))
                {
                    return x => Math.PI;
                }
                if (string.Equals(t.Text, "e", StringComparison.Ordinal))
                {
                    return x => Math.E;
                }

                // 函数调用：名称后必须跟括号表达式（也允许 sin x 形式）
                Func<double, double> arg;
                if (pos < tokens.Count && tokens[pos].Type == TokenType.LeftParen)
                {
                    pos++;
                    arg = ParseExpression(tokens, ref pos, out error);
                    if (arg == null) return null;
                    if (pos >= tokens.Count || tokens[pos].Type != TokenType.RightParen)
                    {
                        error = "缺少右括号";
                        return null;
                    }
                    pos++;
                }
                else if (IsFunctionName(t.Text))
                {
                    arg = ParseUnary(tokens, ref pos, out error);
                    if (arg == null) return null;
                }
                else
                {
                    error = "无法识别的名称 \"" + t.Text + "\"";
                    return null;
                }

                return BuildFunction(t.Text, arg, out error);
            }

            if (t.Type == TokenType.LeftParen)
            {
                pos++;
                var inner = ParseExpression(tokens, ref pos, out error);
                if (inner == null) return null;
                if (pos >= tokens.Count || tokens[pos].Type != TokenType.RightParen)
                {
                    error = "缺少右括号";
                    return null;
                }
                pos++;
                return inner;
            }

            error = "此处不应出现 \"" + t.Text + "\"";
            return null;
        }

        private static Func<double, double> BuildFunction(string name, Func<double, double> arg, out string error)
        {
            error = null;
            switch (name)
            {
                case "sin": return x => Math.Sin(arg(x));
                case "cos": return x => Math.Cos(arg(x));
                case "tan": return x => Math.Tan(arg(x));
                case "cot": return x => { double v = Math.Tan(arg(x)); return v == 0 ? double.NaN : 1 / v; };
                case "sqrt": return x => { double v = arg(x); return v >= 0 ? Math.Sqrt(v) : double.NaN; };
                case "abs": return x => Math.Abs(arg(x));
                case "log": return x => { double v = arg(x); return v > 0 ? Math.Log10(v) : double.NaN; };
                case "ln": return x => { double v = arg(x); return v > 0 ? Math.Log(v) : double.NaN; };
                case "exp": return x => Math.Exp(arg(x));
                default:
                    error = "不支持的函数 \"" + name + "\"";
                    return null;
            }
        }
    }
}
