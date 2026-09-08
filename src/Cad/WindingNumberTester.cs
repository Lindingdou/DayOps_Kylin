using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 广义缠绕数(GWN)点-mesh 内外测试器（忠实移植原 <c>BlockModelLib.Domain.WindingNumberTester</c>）——
/// 对未焊接/破洞/自相交/非流形封闭体仍能判内外(阈值 0.5)。w(p)=(1/4π)·Σ_t Ω_t, Ω_t=三角有符号立体角
/// (Van Oosterom–Strackee)。三角 BVH + Barnes-Hut 偶极远场近似(~O(log F)/点)。前提：三角朝向一致。
/// 纯几何、可单测。
/// </summary>
public sealed class WindingNumberTester : IInsideTester
{
    private const double Inv4Pi = 0.07957747154594767;   // 1/(4π)
    private const double Inv8Pi = 0.03978873577297383;   // 1/(8π)

    /// <summary>远场开角阈值：dist(p,node.c) > Beta·node.r 时用偶极近似。2=精度/速度折中; 0=全精确。</summary>
    public double Beta { get; set; } = 2.0;

    private const int LeafSize = 8;

    private readonly double[] _tv;     // 9·F: v0xyz, v1xyz, v2xyz
    private readonly double[] _ax, _ay, _az;   // cross_t=(v1-v0)×(v2-v0)
    private readonly double[] _cx, _cy, _cz;   // 三角质心
    private readonly double[] _area;
    private readonly int _f;

    private struct Node
    {
        public double Ax, Ay, Az;   // 聚合 Σcross
        public double Cx, Cy, Cz;   // 面积加权质心
        public double R;            // 包围半径
        public int Start, Count;    // 叶子: _order[Start..Start+Count)
        public int Left, Right;     // 内部节点子索引; 叶子 -1
    }
    private readonly Node[] _nodes;
    private int _nodeCount;
    private readonly int[] _order;
    private readonly double[] _key;

