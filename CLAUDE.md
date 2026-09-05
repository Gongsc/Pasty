# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 项目

Pasty —— Windows 剪切板历史管理器。WinUI 3（Windows App SDK 1.6，**unpackaged**：`WindowsPackageType=None` + 自包含）+ .NET 8 + 大量 Win32 P/Invoke。数据全部本地，无网络访问。

## 构建与运行

```powershell
dotnet build Pasty/Pasty.csproj -c Debug -p:Platform=x64
.\Pasty\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Pasty.exe
```

`-p:Platform=x64` 只影响输出目录：省掉也能编过，但产物落到 `bin\Debug\` 而非 `bin\x64\Debug\`，与上面的运行路径不符。`dotnet build Pasty.sln` 亦可（sln 把 Any CPU 映射到 x64）。

**运行前先杀掉上一个实例**（没有单实例保护）。两个进程会同时抢 `RegisterHotKey`、同时装键盘钩子，表现为新实例的设置页弹出"快捷键已被占用"，而按键被旧进程处理。

**没有测试项目、没有 lint 配置。** 编译通过几乎说明不了什么——粘贴链路、键盘钩子、剪贴板读写的正确性只能实机验证：复制文本/图片 → 列表出现 → `Ctrl+Shift+V` 唤出面板 → Enter / `Ctrl+Alt+V` / `Ctrl+V` 粘贴到别的应用 → 编辑/置顶/删除 → 改保存天数后清理生效。

## 架构

### 无 DI，`App` 即服务注册表
`App.Settings` / `App.ViewModel` / `App.MessageWindow` / `App.Hotkeys` 是静态属性，`StorageService`、`Trace`、`StartupService`、`RetentionService.Clean` 是静态类。全部装配在 `App.OnLaunched` 里，顺序有依赖：`Settings.Load` → `StorageService.Load` → ViewModel → MessageWindow → 依赖它的三个服务 → 事件接线 → 显示主窗口。

### 单个隐藏消息窗口是所有 Win32 事件的入口
`Services/MessageWindow.cs` 创建一个 0×0、永不 `ShowWindow` 的 `WS_EX_TOOLWINDOW` 顶层窗口，暴露 `ProcessMessage` 事件；`ClipboardMonitor`（`WM_CLIPBOARDUPDATE`）、`HotkeyService`（`WM_HOTKEY`）、`TrayIconService`（托盘回调 + `TaskbarCreated` 广播）都挂在这一个事件上。它建在 UI 线程，消息由 XAML 消息循环泵出，因此三者的回调天然位于 UI 线程。

**不要改成 `HWND_MESSAGE` 消息专用窗口**：那样收不到 `TaskbarCreated`（Explorer 重启后托盘图标不再恢复），也永远无法成为前台窗口（托盘右键菜单点别处不消失）。WndProc 委托由静态字段持有，删掉那个字段会让窗口类持有被 GC 回收的存根，随机闪退。

### `StorageService.Items` 是唯一数据源
`MainViewModel.All` 就是它的别名；`Pinned` / `Recent` 两个 `ObservableCollection` 是派生视图，每次变更整体重建（`RebuildGroups` → `GroupsChanged` → `MainWindow.UpdateGroups` 再重建 `_rows` 行集合，含分组标题 `HeaderRow`）。

所有增删改必须走 `MainViewModel` 的方法（它们负责重建分组并调用 `StorageService.Save()`）；直接改 `StorageService.Items` 会让 UI 和磁盘都不同步。列表重建会踩掉预览面板的编辑态，`UpdateGroups` 因此专门保存/恢复未提交的文本与光标位置。

### 落盘：合并队列 + 退出必须显式 Flush
`Save()` 只登记最新快照并保证全程只有一个后台写入任务（顺序写、`tmp` + `File.Move` 原子替换）。因此**退出路径必须走 `App.Exit()`**：它按 `Hotkeys.Dispose` → 托盘移除 → `StorageService.Flush()`（同步落盘）→ `Trace.Flush()` → `Current.Exit()` 的顺序收尾。直接调 `Application.Current.Exit()` 会丢掉最后一批改动。

### 粘贴链路（最容易改坏的部分）
`PasteService.PasteAsync`：置起 `ClipboardMonitor.Suspended` + `HotkeyService.SuppressHookAction` → 写剪贴板 → 延时 → `ForceForeground(target)` → `SendCtrlV()` → finally 延时 200ms 再复位标志。

两条不能违反的约束：

- **任何写剪贴板的代码写完后要调 `ClipboardMonitor.MarkSelfWrite()`；什么都没写时绝不能调。** `Suspended` 布尔量单独不够用：`WM_CLIPBOARDUPDATE` 是投递消息，到达时同步段早已把标志复位，真正的过滤靠 `GetClipboardSequenceNumber()` 比较。空写却推进序号会连带吞掉用户的下一次复制。
- **前台窗口句柄是"取用即清除"的**：只经 `HotkeyService.ConsumeLastForegroundWindow()` 读取，并用 `Win32.IsPasteTarget()` 校验（存活、可见、不属于本进程）。缓存的句柄可能已销毁并被回收给别的进程。目标无效时 `PasteAsync` 只写剪贴板、不发按键，而不是整个跳过。

### 低级键盘钩子的硬约束（`HotkeyService.LowLevelHook`）
钩子回调在 UI 线程上执行，**同步耗时超过 `LowLevelHooksTimeout`（默认 300ms）系统会静默摘掉钩子**，此后"覆盖 Ctrl+V"永久失效且没有任何报错。所以：回调内不做文件 IO（`Trace` 因此是后台队列写入），动作一律 `_dispatcher.TryEnqueue` 出去。

抬键分支必须在 `SuppressHookAction` 门控**之外**无条件复位 `_vKeyDown`，否则物理状态卡在 true，下一次 Ctrl+V 撞上自动重复判定被吞掉（表现为每隔一次失效）。钩子只在启动时装一次，`ReRegister()` 只更新开关并在钩子缺失时报错。

### 主窗口是一个窗口的两种形态
`MainWindow._panelMode`：`ShowAsPanel()` 缩到 440×520 并定位到光标处，`ShowAsWindow()` 恢复 1060×680（少了这次 Resize，托盘打开的窗口会一直是面板宽度）。尺寸常量是**逻辑像素**，`AppWindow.Resize/Move` 收的是**物理像素**，必须经 `Scaled()` 按 `GetDpiForWindow` 换算。定位用 `FitInto` 而非 `Math.Clamp`（工作区比窗口还小时 `Clamp` 会因 min > max 抛异常）。关闭按钮被 `AppWindow.Closing` 拦下改为隐藏到托盘。

主题走 `RootGrid.RequestedTheme`（每窗口），`App.ApplyTheme()` 统一分发到主窗口与设置窗口。

### 图片
磁盘上一律 PNG（`%LOCALAPPDATA%\Pasty\images\<guid>.png`）。**编码（`ClipboardMonitor.EncodePngAsync`）与回写剪贴板（`PasteService.WriteImageClipboardAsync`）两端都用 `Bgra8` + `BitmapAlphaMode.Straight`**，改动其中一端会让半透明区域发暗、边缘发黑。回写用原始 Win32 `CF_DIB`（自下而上行序）+ 注册的 "PNG" 格式，覆盖不同应用的取图偏好。

去重：文本比字符串；图片先比文件长度，长度相同再比 SHA-256（`ClipItem.ImageHash` 缓存并随索引持久化）。**去重命中时必须删掉刚写进 images 的那个新文件**，否则每次重复复制都留一个永久孤儿。`SweepOrphanImages()` 只在索引读取成功时执行——索引读失败时 `Items` 是空的，一扫会删光用户所有图片。

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
- 热键可选组合是 `SettingsWindow.xaml.cs` 里硬编码的 `(名称, modifiers, vk)` 数组，设置里存的是裸的 modifier 位与虚拟键码；加组合改数组即可。
- `PLAN.md` 是初始实施计划，`ui-mockup.html` / `ui-design.png` 是 UI 设计稿，均为历史参考，不随代码更新。
