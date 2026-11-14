using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Ink_Canvas.Plugins;

namespace Ink_Canvas.Plugins.FunctionGraph
{
    /// <summary>
    /// 函数绘图输入窗口：手写区 + 可编辑表达式 + 候选纠正 + 范围/刻度选项。
    ///
    /// 对齐 Word「墨迹公式」的三点体验：写完自动识别、识别结果逐符号可点选更正、
    /// 结果始终落在一个可手动编辑的文本框里（识别失败也不会「写完没反应」）。
    ///
    /// 手写区同时跟随宿主的「特殊屏幕模式 / 触摸大小 / 画笔粗细」设置：
    /// 笔迹宽度取宿主 inkCanvas.DefaultDrawingAttributes（并加粗到手指可见），
    /// 用宿主同源的 BoundsWidth × Threshold × EraserSize × TouchMultiplier 过滤手掌级误触，
    /// 特殊屏幕下放大书写区高度。
    /// </summary>
    public partial class FunctionGraphWindow : Window
    {
        /// <summary>确定后的请求；Cancel 时为 null。</summary>
        internal FunctionGraphRequest Result { get; private set; }

        private readonly HostInkSettings _inkSettings;
        private readonly HashSet<int> _suppressedTouchIds = new HashSet<int>();
        private readonly DispatcherTimer _recognizeTimer;

        private bool _suppressRecognize;
        private bool _userEdited;
        private bool _systemRecognizerUnavailable;
        private InkAnalyzer _analyzer;
        private RecognitionResult _result;
        private SystemMathRecognizer _systemSession;

        /// <summary>host 可为 null（独立探针/设计器实例化），此时触控参数回落内置默认值。</summary>
        public FunctionGraphWindow(IPluginHost host = null)
        {
            InitializeComponent();

            _inkSettings = HostInkSettings.Read(host);

            _recognizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            _recognizeTimer.Tick += (s, e) =>
            {
                _recognizeTimer.Stop();
                RecognizeInk();
            };

            ApplyHostInkSettings();

            // 系统数学识别器是按需安装的 Windows 可选功能：检测到就作为额外的高精度输入通道，
            // 没检测到则自动退回内置识别，内置链路在任何 Windows 版本上都完整可用。
            bool systemAvailable = SystemMathCapability.Status == SystemMathStatus.Available;
            BtnSystemInk.IsEnabled = systemAvailable;
            BtnSystemInk.ToolTip = systemAvailable
                ? "打开 Windows 数学输入控件：手写公式后点『插入』，结果自动填入 f(x)"
                : SystemMathCapability.Hint;
            SystemInkHint.Text = systemAvailable
                ? "系统引擎与 Word 墨迹公式同源，支持分式 / 上下标 / 根号"
                : "未安装 Math Recognizer，已改用下方内置手写识别";
            EngineStatusText.Text = "兜底识别层：内置 Chamfer + 语法约束解码（全 Windows 版本可用）  ·  "
                                    + SystemMathCapability.Summary;
            EngineStatusText.ToolTip = SystemMathCapability.Hint;

            InkArea.StrokeCollected += InkArea_StrokeCollected;
            InkArea.Strokes.StrokesChanged += Strokes_StrokesChanged;
            InkArea.PreviewTouchDown += InkArea_PreviewTouchDown;
            InkArea.PreviewTouchUp += InkArea_PreviewTouchUp;
            InkArea.PreviewStylusDown += InkArea_PreviewStylusDown;
            Closed += (s, e) =>
            {
                _recognizeTimer.Stop();
                DisposeAnalyzer();
                try
                {
                    if (_systemSession != null)
                    {
                        _systemSession.Dispose();
                        _systemSession = null;
                    }
                }
                catch { }
            };
        }

