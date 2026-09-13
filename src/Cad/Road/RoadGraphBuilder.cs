// 忠实移植自原 PitMine3D Modules/RoadLib/Network/RoadGraphBuilder.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
namespace PitMine3D.Kylin.Cad.Road;

/// <summary>noding 诊断（构建回显质量指标用）。中线条数、打断后子段数、各类打断计数、去重的重复平行边数、缺口桥接数。</summary>
public sealed class NodingReport
{
    public int InputLines { get; internal set; }
    public int OutputSegments { get; internal set; }
    public int CrossSplits { get; internal set; }           // X 十字交点数
    public int TeeSplits { get; internal set; }             // T 丁字接合数
    public int MidSpanWelds { get; internal set; }          // 半腰贴近焊接数（R-N3：两线最近处都在各自内部）
    public int DuplicateEdgesRemoved { get; internal set; } // 共线重叠产生的重复平行边去重数
    public int ComponentsBeforeBridge { get; internal set; } // 桥接前连通片数（noding 后）
    public int BridgesAdded { get; internal set; }          // 第一遍：悬挂端点桥接补的连接边数
    public int GapBridgesAdded { get; internal set; }       // 第二遍：片间最近点对桥接补的连接边数（两侧都可半腰打断）

    /// <summary>两遍桥接合计。</summary>
    public int TotalBridges => BridgesAdded + GapBridgesAdded;
}

/// <summary>
/// 从一组中线多段线抽取路网图（设计 §3 的 A2，纯算法、不依赖引擎）。
/// 两步：① <see cref="NodePolylines"/> 交叉口打断（X 十字 / T 丁字，Z 闸门区分平交·立交）→
/// ② 端点按容差吸附成共享节点（空间哈希 O(n)），每条子段成一条边；末了去共线重叠产生的重复平行边。
/// 这是"坑线落地中线 → 一期路网图"的桥：调用方只需把多段线喂进来，无需懂图结构。
/// </summary>
public static class RoadGraphBuilder
{
    private const double TEps = 1e-6;   // 段内参数端点判据（交点落端点处交给吸附，不打断）

    /// <summary>抽图（不需诊断的简写）。参数同带 <see cref="NodingReport"/> 的重载。</summary>
    public static RoadGraph FromPolylines(
        IEnumerable<IReadOnlyList<Point3d>> polylines,
        double snapToleranceM = 2.0,
        double gradeSeparationM = 4.0,
        bool node = true,
        double bridgeGapM = 25.0)
        => FromPolylines(polylines, out _, snapToleranceM, gradeSeparationM, node, bridgeGapM);

