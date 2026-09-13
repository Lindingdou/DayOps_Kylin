using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 点云数据集实体（原 PointCloudLib 的 native 点云数据集 + PointCloudInfo 的托管等价）：
/// 一个实体 = 一份点云，作场景一等对象参与 渲染(GL_POINTS)/拾取/选择/图层/显隐/存档/特性。
///
/// 为什么必须是「一个实体一份点云」而不是原来那样每点一个 <see cref="PointEntity"/>：
/// 原版所有点云算子都是非破坏式的 —— 每跑一次就往场景里追加一份新点云(地面点/去噪结果/着色副本…)，
/// 靠「点云管理」把它们分开、切当前、单独显隐/删除，抽稀→去噪→滤波→建面才串得起来。
/// 散成十万个点实体后这些点云彼此无从区分，也没法整份显隐/整份删除，链式操作无从谈起。
/// </summary>
public sealed class PointCloudEntity : SceneEntity
{
    /// <summary>数据集名（点云管理里显示、可重命名）。算子产物按「源名·算子」命名，便于回溯来路。</summary>
    public string Name = "点云";

    public List<(double x, double y, double z)> Pts = new();

    /// <summary>逐点色（真实色 RGB / 着色算子产物）；null = 全份用实体基色 Cr/Cg/Cb。</summary>
    public List<(float r, float g, float b)>? Colors;

    /// <summary>
    /// 逐点法向缓存（原版写在点云旁的 normals.bin sidecar 的托管等价）。
    /// 一次 kNN PCA 是分钟级的，坡度/坡向/曲率三个分析共用同一份，故缓存在数据集上。
    /// </summary>
    public List<(double x, double y, double z)>? Normals;

    /// <summary>
    /// 真实色（导入时文件自带的 RGB）。<see cref="Colors"/> 是"当前显示用的色"，会被着色算子覆盖；
    /// 这一份留着，「点云着色 → 恢复真实颜色 (RGB)」才有得可恢复。
    /// </summary>
    public List<(float r, float g, float b)>? RgbColors;

    /// <summary>源文件路径（导入的点云有；算子产物为 null）。</summary>
    public string? Source;

    /// <summary>
    /// 源文件里的总点数（LAS 头声明；0 = 未知/未抽稀）。加载点云为了显示封顶 200 万均匀抽样，
    /// 场景里这份不是全量；要"按原版在全量点云上算"的算子（坡顶底线提取）靠它判断该不该回源文件重读全量。
    /// </summary>
    public long SourceTotalPoints;

    /// <summary>屏幕像素点径（原版点云渲染的 point size）。</summary>
    public float PointPixels = 2f;

    private double _minX, _minY, _maxX, _maxY, _minZ, _maxZ;
    private bool _boundsOk;
    private float[]? _cache;
    private (float r, float g, float b, double elev, int n) _cacheKey;

    public PointCloudEntity() { Cr = 0.75f; Cg = 0.78f; Cb = 0.82f; }

    public PointCloudEntity(string name, IEnumerable<(double x, double y, double z)> pts) : this()
    {
        Name = name;
        Pts.AddRange(pts);
    }

    public int PointCount => Pts.Count;
    public bool HasColors => Colors != null && Colors.Count == Pts.Count;
    public bool HasRgb => RgbColors != null && RgbColors.Count == Pts.Count;
    public bool HasNormals => Normals != null && Normals.Count == Pts.Count;

    /// <summary>点/色改动后调用：清包围盒与镶嵌缓存。</summary>
    public void Invalidate() { _boundsOk = false; _cache = null; }

    public (double minX, double minY, double maxX, double maxY, double minZ, double maxZ) Bounds
    {
        get
        {
            if (!_boundsOk)
            {
                _minX = _minY = _minZ = double.MaxValue; _maxX = _maxY = _maxZ = double.MinValue;
                foreach (var (x, y, z) in Pts)
                {
                    if (x < _minX) _minX = x; if (x > _maxX) _maxX = x;
                    if (y < _minY) _minY = y; if (y > _maxY) _maxY = y;
                    if (z < _minZ) _minZ = z; if (z > _maxZ) _maxZ = z;
                }
                if (Pts.Count == 0) _minX = _minY = _minZ = _maxX = _maxY = _maxZ = 0;
                _boundsOk = true;
            }
            return (_minX, _minY, _maxX, _maxY, _minZ, _maxZ);
        }
    }

    /// <summary>缩放到本点云用的 [minX,minY,maxX,maxY]。</summary>
    public double[] Bounds2D { get { var b = Bounds; return new[] { b.minX, b.minY, b.maxX, b.maxY }; } }

    /// <summary>点云不进线段通道（点走 <see cref="TessellatePoints"/> 的 GL_POINTS 专用通道）。</summary>
    public override void Tessellate(List<float> o) { }

    /// <summary>逐点 P3_C3 顶点（GL_POINTS）。按 (基色,标高,点数) 缓存，大点云不必每帧重建。</summary>
    public void TessellatePoints(List<float> o)
    {
        var key = (Cr, Cg, Cb, Elevation, Pts.Count);
        if (_cache == null || _cacheKey != key)
        {
            var t = new float[Pts.Count * 6];
            bool per = HasColors;
            for (int i = 0; i < Pts.Count; i++)
            {
                var p = Pts[i];
                float r = Cr, g = Cg, b = Cb;
                if (per) (r, g, b) = Colors![i];
                int k = i * 6;
                t[k] = (float)p.x; t[k + 1] = (float)p.y; t[k + 2] = (float)(p.z + Elevation);
                t[k + 3] = r; t[k + 4] = g; t[k + 5] = b;
            }
            _cache = t; _cacheKey = key;
        }
        o.AddRange(_cache);
    }

