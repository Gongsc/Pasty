using System.IO;
using System.Windows.Media.Imaging;

namespace Pasty.IconGen;

/// <summary>
/// .ico 打包。没走 System.Drawing 的 Icon 那条路：它存不了多尺寸，
/// 而多档细节正是这套图标的全部意义，所以二进制自己拼。
/// </summary>
internal static class Ico
{
    /// <summary>超过这个尺寸用 PNG 压缩，其余用 DIB —— DIB 的取图兼容性最好。</summary>
    private const int PngThreshold = 64;

    public static void Write(string path, IReadOnlyList<(int Size, BitmapSource Bmp)> frames)
    {
        var payloads = frames.Select(f => f.Size > PngThreshold ? Png(f.Bmp) : Dib(f.Bmp, f.Size)).ToList();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((ushort)0);             // reserved
        w.Write((ushort)1);             // 1 = 图标（2 是光标）
        w.Write((ushort)frames.Count);

        var offset = 6 + 16 * frames.Count;
        for (var i = 0; i < frames.Count; i++)
        {
            var size = frames[i].Size;
            w.Write((byte)(size >= 256 ? 0 : size)); // 256 只有一个字节装不下，约定写 0
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);           // 调色板色数，32bpp 恒为 0
            w.Write((byte)0);           // reserved
            w.Write((ushort)1);         // planes
            w.Write((ushort)32);        // bpp
            w.Write(payloads[i].Length);
            w.Write(offset);
            offset += payloads[i].Length;
        }
        foreach (var p in payloads) w.Write(p);
    }

    private static byte[] Png(BitmapSource bmp)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
    /// <summary>
    /// BITMAPINFOHEADER + 32bpp 颜色数据 + 1bpp AND 掩码。
    /// biHeight 写两倍高度：颜色区和掩码区在 .ico 里算同一张位图。行序自下而上。
    /// 32bpp 图标的透明其实由 alpha 决定，但掩码不能省——缺了它，
    /// 某些老的取图路径会把整张图当成不透明矩形画出来。
    /// </summary>
    private static byte[] Dib(BitmapSource bmp, int size)
    {
        var px = Straight(bmp, size);
        var maskStride = (size + 31) / 32 * 4;   // 1bpp，每行补齐到 4 字节

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(40);                     // biSize
        w.Write(size);                   // biWidth
        w.Write(size * 2);               // biHeight = 颜色 + 掩码
        w.Write((ushort)1);              // biPlanes
        w.Write((ushort)32);             // biBitCount
        w.Write(0);                      // biCompression = BI_RGB
        w.Write(size * size * 4 + maskStride * size); // biSizeImage
        w.Write(0); w.Write(0);          // 像素密度，图标里无意义
        w.Write(0); w.Write(0);          // biClrUsed / biClrImportant

        for (var y = size - 1; y >= 0; y--)
            w.Write(px, y * size * 4, size * 4);

        for (var y = size - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            for (var x = 0; x < size; x++)
                if (px[(y * size + x) * 4 + 3] == 0) // 全透明的像素掩码位置 1
                    row[x / 8] |= (byte)(0x80 >> (x % 8));
            w.Write(row);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Pbgra32（预乘 alpha）→ 直通 BGRA。.ico 的 32bpp 数据是不预乘的，
    /// 把预乘值原样写进去，所有半透明边缘会暗一圈（圆角和 V 的抗锯齿边最明显）。
    /// </summary>
    private static byte[] Straight(BitmapSource bmp, int size)
    {
        var px = new byte[size * size * 4];
        bmp.CopyPixels(px, size * 4, 0);
        for (var i = 0; i < px.Length; i += 4)
        {
            var a = px[i + 3];
            if (a == 255) continue;
            if (a == 0)
            {
                px[i] = px[i + 1] = px[i + 2] = 0;
                continue;
            }
            for (var c = 0; c < 3; c++)
                px[i + c] = (byte)Math.Min(255, px[i + c] * 255 / a);
        }
        return px;
    }
}
