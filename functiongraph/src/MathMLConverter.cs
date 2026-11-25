using System;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Ink_Canvas.Plugins.FunctionGraph
{
    /// <summary>
    /// 把 Windows Math Input Control 经 _IMathInputControlEvents::Insert 回传的 MathML
    /// 转成 <see cref="ExpressionParser"/> 能编译的纯文本表达式。
    ///
    /// Word 墨迹公式的识别结果带完整二维结构（分式、上下标、根号），这正是内置
    /// 单符号识别器最难还原的部分，因此转换时优先保留结构而不是丢弃。
    /// 忽略命名空间前缀（MathML 常带 mml: 或默认 xmlns），只按 LocalName 分派。
    /// </summary>
    internal static class MathMLConverter
    {
        public static string ToExpression(string mathML, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(mathML))
                {
                    error = "识别结果为空";
                    return null;
                }

                XDocument document = XDocument.Parse(mathML, LoadOptions.None);
                if (document.Root == null)
                {
                    error = "MathML 无根节点";
                    return null;
                }

                var builder = new StringBuilder();
                bool unsupported = false;
                ConvertElement(document.Root, builder, ref unsupported);
                if (unsupported)
                {
                    error = "包含矩阵/多行表格等函数绘图暂不支持的结构";
                    return null;
                }

                string expression = Clean(builder.ToString());
                if (string.IsNullOrWhiteSpace(expression))
                {
                    error = "未能从 MathML 解析出表达式";
                    return null;
                }
                return ExpressionParser.Normalize(expression);
            }
            catch (Exception ex)
            {
                error = "MathML 解析失败：" + ex.Message;
                return null;
            }
        }

        private static void ConvertElement(XElement element, StringBuilder output, ref bool unsupported)
        {
            if (element == null || unsupported) return;
            string name = element.Name.LocalName;

            switch (name)
            {
                case "math":
                case "mrow":
                case "mstyle":
                case "mpadded":
                case "merror":
                case "semantics":
                    foreach (XElement child in element.Elements())
                        ConvertElement(child, output, ref unsupported);
                    return;

                case "mn":
                    output.Append(Clean(element.Value));
                    return;

                case "mi":
                    output.Append(MapIdentifier(Clean(element.Value)));
                    return;

                case "mo":
                    output.Append(MapOperator(Clean(element.Value)));
                    return;

                case "mtext":
                    output.Append(Clean(element.Value));
                    return;

                case "msup":
                {
                    var children = element.Elements().ToList();
                    if (children.Count >= 2)
                    {
                        string basePart = Render(children[0], ref unsupported);
                        string exponent = Render(children[1], ref unsupported);
                        output.Append(Wrap(basePart)).Append('^').Append(Wrap(exponent));
                    }
                    return;
                }

                case "msub":
                case "msubsup":
                {
                    // 函数绘图没有下标语义，保留底数；msubsup 额外带上上标
                    var children = element.Elements().ToList();
                    if (children.Count >= 1) output.Append(Wrap(Render(children[0], ref unsupported)));
                    if (name == "msubsup" && children.Count >= 3)
                        output.Append('^').Append(Wrap(Render(children[2], ref unsupported)));
                    return;
                }

                case "mfrac":
                {
                    var children = element.Elements().ToList();
                    if (children.Count >= 2)
                    {
                        string numerator = Render(children[0], ref unsupported);
                        string denominator = Render(children[1], ref unsupported);
                        output.Append('(').Append(numerator).Append(")/(").Append(denominator).Append(')');
                    }
                    return;
                }

                case "msqrt":
                case "menclose":
                {
                    var children = element.Elements().ToList();
                    var inner = new StringBuilder();
                    if (children.Count == 0) inner.Append(Clean(element.Value));
                    else
                    {
                        // ref 参数不能进 lambda，这里用显式循环
                        foreach (XElement child in children)
                            inner.Append(Render(child, ref unsupported));
                    }
                    output.Append("sqrt(").Append(inner.ToString()).Append(')');
                    return;
                }

                case "mroot":
                {
                    var children = element.Elements().ToList();
                    if (children.Count >= 2)
                    {
                        string basePart = Render(children[0], ref unsupported);
                        string index = Render(children[1], ref unsupported);
                        output.Append('(').Append(basePart).Append(")^(1/(").Append(index).Append("))");
                    }
                    return;
                }

                case "mfenced":
                {
                    string open = (string)element.Attribute("open") ?? "(";
                    string close = (string)element.Attribute("close") ?? ")";
                    string separators = (string)element.Attribute("separators") ?? ",";
                    char separator = separators.Length > 0 ? separators[0] : ',';
                    var children = element.Elements().ToList();
                    
                    // 绝对值：mfenced open="|" close="|"
                    if (open == "|" && close == "|")
                    {
                        output.Append("abs(");
                        for (int i = 0; i < children.Count; i++)
                        {
                            if (i > 0) output.Append(separator);
                            output.Append(Render(children[i], ref unsupported));
                        }
                        output.Append(")");
                        return;
                    }
                    
                    output.Append(open == "{" || open == "[" ? "(" : open);
                    for (int i = 0; i < children.Count; i++)
                    {
                        if (i > 0) output.Append(separator);
                        output.Append(Render(children[i], ref unsupported));
                    }
                    output.Append(close == "}" || close == "]" ? ")" : close);
                    return;
                }

                case "mspace":
                case "maction":
                case "mphantom":
                case "annotation":
                case "annotation-xml":
                    return;   // 布局/注释，不影响表达式

                case "mtable":
                case "mtr":
                case "mtd":
                case "mlabeledtr":
                    unsupported = true;
                    return;

                default:
                    // 未知容器按 mrow 处理，尽量不丢内容
                    foreach (XElement child in element.Elements())
                        ConvertElement(child, output, ref unsupported);
                    return;
            }
        }

        private static string Render(XElement element, ref bool unsupported)
        {
            var builder = new StringBuilder();
            ConvertElement(element, builder, ref unsupported);
            return Clean(builder.ToString());
        }

        private static string Wrap(string text)
        {
            if (string.IsNullOrEmpty(text)) return "1";
            if (text.All(c => char.IsLetterOrDigit(c) || c == '.')) return text;
            return "(" + text + ")";
        }

        private static string MapIdentifier(string text)
        {
            switch (text)
            {
                case "π": return "pi";
                case "ℯ": case "𝑒": return "e";
                case "∞": return "?";
                default: return text;
            }
        }

        private static string MapOperator(string text)
        {
            switch (text)
            {
                case "−": case "–": case "—": case "‐": case "‑": return "-";
                case "×": case "·": case "⋅": case "∗": case "∙": return "*";
                case "÷": case "∕": return "/";
                case "±": case "∓": return "+";
                case "√": return "sqrt";
                case "≤": case "≥": case "≠": case "≈": return "=";
                // 绝对值竖线
                case "|": return "|";
                case "(": case ")": case "+": case "-": case "*": case "/": case "^": case "=": case ",":
                    return text;
                default:
                    return text;   // 未知符号交给 ExpressionParser 报错，用户可在文本框里改
            }
        }

        /// <summary>去掉 MathML 的不可见运算符（U+2061~U+2064）、空白与 BOM。</summary>
        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var builder = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) continue;
                if (c >= '\u2061' && c <= '\u2064') continue;
                if (c == '\uFEFF' || c == '\u00A0') continue;
                builder.Append(c);
            }
            return builder.ToString();
        }
    }
}
