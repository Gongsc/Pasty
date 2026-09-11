namespace Pasty.Models;

using Pasty.Services;

/// <summary>条目里存的是什么形态的内容。</summary>
public enum ClipType
{
    Text,
    Image,
    /// <summary>复制文件/文件夹得到的条目（视频、音频、压缩包等都归到这里，只记路径不复制文件本体）。</summary>
    File,
}

/// <summary>决定列表里画哪个图标、叫什么名字、用什么配色。</summary>
public enum ContentKind
{
    Text,
    Link,
    Image,
    Video,
    Audio,
    Document,
    Archive,
    Executable,
    Code,
    Folder,
    File,
}

/// <summary>
/// 这条内容现在能不能粘、粘过去目标应用认不认。列表靠它决定要不要高亮，
/// 粘贴链路靠它决定要不要真的写剪贴板。
/// </summary>
public enum PasteReadiness
{
    /// <summary>文字/图片，内容仍在：Ctrl+V 能粘进绝大多数应用。</summary>
    Direct,
    /// <summary>文件条目，源文件仍在：只有支持接收文件的目标（资源管理器、微信、Office）粘得上。</summary>
    Limited,
    /// <summary>内容已经没了（图片被清掉、源文件被删或移动、U 盘拔走）：写不进剪贴板。</summary>
    Unavailable,
}

/// <summary>
/// 各内容类型的图标、名称与配色。字形全部取自 Segoe MDL2 Assets——它随 Windows 10 起自带，
/// 而 Win11 的 Segoe Fluent Icons 在相同码点上兼容，因此不用任何只在 Win11 才有的字形
/// （本项目 TargetPlatformMinVersion 是 10.0.17763）。
/// 配色走 App.xaml 里的 ThemeResource 键（Kind&lt;类型&gt;Brush），亮色/深色/高对比度各一套。
/// </summary>
public static class ContentKindInfo
{
    public static string Label(this ContentKind kind) => kind switch
    {
        ContentKind.Text => Localization.Get("KindText"),
        ContentKind.Link => Localization.Get("KindLink"),
        ContentKind.Image => Localization.Get("KindImage"),
        ContentKind.Video => Localization.Get("KindVideo"),
        ContentKind.Audio => Localization.Get("KindAudio"),
        ContentKind.Document => Localization.Get("KindDocument"),
        ContentKind.Archive => Localization.Get("KindArchive"),
        ContentKind.Executable => Localization.Get("KindExecutable"),
        ContentKind.Code => Localization.Get("KindCode"),
        ContentKind.Folder => Localization.Get("KindFolder"),
        _ => Localization.Get("KindFile"),
    };

    public static string Glyph(this ContentKind kind) => kind switch
    {
        ContentKind.Text => "\uE8C1",       // Characters：A 加一个「字」，比单纯一张纸更像“纯文本”
        ContentKind.Link => "\uE71B",       // Link
        ContentKind.Image => "\uE8B9",      // Picture
        ContentKind.Video => "\uE714",      // Video
        ContentKind.Audio => "\uEC4F",      // MusicNote
        ContentKind.Document => "\uE9F9",   // ReportDocument
        ContentKind.Archive => "\uE7B8",    // Package
        ContentKind.Executable => "\uE756", // CommandPrompt
        ContentKind.Code => "\uE943",       // Code
        ContentKind.Folder => "\uF12B",     // FolderHorizontal
        _ => "\uE8A5",                      // Document：认不出类型的文件就用一张纸
    };

