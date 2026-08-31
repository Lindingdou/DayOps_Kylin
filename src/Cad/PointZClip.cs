using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 高程截断（点云 Z 裁剪）——保留 Z 落在 [低百分位, 高百分位] 内的点, 剔除异常高/低程点。
/// 原走内核(ZClip, 用户给 Z 上下界); 此以托管重算(裁剪算法平凡, 波段以数据百分位默认, 同 SOR/ROR 脉络)。
/// 纯逻辑、可单测。
/// </summary>
public static class PointZClip
{
    /// <summary>按百分位 [lowPct, highPct](0..1) 定 Z 上下界, 保留界内点。返回 (保留点, zLo, zHi, 移除数)。</summary>
    public static (List<(double x, double y, double z)> kept, double zLo, double zHi, int removed) Clip(
        IReadOnlyList<(double x, double y, double z)> pts, double lowPct, double highPct)
    {
        var kept = new List<(double x, double y, double z)>();
        int n = pts?.Count ?? 0;
        if (n == 0) return (kept, 0, 0, 0);
        if (lowPct < 0) lowPct = 0; if (highPct > 1) highPct = 1;
        if (highPct < lowPct) (lowPct, highPct) = (highPct, lowPct);

        var zs = new double[n];
        for (int i = 0; i < n; i++) zs[i] = pts![i].z;
        Array.Sort(zs);
        double zLo = Percentile(zs, lowPct);
        double zHi = Percentile(zs, highPct);
        int removed = 0;
        foreach (var p in pts!)
            if (p.z >= zLo - 1e-9 && p.z <= zHi + 1e-9) kept.Add(p); else removed++;
        return (kept, zLo, zHi, removed);
    }

    private static double Percentile(double[] sorted, double p)
    {
        int n = sorted.Length;
        if (n == 0) return 0;
        double idx = p * (n - 1);
        int lo = (int)Math.Floor(idx), hi = (int)Math.Ceiling(idx);
        if (lo < 0) lo = 0; if (hi >= n) hi = n - 1;
        double frac = idx - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }
}
