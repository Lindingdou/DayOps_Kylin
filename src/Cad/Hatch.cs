using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 平行填充（排土条带 / 简易剖面线）—— 闭合多边形内按间距/角度生成平行线段。
/// 扫描线法：按角度旋到水平扫描求交，取多边形内区间，旋回。纯逻辑、可单测。
/// </summary>
public static class Hatch
{
    public static List<(double x0, double y0, double x1, double y1)> ParallelFill(
        IReadOnlyList<(double x, double y)> poly, double spacing, double angleDeg)
    {
        var segs = new List<(double, double, double, double)>();
        if (poly.Count < 3 || spacing <= 1e-9) return segs;

        double th = -angleDeg * Math.PI / 180, c = Math.Cos(th), s = Math.Sin(th);
        var rp = new List<(double x, double y)>(poly.Count);
        foreach (var p in poly) rp.Add((p.x * c - p.y * s, p.x * s + p.y * c));   // 旋到扫描系

        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var p in rp) { if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y; }

        double bc = Math.Cos(angleDeg * Math.PI / 180), bs = Math.Sin(angleDeg * Math.PI / 180);   // 旋回
        double y0 = Math.Ceiling(minY / spacing) * spacing;
        for (double y = y0; y <= maxY; y += spacing)
        {
            var xs = new List<double>();
            for (int i = 0, j = rp.Count - 1; i < rp.Count; j = i++)
            {
                var a = rp[i]; var b = rp[j];
                if ((a.y <= y && b.y > y) || (b.y <= y && a.y > y))   // 半开区间避免顶点重复
                    xs.Add(a.x + (y - a.y) / (b.y - a.y) * (b.x - a.x));
            }
            xs.Sort();
            for (int k = 0; k + 1 < xs.Count; k += 2)
            {
                // 旋回原坐标系
                double rx0 = xs[k] * bc - y * bs, ry0 = xs[k] * bs + y * bc;
                double rx1 = xs[k + 1] * bc - y * bs, ry1 = xs[k + 1] * bs + y * bc;
                segs.Add((rx0, ry0, rx1, ry1));
            }
        }
        return segs;
    }
}
