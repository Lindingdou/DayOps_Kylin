using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>逐 Z 层煤/岩资源剖面（忠实移植原 <c>BlockModelLib.Domain.ResourceProfile</c> 的求解相关字段）。
/// 从稀疏块体按 Z 层聚合(品位阈值判煤/岩)构建。k=0 最深、k=Nz-1 最顶。</summary>
public sealed class ResourceProfileLite
{
    public int Nz;
    public double Dz;         // 层厚 = 块尺寸
    public double Density;    // 煤密度 t/m³
    public double[] CoalVol = Array.Empty<double>();
    public double[] WasteVol = Array.Empty<double>();

    public static ResourceProfileLite? FromBlocks(IReadOnlyList<(double X, double Y, double Z, double Size, double Grade)> blocks,
        double cutoff, double density)
    {
        if (blocks == null || blocks.Count == 0) return null;
        double cell = blocks[0].Size > 1e-9 ? blocks[0].Size : 1.0;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var b in blocks) { if (b.Z < minZ) minZ = b.Z; if (b.Z > maxZ) maxZ = b.Z; }
        int nz = (int)Math.Round((maxZ - minZ) / cell) + 1;
        if (nz <= 0) return null;
        var p = new ResourceProfileLite { Nz = nz, Dz = cell, Density = density, CoalVol = new double[nz], WasteVol = new double[nz] };
        double cellVol = cell * cell * cell;
        foreach (var b in blocks)
        {
            int k = Math.Clamp((int)Math.Round((b.Z - minZ) / cell), 0, nz - 1);
            if (b.Grade >= cutoff) p.CoalVol[k] += cellVol; else p.WasteVol[k] += cellVol;
        }
        return p;
    }
}

/// <summary>深度版境界圈定求解结果。</summary>
public sealed class DepthSolveResult
{
    public int BottomK;          // 坑底所在层(k 越小越深)
    public double CoalT;         // 圈入煤量 t
    public double WasteM3;       // 圈入岩量 m³
    public double ContourSR;     // 境界剥采比(坑底层边际 m³/t)
    public double NetValueYuan;  // 最优净值 元
    public double DepthM;        // 最优坑深 m = (Nz-BottomK)·Dz
}

/// <summary>
/// 境界圈定求解·深度版（忠实移植原 <c>PlanLib.BoundaryOptimization.SectionSolver.SolveDepth</c>）——
/// 从顶向下逐层累加煤/岩, 按净值最大定坑底(等价境界剥采比法: 净值最大处边际剥采比≈经济合理剥采比 n经=(d−a)/b)。
/// 纯函数、可单测。
/// </summary>
public static class SectionSolver
{
    /// <param name="revenuePerCoalT">单位煤净收益 (d−a) 元/t</param>
    /// <param name="stripCostPerM3">剥离成本 b 元/m³</param>
    /// <param name="maxDepthM">几何允许最大深度; 坑底不深于此。默认不限。</param>
    public static DepthSolveResult SolveDepth(ResourceProfileLite p, double revenuePerCoalT, double stripCostPerM3,
        double maxDepthM = double.MaxValue)
    {
        int nz = p.Nz;
        double dens = p.Density;
        int kFloor = 0;
        if (maxDepthM < double.MaxValue && p.Dz > 1e-9)
            kFloor = Math.Max(0, nz - Math.Max(1, (int)Math.Floor(maxDepthM / p.Dz)));

        double cumCoalT = 0, cumWasteM3 = 0, bestNet = double.NegativeInfinity;
        int bestK = nz;
        double bestCoalT = 0, bestWasteM3 = 0;

        for (int k = nz - 1; k >= kFloor; k--)
        {
            cumCoalT += p.CoalVol[k] * dens;
            cumWasteM3 += p.WasteVol[k];
            double net = cumCoalT * revenuePerCoalT - cumWasteM3 * stripCostPerM3;
            if (net > bestNet)
            {
                bestNet = net; bestK = k;
                bestCoalT = cumCoalT; bestWasteM3 = cumWasteM3;
            }
        }

        double margSR = (bestK < nz && p.CoalVol[bestK] * dens > 1e-6)
            ? p.WasteVol[bestK] / (p.CoalVol[bestK] * dens)
            : 0;

        return new DepthSolveResult
        {
            BottomK = bestK,
            CoalT = bestCoalT,
            WasteM3 = bestWasteM3,
            ContourSR = margSR,
            NetValueYuan = bestNet < 0 ? 0 : bestNet,
            DepthM = (nz - bestK) * p.Dz,
        };
    }
}
