using System.Globalization;
using Microsoft.Windows.Globalization;

namespace Pasty.Services;

/// <summary>
/// 应用语言只允许简体中文和英文。XAML 文案由 RESW/x:Uid 提供，代码动态拼接的文案从这里取；
/// PrimaryLanguageOverride 必须在创建任何窗口前设置，已创建的 WinUI 资源上下文不会完整热切换。
/// </summary>
public static class Localization
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    public static string CurrentLanguage { get; private set; } = Chinese;
    public static bool IsEnglish => CurrentLanguage == English;

    public static void Initialize(string? language)
    {
        CurrentLanguage = Normalize(language);
        try
        {
            // 必须使用 Windows App SDK 的 Microsoft.Windows.Globalization；系统同名 API
            // 在 unpackaged 进程里会抛 InvalidOperationException，并让应用卡在窗口/托盘创建之前。
            ApplicationLanguages.PrimaryLanguageOverride = CurrentLanguage;
        }
        catch (Exception ex)
        {
            // 语言资源失败不能阻断剪贴板管理器启动；动态文案仍按 CurrentLanguage 显示，
            // XAML 文案则安全退回系统语言/默认中文。
            Trace.Log($"设置界面语言失败，退回默认资源: {ex.Message}");
        }
        var culture = CultureInfo.GetCultureInfo(CurrentLanguage);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    public static string Normalize(string? language)
    {
        if (language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true) return English;
        if (language?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true) return Chinese;
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? Chinese : English;
    }

    public static string Get(string key) => (IsEnglish, key) switch
    {
        (true, "SettingsTitle") => "Pasty — Settings",
        (true, "SettingsAbout") => "Pasty v{0} · All data stays on this device; only update checks access the network · GNU GPL v3",
        (true, "RestartLanguage") => "The language change will take effect after restarting Pasty.",
        (true, "ClearDetailPinned") => "This will permanently delete all {0} records, including {1} pinned records.",
        (true, "ClearDetail") => "This will permanently delete all {0} records.",
        (true, "ClearTitle") => "Clear all history?",
        (true, "Clear") => "Clear",
        (true, "Cancel") => "Cancel",
        (true, "AboutTitle") => "About Pasty",
        (true, "Version") => "Version {0}",
        (true, "Checking") => "Checking…",
        (true, "UpdateFound") => "Version {0} is available",
        (true, "UpToDate") => "You're up to date ({0})",
        (true, "UpdateFailed") => "Check failed. Please try again later.",
        (true, "PinnedHeader") => "☆ Pinned",
        (true, "RecentHeader") => "Recent",
        (true, "YesterdayHeader") => "Yesterday",
        (true, "EmptyHistory") => "No clipboard history yet. Copy text, an image, or files to get started.",
        (true, "NoMatches") => "No results for “{0}”. Search checks text content and file names.",
        (true, "PreviewTitle") => "Full preview",
        (true, "EditTitle") => "Edit content",
        (true, "EmptyPreview") => "Select an item on the left to view its full content",
        (true, "Unknown") => "Unknown",
        (true, "Image") => "Image · {0}",
        (true, "Empty") => "(empty)",
        (true, "Files") => "{0} and {1} files total",
        (true, "DirectHint") => "Ready to paste: Ctrl+V works in most apps",
        (true, "LimitedHint") => "File item: paste works only in apps that accept files (File Explorer, Office, etc.)",
        (true, "MissingFileHint") => "Unavailable: the source was deleted, moved, or its drive is disconnected",
        (true, "MissingImageHint") => "Unavailable: the image file is no longer on disk",
        (true, "MissingContentHint") => "Unavailable: the content is empty",
        (true, "Pinned") => "Pinned",
        (true, "Unavailable") => "⚠ Unavailable",
        (true, "Items") => "{0} items · ",
        (true, "MissingPath") => "{0}    (not found)",
        (true, "Characters") => "{0} characters",
        (true, "CharactersK") => "{0:F1}k characters",
        (true, "KindText") => "Text",
        (true, "KindLink") => "Link",
        (true, "KindImage") => "Image",
        (true, "KindVideo") => "Video",
        (true, "KindAudio") => "Audio",
        (true, "KindDocument") => "Document",
        (true, "KindArchive") => "Archive",
        (true, "KindExecutable") => "Application",
        (true, "KindCode") => "Code",
        (true, "KindFolder") => "Folder",
        (true, "KindFile") => "File",
        (true, "ShowPanelHotkey") => "Open panel",
        (true, "PasteLatestHotkey") => "Paste latest",
        (true, "HookFailed") => "Keyboard hook installation failed (error {0}). “Override system Ctrl+V” will not work; restart Pasty.",
        (true, "HotkeyFailed") => "{0} hotkey {1} could not be registered (error {2}). It is usually used by another app; choose a different shortcut.",

        (_, "SettingsTitle") => "Pasty — 设置",
        (_, "SettingsAbout") => "Pasty v{0} · 数据全部存在本机，仅检查更新时联网 · GNU GPL v3",
        (_, "RestartLanguage") => "语言将在重新启动 Pasty 后生效。",
        (_, "ClearDetailPinned") => "将删除全部 {0} 条记录，其中 {1} 条已置顶，且无法恢复。",
        (_, "ClearDetail") => "将删除全部 {0} 条记录，且无法恢复。",
        (_, "ClearTitle") => "清空全部历史？",
        (_, "Clear") => "清空",
        (_, "Cancel") => "取消",
        (_, "AboutTitle") => "关于 Pasty",
        (_, "Version") => "版本 {0}",
        (_, "Checking") => "正在检查…",
        (_, "UpdateFound") => "发现新版本 {0}",
        (_, "UpToDate") => "当前已是最新版本（{0}）",
        (_, "UpdateFailed") => "检查失败，请稍后重试",
        (_, "PinnedHeader") => "☆ 已置顶",
        (_, "RecentHeader") => "最近",
        (_, "YesterdayHeader") => "昨天",
        (_, "EmptyHistory") => "还没有剪贴板历史。复制文字、图片或文件，这里就会出现。",
        (_, "NoMatches") => "没有匹配「{0}」的记录。搜索会查文字内容与文件名。",
        (_, "PreviewTitle") => "全文预览",
        (_, "EditTitle") => "编辑内容",
        (_, "EmptyPreview") => "选择左侧条目查看完整内容",
        (_, "Unknown") => "未知",
        (_, "Image") => "图片 · {0}",
        (_, "Empty") => "（空）",
        (_, "Files") => "{0} 等 {1} 个文件",
        (_, "DirectHint") => "可直接粘贴：Ctrl+V 能粘进绝大多数应用",
        (_, "LimitedHint") => "文件条目：只有支持接收文件的应用（资源管理器、微信、Office 等）粘得上",
        (_, "MissingFileHint") => "已失效：源文件被删除、移动，或所在磁盘/U 盘当前不可用",
        (_, "MissingImageHint") => "已失效：图片文件已不在磁盘上",
        (_, "MissingContentHint") => "已失效：内容为空",
        (_, "Pinned") => "置顶",
        (_, "Unavailable") => "⚠ 已失效",
        (_, "Items") => "{0} 个条目 · ",
        (_, "MissingPath") => "{0}    （已不存在）",
        (_, "Characters") => "{0} 字符",
        (_, "CharactersK") => "{0:F1}k 字符",
        (_, "KindText") => "文字",
        (_, "KindLink") => "链接",
        (_, "KindImage") => "图片",
        (_, "KindVideo") => "视频",
        (_, "KindAudio") => "音频",
        (_, "KindDocument") => "文档",
        (_, "KindArchive") => "压缩包",
        (_, "KindExecutable") => "程序",
        (_, "KindCode") => "代码",
        (_, "KindFolder") => "文件夹",
        (_, "KindFile") => "文件",
        (_, "ShowPanelHotkey") => "唤出面板",
        (_, "PasteLatestHotkey") => "粘贴第一条",
        (_, "HookFailed") => "键盘钩子安装失败（错误码 {0}），“覆盖系统 Ctrl+V”不会生效，请重启 Pasty",
        (_, "HotkeyFailed") => "{0}快捷键 {1} 注册失败（错误码 {2}），通常是已被其他程序占用，换一个组合即可",
        _ => key,
    };

    public static string Format(string key, params object[] args)
        => string.Format(CultureInfo.CurrentUICulture, Get(key), args);
}
