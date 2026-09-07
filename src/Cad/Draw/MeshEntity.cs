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
    public void Invalidate() { _edges = null; _boundsOk = false; _edgeCache = null; _faceCache = null; }

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

    // ── 显示模式(全局, 原「渲染配置」实体/线框着色管线)：线框 / 着色面 / 着色面+线框 ──
    public enum DisplayMode { Wireframe, Shaded, ShadedWireframe }
    public static DisplayMode RenderMode = DisplayMode.Shaded;   // 默认直接显示面(用户 2026-09-07: 默认不显示网格线)
    /// <summary>面按高程着色(地形色带), 否则按实体颜色。</summary>
    public static bool ColorByElevation;
    /// <summary>平行光方向(世界系, 单位化)；两面受光。</summary>
    private static readonly (double x, double y, double z) Light = Normalize((0.35, 0.25, 0.9));
    private static (double x, double y, double z) Normalize((double x, double y, double z) v)
    { double l = Math.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z); return l < 1e-12 ? (0, 0, 1) : (v.x / l, v.y / l, v.z / l); }

    /// <summary>边线(不看显示模式, 供选择高亮/导出)。</summary>
    public void TessellateEdges(List<float> o)
    {
        foreach (var (i, j) in Edges)
        {
            var p = Verts[i]; var q = Verts[j];
            Seg3(o, p.x, p.y, p.z + Elevation, q.x, q.y, q.z + Elevation);
        }
    }

    // 镶嵌缓存：大网(数万三角)每次 RefreshScene 都重算边线/着色面太慢；按 (模式, 高程着色, 颜色, 标高) 键缓存, 几何改动 Invalidate() 清。
    private float[]? _edgeCache, _faceCache;
    private (DisplayMode mode, bool byElev, float r, float g, float b, double elev) _edgeKey, _faceKey;

    public override void Tessellate(List<float> o)
    {
        if (RenderMode == DisplayMode.Shaded) return;   // 纯着色面模式不画边线
        var key = (RenderMode, ColorByElevation, Cr, Cg, Cb, Elevation);
        if (_edgeCache == null || _edgeKey != key)
        {
            var tmp = new List<float>(Edges.Count * 12);
            if (RenderMode == DisplayMode.ShadedWireframe)
            {
                // 面+线框：边线压暗, 与着色面区分
                float cr = Cr, cg = Cg, cb = Cb;
                Cr *= 0.45f; Cg *= 0.45f; Cb *= 0.45f;
                try { TessellateEdges(tmp); } finally { Cr = cr; Cg = cg; Cb = cb; }
            }
            else TessellateEdges(tmp);
            _edgeCache = tmp.ToArray(); _edgeKey = key;
        }
        o.AddRange(_edgeCache);
    }

    /// <summary>着色三角面：逐三角平面法线 × 平行光(两面受光) 调制基色(或高程色带)。</summary>
    public override void TessellateFaces(List<float> o)
    {
        if (RenderMode == DisplayMode.Wireframe) return;
        var key = (RenderMode, ColorByElevation, Cr, Cg, Cb, Elevation);
        if (_faceCache != null && _faceKey == key) { o.AddRange(_faceCache); return; }
        var tmp = new List<float>(Tris.Count * 18);
        BuildFaces(tmp);
        _faceCache = tmp.ToArray(); _faceKey = key;
        o.AddRange(_faceCache);
    }

    private void BuildFaces(List<float> o)
    {
        var b = Bounds; double zr = b.maxZ - b.minZ;
        foreach (var (a, bb, c) in Tris)
        {
            if (a >= Verts.Count || bb >= Verts.Count || c >= Verts.Count) continue;
            var p = Verts[a]; var q = Verts[bb]; var r = Verts[c];
            double ux = q.x - p.x, uy = q.y - p.y, uz = q.z - p.z, vx = r.x - p.x, vy = r.y - p.y, vz = r.z - p.z;
            var n = Normalize((uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx));
            double lambert = Math.Abs(n.x * Light.x + n.y * Light.y + n.z * Light.z);
            float k = (float)(0.42 + 0.58 * lambert);
            void V((double x, double y, double z) w)
            {
                float cr = Cr, cg = Cg, cb = Cb;
                if (ColorByElevation) { var t = zr > 1e-9 ? (w.z - b.minZ) / zr : 0.5; (cr, cg, cb) = TerrainRamp(t); }
                o.Add((float)w.x); o.Add((float)w.y); o.Add((float)(w.z + Elevation));
                o.Add(Math.Min(1f, cr * k)); o.Add(Math.Min(1f, cg * k)); o.Add(Math.Min(1f, cb * k));
            }
            V(p); V(q); V(r);
        }
    }

    /// <summary>地形色带：低=绿 → 黄 → 棕 → 高=白。</summary>
    public static (float r, float g, float b) TerrainRamp(double t)
    {
        t = Math.Clamp(t, 0, 1);
        (float, float, float)[] stops = { (0.20f, 0.55f, 0.25f), (0.85f, 0.85f, 0.35f), (0.65f, 0.45f, 0.25f), (0.95f, 0.95f, 0.95f) };
        double s = t * (stops.Length - 1); int i = Math.Min(stops.Length - 2, (int)Math.Floor(s)); float f = (float)(s - i);
        var (r0, g0, b0) = stops[i]; var (r1, g1, b1) = stops[i + 1];
        return (r0 + (r1 - r0) * f, g0 + (g1 - g0) * f, b0 + (b1 - b0) * f);
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
