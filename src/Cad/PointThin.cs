using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点云抽稀（PointCloudLib 托管切片）—— 体素网格抽稀：每个 cell 尺寸的立方体格保留首个点。
/// 对 XYZ 点集有效（LAS 二进制解析需内核，记录待做）。纯逻辑、可单测。
/// </summary>
public static class PointThin
{
    public static List<(double x, double y, double z)> Thin(
        IReadOnlyList<(double x, double y, double z)> pts, double cell)
    {
        var res = new List<(double x, double y, double z)>();
        if (cell <= 1e-9) { res.AddRange(pts); return res; }
        var seen = new HashSet<(long, long, long)>();
        foreach (var p in pts)
        {
            var key = ((long)Math.Floor(p.x / cell), (long)Math.Floor(p.y / cell), (long)Math.Floor(p.z / cell));
            if (seen.Add(key)) res.Add(p);
        }
        return res;
    }
}
