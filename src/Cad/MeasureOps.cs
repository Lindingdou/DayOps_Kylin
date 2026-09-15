using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「特性」组测量 SplitButton 六个子项的判定与文案 —— 忠实原版
/// <c>MainWindow.ContextMenu.cs</c> 的 OnMeasureQuick/Distance/Radius/Angle/Area/VolumeClick：
/// 都是「先看选集，选集能算就按实体算；不足以判定的才回到鼠标点测(jig)」的双模式。
/// 纯逻辑、可单测：只出文案与「是否要进点测」的判定，不碰 UI。
/// </summary>
public static class MeasureOps
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static string N(double v) => v.ToString("0.###", Inv);

    /// <summary>一次测量的结果：要显示的文案 + 是否需要转入鼠标点测。</summary>
    public readonly record struct Result(string Text, bool NeedJig)
    {
        public static Result Say(string t) => new(t, false);
        public static Result Jig(string t) => new(t, true);
    }

    /// <summary>半径：选中的圆/圆弧/正多边形逐个报半径。忠实原版「仅 Circle/Arc 有 radius 字段」。</summary>
    public static Result Radius(IReadOnlyList<SceneEntity> sel)
    {
        if (sel.Count == 0) return Result.Say("测量 - 半径：请先选中圆或圆弧");
        var rs = new List<double>();
        foreach (var e in sel)
        {
            switch (e)
            {
                case CircleEntity c: rs.Add(c.Radius); break;
                case PolygonEntity p: rs.Add(p.Radius); break;
                case ArcEntity a:
                    var cc = ArcMath.Circumcircle(a.X1, a.Y1, a.X2, a.Y2, a.X3, a.Y3);
                    if (cc != null) rs.Add(cc.Value.Item3);
                    break;
            }
        }
        if (rs.Count == 0) return Result.Say("测量 - 半径：所选实体没有半径（仅圆/圆弧/正多边形有）");
        if (rs.Count == 1) return Result.Say($"测量 - 半径：{N(rs[0])}");
        return Result.Say($"测量 - 半径：共 {rs.Count} 项 · " + string.Join(" / ", rs.Select(N)));
    }

    /// <summary>
    /// 体积：只对闭合实体网格计算。先按模型尺度焊接重复顶点、去掉退化/重复三角形，
    /// 再统一面朝向并用散度定理积分；开口或非流形网格直接提示不能作为实体体积计算。
    /// </summary>
    public static Result Volume(IReadOnlyList<SceneEntity> sel)
    {
        if (sel.Count == 0) return Result.Say("测量 - 体积：请先选中三角网");
        double total = 0; int n = 0, meshCount = 0, rejected = 0;
        MeshDiagnoseResult firstRejected = default;
        foreach (var e in sel)
        {
            if (e is not MeshEntity m || m.TriangleCount <= 0) continue;
            meshCount++;
            var r = SolidVolume(m);
            if (!r.Valid)
            {
                rejected++;
                if (rejected == 1) firstRejected = r.Diagnose;
                continue;
            }
            total += r.Volume;
            n++;
        }
        if (meshCount == 0) return Result.Say("测量 - 体积：所选实体不是三角网或几何为空");
        if (n == 0)
        {
            return Result.Say($"测量 - 体积：无法计算实体体积，网格未闭合" +
                              $"（开放边 {firstRejected.BoundaryEdges}，非流形边 {firstRejected.NonManifoldEdges}）");
        }

        string suffix = rejected > 0 ? $"（另有 {rejected} 个网格因未闭合未计入）" : "";
        return Result.Say(n > 1 ? $"测量 - 体积：共 {n} 个三角网，合计 {N(total)} m³{suffix}"
                                : $"测量 - 体积：{N(total)} m³{suffix}");
    }

    private readonly record struct SolidVolumeResult(bool Valid, double Volume, MeshDiagnoseResult Diagnose);

    private static SolidVolumeResult SolidVolume(MeshEntity mesh)
    {
        if (mesh.Verts.Count == 0 || mesh.Tris.Count == 0)
            return new(false, 0, default);

        var b = mesh.Bounds;
        double dx = b.maxX - b.minX, dy = b.maxY - b.minY, dz = b.maxZ - b.minZ;
        double diagonal = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        double tolerance = Math.Max(1e-9, diagonal * 1e-8);

        // 导入数据常见“同坐标多顶点”和重复面；不先处理会让拓扑被误判为开口/非流形。
        var welded = MeshWeld.Weld(mesh.Verts, mesh.Tris, tolerance, dropDuplicateTris: true);

        // 体积对平移不变；先移到局部坐标，避免矿区常见大地坐标参与叉乘时发生浮点消减。
        var localVerts = new List<(double x, double y, double z)>(welded.Verts.Count);
        double ox = (b.minX + b.maxX) * 0.5;
        double oy = (b.minY + b.maxY) * 0.5;
        double oz = (b.minZ + b.maxZ) * 0.5;
        foreach (var v in welded.Verts)
            localVerts.Add((v.x - ox, v.y - oy, v.z - oz));

        var oriented = MeshOrient.MakeConsistent(localVerts, welded.Tris);
        var diagnose = MeshDiagnose.Analyze(localVerts, oriented, selfIntersect: false);
        if (!diagnose.IsClosed)
            return new(false, 0, diagnose);

        double volume = Math.Abs(MeshOrient.SignedVolume6(localVerts, oriented)) / 6.0;
        return double.IsFinite(volume) ? new(true, volume, diagnose) : new(false, 0, diagnose);
    }

    /// <summary>
    /// 面积：选中的圆/矩形/正多边形/闭合多段线/三角网逐个算并合计。
    /// 原版单选多段线时同时报周长，这里保留(Kylin 原有行为)。
    /// </summary>
    public static Result Area(IReadOnlyList<SceneEntity> sel)
    {
        if (sel.Count == 0) return Result.Jig("面积：指定第一点");
        if (sel.Count == 1 && sel[0] is PolylineEntity single && single.Closed && single.Points.Count >= 3)
            return Result.Say($"测量 - 面积：{N(GeomMeasure.Area(single.Points))} · 周长(闭合) "
                              + $"{N(GeomMeasure.Perimeter(single.Points, true))} · {single.Points.Count} 顶点");
        double total = 0; int n = 0;
        foreach (var e in sel)
        {
            double a = AreaOf(e);
            if (a > 0) { total += a; n++; }
        }
        if (n == 0) return Result.Say("测量 - 面积：所选实体不支持面积（仅圆/矩形/正多边形/闭合多段线/三角网）");
        return Result.Say(n > 1 ? $"测量 - 面积：共 {n} 项，合计 {N(total)}" : $"测量 - 面积：{N(total)}");
    }

    /// <summary>单个实体的面积；不支持返回 0。</summary>
    public static double AreaOf(SceneEntity e) => e switch
    {
        CircleEntity c => Math.PI * c.Radius * c.Radius,
        RectEntity r => Math.Abs(r.X1 - r.X0) * Math.Abs(r.Y1 - r.Y0),
        PolygonEntity p => p.Sides * p.Radius * p.Radius * Math.Sin(2 * Math.PI / p.Sides) / 2,
        PolylineEntity pl when pl.Closed && pl.Points.Count >= 3 => GeomMeasure.Area(pl.Points),
        MeshEntity m => m.SurfaceArea(),
        _ => 0,
    };

    /// <summary>
    /// 距离：无选中 → 两点点测；单选直线 → 报长度；2 个及以上 → 前两个实体锚点间距离。
    /// 忠实原版 OnMeasureDistanceClick 的三分支。
    /// </summary>
    public static Result Distance(IReadOnlyList<SceneEntity> sel)
    {
        if (sel.Count == 0) return Result.Jig("测距：点第一点");
        if (sel.Count == 1)
        {
            if (sel[0] is LineEntity l)
                return Result.Say($"测量 - 距离：直线长度 = {N(Math.Sqrt((l.X1 - l.X0) * (l.X1 - l.X0) + (l.Y1 - l.Y0) * (l.Y1 - l.Y0)))}");
            return Result.Say("测量 - 距离：单选时仅支持直线（多选则量两实体间距）");
        }
        var a = Anchor(sel[0]); var b = Anchor(sel[1]);
        if (a == null || b == null) return Result.Say("测量 - 距离：无法从所选实体提取参考点");
        double d = Math.Sqrt((b.Value.x - a.Value.x) * (b.Value.x - a.Value.x) + (b.Value.y - a.Value.y) * (b.Value.y - a.Value.y));
        return Result.Say($"测量 - 距离：{EntityTypeName.Of(sel[0])} ↔ {EntityTypeName.Of(sel[1])} = {N(d)}");
    }

    /// <summary>角度：选中 2 条及以上直线 → 前两条的夹角；否则回到三点点测。忠实原版 OnMeasureAngleClick。</summary>
    public static Result Angle(IReadOnlyList<SceneEntity> sel)
    {
        var lines = sel.OfType<LineEntity>().Take(2).ToList();
        if (sel.Count < 2 || lines.Count < 2) return Result.Jig("测角：点顶点");
        double v1x = lines[0].X1 - lines[0].X0, v1y = lines[0].Y1 - lines[0].Y0;
        double v2x = lines[1].X1 - lines[1].X0, v2y = lines[1].Y1 - lines[1].Y0;
        double n1 = Math.Sqrt(v1x * v1x + v1y * v1y), n2 = Math.Sqrt(v2x * v2x + v2y * v2y);
        if (n1 < 1e-12 || n2 < 1e-12) return Result.Say("测量 - 角度：退化线段（长度近 0）");
        double cos = Math.Clamp((v1x * v2x + v1y * v2y) / (n1 * n2), -1, 1);
        return Result.Say($"测量 - 角度：{(Math.Acos(cos) * 180 / Math.PI).ToString("0.##", Inv)}°");
    }

    /// <summary>实体的参考锚点（量两实体间距用）；取不到返回 null。</summary>
    public static (double x, double y)? Anchor(SceneEntity e) => e switch
    {
        LineEntity l => ((l.X0 + l.X1) / 2, (l.Y0 + l.Y1) / 2),
        CircleEntity c => (c.Cx, c.Cy),
        PolygonEntity p => (p.Cx, p.Cy),
        RectEntity r => ((r.X0 + r.X1) / 2, (r.Y0 + r.Y1) / 2),
        PointEntity pt => (pt.X, pt.Y),
        TextEntity t => (t.X, t.Y),
        ArcEntity a => (a.X2, a.Y2),
        PolylineEntity pl when pl.Points.Count > 0 => (pl.Points.Average(q => q.x), pl.Points.Average(q => q.y)),
        MeshEntity m => ((m.Bounds.minX + m.Bounds.maxX) / 2, (m.Bounds.minY + m.Bounds.maxY) / 2),
        _ => null,
    };
}
