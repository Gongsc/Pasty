# Pasty — Windows 剪切板管理器

WinUI 3 (Windows App SDK 1.6) + .NET 8 开发的本地剪切板历史管理工具。

## 功能

- **剪贴板历史**：自动记录文本与图片，本地持久化（`%LOCALAPPDATA%\Pasty`），重启不丢失
- **快捷键唤出**：`Ctrl+Shift+V` 在鼠标位置弹出轻量面板；`Enter` 粘贴选中项，`Esc` 关闭，`↑↓` 选择
- **覆盖系统 Ctrl+V**（可在设置中关闭）：开启后按 `Ctrl+V` 直接粘贴历史第一条
- **粘贴第一条快捷键**：`Ctrl+Alt+V` 无需打开面板直接粘贴最近一条
- **全文预览**：选中条目在右侧显示完整内容，支持滚动、复制、粘贴、编辑
- **编辑**：文本条目可修改内容
- **置顶**：置顶条目排在最前，不受保存时长影响
- **保存时长**：永久 / 7 / 30 / 90 / 365 天，超期未置顶自动清理；可设最大条数
- **亮暗色切换**：标题栏一键切换，设置页可选 跟随系统 / 浅色 / 深色
- **托盘图标**：双击打开面板，右键菜单可打开/设置/退出；关闭窗口仅隐藏到托盘

## 构建与运行

```powershell
dotnet build Pasty/Pasty.csproj -c Debug -p:Platform=x64
.\Pasty\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\Pasty.exe
```

首次运行 Windows App SDK 运行时由包内引导，无需单独安装；全局键盘钩子可能被杀软提示，选择允许即可。

## 结构

- `Models/`：ClipItem 条目、AppSettings 设置
- `Services/`：Win32 互操作、消息窗口、剪贴板监听、热键/钩子、粘贴、存储、过期清理、自启、托盘
- `Views/`：MainWindow（历史列表 + 全文预览）、SettingsWindow（独立设置）
- `PLAN.md` / `ui-design.png` / `ui-mockup.html`：实施计划与 UI 设计图
