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
