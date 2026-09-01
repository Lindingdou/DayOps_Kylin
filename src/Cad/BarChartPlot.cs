using System.Collections.Generic;
using System.Globalization;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 类别柱状图 —— 把一串 (类别名, 数值) 画成等宽竖条 + 外框 + 类别标签(条下)/数值标签(条顶) + 值轴刻度。
/// 与 <see cref="HistogramPlot"/>(等宽桶) 互补: 此为**任意类别**分布(煤类占比 / 设备分类 / 审核分类等),
/// 忠实原版「煤类饼 / 分类柱」的量化上屏。纯逻辑、可单测。空/零尺寸安全返回。
/// </summary>
public static class BarChartPlot
{
    private static readonly (float r, float g, float b) Bar = (0.45f, 0.8f, 0.55f);
    private static readonly (float r, float g, float b) Axis = (0.8f, 0.8f, 0.8f);

    private static LineEntity Ln(double a, double b, double c, double d, (float r, float g, float b) col) =>
        new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };
    private static TextEntity Tx(double x, double y, double h, string s) =>
        new TextEntity { X = x, Y = y, Height = h, Text = s, Cr = Axis.r, Cg = Axis.g, Cb = Axis.b };

    /// <summary>
    /// 类别柱: 图区 [x,x+width]×[y,y+height], 字高 textH。条高 ∝ 值/最大值; 每条下标类别名、顶标数值。
    /// valueName 为值轴名。返回可直接 Add 进场景的实体列表。
    /// </summary>
    public static List<SceneEntity> Build(IReadOnlyList<(string label, double value)> items,
        double x, double y, double width, double height, double textH, string valueName)
    {
        var list = new List<SceneEntity>();
        if (items == null || items.Count == 0 || width <= 0 || height <= 0 || textH <= 0) return list;

        double maxV = 0; foreach (var it in items) if (it.value > maxV) maxV = it.value;
        if (maxV <= 0) maxV = 1;
        int n = items.Count;
        double slot = width / n, gap = slot * 0.15, bw = slot - 2 * gap;
        var inv = CultureInfo.InvariantCulture;

        for (int i = 0; i < n; i++)
        {
            double bh = height * items[i].value / maxV;
            double bx = x + i * slot + gap;
            list.Add(Ln(bx, y, bx, y + bh, Bar));                    // 左
            list.Add(Ln(bx, y + bh, bx + bw, y + bh, Bar));          // 顶
            list.Add(Ln(bx + bw, y + bh, bx + bw, y, Bar));          // 右
            list.Add(Ln(bx, y, bx + bw, y, Bar));                    // 底
            list.Add(Tx(bx, y - textH * 1.4, textH, items[i].label));                        // 类别名(条下)
            list.Add(Tx(bx, y + bh + textH * 0.2, textH, items[i].value.ToString("0.#", inv))); // 数值(条顶)
        }
        // 外框(左+底轴)
        list.Add(Ln(x, y, x + width, y, Axis));
        list.Add(Ln(x, y, x, y + height, Axis));
        // 值轴 0/max + 轴名
        list.Add(Tx(x - textH * 2.8, y - textH * 0.4, textH, "0"));
        list.Add(Tx(x - textH * 2.8, y + height - textH * 0.4, textH, maxV.ToString("0.#", inv)));
        list.Add(Tx(x - textH * 2.8, y + height + textH * 0.4, textH, valueName));
        return list;
    }
}
