using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 煤质/品位三维体素插值 —— 忠实移植原 GeoDataBase DefaultIdwInterpolation：把散点样本(x,y,z,值)按 3D IDW
/// 插到规则体素网格; 搜索半径外体素(最近样本超半径)无数据支撑则跳过(不外插), 把体素压到贴数据的薄带。
/// 注: 原 DefaultIdwInterpolation 半径语义与 EstimationAlgorithms.IdwEstimate 略异(只判最近点是否入半径, 再取 k 最近
/// 含半径外者), 故此处按 DefaultIdwInterpolation 原样内联, 不复用 OrdinaryKriging.IdwEstimate。
/// 2D 场景无法直显三维体素(记录: 全 3D 显示受阻), 但体素场可导 CSV(块体模型)+ 取 Z 切片上 2D 图。纯逻辑、可单测。
/// </summary>
public static class QualityVoxelInterp
{
    public readonly record struct Voxel(double X, double Y, double Z, double Value);

    /// <summary>
    /// 3D IDW 体素插值(忠实原 Interpolate)。power=幂次(默认2)·k=最近样本数(默认5)·radius=搜索半径(≤0 自动)。
    /// resolution=体素边长。返回有数据支撑的体素(最近样本超半径者跳过)。maxVoxels 防爆内存(超则空)。
    /// </summary>
    public static List<Voxel> Interpolate(IReadOnlyList<OrdinaryKriging.ControlPoint> points,
        double xMin, double yMin, double zMin, double xMax, double yMax, double zMax,
        double resolution, double power = 2, int k = 5, double radius = 0, int maxVoxels = 2_000_000)
    {
        var result = new List<Voxel>();
        if (points.Count == 0 || resolution <= 0) return result;
        if (xMax < xMin || yMax < yMin || zMax < zMin) return result;
        if (radius <= 0) radius = AutoRadius(points, xMin, yMin, xMax, yMax, resolution);

        long nx = (long)((xMax - xMin) / resolution) + 1, ny = (long)((yMax - yMin) / resolution) + 1, nz = (long)((zMax - zMin) / resolution) + 1;
        if (nx * ny * nz > maxVoxels) return result;   // 过密 → 空(调大 resolution)

        for (double x = xMin; x <= xMax + 1e-9; x += resolution)
        for (double y = yMin; y <= yMax + 1e-9; y += resolution)
        for (double z = zMin; z <= zMax + 1e-9; z += resolution)
        {
            var nearest = new List<(double dist, double v)>(points.Count);
            foreach (var pt in points)
            {
                double dx = pt.X - x, dy = pt.Y - y, dz = pt.Z - z;
                nearest.Add((Math.Sqrt(dx * dx + dy * dy + dz * dz), pt.V));
            }
            nearest.Sort((a, b) => a.dist.CompareTo(b.dist));
            if (nearest[0].dist > radius) continue;                       // 无数据支撑, 不外插
            int take = Math.Min(Math.Max(1, k), nearest.Count);
            if (nearest[0].dist < 1e-3) { result.Add(new Voxel(x, y, z, nearest[0].v)); continue; }   // 落样本上, 精确
            double sumW = 0, sum = 0;
            for (int i = 0; i < take; i++)
            {
                double w = 1.0 / Math.Pow(nearest[i].dist, power);
                sumW += w; sum += w * nearest[i].v;
            }
            result.Add(new Voxel(x, y, z, sum / sumW));
        }
        return result;
    }

    // 忠实原 AutoRadius: 2.5×平均点距(按 2D 面积估), 且 ≥1.5×体素步长。
    private static double AutoRadius(IReadOnlyList<OrdinaryKriging.ControlPoint> pts, double xMin, double yMin, double xMax, double yMax, double resolution)
    {
        double dx = xMax - xMin, dy = yMax - yMin;
        double area = Math.Max(dx * dy, 1);
        double spacing = Math.Sqrt(area / Math.Max(pts.Count, 1));
        return Math.Max(resolution * 1.5, spacing * 2.5);
    }

    /// <summary>取最接近某 Z 的体素切片(供 2D 上图)。tol 为切片厚度容差。</summary>
    public static List<Voxel> ZSlice(IReadOnlyList<Voxel> voxels, double z, double tol)
    {
        var s = new List<Voxel>();
        foreach (var v in voxels) if (Math.Abs(v.Z - z) <= tol) s.Add(v);
        return s;
    }

    /// <summary>体素场 → CSV(x,y,z,value)。</summary>
    public static string ToCsv(IReadOnlyList<Voxel> voxels)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("x,y,z,value\n");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        foreach (var v in voxels)
            sb.Append($"{v.X.ToString("0.###", inv)},{v.Y.ToString("0.###", inv)},{v.Z.ToString("0.###", inv)},{v.Value.ToString("0.####", inv)}\n");
        return sb.ToString();
    }
}
