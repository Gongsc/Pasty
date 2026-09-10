using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Pasty.Models;

namespace Pasty.Services;

/// <summary>把条目写入剪贴板并向目标窗口发送 Ctrl+V。</summary>
public static class PasteService
{
    // 连续触发时顺序执行，避免几次剪贴板写入和注入按键互踩；
    // 不能直接丢弃，否则用户按住 Ctrl 后逐次按 V，后面的有效按键会没有反应。
    private static readonly SemaphoreSlim PasteGate = new(1, 1);

    public static async Task PasteAsync(ClipItem item, IntPtr targetHwnd)
    {
        await PasteGate.WaitAsync();
        try
        {
            // 内容已经不在了（源文件被删、U 盘拔了、图片文件丢了）就什么都别做：
            // 既不该把用户原本的剪贴板清空，也不值得为一次注定失败的粘贴抢前台窗口。
            if (!item.CanWriteToClipboard)
            {
                Trace.Log($"paste 条目内容已失效，取消 item={item.Type}");
                return;
            }
            // 目标必须是仍然存活、可见且不属于本进程的窗口。否则 ForceForeground 会静默失败，
            // 而 SendInput 只认“当前前台窗口”，Ctrl+V 就会落到 Pasty 自己或某个无关窗口里。
            var canSendKeys = Win32.IsPasteTarget(targetHwnd);
            Trace.Log($"paste 开始 item={item.Type} target=0x{targetHwnd.ToInt64():X} 可发送按键={canSendKeys}");
            ClipboardMonitor.Suspended = true;
            try
            {
                await WriteToClipboardAsync(item);
                Trace.Log("paste 剪贴板已写入");

                if (!canSendKeys)
                {
                    // 例如从托盘打开主窗口后直接点“粘贴”：没有外部目标可送按键，
                    // 但内容已经进了剪贴板，用户切到目标应用自己按 Ctrl+V 即可。
                    Trace.Log("paste 无有效外部目标，仅写入剪贴板");
                    return;
                }

                if (Win32.IsIconic(targetHwnd)) Win32.ShowWindow(targetHwnd, 9 /* SW_RESTORE */);
                // 键盘触发时目标本来就在前台，直接发送即可；只有鼠标从 Pasty 窗口发起时
                // 才强切并给窗口一个消息泵回合，不再让所有 Ctrl+V 固定等待 140ms。
                if (Win32.GetForegroundWindow() != targetHwnd)
                {
                    Win32.ForceForeground(targetHwnd);
                    await Task.Delay(30);
                }

                var sent = Win32.SendCtrlV();
                Trace.Log($"paste SendInput 结果={sent} (0=失败)");
                if (sent == 0)
                    Trace.Log($"paste SendInput 失败，错误码={Marshal.GetLastWin32Error()}");
                Trace.Log($"paste 已发送 Ctrl+V 到 0x{Win32.GetForegroundWindow().ToInt64():X}");
                // 使用次数与排序由调用方的 ViewModel.Touch 统一负责，这里不再重复计数
            }
            catch (Exception ex)
            {
                Trace.Log($"paste 异常: {ex.Message}");
            }
            finally
            {
                ClipboardMonitor.Suspended = false;
                Trace.Log("paste 结束");
            }
        }
        finally
        {
            PasteGate.Release();
        }
    }

    /// <summary>仅写入剪贴板，不发送按键。</summary>
    public static async Task WriteToClipboardAsync(ClipItem item)
    {
        if (item.Type == ClipType.Text)
        {
            var package = new DataPackage();
            package.SetText(item.Text);
            Clipboard.SetContent(package);
            await FlushWithRetryAsync();
        }
        else if (item.Type == ClipType.Image)
        {
            // 图片用 Win32 写标准 CF_DIB + PNG 格式，兼容所有应用的 Ctrl+V
            if (item.ImagePath != null && File.Exists(item.ImagePath))
                await WriteImageClipboardAsync(item.ImagePath);
        }
        else if (item.Type == ClipType.File)
        {
            await WriteFileDropClipboardAsync(item);
        }
        else
        {
            return; // 什么都没写，不能推进序号，否则会连带吞掉别人的下一次复制
        }
        ClipboardMonitor.MarkSelfWrite();
    }