    /// <summary>三维包围盒线框（选中高亮用：几十万个点逐点重着色既慢又看不出"选中"）。</summary>
    public void TessellateBoundsBox(List<float> o)
    {
        var b = Bounds;
        double z0 = b.minZ + Elevation, z1 = b.maxZ + Elevation;
        if (Pts.Count == 0) return;
        if (z1 - z0 < 1e-9) z1 = z0 + 1e-9;
        void E(double x0, double y0, double zz0, double x1, double y1, double zz1) => Seg3(o, x0, y0, zz0, x1, y1, zz1);
        foreach (double z in new[] { z0, z1 })
        {
            E(b.minX, b.minY, z, b.maxX, b.minY, z);
            E(b.maxX, b.minY, z, b.maxX, b.maxY, z);
            E(b.maxX, b.maxY, z, b.minX, b.maxY, z);
            E(b.minX, b.maxY, z, b.minX, b.minY, z);
        }
        E(b.minX, b.minY, z0, b.minX, b.minY, z1);
        E(b.maxX, b.minY, z0, b.maxX, b.minY, z1);
        E(b.maxX, b.maxY, z0, b.maxX, b.maxY, z1);
        E(b.minX, b.maxY, z0, b.minX, b.maxY, z1);
    }

    /// <summary>
    /// 拾取距离：先包围盒粗排斥，再逐点 2D 距离取最小（大点云按步长抽样，
    /// 一次点选不该为百万点做全量扫描 —— 拾取只要"够近"，抽样点足以判定）。
    /// </summary>
    public override double DistanceTo(double px, double py)
    {
        if (Pts.Count == 0) return double.MaxValue;
        var b = Bounds;
        double outside = 0;
        if (px < b.minX) outside = Math.Max(outside, b.minX - px);
        if (px > b.maxX) outside = Math.Max(outside, px - b.maxX);
        if (py < b.minY) outside = Math.Max(outside, b.minY - py);
        if (py > b.maxY) outside = Math.Max(outside, py - b.maxY);
        if (outside > 0) return outside + 1e9;   // 盒外明确落选(同三角网口径)
        int step = PickStride(Pts.Count);
        double best = double.MaxValue;
        for (int i = 0; i < Pts.Count; i += step)
        {
            double dx = Pts[i].x - px, dy = Pts[i].y - py;
            double d = dx * dx + dy * dy;
            if (d < best) best = d;
        }
        return Math.Sqrt(best);
    }

    /// <summary>拾取/框选抽样步长：至多测 ~20000 个点。</summary>
    public static int PickStride(int n) => n <= 20000 ? 1 : n / 20000 + 1;

    public override SceneEntity Apply(Affine2 m)
    {
        var pc = new PointCloudEntity { Name = Name, Source = Source, PointPixels = PointPixels };
        foreach (var (x, y, z) in Pts) { var (nx, ny) = m.Map(x, y); pc.Pts.Add((nx, ny, z)); }
        if (HasColors) pc.Colors = new List<(float, float, float)>(Colors!);
        return Colored(pc);
    }

    /// <summary>分解 → 每点一个点实体（供逐点编辑；百万点时慎用，与原版"点云不参与实体编辑"一致由调用方把关）。</summary>
    public override List<SceneEntity>? Explode()
    {
        if (Pts.Count == 0) return null;
        var list = new List<SceneEntity>(Pts.Count);
        bool per = HasColors;
        for (int i = 0; i < Pts.Count; i++)
        {
            var p = Pts[i];
            var pe = Colored(new PointEntity { X = p.x, Y = p.y });
            pe.Elevation = p.z + Elevation;
            if (per) { var c = Colors![i]; pe.Cr = c.r; pe.Cg = c.g; pe.Cb = c.b; }
            list.Add(pe);
        }
        return list;
    }

    /// <summary>点云无夹点（编辑走各点云算子，不逐点拖）。</summary>
    public override List<(double x, double y)> Grips() => new();

    /// <summary>深拷贝（几何 + 逐点色 + 法向缓存 + 样式）。</summary>
    public PointCloudEntity Clone()
    {
        var pc = new PointCloudEntity(Name, Pts) { Source = Source, SourceTotalPoints = SourceTotalPoints, PointPixels = PointPixels };
        if (Colors != null) pc.Colors = new List<(float, float, float)>(Colors);
        if (RgbColors != null) pc.RgbColors = new List<(float, float, float)>(RgbColors);
        if (Normals != null) pc.Normals = new List<(double, double, double)>(Normals);
        pc.CopyStyleFrom(this);
        return pc;
    }

    /// <summary>把逐点色整体设为单色（原「点云着色·单色」）：清逐点色 + 改基色。</summary>
    public void SetSolidColor(float r, float g, float b)
    {
        Colors = null; Cr = r; Cg = g; Cb = b; Invalidate();
    }
}
