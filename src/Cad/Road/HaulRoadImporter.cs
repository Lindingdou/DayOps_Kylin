// 忠实移植自原 PitMine3D Modules/RoadLib/Network/HaulRoadImporter.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 「几何链 → 路网」的接入桥：把 MineAssLib【坑线落地】写进共享存
/// (<see cref="HaulRoadCenterlineSet"/>，key <c>transport.haulroads</c>) 的坑线中线，
/// 转成路网图的边并带上断面属性。设计 §3「RoadEdge.Centerline ← 复用坑线落地输出」由此落地。
///
/// 用法(两步，与 <see cref="RoadGraphBuilder"/> 配合，不另起一套建图逻辑)：
/// <code>
///   var roads = HaulRoadImporter.Read(set);                       // ① 取出可用中线
///   lines.AddRange(HaulRoadImporter.ToPolylines(roads));          //    与图上抽的中线一起喂建图器
///   var g = RoadGraphBuilder.FromPolylines(lines, ...);           //    统一做交叉打断/端点吸附/缺口桥接
///   int n = HaulRoadImporter.StampEdges(g, roads);                // ② 建完图再把路宽/车道/来源回贴到边
/// </code>
/// 为什么分两步：坑线中线必须与图上其它中线在同一次 noding 里打断吸附，才能真连通；
/// 而 <see cref="RoadGraphBuilder"/> 是纯几何的、不认识断面属性，故属性只能在建图后按几何匹配回贴。
///
/// 本类不碰 <c>IUserSettings</c>(那是插件层的事)，只吃 DTO —— 保持 Network 层可单测、无宿主依赖。
/// </summary>
public static class HaulRoadImporter
{
    /// <summary>默认匹配容差 m：边中线到坑线中线的水平距。边本就是坑线中线的子段，正常≈0，留 2m 余量。</summary>
    public const double DefaultMatchToleranceM = 2.0;

    /// <summary>默认标高容差 m：防折返上下层同 XY 误贴。取 6m —— 大于建图器立交判据 4m，小于常见台阶高 10~15m。</summary>
    public const double DefaultZToleranceM = 6.0;

    /// <summary>从共享存 DTO 取出可用的坑线(点数 ≥2)。null / 空集返回空表，调用方不必判空。</summary>
    public static List<HaulRoadCenterline> Read(HaulRoadCenterlineSet? set)
    {
        var list = new List<HaulRoadCenterline>();
        if (set?.Roads is null) return list;
        foreach (var r in set.Roads)
            if (r is not null && r.PointCount() >= 2) list.Add(r);
        return list;
    }

    /// <summary>坑线中线 → 多段线点列(扁平 [x,y,z,...] 展开)，顺序与 <paramref name="roads"/> 一一对应。</summary>
    public static List<IReadOnlyList<Point3d>> ToPolylines(IReadOnlyList<HaulRoadCenterline> roads)
    {
        var lines = new List<IReadOnlyList<Point3d>>(roads.Count);
        foreach (var r in roads) lines.Add(ToPoints(r));
        return lines;
    }

    /// <summary>单条坑线的扁平坐标 → 点列。</summary>
    public static IReadOnlyList<Point3d> ToPoints(HaulRoadCenterline road)
    {
        var f = road.CenterlineXyz;
        int n = (f?.Length ?? 0) / 3;
        var pts = new Point3d[n];
        for (int i = 0; i < n; i++) pts[i] = new Point3d(f![3 * i], f[3 * i + 1], f[3 * i + 2]);
        return pts;
    }

