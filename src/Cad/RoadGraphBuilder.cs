using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

// ─────────────────────────────────────────────────────────────────────────────
// 从中线多段线抽取【属性路网图】(忠实移植原 RoadLib.Network.RoadGraphBuilder, 纯算法、不依赖引擎)。
// 与 Kylin 既有 RoadNetwork.BuildNoded(§82, 无属性通用图, 2D)互补: 此产 §290 属性 RoadGraph(带 Z→纵坡),
// 且做 Z 感知 noding(立交=XY 相交但标高差>zSep 不打断)+ 共线重复边去重 + 缺口桥接(跨标高不桥)——2D 版所无。
// 喂 §290 约束寻径 / §291 全指标, 完成 "多段线 → 属性图 → 寻径/指标" 端到端。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>noding 诊断(构建回显质量指标)。忠实原 NodingReport。</summary>
public sealed class NodingReport
{
    public int InputLines { get; internal set; }
    public int OutputSegments { get; internal set; }
    public int CrossSplits { get; internal set; }           // X 十字交点数
    public int TeeSplits { get; internal set; }             // T 丁字接合数
    public int DuplicateEdgesRemoved { get; internal set; } // 共线重叠产生的重复平行边去重数
    public int ComponentsBeforeBridge { get; internal set; } // 桥接前连通片数(noding 后)
    public int BridgesAdded { get; internal set; }          // 缺口桥接补的连接边数
}

/// <summary>
/// 从一组中线多段线抽取路网图(纯算法、不依赖引擎)。忠实移植原 RoadGraphBuilder。
/// ① NodePolylines 交叉口打断(X 十字 / T 丁字, Z 闸门区分平交·立交)→ ② 端点按容差吸附成共享节点
/// (空间哈希 O(n)), 每子段成一边; 末了去共线重叠的重复平行边 + 缺口桥接。
/// </summary>
public static class RoadGraphBuilder
{
    private const double TEps = 1e-6;   // 段内参数端点判据(交点落端点处交给吸附, 不打断)

    /// <summary>抽图(不需诊断的简写)。</summary>
    public static RoadGraph FromPolylines(
        IEnumerable<IReadOnlyList<Point3d>> polylines,
        double snapToleranceM = 2.0,
        double gradeSeparationM = 4.0,
        bool node = true,
        double bridgeGapM = 25.0)
        => FromPolylines(polylines, out _, snapToleranceM, gradeSeparationM, node, bridgeGapM);

    /// <summary>抽图并回填 report(质量指标)。</summary>
    public static RoadGraph FromPolylines(
        IEnumerable<IReadOnlyList<Point3d>> polylines,
        out NodingReport report,
        double snapToleranceM = 2.0,
        double gradeSeparationM = 4.0,
        bool node = true,
        double bridgeGapM = 25.0)
    {
        var input = new List<IReadOnlyList<Point3d>>();
        foreach (var pl in polylines)
            if (pl is { Count: >= 2 }) input.Add(pl);

        report = new NodingReport { InputLines = input.Count };
        double tol = Math.Max(1e-6, snapToleranceM);

        var segs = node ? NodePolylines(input, tol, gradeSeparationM, report) : input;
        report.OutputSegments = segs.Count;

        var graph = BuildFromNodedPolylines(segs, tol);
        if (node)
        {
            report.DuplicateEdgesRemoved = DedupDuplicateEdges(graph, tol);
            report.ComponentsBeforeBridge = graph.Validate().ComponentCount;
            if (bridgeGapM > tol)
                report.BridgesAdded = BridgeDangles(graph, bridgeGapM, gradeSeparationM, tol);
        }
        return graph;
    }

    // ── ① 交叉口打断(planarize / noding) ──
    private static List<IReadOnlyList<Point3d>> NodePolylines(
        List<IReadOnlyList<Point3d>> lines, double tol, double zSep, NodingReport report)
    {
        int n = lines.Count;
        var breaks = new List<(int seg, Point3d p)>[n];
        for (int i = 0; i < n; i++) breaks[i] = new();

        var box = new (double xmin, double ymin, double xmax, double ymax)[n];
        for (int i = 0; i < n; i++) box[i] = Bbox(lines[i]);

        // (a) X 十字:逐段对求二维相交(包围盒粗筛剪枝)。
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (!BoxOverlap(box[i], box[j], tol)) continue;
                var li = lines[i];
                var lj = lines[j];
                for (int a = 0; a + 1 < li.Count; a++)
                    for (int b = 0; b + 1 < lj.Count; b++)
                    {
                        if (!SegSegCross2D(li[a], li[a + 1], lj[b], lj[b + 1],
                                           out double ta, out double tb, out double ix, out double iy))
                            continue;
                        if (ta <= TEps || ta >= 1 - TEps || tb <= TEps || tb >= 1 - TEps) continue; // 端点→吸附
                        double za = Lerp(li[a].Z, li[a + 1].Z, ta);
                        double zb = Lerp(lj[b].Z, lj[b + 1].Z, tb);
                        if (Math.Abs(za - zb) > zSep) continue;                                     // 立交, 不打断
                        var jp = new Point3d(ix, iy, (za + zb) * 0.5);                              // 规范化交点(共享)
                        breaks[i].Add((a, jp));
                        breaks[j].Add((b, jp));
                        report.CrossSplits++;
                    }
            }

