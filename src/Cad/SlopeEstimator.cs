using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 工作帮坡角估算的纯核 —— 移植自原 `MineAssLib.WorkingSlopeEstimator` 的平面拟合部分：
/// 对点集中心化后最小二乘拟合平面 z'=a·x'+b·y'，取沿指定方向的坡度 = 工作帮坡角口径；
/// 无方向时取最陡坡（|梯度|）。原版沿工作线推进方向采现状面(需 TinSampler)，此抽出点集→坡角的纯算。
/// </summary>
public static class SlopeEstimator
{
    /// <summary>中心化最小二乘拟合平面梯度 (∂z/∂x, ∂z/∂y)；点&lt;3 或近共线返回 null。</summary>
    public static (double a, double b)? FitPlane(IReadOnlyList<(double x, double y, double z)> pts)
    {
        if (pts == null || pts.Count < 3) return null;
        double cx = 0, cy = 0, cz = 0;
        foreach (var p in pts) { cx += p.x; cy += p.y; cz += p.z; }
        cx /= pts.Count; cy /= pts.Count; cz /= pts.Count;
        double sxx = 0, sxy = 0, syy = 0, sxz = 0, syz = 0;
        foreach (var p in pts)
        {
            double x = p.x - cx, y = p.y - cy, z = p.z - cz;
            sxx += x * x; sxy += x * y; syy += y * y; sxz += x * z; syz += y * z;
        }
        double det = sxx * syy - sxy * sxy;
        if (Math.Abs(det) < 1e-9) return null;
        double a = (sxz * syy - syz * sxy) / det;
        double b = (syz * sxx - sxz * sxy) / det;
        return (a, b);
    }

    /// <summary>沿方向 (dx,dy) 的坡角(度)；退化返回 null。</summary>
    public static double? SlopeAlongDeg(IReadOnlyList<(double x, double y, double z)> pts, double dx, double dy)
    {
        var g = FitPlane(pts);
        if (g == null) return null;
        double dl = Math.Sqrt(dx * dx + dy * dy);
        if (dl < 1e-9) return null;
        double slope = Math.Abs(g.Value.a * dx / dl + g.Value.b * dy / dl);
        return Math.Atan(slope) * 180.0 / Math.PI;
    }

    /// <summary>最陡坡角(度) = atan(|梯度|)；退化返回 null。</summary>
    public static double? MaxSlopeDeg(IReadOnlyList<(double x, double y, double z)> pts)
    {
        var g = FitPlane(pts);
        if (g == null) return null;
        return Math.Atan(Math.Sqrt(g.Value.a * g.Value.a + g.Value.b * g.Value.b)) * 180.0 / Math.PI;
    }
}
