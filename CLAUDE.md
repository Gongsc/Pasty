# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目

Pasty —— Windows 剪切板历史管理器。WinUI 3（Windows App SDK 1.6，**unpackaged**：`WindowsPackageType=None` + 自包含）+ .NET 8 + 大量 Win32 P/Invoke。数据全部本地；仅在用户主动点击“检查更新”时访问 GitHub。

## 构建与运行

```powershell
dotnet build Pasty/Pasty.csproj -c Debug -p:Platform=x64
.\Pasty\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Pasty.exe
```

`-p:Platform=x64` 只影响输出目录：省掉也能编过，但产物落到 `bin\Debug\` 而非 `bin\x64\Debug\`，与上面的运行路径不符。`dotnet build Pasty.sln` 亦可（sln 把 Any CPU 映射到 x64）。

**CI**：`.github/workflows/build.yml` 在 `windows-latest` 上跑 Release x64 构建，用 Inno Setup 把整个输出目录（自包含，含 Windows App SDK 运行时）制成每用户安装的 `Setup.exe` artifact；推 `v*` 标签时额外发布到 GitHub Release，并先校验标签与 csproj 的 `<Version>` 一致。安装包只保留简体中文与英文的 MUI 目录。

**只有一个实例能活着**：`SingleInstanceService.TryBecomeFirstInstance()` 在 `OnLaunched` 最前面抢一个会话内 Mutex，抢不到的那个只负责投递唤醒消息（把已运行实例的主窗口带到前台）然后立刻退出。以前没有这层保护时，两个进程会同时抢 `RegisterHotKey`、同时装键盘钩子，表现为新实例的设置页弹出"快捷键已被占用"，而按键被旧进程处理。

**没有测试项目、没有 lint 配置。** 编译通过几乎说明不了什么——粘贴链路、键盘钩子、剪贴板读写的正确性只能实机验证：复制文本/图片/文件 → 列表出现（图标按类型区分、能直接粘的行有主色竖条）→ `Ctrl+Shift+V` 唤出面板 → Enter / `Ctrl+Alt+V` / `Ctrl+V` 粘贴到别的应用 → 编辑/置顶/删除 → 改保存天数后清理生效。

验证界面时注意：**屏幕抓不到 WinUI 3 的窗口内容**（`CopyFromScreen` 与 `PrintWindow(PW_RENDERFULLCONTENT)` 截出来都是黑块，其他应用正常）。可行的办法是用 UI Automation 读元素树（PowerShell 的 `System.Windows.Automation`）：列表项里只有可见元素会进树，所以行内 Text 的 Name 能直接验到类型标签、meta 文案与“图标只剩一个”这类可见性结论；再配合 `InvokePattern` 点“复制”、用 `DragQueryFileW` 读回 `CF_HDROP`，就能不靠眼睛跑完整条链路。

## 架构

### 无 DI，`App` 即服务注册表
`App.Settings` / `App.ViewModel` / `App.MessageWindow` / `App.Hotkeys` 是静态属性，`StorageService`、`Trace`、`StartupService`、`RetentionService.Clean` 是静态类。全部装配在 `App.OnLaunched` 里，顺序有依赖：**单实例守卫**（必须排第一，见下）→ `Settings.Load` → `StorageService.Load` → ViewModel → MessageWindow → 依赖它的四个服务 → 事件接线 → 显示主窗口。

单实例守卫为什么必须排第一：第二个实例如果先走了后面的装配流程，就会读索引、装钩子、往同一个 `index.json` 写，退出时还可能把一份空快照落盘、盖掉真正在跑的那个实例的数据。

### 单个隐藏消息窗口是所有 Win32 事件的入口
`Services/MessageWindow.cs` 创建一个 0×0、永不 `ShowWindow` 的 `WS_EX_TOOLWINDOW` 顶层窗口，暴露 `ProcessMessage` 事件；`ClipboardMonitor`（`WM_CLIPBOARDUPDATE`）、`HotkeyService`（`WM_HOTKEY`）、`TrayIconService`（托盘回调 + `TaskbarCreated` 广播）、`SingleInstanceService`（第二个实例投递的唤醒消息）都挂在这一个事件上。它建在 UI 线程，消息由 XAML 消息循环泵出，因此四者的回调天然位于 UI 线程。它的类名 `Pasty_MsgWindow` 对外可见，第二个实例靠 `FindWindowW` 按类名找到它。

**不要改成 `HWND_MESSAGE` 消息专用窗口**：那样收不到 `TaskbarCreated`（Explorer 重启后托盘图标不再恢复），也永远无法成为前台窗口（托盘右键菜单点别处不消失）。WndProc 委托由静态字段持有，删掉那个字段会让窗口类持有被 GC 回收的存根，随机闪退。

### `StorageService.Items` 是唯一数据源
`MainViewModel.All` 就是它的别名；`Pinned` / `Recent` 两个 `ObservableCollection` 是派生视图，每次变更整体重建（`RebuildGroups` → `GroupsChanged` → `MainWindow.UpdateGroups` 再重建 `_rows` 行集合，含分组标题 `HeaderRow`）。

所有增删改必须走 `MainViewModel` 的方法（它们负责重建分组并调用 `StorageService.Save()`）；直接改 `StorageService.Items` 会让 UI 和磁盘都不同步。列表重建会踩掉预览面板的编辑态，`UpdateGroups` 因此专门保存/恢复未提交的文本与光标位置。

### 落盘：合并队列 + 退出必须显式 Flush
`Save()` 只登记最新快照并保证全程只有一个后台写入任务（顺序写、`tmp` + `File.Move` 原子替换）。因此**退出路径必须走 `App.Exit()`**：它按 `Hotkeys.Dispose` → `ForegroundService.Stop` → 托盘移除 → `StorageService.Flush()`（同步落盘）→ `Trace.Flush()` → `Current.Exit()` 的顺序收尾。直接调 `Application.Current.Exit()` 会丢掉最后一批改动。

### 粘贴链路（最容易改坏的部分）
`PasteService.PasteAsync`：经 `SemaphoreSlim` 串行排队 → 置起 `ClipboardMonitor.Suspended` → 写剪贴板 → 仅在目标不处于前台时 `ForceForeground(target)` 并短暂等待 → `SendCtrlV()` → finally 复位标志。键盘触发且目标仍在前台时没有固定延时。

目标窗口一律经 `ForegroundService.ResolvePasteTarget()` 取，它按两级兜底：先取热键触发时记下的句柄，再取 `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 持续跟踪到的"最近一个外部前台窗口"。

