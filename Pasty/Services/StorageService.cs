using System.IO;
using System.Text.Json;
using Pasty.Models;

namespace Pasty.Services;

public static class StorageService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static List<ClipItem> Items { get; private set; } = new();

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pasty");

    private static string IndexPath => Path.Combine(DataDir, "index.json");
    private static string ImagesDir => Path.Combine(DataDir, "images");

    public static void Load()
    {
        try
        {
            if (File.Exists(IndexPath))
                Items = JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(IndexPath)) ?? new();
        }
        catch
        {
            Items = new();
        }
        // 过滤掉已被删除的图片
        Items.RemoveAll(i => i.Type == ClipType.Image && (i.ImagePath == null || !File.Exists(i.ImagePath)));
    }

    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    /// <summary>后台线程异步落盘，避免阻塞 UI 线程（低级键盘钩子依赖 UI 线程及时响应）。</summary>
    public static void Save()
    {
        var snapshot = Items.ToList();
        Task.Run(async () =>
        {
            await SaveLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(DataDir);
                var tmp = IndexPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
                File.Move(tmp, IndexPath, overwrite: true);
            }
            catch { /* 忽略瞬时 IO 错误，下次保存重试 */ }
            finally
            {
                SaveLock.Release();
            }
        });
    }

    public static string SaveImage(byte[] pngBytes)
    {
        Directory.CreateDirectory(ImagesDir);
        var path = Path.Combine(ImagesDir, $"{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, pngBytes);
        return path;
    }

    public static void DeleteImage(string? path)
    {
        try
        {
            if (path != null && File.Exists(path) &&
                Path.GetFullPath(path).StartsWith(Path.GetFullPath(ImagesDir), StringComparison.OrdinalIgnoreCase))
                File.Delete(path);
        }
        catch { /* 忽略删除失败 */ }
    }
}
