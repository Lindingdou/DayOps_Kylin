using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// LwPolyline 凸度(bulge)→圆弧插值。bulge = tan(圆心角/4)，正=逆时针(向 P1→P2 左侧鼓)。
/// 由弦中点沿左法向抬矢高 s=bulge·弦长/2 得弧顶点，再用三点外接圆 + 过中点扫向生成插值点。
/// 纯几何、可单测。用于导入含弧段的多段线时把弧段还原成弧(而非直线弦)。
/// </summary>
public static class BulgeArc
{
    /// <summary>P1、P2 之间弧上的插值点(不含两端点)；bulge≈0 或退化返回空。</summary>
    public static List<(double x, double y)> Interior(double x1, double y1, double x2, double y2, double bulge, int seg = 16)
    {
        var res = new List<(double x, double y)>();
        if (Math.Abs(bulge) < 1e-9) return res;                 // 直线段
        double dx = x2 - x1, dy = y2 - y1, chord = Math.Sqrt(dx * dx + dy * dy);
        if (chord < 1e-12) return res;
        double mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
        double nx = -dy / chord, ny = dx / chord;               // 左法向
        double s = bulge * chord / 2;                           // 矢高(带符号)
        double ax = mx + nx * s, ay = my + ny * s;              // 弧顶点
        var cc = ArcMath.Circumcircle(x1, y1, ax, ay, x2, y2);
        if (cc == null) return res;
        var (cx, cy, r) = cc.Value;
        double a1 = Math.Atan2(y1 - cy, x1 - cx);
        double am = Math.Atan2(ay - cy, ax - cx);
        double a3 = Math.Atan2(y2 - cy, x2 - cx);
        double sweep = Norm(a3 - a1), mid = Norm(am - a1);
        double total = mid <= sweep ? sweep : sweep - 2 * Math.PI;   // 过中点的扫向
        int n = Math.Max(2, seg);
        for (int i = 1; i < n; i++)
        {
            double t = a1 + total * i / n;
            res.Add((cx + r * Math.Cos(t), cy + r * Math.Sin(t)));
        }
        return res;
    }

    private static double Norm(double a) { while (a < 0) a += 2 * Math.PI; while (a >= 2 * Math.PI) a -= 2 * Math.PI; return a; }
}
