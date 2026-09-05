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

    /// <summary>Ctrl+V 覆盖模式要粘贴的“第一条”：按最近复制/使用时间取，而非置顶优先。</summary>
    public ClipItem? TopItem =>
        StorageService.Items.FirstOrDefault();

    public Task LoadFromStorageAsync()
    {
        RebuildGroups();
        return Task.CompletedTask;
    }

    public async Task AddOrUpdateAsync(ClipItem item)
    {
        var existing = All.FirstOrDefault(i =>
            i.Type == item.Type &&
            (item.Type == ClipType.Text
                ? i.Text == item.Text
                : item.ImagePath != null && File.Exists(item.ImagePath) && File.Exists(i.ImagePath) &&
                  new FileInfo(i.ImagePath).Length == new FileInfo(item.ImagePath).Length));

        if (existing != null)
        {
            // 相同内容：上移并刷新时间
            All.Remove(existing);
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
            : All.Where(i => i.Type == ClipType.Text &&
                             i.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase)).ToList();

        void Sync(ObservableCollection<ClipItem> target, IEnumerable<ClipItem> source)
        {
            target.Clear();
            foreach (var x in source) target.Add(x);
        }

        Sync(Pinned, filtered.Where(i => i.IsPinned));
        Sync(Recent, filtered.Where(i => !i.IsPinned));
        GroupsChanged?.Invoke();
    }
}
