using System;
using System.Collections.Generic;
using System.Globalization;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 制图装饰 —— 指北针 / 比例尺 (场景实体组, 供出图)。忠实原版"指北针"/"比例尺"。纯逻辑、可单测。
/// </summary>
public static class MapDecor
{
    private static readonly (float r, float g, float b) Col = (0.9f, 0.9f, 0.9f);
    private static LineEntity L(double a, double b, double c, double d) =>
        new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = Col.r, Cg = Col.g, Cb = Col.b };

    /// <summary>指北针: 竖直箭头(指上=北, Y+ 为北) + "N" 标注。锚点 (x,y) 为箭杆底。</summary>
    public static List<SceneEntity> NorthArrow(double x, double y, double size)
    {
        var list = new List<SceneEntity>();
        if (size <= 0) return list;
        list.Add(L(x, y, x, y + size));                              // 箭杆
        list.Add(L(x, y + size, x - size * 0.25, y + size * 0.7));   // 箭头左翼
        list.Add(L(x, y + size, x + size * 0.25, y + size * 0.7));   // 箭头右翼
        list.Add(new TextEntity { X = x - size * 0.18, Y = y + size * 1.15, Height = size * 0.35, Text = "N", Cr = Col.r, Cg = Col.g, Cb = Col.b });
        return list;
    }

    /// <summary>比例尺: 水平条(worldLen 世界长) + 两端+中刻度 + "0"/长度 标签。锚点 (x,y) 为条左端。</summary>
    public static List<SceneEntity> ScaleBar(double x, double y, double worldLen, double textH)
    {
        var list = new List<SceneEntity>();
        if (worldLen <= 0 || textH <= 0) return list;
        double tk = textH * 0.6;
        list.Add(L(x, y, x + worldLen, y));                          // 主条
        list.Add(L(x, y - tk, x, y + tk));                           // 左端刻度
        list.Add(L(x + worldLen, y - tk, x + worldLen, y + tk));     // 右端刻度
        list.Add(L(x + worldLen / 2, y - tk * 0.6, x + worldLen / 2, y + tk * 0.6));   // 中刻度
        list.Add(new TextEntity { X = x - textH * 0.3, Y = y + tk * 1.2, Height = textH, Text = "0", Cr = Col.r, Cg = Col.g, Cb = Col.b });
        list.Add(new TextEntity { X = x + worldLen - textH * 0.6, Y = y + tk * 1.2, Height = textH, Text = worldLen.ToString("0.#", CultureInfo.InvariantCulture), Cr = Col.r, Cg = Col.g, Cb = Col.b });
        return list;
    }

    /// <summary>
    /// 标题栏: 外框 + 顶行标题(大字) + 中/底行标签(比例/图号 · 制图/日期)。锚点 (x,y)=左下角。
    /// 忠实原版"标题栏"。scale 为比例文字(如 "1:1000"), 空则留占位。
    /// </summary>
    public static List<SceneEntity> TitleBlock(double x, double y, double w, double h, double textH, string title, string scale)
    {
        var list = new List<SceneEntity>();
        if (w <= 0 || h <= 0 || textH <= 0) return list;
        // 外框
        list.Add(L(x, y, x + w, y)); list.Add(L(x + w, y, x + w, y + h));
        list.Add(L(x + w, y + h, x, y + h)); list.Add(L(x, y + h, x, y));
        double r = h / 3;                                            // 3 行等高
        list.Add(L(x, y + r, x + w, y + r));                         // 分隔线(底↔中)
        list.Add(L(x, y + 2 * r, x + w, y + 2 * r));                 // 分隔线(中↔顶)
        list.Add(L(x + w / 2, y, x + w / 2, y + 2 * r));             // 下两行竖分隔
        // 顶行: 标题(大字)
        list.Add(new TextEntity { X = x + w * 0.04, Y = y + 2 * r + r * 0.3, Height = textH * 1.4, Text = string.IsNullOrEmpty(title) ? "标题" : title, Cr = Col.r, Cg = Col.g, Cb = Col.b });
        // 中行: 比例 | 图号
        list.Add(new TextEntity { X = x + w * 0.04, Y = y + r + r * 0.3, Height = textH, Text = "比例 " + (string.IsNullOrEmpty(scale) ? "" : scale), Cr = Col.r, Cg = Col.g, Cb = Col.b });
        list.Add(new TextEntity { X = x + w * 0.54, Y = y + r + r * 0.3, Height = textH, Text = "图号", Cr = Col.r, Cg = Col.g, Cb = Col.b });
        // 底行: 制图 | 日期
        list.Add(new TextEntity { X = x + w * 0.04, Y = y + r * 0.3, Height = textH, Text = "制图", Cr = Col.r, Cg = Col.g, Cb = Col.b });
        list.Add(new TextEntity { X = x + w * 0.54, Y = y + r * 0.3, Height = textH, Text = "日期", Cr = Col.r, Cg = Col.g, Cb = Col.b });
        return list;
    }

    /// <summary>取"整"长度: ≈ target 的 1/2/5 × 10^n(比例尺常用刻度)。</summary>
    public static double NiceLength(double target)
    {
        if (target <= 0) return 1;
        double exp = Math.Floor(Math.Log10(target));
        double baseP = Math.Pow(10, exp);
        double f = target / baseP;                                   // 1..10
        double nice = f >= 5 ? 5 : f >= 2 ? 2 : 1;
        return nice * baseP;
    }
}
