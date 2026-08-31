using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 境界台阶线生成（MineAssLib「批量台阶扩帮」几何核）—— 闭合境界按定距逐圈内偏移，
/// 生成各台阶顶线。miter 偏移 + 面积递减自交保护。纯逻辑、可单测。
/// 说明：真实台阶距 = W + H/tanα（帮参数），此处以定距近似；帮参数对话框待接（记录）。
/// 凹境界深偏移可能自交，面积不再递减即停（不产出病态环）。
/// </summary>
public static class BenchLines
{
    /// <summary>真实台阶距 = 平盘宽 W + 台阶高 H / tan(坡面角 α)（水平投影距）。α∈(0,90)°。退化(α≤0/≥90)回落 W。</summary>
    public static double BenchDistance(double benchWidthM, double benchHeightM, double slopeAngleDeg)
    {
        if (slopeAngleDeg <= 0 || slopeAngleDeg >= 90) return Math.Max(benchWidthM, 0);
        double t = Math.Tan(slopeAngleDeg * Math.PI / 180.0);
        return Math.Max(benchWidthM, 0) + (t > 1e-9 ? Math.Max(benchHeightM, 0) / t : 0);
    }

    /// <summary>闭合多边形按定距 d 内偏移一圈；退化(平行/太小)返回 null。</summary>
    public static List<(double x, double y)>? OffsetClosed(IReadOnlyList<(double x, double y)> pts, double d)
    {
        int n = pts.Count;
        if (n < 3 || d <= 0) return null;
        double area = SignedArea(pts);
        if (Math.Abs(area) < 1e-12) return null;
        double sgn = area > 0 ? 1.0 : -1.0;                 // CCW→+1，内法向 = sgn·(-dy,dx)

        var off = new (double ax, double ay, double bx, double by)[n];
        for (int i = 0; i < n; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % n];
            double dx = b.x - a.x, dy = b.y - a.y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-12) return null;
            double nx = sgn * (-dy) / len, ny = sgn * dx / len;
            off[i] = (a.x + nx * d, a.y + ny * d, b.x + nx * d, b.y + ny * d);
        }

        var res = new List<(double x, double y)>(n);
        for (int i = 0; i < n; i++)
        {
            var e0 = off[(i - 1 + n) % n]; var e1 = off[i];
            var ip = LineMath.IntersectInfinite(e0.ax, e0.ay, e0.bx, e0.by, e1.ax, e1.ay, e1.bx, e1.by);
            if (ip == null) return null;                    // 相邻边平行 → 退化
            res.Add(ip.Value);
        }
        return res;
    }

    /// <summary>从境界逐圈内偏移，生成至多 count 圈台阶线；面积不再递减即停。</summary>
    public static List<List<(double x, double y)>> Generate(IReadOnlyList<(double x, double y)> boundary, double d, int count)
    {
        var rings = new List<List<(double x, double y)>>();
        var cur = new List<(double x, double y)>(boundary);
        for (int k = 0; k < count; k++)
        {
            var next = OffsetClosed(cur, d);
            if (next == null) break;
            double an = Math.Abs(SignedArea(next));
            if (an < 1e-9) break;
            if (Math.Abs(SignedArea(cur)) <= an + 1e-12) break;   // 面积未递减 → 自交/退化，停
            rings.Add(next);
            cur = next;
        }
        return rings;
    }

    public static double SignedArea(IReadOnlyList<(double x, double y)> p)
    {
        double s = 0; int n = p.Count;
        for (int i = 0; i < n; i++) { var a = p[i]; var b = p[(i + 1) % n]; s += a.x * b.y - b.x * a.y; }
        return s * 0.5;
    }
}
