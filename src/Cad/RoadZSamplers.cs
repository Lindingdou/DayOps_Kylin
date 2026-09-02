using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 地形 Z 采样器具体实现 —— 忠实移植原 PointCloudLib.RoadCenterline.MeshZSampler.cs 的
/// <see cref="MeshZSampler"/>(TIN 三角网重心插值) + <see cref="BenchZField"/>(台阶线 IDW 场) +
/// <see cref="CompositeZSampler"/>(组合)。给 <see cref="RoadNetworkConnector"/> 的可选 <see cref="IRoadZSampler"/>
/// 参数提供落地实现：把补充连接线/中心线铺贴到现状地形（有 TIN 用 TIN、无 TIN 退台阶线场）。
/// 原文件 RoadTinReader(从场景实体读最优 TIN)依赖 IEntityCapability 场景层，属 UI 铺装，此处不移。纯逻辑、可单测。
/// </summary>
public sealed class MeshZSampler : IRoadZSampler
{
    private readonly double[] _v;
    private readonly int[] _t;
    private readonly Dictionary<(int, int), List<int>> _grid = new();
    private readonly double _cs, _x0, _y0;

    private MeshZSampler(double[] v, int[] t, double cs, double x0, double y0)
    { _v = v; _t = t; _cs = cs; _x0 = x0; _y0 = y0; }

    /// <summary>建采样器；verts=[x,y,z,...](world)、tris=[i0,i1,i2,...]。无效返回 null。</summary>
    public static MeshZSampler? Build(double[] verts, int[] tris)
    {
        if (verts == null || tris == null || verts.Length < 9 || tris.Length < 3) return null;
        int nt = tris.Length / 3, nv = verts.Length / 3;
        double xmn = double.MaxValue, ymn = double.MaxValue, xmx = double.MinValue, ymx = double.MinValue;
        for (int i = 0; i < nv; i++)
        {
            double x = verts[3 * i], y = verts[3 * i + 1];
            if (x < xmn) xmn = x; if (x > xmx) xmx = x;
            if (y < ymn) ymn = y; if (y > ymx) ymx = y;
        }
        if (xmx <= xmn || ymx <= ymn) return null;

        // 格尺寸 ≈ 平均三角尺度（每三角注册到其包围盒覆盖的格 → 含点的三角必在查询格里）
        double cs = Math.Max(1.0, ((xmx - xmn) + (ymx - ymn)) * 0.5 / Math.Max(1.0, Math.Sqrt(nt)));
        var s = new MeshZSampler(verts, tris, cs, xmn, ymn);
        for (int t = 0; t < nt; t++)
        {
            int i0 = tris[3 * t], i1 = tris[3 * t + 1], i2 = tris[3 * t + 2];
            if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= nv || i1 >= nv || i2 >= nv) continue;
            double ax = verts[3 * i0], ay = verts[3 * i0 + 1];
            double bx = verts[3 * i1], by = verts[3 * i1 + 1];
            double cx = verts[3 * i2], cy = verts[3 * i2 + 1];
            int gx0 = (int)Math.Floor((Math.Min(ax, Math.Min(bx, cx)) - xmn) / cs);
            int gx1 = (int)Math.Floor((Math.Max(ax, Math.Max(bx, cx)) - xmn) / cs);
            int gy0 = (int)Math.Floor((Math.Min(ay, Math.Min(by, cy)) - ymn) / cs);
            int gy1 = (int)Math.Floor((Math.Max(ay, Math.Max(by, cy)) - ymn) / cs);
            for (int gx = gx0; gx <= gx1; gx++)
                for (int gy = gy0; gy <= gy1; gy++)
                {
                    var k = (gx, gy);
                    if (!s._grid.TryGetValue(k, out var l)) { l = new List<int>(); s._grid[k] = l; }
                    l.Add(t);
                }
        }
        return s;
    }

    /// <summary>采 (px,py) 处地形 Z；命中三角=true（重心插值），否则 false。</summary>
    public bool TrySample(double px, double py, out double z)
    {
        z = 0;
        int gx = (int)Math.Floor((px - _x0) / _cs), gy = (int)Math.Floor((py - _y0) / _cs);
        if (!_grid.TryGetValue((gx, gy), out var l)) return false;
        foreach (int t in l)
        {
            int i0 = _t[3 * t], i1 = _t[3 * t + 1], i2 = _t[3 * t + 2];
            double ax = _v[3 * i0], ay = _v[3 * i0 + 1], az = _v[3 * i0 + 2];
            double bx = _v[3 * i1], by = _v[3 * i1 + 1], bz = _v[3 * i1 + 2];
            double cx = _v[3 * i2], cy = _v[3 * i2 + 1], cz = _v[3 * i2 + 2];
            double d00 = bx - ax, d01 = cx - ax, d10 = by - ay, d11 = cy - ay;
            double den = d00 * d11 - d01 * d10;
            if (Math.Abs(den) < 1e-12) continue;
            double rx = px - ax, ry = py - ay;
            double u = (d11 * rx - d01 * ry) / den;     // 权重(B-A)
            double w = (d00 * ry - d10 * rx) / den;     // 权重(C-A)
            if (u < -1e-6 || w < -1e-6 || u + w > 1 + 1e-6) continue;
            z = az + u * (bz - az) + w * (cz - az);
            return true;
        }
        return false;
    }
}

