using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>一个体素子块：中心 + 尺寸 + 体内占比（"百分比块"）。忠实原 VoxelVolumeBuilder.SubCell。</summary>
public readonly record struct VoxelSubCell(double X, double Y, double Z, double Sx, double Sy, double Sz, double Percent);

/// <summary>自适应体素化结果：总体积（实心块满体积 + 边界块占比加权）+ 实心母块数 + 边界细分子块 + 网格维度。</summary>
public sealed class AdaptiveVoxelResult
{
    public double Volume;
    public long SolidCells;
    public List<VoxelSubCell> SubCells = new();
    public int Nx, Ny, Nz;
}

/// <summary>
/// 自适应体素细分 —— 忠实移植原 <c>BlockModelLib.Domain.VoxelVolumeBuilder</c> 的"自适应子块退化"核：
/// 内外一致的母块整体保留/丢弃；<b>边界母块</b>递归 octree(8-octant)细分，最细层用 N³ 子采样算体内占比，
/// 占比&gt;0 即留满尺寸块、体积按占比加权（成熟矿业软件"百分比块"，储量无偏，消斜面/尖灭缝）。
/// depth≤0 退化为均匀中心判定（原 adaptive=null 旧行为，= Kylin 既有 VoxelBands 中心法）。
/// 纯几何：只吃一个内外判定谓词 <c>inside(x,y,z)</c>（Kylin 传 <see cref="WindingNumberTester"/>.IsInsideClosed）。
/// </summary>
public static class AdaptiveVoxel
{
    /// <summary>一个 cell 的体内占比：N×N×N 均匀网点测内外，命中/N³。N≤1 退化为单中心二值。忠实原 SamplePercent。</summary>
    public static double SamplePercent(Func<double, double, double, bool> inside,
        double cx, double cy, double cz, double sx, double sy, double sz, int sampleN)
    {
        if (sampleN <= 1) return inside(cx, cy, cz) ? 1.0 : 0.0;
        int n = sampleN, hit = 0;
        double stepx = sx / n, stepy = sy / n, stepz = sz / n;
        double x0 = cx - sx * 0.5 + stepx * 0.5;
        double y0 = cy - sy * 0.5 + stepy * 0.5;
        double z0 = cz - sz * 0.5 + stepz * 0.5;
        for (int a = 0; a < n; a++)
        {
            double x = x0 + a * stepx;
            for (int b = 0; b < n; b++)
            {
                double y = y0 + b * stepy;
                for (int c = 0; c < n; c++)
                    if (inside(x, y, z0 + c * stepz)) hit++;
            }
        }
        return hit / ((double)n * n * n);
    }

    /// <summary>递归细分一个边界 cell（忠实原 Refine）：实心 octant→满占比叶子；8 子中心皆外→N³ 兜底(薄壁不丢)；边界→续分。</summary>
    public static void RefineCell(Func<double, double, double, bool> inside, List<VoxelSubCell> outp,
        double cx, double cy, double cz, double sx, double sy, double sz, int depthLeft, int sampleN)
    {
        if (depthLeft <= 0)
        {
            double p = SamplePercent(inside, cx, cy, cz, sx, sy, sz, sampleN);
            if (p > 0) outp.Add(new VoxelSubCell(cx, cy, cz, sx, sy, sz, p));
            return;
        }
        double qx = sx * 0.25, qy = sy * 0.25, qz = sz * 0.25;
        int nIn = 0;
        for (int c = 0; c < 8; c++)
        {
            double ccx = cx + ((c & 1) != 0 ? qx : -qx);
            double ccy = cy + ((c & 2) != 0 ? qy : -qy);
            double ccz = cz + ((c & 4) != 0 ? qz : -qz);
            if (inside(ccx, ccy, ccz)) nIn++;
        }
        if (nIn == 8) { outp.Add(new VoxelSubCell(cx, cy, cz, sx, sy, sz, 1.0)); return; }   // 实心 → 占比 1
        if (nIn == 0)
        {
            double p = SamplePercent(inside, cx, cy, cz, sx, sy, sz, sampleN);   // 薄壁尖灭兜底
            if (p > 0) outp.Add(new VoxelSubCell(cx, cy, cz, sx, sy, sz, p));
            return;
        }
        double hx = sx * 0.5, hy = sy * 0.5, hz = sz * 0.5;                       // 边界 → 续分 8 octant
        for (int c = 0; c < 8; c++)
        {
            double ccx = cx + ((c & 1) != 0 ? qx : -qx);
            double ccy = cy + ((c & 2) != 0 ? qy : -qy);
            double ccz = cz + ((c & 4) != 0 ? qz : -qz);
            RefineCell(inside, outp, ccx, ccy, ccz, hx, hy, hz, depthLeft - 1, sampleN);
        }
    }

