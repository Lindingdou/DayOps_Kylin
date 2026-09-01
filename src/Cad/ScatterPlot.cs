using System.Collections.Generic;
using System.Globalization;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 散点图(交会图) —— 把两变量 (x,y) 样本画成点标记, 可叠加拟合直线与离群高亮 + 外框 + 四角刻度 + 轴名。
/// 与 <see cref="CurvePlot"/>(折线)/<see cref="BarChartPlot"/>(类别柱) 并列的第三类图, 供灰分-发热量回归等交会可视化。
/// 拟合线端点纳入 Y 量程, 保证线与散点同框。纯逻辑、可单测。空/零尺寸安全返回。
/// </summary>
public static class ScatterPlot
{
    private static readonly (float r, float g, float b) Dot = (0.55f, 0.75f, 0.95f);
    private static readonly (float r, float g, float b) Hi = (0.95f, 0.4f, 0.35f);   // 离群
    private static readonly (float r, float g, float b) Fit = (0.95f, 0.75f, 0.3f);  // 拟合线
    private static readonly (float r, float g, float b) Axis = (0.8f, 0.8f, 0.8f);

    private static LineEntity Ln(double a, double b, double c, double d, (float r, float g, float b) col) =>
        new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };
    private static TextEntity Tx(double x, double y, double h, string s) =>
        new TextEntity { X = x, Y = y, Height = h, Text = s, Cr = Axis.r, Cg = Axis.g, Cb = Axis.b };

    /// <summary>
    /// 散点: 图区 [x,x+width]×[y,y+height], 字高 textH。pts 为样本; fitLine=(斜率,截距) 叠加 y=intercept+slope·x 直线;
    /// highlight 内的点以红叉突出。X/Y 自动量程(含拟合线端点)。返回可直接 Add 进场景的实体列表。
    /// </summary>
    public static List<SceneEntity> Build(IReadOnlyList<(double x, double y)> pts,
        double x, double y, double width, double height, double textH, string xName, string yName,
        (double slope, double intercept)? fitLine = null,
        System.Collections.Generic.IReadOnlyCollection<(double x, double y)>? highlight = null)
    {
        var list = new List<SceneEntity>();
        if (pts == null || pts.Count == 0 || width <= 0 || height <= 0 || textH <= 0) return list;

        double minX = pts[0].x, maxX = pts[0].x, minY = pts[0].y, maxY = pts[0].y;
        foreach (var p in pts)
        {
            if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
            if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
        }
        // 拟合线端点纳入 Y 量程, 保证同框
        double fy0 = 0, fy1 = 0;
        if (fitLine is { } fl)
        {
            fy0 = fl.intercept + fl.slope * minX; fy1 = fl.intercept + fl.slope * maxX;
            if (fy0 < minY) minY = fy0; if (fy0 > maxY) maxY = fy0;
            if (fy1 < minY) minY = fy1; if (fy1 > maxY) maxY = fy1;
        }
        double rx = maxX - minX, ry = maxY - minY;
        if (rx < 1e-12) rx = 1; if (ry < 1e-12) ry = 1;
        var inv = CultureInfo.InvariantCulture;
        double SX(double v) => x + (v - minX) / rx * width;
        double SY(double v) => y + (v - minY) / ry * height;
        double marker = System.Math.Max(System.Math.Min(width, height) * 0.02, textH * 0.4);

        var hiset = highlight != null ? new HashSet<(double, double)>(highlight) : null;
        foreach (var p in pts)
        {
            bool hot = hiset != null && hiset.Contains((p.x, p.y));
            var col = hot ? Hi : Dot;
            list.Add(new PointEntity { X = SX(p.x), Y = SY(p.y), Size = hot ? marker * 1.6 : marker, Style = 3, Cr = col.r, Cg = col.g, Cb = col.b });
        }
        if (fitLine is not null)                                              // 拟合直线
            list.Add(Ln(SX(minX), SY(fy0), SX(maxX), SY(fy1), Fit));
        // 外框
        list.Add(Ln(x, y, x + width, y, Axis)); list.Add(Ln(x + width, y, x + width, y + height, Axis));
        list.Add(Ln(x + width, y + height, x, y + height, Axis)); list.Add(Ln(x, y + height, x, y, Axis));
        // 四角刻度
        list.Add(Tx(x - textH * 0.3, y - textH * 1.6, textH, minX.ToString("0.##", inv)));
        list.Add(Tx(x + width - textH, y - textH * 1.6, textH, maxX.ToString("0.##", inv)));
        list.Add(Tx(x - textH * 3.2, y - textH * 0.4, textH, minY.ToString("0.##", inv)));
        list.Add(Tx(x - textH * 3.2, y + height - textH * 0.4, textH, maxY.ToString("0.##", inv)));
        // 轴名
        list.Add(Tx(x + width / 2 - textH, y - textH * 3, textH, xName));
        list.Add(Tx(x - textH * 3.2, y + height + textH * 0.4, textH, yName));
        return list;
    }
}
