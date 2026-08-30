using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>点云质量统计结果（对应原 PointCloudQualityStats 的核心指标）。</summary>
public readonly record struct PointCloudStatsResult(
    int Count,
    double MinX, double MinY, double MinZ,
    double MaxX, double MaxY, double MaxZ,
    double AreaXY, double DensityXY, double MeanZ, double StdZ);

/// <summary>
/// 点云质量统计（托管重算）—— 原 `PointCloudQualityStats` 的指标由 C++ LasLib 内核算、该类仅解析其二进制输出；
/// 此处以托管从点集重算同一组指标：计数/包围盒/XY 投影面积/点密度/高程均值·标准差(总体)。纯逻辑、可单测。
/// </summary>
public static class PointCloudStats
{
    public static PointCloudStatsResult Compute(IReadOnlyList<(double x, double y, double z)> pts)
    {
        if (pts == null || pts.Count == 0) return new PointCloudStatsResult(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        double sumZ = 0;
        foreach (var (x, y, z) in pts)
        {
            if (x < minX) minX = x; if (y < minY) minY = y; if (z < minZ) minZ = z;
            if (x > maxX) maxX = x; if (y > maxY) maxY = y; if (z > maxZ) maxZ = z;
            sumZ += z;
        }
        int n = pts.Count;
        double meanZ = sumZ / n;
        double varZ = 0;
        foreach (var p in pts) { double d = p.z - meanZ; varZ += d * d; }
        varZ /= n;                                   // 总体标准差(描述该点云本身的高程离散)
        double area = (maxX - minX) * (maxY - minY);
        double density = area > 1e-9 ? n / area : 0;
        return new PointCloudStatsResult(n, minX, minY, minZ, maxX, maxY, maxZ, area, density, meanZ, Math.Sqrt(varZ));
    }
}
