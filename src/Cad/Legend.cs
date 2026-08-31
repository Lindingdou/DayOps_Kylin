using System;
using System.Collections.Generic;
using System.Globalization;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 色带图例 —— 为高程/属性着色生成一个色条(渐变) + 值标签(min/中/max)的场景实体组。
/// 忠实原版"图例"(Legend): 色带映射值域可视化。纯逻辑、可单测。色条用 N 条横线堆叠成渐变(线渲染)。
/// </summary>
public static class Legend
{
    /// <summary>
    /// 生成图例实体：竖直色条(x..x+width, y..y+height) + 值标签(右侧, min 底/中/max 顶) + 边框。
    /// colormap 非空; height/width &gt; 0; textH = 标签字高。
    /// </summary>
    public static List<SceneEntity> Build((byte r, byte g, byte b)[] colormap, double vmin, double vmax,
        double x, double y, double width, double height, double textH)
    {
        var list = new List<SceneEntity>();
        if (colormap == null || colormap.Length == 0 || height <= 0 || width <= 0) return list;

        const int strips = 40;                                  // 色条分带数(越多越平滑)
        for (int i = 0; i < strips; i++)
        {
            double t = strips == 1 ? 0 : (double)i / (strips - 1);
            var c = colormap[(int)Math.Round(t * (colormap.Length - 1))];
            double yy = y + t * height;
            list.Add(new LineEntity { X0 = x, Y0 = yy, X1 = x + width, Y1 = yy, Cr = c.r / 255f, Cg = c.g / 255f, Cb = c.b / 255f });
        }
        // 边框(白)
        (float, float, float) fr = (0.9f, 0.9f, 0.9f);
        SceneEntity B(double a, double b, double cc, double d) => new LineEntity { X0 = a, Y0 = b, X1 = cc, Y1 = d, Cr = fr.Item1, Cg = fr.Item2, Cb = fr.Item3 };
        list.Add(B(x, y, x + width, y)); list.Add(B(x + width, y, x + width, y + height));
        list.Add(B(x + width, y + height, x, y + height)); list.Add(B(x, y + height, x, y));

        // 值标签(右侧): 底=min, 中, 顶=max
        double lx = x + width + textH * 0.5;
        string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        list.Add(new TextEntity { X = lx, Y = y - textH * 0.4, Height = textH, Text = F(vmin), Cr = fr.Item1, Cg = fr.Item2, Cb = fr.Item3 });
        list.Add(new TextEntity { X = lx, Y = y + height / 2 - textH * 0.4, Height = textH, Text = F((vmin + vmax) / 2), Cr = fr.Item1, Cg = fr.Item2, Cb = fr.Item3 });
        list.Add(new TextEntity { X = lx, Y = y + height - textH * 0.4, Height = textH, Text = F(vmax), Cr = fr.Item1, Cg = fr.Item2, Cb = fr.Item3 });
        return list;
    }
}
