// 忠实移植自原 PitMine3D Modules/RoadLib/Network/RoadConnectivity.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 两个连通片之间最窄的那处缺口：各自最近的一对中线点 + 水平间隙 + 高差。
/// <see cref="From"/> 在 <see cref="FromComp"/> 片上、<see cref="To"/> 在 <see cref="ToComp"/> 片上，都落在中线上（不是节点）。
/// </summary>
public readonly record struct RoadGap(
    int FromComp, int ToComp, Point3d From, Point3d To, double GapM, double HorizGapM, double DzM)
{
    /// <summary>这处缺口是不是"平接得上"：高差在立交阈值内，接上去是一段能走的平路而不是一堵墙。</summary>
    public bool IsFlat(double zSepM) => Math.Abs(DzM) <= zSepM;
}

/// <summary>
/// 「要接通这两点，得补哪几处缺口」的答案。<see cref="Reachable"/>=false 表示在给定的最大缺口内怎么接都接不通。
/// </summary>
public sealed class RoadBridgePlan
{
    /// <summary>起点所在连通片编号（<see cref="RoadGraph.BuildComponentMap"/> 的编号）。</summary>
    public required int SrcComp { get; init; }
    /// <summary>终点所在连通片编号。</summary>
    public required int DstComp { get; init; }
    /// <summary>全图连通片数（背景量：这张网碎成几块）。</summary>
    public required int ComponentCount { get; init; }
    /// <summary>按顺序要接的缺口链（空 = 本来就同片，或在 maxGap 内接不通）。</summary>
    public required IReadOnlyList<RoadGap> Gaps { get; init; }
    /// <summary>在给定最大缺口内能不能接通。</summary>
    public required bool Reachable { get; init; }

    /// <summary>链里最宽的那处缺口 m（瓶颈；求解按"最大缺口最小"取链，故它就是"最少要接多长"）。</summary>
    public double BottleneckM => Gaps.Count == 0 ? 0 : Gaps.Max(g => g.GapM);

    /// <summary>缺口全是平接（高差 ≤ <paramref name="zSepM"/>）—— 只有这种才可以一键接上，跨标高的缺的是坡道。</summary>
    public bool AllFlat(double zSepM) => Gaps.Count > 0 && Gaps.All(g => g.IsFlat(zSepM));
}

/// <summary>
/// 连通性诊断：不可达时回答「断在哪、要接哪几处、每处多宽」。
///
/// 存在的理由：求解器只答得出"不可达"，<see cref="RoadGraph.BuildComponentMap"/> 也只答得出"你俩不在一片"。
/// 但用户下一步要做的事 —— 去哪儿补一条线 —— 这两个答案都给不了。真实路网碎成 69 片、最大片只占 40% 里程，
/// "两片之间没有任何路"这句话原样回一百遍也修不好一条路。
///
/// 口径：把每条边的中线按 <c>sampleStepM</c> 加密成点、按连通片分组，求两两片之间最近的一对点（三维距）；
/// 再在"片"这一层跑**瓶颈最短路**（路径代价 = 沿途最宽的那处缺口），得出"最大缺口最小"的那条接法。
/// 为什么不是最短路而是瓶颈：链上每处缺口都得真去补，用户关心的是"最长要补多少米"，不是总和。
/// </summary>
public static class RoadConnectivity
{
    /// <summary>中线加密步长 m（缺口测量精度的下限）。</summary>
    public const double SampleStepM = 10.0;

    /// <summary>
    /// 求「把 <paramref name="srcNodeId"/> 接到 <paramref name="dstNodeId"/> 要补的缺口链」。
    /// 同片时返回空链 + <c>Reachable=true</c>；<paramref name="maxGapM"/> 内接不通返回 <c>Reachable=false</c>。
    /// 纯函数（不改图），同图同参必得同结论。
    /// </summary>
    public static RoadBridgePlan PlanBridge(RoadGraph g, string srcNodeId, string dstNodeId,
        double maxGapM = 300.0, double sampleStepM = SampleStepM)
    {
        var comp = g.BuildComponentMap();
        int cs = comp.GetValueOrDefault(srcNodeId, 0), cd = comp.GetValueOrDefault(dstNodeId, 0);
        int total = comp.Count == 0 ? 0 : comp.Values.Distinct().Count();
        var empty = new RoadBridgePlan
        {
            SrcComp = cs, DstComp = cd, ComponentCount = total,
            Gaps = Array.Empty<RoadGap>(), Reachable = cs == cd && cs != 0,
        };
        if (cs == 0 || cd == 0 || cs == cd) return empty;

        var pairs = NearestPairsBetweenComponents(g, comp, maxGapM, sampleStepM);
        if (pairs.Count == 0) return empty;

        // 片级邻接（无向）。
        var adj = new Dictionary<int, List<RoadGap>>();
        void Link(RoadGap gap)
        {
            if (!adj.TryGetValue(gap.FromComp, out var l)) { l = new(); adj[gap.FromComp] = l; }
            l.Add(gap);
        }
        foreach (var gap in pairs.Values)
        {
            Link(gap);
            Link(new RoadGap(gap.ToComp, gap.FromComp, gap.To, gap.From, gap.GapM, gap.HorizGapM, -gap.DzM));
        }

        // 瓶颈最短路（Dijkstra，代价 = 沿途最宽缺口）。
        var best = new Dictionary<int, double> { [cs] = 0.0 };
        var prev = new Dictionary<int, RoadGap>();
        var pq = new PriorityQueue<int, double>();
        pq.Enqueue(cs, 0.0);
        while (pq.TryDequeue(out int u, out double du))
        {
            if (du > best.GetValueOrDefault(u, double.PositiveInfinity)) continue;
            if (u == cd) break;
            foreach (var gap in adj.GetValueOrDefault(u) ?? new List<RoadGap>())
            {
                double nd = Math.Max(du, gap.GapM);
                int v = gap.ToComp;
                if (nd >= best.GetValueOrDefault(v, double.PositiveInfinity)) continue;
                best[v] = nd;
                prev[v] = gap;
                pq.Enqueue(v, nd);
            }
        }
        if (!best.ContainsKey(cd)) return empty;

        var chain = new List<RoadGap>();
        for (int cur = cd; cur != cs && prev.TryGetValue(cur, out var gap); cur = gap.FromComp)
            chain.Add(new RoadGap(gap.FromComp, gap.ToComp, gap.From, gap.To, gap.GapM, gap.HorizGapM, gap.DzM));
        chain.Reverse();

        return new RoadBridgePlan
        {
            SrcComp = cs, DstComp = cd, ComponentCount = total,
            Gaps = chain, Reachable = chain.Count > 0,
        };
    }

