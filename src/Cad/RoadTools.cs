using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 道路工具（RoadLib 托管切片）—— 由两条路边多段线提取中心线：
/// 对边 A 每个顶点，取其到边 B 的最近点，二者中点连成中心线。纯逻辑、可单测。
/// </summary>
public static class RoadTools
{
    /// <summary>点到多段线的最近点（各段投影取最近）。</summary>
    public static (double x, double y) NearestOnPolyline(double px, double py, IReadOnlyList<(double x, double y)> poly)
    {
        (double x, double y) best = poly.Count > 0 ? poly[0] : (px, py);
        double bestD = double.MaxValue;
        for (int i = 0; i + 1 < poly.Count; i++)
        {
            var a = poly[i]; var b = poly[i + 1];
            double dx = b.x - a.x, dy = b.y - a.y, len2 = dx * dx + dy * dy;
            double t = len2 < 1e-12 ? 0 : Math.Clamp(((px - a.x) * dx + (py - a.y) * dy) / len2, 0, 1);
            double cx = a.x + t * dx, cy = a.y + t * dy;
            double d = (px - cx) * (px - cx) + (py - cy) * (py - cy);
            if (d < bestD) { bestD = d; best = (cx, cy); }
        }
        return best;
    }

    /// <summary>两路边 → 中心线：边 A 每顶点与其在边 B 上最近点的中点。</summary>
    public static List<(double x, double y)> Centerline(
        IReadOnlyList<(double x, double y)> edgeA, IReadOnlyList<(double x, double y)> edgeB)
    {
        var line = new List<(double x, double y)>();
        if (edgeA.Count == 0 || edgeB.Count < 2) return line;
        foreach (var v in edgeA)
        {
            var np = NearestOnPolyline(v.x, v.y, edgeB);
            line.Add(((v.x + np.x) / 2, (v.y + np.y) / 2));
        }
        return line;
    }
}
