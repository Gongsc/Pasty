using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Pasty.Models;

namespace Pasty.Services;

/// <summary>监听系统剪贴板变化，产出 ClipItem（文本/图片/文件）。</summary>
public sealed class ClipboardMonitor
{
    private static readonly uint PngFormat = Win32.RegisterClipboardFormatW("PNG");
    private static readonly uint MimePngFormat = Win32.RegisterClipboardFormatW("image/png");

    public event Action<ClipItem>? ClipboardChanged;

    /// <summary>自身粘贴/写剪贴板期间挂起监听，避免重复记录。</summary>
    public static bool Suspended { get; set; }

    /// <summary>
    /// 自身最近一次写剪贴板后观察到的序号。WM_CLIPBOARDUPDATE 是“投递”的消息，
    /// 只有在处理函数返回、消息泵转起来之后才会到达；而写剪贴板的调用链会在同一个
    /// 同步段里把 Suspended 复位，通知到达时守卫早已关闭，于是自己写的内容被回录成新条目。
    /// 序号只增不减，凡是不大于自身写入后序号的变化必然是我们自己造成的，据此确定性地过滤。
    /// </summary>
    private static uint s_selfWriteSeq;

    /// <summary>自身写完剪贴板后调用，使随后投递到达的那次变化通知被忽略。</summary>
    public static void MarkSelfWrite() => s_selfWriteSeq = Win32.GetClipboardSequenceNumber();

    private readonly IntPtr _hwnd;

    public ClipboardMonitor(MessageWindow messageWindow)
    {
        _hwnd = messageWindow.Handle;
        Win32.AddClipboardFormatListener(_hwnd);
        messageWindow.ProcessMessage += OnMessage;
    }

