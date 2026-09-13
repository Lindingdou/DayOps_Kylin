// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SimRoadGraphTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

// SimRoadGraph 静态单例是**进程级**状态：测试类之间并行跑会互相踩。整个程序集串行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  SimRoadGraph 离线验收台架
//
//  四条判据（都能证伪，不是只判「成功」）：
//   J1 单例：同一 O-D 调两次拿到同一份图、同一份折线；装载只发生一次。
//   J2 失败只试一次：读库失败时反复问路，尝试次数恒为 1，且**返回未命中而不是抛**。
//   J3 计数自洽：总次数 = 命中 + 各类未命中（Stats.Balanced），漏计一条分支立刻为 false。
//   J4 行为不变：把改造前 TripAnimator 的 FindNode/Polyline 原样抄成**对照实现**，
//      在同一张图上跑同一批 O-D，逐点比折线 —— 不是「跑通了就算过」。
//
//  外加吸附半径口径的两条：
//   J5 半径取【待吸附点→路网】的分布分位数，不是节点间距（合成算例直接把错法证伪）。
//   J6 吸附按平面距：高差不该把有坡道可达的点判成够不着。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SimRoadGraphTests : IDisposable
{
    // 合成路网：一条主干 N1—N2—N3，末端接卸点 N4；N9 是孤立节点（不连通用）。
    // 整体挪离原点：(0,0) 在口径上表示「没有坐标」，节点不能压在那上面。
    private const string N1 = "N1", N2 = "N2", N3 = "N3", N4 = "N4", N9 = "N9";

    private static RoadGraph BuildGraph()
    {
        var g = new RoadGraph();
        g.AddNode(N1, RoadNodeType.Loading, new Point3d(1000, 1000, 100));
        g.AddNode(N2, RoadNodeType.Junction, new Point3d(1200, 1000, 100));
        g.AddNode(N3, RoadNodeType.Junction, new Point3d(1400, 1000, 100));
        var n4 = g.AddNode(N4, RoadNodeType.Unloading, new Point3d(1400, 1300, 120));
        n4.RefId = "DUMP-A";
        g.AddNode(N9, RoadNodeType.Junction, new Point3d(5000, 5000, 100));   // 无边，永远不可达

        // 有中线（3 点，中间拐一下）—— 折线要按中线走，不能只连端点
        g.AddEdge(new RoadEdge("E12", N1, N2, new[]
        {
            new Point3d(1000, 1000, 100), new Point3d(1100, 1040, 100), new Point3d(1200, 1000, 100),
        }));
        // 有中线（2 点）
        g.AddEdge(new RoadEdge("E23", N2, N3, new[]
        {
            new Point3d(1200, 1000, 100), new Point3d(1400, 1000, 100),
        }));
        // 无中线 —— 走 Polyline 里的「拿两端节点兜底」分支
        g.AddEdge(new RoadEdge("E34", N3, N4));
        return g;
    }

    private static void Attach() => SimRoadGraphTestAccess.Attach(BuildGraph());

    public void Dispose() => SimRoadGraph.Invalidate();   // 每个用例后把进程级状态还原

    // ── J1 单例 ──────────────────────────────────────────────────────────────

    [Fact]
    public void J1_同一OD两次拿到同一份图与同一份折线()
    {
        Attach();
        int loadsBefore = SimRoadGraph.GraphLoadCount;

        var src = new Point3d(1000, 1000, 100);
        var dst = new Point3d(1400, 1300, 120);

        var r1 = SimRoadGraph.TryGetRoute(src, dst);
        var g1 = SimRoadGraph.Graph;
        var r2 = SimRoadGraph.TryGetRoute(src, dst);
        var g2 = SimRoadGraph.Graph;

        Assert.True(r1.Hit, r1.MissText);
        Assert.True(r2.Hit, r2.MissText);
        Assert.Same(g1, g2);                                   // 同一份图
        Assert.Same(r1.Points, r2.Points);                     // 同一份折线（第二次由缓存答）
        Assert.Equal(loadsBefore, SimRoadGraph.GraphLoadCount);// 没有第二次装载
        Assert.Equal(1, SimRoadGraph.Stats.CacheHits);
        Assert.Equal(1, SimRoadGraph.Stats.DistinctOd);        // 两次调用只是一条 O-D
    }

    [Fact]
    public void J1b_折线走中线而不是只连端点()
    {
        Attach();
        var r = SimRoadGraph.TryGetRoute(new Point3d(1000, 1000, 100), new Point3d(1400, 1300, 120));
        Assert.True(r.Hit, r.MissText);

        // N1→N2 的中线中间点 (1100,1040) 必须在折线里；只连端点的实现会漏掉它。
        Assert.Contains(r.Points, p => Math.Abs(p.X - 1100) < 1e-6 && Math.Abs(p.Y - 1040) < 1e-6);
        // 首尾就是源汇节点
        Assert.Equal(1000, r.Points[0].X, 6);
        Assert.Equal(1300, r.Points[^1].Y, 6);
        // 平面折线与三维折线点数一致（同一条线的投影，不是两套口径）
        Assert.Equal(r.Points3d.Count, r.Points.Count);
        // 相邻点不允许平面重合（去重口径）
        for (int i = 1; i < r.Points.Count; i++)
            Assert.True((r.Points[i] - r.Points[i - 1]).Length > 1e-6, $"第 {i} 点与前一点平面重合");
    }

    // ── J2 失败只试一次 + 永不抛 ─────────────────────────────────────────────

    [Fact]
    public void J2_读不到路网时反复问路只尝试一次且不抛()
    {
        SimRoadGraph.Invalidate();   // 回到「没装载过」，本进程没有工程库 ⇒ 装载必失败

        var results = new List<SimRoute>();
        for (int i = 0; i < 5; i++)
            results.Add(SimRoadGraph.TryGetRoute(new Point3d(1000, 1000, 0), new Point3d(1400, 1300, 0)));

        Assert.All(results, r => Assert.False(r.Hit));
        Assert.All(results, r => Assert.Equal(SimRouteMiss.NoGraph, r.Miss));
        Assert.All(results, r => Assert.Empty(r.Points));            // 未命中不给假线
        Assert.Equal(1, SimRoadGraph.GraphLoadAttempts);             // ★ 失败只试一次
        Assert.Equal(0, SimRoadGraph.GraphLoadCount);

        var s = SimRoadGraph.Stats;
        Assert.Equal(5, s.Queries);
        Assert.Equal(0, s.Hits);
        Assert.Equal(5, s.MissNoGraph);
        Assert.True(s.Balanced);
    }

    [Fact]
    public void J2b_各种解不出来都返回未命中而不是抛()
    {
        Attach();

        // 不连通（N9 是孤立节点）
        var far = SimRoadGraph.TryGetRoute(new Point3d(1000, 1000, 100), new Point3d(5000, 5000, 100));
        Assert.False(far.Hit);
        Assert.Equal(SimRouteMiss.Unreachable, far.Miss);

        // 源汇吸到同一节点
        var same = SimRoadGraph.TryGetRoute(new Point3d(1000, 1000, 100), new Point3d(1005, 1002, 100));
        Assert.False(same.Hit);
        Assert.Equal(SimRouteMiss.SameNode, same.Miss);

        // 半径外没有节点
        var away = SimRoadGraph.TryGetRoute(new Point3d(9000, 9000, 100), new Point3d(1400, 1300, 120));
        Assert.False(away.Hit);
        Assert.Equal(SimRouteMiss.SourceUnsnapped, away.Miss);

        // XY 全 0 ⇒ 没有坐标，绝不吸附（否则会吸到离原点最近的节点上算出一条假路线）
        var zero = SimRoadGraph.TryGetRoute(new Point3d(0, 0, 0), new Point3d(1400, 1300, 120));
        Assert.False(zero.Hit);
        Assert.Equal(SimRouteMiss.NoPosition, zero.Miss);

        // 汇端 XY 全 0 也一样
        var zero2 = SimRoadGraph.TryGetRoute(new Point3d(1000, 1000, 100), new Point3d(0, 0, 0));
        Assert.Equal(SimRouteMiss.NoPosition, zero2.Miss);
    }

    // ── J3 计数自洽 ──────────────────────────────────────────────────────────

    [Fact]
    public void J3_命中率数得对()
    {
        Attach();
        SimRoadGraph.ResetStats();

        var pit = new Point3d(1000, 1000, 100);
        var dump = new Point3d(1400, 1300, 120);

        SimRoadGraph.TryGetRoute(pit, dump);                                  // 命中
        SimRoadGraph.TryGetRoute(pit, dump);                                  // 命中（缓存）
        SimRoadGraph.TryGetRoute(pit, new Point3d(5000, 5000, 100));          // 不连通
        SimRoadGraph.TryGetRoute(new Point3d(9000, 9000, 0), dump);           // 源未吸附
        SimRoadGraph.TryGetRoute(new Point3d(0, 0, 0), dump);                 // 无坐标
        SimRoadGraph.TryGetRoute(pit, new Point3d(1004, 1001, 100));          // 源汇同点

        var s = SimRoadGraph.Stats;
        Assert.Equal(6, s.Queries);
        Assert.Equal(2, s.Hits);
        Assert.Equal(1, s.MissUnreachable);
        Assert.Equal(1, s.MissSourceUnsnapped);
        Assert.Equal(1, s.MissNoPosition);
        Assert.Equal(1, s.MissSameNode);
        Assert.Equal(4, s.MissTotal);
        Assert.True(s.Balanced);                       // ★ 总数 = 命中 + 各类未命中
        Assert.Equal(2.0 / 6.0, s.HitRate, 9);

        // 不重复 O-D：只有「吸到了两个不同节点」的才会进节点对统计
        Assert.Equal(2, s.DistinctOd);                 // (N1,N4) 与 (N1,N9)
        Assert.Equal(1, s.DistinctOdHits);
        Assert.Equal(0.5, s.DistinctHitRate, 9);

        // 快照是拷贝：拿到手之后再问路，手里这份不该变
        SimRoadGraph.TryGetRoute(pit, dump);
        Assert.Equal(6, s.Queries);
        Assert.Equal(7, SimRoadGraph.Stats.Queries);
    }

    // ── J4 TripAnimator 行为不变（对照改造前的实现）───────────────────────────

    [Fact]
    public void J4_业务标识口与改造前逐点一致()
    {
        var g = BuildGraph();
        SimRoadGraphTestAccess.Attach(g);

        // 每个算例：源键 / 汇键 / 汇坐标（Z 传 0 —— 改造前 TripAnimator 就是这么调的）
        var cases = new (string[] SrcKeys, string[] DstKeys, double X, double Y)[]
        {
            (new[] { N1, "" },        new[] { "DUMP-A", N4 }, 0, 0),           // 双双按名字匹配
            (new[] { N1 },            new[] { "查无此汇", "也查无" }, 1400, 1300),// 汇按坐标兜底（3D 距 120 < 500）
            (new[] { N1 },            new[] { "查无此汇" }, 9000, 9000),        // 汇远在天边 ⇒ 未命中
            (new[] { "查无此面" },     new[] { "DUMP-A" }, 0, 0),               // 源匹配不上且无坐标 ⇒ 未命中
            (new[] { N1 },            new[] { N1 }, 0, 0),                     // 源汇同点 ⇒ 未命中
            (new[] { N9 },            new[] { "DUMP-A" }, 0, 0),               // 不连通 ⇒ 未命中
            (new[] { N4 },            new[] { N1 }, 0, 0),                     // 反向（中线要倒过来）
        };

        int routed = 0;
        foreach (var c in cases)
        {
            var expect = LegacyTripAnimator.RealRoute(g, c.SrcKeys, c.DstKeys, c.X, c.Y);
            if (expect != null) routed++;
            var actual = SimRoadGraph.RouteByKeys(c.SrcKeys, new Point3d(0, 0, 0),
                                                  c.DstKeys, new Point3d(c.X, c.Y, 0));

            string tag = $"[{string.Join("|", c.SrcKeys)} → {string.Join("|", c.DstKeys)} @({c.X},{c.Y})]";
            if (expect == null)
            {
                Assert.False(actual.Hit, $"{tag} 改造前是 null，改造后却命中了（{actual}）");
                continue;
            }
            Assert.True(actual.Hit, $"{tag} 改造前有路线，改造后未命中（{actual.MissText}）");
            Assert.Equal(expect.Count, actual.Points.Count);
            for (int i = 0; i < expect.Count; i++)
            {
                Assert.Equal(expect[i].X, actual.Points[i].X, 9);
                Assert.Equal(expect[i].Y, actual.Points[i].Y, 9);
            }
        }

        // ★ 防空过：如果对照实现一条路都没解出来，上面那堆比对就全走了「两边都是 null」那支，
        //   这个用例会在什么都没验的情况下变绿。至少要有 3 个算例真的解出了路线。
        Assert.True(routed >= 3, $"对照组只解出 {routed} 条路线，比对形同虚设");
    }

    /// <summary>
    /// 改造前 TripAnimator 里那份实现的**逐字复制**（git 71371a8 的 RealRoute/FindNode/Polyline/Push）。
    /// 只作为对照组存在 —— 它和被测实现同源同图同输入，逐点比对，谁漂了都能立刻抓出来。
    /// </summary>
    private static class LegacyTripAnimator
    {
        public static List<SimPoint>? RealRoute(RoadGraph g, IReadOnlyList<string> srcKeys,
                                                IReadOnlyList<string> dstKeys, double dstX, double dstY)
        {
            try
            {
                var solver = new DijkstraPathSolver(g);
                var src = FindNode(g, srcKeys, 0, 0);
                var dst = FindNode(g, dstKeys, dstX, dstY);
                if (src == null || dst == null || src.Id == dst.Id) return null;

                var pr = solver.FindPath(src.Id, dst.Id, new PathQuery { Loaded = true, Mode = WeightMode.Distance });
                if (!pr.Feasible || pr.EdgeIds.Count == 0) return null;

                var pts = Polyline(g, pr, src.Id);
                return pts.Count >= 2 ? pts : null;
            }
            catch { return null; }
        }

        private static RoadNode? FindNode(RoadGraph g, IReadOnlyList<string> keys, double x, double y)
        {
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key)) continue;
                foreach (var n in g.Nodes)
                    if (string.Equals(n.RefId, key, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(n.Id, key, StringComparison.OrdinalIgnoreCase)) return n;
            }
            if (Math.Abs(x) > 1e-6 || Math.Abs(y) > 1e-6) return g.NearestNode(new Point3d(x, y, 0), 500);
            return null;
        }

        private static List<SimPoint> Polyline(RoadGraph g, PathResult pr, string startNodeId)
        {
            var pts = new List<SimPoint>();
            string cur = startNodeId;
            foreach (var eid in pr.EdgeIds)
            {
                var e = g.GetEdge(eid);
                if (e == null) continue;
                bool reversed = string.Equals(e.ToId, cur, StringComparison.OrdinalIgnoreCase);
                var cl = e.Centerline;
                if (cl == null || cl.Count < 2)
                {
                    var na = g.GetNode(reversed ? e.ToId : e.FromId);
                    var nb = g.GetNode(reversed ? e.FromId : e.ToId);
                    if (na != null) Push(pts, new SimPoint(na.Position.X, na.Position.Y));
                    if (nb != null) Push(pts, new SimPoint(nb.Position.X, nb.Position.Y));
                }
                else
                {
                    var seq = reversed ? cl.Reverse().ToList() : cl.ToList();
                    foreach (var p in seq) Push(pts, new SimPoint(p.X, p.Y));
                }
                cur = reversed ? e.FromId : e.ToId;
            }
            return pts;
        }

        private static void Push(List<SimPoint> pts, SimPoint p)
        {
            if (pts.Count > 0 && (pts[^1] - p).Length < 1e-6) return;
            pts.Add(p);
        }
    }

    // ── J5 吸附半径的取法 ────────────────────────────────────────────────────

    [Fact]
    public void J5_半径按点到路网的分布取而不是按节点间距()
    {
        // 合成一张「节点很密、但作业点离得远」的网：中线上每 19 m 一个节点（与实测中位相同量级），
        // 待吸附的点全在 76~157 m 外。这正是错法翻车的形状。
        var g = new RoadGraph();
        string? prev = null;
        for (int i = 0; i < 60; i++)   // 60 个节点 × 19 m ≈ 1.1 km 主干
        {
            string id = $"R{i}";
            g.AddNode(id, RoadNodeType.Junction, new Point3d(1000 + i * 19.0, 1000, 100));
            if (prev != null) g.AddEdge(new RoadEdge($"E{i}", prev, id));
            prev = id;
        }
        SimRoadGraphTestAccess.Attach(g);

        // 作业点：正对着某个节点、垂直于主干摆出去（2D 距就等于设定值，不掺 dx）。
        // 距离分布按实测形状造：中位 76 m、p90 157 m，尾巴拖到 300 m。
        var pts = new List<Point3d>();
        for (int i = 0; i < 100; i++)
        {
            double d = i < 50 ? 20 + i * (76 - 20) / 49.0                // 前 50 个：20→76
                    : i < 90 ? 76 + (i - 49) * (157 - 76) / 40.0        // 中 40 个：76→157
                             : 157 + (i - 89) * (300 - 157) / 10.0;     // 尾 10 个：157→300
            pts.Add(new Point3d(1000 + (i % 60) * 19.0, 1000 + d, 140));
        }

        var cal = SimRoadGraph.Calibrate(pts);
        Assert.Equal(100, cal.Count);
        Assert.Equal(76, cal.P50, 6);
        Assert.Equal(157, cal.P90, 6);

        // ① 错法：半径 = 2 × 节点间距中位数 = 38 m ⇒ 绝大多数点吸不上，
        //    而它们其实全都紧挨着主干 —— 这就是「28.3% 命中」的形状。
        Assert.True(cal.SnapRateAt(2 * 19.0) < 0.35, $"错法居然吸上了 {cal.SnapRateAt(38):P1}");

        // ② 对法：取分布的分位数
        double r = cal.Recommend(0.95);
        Assert.Equal(Math.Ceiling(cal.P95 / 25.0) * 25.0, r, 9);      // 建议值 = p95 上取到 25 m 整数倍
        Assert.True(cal.SnapRateAt(r) >= 0.95, $"建议半径 {r} m 只吸上 {cal.SnapRateAt(r):P1}");

        // ③ 半径不是越大越好：它是「允许当成在路上」的容差，剩下的要老实报未命中
        Assert.True(cal.SnapRateAt(cal.P50) < 0.6);
        Assert.NotEqual(cal.Max, r);

        // ④ 无坐标的点不许混进分布（那不是「离得远」，是没有坐标）
        var cal2 = SimRoadGraph.Calibrate(pts.Concat(new[] { new Point3d(0, 0, 0), new Point3d(0, 0, 500) }));
        Assert.Equal(100, cal2.Count);
        Assert.Equal(2, cal2.NoPositionCount);
    }

    [Fact]
    public void J5b_半径来源必须说得出口()
    {
        Assert.Contains("未在本机现场标定", SimRoadGraph.SnapRadiusSource);
        Assert.Equal(SimRoadGraph.DefaultSnapRadiusM, SimRoadGraph.SnapRadiusM, 9);
        Assert.Throws<ArgumentOutOfRangeException>(() => SimRoadGraph.UseSnapRadius(0, "随便写"));
        Assert.Throws<ArgumentException>(() => SimRoadGraph.UseSnapRadius(200, "  "));

        try
        {
            SimRoadGraph.UseSnapRadius(175, "台架标定：n=100 p95=163 m ⇒ 175 m");
            Assert.Equal(175, SimRoadGraph.SnapRadiusM, 9);
            Assert.Contains("台架标定", SimRoadGraph.SnapRadiusSource);
        }
        finally
        {
            SimRoadGraph.UseSnapRadius(SimRoadGraph.DefaultSnapRadiusM,
                $"缺省 {SimRoadGraph.DefaultSnapRadiusM:0} m（按上游实测分布 p90=157 m 上取，**未在本机现场标定**）");
        }
    }

    // ── J6 吸附按平面距 ──────────────────────────────────────────────────────

    [Fact]
    public void J6_高差不该把有坡道可达的点判成够不着()
    {
        var g = BuildGraph();
        SimRoadGraphTestAccess.Attach(g);

        // 台阶上的作业点：正对 N2(1200,1000,100)，平面 180 m、高出 160 m。
        // 三维距 = √(180² + 160²) ≈ 240.8 m > 200（其余节点更远）；平面距 180 m ≤ 200。
        var onBench = new Point3d(1200, 1180, 260);

        Assert.Null(g.NearestNode(onBench, 200));                 // 三维距那套：判成够不着

        var (node, dist, dz) = SimRoadGraph.TrySnap(onBench, 200); // 平面距这套：吸得上
        Assert.NotNull(node);
        Assert.Equal(N2, node!.Id);
        Assert.Equal(180, dist, 6);
        Assert.Equal(160, dz, 6);                                  // 高差如实报出，不藏进距离里

        // 半径外：节点不给，但距离照报（界面要能说「这个单元离路 700 m」）
        var (none, farDist, _) = SimRoadGraph.TrySnap(new Point3d(1400, 2000, 120), 200);
        Assert.Null(none);
        Assert.Equal(700, farDist, 6);                             // 最近的是 N4(1400,1300)
    }

    // ── 其它 ────────────────────────────────────────────────────────────────

    [Fact]
    public void 换网后计数与缓存一起归零()
    {
        Attach();
        SimRoadGraph.TryGetRoute(new Point3d(1000, 1000, 100), new Point3d(1400, 1300, 120));
        Assert.True(SimRoadGraph.Stats.Queries > 0);

        SimRoadGraph.Invalidate();
        var s = SimRoadGraph.Stats;
        Assert.Equal(0, s.Queries);
        Assert.Equal(0, s.DistinctOd);
        Assert.Equal(0, s.DistinctOdHits);
        Assert.Equal(0, SimRoadGraph.GraphLoadAttempts);
    }

    [Fact]
    public void 外挂图会在来源文案里自报家门()
    {
        Attach();
        Assert.Contains("外挂图", SimRoadGraph.Label);   // 漏到界面上要一眼看得出来不是真存档
    }
}

/// <summary>把 internal 的台架入口收在一处，免得每个用例都写一遍。</summary>
internal static class SimRoadGraphTestAccess
{
    public static void Attach(RoadGraph g) => SimRoadGraph.AttachForTest(g);
}
