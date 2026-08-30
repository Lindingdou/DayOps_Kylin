using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点云比对 C2C（Cloud-to-Cloud）—— A 中每点到 B 的最近邻距离(偏差)。朴素 O(n·m)。纯逻辑、可单测。
/// </summary>
public static class CloudCompare
{
    public static double[] Distances(
        IReadOnlyList<(double x, double y, double z)> a, IReadOnlyList<(double x, double y, double z)> b)
    {
        var d = new double[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            double best = double.MaxValue;
            var p = a[i];
            foreach (var q in b)
            {
                double dx = p.x - q.x, dy = p.y - q.y, dz = p.z - q.z;
                double dd = dx * dx + dy * dy + dz * dz;
                if (dd < best) best = dd;
            }
            d[i] = b.Count == 0 ? 0 : Math.Sqrt(best);
        }
        return d;
    }

    public static (double max, double mean) Stats(double[] dists)
    {
        if (dists.Length == 0) return (0, 0);
        double max = 0, sum = 0;
        foreach (var v in dists) { if (v > max) max = v; sum += v; }
        return (max, sum / dists.Length);
    }
}