    /// <summary>
    /// 图标配色。亮/深各一档：亮色下取深色（要压得住白底），深色下取亮色（要在 #272727 上看得清）。
    /// 颜色只是装饰，认类型靠的是字形与文字标签，所以高对比度主题下调用方会直接退回系统前景色
    /// （见 MainWindow.KindBrush）。
    /// </summary>
    public static Windows.UI.Color Color(this ContentKind kind, bool dark)
    {
        var rgb = kind switch
        {
            ContentKind.Text => (0x0B62B8, 0x6CB6F5),
            ContentKind.Link => (0x0E7166, 0x4FD1BC),
            ContentKind.Image => (0xAD3B73, 0xF07FC0),
            ContentKind.Video => (0x6B33AE, 0xB98AF0),
            ContentKind.Audio => (0xA85B12, 0xF0A860),
            ContentKind.Document => (0x12693A, 0x5BC98C),
            ContentKind.Archive => (0x7A5C12, 0xD9BE5C),
            ContentKind.Executable => (0xA82B22, 0xFF7A6E),
            ContentKind.Code => (0x0A627E, 0x57C7E3),
            ContentKind.Folder => (0x5A6472, 0xA8B3C0),
            _ => (0x5A6472, 0xA8B3C0),
        };
        var v = dark ? rgb.Item2 : rgb.Item1;
        return Windows.UI.Color.FromArgb(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static readonly string[] ImageExt =
        { "png", "jpg", "jpeg", "gif", "bmp", "webp", "tif", "tiff", "heic", "heif", "ico", "svg", "raw" };
    private static readonly string[] VideoExt =
        { "mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "ts", "mpg", "mpeg", "3gp", "vob", "rmvb" };
    private static readonly string[] AudioExt =
        { "mp3", "wav", "flac", "m4a", "aac", "ogg", "opus", "wma", "aiff", "amr", "mid", "midi" };
    private static readonly string[] DocumentExt =
        { "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "txt", "md", "rtf", "csv", "epub", "xps" };
    private static readonly string[] ArchiveExt =
        { "zip", "rar", "7z", "tar", "gz", "bz2", "xz", "zst", "iso", "cab" };
    private static readonly string[] ExecutableExt =
        { "exe", "msi", "msix", "appx", "apk", "bat", "cmd", "ps1", "vbs", "com", "scr" };
    private static readonly string[] CodeExt =
        { "cs", "js", "mjs", "ts", "tsx", "jsx", "py", "java", "go", "rs", "c", "cc", "cpp", "h", "hpp",
          "rb", "php", "html", "css", "xml", "json", "yaml", "yml", "toml", "ini", "sql", "sh", "ipynb", "sln" };

    /// <summary>
    /// 按扩展名归类。先判可执行再判代码：.py 归代码、.exe 归程序，两者都落在扩展名上，
    /// 顺序反了会把脚本一律显示成“程序”。
    /// </summary>
    public static ContentKind FromExtension(string path)
    {
        if (Directory.Exists(path)) return ContentKind.Folder;
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext.Length == 0) return ContentKind.File;
        if (Array.IndexOf(ExecutableExt, ext) >= 0) return ContentKind.Executable;
        if (Array.IndexOf(ImageExt, ext) >= 0) return ContentKind.Image;
        if (Array.IndexOf(VideoExt, ext) >= 0) return ContentKind.Video;
        if (Array.IndexOf(AudioExt, ext) >= 0) return ContentKind.Audio;
        if (Array.IndexOf(ArchiveExt, ext) >= 0) return ContentKind.Archive;
        if (Array.IndexOf(CodeExt, ext) >= 0) return ContentKind.Code;
        if (Array.IndexOf(DocumentExt, ext) >= 0) return ContentKind.Document;
        return ContentKind.File;
    }

    /// <summary>整段文字就是一个网址时单独给个图标：复制链接是最高频的操作之一。</summary>
    public static bool IsSingleLink(string text)
    {
        var s = text.Trim();
        if (s.Length == 0 || s.Length > 2048) return false;
        if (s.IndexOfAny(new[] { '\r', '\n', ' ', '\t' }) >= 0) return false;
        return s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
               s.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>列表中的分组标题行。</summary>
public class HeaderRow
{
    public string Title { get; set; } = string.Empty;
}