        // (b) T 丁字:每条线两端点投影到其它线的中段(共线重叠的发散边界同样在此打断)。
        for (int i = 0; i < n; i++)
        {
            var li = lines[i];
            foreach (int end in stackalloc[] { 0, li.Count - 1 })
            {
                var ep = li[end];
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    if (!PointNearBox(ep, box[j], tol)) continue;
                    var lj = lines[j];
                    if (!NearestOnPolyline(lj, ep, out int seg, out _, out Point3d proj, out double d)) continue;
                    if (d > tol) continue;                                       // 不够近
                    if (Math.Abs(proj.Z - ep.Z) > zSep) continue;               // 上下层贴近, 非同高
                    if (proj.DistanceTo(lj[0]) <= tol || proj.DistanceTo(lj[^1]) <= tol) continue; // 落 B 端点→吸附
                    breaks[j].Add((seg, new Point3d(proj.X, proj.Y, ep.Z)));    // 用端点 Z, 贴合接入
                    report.TeeSplits++;
                }
            }
        }

        var result = new List<IReadOnlyList<Point3d>>();
        for (int i = 0; i < n; i++)
            result.AddRange(SplitPolyline(lines[i], breaks[i], tol));
        return result;
    }

    /// <summary>按打断点把一条多段线切成多条子段(重建顶点序列并标记切点, 去近重合)。</summary>
    private static IEnumerable<IReadOnlyList<Point3d>> SplitPolyline(
        IReadOnlyList<Point3d> line, List<(int seg, Point3d p)> brk, double tol)
    {
        if (brk.Count == 0) { yield return line; yield break; }
        brk.Sort((x, y) => x.seg.CompareTo(y.seg));

        var verts = new List<Point3d>();
        var cut = new List<bool>();
        void Push(in Point3d p, bool isCut)
        {
            if (verts.Count > 0 && verts[^1].DistanceTo(p) <= tol)
            { if (isCut) cut[^1] = true; return; }
            verts.Add(p);
            cut.Add(isCut);
        }

        Push(line[0], false);
        int bi = 0;
        for (int s = 0; s + 1 < line.Count; s++)
        {
            while (bi < brk.Count && brk[bi].seg == s) { Push(brk[bi].p, true); bi++; }
            Push(line[s + 1], false);
        }
        cut[0] = false;
        cut[^1] = false;   // 首尾不作切点

        var cur = new List<Point3d> { verts[0] };
        for (int k = 1; k < verts.Count; k++)
        {
            cur.Add(verts[k]);
            if (cut[k] && k < verts.Count - 1)
            {
                if (cur.Count >= 2) yield return cur;
                cur = new List<Point3d> { verts[k] };
            }
        }
        if (cur.Count >= 2) yield return cur;
    }

    // ── ② 端点吸附成图 ──
    private static RoadGraph BuildFromNodedPolylines(
        IEnumerable<IReadOnlyList<Point3d>> polylines, double tol)
    {
        var graph = new RoadGraph();
        var buckets = new Dictionary<(long, long), List<string>>();
        int nodeSeq = 0, edgeSeq = 0;

        (long, long) BucketOf(in Point3d p) => ((long)Math.Floor(p.X / tol), (long)Math.Floor(p.Y / tol));

        string SnapOrCreate(in Point3d p)
        {
            var (bx, by) = BucketOf(p);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                {
                    if (!buckets.TryGetValue((bx + dx, by + dy), out var ids)) continue;
                    foreach (var id in ids)
                        if (graph.GetNode(id)!.Position.DistanceTo(p) <= tol)
                            return id;
                }
            var newId = $"N{nodeSeq++}";
            graph.AddNode(newId, RoadNodeType.Junction, p);
            var b = BucketOf(p);
            if (!buckets.TryGetValue(b, out var bl)) { bl = new(); buckets[b] = bl; }
            bl.Add(newId);
            return newId;
        }

        foreach (var pl in polylines)
        {
            if (pl is null || pl.Count < 2) continue;
            string from = SnapOrCreate(pl[0]);
            string to = SnapOrCreate(pl[^1]);
            if (from == to) continue;                 // 首尾吸附到同点 → 自环, 跳过
            graph.AddEdge(new RoadEdge($"E{edgeSeq++}", from, to, pl));
        }
        return graph;
    }

    /// <summary>去共线重叠产生的重复平行边:同一对节点间、里程相近且质心相近判为重复, 删较长者。</summary>
    private static int DedupDuplicateEdges(RoadGraph g, double tol)
    {
        var seen = new Dictionary<(string, string), RoadEdge>();
        var drop = new List<string>();
        foreach (var e in g.Edges)
        {
            var key = string.CompareOrdinal(e.FromId, e.ToId) <= 0 ? (e.FromId, e.ToId) : (e.ToId, e.FromId);
            if (seen.TryGetValue(key, out var prev))
            {
                double lenTol = Math.Max(tol, 0.02 * Math.Max(prev.LengthM, e.LengthM));
                double midTol = Math.Max(tol, 0.05 * Math.Max(prev.LengthM, e.LengthM));
                if (Math.Abs(prev.LengthM - e.LengthM) <= lenTol &&
                    Centroid(prev.Centerline).DistanceTo(Centroid(e.Centerline)) <= midTol)
                {
                    drop.Add(e.LengthM > prev.LengthM ? e.Id : prev.Id);   // 删较长者
                    if (e.LengthM <= prev.LengthM) seen[key] = e;           // 保留较短者作基准
                    continue;
                }
            }
            seen[key] = e;
        }
        foreach (var id in drop) g.RemoveEdge(id);
        return drop.Count;
    }

    private static Point3d Centroid(IReadOnlyList<Point3d> line)
    {
        if (line.Count == 0) return default;
        double x = 0, y = 0, z = 0;
        foreach (var p in line) { x += p.X; y += p.Y; z += p.Z; }
        return new Point3d(x / line.Count, y / line.Count, z / line.Count);
    }

    // ── ③ 缺口桥接:能联通的尽量联通(只连不同连通片的悬挂端点) ──
    private readonly record struct BridgeTarget(bool IsNode, string RefId, Point3d Pos);

    private static int BridgeDangles(RoadGraph g, double bridgeTol, double zSep, double snapTol)
    {
        var degree = new Dictionary<string, int>();
        foreach (var n in g.Nodes) degree[n.Id] = 0;
        foreach (var e in g.Edges) { degree[e.FromId]++; degree[e.ToId]++; }
        var dangles = new List<string>();
        foreach (var n in g.Nodes) if (degree[n.Id] <= 1) dangles.Add(n.Id);
        if (dangles.Count == 0) return 0;

        var uf = new UnionFind();
        foreach (var n in g.Nodes) uf.Add(n.Id);
        foreach (var e in g.Edges) uf.Union(e.FromId, e.ToId);

        var cands = new List<(double gap, string from, BridgeTarget tgt)>();
        foreach (var dId in dangles)
        {
            var d = g.GetNode(dId)!;
            BridgeTarget? best = null;
            double bestGap = bridgeTol;
            foreach (var t in g.Nodes)                              // 节点目标
            {
                if (t.Id == dId || uf.Find(t.Id) == uf.Find(dId)) continue;
                double gap = d.Position.HorizontalDistanceTo(t.Position);
                if (gap > bestGap || Math.Abs(t.Position.Z - d.Position.Z) > zSep) continue;
                best = new BridgeTarget(true, t.Id, t.Position);
                bestGap = gap;
            }
            foreach (var e in g.Edges)                              // 边投影目标
            {
                if (e.FromId == dId || e.ToId == dId || uf.Find(e.FromId) == uf.Find(dId)) continue;
                NearestOnEdge(g, e, d.Position, out var proj, out double gap);
                if (gap > bestGap || Math.Abs(proj.Z - d.Position.Z) > zSep) continue;
                best = new BridgeTarget(false, e.Id, proj);
                bestGap = gap;
            }
            if (best is { } bt && bestGap > snapTol) cands.Add((bestGap, dId, bt));
        }

        cands.Sort((a, b) => a.gap.CompareTo(b.gap));
        int added = 0, seq = 0;
        foreach (var (_, fromId, tgt) in cands)
        {
            string targetId;
            if (tgt.IsNode)
            {
                if (g.GetNode(tgt.RefId) is null || uf.Find(fromId) == uf.Find(tgt.RefId)) continue;
                targetId = tgt.RefId;
            }
            else
            {
                var e = g.GetEdge(tgt.RefId);                       // 可能已被先前 split 改名 → 跳
                if (e is null || uf.Find(fromId) == uf.Find(e.FromId)) continue;
                var fromN = g.GetNode(e.FromId)!;
                var toN = g.GetNode(e.ToId)!;
                if (tgt.Pos.DistanceTo(fromN.Position) <= snapTol) targetId = e.FromId;
                else if (tgt.Pos.DistanceTo(toN.Position) <= snapTol) targetId = e.ToId;
                else
                {
                    var hub = g.SplitEdgeAtNearest(e.Id, tgt.Pos, $"BRH{seq}");
                    uf.Add(hub.Id);
                    uf.Union(hub.Id, e.FromId);                     // hub 在被接边的片内
                    targetId = hub.Id;
                }
            }

            var a = g.GetNode(fromId)!.Position;
            var b = g.GetNode(targetId)!.Position;
            g.AddEdge(new RoadEdge($"BR{seq++}", fromId, targetId, new[] { a, b }));
            uf.Union(fromId, targetId);
            added++;
        }
        return added;
    }

    private static void NearestOnEdge(RoadGraph g, RoadEdge e, in Point3d p, out Point3d proj, out double dist)
    {
        IReadOnlyList<Point3d> line = e.Centerline.Count >= 2
            ? e.Centerline
            : new[] { g.GetNode(e.FromId)!.Position, g.GetNode(e.ToId)!.Position };
        NearestOnPolyline(line, p, out _, out _, out proj, out dist);
    }

    /// <summary>并查集(节点 Id; Find 自动补未知 Id, 供桥接动态插入的 hub 节点用)。</summary>
    private sealed class UnionFind
    {
        private readonly Dictionary<string, string> _p = new();
        public void Add(string id) { if (!_p.ContainsKey(id)) _p[id] = id; }
        public string Find(string x)
        {
            Add(x);
            string r = x;
            while (_p[r] != r) r = _p[r];
            while (_p[x] != r) { var n = _p[x]; _p[x] = r; x = n; }
            return r;
        }
        public void Union(string a, string b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) _p[ra] = rb;
        }
    }

    // ── 几何原语 ──
    private static bool SegSegCross2D(in Point3d p1, in Point3d p2, in Point3d q1, in Point3d q2,
                                      out double ta, out double tb, out double ix, out double iy)
    {
        ta = tb = ix = iy = 0;
        double rx = p2.X - p1.X, ry = p2.Y - p1.Y;
        double sx = q2.X - q1.X, sy = q2.Y - q1.Y;
        double denom = rx * sy - ry * sx;
        if (Math.Abs(denom) < 1e-12) return false;                    // 平行/共线
        double qpx = q1.X - p1.X, qpy = q1.Y - p1.Y;
        ta = (qpx * sy - qpy * sx) / denom;
        tb = (qpx * ry - qpy * rx) / denom;
        if (ta < 0 || ta > 1 || tb < 0 || tb > 1) return false;
        ix = p1.X + ta * rx;
        iy = p1.Y + ta * ry;
        return true;
    }

    private static bool NearestOnPolyline(IReadOnlyList<Point3d> line, in Point3d p,
                                          out int seg, out double t, out Point3d proj, out double dist)
    {
        seg = -1; t = 0; proj = default; dist = double.MaxValue;
        for (int i = 0; i + 1 < line.Count; i++)
        {
            var a = line[i];
            var b = line[i + 1];
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            double tt = len2 < 1e-12 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            tt = tt < 0 ? 0 : (tt > 1 ? 1 : tt);
            double qx = a.X + tt * dx, qy = a.Y + tt * dy, qz = a.Z + tt * (b.Z - a.Z);
            double d = Math.Sqrt((p.X - qx) * (p.X - qx) + (p.Y - qy) * (p.Y - qy));
            if (d < dist) { dist = d; seg = i; t = tt; proj = new Point3d(qx, qy, qz); }
        }
        return seg >= 0;
    }

    private static (double xmin, double ymin, double xmax, double ymax) Bbox(IReadOnlyList<Point3d> line)
    {
        double xmin = double.MaxValue, ymin = double.MaxValue, xmax = double.MinValue, ymax = double.MinValue;
        foreach (var p in line)
        {
            if (p.X < xmin) xmin = p.X;
            if (p.X > xmax) xmax = p.X;
            if (p.Y < ymin) ymin = p.Y;
            if (p.Y > ymax) ymax = p.Y;
        }
        return (xmin, ymin, xmax, ymax);
    }

    private static bool BoxOverlap(in (double xmin, double ymin, double xmax, double ymax) a,
                                   in (double xmin, double ymin, double xmax, double ymax) b, double pad)
        => a.xmin - pad <= b.xmax && b.xmin - pad <= a.xmax &&
           a.ymin - pad <= b.ymax && b.ymin - pad <= a.ymax;

    private static bool PointNearBox(in Point3d p, in (double xmin, double ymin, double xmax, double ymax) b, double pad)
        => p.X >= b.xmin - pad && p.X <= b.xmax + pad && p.Y >= b.ymin - pad && p.Y <= b.ymax + pad;

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
