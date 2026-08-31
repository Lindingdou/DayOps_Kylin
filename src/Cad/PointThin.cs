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

    /// <summary>
    /// 自适应保特征抽稀（原「自适应保特征抽稀」模式）：高曲率点(脊/棱/坎)密留、平坦区疏化。
    /// 曲率度量 = |z − 邻域均 z|(平面≈0)；按曲率降序贪心, 排斥半径 = cell·(1..maxThin) 随平坦度增大。
    /// 网格哈希加速。纯逻辑、可单测。
    /// </summary>
    public static List<(double x, double y, double z)> ThinAdaptive(
        IReadOnlyList<(double x, double y, double z)> pts, double cell, double maxThin = 4.0)
    {
        int n = pts.Count;
        var res = new List<(double x, double y, double z)>();
        if (n == 0) return res;
        if (cell <= 1e-9) { res.AddRange(pts); return res; }

        (long, long, long) Key(double x, double y, double z) => ((long)Math.Floor(x / cell), (long)Math.Floor(y / cell), (long)Math.Floor(z / cell));
        // 网格哈希(全点) 供曲率邻域
        var grid = new Dictionary<(long, long, long), List<int>>();
        for (int i = 0; i < n; i++) { var k = Key(pts[i].x, pts[i].y, pts[i].z); if (!grid.TryGetValue(k, out var l)) grid[k] = l = new List<int>(); l.Add(i); }

        // 逐点曲率 = |z − 3×3×3 邻域均 z|
        var rough = new double[n];
        double maxR = 0;
        for (int i = 0; i < n; i++)
        {
            var (kx, ky, kz) = Key(pts[i].x, pts[i].y, pts[i].z);
            double sz = 0; int cnt = 0;
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
                if (grid.TryGetValue((kx + dx, ky + dy, kz + dz), out var lst))
                    foreach (int j in lst) { sz += pts[j].z; cnt++; }
            double meanZ = cnt > 0 ? sz / cnt : pts[i].z;
            rough[i] = Math.Abs(pts[i].z - meanZ);
            if (rough[i] > maxR) maxR = rough[i];
        }
        if (maxR < 1e-12) return Thin(pts, cell);   // 全平 → 退回体素

        // 曲率降序
        var order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => rough[b].CompareTo(rough[a]));

        // 贪心保留, 排斥半径随平坦度增大(高曲率 r=cell 密, 平坦 r=cell·maxThin 疏)
        var kept = new Dictionary<(long, long, long), List<int>>();
        var keptPts = new List<(double x, double y, double z)>();
        foreach (int i in order)
        {
            double norm = rough[i] / maxR;                 // 0..1
            double r = cell * (1 + (maxThin - 1) * (1 - norm));
            double r2 = r * r;
            var (kx, ky, kz) = Key(pts[i].x, pts[i].y, pts[i].z);
            int span = (int)Math.Ceiling(r / cell);
            bool blocked = false;
            for (long dx = -span; dx <= span && !blocked; dx++)
                for (long dy = -span; dy <= span && !blocked; dy++)
                    for (long dz = -span; dz <= span && !blocked; dz++)
                        if (kept.TryGetValue((kx + dx, ky + dy, kz + dz), out var lst))
                            foreach (int j in lst)
                            {
                                double ex = keptPts[j].x - pts[i].x, ey = keptPts[j].y - pts[i].y, ez = keptPts[j].z - pts[i].z;
                                if (ex * ex + ey * ey + ez * ez < r2) { blocked = true; break; }
                            }
            if (!blocked)
            {
                int idx = keptPts.Count; keptPts.Add(pts[i]);
                if (!kept.TryGetValue((kx, ky, kz), out var kl)) kept[(kx, ky, kz)] = kl = new List<int>();
                kl.Add(idx);
            }
        }
        return keptPts;
    }
}
