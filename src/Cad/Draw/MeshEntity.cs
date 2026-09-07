using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 三角网实体（原 MeshEditLib 的 MeshData / 内核 MeshEntity 的托管等价）：顶点(x,y,z) + 三角索引，
/// 作为场景一等对象参与 渲染(唯一边线, 逐顶点真高程)/拾取/选择/图层/隐藏/存档/特性，
/// 供 建模·编辑·剖面·估值·块体 等三维地质建模功能直接以「选中的三角网」为输入输出，不再经 OFF 文件中转。
/// </summary>
public sealed class MeshEntity : SceneEntity
{
    public string Name = "三角网";
    public List<(double x, double y, double z)> Verts = new();
    public List<(int a, int b, int c)> Tris = new();

    private List<(int i, int j)>? _edges;
    private double _minX, _minY, _maxX, _maxY, _minZ, _maxZ;
    private bool _boundsOk;

    public MeshEntity() { Cr = 0.55f; Cg = 0.75f; Cb = 0.85f; }

    public MeshEntity(string name, IEnumerable<(double x, double y, double z)> verts, IEnumerable<(int a, int b, int c)> tris) : this()
    {
        Name = name;
        Verts.AddRange(verts);
        Tris.AddRange(tris);
    }

    /// <summary>顶点/三角改动后调用，重建边集与包围盒缓存。</summary>
    public void Invalidate() { _edges = null; _boundsOk = false; }

    /// <summary>唯一无向边(i&lt;j)，按三角遍历去重。</summary>
    public IReadOnlyList<(int i, int j)> Edges
    {
        get
        {
            if (_edges != null) return _edges;
            var set = new HashSet<long>();
            var list = new List<(int, int)>(Tris.Count * 2);
            void Add(int a, int b)
            {
                if (a == b || a < 0 || b < 0 || a >= Verts.Count || b >= Verts.Count) return;
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                if (set.Add(((long)lo << 32) | (uint)hi)) list.Add((lo, hi));
            }
            foreach (var (a, b, c) in Tris) { Add(a, b); Add(b, c); Add(c, a); }
            _edges = list;
            return list;
        }
    }

    public (double minX, double minY, double maxX, double maxY, double minZ, double maxZ) Bounds
    {
        get
        {
            if (!_boundsOk)
            {
                _minX = _minY = _minZ = double.MaxValue; _maxX = _maxY = _maxZ = double.MinValue;
                foreach (var (x, y, z) in Verts)
                {
                    if (x < _minX) _minX = x; if (x > _maxX) _maxX = x;
                    if (y < _minY) _minY = y; if (y > _maxY) _maxY = y;
                    if (z < _minZ) _minZ = z; if (z > _maxZ) _maxZ = z;
                }
                if (Verts.Count == 0) _minX = _minY = _minZ = _maxX = _maxY = _maxZ = 0;
                _boundsOk = true;
            }
            return (_minX, _minY, _maxX, _maxY, _minZ, _maxZ);
        }
    }

    public int VertexCount => Verts.Count;
    public int TriangleCount => Tris.Count;

    /// <summary>扁平副本(x,y,z…)与(a,b,c…)，供以 double[]/int[] 为参数的算法(BenchFaceExtractor/SurfaceUpdate/MeshVoxelizer…)。</summary>
    public (double[] verts, int[] tris) Flatten()
    {
        var v = new double[Verts.Count * 3]; var t = new int[Tris.Count * 3];
        for (int i = 0; i < Verts.Count; i++) { v[i * 3] = Verts[i].x; v[i * 3 + 1] = Verts[i].y; v[i * 3 + 2] = Verts[i].z; }
        for (int i = 0; i < Tris.Count; i++) { t[i * 3] = Tris[i].a; t[i * 3 + 1] = Tris[i].b; t[i * 3 + 2] = Tris[i].c; }
        return (v, t);
    }

    public override void Tessellate(List<float> o)
    {
        foreach (var (i, j) in Edges)
        {
            var p = Verts[i]; var q = Verts[j];
            Seg3(o, p.x, p.y, p.z + Elevation, q.x, q.y, q.z + Elevation);
        }
    }

    /// <summary>拾取距离：先包围盒粗排斥(避免大网逐边计算)，再逐边 2D 距离取最小。</summary>
    public override double DistanceTo(double px, double py)
    {
        var b = Bounds;
        double outside = 0;
        if (px < b.minX) outside = Math.Max(outside, b.minX - px);
        if (px > b.maxX) outside = Math.Max(outside, px - b.maxX);
        if (py < b.minY) outside = Math.Max(outside, b.minY - py);
        if (py > b.maxY) outside = Math.Max(outside, py - b.maxY);
        // 包围盒外：真距离 ≥ outside；用 outside 作下界即可(超容差就落选)
        if (outside > 0) return outside + 1e9;   // 明确落选(不做逐边), 避免误选大网外围
        double best = double.MaxValue;
        foreach (var (i, j) in Edges)
        {
            var p = Verts[i]; var q = Verts[j];
            double d = SegDist(px, py, p.x, p.y, q.x, q.y);
            if (d < best) best = d;
        }
        return best;
    }

    public override SceneEntity Apply(Affine2 m)
    {
        var me = new MeshEntity { Name = Name };
        foreach (var (x, y, z) in Verts) { var (nx, ny) = m.Map(x, y); me.Verts.Add((nx, ny, z)); }
        me.Tris.AddRange(Tris);
        return Colored(me);
    }

    /// <summary>分解 → 每条边一条直线(标高取两端均值)。</summary>
    public override List<SceneEntity>? Explode()
    {
        if (Edges.Count == 0) return null;
        var list = new List<SceneEntity>(Edges.Count);
        foreach (var (i, j) in Edges)
        {
            var p = Verts[i]; var q = Verts[j];
            var l = Colored(new LineEntity { X0 = p.x, Y0 = p.y, X1 = q.x, Y1 = q.y });
            l.Elevation = (p.z + q.z) / 2 + Elevation;
            list.Add(l);
        }
        return list;
    }

    /// <summary>三角网无夹点(顶点编辑走 修改高程点/焊接 等命令)。</summary>
    public override List<(double x, double y)> Grips() => new();

    /// <summary>深拷贝(几何 + 样式)。</summary>
    public MeshEntity Clone()
    {
        var me = new MeshEntity(Name, Verts, Tris);
        me.CopyStyleFrom(this);
        return me;
    }

    /// <summary>三角总面积(三维真面积)。</summary>
    public double SurfaceArea()
    {
        double s = 0;
        foreach (var (a, b, c) in Tris)
        {
            if (a >= Verts.Count || b >= Verts.Count || c >= Verts.Count) continue;
            var p = Verts[a]; var q = Verts[b]; var r = Verts[c];
            double ux = q.x - p.x, uy = q.y - p.y, uz = q.z - p.z, vx = r.x - p.x, vy = r.y - p.y, vz = r.z - p.z;
            double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
            s += Math.Sqrt(cx * cx + cy * cy + cz * cz) / 2;
        }
        return s;
    }
}