        /// <summary>把宿主设置应用到手写区：笔迹属性、书写区尺寸、可见的状态说明。</summary>
        private void ApplyHostInkSettings()
        {
            double width = _inkSettings.GetInkStrokeWidth();
            var foreground = TryGetForeground() as SolidColorBrush;
            Color inkColor = foreground != null ? foreground.Color : Color.FromRgb(0x33, 0x33, 0x33);

            InkArea.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = inkColor,
                Width = width,
                Height = width,
                FitToCurve = true,
                IgnorePressure = false,
                IsHighlighter = false,
                StylusTip = StylusTip.Ellipse
            };
            // 显式固定为落笔模式：宿主画布会因手掌/橡皮逻辑临时切 None/擦除，
            // 手写框是独立窗口，不应继承那套状态机，否则会出现「写了没反应」。
            InkArea.EditingMode = InkCanvasEditingMode.Ink;

            InkAreaBorder.Height = _inkSettings.UseSpecialScreenLayout ? 210 : 150;

            TouchModeText.Text = _inkSettings.UseSpecialScreenLayout
                ? "已跟随：特殊屏幕 " + _inkSettings.TouchMultiplier.ToString("0.00") + "× · 触摸界限 "
                  + _inkSettings.BoundsWidth.ToString("0") + "px · 笔宽 " + width.ToString("0.0") + "px"
                : "笔宽跟随画布 " + width.ToString("0.0") + "px · 触摸界限 "
                  + _inkSettings.BoundsWidth.ToString("0") + "px";
        }

        /// <summary>手掌级宽触点不落笔：与宿主 BoundsWidth/Threshold/EraserSize/TouchMultiplier 同源判定。</summary>
        private void InkArea_PreviewTouchDown(object sender, TouchEventArgs e)
        {
            try
            {
                if (e.TouchDevice == null) return;
                double boundsWidth = e.GetTouchPoint(null).Bounds.Width;
                if (_inkSettings.IsPalmLikeTouch(boundsWidth))
                {
                    _suppressedTouchIds.Add(e.TouchDevice.Id);
                    e.Handled = true;
                }
            }
            catch { }
        }

        private void InkArea_PreviewTouchUp(object sender, TouchEventArgs e)
        {
            try
            {
                if (e.TouchDevice != null) _suppressedTouchIds.Remove(e.TouchDevice.Id);
            }
            catch { }
        }

        /// <summary>触摸提升出的 StylusDown 才是 InkCanvas 真正落笔的入口，在这里一并吞掉。</summary>
        private void InkArea_PreviewStylusDown(object sender, StylusDownEventArgs e)
        {
            try
            {
                if (e.StylusDevice != null && _suppressedTouchIds.Contains(e.StylusDevice.Id))
                    e.Handled = true;
            }
            catch { }
        }

        private void InkArea_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
        {
            // 防抖：连写多个符号时不在每一笔后同步跑全量识别，避免触摸书写掉帧
            _recognizeTimer.Stop();
            _recognizeTimer.Start();
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
            _recognizeTimer.Stop();
            InkArea.Strokes.Clear();
            ExpressionBox.Text = "";
            StatusText.Text = "";
            _result = null;
            _userEdited = false;
            TokenBar.Items.Clear();
        }

        private void BtnUndoStroke_Click(object sender, RoutedEventArgs e)
        {
            _recognizeTimer.Stop();
            if (InkArea.Strokes.Count == 0) return;
            InkArea.Strokes.RemoveAt(InkArea.Strokes.Count - 1);
            if (InkArea.Strokes.Count == 0)
            {
                _result = null;
                _userEdited = false;
                TokenBar.Items.Clear();
                ExpressionBox.Text = "";
                StatusText.Text = "";
                return;
            }
            RecognizeInk();
        }

        private void BtnRecognize_Click(object sender, RoutedEventArgs e)
        {
            _recognizeTimer.Stop();
            RecognizeInk();
        }

