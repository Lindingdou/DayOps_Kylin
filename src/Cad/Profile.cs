using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 剖面分析 —— 沿剖面线按等距采样，IDW 从地形高程点插值 → (沿线距离, 高程) 剖面序列。纯逻辑、可单测。
/// </summary>
public static class Profile
{
    public static List<(double dist, double z)> Sample(
        IReadOnlyList<(double x, double y)> section,
        IReadOnlyList<(double x, double y, double z)> terrain, int samples)
    {
        var res = new List<(double dist, double z)>();
        if (section.Count < 2 || terrain.Count == 0 || samples < 2) return res;

        var seg = new double[section.Count - 1];
        double total = 0;
        for (int i = 0; i + 1 < section.Count; i++)
        {
            double dx = section[i + 1].x - section[i].x, dy = section[i + 1].y - section[i].y;
            seg[i] = Math.Sqrt(dx * dx + dy * dy); total += seg[i];
        }
        if (total < 1e-9) return res;

        for (int k = 0; k < samples; k++)
        {
            double s = total * k / (samples - 1);
            var (px, py) = PointAt(section, seg, s);
            res.Add((s, Idw(terrain, px, py)));
        }
        return res;
    }

    private static (double x, double y) PointAt(IReadOnlyList<(double x, double y)> section, double[] seg, double s)
    {
        double acc = 0;
        for (int i = 0; i < seg.Length; i++)
        {
            if (s <= acc + seg[i] || i == seg.Length - 1)
            {
                double t = seg[i] < 1e-12 ? 0 : (s - acc) / seg[i];
                t = Math.Clamp(t, 0, 1);
                return (section[i].x + (section[i + 1].x - section[i].x) * t,
                        section[i].y + (section[i + 1].y - section[i].y) * t);
            }
            acc += seg[i];
        }
        return section[^1];
    }

    private static double Idw(IReadOnlyList<(double x, double y, double z)> pts, double px, double py)
    {
        double num = 0, den = 0;
        foreach (var p in pts)
        {
            double d2 = (px - p.x) * (px - p.x) + (py - p.y) * (py - p.y);
            if (d2 < 1e-9) return p.z;
            double w = 1.0 / d2;
            num += w * p.z; den += w;
        }
        return den > 0 ? num / den : 0;
    }
}
