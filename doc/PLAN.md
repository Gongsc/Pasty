# Pasty — Windows 剪切板管理器（WinUI 3）实施计划

全新项目，位于 `D:\code\Pasty`，C# / .NET 8 + WinUI 3 (Windows App SDK，unpackaged，便于本地构建运行)。

## 已确认的需求决策
- 开启"覆盖系统 Ctrl+V"选项时，全局拦截 Ctrl+V 并粘贴历史第一条（可独立设置快捷键；面板内回车也可粘贴）
- 支持文本 + 图片，仅文本可编辑
- 快捷键唤出为轻量弹出面板（跟随鼠标、置顶、Esc 隐藏）
- 本地持久化、可设保存时长、置顶项不受时长影响

## 需求对照
| 需求 | 实现方式 |
|---|---|
| 1. 本地保存历史，重启不清空，可设保存时长 | JSON 索引 + 图片文件落盘 %LOCALAPPDATA%\Pasty；RetentionService 定时清理超期项 |
| 2. WinUI 3 界面 | Windows App SDK 1.5+，Mica 材质、NavigationView、ContentDialog |
| 3. 快捷键唤出 | RegisterHotKey（默认 Ctrl+Shift+V）弹出轻量面板 |
| 4. 编辑文字内容 | ContentDialog + TextBox 编辑文本项 |
| 5. 系统 Ctrl+V 默认粘贴第一条 | 低级键盘钩子拦截 Ctrl+V（可开关），替换为粘贴第一条；另有独立粘贴热键 |
| 6. 置顶不受保存时长影响 | ClipItem.IsPinned，置顶分组展示，清理时跳过 |

## 项目结构
```
Pasty.sln
Pasty/
  Pasty.csproj          (net8.0-windows10.0.19041.0, WindowsAppSDK, WindowsPackageType=None)
  App.xaml / App.xaml.cs
  Models/
    ClipItem.cs             (Id, Type, Text, ImagePath, CreatedAt, LastUsedAt, IsPinned, UseCount)
    AppSettings.cs          (保存天数 0=永久, 最大条数, 唤出热键, 粘贴第一条热键, 覆盖Ctrl+V开关, 开机自启)
  Services/
    NativeMethods.cs        (P/Invoke: 消息窗口、RegisterHotKey、WH_KEYBOARD_LL 钩子、SendInput、SetForegroundWindow、剪贴板位图读取)
    ClipboardMonitor.cs     (隐藏 message-only 窗口监听 WM_CLIPBOARDUPDATE，捕获文本/图片)
    HotkeyService.cs        (RegisterHotKey 注册唤出/粘贴热键；低级键盘钩子实现 Ctrl+V 覆盖)
    PasteService.cs         (写入剪贴板 → SendInput 发送 Ctrl+V 到前台窗口)
    StorageService.cs       (%LOCALAPPDATA%\Pasty：JSON 索引 index.json + images/ 图片文件)
    RetentionService.cs     (DispatcherTimer 定期清理超期未置顶项)
    StartupService.cs       (注册表 Run 键实现开机自启)
  ViewModels/MainViewModel.cs (ObservableCollection<ClipItem>，置顶分组排序，命令：粘贴/置顶/编辑/删除/清空)
  Views/
    MainWindow.xaml         (唯一主窗口：历史记录界面，快捷键唤出时以轻量面板形态出现在鼠标附近并置顶；搜索框、置顶/最近分组、悬停操作按钮、Enter 粘贴、Esc 隐藏、失焦自动隐藏；右侧全文预览面板：点击条目显示完整内容，支持滚动查看/复制/粘贴/编辑入口；标题栏 ⚙ 按钮打开设置窗口)
    SettingsWindow.xaml     (独立设置窗口：保存时长、最大条数、快捷键、覆盖 Ctrl+V 开关、开机自启、主题：浅色/深色/跟随系统)
    EditDialog.xaml         (文本编辑对话框 ContentDialog)
```

## 关键实现点
1. **剪贴板监听**：隐藏窗口 `AddClipboardFormatListener`；文本直接存，图片存为 PNG 到 images/ 目录；相同内容去重（刷新时间并上移）。
2. **覆盖系统 Ctrl+V**：`SetWindowsHookEx(WH_KEYBOARD_LL)` 低级钩子；开启时拦截 Ctrl+V（吞掉按键），执行"粘贴第一条"；关闭时钩子直通。粘贴流程：第一条写回剪贴板 → `SendInput` 发送 Ctrl+V（临时挂起自身监听避免重复记录）。
3. **热键唤出**：`RegisterHotKey`（默认 Ctrl+Shift+V）唤出主窗口（历史记录界面），窗口置于鼠标附近并 `SetForegroundWindow`，Esc/失焦自动隐藏（可设为常驻）；Enter 粘贴选中项（默认第一条）。设置位于独立窗口，从主窗口标题栏 ⚙ 打开。
4. **编辑**：列表项右键/按钮 → ContentDialog 内 TextBox 修改文本，保存后更新存储。
5. **置顶**：`IsPinned`，UI 分"置顶/最近"两节，置顶项跳过过期清理。
6. **保存时长**：设置页提供天数选择（永久/7/30/90 天等），定时清理 `CreatedAt` 超期且未置顶的条目（含图片文件）。
7. **重启持久化**：JSON 索引 + 图片文件落盘，启动时加载。
8. **亮暗色切换**：基于 `FrameworkElement.RequestedTheme`（`Application.RequestedTheme` 全局生效）；设置页可选 浅色/深色/跟随系统，主窗口标题栏提供 ☀/🌙 按钮一键切换（切换结果写回设置持久化）。

## 验证
`dotnet build` 通过；运行后手动验证：复制文本/图片 → 面板显示 → 热键唤出 → Enter/全局热键粘贴 → 编辑/置顶/删除 → 修改保存天数后清理生效。

## 说明
全局钩子、SendInput 粘贴、开机自启在运行时可能需要用户授权/杀软放行，构建阶段无需额外权限。
