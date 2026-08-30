using System;
using System.Collections.Generic;
using System.Globalization;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 线性标注（尺寸）—— 两点 → 尺寸线 + 端点刻度 + 距离文字（复用单笔画字体）。纯逻辑、可单测。
/// </summary>
public static class DimTools
{
    public static List<SceneEntity> Build(double x1, double y1, double x2, double y2, double h)
    {
        var list = new List<SceneEntity>();
        (float r, float g, float b) col = (0.95f, 0.85f, 0.30f);
        SceneEntity L(double a, double b, double c, double d) => new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };

        list.Add(L(x1, y1, x2, y2));                       // 尺寸线
        double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return list;
        double nx = -dy / len, ny = dx / len;              // 单位法线
        double tk = h * 0.4;
        list.Add(L(x1 - nx * tk, y1 - ny * tk, x1 + nx * tk, y1 + ny * tk));   // 端刻度
        list.Add(L(x2 - nx * tk, y2 - ny * tk, x2 + nx * tk, y2 + ny * tk));

        string s = len.ToString("0.##", CultureInfo.InvariantCulture);
        double mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
        double ox = nx * h * 0.6, oy = ny * h * 0.6;        // 文字偏到尺寸线上方
        double tw = s.Length * h * 0.8;                     // 粗略文字宽 → 居中
        list.Add(new TextEntity { X = mx + ox - tw / 2, Y = my + oy, Height = h, Text = s, Cr = col.r, Cg = col.g, Cb = col.b });
        return list;
    }

    /// <summary>半径标注(DIMRADIAL)：圆心→(dirx,diry) 方向的圆周点，径向线 + 箭头 + "R值"文字。</summary>
    public static List<SceneEntity> BuildRadial(double cx, double cy, double radius, double dirx, double diry, double h)
    {
        var list = new List<SceneEntity>();
        (float r, float g, float b) col = (0.95f, 0.85f, 0.30f);
        SceneEntity L(double a, double b, double c, double d) => new LineEntity { X0 = a, Y0 = b, X1 = c, Y1 = d, Cr = col.r, Cg = col.g, Cb = col.b };

        double dl = Math.Sqrt(dirx * dirx + diry * diry);
        double ux = dl < 1e-9 ? 1 : dirx / dl, uy = dl < 1e-9 ? 0 : diry / dl;   // 单位方向
        double ex = cx + ux * radius, ey = cy + uy * radius;                       // 圆周端点
        list.Add(L(cx, cy, ex, ey));                                              // 径向线

        // 圆周端箭头：沿方向回缩，左右各张开
        double ah = h * 0.5, aw = h * 0.25;
        double bx = ex - ux * ah, by = ey - uy * ah;   // 箭底
        double px = -uy, py = ux;                       // 垂向
        list.Add(L(ex, ey, bx + px * aw, by + py * aw));
        list.Add(L(ex, ey, bx - px * aw, by - py * aw));

        string s = "R" + radius.ToString("0.##", CultureInfo.InvariantCulture);
        list.Add(new TextEntity { X = ex + ux * h * 0.3, Y = ey + uy * h * 0.3, Height = h, Text = s, Cr = col.r, Cg = col.g, Cb = col.b });
        return list;
    }
}
