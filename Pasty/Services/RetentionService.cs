using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Pasty.Models;

namespace Pasty.Services;

/// <summary>按保存时长定期清理超期的未置顶条目。</summary>
public sealed class RetentionService
{
    private readonly DispatcherQueue _dispatcher;
    private DispatcherQueueTimer? _timer;

    public RetentionService(DispatcherQueue dispatcher) => _dispatcher = dispatcher;

    public void Start()
    {
        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(10);
        _timer.Tick += (s, e) => Clean();
        _timer.Start();
        Clean();
    }

    public static void Clean()
    {
        var days = App.Settings.RetentionDays;
        if (days <= 0) return;
        var cutoff = DateTime.Now.AddDays(-days);
        var expired = StorageService.Items.Where(i => !i.IsPinned && i.CreatedAt < cutoff).ToList();
        if (expired.Count == 0) return;

        foreach (var item in expired)
        {
            StorageService.Items.Remove(item);
            if (item.Type == ClipType.Image) StorageService.DeleteImage(item.ImagePath);
        }
        StorageService.Save();
        App.ViewModel.RemoveItems(expired);
    }
}
