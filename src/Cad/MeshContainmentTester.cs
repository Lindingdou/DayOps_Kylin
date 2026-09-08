using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>点在闭合网格内外的测试器抽象（原 BlockModelLib.IInsideTester）：奇偶射线 / 缠绕数两种实现共用。</summary>
public interface IInsideTester
{
    /// <summary>点是否在闭合网格内部。</summary>
    bool IsInsideClosed(double x, double y, double z);
}

/// <summary>
/// 点-mesh 内外测试器（忠实移植原 <c>BlockModelLib.Domain.MeshContainmentTester</c>，块体精确约束 / 体素化默认判据）。
///
/// 方法：垂直 +Z 射线 + XY 均匀网格加速结构。
///   - 闭合 mesh：奇数次相交 = inside，偶数 = outside（射线参数定理）
///   - 开放 2.5D 表面 mesh：0/1 次相交 → 上下方判别
///
/// 性能：建结构 O(F · cellsPerTri)，单点查询 O(triPerCell)。与 <see cref="WindingNumberTester"/> 的分工同原版——
/// 水密体奇偶射线本就精确且快得多，GWN 只留给真正破洞 / 自交 / 未焊接的网格（「高容错」选项）。
///
/// 精度：射线起点做确定性微抖动（~1e-7 × mesh XY 尺度），避免正好穿过三角共享边被双计数。
/// 只读、无状态 → 线程安全。
/// </summary>
public sealed class MeshContainmentTester : IInsideTester
{
    // ── 输入几何 ──
    private readonly double[] _verts;        // [x0,y0,z0, x1,y1,z1, ...]
    private readonly int[] _tris;            // length = 3·F
    private readonly int _faceCount;

    // ── XY 加速网格 ──
    private readonly int _gridW, _gridH;
    private readonly double _ox, _oy;
    private readonly double _cellW, _cellH;
    private readonly List<int>?[] _bins;     // bins[gx + gy*gridW] = 三角索引列表
    // 每个 bin 内三角的 Z 包络（空 bin = +∞/−∞）。BoxTouchesSurface 先整 bin 剪枝再逐三角精判。
    private readonly double[] _binZMin;
    private readonly double[] _binZMax;