    /// <summary>
    /// 自适应体素化 bounds：母块中心判内外 → 分类(实心/空/边界，按 6 邻居异号) → 边界母块递归细分 →
    /// 总体积(实心满 + 边界占比加权)。depth≤0 = 均匀(中心法)。忠实原 VoxelVolumeBuilder.Build（去并行/取消/进度，纯算法）。
    /// </summary>
    public static AdaptiveVoxelResult Voxelize(Func<double, double, double, bool> inside,
        double minX, double minY, double minZ, double maxX, double maxY, double maxZ,
        double vx, double vy, double vz, int depth, int sampleN)
    {
        var res = new AdaptiveVoxelResult();
        if (vx <= 0 || vy <= 0 || vz <= 0 || maxX <= minX || maxY <= minY || maxZ <= minZ) return res;
        int nx = Math.Max(1, (int)Math.Ceiling((maxX - minX) / vx));
        int ny = Math.Max(1, (int)Math.Ceiling((maxY - minY) / vy));
        int nz = Math.Max(1, (int)Math.Ceiling((maxZ - minZ) / vz));
        res.Nx = nx; res.Ny = ny; res.Nz = nz;
        double cellVol = vx * vy * vz;
        sampleN = Math.Max(1, sampleN);

        double CX(int i) => minX + (i + 0.5) * vx;
        double CY(int j) => minY + (j + 0.5) * vy;
        double CZ(int k) => minZ + (k + 0.5) * vz;

        // ① 母块中心内外
        var centerIn = new bool[(long)nx * ny * nz];
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                    if (inside(CX(i), CY(j), CZ(k))) centerIn[(long)i + (long)j * nx + (long)k * nx * ny] = true;

        bool NIn(int i, int j, int k) => (uint)i < (uint)nx && (uint)j < (uint)ny && (uint)k < (uint)nz
            && centerIn[(long)i + (long)j * nx + (long)k * nx * ny];

        int maxDepth = depth > 0 ? depth : 0;

        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    bool ci = NIn(i, j, k);
                    if (maxDepth <= 0)   // 均匀（旧行为）
                    {
                        if (ci) { res.Volume += cellVol; res.SolidCells++; }
                        continue;
                    }
                    bool boundary =
                        ci != NIn(i - 1, j, k) || ci != NIn(i + 1, j, k) ||
                        ci != NIn(i, j - 1, k) || ci != NIn(i, j + 1, k) ||
                        ci != NIn(i, j, k - 1) || ci != NIn(i, j, k + 1);
                    if (!boundary)
                    {
                        if (ci) { res.Volume += cellVol; res.SolidCells++; }   // 实心母块
                        continue;                                              // 全外母块跳过
                    }
                    int before = res.SubCells.Count;
                    RefineCell(inside, res.SubCells, CX(i), CY(j), CZ(k), vx, vy, vz, maxDepth, sampleN);
                    for (int s = before; s < res.SubCells.Count; s++)
                    {
                        var sc = res.SubCells[s];
                        res.Volume += sc.Sx * sc.Sy * sc.Sz * sc.Percent;
                    }
                }
        return res;
    }
}