    public double MinX { get; }
    public double MinY { get; }
    public double MinZ { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public double MaxZ { get; }
    public int FaceCount => _f;

    public WindingNumberTester(double[] worldVerts, int[] tris)
    {
        if (worldVerts == null || tris == null) throw new ArgumentNullException("worldVerts/tris");
        if (worldVerts.Length < 9 || worldVerts.Length % 3 != 0) throw new ArgumentException("worldVerts 须 ≥9 且为 3 的倍数");
        if (tris.Length < 3 || tris.Length % 3 != 0) throw new ArgumentException("tris 须 ≥3 且为 3 的倍数");

        _f = tris.Length / 3;
        _tv = new double[9 * _f];
        _ax = new double[_f]; _ay = new double[_f]; _az = new double[_f];
        _cx = new double[_f]; _cy = new double[_f]; _cz = new double[_f];
        _area = new double[_f];

        double minX = double.PositiveInfinity, minY = minX, minZ = minX;
        double maxX = double.NegativeInfinity, maxY = maxX, maxZ = maxX;

        for (int f = 0; f < _f; f++)
        {
            int i0 = tris[3 * f] * 3, i1 = tris[3 * f + 1] * 3, i2 = tris[3 * f + 2] * 3;
            double x0 = worldVerts[i0], y0 = worldVerts[i0 + 1], z0 = worldVerts[i0 + 2];
            double x1 = worldVerts[i1], y1 = worldVerts[i1 + 1], z1 = worldVerts[i1 + 2];
            double x2 = worldVerts[i2], y2 = worldVerts[i2 + 1], z2 = worldVerts[i2 + 2];

            int b = 9 * f;
            _tv[b] = x0; _tv[b + 1] = y0; _tv[b + 2] = z0;
            _tv[b + 3] = x1; _tv[b + 4] = y1; _tv[b + 5] = z1;
            _tv[b + 6] = x2; _tv[b + 7] = y2; _tv[b + 8] = z2;

            double e1x = x1 - x0, e1y = y1 - y0, e1z = z1 - z0;
            double e2x = x2 - x0, e2y = y2 - y0, e2z = z2 - z0;
            double cxv = e1y * e2z - e1z * e2y;
            double cyv = e1z * e2x - e1x * e2z;
            double czv = e1x * e2y - e1y * e2x;
            _ax[f] = cxv; _ay[f] = cyv; _az[f] = czv;
            _area[f] = 0.5 * Math.Sqrt(cxv * cxv + cyv * cyv + czv * czv);
            _cx[f] = (x0 + x1 + x2) / 3.0;
            _cy[f] = (y0 + y1 + y2) / 3.0;
            _cz[f] = (z0 + z1 + z2) / 3.0;

            if (x0 < minX) minX = x0; if (x0 > maxX) maxX = x0;
            if (y0 < minY) minY = y0; if (y0 > maxY) maxY = y0;
            if (z0 < minZ) minZ = z0; if (z0 > maxZ) maxZ = z0;
            if (x1 < minX) minX = x1; if (x1 > maxX) maxX = x1;
            if (y1 < minY) minY = y1; if (y1 > maxY) maxY = y1;
            if (z1 < minZ) minZ = z1; if (z1 > maxZ) maxZ = z1;
            if (x2 < minX) minX = x2; if (x2 > maxX) maxX = x2;
            if (y2 < minY) minY = y2; if (y2 > maxY) maxY = y2;
            if (z2 < minZ) minZ = z2; if (z2 > maxZ) maxZ = z2;
        }
        MinX = minX; MinY = minY; MinZ = minZ; MaxX = maxX; MaxY = maxY; MaxZ = maxZ;

        _order = new int[_f];
        for (int i = 0; i < _f; i++) _order[i] = i;
        _key = new double[_f];
        var build = new List<Node>(2 * (_f / LeafSize + 1));
        BuildNode(build, 0, _f);
        _nodes = build.ToArray();
        _nodeCount = _nodes.Length;
    }

    private int BuildNode(List<Node> nodes, int start, int count)
    {
        int ni = nodes.Count;
        nodes.Add(default);
        Node nd = default;
        double Ax = 0, Ay = 0, Az = 0, wsumX = 0, wsumY = 0, wsumZ = 0, areaSum = 0;
        double bminx = double.PositiveInfinity, bminy = bminx, bminz = bminx;
        double bmaxx = double.NegativeInfinity, bmaxy = bmaxx, bmaxz = bmaxx;
        for (int i = start; i < start + count; i++)
        {
            int t = _order[i];
            Ax += _ax[t]; Ay += _ay[t]; Az += _az[t];
            double a = _area[t]; areaSum += a;
            wsumX += a * _cx[t]; wsumY += a * _cy[t]; wsumZ += a * _cz[t];
            if (_cx[t] < bminx) bminx = _cx[t]; if (_cx[t] > bmaxx) bmaxx = _cx[t];
            if (_cy[t] < bminy) bminy = _cy[t]; if (_cy[t] > bmaxy) bmaxy = _cy[t];
            if (_cz[t] < bminz) bminz = _cz[t]; if (_cz[t] > bmaxz) bmaxz = _cz[t];
        }
        double cx, cy, cz;
        if (areaSum > 1e-300) { cx = wsumX / areaSum; cy = wsumY / areaSum; cz = wsumZ / areaSum; }
        else { cx = (bminx + bmaxx) * 0.5; cy = (bminy + bmaxy) * 0.5; cz = (bminz + bmaxz) * 0.5; }

        double r2 = 0;
        for (int i = start; i < start + count; i++)
        {
            int b = 9 * _order[i];
            for (int k = 0; k < 3; k++)
            {
                double dx = _tv[b + 3 * k] - cx, dy = _tv[b + 3 * k + 1] - cy, dz = _tv[b + 3 * k + 2] - cz;
                double d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > r2) r2 = d2;
            }
        }

        nd.Ax = Ax; nd.Ay = Ay; nd.Az = Az;
        nd.Cx = cx; nd.Cy = cy; nd.Cz = cz;
        nd.R = Math.Sqrt(r2);

        if (count <= LeafSize)
        {
            nd.Start = start; nd.Count = count;
            nd.Left = -1; nd.Right = -1;
            nodes[ni] = nd;
            return ni;
        }

        double ex = bmaxx - bminx, ey = bmaxy - bminy, ez = bmaxz - bminz;
        int axis = (ex >= ey && ex >= ez) ? 0 : (ey >= ez ? 1 : 2);
        for (int i = start; i < start + count; i++)
        {
            int tt = _order[i];
            _key[i] = axis == 0 ? _cx[tt] : axis == 1 ? _cy[tt] : _cz[tt];
        }
        Array.Sort(_key, _order, start, count);
        int mIdx = start + count / 2;

        int left = BuildNode(nodes, start, mIdx - start);
        int right = BuildNode(nodes, mIdx, start + count - mIdx);
        nd.Start = 0; nd.Count = 0;
        nd.Left = left; nd.Right = right;
        nodes[ni] = nd;
        return ni;
    }