三条不能违反的约束：

- **任何写剪贴板的代码写完后要调 `ClipboardMonitor.MarkSelfWrite()`；什么都没写时绝不能调。** `Suspended` 布尔量单独不够用：`WM_CLIPBOARDUPDATE` 是投递消息，到达时同步段早已把标志复位，真正的过滤靠 `GetClipboardSequenceNumber()` 比较。空写却推进序号会连带吞掉用户的下一次复制。
- **粘贴目标绝不能只靠热键那一刻的缓存。** 热键缓存是"取用即清除"的（`HotkeyService.ConsumeLastForegroundWindow()`），只对紧随触发的那一次粘贴有效；留着不清会让几小时前按热键时记下的窗口继续被当成目标，而那个句柄可能已销毁并被回收给别的进程。**鼠标操作（双击条目、点"粘贴"按钮）没有那样的触发时刻可记**，早先因此退回 `GetForegroundWindow()` 拿到 Pasty 自己，`IsPasteTarget` 不通过就只写剪贴板不发按键，用户看到的就是"条目跳到最顶端、目标应用里什么都没出现"。这就是 `ForegroundService` 存在的理由，别把它删了退回单级缓存。
- **任何候选句柄用之前都要过 `Win32.IsPasteTarget()`**（存活、可见、不属于本进程）。目标无效时 `PasteAsync` 只写剪贴板、不发按键，而不是整个跳过。`ForegroundService` 的回调跑在 UI 线程的消息泵上，和键盘钩子一样不许做耗时事，只记句柄；它刻意忽略自己的窗口（否则面板一弹出就把目标覆盖成面板自己），并跳过任务栏/桌面这类“不是任何应用”的窗口。
- **内容已经不在了的条目（`Readiness == Unavailable`）一个字节都不写**。`PasteAsync` 开头就退出，`WriteToClipboardAsync` 对文件条目要求每个路径都还在才写：把不存在的路径塞进剪贴板，目标应用只会报“找不到文件”，而 `EmptyClipboard` 已经先把用户原本的内容清掉了。宁可什么都不做。

### 低级键盘钩子的硬约束（`HotkeyService.LowLevelHook`）
钩子回调在 UI 线程上执行，**同步耗时超过 `LowLevelHooksTimeout`（默认 300ms）系统会静默摘掉钩子**，此后"覆盖 Ctrl+V"永久失效且没有任何报错。所以：回调内不做文件 IO（`Trace` 因此是后台队列写入），动作一律 `_dispatcher.TryEnqueue` 出去。

被吞掉的 V 抬键必须无条件复位 `_vKeyDown` 并一并吞掉，否则物理状态会卡住或把半截按键交给目标应用。`SendCtrlV()` 注入的每个按键都带 `PastyInjectedInput` 标记，钩子必须优先放行；用户仍按着物理 Ctrl 时只能注入 V，不能注入 Ctrl 抬起，否则下一次 V 会退化成裸字母。钩子只在启动时装一次，`ReRegister()` 只更新开关并在钩子缺失时报错。

