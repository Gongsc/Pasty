using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Pasty.Models;

namespace Pasty.Services;

/// <summary>监听系统剪贴板变化，产出 ClipItem（文本/图片）。</summary>
public sealed class ClipboardMonitor
{
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
        _ = ReadClipboardAsync();
        return false;
    }

    private async Task ReadClipboardAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text))
            {
                var text = await content.GetTextAsync();
                if (string.IsNullOrWhiteSpace(text)) return;
                ClipboardChanged?.Invoke(new ClipItem { Type = ClipType.Text, Text = text });
            }
            else if (content.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await content.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var pixelData = await decoder.GetPixelDataAsync();
                var png = await EncodePngAsync(decoder.BitmapPixelFormat, pixelData.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
                if (png == null) return;
                var path = StorageService.SaveImage(png);
                ClipboardChanged?.Invoke(new ClipItem { Type = ClipType.Image, ImagePath = path });
            }
        }
        catch
        {
            // 剪贴板被其他进程占用时读取失败，忽略本次
        }
    }

    private static async Task<byte[]?> EncodePngAsync(BitmapPixelFormat format, byte[] pixels, int width, int height)
    {
        try
        {
            using var ms = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
            encoder.SetPixelData(format, BitmapAlphaMode.Premultiplied, (uint)width, (uint)height, 96, 96, pixels);
            await encoder.FlushAsync();
            var bytes = new byte[ms.Size];
            using var reader = new DataReader(ms.GetInputStreamAt(0));
            await reader.LoadAsync((uint)ms.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }
}
