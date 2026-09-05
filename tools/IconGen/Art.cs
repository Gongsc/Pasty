using System.Windows;
using System.Windows.Media;

namespace Pasty.IconGen;

/// <summary>
/// 图标几何。坐标系是 64×64 单位网格，与 icon-preview.html 里的 SVG 完全一致。
/// 所有明暗都是独立平涂色块，没有一处渐变：板身靠底部露出的深色带做厚度，
/// 纸叠靠逐层加深 + 偏移的实色投影做层次，金属夹靠三段钢色做柱面。
/// </summary>
internal static class Art
{
    private static readonly Brush BoardTop = B("#3C90DC");   // 板面顶部亮带
    private static readonly Brush BoardFace = B("#0067C0");  // 板面主色，沿用应用强调色
    private static readonly Brush BoardBottom = B("#00559E"); // 板面底部暗带 / 纸叠投影
    private static readonly Brush BoardEdge = B("#00427C");  // 板身厚度 / 品牌 V
    private static readonly Brush SteelTop = B("#C6D0DA");
    private static readonly Brush SteelBody = B("#9DAAB7");
    private static readonly Brush SteelDark = B("#6F7C8A");
    private static readonly Brush Jaw = B("#7E8B99");
    private static readonly Brush JawShadow = B("#DCE3EA");  // 夹口落在白纸上的投影
    private static readonly Brush Rivet = B("#5C6874");
    private static readonly Brush RivetHi = B("#93A0AD");
    private static readonly Brush Paper1 = B("#FFFFFF");
    private static readonly Brush Paper2 = B("#CFDBE6");
    private static readonly Brush Paper3 = B("#A9BDD1");
    private static readonly Brush ClampFlat2 = B("#A8B4C0"); // L2 单色夹子
    private static readonly Brush ClampFlat3 = B("#8A97A5"); // L3 单色夹子，压深一点保住 16px 边缘

    public static readonly Color TrayLight = C("#0067C0");   // 浅色任务栏
    public static readonly Color TrayDark = C("#4CB2F5");    // 深色任务栏

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;
    private static Brush B(string hex)
    {
        var b = new SolidColorBrush(C(hex));
        b.Freeze();
        return b;
    }

    private static void Rr(DrawingContext dc, Brush b, double x, double y, double w, double h, double r)
        => dc.DrawRoundedRectangle(b, null, new Rect(x, y, w, h), r, r);

    private static Geometry Vee(double x1, double yTop, double xMid, double yBottom, double x2)
    {
        var fig = new PathFigure { StartPoint = new Point(x1, yTop), IsClosed = false, IsFilled = false };
        fig.Segments.Add(new LineSegment(new Point(xMid, yBottom), true));
        fig.Segments.Add(new LineSegment(new Point(x2, yTop), true));
        var pg = new PathGeometry();
        pg.Figures.Add(fig);
        return pg;
    }

