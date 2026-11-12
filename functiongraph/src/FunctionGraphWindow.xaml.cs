using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace Ink_Canvas.Plugins.FunctionGraph
{
    /// <summary>
    /// 函数绘图输入窗口：手写区（InkAnalyzer 自动识别为文字）+ 可编辑表达式 + 范围/刻度选项。
    /// 识别失败或置信度不高时保持文本框内容不变，用户可手动输入/修改——不出现"写完没反应"。
    /// </summary>
    public partial class FunctionGraphWindow : Window
    {
        /// <summary>确定后的请求；Cancel 时为 null。</summary>
        internal FunctionGraphRequest Result { get; private set; }

        private bool _suppressRecognize;
        private InkAnalyzer _analyzer;

        public FunctionGraphWindow()
        {
            InitializeComponent();

            InkArea.StrokeCollected += InkArea_StrokeCollected;
            InkArea.Strokes.StrokesChanged += Strokes_StrokesChanged;
            Closed += (s, e) => DisposeAnalyzer();
        }

        private void InkArea_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            RecognizeInk();
        }

        private void Strokes_StrokesChanged(object sender, StrokeCollectionChangedEventArgs e)
        {
            if (InkArea.Strokes.Count == 0)
            {
                InkHint.Visibility = Visibility.Visible;
                StatusText.Text = "";
                return;
            }
            InkHint.Visibility = Visibility.Collapsed;
        }

        private void BtnClearInk_Click(object sender, RoutedEventArgs e)
        {
            InkArea.Strokes.Clear();
            ExpressionBox.Text = "";
            StatusText.Text = "";
        }

        private void BtnRecognize_Click(object sender, RoutedEventArgs e)
        {
            RecognizeInk();
        }

        /// <summary>手写 → 文字识别（StrokeType.Writing），结果写入表达式框；识别不到则提示手动输入。</summary>
        private void RecognizeInk()
        {
            try
            {
                if (InkArea.Strokes.Count == 0) return;

                DisposeAnalyzer();
                _analyzer = new InkAnalyzer();
                _analyzer.AddStrokes(InkArea.Strokes);
                _analyzer.SetStrokesType(InkArea.Strokes, StrokeType.Writing);

                var analysisStatus = _analyzer.Analyze();
                string recognized = null;
                if (analysisStatus.Successful)
                {
                    var alternates = _analyzer.GetAlternates();
                    if (alternates != null && alternates.Count > 0)
                    {
                        recognized = alternates[0].RecognizedString;
                    }
                }

                if (!string.IsNullOrWhiteSpace(recognized))
                {
                    var normalized = ExpressionParser.Normalize(recognized);
                    _suppressRecognize = true;
                    ExpressionBox.Text = normalized;
                    _suppressRecognize = false;
                    StatusText.Text = "已识别：" + recognized;
                    StatusText.Foreground = TryGetForeground();
                }
                else
                {
                    StatusText.Text = "未能识别手写内容，请直接在 f(x) 框中手动输入（如 x^2、sin(x)、1/x）。";
                    StatusText.Foreground = TryGetForeground();
                }
            }
            catch (Exception ex)
            {
                // 识别引擎缺失/异常时静默降级为手动输入，不弹窗不中断
                StatusText.Text = "手写识别不可用（" + ex.Message + "），请直接在 f(x) 框中手动输入。";
                StatusText.Foreground = TryGetForeground();
            }
        }

        private Brush TryGetForeground()
        {
            try
            {
                var brush = TryFindResource("PopupWindowForeground") as Brush;
                if (brush != null) return brush;
            }
            catch { }
            return new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        }

        private void ExpressionBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressRecognize) return;
            // 手动输入时只做轻量校验提示，不阻塞输入
            StatusText.Text = "";
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            string expression = ExpressionBox.Text;
            if (string.IsNullOrWhiteSpace(expression))
            {
                ShowError("请手写或输入表达式。");
                return;
            }

            string error;
            var compiled = ExpressionParser.Compile(expression, out error);
            if (compiled == null)
            {
                ShowError("表达式无法解析：" + error + "。支持 x、+ - * / ^、括号、sin/cos/tan/cot/sqrt/abs/log/ln/exp、pi/e，可直接修改上方文本。");
                return;
            }

            double xMax, yMax;
            if (!double.TryParse(XMaxBox.Text.Trim(), out xMax) || xMax <= 0 || xMax > 10000)
            {
                ShowError("x 轴最大范围需为正数（0 < xMax ≤ 10000）。");
                return;
            }
            if (!double.TryParse(YMaxBox.Text.Trim(), out yMax) || yMax <= 0 || yMax > 10000)
            {
                ShowError("y 轴最大范围需为正数（0 < yMax ≤ 10000）。");
                return;
            }

            Result = new FunctionGraphRequest
            {
                ExpressionText = ExpressionParser.Normalize(expression),
                XMax = xMax,
                YMax = yMax,
                ShowTicks = TicksCheck.IsChecked == true
            };

            DialogResult = true;
            Close();
        }

        private void ShowError(string message)
        {
            StatusText.Text = message;
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50));
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void DisposeAnalyzer()
        {
            try
            {
                if (_analyzer != null)
                {
                    _analyzer.Dispose();
                }
            }
            catch { }
            _analyzer = null;
        }
    }
}