    /// <summary>
    /// 抽图并回填 <paramref name="report"/>（质量指标）。
    /// <paramref name="snapToleranceM"/> 内的端点视为同一节点（按三维距离，
    /// 故立体交叉/折返上下层同 XY 不同标高不会误并）。
    /// <paramref name="gradeSeparationM"/>：XY 相交但两线标高差超此值视为立交，不打断（默认 4m）。
    /// <paramref name="node"/>：是否做交叉口打断 + 重复边去重 + 缺口桥接（默认开；关则等价旧版纯端点吸附）。
    /// <paramref name="bridgeGapM"/>：缺口桥接距离 m —— ≤此距离、分属不同连通片的悬挂端点主动接上（0 关）。默认 25m。
    /// </summary>
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
            {
                report.BridgesAdded = BridgeDangles(graph, bridgeGapM, gradeSeparationM, tol);
                report.GapBridgesAdded = BridgeComponentGaps(graph, bridgeGapM, gradeSeparationM, tol);
            }
        }
        return graph;
    }

    // ── ① 交叉口打断（planarize / noding） ──────────────────────────────────────────

    /// <summary>
    /// 在中线相交处把多段线打断成子段，让贴着却不共端点的路真正连通。
    /// X 十字：两段在 XY 内部真相交且标高接近 → 两段都在交点打断、对齐为同一交点。
    /// T 丁字：某线端点落在另一线中段（≤tol）且标高接近 → 被穿过的线在投影脚打断
    ///         （共线重叠的边界也由此打断；重叠产生的重复平行边在建图后去重）。
    /// 立交：XY 相交但标高差 &gt; <paramref name="zSep"/> → 跳过（上下层不并）。
    /// 端点对端点交给后续吸附，这里不处理。
    /// </summary>
    private static List<IReadOnlyList<Point3d>> NodePolylines(
        List<IReadOnlyList<Point3d>> lines, double tol, double zSep, NodingReport report)
    {
        int n = lines.Count;
        var breaks = new List<(int seg, Point3d p)>[n];
        for (int i = 0; i < n; i++) breaks[i] = new();

        var box = new (double xmin, double ymin, double xmax, double ymax)[n];
        for (int i = 0; i < n; i++) box[i] = Bbox(lines[i]);

        // (a) X 十字 + (c) 半腰贴近：同一趟逐段对循环（包围盒粗筛剪枝）。
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (!BoxOverlap(box[i], box[j], tol)) continue;
                var li = lines[i];
                var lj = lines[j];
                for (int a = 0; a + 1 < li.Count; a++)
                    for (int b = 0; b + 1 < lj.Count; b++)
                    {
                        if (SegSegCross2D(li[a], li[a + 1], lj[b], lj[b + 1],
                                          out double ta, out double tb, out double ix, out double iy))
                        {
                            if (ta <= TEps || ta >= 1 - TEps || tb <= TEps || tb >= 1 - TEps) continue; // 端点→吸附
                            double za = Lerp(li[a].Z, li[a + 1].Z, ta);
                            double zb = Lerp(lj[b].Z, lj[b + 1].Z, tb);
                            if (Math.Abs(za - zb) > zSep) continue;                                     // 立交，不打断
                            var jp = new Point3d(ix, iy, (za + zb) * 0.5);                              // 规范化交点（共享）
                            breaks[i].Add((a, jp));
                            breaks[j].Add((b, jp));
                            report.CrossSplits++;
                            continue;
                        }

                        // (c) 半腰贴近（R-N3）：两段不相交，但最近处 ≤tol、且落在**两段各自的内部**。
                        //
                        // 为什么必须单加这一条：X 规则要求真相交，T 规则**只投影每条线的两个端点** ——
                        // "两条线最近处都在各自半腰上"这种局面两条规则都看不见。对现场那张网量过：
                        // 空间上贴到 ≤10m、同高、同片、却不共节点的边对有 <b>226 处</b>，
                        // 其中 50 处逼着车绕行 >200m、10 处 >1000m，最离谱一处
                        // <b>擦身 4.4m、图上要绕 6594m</b>。它不改变连通性（本来就同片），
                        // 所以连通片数一直看着正常，只是每条路默默多绕几百米 —— 这正是现场说的"明明这么走更近"。
                        if (!SegSegNearest(li[a], li[a + 1], lj[b], lj[b + 1],
                                           out double sa, out double sb, out double dist)) continue;
                        if (dist > tol) continue;
                        var pa = LerpP(li[a], li[a + 1], sa);
                        var pb = LerpP(lj[b], lj[b + 1], sb);
                        // 只把「落在多段线**首末点**附近」的让给 T 规则 / 端点吸附。
                        // ⚠ 不能按"段内参数是不是 0/1"来判：两条不相交的线段，最近点几乎总落在某一段的端点上，
                        //   而那个端点是多段线**内部的折点**——吸附只并首末点，内部折点离另一条线 4m 也没人管。
                        //   照 X 规则抄那句"端点→交给吸附"，这条规则就一次都不会触发（实测半腰焊恒为 0）。
                        if (NearPolylineEnd(li, pa, tol) || NearPolylineEnd(lj, pb, tol)) continue;
                        if (Math.Abs(pa.Z - pb.Z) > zSep) continue;                                   // 上下台阶擦肩，不焊
                        // 取两脚中点作**共享**打断点（同 X 规则的规范化交点）：两边断在同一个位置，
                        // 建图时才会吸成同一个节点，而不是留下两个相距 4m 的孤点。
                        var mid = new Point3d((pa.X + pb.X) * 0.5, (pa.Y + pb.Y) * 0.5, (pa.Z + pb.Z) * 0.5);
                        breaks[i].Add((a, mid));
                        breaks[j].Add((b, mid));
                        report.MidSpanWelds++;
                    }
            }

        // (b) T 丁字：每条线两端点投影到其它线的中段（共线重叠的发散边界同样在此打断）。
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
                    if (Math.Abs(proj.Z - ep.Z) > zSep) continue;               // 上下层贴近，非同高
                    if (proj.DistanceTo(lj[0]) <= tol || proj.DistanceTo(lj[^1]) <= tol) continue; // 落 B 端点→吸附
                    breaks[j].Add((seg, new Point3d(proj.X, proj.Y, ep.Z)));    // 用端点 Z，贴合接入
                    report.TeeSplits++;
                }
            }
        }

        var result = new List<IReadOnlyList<Point3d>>();
        for (int i = 0; i < n; i++)
            result.AddRange(SplitPolyline(lines[i], breaks[i], tol));
        return result;
    }

    /// <summary>按打断点把一条多段线切成多条子段（重建顶点序列并标记切点，去近重合）。</summary>
    private static IEnumerable<IReadOnlyList<Point3d>> SplitPolyline(
        IReadOnlyList<Point3d> line, List<(int seg, Point3d p)> brk, double tol)
    {
        if (brk.Count == 0) { yield return line; yield break; }
        brk.Sort((x, y) => x.seg.CompareTo(y.seg));

        // 重建顶点序列，标记哪些 index 是切点；近重合点合并（继承切标记）。
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

    // ── ② 端点吸附成图（原 FromPolylines 主体，保持不变） ─────────────────────────

    private static RoadGraph BuildFromNodedPolylines(
        IEnumerable<IReadOnlyList<Point3d>> polylines, double tol)
    {
        var graph = new RoadGraph();
        // 空间哈希：桶按水平 (X,Y)；同桶内再用三维距离区分标高。
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
            if (from == to) continue;                 // 首尾吸附到同点 → 自环，跳过
            graph.AddEdge(new RoadEdge($"E{edgeSeq++}", from, to, pl));
        }
        return graph;
    }

    /// <summary>
    /// 去共线重叠产生的重复平行边：同一对节点之间、里程相近且质心相近的两条边判为重复，删较长者。
    /// 质心判据避免误删两条真不同的平行路（绕行的会鼓出去，质心分得开）。
    /// </summary>
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

    // ── ③ 缺口桥接：能联通的尽量联通（只连不同连通片的悬挂端点，Kruskal 式不加冗余/错连） ────────

    private readonly record struct BridgeTarget(bool IsNode, string RefId, Point3d Pos);

    /// <summary>
    /// 把分属不同连通片、且水平间距 ≤<paramref name="bridgeTol"/> 的悬挂端点（degree≤1）主动接上：
    /// 每个悬挂端点找最近的"异片"目标（节点 / 边投影），按间距升序 Kruskal 贪心加连接边
    /// （只在不同片间加 → 绝不产生同片冗余边或环），Z 差 &gt; <paramref name="zSep"/> 不桥（不跨标高）。
    /// 接到边中段时打断插节点。返回新增连接边数。
    /// </summary>
    private static int BridgeDangles(RoadGraph g, double bridgeTol, double zSep, double snapTol)
    {
        // 度数 → 悬挂端点（松头）。
        var degree = new Dictionary<string, int>();
        foreach (var n in g.Nodes) degree[n.Id] = 0;
        foreach (var e in g.Edges) { degree[e.FromId]++; degree[e.ToId]++; }
        var dangles = new List<string>();
        foreach (var n in g.Nodes) if (degree[n.Id] <= 1) dangles.Add(n.Id);
        if (dangles.Count == 0) return 0;

        // 初始连通分量。
        var uf = new UnionFind();
        foreach (var n in g.Nodes) uf.Add(n.Id);
        foreach (var e in g.Edges) uf.Union(e.FromId, e.ToId);

        // 每个松头 → 最近的异片目标。
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

        // 按间距升序 Kruskal：仅连接仍属不同片的，避免环 / 错连。
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
            g.AddEdge(new RoadEdge($"BR{seq++}", fromId, targetId, new[] { a, b })
            { SourceRef = RoadEdge.SourceAutoBridge });
            uf.Union(fromId, targetId);
            added++;
        }
        return added;
    }

    /// <summary>
    /// 缺口桥接第二遍：按**片与片之间最近的那对中线点**接，<b>两侧都允许在半腰打断</b>。
    ///
    /// 为什么要有第二遍：<see cref="BridgeDangles"/> 只从悬挂端点（度≤1）出发找目标，
    /// 碎片"离主网最近的地方在自己半腰上"时它永远接不上。对着现场那张网量过 ——
    /// 桥接距离从 25m 调到 50m，连通片 69→37 看着降了一半，<b>最大片里程纹丝不动</b>（40.4%→40.1%）、
    /// 沿里程随机两点的可解率 19.7%→20.3%：接出来的全是碎片跟碎片抱团，路网并没有连通。
    /// 换成本遍这种接法，同样 ≤50m：最大片 40%→76%、可解率 20%→59%（判据见 Tests.RoadLib 的 P3/P4）。
    ///
    /// 两道闸一道不松：
    ///   · 只接**不同**连通片（Kruskal 式按间距升序，绝不加同片冗余边、绝不成环）；
    ///   · 高差 &gt; <paramref name="zSep"/> 的不接 —— 那儿缺的是坡道，硬连一条直线等于造一堵车爬不上去的墙。
    /// </summary>
    private static int BridgeComponentGaps(RoadGraph g, double bridgeTol, double zSep, double snapTol)
    {
        int added = 0, seq = 0;
        // 打断会改几何邻接，理论上一遍就够（缺口全表已含所有片对）；留三轮上限兜住打断后"最近点换了一处"的边角。
        for (int round = 0; round < 3; round++)
        {
            // 不设"小于吸附容差就不接"的下限。第一遍那句 `bestGap > snapTol` 只对**端点对端点**成立
            // （那种早被端点吸附并了）；两条线最近处都在各自半腰上时，T 形打断只投影端点、根本不会去看它，
            // 于是 3m 的缝也能一直断着。现场量过：≤50m 的 43 处平接缺口里有 9 处就是这种 ≤5m 的贴而不连，
            // 而且正是接进主网的那几处 —— 滤掉它们，最大片只到 44.6%；留着，到 75.9%。
            var gaps = RoadConnectivity.AllGaps(g, bridgeTol)                 // 已按间距升序
                .Where(x => Math.Abs(x.DzM) <= zSep && x.GapM > 1e-6)
                .ToList();
            if (gaps.Count == 0) break;

            var uf = new UnionFind();
            foreach (var n in g.Nodes) uf.Add(n.Id);
            foreach (var e in g.Edges) uf.Union(e.FromId, e.ToId);

            int addedThisRound = 0;
            foreach (var gap in gaps)
            {
                var a = AttachAt(g, gap.From, $"GH{seq++}", snapTol, out string anchorA);
                if (a is null) continue;
                var b = AttachAt(g, gap.To, $"GH{seq++}", snapTol, out string anchorB);
                if (b is null || a.Id == b.Id) continue;
                // 打断出来的新节点住在被断那条边的片里 —— 不跟锚点 Union 的话它会被当成独立一片，
                // 「只接不同片」这道闸就形同虚设（同一片里会被反复加冗余边）。
                uf.Union(a.Id, anchorA);
                uf.Union(b.Id, anchorB);
                if (uf.Find(a.Id) == uf.Find(b.Id)) continue;                 // 已经同片：别加冗余边
                g.AddEdge(new RoadEdge($"BRG{seq++}", a.Id, b.Id, new[] { a.Position, b.Position })
                { SourceRef = RoadEdge.SourceAutoBridge });
                uf.Union(a.Id, b.Id);
                added++; addedThisRound++;
            }
            if (addedThisRound == 0) break;
        }
        return added;
    }

    /// <summary>
    /// 把落在中线上的点接进图：贴着已有节点就用它，否则就地打断插一个节点。
    /// <paramref name="anchorId"/> 回一个**已在图里**、与返回节点同片的节点 Id（供并查集续上片属）。
    /// </summary>
    private static RoadNode? AttachAt(RoadGraph g, in Point3d p, string newNodeId, double snapTol, out string anchorId)
    {
        anchorId = "";
        double reach = Math.Max(snapTol, 1.0);
        var e = g.NearestEdgeWithFoot(p, reach, out var foot, out _);
        if (e is null) return null;
        if (g.GetNode(e.FromId) is { } a && a.Position.DistanceTo(foot) <= reach) { anchorId = a.Id; return a; }
        if (g.GetNode(e.ToId) is { } b && b.Position.DistanceTo(foot) <= reach) { anchorId = b.Id; return b; }
        anchorId = e.FromId;
        return g.SplitEdgeAtNearest(e.Id, foot, newNodeId);
    }

    /// <summary>点到一条边中线的最近投影（水平距，Z 按段插值）。中线缺失按两端节点直线兜底。</summary>
    private static void NearestOnEdge(RoadGraph g, RoadEdge e, in Point3d p, out Point3d proj, out double dist)
    {
        IReadOnlyList<Point3d> line = e.Centerline.Count >= 2
            ? e.Centerline
            : new[] { g.GetNode(e.FromId)!.Position, g.GetNode(e.ToId)!.Position };
        NearestOnPolyline(line, p, out _, out _, out proj, out dist);
    }

    /// <summary>并查集（节点 Id；Find 自动补未知 Id，供桥接动态插入的 hub 节点用）。</summary>
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

    // ── 几何原语 ─────────────────────────────────────────────────────────────────

    /// <summary>段上按参数取点。</summary>
    private static Point3d LerpP(in Point3d a, in Point3d b, double t)
        => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    /// <summary>点落在多段线的首末点附近（那是 T 规则与端点吸附的地盘，R-N3 不插手）。</summary>
    private static bool NearPolylineEnd(IReadOnlyList<Point3d> line, in Point3d p, double tol)
        => line.Count >= 2 && (p.DistanceTo(line[0]) <= tol || p.DistanceTo(line[^1]) <= tol);

    /// <summary>
    /// 两条三维线段的最近接近：回各自参数 <paramref name="sa"/>/<paramref name="sb"/>∈[0,1] 与最近距离。
    /// 标准的"两段最近点"解法（退化成点/平行时夹到端点）。R-N3 的几何底座。
    /// </summary>
    private static bool SegSegNearest(in Point3d p0, in Point3d p1, in Point3d q0, in Point3d q1,
                                      out double sa, out double sb, out double dist)
    {
        sa = sb = 0; dist = double.MaxValue;
        double ux = p1.X - p0.X, uy = p1.Y - p0.Y, uz = p1.Z - p0.Z;
        double vx = q1.X - q0.X, vy = q1.Y - q0.Y, vz = q1.Z - q0.Z;
        double wx = p0.X - q0.X, wy = p0.Y - q0.Y, wz = p0.Z - q0.Z;

        double A = ux * ux + uy * uy + uz * uz;
        double B = ux * vx + uy * vy + uz * vz;
        double C = vx * vx + vy * vy + vz * vz;
        double D = ux * wx + uy * wy + uz * wz;
        double E = vx * wx + vy * wy + vz * wz;
        if (A <= 1e-12 || C <= 1e-12) return false;          // 退化段：交给端点吸附

        double den = A * C - B * B;
        if (Math.Abs(den) < 1e-12)
        {
            // 平行：把 a 端夹到 b 上求个代表解即可（真正的共线重叠由 T 规则 + 去重边负责）。
            sa = 0.0;
            sb = Math.Clamp(E / C, 0.0, 1.0);
        }
        else
        {
            sa = Math.Clamp((B * E - C * D) / den, 0.0, 1.0);
            sb = Math.Clamp((A * E - B * D) / den, 0.0, 1.0);
            // 夹过之后另一侧要重解，否则夹到端点时给的不是最近点。
            sb = Math.Clamp((B * sa + E) / C, 0.0, 1.0);
            sa = Math.Clamp((B * sb - D) / A, 0.0, 1.0);
        }
        var a = LerpP(p0, p1, sa);
        var b = LerpP(q0, q1, sb);
        dist = a.DistanceTo(b);
        return true;
    }

    /// <summary>二维线段真相交（解 p+ta·r = q+tb·s）。平行/共线返回 false；ta/tb∈[0,1] 才算。</summary>
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

    /// <summary>点到多段线最近投影（水平距判据，Z 按段插值）。回最近段序号 / 段内参数 / 投影点 / 水平距。</summary>
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
