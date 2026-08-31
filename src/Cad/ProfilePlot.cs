using System;
using System.Collections.Generic;
using System.Globalization;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 剖面图框架 —— 为剖面曲线(里程 X, 标高 Z)加 外框 + X轴(里程)刻度/标签 + Y轴(标高)刻度/标签 + 网格 + 轴名。
/// 忠实原版"剖面图"(带里程/标高轴)。剖面曲线本身由调用方绘制, 本类只出框架。纯逻辑、可单测。
/// </summary>
public static class ProfilePlot
{
    private static readonly (float r, float g, float b) Axis = (0.75f, 0.75f, 0.75f);
    private static readonly (float r, float g, float b) Grid = (0.28f, 0.28f, 0.28f);

    private static LineEntity Ln(double a, double b, double c, double d, (float r, float g, float b) col) =>
        new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };
    private static TextEntity Tx(double x, double y, double h, string s) =>
        new TextEntity { X = x, Y = y, Height = h, Text = s, Cr = Axis.r, Cg = Axis.g, Cb = Axis.b };

    /// <summary>
    /// 剖面图框架: 曲线画在 x = originX + 里程, y = originY + (标高 - zMin)。里程域 [0,distMax], 标高域 [zMin,zMax]。
    /// 外框 + 网格 + X轴里程刻度/标签(底, 值=里程) + Y轴标高刻度/标签(左, 值=真实标高) + 轴名"里程"/"标高"。
    /// </summary>
    public static List<SceneEntity> Frame(double originX, double originY, double distMax, double zMin, double zMax, double textH)
    {
        var list = new List<SceneEntity>();
        if (distMax <= 0 || zMax <= zMin || textH <= 0) return list;
        var inv = CultureInfo.InvariantCulture;
        double x0 = originX, x1 = originX + distMax, y0 = originY, y1 = originY + (zMax - zMin);
        // 外框
        list.Add(Ln(x0, y0, x1, y0, Axis)); list.Add(Ln(x1, y0, x1, y1, Axis));
        list.Add(Ln(x1, y1, x0, y1, Axis)); list.Add(Ln(x0, y1, x0, y0, Axis));

        double dStep = MapDecor.NiceLength(distMax / 6);              // ~6 段里程刻度
        for (double d = 0; d <= distMax + 1e-9; d += dStep)
        {
            double xx = originX + d;
            if (d > 1e-9 && d < distMax - 1e-9) list.Add(Ln(xx, y0, xx, y1, Grid));   // 竖网格
            list.Add(Ln(xx, y0, xx, y0 - textH * 0.5, Axis));                          // 刻度
            list.Add(Tx(xx - textH * 0.5, y0 - textH * 1.7, textH, d.ToString("0", inv)));   // 里程值
        }
        double zStep = MapDecor.NiceLength((zMax - zMin) / 5);        // ~5 段标高刻度
        for (double z = Math.Ceiling(zMin / zStep) * zStep; z <= zMax + 1e-9; z += zStep)
        {
            double yy = originY + (z - zMin);
            if (z > zMin + 1e-9 && z < zMax - 1e-9) list.Add(Ln(x0, yy, x1, yy, Grid)); // 横网格
            list.Add(Ln(x0, yy, x0 - textH * 0.4, yy, Axis));                           // 刻度
            list.Add(Tx(x0 - textH * 3.2, yy - textH * 0.4, textH, z.ToString("0", inv)));   // 真实标高
        }
        // 轴名
        list.Add(Tx((x0 + x1) / 2 - textH, y0 - textH * 3.2, textH, "里程"));
        list.Add(Tx(x0 - textH * 3.2, y1 + textH * 0.5, textH, "标高"));
        return list;
    }
}
