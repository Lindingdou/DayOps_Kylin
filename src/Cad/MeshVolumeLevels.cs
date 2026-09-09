using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 三角网「解析散度法 + 分标高」报量（原「三角网体积」：不转块体，直接由面算量）。
///
/// 分标高的关键是"某标高以下的体积"要**精确**，不能靠体素数格子。用的是散度定理的一个变形：
/// 取向量场 F = (0, 0, z − zc)（div F = 1），对"被平面 z = zc 切下来的下半部分"用高斯公式，
/// 平的盖面上 z − zc ≡ 0 不产生通量，于是
///
///     V(z &lt; zc) = ∮_{S, z&lt;zc} (z − zc) · n_z dA
///
/// 也就是说**只需对原曲面被平面切下来的部分积分，盖面不用三角化**。
/// 单个平面三角形上的积分还有闭式：((b−a)×(c−a))_z / 2 × (三顶点 (z−zc) 的均值)。
/// 跨平面的三角形按 z = zc 切成 1~3 片再各自套公式，结果对任意闭合网都精确（不要求 z 单调）。
///
/// 分层体积 = V(z&lt;上界) − V(z&lt;下界)，逐层相加必然等于整体体积（可用作自检）。纯逻辑、可单测。
/// </summary>
public static class MeshVolumeLevels
{
    public readonly record struct Band(double Z0, double Z1, double Volume);

    public sealed class Report
    {
        public double TotalVolume;          // 散度定理整体体积（闭合体才有意义）
        public double SurfaceArea;          // 表面积
        public double ProjectedAreaXY;      // XY 投影面积（凸包）
        public double MinZ, MaxZ;
        public bool Closed;
        public int BoundaryEdges;
        public int VertexCount, TriangleCount;
        public List<Band> Bands = new();
        public double BandSum;              // 分层体积和（应 ≈ TotalVolume，差值即自检残差）
    }

    /// <summary>某标高以下的体积（闭合体；外法线朝外时为正）。</summary>
    public static double VolumeBelow(
        IReadOnlyList<(double x, double y, double z)> verts,
        IReadOnlyList<(int a, int b, int c)> tris, double zc)
    {
        if (verts == null || tris == null) return 0;
        double v = 0;
        foreach (var (ia, ib, ic) in tris)
        {
            if ((uint)ia >= verts.Count || (uint)ib >= verts.Count || (uint)ic >= verts.Count) continue;
            v += TriBelow(verts[ia], verts[ib], verts[ic], zc);
        }
        return v;
    }

    /// <summary>整体 + 分标高报量。<paramref name="levels"/> 为标高边界（升序去重后取 N−1 个区间）；空则只报整体。</summary>
    public static Report Compute(
        IReadOnlyList<(double x, double y, double z)> verts,
        IReadOnlyList<(int a, int b, int c)> tris, IReadOnlyList<double>? levels)
    {
        var r = new Report();
        if (verts == null || tris == null || verts.Count == 0 || tris.Count == 0) return r;
        var m = MeshMetrics.Compute(verts, tris);
        var d = MeshDiagnose.Analyze(verts, tris);
        r.VertexCount = m.VertexCount; r.TriangleCount = m.TriangleCount;
        r.SurfaceArea = m.SurfaceArea; r.MinZ = m.MinZ; r.MaxZ = m.MaxZ;
        r.Closed = d.IsClosed; r.BoundaryEdges = d.BoundaryEdges;
        r.ProjectedAreaXY = MeshMetrics.HullAreaXY(verts);
        r.TotalVolume = MeshMetrics.RobustVolume(verts, tris);

        if (levels == null || levels.Count < 2) return r;
        var ls = new List<double>();
        foreach (var z in levels)
        {
            if (double.IsNaN(z) || double.IsInfinity(z)) continue;
            ls.Add(z);
        }
        ls.Sort();
        for (int i = ls.Count - 1; i > 0; i--) if (Math.Abs(ls[i] - ls[i - 1]) < 1e-9) ls.RemoveAt(i);
        if (ls.Count < 2) return r;

        // 逐边界算一次"以下体积"，相邻相减即得分层量（N+1 次积分而不是 N 次切网）
        var below = new double[ls.Count];
        for (int i = 0; i < ls.Count; i++) below[i] = VolumeBelow(verts, tris, ls[i]);
        for (int i = 0; i + 1 < ls.Count; i++)
        {
            double v = below[i + 1] - below[i];
            r.Bands.Add(new Band(ls[i], ls[i + 1], v));
            r.BandSum += v;
        }
        return r;
    }

    /// <summary>按等间距标高生成边界列表（覆盖 [minZ, maxZ]，边界对齐到 step 的整数倍，便于与台阶标高对上）。</summary>
    public static List<double> EvenLevels(double minZ, double maxZ, double step)
    {
        var r = new List<double>();
        if (step <= 0 || maxZ <= minZ) return r;
        double start = Math.Floor(minZ / step) * step;
        for (double z = start; z < maxZ + step * 0.5; z += step)
        {
            r.Add(Math.Round(z, 9));
            if (r.Count > 10000) break;
        }
        if (r.Count > 0 && r[^1] < maxZ) r.Add(Math.Round(maxZ, 9));
        return r;
    }

    // ── 单三角形在 z < zc 部分的 ∫ (z−zc)·n_z dA ──
    private static double TriBelow(
        (double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c, double zc)
    {
        double da = a.z - zc, db = b.z - zc, dc = c.z - zc;
        bool ba = da < 0, bb = db < 0, bc = dc < 0;
        int n = (ba ? 1 : 0) + (bb ? 1 : 0) + (bc ? 1 : 0);
        if (n == 0) return 0;                       // 整片在平面以上
        if (n == 3) return Piece(a, b, c, zc);      // 整片在平面以下

        // 跨平面：把三角形按 z = zc 切开，只留下方多边形（三角形或四边形）再扇形三角化
        var poly = new List<(double x, double y, double z)>(4);
        void Edge((double x, double y, double z) p, double dp, (double x, double y, double z) q, double dq)
        {
            if (dp < 0) poly.Add(p);
            if ((dp < 0) != (dq < 0))
            {
                double t = dp / (dp - dq);
                poly.Add((p.x + t * (q.x - p.x), p.y + t * (q.y - p.y), zc));
            }
        }
        Edge(a, da, b, db); Edge(b, db, c, dc); Edge(c, dc, a, da);
        if (poly.Count < 3) return 0;
        double v = 0;
        for (int i = 1; i + 1 < poly.Count; i++) v += Piece(poly[0], poly[i], poly[i + 1], zc);
        return v;
    }

    // ((b−a)×(c−a))_z / 2 × mean(z−zc)  —— 平面三角形上的闭式积分
    private static double Piece(
        (double x, double y, double z) a, (double x, double y, double z) b, (double x, double y, double z) c, double zc)
    {
        double nz = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);   // = 2·Area·n_z
        double mean = ((a.z - zc) + (b.z - zc) + (c.z - zc)) / 3.0;
        return nz * 0.5 * mean;
    }
}
