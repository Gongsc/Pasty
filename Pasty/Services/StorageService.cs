using System.IO;
using System.Security.Cryptography;
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
        var indexReadable = true;
        try
        {
            if (File.Exists(IndexPath))
                Items = JsonSerializer.Deserialize<List<ClipItem>>(File.ReadAllText(IndexPath)) ?? new();
        }
        catch
        {
            Items = new();
            indexReadable = false; // 索引损坏或读不到，此时“没人引用”不能当真
        }
        // 过滤掉已被删除的图片
        Items.RemoveAll(i => i.Type == ClipType.Image && (i.ImagePath == null || !File.Exists(i.ImagePath)));
        // 索引读失败时 Items 是空的，一扫就会把用户全部图片删光，只在读到了索引时清理
        if (indexReadable) SweepOrphanImages();
    }

    /// <summary>
    /// 删除 images 目录里没有任何条目引用的 PNG。
    /// 保存图片和写入索引是两步，中间崩溃、或去重命中后忘记删除新文件，
    /// 都会留下永远不会再被读取的文件；不清理的话它们会一直占着磁盘。
    /// 必须在 Items 加载完成之后调用，否则会把全部图片当成孤儿删掉。
    /// </summary>
    public static void SweepOrphanImages()
    {
        try
        {
            if (!Directory.Exists(ImagesDir)) return;
            var referenced = Items
                .Where(i => i.Type == ClipType.Image && i.ImagePath != null)
                .Select(i => Path.GetFullPath(i.ImagePath!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(ImagesDir, "*.png"))
            {
                if (referenced.Contains(Path.GetFullPath(file))) continue;
                try { File.Delete(file); } catch { /* 单个文件删不掉就跳过 */ }
            }
        }
        catch { /* 目录枚举失败不影响启动 */ }
    }

    /// <summary>文件内容的 SHA-256（十六进制小写）；读不到返回 null。</summary>
    public static string? HashFile(string? path)
    {
        try
        {
            if (path == null || !File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    /// <summary>Flush 之后置起，让还排在队列里的后台保存直接放弃——它们手上的快照都比 Flush 的旧。</summary>
    private static volatile bool s_shuttingDown;

    /// <summary>后台线程异步落盘，避免阻塞 UI 线程（低级键盘钩子依赖 UI 线程及时响应）。</summary>
    public static void Save()
    {
        if (s_shuttingDown) return;
        var snapshot = Items.ToList();
        Task.Run(async () =>
        {
            await SaveLock.WaitAsync();
            try
            {
                if (s_shuttingDown) return;
                WriteIndex(snapshot);
            }
            catch { /* 忽略瞬时 IO 错误，下次保存重试 */ }
            finally
            {
                SaveLock.Release();
            }
        });
    }

    /// <summary>
    /// 退出前同步落盘。Save() 是后台 fire-and-forget，进程随即结束就会丢掉最后一次写入——
    /// 表现为退出前刚复制的几条、刚做的置顶或编辑，下次启动全都不见了。
    /// 等不到锁就放弃，绝不因为落盘卡住退出流程。
    /// </summary>
    public static void Flush(int waitMs = 2000)
    {
        var snapshot = Items.ToList();
        var acquired = false;
        try
        {
            acquired = SaveLock.Wait(waitMs); // 等在飞的那次后台保存写完，避免两个进程内写者互踩
            s_shuttingDown = true;            // 队列里还没跑的保存从此作废，不会用旧快照盖掉这次
            WriteIndex(snapshot);
        }
        catch { /* 退出路径上任何失败都只能忍受 */ }
        finally
        {
            if (acquired) SaveLock.Release();
        }
    }

    private static void WriteIndex(List<ClipItem> snapshot)
    {
        Directory.CreateDirectory(DataDir);
        var tmp = IndexPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
        File.Move(tmp, IndexPath, overwrite: true);
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
