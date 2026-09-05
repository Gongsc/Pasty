using System.IO;

namespace Pasty.Services;

/// <summary>轻量追踪日志：定位粘贴链路问题用，写入 %LOCALAPPDATA%\Pasty\trace.log。</summary>
public static class Trace
{
    private static readonly object Lock = new();
    public static bool Enabled = true;

    public static void Log(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Lock)
            {
                File.AppendAllText(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pasty", "trace.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {message}\r\n");
            }
        }
        catch { }
    }
}