        /// <summary>启动 Windows 原生 Math Input Control（与 Word「墨迹公式」同一识别引擎）。</summary>
        private void BtnSystemInk_Click(object sender, RoutedEventArgs e)
        {
            if (_systemSession != null)
            {
                SetStatus("Windows 墨迹公式已打开，请在系统输入窗中书写并点『插入』。", false);
                return;
            }

            IntPtr owner = IntPtr.Zero;
            try { owner = new WindowInteropHelper(this).Handle; } catch { }

            // 回调式异步会话：绝不阻塞 UI 线程，否则 COM 事件无法派发
            string failureReason;
            _systemSession = SystemMathRecognizer.TryStart(owner, OnSystemMathResult, OnSystemMathCancelled, out failureReason);
            if (_systemSession == null)
            {
                // 明确告知具体失败原因；MathRecognizer 是 Windows 可选功能（非插件），
                // 未安装时可按提示安装，或继续使用下方内置手写识别（功能不残缺）
                SetStatus("无法启动 Windows 墨迹公式：" + failureReason
                    + "。可安装 Math Recognizer 可选功能（管理员运行："
                    + SystemMathCapability.InstallHint + "），或继续使用内置手写识别。", true);
                return;
            }
            SetStatus("已打开 Windows 墨迹公式：写完点『插入』；点『取消 / 清除』会关闭系统输入窗。", false);
        }

        private void OnSystemMathResult(string mathML)
        {
            _systemSession = null;
            string error;
            string expression = MathMLConverter.ToExpression(mathML, out error);
            if (expression == null)
            {
                SetStatus("系统识别结果无法转为函数：" + error + "。请直接修改下方文本，或改用内置手写区。", true);
                return;
            }

            string compileError;
            bool parseable = ExpressionParser.Compile(expression, out compileError) != null;

            _suppressRecognize = true;
            ExpressionBox.Text = expression;
            _suppressRecognize = false;
            _userEdited = false;
            _result = null;
            TokenBar.Items.Clear();

            SetStatus(parseable
                ? "已由 Windows 墨迹公式识别：" + expression
                : "已由 Windows 墨迹公式识别，但表达式需修正：" + compileError,
                !parseable);
        }

        private void OnSystemMathCancelled()
        {
            _systemSession = null;
            SetStatus("已关闭 Windows 墨迹公式；可重新打开，或直接使用下方内置手写识别。", false);
        }

        /// <summary>手写 → 表达式 + 逐符号候选；系统 InkAnalyzer 实测无文字识别能力，仅尝试一次后永久降级。</summary>
        private void RecognizeInk()
        {
            try
            {
                if (InkArea.Strokes.Count == 0)
                {
                    _result = null;
                    TokenBar.Items.Clear();
                    return;
                }

                string systemText = null;
                if (!_systemRecognizerUnavailable)
                {
                    systemText = TrySystemRecognizer();
                    if (string.IsNullOrWhiteSpace(systemText)) _systemRecognizerUnavailable = true;
                }

                string expression;
                if (!string.IsNullOrWhiteSpace(systemText))
                {
                    expression = ExpressionParser.Normalize(systemText);
                    _result = new RecognitionResult();
                    _result.Symbols.Add(new RecognizedSymbol
                    {
                        Chosen = expression,
                        Confidence = 1
                    });
                    _result.Symbols[0].Candidates.Add(new SymbolCandidate { Symbol = expression, Score = 0 });
                    SetStatus("已识别（系统手写引擎）：" + systemText, false);
                }
                else
                {
                    string detail;
                    _result = HandwritingMathRecognizer.Recognize(InkArea.Strokes, out detail);
                    expression = _result.BuildExpression();
                    int uncertain = _result.UncertainCount;
                    int corrected = _result.Symbols.Count(s => s.WasAutoCorrected);
                    string correctionNote = corrected > 0
                        ? "已按表达式语法自动纠正 " + corrected + " 处（蓝色标记）；"
                        : "";
                    SetStatus(correctionNote + (uncertain > 0
                        ? "识别到 " + _result.Symbols.Count + " 个符号，其中 " + uncertain
                          + " 个不确定（橙色标记）——点选符号更换候选，或直接修改下方文本。"
                        : "已识别 " + _result.Symbols.Count + " 个符号，可在下方修正。"), false);
                }

                RebuildTokenBar();

                // 用户已经手动改过文本框时，新的自动识别结果不覆盖他的修改（避免「改了又被冲掉」）
                bool canOverwrite = !_userEdited || string.IsNullOrWhiteSpace(ExpressionBox.Text);
                if (canOverwrite && !string.IsNullOrWhiteSpace(expression))
                {
                    _suppressRecognize = true;
                    ExpressionBox.Text = ExpressionParser.Normalize(expression);
                    _suppressRecognize = false;
                }
                else if (!canOverwrite)
                {
                    SetStatus("识别结果已更新在上方候选条；下方文本框保留了你的手动修改。", false);
                }
            }
            catch (Exception ex)
            {
                SetStatus("手写识别不可用（" + ex.Message + "），请直接在 f(x) 框中手动输入。", true);
            }
        }

