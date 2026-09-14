using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Draw;

/// <summary>
/// 图案填充实体 —— 填充是**一个整体对象**(同原版 Hatch/AcDbHatch): 存的是「边界 + 图案名 + 比例 + 角度 + 颜色」,
/// 图案线是照这些参数**算出来的**, 不是一堆散线。于是:
///   · 点一下选中的是整块填充(而不是其中一根线)
///   · 移动/旋转/缩放/镜像/删除 整块走
///   · 「编辑填充」可随时改图案/比例/角度/颜色, 改完就地重算
///   · 拖边界夹点改形状, 图案跟着重新铺满
/// 与原版一致的还有「分解」: 炸开才变成一根根线。
///
/// SOLID(实心)走着色面通道铺三角; 其余图案走线通道。纯逻辑、可单测。
/// </summary>
public sealed class HatchEntity : SceneEntity
{
    /// <summary>闭合边界(不重复首点)。</summary>
    public List<(double x, double y)> Boundary = new();

    /// <summary>图案名(见 <see cref="HatchPatternLibrary"/>): SOLID / USER / ANSI31 / BRICK …</summary>
    public string PatternName = "ANSI31";

    /// <summary>命名图案 = 缩放倍数(越大越疏); 用户定义(USER) = 剖面线间距(世界单位)。</summary>
    public double Scale = 1;

    /// <summary>图案旋转角(度), 叠加在图案自带角度上; 用户定义时就是剖面线角度。</summary>
    public double Angle;

    /// <summary>用户定义图案: 是否再加一组正交线(十字交叉)。</summary>
    public bool Cross;

    /// <summary>
    /// 图案原点(世界坐标): 图案网格从这里起算, 且**随实体一起走**。
    /// 不锚在实体上的话, 填充一移动图案就相对边界错开、线数都会变(同一块填充搬个家就换了个样)。
    /// 对应 AutoCAD 的填充原点(HPORIGIN)。
    /// </summary>
    public double OriginX, OriginY;

    // 算出来的图案线缓存: 参数/边界没变就不重算(拖拽、每帧镶嵌都会问它要)
    private List<(double x1, double y1, double x2, double y2)>? _lines;
    private (string pat, double sc, double ang, bool cross, int n, double x0, double y0) _key;

    /// <summary>参数或边界改了以后调一次, 下次镶嵌重算图案。</summary>
    public void Invalidate() => _lines = null;

    private (string, double, double, bool, int, double, double) Key()
        => (PatternName, Scale, Angle, Cross, Boundary.Count,
            (Boundary.Count > 0 ? Boundary[0].x : 0) + OriginX,
            (Boundary.Count > 0 ? Boundary[0].y : 0) + OriginY);

    /// <summary>当前参数下的图案线段(世界坐标)。实心图案返回空表(它走面通道)。</summary>
    public IReadOnlyList<(double x1, double y1, double x2, double y2)> Lines()
    {
        var k = Key();
        if (_lines != null && _key.Equals(k)) return _lines;
        _key = k;
        _lines = Build();
        return _lines;
    }

