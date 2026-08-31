using System;
using System.Collections.Generic;
using System.Globalization;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 直方图 —— 把 <see cref="Statistics.Summary"/> 的等宽桶计数画成竖条柱状图 + 外框 + X轴(值)/Y轴(频数) 刻度/标签/轴名。
/// 忠实原版"直方图/柱状图"。纯逻辑、可单测。桶轮廓用线渲染(顶+两侧, 底由 X 轴)。
/// </summary>
public static class HistogramPlot
{
    private static readonly (float r, float g, float b) Bar = (0.35f, 0.75f, 0.95f);
    private static readonly (float r, float g, float b) Axis = (0.8f, 0.8f, 0.8f);

    private static LineEntity Ln(double a, double b, double c, double d, (float r, float g, float b) col) =>
        new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };
    private static TextEntity Tx(double x, double y, double h, string s) =>
        new TextEntity { X = x, Y = y, Height = h, Text = s, Cr = Axis.r, Cg = Axis.g, Cb = Axis.b };

    /// <summary>直方图: 图区 [x,x+width]×[y,y+height], 字高 textH。桶高 ∝ 计数/最大计数。</summary>
    public static List<SceneEntity> Build(Statistics.Summary s, double x, double y, double width, double height, double textH)
    {
        var list = new List<SceneEntity>();
        var hist = s.Histogram;
        if (hist == null || hist.Length == 0 || width <= 0 || height <= 0 || textH <= 0) return list;
        long maxCount = 0; foreach (var c in hist) if (c > maxCount) maxCount = c;
        if (maxCount == 0) maxCount = 1;
        int n = hist.Length;
        double bw = width / n;
        var inv = CultureInfo.InvariantCulture;

        for (int i = 0; i < n; i++)                                   // 各桶竖条(轮廓)
        {
            double bh = height * hist[i] / maxCount;
            double bx = x + i * bw;
            list.Add(Ln(bx, y, bx, y + bh, Bar));                     // 左
            list.Add(Ln(bx, y + bh, bx + bw, y + bh, Bar));           // 顶
            list.Add(Ln(bx + bw, y + bh, bx + bw, y, Bar));           // 右
        }
        // 外框
        list.Add(Ln(x, y, x + width, y, Axis)); list.Add(Ln(x + width, y, x + width, y + height, Axis));
        list.Add(Ln(x + width, y + height, x, y + height, Axis)); list.Add(Ln(x, y + height, x, y, Axis));
        // X 轴值标签: min / max
        list.Add(Tx(x - textH * 0.3, y - textH * 1.6, textH, s.Min.ToString("0.#", inv)));
        list.Add(Tx(x + width - textH, y - textH * 1.6, textH, s.Max.ToString("0.#", inv)));
        // Y 轴频数标签: 0 / maxCount
        list.Add(Tx(x - textH * 2.5, y - textH * 0.4, textH, "0"));
        list.Add(Tx(x - textH * 2.5, y + height - textH * 0.4, textH, maxCount.ToString(inv)));
        // 轴名
        list.Add(Tx(x + width / 2 - textH, y - textH * 3, textH, "值"));
        list.Add(Tx(x - textH * 2.5, y + height + textH * 0.4, textH, "频数"));
        return list;
    }
}