    /// <summary>缠绕数 w(p)。w>0.5 视为内部。</summary>
    public double Winding(double px, double py, double pz)
    {
        if (_nodeCount == 0) return 0;
        double w = 0;
        Span<int> stack = stackalloc int[96];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            int ni = stack[--sp];
            ref Node nd = ref _nodes[ni];
            double dx = nd.Cx - px, dy = nd.Cy - py, dz = nd.Cz - pz;
            double d2 = dx * dx + dy * dy + dz * dz;
            double thr = Beta * nd.R;
            if (Beta > 0 && d2 > thr * thr)
            {
                double invD3 = 1.0 / (d2 * Math.Sqrt(d2));
                w += Inv8Pi * (nd.Ax * dx + nd.Ay * dy + nd.Az * dz) * invD3;
            }
            else if (nd.Left < 0)
            {
                int end = nd.Start + nd.Count;
                for (int i = nd.Start; i < end; i++)
                    w += Inv4Pi * TriSolidAngle(_order[i], px, py, pz);
            }
            else
            {
                stack[sp++] = nd.Left;
                stack[sp++] = nd.Right;
            }
        }
        return w;
    }

    /// <summary>点是否在封闭体内(盒外快速排除 + w>0.5)。</summary>
    public bool IsInsideClosed(double x, double y, double z)
    {
        if (x < MinX || x > MaxX || y < MinY || y > MaxY || z < MinZ || z > MaxZ) return false;
        return Winding(x, y, z) > 0.5;
    }

    private double TriSolidAngle(int f, double px, double py, double pz)
    {
        int b = 9 * f;
        double ax = _tv[b] - px, ay = _tv[b + 1] - py, az = _tv[b + 2] - pz;
        double bx = _tv[b + 3] - px, by = _tv[b + 4] - py, bz = _tv[b + 5] - pz;
        double cx = _tv[b + 6] - px, cy = _tv[b + 7] - py, cz = _tv[b + 8] - pz;

        double la = Math.Sqrt(ax * ax + ay * ay + az * az);
        double lb = Math.Sqrt(bx * bx + by * by + bz * bz);
        double lc = Math.Sqrt(cx * cx + cy * cy + cz * cz);
        if (la < 1e-300 || lb < 1e-300 || lc < 1e-300) return 0;

        double crx = by * cz - bz * cy;
        double cry = bz * cx - bx * cz;
        double crz = bx * cy - by * cx;
        double numer = ax * crx + ay * cry + az * crz;

        double ab = ax * bx + ay * by + az * bz;
        double bc = bx * cx + by * cy + bz * cz;
        double ca = cx * ax + cy * ay + cz * az;
        double denom = la * lb * lc + ab * lc + bc * la + ca * lb;

        return 2.0 * Math.Atan2(numer, denom);
    }
}