    public double MinX { get; }
    public double MinY { get; }
    public double MinZ { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public double MaxZ { get; }

    public int FaceCount => _faceCount;

    public MeshContainmentTester(double[] worldVerts, int[] triangleIndices)
    {
        if (worldVerts == null || triangleIndices == null) throw new ArgumentNullException(nameof(worldVerts));
        if (worldVerts.Length < 9 || worldVerts.Length % 3 != 0) throw new ArgumentException("worldVerts 必须 ≥9 且为 3 的倍数");
        if (triangleIndices.Length < 3 || triangleIndices.Length % 3 != 0) throw new ArgumentException("triangleIndices 必须 ≥3 且为 3 的倍数");

        _verts = worldVerts;
        _tris = triangleIndices;
        _faceCount = triangleIndices.Length / 3;

        double minX = double.PositiveInfinity, minY = minX, minZ = minX;
        double maxX = double.NegativeInfinity, maxY = maxX, maxZ = maxX;
        for (int i = 0; i < worldVerts.Length; i += 3)
        {
            double x = worldVerts[i], y = worldVerts[i + 1], z = worldVerts[i + 2];
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
            if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
        }
        MinX = minX; MinY = minY; MinZ = minZ;
        MaxX = maxX; MaxY = maxY; MaxZ = maxZ;

        // XY 网格：~sqrt(F) 每边（每 cell 平均 ~1 三角），上限 256 边
        int side = Math.Max(8, (int)Math.Ceiling(Math.Sqrt(_faceCount)));
        side = Math.Min(side, 256);
        _gridW = side; _gridH = side;

        double rangeX = Math.Max(1e-9, maxX - minX);
        double rangeY = Math.Max(1e-9, maxY - minY);
        _ox = minX; _oy = minY;
        _cellW = rangeX / _gridW;
        _cellH = rangeY / _gridH;

        _bins = new List<int>?[_gridW * _gridH];
        _binZMin = new double[_gridW * _gridH];
        _binZMax = new double[_gridW * _gridH];
        for (int i = 0; i < _binZMin.Length; i++) { _binZMin[i] = double.PositiveInfinity; _binZMax[i] = double.NegativeInfinity; }

        for (int f = 0; f < _faceCount; f++)
        {
            int i0 = _tris[3 * f] * 3, i1 = _tris[3 * f + 1] * 3, i2 = _tris[3 * f + 2] * 3;
            double x0 = _verts[i0], y0 = _verts[i0 + 1], tz0 = _verts[i0 + 2];
            double x1 = _verts[i1], y1 = _verts[i1 + 1], tz1 = _verts[i1 + 2];
            double x2 = _verts[i2], y2 = _verts[i2 + 1], tz2 = _verts[i2 + 2];
            double tminX = Math.Min(x0, Math.Min(x1, x2)), tmaxX = Math.Max(x0, Math.Max(x1, x2));
            double tminY = Math.Min(y0, Math.Min(y1, y2)), tmaxY = Math.Max(y0, Math.Max(y1, y2));
            double tminZ = Math.Min(tz0, Math.Min(tz1, tz2)), tmaxZ = Math.Max(tz0, Math.Max(tz1, tz2));

            int gx0 = (int)Math.Max(0, Math.Floor((tminX - _ox) / _cellW));
            int gx1 = (int)Math.Min(_gridW - 1, Math.Floor((tmaxX - _ox) / _cellW));
            int gy0 = (int)Math.Max(0, Math.Floor((tminY - _oy) / _cellH));
            int gy1 = (int)Math.Min(_gridH - 1, Math.Floor((tmaxY - _oy) / _cellH));
            for (int gy = gy0; gy <= gy1; gy++)
                for (int gx = gx0; gx <= gx1; gx++)
                {
                    int idx = gx + gy * _gridW;
                    var bin = _bins[idx] ??= new List<int>(4);
                    bin.Add(f);
                    if (tminZ < _binZMin[idx]) _binZMin[idx] = tminZ;
                    if (tmaxZ > _binZMax[idx]) _binZMax[idx] = tmaxZ;
                }
        }
    }

    /// <summary>从「顶点列 + 三角索引三元组」建（Kylin 场景 MeshEntity 用）。</summary>
    public static MeshContainmentTester FromMesh(IReadOnlyList<(double x, double y, double z)> verts, IReadOnlyList<(int a, int b, int c)> tris)
    {
        var (fv, ft) = MeshContainment.Flatten(verts, tris);
        return new MeshContainmentTester(fv, ft);
    }

    /// <summary>
    /// 盒 [x0,x1]×[y0,y1]×[z0,z1] 是否被 mesh 三角穿过（保守判交，宁可多报不可漏报）。
    /// 用于「8 角一致但面从盒肚子里穿过」的兜底细分判据。
    /// </summary>
    public bool BoxTouchesSurface(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        if (x0 > x1) (x0, x1) = (x1, x0);
        if (y0 > y1) (y0, y1) = (y1, y0);
        if (z0 > z1) (z0, z1) = (z1, z0);
        if (x1 < MinX || x0 > MaxX || y1 < MinY || y0 > MaxY || z1 < MinZ || z0 > MaxZ) return false;

        int gx0 = (int)Math.Max(0, Math.Floor((x0 - _ox) / _cellW));
        int gx1 = (int)Math.Min(_gridW - 1, Math.Floor((x1 - _ox) / _cellW));
        int gy0 = (int)Math.Max(0, Math.Floor((y0 - _oy) / _cellH));
        int gy1 = (int)Math.Min(_gridH - 1, Math.Floor((y1 - _oy) / _cellH));

        for (int gy = gy0; gy <= gy1; gy++)
            for (int gx = gx0; gx <= gx1; gx++)
            {
                int idx = gx + gy * _gridW;
                var bin = _bins[idx];
                if (bin == null) continue;
                if (_binZMax[idx] < z0 || _binZMin[idx] > z1) continue;
                foreach (int f in bin)
                {
                    int i0 = _tris[3 * f] * 3, i1 = _tris[3 * f + 1] * 3, i2 = _tris[3 * f + 2] * 3;
                    double ax = _verts[i0], ay = _verts[i0 + 1], az = _verts[i0 + 2];
                    double bx = _verts[i1], by = _verts[i1 + 1], bz = _verts[i1 + 2];
                    double cx = _verts[i2], cy = _verts[i2 + 1], cz = _verts[i2 + 2];

                    double tminX = Math.Min(ax, Math.Min(bx, cx)), tmaxX = Math.Max(ax, Math.Max(bx, cx));
                    if (tminX > x1 || tmaxX < x0) continue;
                    double tminY = Math.Min(ay, Math.Min(by, cy)), tmaxY = Math.Max(ay, Math.Max(by, cy));
                    if (tminY > y1 || tmaxY < y0) continue;
                    double tminZ = Math.Min(az, Math.Min(bz, cz)), tmaxZ = Math.Max(az, Math.Max(bz, cz));
                    if (tminZ > z1 || tmaxZ < z0) continue;

                    // 收紧 Z：取三角所在平面在 XY 重叠矩形上的 Z 幅度 ∩ 三角自身 Z 包络（陡壁三角别把整列都判成被穿过）
                    double area2 = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
                    if (Math.Abs(area2) > 1e-12)
                    {
                        double rx0 = Math.Max(x0, tminX), rx1 = Math.Min(x1, tmaxX);
                        double ry0 = Math.Max(y0, tminY), ry1 = Math.Min(y1, tmaxY);
                        double dzdx = -((by - ay) * (cz - az) - (bz - az) * (cy - ay)) / area2;
                        double dzdy = -((bz - az) * (cx - ax) - (bx - ax) * (cz - az)) / area2;
                        double e00 = az + dzdx * (rx0 - ax) + dzdy * (ry0 - ay);
                        double e10 = az + dzdx * (rx1 - ax) + dzdy * (ry0 - ay);
                        double e01 = az + dzdx * (rx0 - ax) + dzdy * (ry1 - ay);
                        double e11 = az + dzdx * (rx1 - ax) + dzdy * (ry1 - ay);
                        double pz0 = Math.Max(tminZ, Math.Min(Math.Min(e00, e10), Math.Min(e01, e11)));
                        double pz1 = Math.Min(tmaxZ, Math.Max(Math.Max(e00, e10), Math.Max(e01, e11)));
                        if (pz0 > z1 || pz1 < z0) continue;
                    }
                    return true;
                }
            }
        return false;
    }

    /// <summary>
    /// 沿 +Z 方向统计从 (x,y,z) 出发的射线与 mesh 的相交。
    /// </summary>
    /// <param name="crossingsAbove">点之上与 mesh 相交的次数</param>
    /// <param name="minTriZAbove">点之上首个被命中的三角面 Z（无命中 = NaN）</param>
    /// <returns>(x,y) 是否落在 mesh 的 XY 投影范围内</returns>
    public bool RaycastAbove(double x, double y, double z, out int crossingsAbove, out double minTriZAbove)
    {
        crossingsAbove = 0;
        minTriZAbove = double.NaN;

        // 确定性微抖动：避免 +Z 射线正好穿过共享边/顶点被计两次（轴对齐 mesh 上会成行系统性发生）。
        // 幅度 ~ mesh XY 尺度的 1e-7：远大于重心 eps 带，远小于体素尺度。两轴取不同因子避开对角线。
        x += (MaxX - MinX) * 1e-7 + 1e-12;
        y += (MaxY - MinY) * 0.73e-7 + 1e-12;

        if (x < MinX || x > MaxX || y < MinY || y > MaxY) return false;

        int gx = (int)Math.Max(0, Math.Min(_gridW - 1, Math.Floor((x - _ox) / _cellW)));
        int gy = (int)Math.Max(0, Math.Min(_gridH - 1, Math.Floor((y - _oy) / _cellH)));
        var bin = _bins[gx + gy * _gridW];
        if (bin == null) return false;

        bool inProjection = false;
        double bestZ = double.PositiveInfinity;
        foreach (int f in bin)
        {
            int i0 = _tris[3 * f] * 3, i1 = _tris[3 * f + 1] * 3, i2 = _tris[3 * f + 2] * 3;
            double x0 = _verts[i0], y0 = _verts[i0 + 1], z0 = _verts[i0 + 2];
            double x1 = _verts[i1], y1 = _verts[i1 + 1], z1 = _verts[i1 + 2];
            double x2 = _verts[i2], y2 = _verts[i2 + 1], z2 = _verts[i2 + 2];

            double area2 = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
            if (Math.Abs(area2) < 1e-12) continue;   // XY 退化（垂直壁）跳过

            double w0 = ((x1 - x) * (y2 - y) - (x2 - x) * (y1 - y)) / area2;
            double w1 = ((x2 - x) * (y0 - y) - (x0 - x) * (y2 - y)) / area2;
            double w2 = 1.0 - w0 - w1;
            const double eps = 1e-9;
            if (w0 < -eps || w1 < -eps || w2 < -eps) continue;

            double triZ = w0 * z0 + w1 * z1 + w2 * z2;
            inProjection = true;
            if (triZ > z + 1e-9)
            {
                crossingsAbove++;
                if (triZ < bestZ) bestZ = triZ;
            }
        }
        if (inProjection && !double.IsPositiveInfinity(bestZ)) minTriZAbove = bestZ;
        return inProjection;
    }

    /// <summary>闭合 mesh 内部判别：+Z 射线奇数次相交。</summary>
    public bool IsInsideClosed(double x, double y, double z)
    {
        RaycastAbove(x, y, z, out int crossings, out _);
        return (crossings & 1) == 1;
    }

    /// <summary>开放 2.5D 表面下方判别（cell.z &lt; surfaceZ(x,y)）。XY 投影外返回 false。</summary>
    public bool IsBelowSurface(double x, double y, double z)
    {
        bool inProj = RaycastAbove(x, y, z, out _, out double minZ);
        if (!inProj || double.IsNaN(minZ)) return false;
        return minZ > z;
    }

    /// <summary>开放 2.5D 表面上方判别。XY 投影外返回 false。</summary>
    public bool IsAboveSurface(double x, double y, double z)
    {
        bool inProj = RaycastAbove(x, y, z, out int crossings, out _);
        if (!inProj) return false;
        return crossings == 0;   // 投影内且上方无三角 → 在面之上
    }
}
