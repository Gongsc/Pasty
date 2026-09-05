using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Pasty.Models;

namespace Pasty.Services;

/// <summary>把条目写入剪贴板并向目标窗口发送 Ctrl+V。</summary>
public static class PasteService
{
    private static bool _busy; // 粘贴流程进行中，忽略新触发，避免按键/剪贴板互踩

    public static async Task PasteAsync(ClipItem item, IntPtr targetHwnd)
    {
        if (_busy)
        {
            Trace.Log("paste 忙碌中，忽略新触发");
            return;
        }
        _busy = true;
        Trace.Log($"paste 开始 item={item.Type} target=0x{targetHwnd.ToInt64():X} 前台=0x{Win32.GetForegroundWindow().ToInt64():X}");
        ClipboardMonitor.Suspended = true;
        HotkeyService.SuppressHookAction = true;
        try
        {
            await WriteToClipboardAsync(item);
            Trace.Log("paste 剪贴板已写入");
            await Task.Delay(80);

            if (targetHwnd != IntPtr.Zero)
            {
                if (Win32.IsIconic(targetHwnd)) Win32.ShowWindow(targetHwnd, 9 /* SW_RESTORE */);
                // 用户刚在目标窗口按下 Ctrl+V 时它已是前台，无需切换；仅在必要时强切
                Win32.ForceForeground(targetHwnd);
                await Task.Delay(60);
            }

            var sent = Win32.SendCtrlV();
            Trace.Log($"paste SendInput 结果={sent} (0=失败)");
            if (sent == 0)
                Trace.Log($"paste SendInput 失败，错误码={Marshal.GetLastWin32Error()}");
            Trace.Log($"paste 已发送 Ctrl+V 到 0x{Win32.GetForegroundWindow().ToInt64():X}");
            item.LastUsedAt = DateTime.Now;
            item.UseCount++;
            StorageService.Save();
        }
        catch (Exception ex)
        {
            Trace.Log($"paste 异常: {ex.Message}");
        }
        finally
        {
            await Task.Delay(200); // 等目标应用处理完按键再解除挂起
            HotkeyService.SuppressHookAction = false;
            ClipboardMonitor.Suspended = false;
            _busy = false;
            Trace.Log("paste 结束");
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
        else if (item.ImagePath != null && File.Exists(item.ImagePath))
        {
            // 图片用 Win32 写标准 CF_DIB + PNG 格式，兼容所有应用的 Ctrl+V
            await WriteImageClipboardAsync(item.ImagePath);
        }
    }

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
