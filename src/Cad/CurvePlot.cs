using System.Collections.Generic;
using System.Globalization;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 通用 XY 折线图 —— 把一串 (x,y) 点自动量程归一化到图区矩形, 画折线 + 外框 + 四角刻度 + 轴名。
/// 与 <see cref="HistogramPlot"/> 同风格, 供品位-储量曲线 / 剥采比 / 趋势等「已算未绘」曲线上屏。
/// 纯逻辑、可单测。空/单点/退化范围安全返回。
/// </summary>
public static class CurvePlot
{
    private static readonly (float r, float g, float b) LineCol = (0.95f, 0.6f, 0.25f);
    private static readonly (float r, float g, float b) Axis = (0.8f, 0.8f, 0.8f);

    private static LineEntity Ln(double a, double b, double c, double d, (float r, float g, float b) col) =>
        new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };
    private static TextEntity Tx(double x, double y, double h, string s) =>
        new TextEntity { X = x, Y = y, Height = h, Text = s, Cr = Axis.r, Cg = Axis.g, Cb = Axis.b };

    /// <summary>
    /// XY 折线: 图区 [x,x+width]×[y,y+height], 字高 textH。X 按点集 X 范围、Y 按点集 Y 范围自动量程。
    /// xName/yName 为轴名; color 为折线色(默认橙)。返回可直接 Add 进场景的实体列表。
    /// </summary>
    public static List<SceneEntity> Build(IReadOnlyList<(double x, double y)> pts, double x, double y,
        double width, double height, double textH, string xName, string yName, (float r, float g, float b)? color = null)
    {
        var list = new List<SceneEntity>();
        if (pts == null || pts.Count == 0 || width <= 0 || height <= 0 || textH <= 0) return list;

        double minX = pts[0].x, maxX = pts[0].x, minY = pts[0].y, maxY = pts[0].y;
        foreach (var p in pts)
        {
            if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
        }
        double rx = maxX - minX, ry = maxY - minY;
        if (rx < 1e-12) rx = 1; if (ry < 1e-12) ry = 1;
        var col = color ?? LineCol;
        var inv = CultureInfo.InvariantCulture;
        double SX(double v) => x + (v - minX) / rx * width;
        double SY(double v) => y + (v - minY) / ry * height;

        for (int i = 1; i < pts.Count; i++)                                   // 折线段
            list.Add(Ln(SX(pts[i - 1].x), SY(pts[i - 1].y), SX(pts[i].x), SY(pts[i].y), col));
        // 外框
        list.Add(Ln(x, y, x + width, y, Axis)); list.Add(Ln(x + width, y, x + width, y + height, Axis));
        list.Add(Ln(x + width, y + height, x, y + height, Axis)); list.Add(Ln(x, y + height, x, y, Axis));
        // X 轴 min/max 刻度
        list.Add(Tx(x - textH * 0.3, y - textH * 1.6, textH, minX.ToString("0.##", inv)));
        list.Add(Tx(x + width - textH, y - textH * 1.6, textH, maxX.ToString("0.##", inv)));
        // Y 轴 min/max 刻度
        list.Add(Tx(x - textH * 3.2, y - textH * 0.4, textH, minY.ToString("0.##", inv)));
        list.Add(Tx(x - textH * 3.2, y + height - textH * 0.4, textH, maxY.ToString("0.##", inv)));
        // 轴名
        list.Add(Tx(x + width / 2 - textH, y - textH * 3, textH, xName));
        list.Add(Tx(x - textH * 3.2, y + height + textH * 0.4, textH, yName));
        return list;
    }
}
