using System.Collections.ObjectModel;
using Pasty.Models;
using Pasty.Services;

namespace Pasty;

/// <summary>维护全部条目（直接以 StorageService.Items 为唯一数据源），并派生“置顶/最近”两个分组集合。</summary>
public class MainViewModel
{
    public ObservableCollection<ClipItem> Pinned { get; } = new();
    public ObservableCollection<ClipItem> Recent { get; } = new();

    private string _filter = string.Empty;

    public event Action? GroupsChanged;

    private List<ClipItem> All => StorageService.Items;

    /// <summary>
    /// Ctrl+V 覆盖模式要粘贴的“第一条”：按最近复制/使用时间取，而非置顶优先。
    /// 跳过内容已经不在了的条目（刚复制过一个文件、随后把那个文件删了）：
    /// 否则按 Ctrl+V 会撞上一条粘不出去的记录，在用户看来就是“按了没反应”。
    /// </summary>
    public ClipItem? TopItem =>
        StorageService.Items.FirstOrDefault(i => i.CanWriteToClipboard);

    /// <summary>历史总条数，不受搜索过滤影响。空状态文案与“清空”按钮的可用性都要看它。</summary>
    public int TotalCount => All.Count;

    /// <summary>已置顶条数。清空确认框要如实说明会连置顶项一起删掉多少条。</summary>
    public int PinnedCount => All.Count(i => i.IsPinned);

    /// <summary>当前搜索词。空列表要区分“一条历史都没有”和“搜索没命中”，文案不一样。</summary>
    public string Filter => _filter;

    public Task LoadFromStorageAsync()
    {
        RebuildGroups();
        return Task.CompletedTask;
    }

    public async Task AddOrUpdateAsync(ClipItem item)
    {
        var existing = All.FirstOrDefault(i => IsSameContent(i, item));

        if (existing != null)
        {
            // 去重命中：ClipboardMonitor 已经把这次的 PNG 写进了 images 目录，
            // 而它马上就不再被任何条目引用。不在这里删掉就是一个永久孤儿文件——
            // 连续复制同一张图会一次一份地堆在磁盘上。
            if (item.Type == ClipType.Image && !ReferenceEquals(existing, item))
                StorageService.DeleteImage(item.ImagePath);

            // 相同内容：上移并刷新时间
            All.Remove(existing);
            if (!string.IsNullOrWhiteSpace(item.Source)) existing.Source = item.Source;
            existing.LastUsedAt = DateTime.Now;
            All.Insert(0, existing);
        }
        else
        {
            item.CreatedAt = DateTime.Now;
            item.LastUsedAt = DateTime.Now;
            All.Insert(0, item);
        }

        // 条数上限：优先淘汰最旧的未置顶项
        while (All.Count > App.Settings.MaxItems)
        {
            var oldest = All.LastOrDefault(i => !i.IsPinned);
            if (oldest == null) break;
            All.Remove(oldest);
            if (oldest.Type == ClipType.Image) StorageService.DeleteImage(oldest.ImagePath);
        }

        RebuildGroups();
        StorageService.Save();
        await Task.CompletedTask;
    }

    /// <summary>
    /// 两个条目内容是否相同。图片先比文件长度（廉价），只有长度相同才真正比内容哈希：
    /// 仅凭长度判定会把两张恰好等长的不同截图当成同一张，后来的那张被静默丢弃
    /// （截图尺寸相同时 PNG 长度撞车并不罕见，所以这一步不能省）。
    /// 文件条目只比路径（顺序无关、忽略大小写）：绝不为了去重去哈希一个几个 GB 的视频。
    /// </summary>
    private static bool IsSameContent(ClipItem a, ClipItem b)
    {
        if (a.Type != b.Type) return false;
        if (a.Type == ClipType.Text) return a.Text == b.Text;
        if (a.Type == ClipType.File)
            return a.FilePaths.Count == b.FilePaths.Count &&
                   a.FilePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(b.FilePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase);

        if (a.ImagePath == null || b.ImagePath == null) return false;
        var fa = new FileInfo(a.ImagePath);
        var fb = new FileInfo(b.ImagePath);
        if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;

        var ha = a.ImageHash ??= StorageService.HashFile(a.ImagePath);
        var hb = b.ImageHash ??= StorageService.HashFile(b.ImagePath);
        return ha != null && ha == hb;
    }

    public void RemoveItems(IEnumerable<ClipItem> items)
    {
        foreach (var item in items)
        {
            All.Remove(item);
            if (item.Type == ClipType.Image) StorageService.DeleteImage(item.ImagePath);
        }
        RebuildGroups();
        StorageService.Save();
    }

    public void Remove(ClipItem item) => RemoveItems(new[] { item });

    public void TogglePin(ClipItem item)
    {
        item.IsPinned = !item.IsPinned;
        RebuildGroups();
        StorageService.Save();
    }

    public void Touch(ClipItem item)
    {
        item.LastUsedAt = DateTime.Now;
        item.UseCount++;
        All.Remove(item);
        All.Insert(0, item);
        RebuildGroups();
        StorageService.Save();
    }

    public void UpdateText(ClipItem item, string newText)
    {
        item.Text = newText;
        RebuildGroups();
        StorageService.Save();
    }

    public void ClearAll()
    {
        foreach (var item in All.Where(i => i.Type == ClipType.Image).ToList())
            StorageService.DeleteImage(item.ImagePath);
        All.Clear();
        RebuildGroups();
        StorageService.Save();
    }

    public void SetFilter(string text)
    {
        _filter = text ?? string.Empty;
        RebuildGroups();
    }

    private void RebuildGroups()
    {
        var filtered = string.IsNullOrWhiteSpace(_filter)
            ? All
            : All.Where(i => Matches(i, _filter)).ToList();

        void Sync(ObservableCollection<ClipItem> target, IEnumerable<ClipItem> source)
        {
            target.Clear();
            foreach (var x in source) target.Add(x);
        }

        Sync(Pinned, filtered.Where(i => i.IsPinned));
        Sync(Recent, filtered.Where(i => !i.IsPinned));
        GroupsChanged?.Invoke();
    }

    /// <summary>
    /// 搜索匹配：文字条目比内容，文件条目比文件名（按视频找以前复制过的片子是最常见的用法）。
    /// 图片存的是 Pasty 自己生成的 GUID 文件名，比它没有意义。
    /// </summary>
    private static bool Matches(ClipItem item, string filter) => item.Type switch
    {
        ClipType.Text => item.Text.Contains(filter, StringComparison.OrdinalIgnoreCase),
        ClipType.File => item.FilePaths.Any(p =>
            Path.GetFileName(p).Contains(filter, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };
}