    private static Pen VeePen(Brush b, double w)
        => new(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    /// <summary>应用图标。lod 1=全细节，2=去掉铆钉/纸叠/明暗带，3=平涂剪影。</summary>
    public static void App(DrawingContext dc, int lod)
    {
        switch (lod)
        {
            case 1: App1(dc); break;
            case 2: App2(dc); break;
            default: App3(dc); break;
        }
    }

    /// <summary>L1：48px 以上。铆钉、三层纸叠、板身厚度、钢夹柱面全部在。</summary>
    private static void App1(DrawingContext dc)
    {
        // 板身：先铺满高 51 的深色，再把高 48.5 的板面盖上去，底部露出的 2.5 就是厚度
        Rr(dc, BoardEdge, 9, 7, 46, 51, 5.5);
        dc.PushClip(new RectangleGeometry(new Rect(9, 7, 46, 48.5), 5.5, 5.5));
        dc.DrawRectangle(BoardFace, null, new Rect(9, 7, 46, 48.5));
        dc.DrawRectangle(BoardTop, null, new Rect(9, 7, 46, 2.8));
        dc.DrawRectangle(BoardBottom, null, new Rect(9, 50.5, 46, 5));
        dc.Pop();

        // 纸叠：投影 → 第三张 → 第二张 → 最上面的白纸，每层向左上偏移 1
        Rr(dc, BoardBottom, 18.5, 15.5, 31, 35, 1.8);
        Rr(dc, Paper3, 17, 14, 31, 35, 1.8);
        Rr(dc, Paper2, 16, 13, 31, 35, 1.8);
        Rr(dc, Paper1, 15, 12, 31, 35, 1.8);

        dc.DrawGeometry(null, VeePen(BoardEdge, 6), Vee(23, 27.5, 30.5, 39, 38));

        // 金属夹：纸上投影 → 夹口 → 钢柱（三段平涂）→ 铆钉
        Rr(dc, JawShadow, 25.5, 17.2, 13, 1.9, 0.95);
        Rr(dc, Jaw, 25, 11, 14, 6.4, 1.6);
        dc.PushClip(new RectangleGeometry(new Rect(23, 4.5, 18, 9.5), 2.6, 2.6));
        dc.DrawRectangle(SteelBody, null, new Rect(23, 4.5, 18, 9.5));
        dc.DrawRectangle(SteelTop, null, new Rect(23, 4.5, 18, 2.8));
        dc.DrawRectangle(SteelDark, null, new Rect(23, 11.8, 18, 2.2));
        dc.Pop();
        dc.DrawEllipse(Rivet, null, new Point(32, 8.6), 1.75, 1.75);
        dc.DrawEllipse(RivetHi, null, new Point(32, 8.6), 0.7, 0.7);
    }

    /// <summary>L2：40/32/24px。只留一张白纸和板身厚度，铆钉与明暗带在这个尺寸只会变成脏点。</summary>
    private static void App2(DrawingContext dc)
    {
        Rr(dc, BoardEdge, 9, 7, 46, 51, 5.5);
        Rr(dc, BoardFace, 9, 7, 46, 48.5, 5.5);
        Rr(dc, Paper1, 15, 12, 31, 35, 2);
        dc.DrawGeometry(null, VeePen(BoardEdge, 6.6), Vee(23, 27.5, 30.5, 39, 38));
        Rr(dc, Jaw, 25, 10.5, 14, 6.6, 1.6);
        Rr(dc, ClampFlat2, 23, 4.5, 18, 9.5, 2.6);
    }

    /// <summary>
    /// L3：20/16px。纯剪影 + 挖空的白 V。
    /// 坐标全部取 4 的倍数：64 网格缩到 16px 时是 ÷4，缩到 32px 时是 ÷2，
    /// 这样板面与 V 的边缘正好落在整像素上，不会糊成两排半透明灰。
    /// </summary>
    private static void App3(DrawingContext dc)
    {
        Rr(dc, BoardFace, 8, 8, 48, 48, 5);
        Rr(dc, ClampFlat3, 24, 4, 16, 8, 2.6);
        dc.DrawGeometry(null, VeePen(Paper1, 8), Vee(22, 26, 32, 40, 42));
    }

    /// <summary>
    /// 托盘图标：与 L3 同形，但整体单色，且 V 是**挖空**而不是填白——
    /// 任务栏背景必须从 V 里透出来，填白在深色任务栏上会变成一道刺眼的亮条。
    /// </summary>
    public static void Tray(DrawingContext dc, Color color)
    {
        var body = Geometry.Combine(
            new RectangleGeometry(new Rect(8, 8, 48, 48), 5, 5),
            new RectangleGeometry(new Rect(24, 4, 16, 8), 2.6, 2.6),
            GeometryCombineMode.Union, null);
        var vee = Vee(22, 26, 32, 40, 42).GetWidenedPathGeometry(VeePen(Paper1, 8));
        var shape = Geometry.Combine(body, vee, GeometryCombineMode.Exclude, null);

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        dc.DrawGeometry(brush, null, shape);
    }
}
