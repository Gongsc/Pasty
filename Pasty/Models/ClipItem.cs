using System.Text.Json.Serialization;

namespace Pasty.Models;

public class ClipItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ClipType Type { get; set; }
    public string Text { get; set; } = string.Empty;
    public string? ImagePath { get; set; }

    /// <summary>
    /// 文件条目的原始路径。Pasty 只记路径、不复制文件本体（一段视频可能就是几个 GB），
    /// 因此这些路径指向的是**用户自己的文件**：删除条目、过期清理、去重都绝不能碰它们。
    /// 唯一会删的文件是 images 目录里 Pasty 自己写进去的 PNG（StorageService.DeleteImage 有前缀校验）。
    /// </summary>
    public List<string> FilePaths { get; set; } = new();

    /// <summary>
    /// 图片内容的 SHA-256（十六进制）。首次需要比较内容时才计算并缓存下来，
    /// 之后随索引一起持久化——images 目录里的文件写入后不再改动，缓存不会失效。
    /// </summary>
    public string? ImageHash { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime LastUsedAt { get; set; } = DateTime.Now;
    public bool IsPinned { get; set; }
    public int UseCount { get; set; }

    [JsonIgnore]
    public string PrimaryPath => FilePaths.Count > 0 ? FilePaths[0] : string.Empty;

    /// <summary>决定图标与配色。文字里再分一次链接，文件按扩展名归类。结果缓存：路径不变类型就不变。</summary>
    [JsonIgnore]
    public ContentKind Kind => _kind ??= Type switch
    {
        ClipType.Image => ContentKind.Image,
        ClipType.File => ContentKindInfo.FromExtension(PrimaryPath),
        _ => ContentKindInfo.IsSingleLink(Text) ? ContentKind.Link : ContentKind.Text,
    };

    private ContentKind? _kind;

    [JsonIgnore] public string KindLabel => Kind.Label();
    [JsonIgnore] public string TypeGlyph => Kind.Glyph();

    [JsonIgnore]
    public string PreviewText => Type switch
    {
        ClipType.Text => Text.ReplaceLineEndings(" ").Trim(),
        ClipType.Image => $"图片 · {Path.GetFileName(ImagePath)}",
        _ => FilePaths.Count switch
        {
            0 => "（空）",
            1 => Path.GetFileName(PrimaryPath),
            _ => $"{Path.GetFileName(PrimaryPath)} 等 {FilePaths.Count} 个文件",
        },
    };

    /// <summary>条目现在还能不能粘。见 <see cref="PasteReadiness"/>。</summary>
    [JsonIgnore]
    public PasteReadiness Readiness
    {
        get
        {
            Probe();
            return _readiness;
        }
    }

    /// <summary>悬停在图标上、以及预览面板底部要显示的那句说明。</summary>
    [JsonIgnore]
    public string ReadinessHint => Readiness switch
    {
        PasteReadiness.Direct => "可直接粘贴：Ctrl+V 能粘进绝大多数应用",
        PasteReadiness.Limited => "文件条目：只有支持接收文件的应用（资源管理器、微信、Office 等）粘得上",
        _ => Type switch
        {
            ClipType.File => "已失效：源文件被删除、移动，或所在磁盘/U 盘当前不可用",
            ClipType.Image => "已失效：图片文件已不在磁盘上",
            _ => "已失效：内容为空",
        },
    };

    [JsonIgnore]
    public bool CanWriteToClipboard => Readiness != PasteReadiness.Unavailable;

    [JsonIgnore]
    public string PinGlyph => IsPinned ? "\uE77A" : "\uE718"; // 取消置顶 / 置顶

    [JsonIgnore]
    public string MetaText
    {
        get
        {
            Probe(); // 顺带把 _sizes 算出来，别为一行问两次文件系统
            var parts = new List<string> { LastUsedAt.ToString("MM-dd HH:mm") };
            if (Type == ClipType.File) parts.Add(KindLabel);
            if (_sizes.Length > 0) parts.Add(_sizes);
            if (IsPinned) parts.Add("置顶");
            if (_readiness == PasteReadiness.Unavailable) parts.Insert(0, "⚠ 已失效");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>预览面板里的全文：文件条目显示路径清单，并标出哪些已经不在了。</summary>
    [JsonIgnore]
    public string FullPreviewText
    {
        get
        {
            if (Type != ClipType.File) return Text;
            Probe();
            var head = $"{KindLabel} · {(FilePaths.Count > 1 ? $"{FilePaths.Count} 个条目 · " : string.Empty)}{_sizes}";
            var lines = new List<string>
            {
                head.TrimEnd(' ', '·'), // 量不到大小时别留一个孤零零的“ · ”
                string.Empty,
            };
            foreach (var p in FilePaths)
                lines.Add(StillThere(p) ? p : $"{p}    （已不存在）");
            return string.Join(Environment.NewLine, lines);
        }
    }

    // ---------- 文件系统探测（合并 + 缓存） ----------

    private const long ProbeCacheMs = 30_000;
    private PasteReadiness _readiness = PasteReadiness.Direct;
    private string _sizes = string.Empty;
    private long _probedAt;
    private bool _probed;

    /// <summary>
    /// 判断内容还在不在、顺便算出大小文案。
    ///
    /// 必须合并成一次并缓存：一次列表重建要为每一行问上好几遍（可见性、配色、图标提示、
    /// 大小各问一次），而 File.Exists / FileInfo.Length 在未插卡的读卡器、已断线的移动硬盘
    /// 或网络盘上可能阻塞上百毫秒。列表重建发生在 UI 线程上，低级键盘钩子也靠这个线程的
    /// 消息泵按时转——同步卡太久，钩子会被系统静默摘掉，之后“覆盖 Ctrl+V”就永久失效且没有任何报错。
    ///
    /// 代价是最长 30 秒内可能还显示成“能粘”，下一次任何改动都会重新探测。
    /// </summary>
    private void Probe()
    {
        // 文字条目不碰文件系统，每次现算：编辑完内容字数就该立刻变对，不该被缓存拖住 30 秒。
        if (Type == ClipType.Text)
        {
            _sizes = Text.Length > 1000 ? $"{Text.Length / 1000.0:F1}k 字符" : $"{Text.Length} 字符";
            _readiness = string.IsNullOrWhiteSpace(Text) ? PasteReadiness.Unavailable : PasteReadiness.Direct;
            return;
        }

        // 用 _probed 而不是只比时间：_probedAt 初值为 0 时 TickCount64 - 0 是正常值，
        // 而写成 long.MinValue 会直接溢出成负数，于是“永远不探测”——
        // 表现就是大小文案全空、失效条目永远显示成能粘。
        if (_probed && Environment.TickCount64 - _probedAt < ProbeCacheMs) return;
        _probed = true;
        _probedAt = Environment.TickCount64;

        switch (Type)
        {
            case ClipType.Image:
                if (ImagePath != null && StillThere(ImagePath))
                {
                    _sizes = SizeText(LengthOf(ImagePath));
                    _readiness = PasteReadiness.Direct;
                }
                else
                {
                    _sizes = string.Empty;
                    _readiness = PasteReadiness.Unavailable;
                }
                return;

            default:
                long total = 0;
                var all = FilePaths.Count > 0;
                var measured = false;
                foreach (var p in FilePaths)
                {
                    if (!StillThere(p)) { all = false; continue; }
                    if (Directory.Exists(p)) continue; // 文件夹没有“大小”可算
                    total += LengthOf(p);
                    measured = true;
                }
                // 量不到就不编一个“0 KB”出来：要么条目已失效（大小没意义），
                // 要么全是文件夹（本来就没有“大小”）。空字符串会在 MetaText 里被过滤掉。
                _sizes = measured ? SizeText(total) : string.Empty;
                _readiness = all ? PasteReadiness.Limited : PasteReadiness.Unavailable;
                return;
        }
    }

    /// <summary>路径还在不在。非法路径（断开的网络盘之类）按“没了”处理，绝不能把异常抛给列表。</summary>
    private static bool StillThere(string p)
    {
        try { return File.Exists(p) || Directory.Exists(p); }
        catch { return false; }
    }

    private static long LengthOf(string p)
    {
        try { return new FileInfo(p).Length; }
        catch { return 0; }
    }

    private static string SizeText(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1024.0 / 1024.0 / 1024.0:F1} GB",
        >= 1L << 20 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B", // 几十字节的小脚本显示成“0 KB”会让人以为文件是空的
    };
}
