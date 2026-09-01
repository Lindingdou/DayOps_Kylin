using System.Collections.Generic;
using System.Globalization;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 箱线图 —— 每类别一个箱体(Q1–Q3 矩形 + 中位横线 + Min/Max 须与端帽), 类别沿 X、值沿 Y(全局共享量程)。
/// 忠实原 CoalQualityStatsWindow「每煤层箱线」。与 CurvePlot/BarChartPlot/ScatterPlot 并列第四类图。
/// 纯逻辑、可单测。空/零尺寸/退化量程安全返回。
/// </summary>
public static class BoxPlot
{
    private static readonly (float r, float g, float b) Box = (0.4f, 0.7f, 0.9f);
    private static readonly (float r, float g, float b) Med = (0.95f, 0.6f, 0.25f);
    private static readonly (float r, float g, float b) Axis = (0.8f, 0.8f, 0.8f);

    private static LineEntity Ln(double a, double b, double c, double d, (float r, float g, float b) col) =>
        new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };
    private static TextEntity Tx(double x, double y, double h, string s) =>
        new TextEntity { X = x, Y = y, Height = h, Text = s, Cr = Axis.r, Cg = Axis.g, Cb = Axis.b };

    /// <summary>
    /// 箱线: 图区 [x,x+width]×[y,y+height], 字高 textH。boxes 每项 (类别名, min, q1, median, q3, max)。
    /// Y 按全部箱体的 min..max 共享量程。返回可 Add 进场景的实体列表。
    /// </summary>
    public static List<SceneEntity> Build(IReadOnlyList<(string label, double min, double q1, double median, double q3, double max)> boxes,
        double x, double y, double width, double height, double textH, string valueName)
    {
        var list = new List<SceneEntity>();
        if (boxes == null || boxes.Count == 0 || width <= 0 || height <= 0 || textH <= 0) return list;

        double gMin = double.MaxValue, gMax = double.MinValue;
        foreach (var b in boxes) { if (b.min < gMin) gMin = b.min; if (b.max > gMax) gMax = b.max; }
        double ry = gMax - gMin; if (ry < 1e-12) ry = 1;
        var inv = CultureInfo.InvariantCulture;
        double SY(double v) => y + (v - gMin) / ry * height;
        int n = boxes.Count;
        double slot = width / n, bw = slot * 0.6, half = bw / 2;

        for (int i = 0; i < n; i++)
        {
            var b = boxes[i];
            double cx = x + (i + 0.5) * slot;
            double yMin = SY(b.min), yQ1 = SY(b.q1), yMed = SY(b.median), yQ3 = SY(b.q3), yMax = SY(b.max);
            list.Add(Ln(cx, yMin, cx, yMax, Axis));                         // 须(min→max)
            list.Add(Ln(cx - half * 0.5, yMin, cx + half * 0.5, yMin, Axis));   // 下端帽
            list.Add(Ln(cx - half * 0.5, yMax, cx + half * 0.5, yMax, Axis));   // 上端帽
            // 箱(Q1..Q3 矩形四边)
            list.Add(Ln(cx - half, yQ1, cx + half, yQ1, Box));
            list.Add(Ln(cx + half, yQ1, cx + half, yQ3, Box));
            list.Add(Ln(cx + half, yQ3, cx - half, yQ3, Box));
            list.Add(Ln(cx - half, yQ3, cx - half, yQ1, Box));
            list.Add(Ln(cx - half, yMed, cx + half, yMed, Med));            // 中位横线
            list.Add(Tx(cx - half, y - textH * 1.4, textH, b.label));      // 类别名
        }
        // 外框(左+底) + 值轴 min/max + 轴名
        list.Add(Ln(x, y, x + width, y, Axis)); list.Add(Ln(x, y, x, y + height, Axis));
        list.Add(Tx(x - textH * 3.2, y - textH * 0.4, textH, gMin.ToString("0.##", inv)));
        list.Add(Tx(x - textH * 3.2, y + height - textH * 0.4, textH, gMax.ToString("0.##", inv)));
        list.Add(Tx(x - textH * 3.2, y + height + textH * 0.4, textH, valueName));
        return list;
    }
}