### 主窗口是一个窗口的两种形态
`MainWindow._panelMode`：`ShowAsPanel()` 缩到 440×520 并定位到光标处，`ShowAsWindow()` 恢复 1060×680（少了这次 Resize，托盘打开的窗口会一直是面板宽度）。尺寸常量是**逻辑像素**，`AppWindow.Resize/Move` 收的是**物理像素**，必须经 `Scaled()` 按 `GetDpiForWindow` 换算。定位用 `FitInto` 而非 `Math.Clamp`（工作区比窗口还小时 `Clamp` 会因 min > max 抛异常）。关闭按钮被 `AppWindow.Closing` 拦下改为隐藏到托盘。

应用首次启动只驻留托盘：`App.OnLaunched` 会创建 `MainWindow` 供托盘、快捷键和单实例唤醒复用，但不能调用 `Activate()`。用户从托盘打开、按唤出快捷键或再次运行程序时才显示窗口。

主题走 `RootGrid.RequestedTheme`（每窗口），`App.ApplyTheme()` 统一分发到主窗口与设置窗口。

### 列表行的类型图标与“能不能粘”
每条 `ClipItem` 有一个 `Readiness`（`Direct` / `Limited` / `Unavailable`），列表与粘贴链路都读它：

- **`Direct`**（文字 / 链接 / 图片，且内容还在）——行左缘有主色竖条，粘进大多数应用都认。
- **`Limited`**（文件条目，源文件还在）——没有竖条：只有支持接收文件的目标（资源管理器、微信、Office）粘得上。
- **`Unavailable`**（图片被清掉、源文件删了或 U 盘拔了）——图标换成警告三角、整行置灰、meta 开头是 `⚠ 已失效`，预览区的“复制 / 粘贴”按钮直接禁用。

判定内容在不在**必须带 30 秒缓存**（`ClipItem.Probe`）：一次列表重建要为每行问上好几遍文件系统，
而 `File.Exists` / `FileInfo.Length` 在未插卡的读卡器、断线的移动硬盘上能阻塞上百毫秒——
这些都发生在 UI 线程上，而键盘钩子靠这个线程的消息泵，卡久了钩子会被静默摘掉。
文字条目不碰文件系统，所以走缓存之外每次现算（否则编辑完字数还要 30 秒才更新）。

图标字形与配色表在 `Models/ContentKind.cs`：字形全部取自 **Segoe MDL2 Assets**（Win10 起自带，
Win11 的 Segoe Fluent Icons 同码点兼容），不要换成只在 Win11 才有的字形。
底色走 XAML 的 `ThemeResource`，只有按类型取的那支画笔走代码（`MainWindow.KindBrush`），
并且必须看**本窗口**的 `RootGrid.ActualTheme`——用 `Application.Current.Resources` 解析的是“应用”主题，
手动切亮/深色时颜色不跟着变（以前就踩过）。模板是 OneTime 绑定，所以 `ActualThemeChanged` 里要 `UpdateGroups()` 重建一次。

### 图片与文件
磁盘上一律 PNG（`%LOCALAPPDATA%\Pasty\images\<guid>.png`）。**编码（`ClipboardMonitor.EncodePngAsync`）与回写剪贴板（`PasteService.WriteImageClipboardAsync`）两端都用 `Bgra8` + `BitmapAlphaMode.Straight`**，改动其中一端会让半透明区域发暗、边缘发黑。回写用原始 Win32 `CF_DIB`（自下而上行序）+ 注册的 "PNG" 格式，覆盖不同应用的取图偏好。

文件条目（复制视频、压缩包、文件夹得到的）**只记路径，绝不把文件复制进数据目录**（一段视频可能就是几个 GB），
因此有两个推论：**删除条目 / 过期清理 / 去重都不能删 `FilePaths` 指向的东西**（只有 `images` 目录里 Pasty 自己写的 PNG 能删，
`StorageService.DeleteImage` 有目录前缀校验兼作这道门）；回写用 `CF_HDROP`，并且**必须同时写 `Preferred DropEffect = DROPEFFECT_COPY`**，
不写时资源管理器粘过去可能按“移动”理解，把用户的原文件搬走。

读剪贴板时的优先级是 **文字 → 位图 → 文件**：浏览器“复制网页图片”会同时留下位图与临时文件路径，
按图片记才不会存一个随时会被清掉的临时路径。文件走 `Win32.ReadClipboardFileDrop()`（`CF_HDROP` + `DragQueryFileW`）
而不是 `DataPackage.GetStorageItemsAsync()`：后者要为每个路径构造 shell item，慢且不稳定的时候会堵死 UI 线程的消息泵。

