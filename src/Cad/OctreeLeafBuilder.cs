using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>八叉树叶块：细格原点 (X,Y,Z)（细格单位）+ 层级位移 Shift，边长 = 1 &lt;&lt; Shift 个细格。</summary>
public readonly record struct OctreeLeaf(int X, int Y, int Z, byte Shift)
{
    /// <summary>叶块边长（细格数）。</summary>
    public int Size => 1 << Shift;
    /// <summary>叶块体积（细格数）= s³。</summary>
    public long VolumeCells { get { long s = 1L << Shift; return s * s * s; } }
}

/// <summary>
/// 自底向上把【占位网格】压成八叉树叶块（忠实原 <c>BlockModelLib.Domain.OctreeLeafBuilder</c>）。
///
/// 原版所有自建生产者（实体转块体 / 体素格网体积 生成块体）都走这条：逐 cell 中心在体内的占位位图 →
/// 只存实际块。合并规则：以 pow2 立方体为根，递归八分；某节点 8 个子全实心 → 上交父级继续并块（实心内部
/// 自然塌成少量大叶块）；混合节点把实心子作为叶块就地发射、空子丢弃 → 边界细到最细格。
/// 输出 Shift = log2(边长细格数)，叶块坐标自 0 起（与 .blk 同基准）。
///
/// 代价 ∝ 占据区域（节点盒与占据 AABB 不交即 O(1) 剪枝，空区不下钻）。无属性（纯几何）。纯逻辑、可单测。
/// </summary>
public static class OctreeLeafBuilder
{
    private enum Oc : byte { Empty, Solid, Mixed }

    /// <summary>
    /// 由占位位图（keep[i]=true 即该 cell 在体内，线性索引 i+j*nx+k*nx*ny）构造叶块集。
    /// 无占据 cell 时返回空表（调用方据此判定"体内无 cell"）。
    /// </summary>
    public static List<OctreeLeaf> BuildFromOccupancy(int nx, int ny, int nz, bool[] keep)
    {
        var leaves = new List<OctreeLeaf>();
        if (nx <= 0 || ny <= 0 || nz <= 0 || keep == null || keep.Length < (long)nx * ny * nz) return leaves;

        // 占据 AABB（细格索引）：用于 O(1) 剪掉空区，使代价 ∝ 实体而非整个 pow2 根。
        int imin = int.MaxValue, jmin = int.MaxValue, kmin = int.MaxValue;
        int imax = -1, jmax = -1, kmax = -1;
        long layer = (long)nx * ny;
        for (int k = 0; k < nz; k++)
        {
            long kb = k * layer;
            for (int j = 0; j < ny; j++)
            {
                long jb = kb + (long)j * nx;
                for (int i = 0; i < nx; i++)
                {
                    if (!keep[jb + i]) continue;
                    if (i < imin) imin = i; if (i > imax) imax = i;
                    if (j < jmin) jmin = j; if (j > jmax) jmax = j;
                    if (k < kmin) kmin = k; if (k > kmax) kmax = k;
                }
            }
        }
        if (imax < 0) return leaves;   // 无占据

        // 根 = 覆盖整域的 pow2 立方体（叶块坐标自 0 起）。
        int span = Math.Max(nx, Math.Max(ny, nz));
        int root = NextPow2(span);
        byte rootShift = Log2(root);

        bool Occ(int x, int y, int z)
            => (uint)x < (uint)nx && (uint)y < (uint)ny && (uint)z < (uint)nz && keep[(long)x + (long)y * nx + (long)z * layer];

        // 返回该节点整体状态；实心节点【不发射】，交父级并块；混合节点就地发射实心子。
        Oc Rec(int x0, int y0, int z0, int size, byte shift)
        {
            // 节点盒与占据 AABB 不相交 → 整块空，O(1) 剪枝（跳过 7/8 空区）。
            if (x0 > imax || y0 > jmax || z0 > kmax || x0 + size <= imin || y0 + size <= jmin || z0 + size <= kmin)
                return Oc.Empty;
            if (size == 1) return Occ(x0, y0, z0) ? Oc.Solid : Oc.Empty;

            int h = size >> 1;
            byte cs = (byte)(shift - 1);
            Span<Oc> r = stackalloc Oc[8];   // 八子先全算，再决定是否并块 / 发射
            int ci = 0;
            for (int dz = 0; dz <= h; dz += h)
                for (int dy = 0; dy <= h; dy += h)
                    for (int dx = 0; dx <= h; dx += h)
                        r[ci++] = Rec(x0 + dx, y0 + dy, z0 + dz, h, cs);

            bool allSolid = true, any = false;
            for (int c = 0; c < 8; c++)
            {
                if (r[c] != Oc.Solid) allSolid = false;
                if (r[c] != Oc.Empty) any = true;
            }
            if (allSolid) return Oc.Solid;   // 全子实心 → 上交父级并块
            if (!any) return Oc.Empty;

            // 混合：把实心子作为叶块发射（混合子已在递归内发射，空子丢弃）。
            ci = 0;
            for (int dz = 0; dz <= h; dz += h)
                for (int dy = 0; dy <= h; dy += h)
                    for (int dx = 0; dx <= h; dx += h)
                    {
                        if (r[ci] == Oc.Solid) leaves.Add(new OctreeLeaf(x0 + dx, y0 + dy, z0 + dz, cs));
                        ci++;
                    }
            return Oc.Mixed;
        }

        if (Rec(0, 0, 0, root, rootShift) == Oc.Solid) leaves.Add(new OctreeLeaf(0, 0, 0, rootShift));   // 整域实心 → 单根叶块
        return leaves;
    }

    /// <summary>≥ v 的最小 2 的幂（v≤1 → 1）。</summary>
    private static int NextPow2(int v)
    {
        if (v <= 1) return 1;
        int p = 1;
        while (p < v) p <<= 1;
        return p;
    }

    /// <summary>log2(pow2)（p 须为 2 的幂）。</summary>
    private static byte Log2(int p)
    {
        byte s = 0;
        while ((1 << s) < p) s++;
        return s;
    }
}
