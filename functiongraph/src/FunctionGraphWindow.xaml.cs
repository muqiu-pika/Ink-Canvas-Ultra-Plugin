using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Ink_Canvas.Plugins;

namespace Ink_Canvas.Plugins.FunctionGraph
{
    /// <summary>
    /// 函数绘图输入窗口：Windows 手写公式面板（Math Input Control，与 Office「墨迹公式」
    /// 同款识别引擎）+ 可编辑表达式 + 范围/刻度选项。
    ///
    /// 手写识别只走 Windows 系统组件这一条路：面板以中文界面启动（zh-CN 资源），
    /// 识别结果 MathML 自动转为受限表达式并填入可编辑文本框；系统组件不可用时
    /// 明确提示原因与安装方法，f(x) 框始终可手动输入，不出现「写完没反应」。
    /// </summary>
    public partial class FunctionGraphWindow : Window
    {
        /// <summary>确定后的请求；Cancel 时为 null。</summary>
        internal FunctionGraphRequest Result { get; private set; }

        private bool _suppressTextEvent;
        private SystemMathRecognizer _systemSession;
        private readonly IPluginHost _host;

        /// <summary>host 可为 null（独立探针/设计器实例化）。</summary>
        public FunctionGraphWindow(IPluginHost host = null)
        {
            InitializeComponent();
            _host = host;

            // 窗口关闭/确定/取消时必须先收尾系统手写会话：
            // 控件窗口的 owner 是本窗口的 HWND，本窗口销毁后控件状态失效，
            // 之后再从 GC 终结器里 Release 容易在原生侧崩溃
            Closing += FunctionGraphWindow_Closing;

            // 系统数学识别器是按需安装的 Windows 可选功能：检测到才启用手写面板按钮，
            // 未检测到时给出明确的安装指引（f(x) 手动输入始终可用）。
            bool systemAvailable = SystemMathCapability.Status == SystemMathStatus.Available;
            BtnSystemInk.IsEnabled = systemAvailable;
            BtnSystemInk.ToolTip = systemAvailable
                ? "打开 Windows 手写公式面板，写完点『插入』自动填入"
                : SystemMathCapability.Hint;
            SystemInkHint.Text = systemAvailable ? "" : SystemMathCapability.Hint;
        }

        private void FunctionGraphWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            try
            {
                if (_systemSession != null)
                {
                    _host?.Log("函数绘图：窗口关闭，收尾手写会话", PluginLogLevel.Info);
                    _systemSession.Dispose();
                    _systemSession = null;
                }
            }
            catch (Exception ex)
            {
                _host?.Log("函数绘图：关闭手写会话失败 " + ex.Message, PluginLogLevel.Warning);
            }
        }

        /// <summary>启动 Windows 原生 Math Input Control（与 Word「墨迹公式」同一识别引擎）。</summary>
        private void BtnSystemInk_Click(object sender, RoutedEventArgs e)
        {
            if (_systemSession != null)
            {
                SetStatus("手写公式面板已打开，请在面板中书写并点『插入』。", false);
                return;
            }

            IntPtr owner = IntPtr.Zero;
            try { owner = new WindowInteropHelper(this).Handle; } catch { }

            // 回调式异步会话：绝不阻塞 UI 线程，否则 COM 事件无法派发
            string failureReason;
            _systemSession = SystemMathRecognizer.TryStart(owner, OnSystemMathResult, OnSystemMathCancelled, out failureReason);
            if (_systemSession == null)
            {
                _host?.Log("函数绘图：打开系统手写面板失败 " + failureReason, PluginLogLevel.Warning);
                // 明确告知具体失败原因；MathRecognizer 是 Windows 可选功能（非插件），
                // 未安装时可按提示安装，或直接在 f(x) 框手动输入
                SetStatus("无法打开手写公式面板：" + failureReason
                    + "。安装方法见上方提示，或直接在 f(x) 框输入。", true);
                return;
            }
            _host?.Log("函数绘图：系统手写面板已打开", PluginLogLevel.Info);
            SetStatus("请在面板中书写，点『插入』自动填入", false);
        }

        private void OnSystemMathResult(string mathML)
        {
            _systemSession = null;

            // 防御：事件回调统一在 UI 线程执行；万一宿主版本把回调送到别的线程，
            // 这里再兜一次，避免更新 WPF 控件时抛跨线程异常
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnSystemMathResult(mathML)));
                return;
            }

            string error;
            string expression = MathMLConverter.ToExpression(mathML, out error);
            if (expression == null)
            {
                _host?.Log("函数绘图：MathML 转换失败 " + error, PluginLogLevel.Warning);
                SetStatus("系统识别结果无法转为函数：" + error + "。请直接修改下方文本。", true);
                return;
            }

            string compileError;
            bool parseable = ExpressionParser.Compile(expression, out compileError) != null;

            _suppressTextEvent = true;
            ExpressionBox.Text = expression;
            _suppressTextEvent = false;

            _host?.Log("函数绘图：系统识别结果 " + expression
                + (parseable ? "" : "（需修正：" + compileError + "）"), PluginLogLevel.Info);

            SetStatus(parseable
                ? "已由 Windows 墨迹公式识别：" + expression
                : "已由 Windows 墨迹公式识别，但表达式需修正：" + compileError,
                !parseable);
        }

        private void OnSystemMathCancelled()
        {
            _systemSession = null;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnSystemMathCancelled));
                return;
            }
            SetStatus("面板已关闭", false);
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
            if (_suppressTextEvent) return;
            StatusText.Text = "";
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            string expression = ExpressionBox.Text;
            if (string.IsNullOrWhiteSpace(expression))
            {
                ShowError("请输入表达式。");
                return;
            }

            string error;
            var compiled = ExpressionParser.Compile(expression, out error);
            if (compiled == null)
            {
                ShowError("表达式无法解析：" + error + "。可直接修改上方文本。");
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
                ShowTicks = TicksCheck.IsChecked == true,
                ShowGrid = GridCheck.IsChecked == true
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
    }
}