去重：文本比字符串；图片先比文件长度，长度相同再比 SHA-256（`ClipItem.ImageHash` 缓存并随索引持久化）；
文件只比路径集合（忽略大小写、顺序无关），**绝不为了去重去哈希一个几个 GB 的视频**。
**去重命中时必须删掉刚写进 images 的那个新文件**，否则每次重复复制都留一个永久孤儿。`SweepOrphanImages()` 只在索引读取成功时执行——索引读失败时 `Items` 是空的，一扫会删光用户所有图片。

## 数据目录与调试开关

`%LOCALAPPDATA%\Pasty\`：`index.json`（历史索引）、`images\`、`settings.json`、`error.log`、`trace.log`。

`App` 的 `UnhandledException` 处理器把异常写进 `error.log` 后 `e.Handled = true` 吞掉，所以故障的表现往往是"按了没反应"。排查顺序：先看 `error.log`，再建 flag 文件开日志。

两个 flag 文件（空文件即可，重启生效）：
- `trace` —— 开启 `trace.log` 追踪。**日志绝不记录剪贴板内容本身**，只记类型与长度；这是剪贴板管理器，用户复制的就是密码和令牌。加日志时守住这条。
- `hooktest` —— 让键盘钩子把注入的按键也当物理键处理，仅用于诊断。

## 图标

`Pasty/Assets/` 下三个 `.ico` **是生成物，不要手改**。唯一的形状来源是 `tools/IconGen/Art.cs` 里的 64×64 单位网格坐标：

```powershell
dotnet run --project tools/IconGen -- Pasty/Assets
```

要点：

- **三档细节分别绘制，不是同一张图缩放。** L1（256/128/64/48）有纸叠、铆钉、板身厚度；L2（40/32/24）只留一张白纸；L3（20/16）退成纯剪影。拟物细节在 16px 下只会变成脏点，所以小尺寸必须另画一版，但三档共用同一个剪影（板 + 顶部夹子），换档时形状不跳。
- **不使用任何渐变**，立体感全靠平涂色阶：硬边明暗带、逐层加深的纸色、偏移的实色投影。
- L3 与托盘图标的坐标全部取 4 的倍数——64 网格缩到 16px 是 ÷4、32px 是 ÷2，边缘才落在整像素上。改这两档的几何时保持这个约束，否则 16px 会糊成两排半透明灰。
- **托盘图标的 V 是挖空的**（`Geometry.Combine(..., Exclude)`），任务栏底色从 V 里透出来；填白在深色任务栏上是一道刺眼亮条。Windows 不会替第三方托盘图标反色，所以浅/深两套都得有，`TrayIconService` 按注册表 `SystemUsesLightTheme` 挑，收到 `WM_SETTINGCHANGE`/`ImmersiveColorSet` 且明暗真的翻转时 `NIM_MODIFY` 换图。
- 托盘句柄是 `LoadImageW` 自己加载的，**必须 DestroyIcon**（换图时连旧句柄一起，否则每切一次主题漏一个）；只有退化到 `LoadIconW(IDI_APPLICATION)` 那条路上的句柄是系统共享的，不能销毁——区别记在 `s_iconOwned` 上。
- 标题栏左上角那个 18px 标记（`MainWindow.xaml`）是同一套 L3 几何的 `Viewbox`/`Canvas` 手抄版，改了图标记得同步。
- `icon-preview.html` 是设计稿兼对照表，几何与 `Art.cs` 一一对应，改一边要改另一边。

## 约定

- 界面文案、代码注释、提交信息全部中文。注释解释**为什么**（多数是某个已修 bug 的成因），改动相关代码时要么保持注释成立，要么一并更新——不要留下描述已不存在行为的注释。
- **版本号只写 `Pasty.csproj` 的 `<Version>` 一处**，设置页底部读 `App.Version`（从程序集元数据来），CI 的产物名与 Release 标题从 csproj 读。发版同时打一个同名的 `v<版本号>` 标签，CI 会校验两者一致。
- 界面语言只支持 `zh-CN` 与 `en-US`。XAML 静态文案放在 `Strings/<语言>/Resources.resw` 并通过 `x:Uid` 读取；代码动态文案统一走 `Services/Localization.cs`。语言设置写入 `settings.json`，下次启动时在任何窗口创建前设置 `PrimaryLanguageOverride`。
- 热键可选组合是 `SettingsWindow.xaml.cs` 里硬编码的 `(名称, modifiers, vk)` 数组，设置里存的是裸的 modifier 位与虚拟键码；加组合改数组即可。
- `PLAN.md` 是初始实施计划，`ui-mockup.html` / `ui-design.png` 是 UI 设计稿，均为历史参考，不随代码更新。
