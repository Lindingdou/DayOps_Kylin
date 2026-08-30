using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 地面点滤波（PointCloudLib 托管切片）—— 每个 XY 网格单元保留最低高程点（最低点≈地面点）。
/// 简化的最小高程地面提取；对 XYZ 点集有效。纯逻辑、可单测。
/// </summary>
public static class GroundFilter
{
    public static List<(double x, double y, double z)> LowestPerCell(
        IReadOnlyList<(double x, double y, double z)> pts, double cell)
    {
        if (cell <= 1e-9)
        {
            var all = new List<(double x, double y, double z)>();
            all.AddRange(pts);
            return all;
        }
        var best = new Dictionary<(long, long), (double x, double y, double z)>();
        foreach (var p in pts)
        {
            var key = ((long)Math.Floor(p.x / cell), (long)Math.Floor(p.y / cell));
            if (!best.TryGetValue(key, out var cur) || p.z < cur.z) best[key] = p;
        }
        var res = new List<(double x, double y, double z)>(best.Count);
        foreach (var v in best.Values) res.Add(v);
        return res;
    }
}
