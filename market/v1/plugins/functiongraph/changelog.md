# 函数绘图插件更新日志

## v1.3.0 (2026-11-14) — 接入 Windows 原生「墨迹公式」
- 按微软官方文档 `programming-the-math-input-control` / `math-input-control-reference` / `receiving-input-from-the-math-input-control` 接入 **Windows Math Input Control**（与 Word「墨迹公式」同一识别引擎）：`CoCreateInstance(CLSID_MathInputControl)` → `SetOwnerWindow` / `SetPosition` / `EnableExtendedButtons` / `Show` → `_IMathInputControlEvents::Insert(BSTR mathXml)` 拿 MathML → `Hide`。窗口新增「使用 Windows 墨迹公式（Word 同款）」按钮。
- **零编译期 COM 依赖**：全部后期绑定，不引用 `micaut.dll`、不需要 Tlbimp interop DLL，也不硬编码未公开的 IID/DISPID。通过 `IConnectionPointContainer.EnumConnectionPoints` 枚举连接点、`IConnectionPoint.GetConnectionInterface` 运行时取得事件接口 IID，事件接收器实现 `IDispatch` + `ICustomQueryInterface`。
- 遵循官方 `IUnknown` / `QueryInterface` 规则：只对 `IUnknown` / `IDispatch` / 运行时取得的事件 IID 返回接口指针，其余返回 `E_NOINTERFACE`；缓存 IDispatch 指针保证 QI 身份稳定。
- 按 `MICUIELEMENT` 枚举识别出 `Paint` 事件（多个非字符串参数）并忽略，避免被误判成「取消」导致控件被关掉；`Insert` 按「1 个 BSTR 参数」识别，`Close`/`Clear` 按「0 参数」处理。
- 控件事件在 STA 线程派发，因此采用**回调式异步会话**，绝不阻塞 UI 线程；窗口关闭、5 分钟超时、用户取消都能 `Hide` + `Unadvise` + 释放，且释放延迟到 COM 调用栈之外避免重入。
- 新增 `MathMLConverter`：把回传的 MathML 转成 `ExpressionParser` 可编译的表达式，支持 `msup`（上下标）、`mfrac`（分式）、`msqrt`/`menclose`（根号）、`mroot`、`mfenced`、MathML 命名空间前缀、`&InvisibleTimes;` 等不可见运算符与数学 Unicode（`− × ÷ π`）；矩阵等不支持结构给出明确提示而非崩溃。
- **兼容性兜底保留**：`SystemMathCapability` 只读检测 `micaut.dll` + `mraut.dll`，未安装 / 组件位数不匹配 / COM 创建失败 / 用户取消时，内置 Chamfer + 语法约束解码 + 候选纠正手写区完整可用。不依赖 Office、不联网、不新增 NuGet 包，Windows 7 SP1 ~ Windows 11 均可运行。
- 回归验证：Release 编译 0 警告 0 错误；探针 10/10 笔迹用例 + 4/4 语法解码 + 6/6 MathML 转换（全部可编译）+ 宿主设置契约 11 项；COM CLSID 可解析、`TryStart` 存在、程序集引用列表无 micaut/Office interop；本机 32/64 位注册表视图均指向 `micaut.dll`。
- 已知限制：沙箱策略禁止在本环境实例化 COM 控件，因此系统引擎的实际弹出 / `Insert` 回调需在 ICU 中点击按钮实测；若 ICU 以 32 位进程运行而 `micaut.dll` 为 64 位，`Activator.CreateInstance` 会失败并自动回落到内置识别。

## v1.2.0 (2026-11-14) — 全 Windows 版本可用的识别链路
- **兼容性优先的架构决策**：Windows 的 Math Recognizer（Word「墨迹公式」同源引擎）是按需安装的 Feature on Demand（Win10 1809+ / Win11 可能被标记 Superseded、DISM 安装失败），且调用其 COM 控件需要未公开的事件连接点 IID/DISPID 与 32/64 位互操作包装。为保证大部分 Windows 版本都能运行，**不把它作为功能依赖**：新增 `SystemMathCapability` 只做只读能力探测并在窗口显示状态与安装引导，不实例化 COM、不弹窗、不抛异常。
- 新增 `ExpressionConstrainedDecoder`：以每个符号的 Top-N 候选为搜索空间做 beam search，用 `ExpressionParser.Compile` 作为「能否解析成 f(x)」的合法性检验器，联合几何分数、偏离首选的惩罚与可解析性奖励，自动纠正整式级误识别（如 `sin(x)` 的左括号被判成 `c`、`x?2` 的占位符补成 `^`）。纯托管、零系统组件、零联网，Win7 SP1 ~ Win11 / Server 行为一致；带「必须可解析且含 x / 基线合法需实质改进 / 改动比例上限」三重防过度纠正约束。
- 识别器补充常见手写变体模板（平底 2、扁 3、连笔 5、开口 6、带尾 9、带横线根号、直角方括号），候选数 5 → 6；修正相邻数字被拼成 `1*2` 的 bug（现在连成多位数）。
- 候选条新增蓝色标记表示「已按语法自动纠正」，与橙色的「不确定」区分；状态栏显示自动纠正处数；窗口新增一行识别层说明，明确当前使用内置引擎并展示系统数学识别器状态。
- 回归验证：Release 编译 0 警告 0 错误；探针 10/10 笔迹用例 + 4/4 语法解码用例 + 宿主设置契约 11 项 + 系统引擎探测全部通过。