    /// <summary>
    /// 建图后把坑线的断面属性回贴到边：逐边找「中线整条都贴着」的那条坑线
    /// (取边中线各顶点到坑线中线的最大水平距，最小且 ≤<paramref name="toleranceM"/> 者胜)，
    /// 命中则写 <c>WidthM / LaneCount / IsTemporary / SourceRef</c>。返回贴上属性的边数。
    ///
    /// 口径：<c>GradePct</c> 与 <c>LengthM</c> 不覆盖 —— 那是 <see cref="RoadEdge.RecomputeGeometry"/>
    /// 按实际中线逐段反算的真值；DTO 里的 <c>GradePct</c> 是设计限坡，只在边几何退化(点数&lt;2、
    /// 算不出坡度)时才拿来兜底，免得「设计限坡」与「实际中线」两个口径打架。
    /// </summary>
    public static int StampEdges(RoadGraph graph, IReadOnlyList<HaulRoadCenterline> roads,
                                 double toleranceM = DefaultMatchToleranceM,
                                 double zToleranceM = DefaultZToleranceM)
    {
        if (roads.Count == 0) return 0;
        var geo = ToPolylines(roads);

        int stamped = 0;
        foreach (var e in graph.Edges)
        {
            var c = e.Centerline;
            if (c.Count < 2)
            {
                var a = graph.GetNode(e.FromId);
                var b = graph.GetNode(e.ToId);
                if (a is null || b is null) continue;
                c = new[] { a.Position, b.Position };
            }

            int best = -1;
            double bestDev = double.MaxValue;
            for (int i = 0; i < geo.Count; i++)
            {
                double dev = MaxDeviation(c, geo[i], zToleranceM);
                if (dev <= toleranceM && dev < bestDev) { bestDev = dev; best = i; }   // 并列取先者
            }
            if (best < 0) continue;

            var r = roads[best];
            e.WidthM = r.RoadWidthM;
            if (r.LaneCount >= 1) e.LaneCount = r.LaneCount;
            e.IsTemporary = r.IsTemporary;
            e.SourceRef = string.IsNullOrWhiteSpace(r.Id)
                ? (string.IsNullOrWhiteSpace(r.Source) ? "坑线落地" : r.Source)
                : (string.IsNullOrWhiteSpace(r.Source) ? r.Id : $"{r.Source}:{r.Id}");
            if (e.Centerline.Count < 2 && Math.Abs(e.GradePct) < 1e-9 && r.GradePct > 1e-9)
                e.GradePct = r.GradePct;   // 仅几何退化时用设计限坡兜底
            stamped++;
        }
        return stamped;
    }

    /// <summary>
    /// 边中线相对坑线中线的最大偏离(水平距 m)。任一顶点的标高与坑线投影点差超
    /// <paramref name="zTol"/> 视为不同层(折返上下层)，直接判不匹配。
    /// </summary>
    private static double MaxDeviation(IReadOnlyList<Point3d> edge, IReadOnlyList<Point3d> road, double zTol)
    {
        if (road.Count < 2) return double.MaxValue;
        double worst = 0;
        foreach (var p in edge)
        {
            NearestOnPolyline(road, p, out var proj, out double d);
            if (Math.Abs(proj.Z - p.Z) > zTol) return double.MaxValue;
            if (d > worst) worst = d;
        }
        return worst;
    }

    /// <summary>点到多段线最近投影(水平距判据，Z 按段插值)。与建图器同口径。</summary>
    private static void NearestOnPolyline(IReadOnlyList<Point3d> line, in Point3d p,
                                          out Point3d proj, out double dist)
    {
        proj = line[0];
        dist = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i];
            var b = line[i + 1];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-12 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            t = t < 0 ? 0 : (t > 1 ? 1 : t);
            double qx = a.X + t * dx, qy = a.Y + t * dy, qz = a.Z + t * (b.Z - a.Z);
            double d = Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
            if (d < dist) { dist = d; proj = new Point3d(qx, qy, qz); }
        }
    }

    /// <summary>一句话自述(命令行回显用)：条数 + 总长 + 路宽区间。</summary>
    public static string Describe(IReadOnlyList<HaulRoadCenterline> roads)
    {
        if (roads.Count == 0) return "无已落地坑线";
        double total = 0, wMin = double.MaxValue, wMax = 0;
        foreach (var r in roads)
        {
            var pts = ToPoints(r);
            for (int i = 1; i < pts.Count; i++) total += pts[i].DistanceTo(pts[i - 1]);
            if (r.RoadWidthM > 1e-9)
            {
                if (r.RoadWidthM < wMin) wMin = r.RoadWidthM;
                if (r.RoadWidthM > wMax) wMax = r.RoadWidthM;
            }
        }
        string w = wMax <= 1e-9 ? "宽未知"
                 : (wMax - wMin > 0.05 ? $"路宽 {wMin:F1}~{wMax:F1}m" : $"路宽 {wMax:F1}m");
        return $"{roads.Count} 条 / 共 {total:F0}m / {w}";
    }
}