    /// <summary>
    /// 写 CF_HDROP（文件列表）。只要有一个源文件已经不在，就整个不写：
    /// 把不存在的路径塞进剪贴板，目标应用只会报“找不到文件”，还不如保持用户原有的内容。
    /// </summary>
    private static async Task WriteFileDropClipboardAsync(ClipItem item)
    {
        var paths = item.FilePaths;
        if (paths.Count == 0) return;
        foreach (var p in paths)
        {
            try
            {
                if (!File.Exists(p) && !Directory.Exists(p)) return;
            }
            catch { return; } // 路径本身非法（断开的网络盘之类）
        }

        var data = Win32.BuildDropFiles(paths);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (Win32.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    Win32.EmptyClipboard();
                    var ok = Win32.SetClipboardBytes(Win32.CF_HDROP, data);
                    // 必须一并写 Preferred DropEffect。不写时资源管理器粘过去可能按“移动”理解，
                    // 那会把用户的原文件搬走——一个剪贴板管理器最不能犯的就是这个错。
                    ok &= Win32.SetClipboardBytes(s_preferredDropEffect, PreferredDropEffectCopy);
                    if (ok) return;
                }
                finally
                {
                    Win32.CloseClipboard();
                }
            }
            await Task.Delay(80); // 剪贴板被占用，稍后重试
        }
    }

    /// <summary>CFSTR_PREFERREDDROPEFFECT：4 字节的 DROPEFFECT 位掩码，5 = DROPEFFECT_COPY。</summary>
    private static readonly byte[] PreferredDropEffectCopy = { 5, 0, 0, 0 };

    private static readonly uint s_preferredDropEffect =
        Win32.RegisterClipboardFormatW("Preferred DropEffect");

    private static async Task FlushWithRetryAsync()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.Flush();
                return;
            }
            catch
            {
                await Task.Delay(80);
            }
        }
    }

    private static async Task WriteImageClipboardAsync(string imagePath)
    {
        // 解码为 32bpp BGRA 像素，构造自下而上的 CF_DIB
        using var stream = File.OpenRead(imagePath).AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var pixels = pixelData.DetachPixelData();

        var width = (int)decoder.PixelWidth;
        var height = (int)decoder.PixelHeight;
        var dib = new byte[40 + width * height * 4];
        BitConverter.GetBytes(40).CopyTo(dib, 0);                       // biSize
        BitConverter.GetBytes(width).CopyTo(dib, 4);                    // biWidth
        BitConverter.GetBytes(height).CopyTo(dib, 8);                   // biHeight（正数 = 自下而上）
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);                // biPlanes
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);               // biBitCount
        BitConverter.GetBytes(0u).CopyTo(dib, 16);                      // biCompression = BI_RGB
        BitConverter.GetBytes((uint)(width * height * 4)).CopyTo(dib, 20); // biSizeImage
        for (var row = 0; row < height; row++)
            System.Buffer.BlockCopy(pixels, row * width * 4, dib, 40 + (height - 1 - row) * width * 4, width * 4);

        var png = await File.ReadAllBytesAsync(imagePath);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (Win32.OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    Win32.EmptyClipboard();
                    var ok = Win32.SetClipboardBytes(Win32.CF_DIB, dib);
                    ok &= Win32.SetClipboardBytes(Win32.RegisterClipboardFormatW("PNG"), png);
                    if (ok) return;
                }
                finally
                {
                    Win32.CloseClipboard();
                }
            }
            await Task.Delay(80); // 剪贴板被占用，稍后重试
        }
    }
}
