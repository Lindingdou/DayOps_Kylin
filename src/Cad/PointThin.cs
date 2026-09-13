using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点云抽稀（PointCloudLib 托管切片）—— 体素网格抽稀：每个 cell 尺寸的立方体格保留首个点。
/// 对 XYZ 点集有效（LAS 二进制解析已由 <see cref="LasImportService"/> 支持, 解出的点可直接抽稀）。纯逻辑、可单测。
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
    /// 2.5D 建面用的 XY 体素抽稀（忠实原 LasLib recon.cpp <c>voxel_downsample_xy_minz</c>）：
    /// 只按 XY 平面分格（不按 Z），每格保留 **Z 最低** 的那个点 —— 压掉树冠/设备/车辆，留地面样本。
    /// 返回**源点索引**而不是坐标：建面顶点的真实色 / 属性由调用方按索引从点云带过去（原版 TIN 顶点
    /// 写的就是「该顶点对应点云点的本色」）。voxel ≤ 0 → 全部索引原样返回。纯逻辑、可单测。
    /// </summary>
    public static List<int> ThinXyMinZ(IReadOnlyList<(double x, double y, double z)> pts, double voxel)
    {
        int n = pts.Count;
        if (voxel <= 1e-9)
        {
            var all = new List<int>(n);
            for (int i = 0; i < n; i++) all.Add(i);
            return all;
        }
        double inv = 1.0 / voxel;
        // 键 = (ix<<32)|iy 打包 long；必须配 PackedKeyComparer，默认散列在格网上撞成链表(见该类注释)
        var cell = new Dictionary<long, int>(Math.Max(16, n / 8), PackedKeyComparer.Instance);
        var keep = new List<int>(Math.Max(16, n / 8));
        for (int i = 0; i < n; i++)
        {
            var p = pts[i];
            long k = XyKey(p.x, p.y, inv);
            if (cell.TryGetValue(k, out int slot))
            {
                if (p.z < pts[keep[slot]].z) keep[slot] = i;
            }
            else { cell[k] = keep.Count; keep.Add(i); }
        }
        return keep;
    }

    /// <summary>
    /// 自适应（保坡面细节）XY 抽稀（忠实原 LasLib <c>adaptive_voxel_downsample_minz</c> 的两遍粗先法）：
    ///   遍 1：按粗格 (voxel) 汇总每格 zmin / zmax / 最低点；
    ///   遍 2：Z 落差 &lt; flatZRange 的粗格是「平地」→ 只留那 1 个最低点；≥ 的是「坡面」→ 格内再按细格
    ///        (voxel × fineRatio) 各留最低点。平台塌稀、台阶顶/坡脚/坡面留住。
    /// 原版实现里 FineVoxelRatio=0.25、FlatZRange=1m 是内部经验常量。返回源点索引 + 平/坡粗格数（供回显）。
    /// 旧的 <see cref="ThinAdaptive"/>（按曲率贪心排斥）是点云「自适应抽稀」命令用的另一套算法，
    /// 每点要查 (2·span+1)³ 个三维格、百万点起就是分钟级，建面这条路不能走它。
    /// </summary>
    public static List<int> ThinAdaptiveXyMinZ(IReadOnlyList<(double x, double y, double z)> pts, double voxel,
                                              out long flatCells, out long steepCells,
                                              double fineRatio = 0.25, double flatZRange = 1.0)
    {
        flatCells = steepCells = 0;
        int n = pts.Count;
        fineRatio = Math.Clamp(fineRatio, 0.05, 1.0);
        if (voxel <= 1e-9) return ThinXyMinZ(pts, voxel);
        double fine = voxel * fineRatio;
        double invC = 1.0 / voxel, invF = 1.0 / fine;

        // 遍 1：粗格汇总 zmin / zmax / 最低点
        var coarse = new Dictionary<long, int>(Math.Max(16, n / 16), PackedKeyComparer.Instance);
        var zmin = new List<double>(); var zmax = new List<double>(); var sample = new List<int>();
        for (int i = 0; i < n; i++)
        {
            var p = pts[i];
            long k = XyKey(p.x, p.y, invC);
            if (coarse.TryGetValue(k, out int c))
            {
                if (p.z > zmax[c]) zmax[c] = p.z;
                if (p.z < zmin[c]) { zmin[c] = p.z; sample[c] = i; }
            }
            else { coarse[k] = zmin.Count; zmin.Add(p.z); zmax.Add(p.z); sample.Add(i); }
        }

        // 平地格：直接出最低点；坡面格：标记，遍 2 再按细格分
        var keep = new List<int>(Math.Max(16, coarse.Count * 2));
        var steep = new bool[zmin.Count];
        for (int c = 0; c < zmin.Count; c++)
        {
            if (zmax[c] - zmin[c] < flatZRange) { keep.Add(sample[c]); flatCells++; }
            else { steep[c] = true; steepCells++; }
        }
        if (steepCells == 0) return keep;

        // 遍 2：只对落在坡面粗格里的点按细格取最低点（细格键用全局细格网，不必再按粗格分组）
        var fineCell = new Dictionary<long, int>(Math.Max(16, n / 16), PackedKeyComparer.Instance);
        for (int i = 0; i < n; i++)
        {
            var p = pts[i];
            if (!coarse.TryGetValue(XyKey(p.x, p.y, invC), out int c) || !steep[c]) continue;
            long fk = XyKey(p.x, p.y, invF);
            if (fineCell.TryGetValue(fk, out int slot))
            {
                if (p.z < pts[keep[slot]].z) keep[slot] = i;
            }
            else { fineCell[fk] = keep.Count; keep.Add(i); }
        }
        return keep;
    }

    /// <summary>XY 格号打包成 long（各 32 位；坐标 / 格宽 落在 ±2^31 内即可，矿区坐标绰绰有余）。</summary>
    private static long XyKey(double x, double y, double inv)
        => ((long)(uint)(int)Math.Floor(x * inv) << 32) | (uint)(int)Math.Floor(y * inv);

    /// <summary>随机抽稀（原「随机抽稀」模式）：按 keepFraction(0..1) 随机保留子集(快速粗采样)。rng 供确定性测试。</summary>
    public static List<(double x, double y, double z)> ThinRandom(
        IReadOnlyList<(double x, double y, double z)> pts, double keepFraction, Random rng)
    {
        var res = new List<(double x, double y, double z)>();
        if (keepFraction >= 1) { res.AddRange(pts); return res; }
        if (keepFraction <= 0) return res;
        foreach (var p in pts) if (rng.NextDouble() < keepFraction) res.Add(p);
        return res;
    }

    /// <summary>
    /// 均匀(距离)抽稀（原「距离抽稀」模式）：贪心保留、任两保留点间距 ≥ minDist(保证最小间距,
    /// 比体素更均匀无网格偏差)。网格哈希加速。纯逻辑、可单测。
    /// </summary>
    public static List<(double x, double y, double z)> ThinUniform(
        IReadOnlyList<(double x, double y, double z)> pts, double minDist)
    {
        var res = new List<(double x, double y, double z)>();
        if (minDist <= 1e-9) { res.AddRange(pts); return res; }
        double r2 = minDist * minDist;
        (long, long, long) Key(double x, double y, double z) => ((long)Math.Floor(x / minDist), (long)Math.Floor(y / minDist), (long)Math.Floor(z / minDist));
        var kept = new Dictionary<(long, long, long), List<int>>();
        foreach (var p in pts)
        {
            var (kx, ky, kz) = Key(p.x, p.y, p.z);
            bool blocked = false;
            for (long dx = -1; dx <= 1 && !blocked; dx++)
                for (long dy = -1; dy <= 1 && !blocked; dy++)
                    for (long dz = -1; dz <= 1 && !blocked; dz++)
                        if (kept.TryGetValue((kx + dx, ky + dy, kz + dz), out var lst))
                            foreach (int j in lst)
                            {
                                var q = res[j];
                                double ex = q.x - p.x, ey = q.y - p.y, ez = q.z - p.z;
                                if (ex * ex + ey * ey + ez * ez < r2) { blocked = true; break; }
                            }
            if (!blocked)
            {
                int idx = res.Count; res.Add(p);
                if (!kept.TryGetValue((kx, ky, kz), out var kl)) kept[(kx, ky, kz)] = kl = new List<int>();
                kl.Add(idx);
            }
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
