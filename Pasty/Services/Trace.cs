using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace Pasty.Services;

/// <summary>
/// 轻量追踪日志：定位粘贴链路问题用，默认关闭。
/// 开启方式：在 %LOCALAPPDATA%\Pasty 下建一个名为 trace 的空文件，重启生效。
///
/// 两条硬约束：
/// 1) 绝不记录剪贴板内容本身。这是剪贴板管理器，用户复制的就是密码、令牌和恢复码，
///    把内容（哪怕只是前若干字符）写进明文日志等于把它们长期留在磁盘上。
/// 2) 写入走后台队列，调用方不阻塞。低级键盘钩子在 UI 线程上回调，同步写文件一旦
///    超过 LowLevelHooksTimeout（默认 300ms），系统会静默摘掉钩子。
/// </summary>
public static class Trace
{
    private const long MaxBytes = 1024 * 1024; // 超过则轮转一次，避免无限增长

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pasty");

    private static string LogPath => Path.Combine(DataDir, "trace.log");

    /// <summary>由 flag 文件开启：用户不主动建文件就完全不落盘。</summary>
    public static bool Enabled { get; set; } = File.Exists(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pasty", "trace"));

    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 512);
    private static readonly object WriterLock = new();
    private static Task? s_writer;

    public static void Log(string message)
    {
        if (!Enabled) return;
        EnsureWriter();
        Queue.TryAdd($"{DateTime.Now:HH:mm:ss.fff} {message}"); // 队列满则丢弃，绝不阻塞调用方
    }

    /// <summary>退出前把队列里剩余的行写完（最多等 waitMs 毫秒）。</summary>
    public static void Flush(int waitMs = 500)
    {
        var writer = s_writer;
        if (writer == null) return;
        Queue.CompleteAdding();
        try { writer.Wait(waitMs); } catch { /* 超时或异常都不阻碍退出 */ }
    }

    private static void EnsureWriter()
    {
        if (s_writer != null) return;
        lock (WriterLock)
        {
            s_writer ??= Task.Factory.StartNew(Drain, TaskCreationOptions.LongRunning);
        }
    }

    private static void Drain()
    {
        foreach (var first in Queue.GetConsumingEnumerable())
        {
            var batch = new StringBuilder().AppendLine(first);
            while (Queue.TryTake(out var more)) batch.AppendLine(more);
            try
            {
                Directory.CreateDirectory(DataDir);
                Rotate();
                File.AppendAllText(LogPath, batch.ToString());
            }
            catch { /* 落盘失败不影响主流程 */ }
        }
    }

    private static void Rotate()
    {
        var info = new FileInfo(LogPath);
        if (!info.Exists || info.Length < MaxBytes) return;
        var previous = LogPath + ".1";
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(LogPath, previous);
    }
}