## v1.1.0 (2026-11-14)
- 手写框跟随主程序触控设置：读取宿主 `Settings.Advanced`（特殊屏幕模式、触摸大小倍数、BoundsWidth / ThresholdValue / EraserSize）与宿主 `inkCanvas.DefaultDrawingAttributes`，笔迹粗细跟随画笔并在特殊屏幕下加粗到手指可见；用同源判定过滤手掌级宽触点；特殊屏幕下手写区高度由 150px 放大到 210px。宿主无需改版（插件以反射读取，读不到时回落默认值）。
- 重写手写识别器：由「逐笔独立归一化 + $1 逐点距离」改为「整组统一归一化 + 28×28 栅格双向 Chamfer 距离」，保留笔画间相对位置，修复 "=" 两横 / "x" 两笔 / "i" 点竖被抹平导致的误识别；分组间隙改为按字符高度自适应（去掉硬编码 18px）；补齐 a/b/e/p/q 等字母与常见手写变体，sqrt / sin / cos / tan / exp / pi / abs 等函数词可正确拼出；修正括号与 c 的模板方向，新增上标、分数线、括号配对的上下文纠错。
- 对齐 Word「墨迹公式」：识别结果按符号展示候选条，不确定符号橙色标记，点选即可更换候选（含常用符号兜底）；自动识别不再覆盖用户手动修改的表达式；新增「撤销一笔」；识别改为 260ms 防抖，连续书写不掉帧；系统 InkAnalyzer 实测无文字识别能力后只尝试一次即永久降级。
- 放置交互改为一次拖动完成（按下点为原点，拖动定范围），与「图形」面板其他图形一致；按宿主图库拖拽的模式用单一输入源 + CaptureMouse / CaptureStylus / CaptureTouch 消除触摸提升(promotion)导致的重复按下，修复指针移出画布丢失 Up、两段式误触、预览层把 Canvas 添加为自身子元素导致的拖动异常；预览改为复用的轻量虚线框；结束无条件恢复 EditingMode。
- 工具按钮补 TouchUp 并做短时抑制，修复一次触摸把手写窗口打开两次。
- 回归验证：Release 编译 0 警告 0 错误；识别探针 10/10 用例通过（x、=、2、+、sin、x^2、1/x、sin(x)、y=x^2、x+1）。

## v1.0.0 (2026-11-12)
- 首个版本。安装后在浮动栏与白板栏的「图形」面板末尾各出现一个「函数」项，卸载或禁用插件后完全消失。
- 点击后弹出输入窗口：支持手写函数自动识别（对齐 Word「墨迹公式」体验），识别结果可手动修改，也可直接键入表达式。
- 支持语法：x、加减乘除、乘方 ^、括号，以及 sin / cos / tan / cot / sqrt / abs / log / ln / exp / pi / e。
- 手写识别双引擎：优先使用系统手写识别器；系统未提供可用文本识别器时（Windows 8+ 的暂存引擎不暴露给 .NET InkAnalyzer），自动降级到内置模板识别器（尺度/旋转不变的 $1 算法，识别数字、x/y 与常用运算符、函数字母），识别结果始终可手动修改。
- 可设置 x、y 轴最大范围（渲染坐标域 [-xMax, xMax] × [-yMax, yMax]），并选择是否显示刻度。
- 两段式放置：先在画布上单击选中原点（坐标系中心，固定），再拖动确定图像范围——被控制的是图像右上角（即 (xMax, yMax) 落点），拖动期间显示半透明预览。
- 数轴以 1 个坐标单位为一格；曲线逐像素采样绘制，1/x、tan 等不连续点自动断开，不会连成竖线，超出范围的部分被裁剪。
- 插入的函数图像可被主程序选中、移动、删除，支持撤销/重做，随白板页面保存与恢复。
- 需主程序 26.11.1 及以上（该版本新增「图形」面板插件扩展点）。