    private bool OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != Win32.WM_CLIPBOARDUPDATE || Suspended) return false;
        if (Win32.GetClipboardSequenceNumber() <= s_selfWriteSeq) return false; // 自己写入引发的通知
        // 异步读取与重试期间前台窗口可能已经切走，来源窗口必须在收到通知这一刻记下。
        // 这里只取一个句柄，进程查询留到异步读取完成后，避免在 UI 消息回调里多做工作。
        var sourceWindow = Win32.GetForegroundWindow();
        _ = ReadClipboardAsync(sourceWindow);
        return false;
    }

    private async Task ReadClipboardAsync(IntPtr sourceWindow)
    {
        // WM_CLIPBOARDUPDATE 与低级键盘钩子共用 UI 消息泵。先让消息处理函数返回，
        // 避免复制较大的原生 DIB 时同步占住回调、触发系统静默摘掉键盘钩子。
        await Task.Yield();
        var item = await ReadWithRetryAsync();
        if (item == null) return;
        item.Source = GetSource(sourceWindow);
        try { ClipboardChanged?.Invoke(item); }
        catch (Exception ex) { Trace.Log($"clipboard 通知处理失败: {ex.Message}"); }
    }

    /// <summary>
    /// 来源只保存进程名，不保存窗口标题。标题常带文档名、网页标题等用户内容，
    /// 既不适合作为稳定的应用标识，也不该被剪贴板历史额外持久化。
    /// </summary>
    private static string GetSource(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            Win32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return string.Empty;
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 读剪贴板，失败则重试。剪贴板同一时刻只能被一个进程打开，别的应用
    /// （Office、浏览器、远程桌面、密码管理器）正持有时 GetContent 直接抛异常。
    /// 原先一次失败就静默丢弃整条，用户这次复制凭空消失且没有任何提示。
    /// </summary>
    private static async Task<ClipItem?> ReadWithRetryAsync()
    {
        const int attempts = 5;
        for (var i = 0; i < attempts; i++)
        {
            try
            {
                return await ReadOnceAsync();
            }
            catch (Exception ex)
            {
                if (i == attempts - 1)
                {
                    Trace.Log($"clipboard 读取重试耗尽，本次变化丢弃: {ex.Message}");
                    return null;
                }
                await Task.Delay(60 * (i + 1)); // 退让，等占用剪贴板的进程收工
            }
        }
        return null;
    }

    /// <summary>读一次剪贴板；既不是文本、也不是图片、也不是文件时返回 null。失败向上抛，由调用方决定是否重试。</summary>
    private static async Task<ClipItem?> ReadOnceAsync()
    {
        var content = Clipboard.GetContent();
        if (content.Contains(StandardDataFormats.Text))
        {
            var text = await content.GetTextAsync();
            return string.IsNullOrWhiteSpace(text)
                ? null
                : new ClipItem { Type = ClipType.Text, Text = text };
        }

        // Windows 截图工具常直接放入注册格式 PNG 或 CF_DIBV5；unpackaged WinUI 进程里，
        // DataPackage 不一定把这些格式投影成 StandardDataFormats.Bitmap。先走原生格式，
        // 再保留下面的 WinRT 路径兼容浏览器等提供 StorageItem 位图引用的应用。
        var nativePng = await ReadNativeImageAsync();
        if (nativePng != null)
            return new ClipItem { Type = ClipType.Image, ImagePath = StorageService.SaveImage(nativePng) };

        if (content.Contains(StandardDataFormats.Bitmap))
        {
            var reference = await content.GetBitmapAsync();
            using var stream = await reference.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var png = await EncodePngAsync(decoder);
            return png == null
                ? null
                : new ClipItem { Type = ClipType.Image, ImagePath = StorageService.SaveImage(png) };
        }
        // 文件（视频、音频、压缩包、文件夹……）排在最后：浏览器“复制网页图片”之类
        // 会同时留下位图和临时文件路径，按图片记才不会存一个随时会被清掉的临时文件路径。
        // 这里走 Win32 而不是 DataPackage.GetStorageItemsAsync()：后者要为每个路径构造
        // shell item，慢且不稳定的时候会把 UI 线程的消息泵堵在那里（键盘钩子靠它）。
        var files = Win32.ReadClipboardFileDrop();
        return files.Count > 0
            ? new ClipItem { Type = ClipType.File, FilePaths = files }
            : null;
    }

    /// <summary>读取原生 PNG / DIB，并统一转换成磁盘所需的 PNG。</summary>
    private static async Task<byte[]?> ReadNativeImageAsync()
    {
        foreach (var format in new[] { PngFormat, MimePngFormat })
        {
            var png = Win32.ReadClipboardBytes(format);
            if (png is { Length: >= 8 } &&
                png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47 &&
                png[4] == 0x0D && png[5] == 0x0A && png[6] == 0x1A && png[7] == 0x0A)
                return png;
        }

        // 优先 V5：它能完整表达截图中的 alpha 与色彩空间；没有时再退回普通 CF_DIB。
        var dib = Win32.ReadClipboardBytes(Win32.CF_DIBV5) ?? Win32.ReadClipboardBytes(Win32.CF_DIB);
        if (dib == null) return null;
        var bmp = WrapDibAsBmp(dib);
        if (bmp == null) return null;

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bmp);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await EncodePngAsync(decoder);
    }

    /// <summary>
    /// CF_DIB 没有普通 BMP 文件开头的 14 字节 BITMAPFILEHEADER；WIC 解码器需要它。
    /// 这里根据 DIB 头、颜色表、位掩码与 V5 色彩配置算出像素偏移，再补一个文件头。
    /// </summary>
    private static byte[]? WrapDibAsBmp(byte[] dib)
    {
        if (dib.Length < 12) return null;
        var headerSize = BitConverter.ToUInt32(dib, 0);
        if (headerSize < 12 || headerSize > dib.Length) return null;

        uint pixelOffset;
        if (headerSize == 12) // BITMAPCOREHEADER
        {
            var bitCount = BitConverter.ToUInt16(dib, 10);
            var colors = bitCount <= 8 ? 1u << bitCount : 0;
            pixelOffset = headerSize + colors * 3;
        }
        else
        {
            if (headerSize < 40 || dib.Length < 40) return null;
            var bitCount = BitConverter.ToUInt16(dib, 14);
            var compression = BitConverter.ToUInt32(dib, 16);
            var colorsUsed = BitConverter.ToUInt32(dib, 32);
            var colors = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1u << bitCount : 0;
            var masks = headerSize == 40
                ? compression == 3 ? 12u : compression == 6 ? 16u : 0u
                : 0u;
            pixelOffset = headerSize + masks + colors * 4;

            // V5 的内嵌 ICC 配置可能位于像素之前，不能把它误当像素数据。
            if (headerSize >= 124 && dib.Length >= 120)
            {
                var profileOffset = BitConverter.ToUInt32(dib, 112);
                var profileSize = BitConverter.ToUInt32(dib, 116);
                if (profileOffset <= dib.Length && profileSize <= dib.Length - profileOffset)
                    pixelOffset = Math.Max(pixelOffset, profileOffset + profileSize);
            }
        }

        if (pixelOffset >= dib.Length || dib.Length > int.MaxValue - 14) return null;
        var bmp = new byte[dib.Length + 14];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes((uint)bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(pixelOffset + 14).CopyTo(bmp, 10);
        System.Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);
        return bmp;
    }

    /// <summary>
    /// 把解码出的位图编码成 PNG。
    ///
    /// 两端都显式指定 Bgra8 + Straight。原先用无参 GetPixelDataAsync（返回源图的
    /// 原生格式与原生 alpha 模式），却把 SetPixelData 的 alpha 模式硬编码成
    /// Premultiplied：源图是直通 alpha 时（截图工具和浏览器给的位图常常是），
    /// 编码器会按“已预乘”再反算一遍，半透明区域整体发暗、边缘出现黑边。
    /// PNG 存的本来就是直通 alpha，两端都取 Straight 最省事也最不容易再错，
    /// 并且与 PasteService 写回剪贴板时请求的 alpha 模式一致。
    /// </summary>
    private static async Task<byte[]?> EncodePngAsync(BitmapDecoder decoder)
    {
        try
        {
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            using var ms = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                decoder.PixelWidth, decoder.PixelHeight, 96, 96, pixelData.DetachPixelData());
            await encoder.FlushAsync();

            var bytes = new byte[ms.Size];
            using var reader = new DataReader(ms.GetInputStreamAt(0));
            await reader.LoadAsync((uint)ms.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            Trace.Log($"clipboard PNG 编码失败: {ex.Message}"); // 编码失败与占用无关，重试也没用
            return null;
        }
    }
}