/// <summary>
/// 台阶线地形 Z 场：没有 TIN 时用台阶线（坡顶/坡底/台阶边线）插值出现状地形标高。
/// 把台阶线沿程加密成稠密点 → 空间哈希 → 反距离加权(IDW)采样：落在某条台阶上≈该台阶高，
/// 落在相邻两台阶之间(坡面/坡道)则平滑过渡到中间标高 →「按台阶特征平滑对接」。忠实原 BenchZField。
/// </summary>
public sealed class BenchZField : IRoadZSampler
{
    private readonly List<double> _px = new(), _py = new(), _pz = new();
    private readonly Dictionary<(int, int), List<int>> _grid = new();
    private readonly double _cs, _r2;

    private BenchZField(double cs, double radius) { _cs = cs; _r2 = radius * radius; }

    private void Add(double x, double y, double z)
    {
        int i = _px.Count; _px.Add(x); _py.Add(y); _pz.Add(z);
        var k = ((int)Math.Floor(x / _cs), (int)Math.Floor(y / _cs));
        if (!_grid.TryGetValue(k, out var l)) { l = new List<int>(); _grid[k] = l; }
        l.Add(i);
    }

    /// <param name="benchLines">台阶线集合，每条 = [x,y,z, x,y,z, ...] 世界坐标。</param>
    /// <param name="sampleSpacing">台阶线加密步距 (m)。</param>
    /// <param name="radius">IDW 影响半径 (m)：取够大以覆盖相邻台阶（≈台阶宽+坡面），坡面才有上下台阶共同插值。</param>
    public static BenchZField? Build(IReadOnlyList<double[]> benchLines, double sampleSpacing = 5.0, double radius = 60.0)
    {
        if (benchLines == null || benchLines.Count == 0) return null;
        var f = new BenchZField(Math.Max(radius, 1.0), radius);
        foreach (var ln in benchLines)
        {
            if (ln == null) continue;
            int n = ln.Length / 3;
            for (int s = 0; s + 1 < n; s++)
            {
                double ax = ln[3 * s], ay = ln[3 * s + 1], az = ln[3 * s + 2];
                double bx = ln[3 * s + 3], by = ln[3 * s + 4], bz = ln[3 * s + 5];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                int steps = Math.Max(1, (int)(len / Math.Max(0.5, sampleSpacing)));
                for (int k = 0; k <= steps; k++)
                {
                    double t = (double)k / steps;
                    f.Add(ax + (bx - ax) * t, ay + (by - ay) * t, az + (bz - az) * t);
                }
            }
        }
        return f._px.Count > 0 ? f : null;
    }

    public bool TrySample(double x, double y, out double z)
    {
        z = 0;
        int cx = (int)Math.Floor(x / _cs), cy = (int)Math.Floor(y / _cs);
        double wsum = 0, zsum = 0, bestD2 = double.MaxValue, bestZ = 0; int cnt = 0;
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (_grid.TryGetValue((cx + dx, cy + dy), out var l))
                    foreach (int i in l)
                    {
                        double ddx = _px[i] - x, ddy = _py[i] - y, d2 = ddx * ddx + ddy * ddy;
                        if (d2 > _r2) continue;
                        if (d2 < bestD2) { bestD2 = d2; bestZ = _pz[i]; }
                        double w = 1.0 / (d2 + 1e-6);
                        wsum += w; zsum += w * _pz[i]; cnt++;
                    }
        if (cnt == 0) return false;
        z = bestD2 < 1e-6 ? bestZ : zsum / wsum;   // 正落在某点上=取其高，否则 IDW
        return true;
    }
}

/// <summary>组合采样器：按序尝试（TIN 优先，落空退台阶线场）。忠实原 CompositeZSampler。</summary>
public sealed class CompositeZSampler : IRoadZSampler
{
    private readonly IRoadZSampler?[] _s;
    public CompositeZSampler(params IRoadZSampler?[] s) { _s = s; }
    public bool TrySample(double x, double y, out double z)
    {
        foreach (var s in _s) if (s != null && s.TrySample(x, y, out z)) return true;
        z = 0; return false;
    }
}

/// <summary>统一构建地形采样器：有 TIN 用 TIN、其余/落空用台阶线场；都无则 null。忠实原 RoadTerrain.BuildSampler。</summary>
public static class RoadTerrainSampler
{
    public static IRoadZSampler? BuildSampler(double[] meshVerts, int[] meshTris, IReadOnlyList<double[]> benchLines)
    {
        var m = MeshZSampler.Build(meshVerts, meshTris);
        var b = BenchZField.Build(benchLines);
        if (m != null && b != null) return new CompositeZSampler(m, b);
        if (m != null) return m;
        return b;
    }
}