        private string TrySystemRecognizer()
        {
            try
            {
                DisposeAnalyzer();
                _analyzer = new InkAnalyzer();
                _analyzer.AddStrokes(InkArea.Strokes);
                _analyzer.SetStrokesType(InkArea.Strokes, StrokeType.Writing);
                var status = _analyzer.Analyze();
                if (!status.Successful) return null;
                var alternates = _analyzer.GetAlternates();
                if (alternates == null || alternates.Count == 0) return null;
                return alternates[0].RecognizedString;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>构建 Word 墨迹公式式的符号条：每个符号一个可点选按钮，弹出候选菜单。</summary>
        private void RebuildTokenBar()
        {
            TokenBar.Items.Clear();
            if (_result == null || _result.Symbols.Count == 0) return;

            for (int i = 0; i < _result.Symbols.Count; i++)
            {
                int index = i;
                var symbol = _result.Symbols[index];
                var button = new Button
                {
                    Content = symbol.Chosen,
                    MinWidth = 38,
                    Height = 36,
                    Margin = new Thickness(2),
                    Padding = new Thickness(4, 0, 4, 0),
                    FontSize = 16,
                    ToolTip = "点击更换候选 · 置信度 " + (int)Math.Round(symbol.Confidence * 100) + "%"
                              + (symbol.WasAutoCorrected ? " · 已按语法自动纠正" : "")
                };
                if (symbol.IsUncertain)
                {
                    // 橙色：识别不确定，建议点选候选
                    button.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x8A, 0x22));
                    button.BorderThickness = new Thickness(2);
                }
                else if (symbol.WasAutoCorrected)
                {
                    // 蓝色：语法约束解码器改写过，仍可点开候选改回
                    button.BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x86, 0xD8));
                    button.BorderThickness = new Thickness(2);
                }

                var menu = new ContextMenu();
                var options = new List<string>();
                foreach (var candidate in symbol.Candidates)
                    if (!options.Contains(candidate.Symbol)) options.Add(candidate.Symbol);
                // Top-N 里没有正确符号时，仍能选到最常见的一批符号，不必整框重打
                foreach (var fallback in new[] { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
                                                "x", "+", "-", "*", "/", "^", "(", ")", ".", "sin", "cos", "sqrt" })
                {
                    if (options.Count >= 14) break;
                    if (!options.Contains(fallback)) options.Add(fallback);
                }
                foreach (var option in options)
                {
                    string value = option;
                    var item = new MenuItem { Header = value, FontSize = 14, MinHeight = 30 };
                    item.Click += (s, e) =>
                    {
                        string updated = _result.Replace(index, value);
                        button.Content = value;
                        button.ClearValue(BorderBrushProperty);
                        button.ClearValue(BorderThicknessProperty);
                        _suppressRecognize = true;
                        ExpressionBox.Text = ExpressionParser.Normalize(updated);
                        _suppressRecognize = false;
                        _userEdited = false;
                        SetStatus("已更正第 " + (index + 1) + " 个符号为 " + value + "。", false);
                    };
                    menu.Items.Add(item);
                }

                button.Click += (s, e) =>
                {
                    menu.PlacementTarget = button;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                };
                TokenBar.Items.Add(button);
            }
        }

        private void SetStatus(string message, bool isError)
        {
            StatusText.Text = message;
            StatusText.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50))
                : TryGetForeground();
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
            _userEdited = true;
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
                if (_analyzer != null) _analyzer.Dispose();
            }
            catch { }
            _analyzer = null;
        }
    }
}