    /// <summary>
    /// 全图所有「片与片之间最近的那处缺口」（≤<paramref name="maxGapM"/> 的才收），按宽度升序。
    /// 路网体检 / "补到多宽能连成一片"这类问题问的是全表，不是某一对源汇的那条链。
    /// </summary>
    public static IReadOnlyList<RoadGap> AllGaps(RoadGraph g, double maxGapM = 300.0, double sampleStepM = SampleStepM)
        => NearestPairsBetweenComponents(g, g.BuildComponentMap(), maxGapM, sampleStepM)
            .Values.OrderBy(x => x.GapM).ToList();

    /// <summary>
    /// 各连通片两两之间最近的一对中线点（只留 ≤<paramref name="maxGapM"/> 的）。
    /// 网格哈希：格边长取 <paramref name="maxGapM"/>，3×3 邻域即覆盖搜索半径。
    /// </summary>
    private static Dictionary<(int, int), RoadGap> NearestPairsBetweenComponents(
        RoadGraph g, IReadOnlyDictionary<string, int> comp, double maxGapM, double stepM)
    {
        double cell = Math.Max(1.0, maxGapM);
        var grid = new Dictionary<(long, long), List<(int Comp, Point3d P)>>();
        foreach (var e in g.Edges)
        {
            int c = comp.GetValueOrDefault(e.FromId, 0);
            if (c == 0) continue;
            foreach (var p in Densify(g, e, stepM))
            {
                var key = ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));
                if (!grid.TryGetValue(key, out var bucket)) { bucket = new(); grid[key] = bucket; }
                bucket.Add((c, p));
            }
        }

        var best = new Dictionary<(int, int), RoadGap>();
        foreach (var (key, bucket) in grid)
        {
            var near = new List<(int Comp, Point3d P)>();
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (grid.TryGetValue((key.Item1 + dx, key.Item2 + dy), out var nb)) near.AddRange(nb);

            foreach (var (ca, pa) in bucket)
                foreach (var (cb, pb) in near)
                {
                    if (ca == cb) continue;
                    double d = pa.DistanceTo(pb);
                    if (d > maxGapM) continue;
                    var k = ca < cb ? (ca, cb) : (cb, ca);
                    if (best.TryGetValue(k, out var cur) && cur.GapM <= d) continue;
                    var (fromC, fromP, toC, toP) = ca < cb ? (ca, pa, cb, pb) : (cb, pb, ca, pa);
                    best[k] = new RoadGap(fromC, toC, fromP, toP, d,
                        fromP.HorizontalDistanceTo(toP), toP.Z - fromP.Z);
                }
        }
        return best;
    }

    /// <summary>把一条边的中线加密成间距 ≤<paramref name="stepM"/> 的点串（含两端；无中线退回两端节点）。</summary>
    private static IEnumerable<Point3d> Densify(RoadGraph g, RoadEdge e, double stepM)
    {
        var c = e.Centerline;
        if (c.Count < 2)
        {
            if (g.GetNode(e.FromId) is { } a) yield return a.Position;
            if (g.GetNode(e.ToId) is { } b) yield return b.Position;
            yield break;
        }
        double step = Math.Max(1.0, stepM);
        yield return c[0];
        for (int i = 1; i < c.Count; i++)
        {
            double len = c[i].DistanceTo(c[i - 1]);
            int n = (int)(len / step);
            for (int k = 1; k <= n; k++)
            {
                double t = k * step / len;
                if (t >= 1.0) break;
                yield return new Point3d(
                    c[i - 1].X + (c[i].X - c[i - 1].X) * t,
                    c[i - 1].Y + (c[i].Y - c[i - 1].Y) * t,
                    c[i - 1].Z + (c[i].Z - c[i - 1].Z) * t);
            }
            yield return c[i];
        }
    }
}
