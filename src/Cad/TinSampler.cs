using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 点对 TIN（三角网）的 Z 采样器：实例侧忠实移植原 <c>MineAssLib.Driving.TinSampler</c>（均匀网格加速，「驱动量」逐列采样现状面 / 煤层顶底板面），
/// 静态 <see cref="SampleZ(IReadOnlyList{(double, double, double)}, IReadOnlyList{(int, int, int)}, double, double, double)"/> 是 §层位求交 既有接口（原样保留）。
/// </summary>
public sealed class TinSampler
{
    private readonly double[] _x, _y, _z;
    private readonly int[] _tri;
    private readonly double _minX, _minY, _invCell;
    private readonly int _nx, _ny;
    private readonly List<int>[] _grid;

    public double MinX { get; }
    public double MinY { get; }
    public double MinZ { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public double MaxZ { get; }
    public int TriangleCount => _tri.Length / 3;

    private TinSampler(double[] x, double[] y, double[] z, int[] tri)
    {
        _x = x; _y = y; _z = z; _tri = tri;
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (int i = 0; i < x.Length; i++)
        {
            if (x[i] < minX) minX = x[i]; if (x[i] > maxX) maxX = x[i];
            if (y[i] < minY) minY = y[i]; if (y[i] > maxY) maxY = y[i];
            if (z[i] < minZ) minZ = z[i]; if (z[i] > maxZ) maxZ = z[i];
        }
        MinX = _minX = minX; MinY = _minY = minY; MinZ = minZ;
        MaxX = maxX; MaxY = maxY; MaxZ = maxZ;
        int triCount = tri.Length / 3;
        double extX = Math.Max(1e-6, maxX - minX), extY = Math.Max(1e-6, maxY - minY);
        double cell = Math.Sqrt(extX * extY / Math.Max(1, triCount));
        if (cell <= 1e-9) cell = Math.Max(extX, extY);
        _invCell = 1.0 / cell;
        _nx = Math.Max(1, Math.Min(2048, (int)(extX / cell) + 1));
        _ny = Math.Max(1, Math.Min(2048, (int)(extY / cell) + 1));
        _grid = new List<int>[_nx * _ny];
        for (int t = 0; t < triCount; t++)
        {
            int a = tri[t * 3], b = tri[t * 3 + 1], c = tri[t * 3 + 2];
            double tx0 = Math.Min(x[a], Math.Min(x[b], x[c])), tx1 = Math.Max(x[a], Math.Max(x[b], x[c]));
            double ty0 = Math.Min(y[a], Math.Min(y[b], y[c])), ty1 = Math.Max(y[a], Math.Max(y[b], y[c]));
            int gx0 = ClampX(CellX(tx0)), gx1 = ClampX(CellX(tx1)), gy0 = ClampY(CellY(ty0)), gy1 = ClampY(CellY(ty1));
            for (int gy = gy0; gy <= gy1; gy++)
                for (int gx = gx0; gx <= gx1; gx++)
                    (_grid[gy * _nx + gx] ??= new List<int>()).Add(t);
        }
    }

    private int CellX(double x) => (int)((x - _minX) * _invCell);
    private int CellY(double y) => (int)((y - _minY) * _invCell);
    private int ClampX(int gx) => gx < 0 ? 0 : (gx >= _nx ? _nx - 1 : gx);
    private int ClampY(int gy) => gy < 0 ? 0 : (gy >= _ny ? _ny - 1 : gy);

    /// <summary>从世界三角网构建采样器；顶点 &lt; 3 或无三角则返回 null。</summary>
    public static TinSampler? TryBuild(double[]? worldVerts, int[]? triIndices)
    {
        if (worldVerts == null || triIndices == null) return null;
        int vn = worldVerts.Length / 3;
        if (vn < 3 || triIndices.Length < 3) return null;
        var x = new double[vn]; var y = new double[vn]; var z = new double[vn];
        for (int i = 0; i < vn; i++) { x[i] = worldVerts[i * 3]; y[i] = worldVerts[i * 3 + 1]; z[i] = worldVerts[i * 3 + 2]; }
        return new TinSampler(x, y, z, triIndices);
    }

    /// <summary>从场景三角网建（顶点/三角元组）。</summary>
    public static TinSampler? TryBuild(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        if (verts == null || tris == null || verts.Count < 3 || tris.Count < 1) return null;
        var flat = new double[verts.Count * 3];
        for (int i = 0; i < verts.Count; i++) { flat[i * 3] = verts[i].x; flat[i * 3 + 1] = verts[i].y; flat[i * 3 + 2] = verts[i].z; }
        var idx = new int[tris.Count * 3];
        for (int i = 0; i < tris.Count; i++) { idx[i * 3] = tris[i].a; idx[i * 3 + 1] = tris[i].b; idx[i * 3 + 2] = tris[i].c; }
        return TryBuild(flat, idx);
    }

    public void GetGeometry(out double[] worldVerts, out int[] triIndices)
    {
        worldVerts = new double[_x.Length * 3];
        for (int i = 0; i < _x.Length; i++) { worldVerts[i * 3] = _x[i]; worldVerts[i * 3 + 1] = _y[i]; worldVerts[i * 3 + 2] = _z[i]; }
        triIndices = (int[])_tri.Clone();
    }

    // ── 既有的静态采高（§层位求交：顶底板竖直求交算高程）—— 保留原接口，调用方不动 ──

    /// <summary>在 (qx,qy) 处对三角网采高：命中含该点的三角形→重心插值 Z；无命中返回 null。</summary>
    public static double? SampleZ(IReadOnlyList<(double x, double y, double z)> pts, IReadOnlyList<(int a, int b, int c)> tris, double qx, double qy, double eps = 1e-9)
    {
        foreach (var (ia, ib, ic) in tris)
        {
            if (ia < 0 || ib < 0 || ic < 0 || ia >= pts.Count || ib >= pts.Count || ic >= pts.Count) continue;
            var p0 = pts[ia]; var p1 = pts[ib]; var p2 = pts[ic];
            double denom = (p1.y - p2.y) * (p0.x - p2.x) + (p2.x - p1.x) * (p0.y - p2.y);
            if (Math.Abs(denom) < 1e-15) continue;
            double u = ((p1.y - p2.y) * (qx - p2.x) + (p2.x - p1.x) * (qy - p2.y)) / denom;
            double v = ((p2.y - p0.y) * (qx - p2.x) + (p0.x - p2.x) * (qy - p2.y)) / denom;
            double w = 1 - u - v;
            if (u >= -eps && v >= -eps && w >= -eps) return u * p0.z + v * p1.z + w * p2.z;
        }
        return null;
    }

    /// <summary>便捷：对点集自动三角剖分后采高。点少于 3 或落网外返回 null。</summary>
    public static double? SampleZ(IReadOnlyList<(double x, double y, double z)> pts, double qx, double qy)
    {
        if (pts.Count < 3) return null;
        var xy = new List<(double x, double y)>(pts.Count);
        foreach (var p in pts) xy.Add((p.x, p.y));
        var tris = Delaunay.Triangulate(xy);
        return SampleZ(pts, tris, qx, qy);
    }

    /// <summary>在 (x,y) 处采面高程 z。命中某三角即插值返回 true；点不在面投影内（洞/外侧）返回 false。</summary>
    public bool TrySampleZ(double x, double y, out double z)
    {
        z = 0;
        if (x < MinX || x > MaxX || y < MinY || y > MaxY) return false;
        int gx = ClampX(CellX(x)), gy = ClampY(CellY(y));
        var bucket = _grid[gy * _nx + gx];
        if (bucket == null) return false;
        const double tol = 1e-7;
        foreach (int t in bucket)
        {
            int a = _tri[t * 3], b = _tri[t * 3 + 1], c = _tri[t * 3 + 2];
            double x1 = _x[a], y1 = _y[a], x2 = _x[b], y2 = _y[b], x3 = _x[c], y3 = _y[c];
            double d = (y2 - y3) * (x1 - x3) + (x3 - x2) * (y1 - y3);
            if (Math.Abs(d) < 1e-12) continue;
            double l1 = ((y2 - y3) * (x - x3) + (x3 - x2) * (y - y3)) / d;
            double l2 = ((y3 - y1) * (x - x3) + (x1 - x3) * (y - y3)) / d;
            double l3 = 1.0 - l1 - l2;
            if (l1 >= -tol && l2 >= -tol && l3 >= -tol) { z = l1 * _z[a] + l2 * _z[b] + l3 * _z[c]; return true; }
        }
        return false;
    }
}
