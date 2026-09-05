using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pasty.IconGen;

/// <summary>
/// 从矢量几何生成 Pasty 的 .ico 资源。图标形状的唯一来源就是这里的坐标，
/// 手改 .ico 无法维护，所以生成器随代码一起提交。
///
/// 用法：dotnet run --project tools/IconGen -- &lt;输出目录&gt;
///
/// 三级细节分别绘制而非缩放同一张图：拟物化的铆钉、纸叠、明暗带在 16px 下
/// 只会退化成脏点。L1 全细节给大尺寸，L2 去掉细节保留板厚，L3 退回平涂。
/// 立体感全部由平涂色阶堆出——硬边明暗带、逐层加深的纸色、偏移实色投影，
/// 不使用任何渐变。
/// </summary>
internal static class Program
{
    /// <summary>应用图标各档尺寸用哪一级细节。</summary>
    private static readonly (int Size, int Lod)[] AppSizes =
    {
        (256, 1), (128, 1), (64, 1), (48, 1), (40, 2), (32, 2), (24, 2), (20, 3), (16, 3),
    };

    /// <summary>托盘只有小尺寸：16 = 100% DPI，20 = 125%，24 = 150%，32 = 200%。</summary>
    private static readonly int[] TraySizes = { 32, 24, 20, 16 };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("用法：IconGen <输出目录>");
            return 1;
        }
        var dir = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(dir);

        Write(Path.Combine(dir, "Pasty.ico"),
            AppSizes.Select(s => (s.Size, Render(dc => Art.App(dc, s.Lod), s.Size))));

        Write(Path.Combine(dir, "Tray-light.ico"),
            TraySizes.Select(s => (s, Render(dc => Art.Tray(dc, Art.TrayLight), s))));

        Write(Path.Combine(dir, "Tray-dark.ico"),
            TraySizes.Select(s => (s, Render(dc => Art.Tray(dc, Art.TrayDark), s))));

        return 0;
    }

    /// <summary>按目标像素尺寸真实栅格化。几何写在 64 单位网格上，这里整体缩放过去。</summary>
    private static BitmapSource Render(Action<DrawingContext> draw, int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 64.0, size / 64.0));
            draw(dc);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    private static void Write(string path, IEnumerable<(int Size, BitmapSource Bmp)> frames)
    {
        var list = frames.ToList();
        Ico.Write(path, list);
        Console.WriteLine($"{Path.GetFileName(path)}  {list.Count} 档：{string.Join(", ", list.Select(f => f.Size))}");
    }
}