    private List<(double x1, double y1, double x2, double y2)> Build()
    {
        var pat = HatchPatternLibrary.ByName(PatternName);
        if (Boundary.Count < 3 || pat == null || pat.IsSolid)
            return new List<(double, double, double, double)>();
        // 图案锚在实体自己的原点上: 先把边界平移到原点系里生成, 再把线搬回世界系。
        var local = new List<(double x, double y)>(Boundary.Count);
        foreach (var p in Boundary) local.Add((p.x - OriginX, p.y - OriginY));
        var lines = pat.IsUserDefined
            ? HatchPattern.Generate(local, Angle, Scale > 1e-12 ? Scale : AutoSpacing(), Cross)
            : HatchPatternLibrary.Generate(pat, local, Scale > 1e-12 ? Scale : AutoScale(pat), Angle);
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            lines[i] = (l.x1 + OriginX, l.y1 + OriginY, l.x2 + OriginX, l.y2 + OriginY);
        }
        return lines;
    }

    /// <summary>把图案原点放到边界左下角 —— 新建填充时调一次。</summary>
    public void AnchorOriginToBoundary()
    {
        if (Boundary.Count == 0) return;
        double minX = double.MaxValue, minY = double.MaxValue;
        foreach (var v in Boundary) { if (v.x < minX) minX = v.x; if (v.y < minY) minY = v.y; }
        OriginX = minX; OriginY = minY;
        Invalidate();
    }

    /// <summary>边界包围盒对角线 —— 比例/间距没给时按它自动取(约 24 条线)。</summary>
    public double Diagonal()
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var v in Boundary)
        { if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x; if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y; }
        return maxX <= minX && maxY <= minY ? 0 : Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY));
    }

    private double AutoSpacing() => Math.Max(Diagonal() / 24.0, 1e-6);

    private double AutoScale(HatchPatternLibrary.Pattern pat)
        => Math.Max(Diagonal() / 24.0 / HatchPatternLibrary.BaseSpacing(pat), 1e-9);

    /// <summary>实心填充: 边界三角剖分(质心在内的三角才留), 供着色面通道。</summary>
    public List<(int a, int b, int c)> SolidTris()
        => Boundary.Count >= 3 ? Delaunay.TriangulateClipped(Boundary, Boundary) : new List<(int, int, int)>();

    public bool IsSolid => HatchPatternLibrary.ByName(PatternName)?.IsSolid == true;

    public override void Tessellate(List<float> o)
    {
        foreach (var (x1, y1, x2, y2) in Lines()) Seg(o, x1, y1, x2, y2);
    }

    public override void TessellateFaces(List<float> o)
    {
        if (!IsSolid) return;
        var tris = SolidTris();
        double ox = RenderOrigin.X, oy = RenderOrigin.Y;
        float z = (float)Elevation;
        foreach (var (a, b, c) in tris)
        {
            foreach (int i in stackalloc[] { a, b, c })
            {
                var p = Boundary[i];
                o.Add((float)(p.x - ox)); o.Add((float)(p.y - oy)); o.Add(z);
                o.Add(Cr); o.Add(Cg); o.Add(Cb);
            }
        }
    }

    /// <summary>实心填充线通道不出几何 —— 拖拽幽灵改用边界, 否则拖着走一路看不见东西。</summary>
    public override void TessellatePreview(List<float> o)
    {
        if (!IsSolid) { Tessellate(o); return; }
        for (int i = 0; i < Boundary.Count; i++)
        {
            var a = Boundary[i]; var b = Boundary[(i + 1) % Boundary.Count];
            Seg(o, a.x, a.y, b.x, b.y);
        }
    }

    // O(1): 拿缓存条数(还没算过就给个常数), 不许在这里遍历几何
    public override int PreviewCost() => _lines != null ? Math.Max(64, _lines.Count * 2) : 512;

    /// <summary>拖拽替身用边界环(说得清"这块填充在哪、多大"), 比几千根图案线便宜得多。</summary>
    public override void BuildPreviewProxy(List<(double x, double y, double z)> o, int maxSegments)
    {
        if (Boundary.Count < 2 || maxSegments < Boundary.Count) { base.BuildPreviewProxy(o, maxSegments); return; }
        for (int i = 0; i < Boundary.Count; i++)
        {
            var a = Boundary[i]; var b = Boundary[(i + 1) % Boundary.Count];
            o.Add((a.x, a.y, Elevation)); o.Add((b.x, b.y, Elevation));
        }
    }

    public override (double minX, double minY, double minZ, double maxX, double maxY, double maxZ)? PreviewAabb()
    {
        if (Boundary.Count == 0) return null;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var v in Boundary)
        { if (v.x < minX) minX = v.x; if (v.x > maxX) maxX = v.x; if (v.y < minY) minY = v.y; if (v.y > maxY) maxY = v.y; }
        return (minX, minY, Elevation, maxX, maxY, Elevation);
    }

    /// <summary>
    /// 拾取距离: 实心填充没有线, 点在里面就算命中(否则永远选不中);
    /// 有图案线的按线算(同 AutoCAD —— 点线选中, 不会因为一大块填充把上面的图元全"吞掉")。
    /// </summary>
    public override double DistanceTo(double px, double py)
    {
        if (!IsSolid) return base.DistanceTo(px, py);
        if (Boundary.Count >= 3 && LineMath.PointInPolygon(px, py, Boundary)) return 0;
        double best = double.MaxValue;
        for (int i = 0; i < Boundary.Count; i++)
        {
            var a = Boundary[i]; var b = Boundary[(i + 1) % Boundary.Count];
            best = Math.Min(best, SegDist(px, py, a.x, a.y, b.x, b.y));
        }
        return best;
    }

    public override SceneEntity Apply(Affine2 m)
    {
        var h = new HatchEntity
        {
            PatternName = PatternName,
            // 缩放跟着整体缩放走(图案不会因为放大图形就变密); 旋转叠加到图案角度上
            Scale = Scale * (Scale > 1e-12 ? m.ScaleMag : 1),
            Angle = Angle + Math.Atan2(m.B, m.A) * 180.0 / Math.PI,
            Cross = Cross,
        };
        var (ox, oy) = m.Map(OriginX, OriginY);   // 图案原点跟着实体走: 移动/旋转后图案不相对边界错位
        h.OriginX = ox; h.OriginY = oy;
        foreach (var p in Boundary) h.Boundary.Add(m.Map(p.x, p.y));
        return Colored(h);
    }

    /// <summary>夹点 = 边界顶点: 拖一个点改形状, 图案自动重新铺满(同原版关联填充)。</summary>
    public override List<(double x, double y)> Grips() => new(Boundary);

    public override SceneEntity? MoveGrip(int i, double nx, double ny)
    {
        if (i < 0 || i >= Boundary.Count) return null;
        var h = new HatchEntity { PatternName = PatternName, Scale = Scale, Angle = Angle, Cross = Cross, OriginX = OriginX, OriginY = OriginY };
        h.Boundary.AddRange(Boundary);
        h.Boundary[i] = (nx, ny);
        return Colored(h);
    }

    /// <summary>分解: 图案线变成一根根直线(实心则给边界环) —— 同原版「分解填充」。</summary>
    public override List<SceneEntity>? Explode()
    {
        var outp = new List<SceneEntity>();
        foreach (var (x1, y1, x2, y2) in Lines())
            outp.Add(Colored(new LineEntity { X0 = x1, Y0 = y1, X1 = x2, Y1 = y2 }));
        if (outp.Count == 0 && Boundary.Count >= 3)
        {
            var pl = new PolylineEntity { Closed = true };
            pl.Points.AddRange(Boundary);
            outp.Add(Colored(pl));
        }
        return outp.Count > 0 ? outp : null;
    }

    /// <summary>同参数深拷(改属性用): 边界另存一份, 图案缓存不带过去。</summary>
    public HatchEntity Clone()
    {
        var h = new HatchEntity { PatternName = PatternName, Scale = Scale, Angle = Angle, Cross = Cross, OriginX = OriginX, OriginY = OriginY };
        h.Boundary.AddRange(Boundary);
        h.CopyStyleFrom(this);
        return h;
    }

    /// <summary>面板/状态栏用的一句话说明。</summary>
    public string Describe()
    {
        var pat = HatchPatternLibrary.ByName(PatternName);
        string name = pat?.Display ?? PatternName;
        string sc = Scale > 1e-12 ? (pat?.IsUserDefined == true ? $"间距 {Scale:0.###}" : $"比例 {Scale:0.###}") : "比例 自动";
        return pat?.IsSolid == true ? $"{name}" : $"{name} · {sc} · 角度 {Angle:0.#}°{(Cross && pat?.IsUserDefined == true ? " · 十字" : "")}";
    }
}
