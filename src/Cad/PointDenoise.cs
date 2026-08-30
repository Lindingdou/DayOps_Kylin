using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点云去噪（托管重实现，对应原 PointCloudLib 的 SOR/ROR 去噪；原走内核 IPointCloudCapability, 算法标准可托管）。
/// SOR 统计离群：每点到 k 近邻的平均距离 &gt; 全局 μ+kσ 即剔除（相对离群）。
/// ROR 半径离群：半径内邻点数 &lt; 下限即剔除（绝对稀疏）。纯逻辑、可单测（O(n²) 朴素, 供 CSV 规模）。
/// </summary>
public static class PointDenoise
{
    private static double Dist2((double x, double y, double z) a, (double x, double y, double z) b)
    { double dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z; return dx * dx + dy * dy + dz * dz; }

    /// <summary>SOR：邻距超全局 μ+stdMul·σ 的点剔除。k≥1, stdMul&gt;0。</summary>
    public static List<(double x, double y, double z)> Sor(IReadOnlyList<(double x, double y, double z)> pts, int k, double stdMul)
    {
        int n = pts?.Count ?? 0;
        if (n == 0) return new();
        if (n <= k + 1) return new List<(double, double, double)>(pts!);   // 点太少不滤

        var meanDist = new double[n];
        for (int i = 0; i < n; i++)
        {
            var ds = new List<double>(n - 1);
            for (int j = 0; j < n; j++) if (j != i) ds.Add(Math.Sqrt(Dist2(pts[i], pts[j])));
            ds.Sort();
            double s = 0; int kk = Math.Min(k, ds.Count);
            for (int t = 0; t < kk; t++) s += ds[t];
            meanDist[i] = kk > 0 ? s / kk : 0;
        }
        double mu = meanDist.Average();
        double sigma = Math.Sqrt(meanDist.Sum(d => (d - mu) * (d - mu)) / n);
        double thr = mu + stdMul * sigma;

        var res = new List<(double x, double y, double z)>();
        for (int i = 0; i < n; i++) if (meanDist[i] <= thr + 1e-12) res.Add(pts[i]);
        return res;
    }

    /// <summary>ROR：半径 r 内邻点数(不含自身) &lt; minNeighbors 的点剔除。</summary>
    public static List<(double x, double y, double z)> Ror(IReadOnlyList<(double x, double y, double z)> pts, double radius, int minNeighbors)
    {
        int n = pts?.Count ?? 0;
        if (n == 0 || radius <= 0) return new();
        double r2 = radius * radius;
        var res = new List<(double x, double y, double z)>();
        for (int i = 0; i < n; i++)
        {
            int cnt = 0;
            for (int j = 0; j < n && cnt < minNeighbors; j++)
                if (j != i && Dist2(pts[i], pts[j]) <= r2) cnt++;
            if (cnt >= minNeighbors) res.Add(pts[i]);
        }
        return res;
    }
}
