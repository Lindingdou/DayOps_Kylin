// 忠实移植自原 PitMine3D Tests/Tests.RoadLib/RoadTopologyTests.cs（仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PitMine3D.Kylin.Tests.Road;

/// <summary>
/// 路网拓扑分类（R-T1 ~ R-T6）的判据。
///
/// 判据设计遵守既有两条纪律：
///   · <b>关掉规则闸必须真的红</b> —— T5 用 A/B 对照证明「接缝合并」这条规则确实在起作用
///     （同一条链，中间插不插接缝，路段数必须都是 1；而边数必须真的不同）。
///   · <b>同色不同义要有闸挡着</b> —— T13 钉死「路网_预览」层上并存的语义色两两不撞，
///     并把「孤立段红 == 缺口红」这条<b>刻意的复用</b>正面断言下来，免得被后人当 bug「修掉」。
/// </summary>
public class RoadTopologyTests
{
    // ── 造图助手 ───────────────────────────────────────────────────────────

    private static Point3d P(double x, double y = 0, double z = 0) => new(x, y, z);

    /// <summary>加一条直线边（自动补两端节点）。</summary>
    private static RoadEdge Add(RoadGraph g, string id, string from, Point3d a, string to, Point3d b)
    {
        if (g.GetNode(from) is null) g.AddNode(from, RoadNodeType.Junction, a);
        if (g.GetNode(to) is null) g.AddNode(to, RoadNodeType.Junction, b);
        return g.AddEdge(new RoadEdge(id, from, to, new[] { a, b }));
    }

    /// <summary>一条被打断成 n 段的直链：N0—N1—…—Nn，每段长 100m（沿 X）。</summary>
    private static RoadGraph Chain(int n, string prefix = "N")
    {
        var g = new RoadGraph();
        for (int i = 0; i < n; i++)
            Add(g, $"E{i}", $"{prefix}{i}", P(i * 100), $"{prefix}{i + 1}", P((i + 1) * 100));
        return g;
    }

    // ── T1~T4 节点分类（R-T1） ─────────────────────────────────────────────

    [Fact]
    public void T1_度数决定节点类别_接缝不是路口()
    {
        // N0—N1—N2 是一条链：两头端点、中间接缝。
        var t = RoadTopology.Analyze(Chain(2));

        Assert.Equal(RoadNodeClass.Endpoint, t.NodeClass["N0"]);
        Assert.Equal(RoadNodeClass.Seam, t.NodeClass["N1"]);
        Assert.Equal(RoadNodeClass.Endpoint, t.NodeClass["N2"]);
        Assert.Equal(0, t.JunctionCount);          // 中间那个点不是路口
        Assert.Equal(1, t.SeamCount);
        Assert.Equal(2, t.RealNodeCount);          // 只有两头算真节点
    }

    [Fact]
    public void T2_丁字与多岔分开_度3是丁字度4是多岔()
    {
        var g = new RoadGraph();
        Add(g, "E0", "H", P(0), "A", P(100));
        Add(g, "E1", "H", P(0), "B", P(-100));
        Add(g, "E2", "H", P(0), "C", P(0, 100));
        var t3 = RoadTopology.Analyze(g);
        Assert.Equal(RoadNodeClass.Tee, t3.NodeClass["H"]);

        Add(g, "E3", "H", P(0), "D", P(0, -100));
        var t4 = RoadTopology.Analyze(g);
        Assert.Equal(RoadNodeClass.Multi, t4.NodeClass["H"]);
        Assert.Equal(1, t4.JunctionCount);
        Assert.Equal(4, t4.DangleCount);
    }

    [Fact]
    public void T3_孤立点归孤立类_不计入路口()
    {
        var g = Chain(1);
        g.AddNode("LONE", RoadNodeType.Junction, P(500, 500));
        var t = RoadTopology.Analyze(g);

        Assert.Equal(RoadNodeClass.Isolated, t.NodeClass["LONE"]);
        Assert.Equal(0, t.JunctionCount);
        Assert.Equal(3, t.DangleCount);            // 两个端点 + 一个孤立点
    }

    [Fact]
    public void T4_装卸点无论度数都算真节点_不被路段吞掉()
    {
        // A—D—B，D 度=2 但它是卸载点：必须在 D 处断成两条路段，否则源汇就被吞进一条线里了。
        var g = new RoadGraph();
        Add(g, "E0", "A", P(0), "D", P(100));
        Add(g, "E1", "D", P(100), "B", P(200));
        Assert.Single(RoadTopology.Analyze(g).Segments);   // 对照组：D 还是普通点时被当接缝，两边并成一条

        g.GetNode("D")!.Type = RoadNodeType.Unloading;
        var t = RoadTopology.Analyze(g);
        Assert.Equal(2, t.Segments.Count);
        Assert.Contains(t.Segments, s => s.FromId == "D" || s.ToId == "D");
        Assert.Equal(0, t.SeamCount);              // 装卸点不算接缝
        Assert.Equal(3, t.RealNodeCount);
    }

    // ── T5~T8 路段合并（R-T2） ─────────────────────────────────────────────

    [Fact]
    public void T5_接缝处串成一条路段_插多少接缝都还是一条()
    {
        // A/B 对照：同一条 400m 的路，被打断成 1 段 / 4 段，路段数必须都是 1，里程必须一致。
        var one = RoadTopology.Analyze(BuildSingle(400));
        var four = RoadTopology.Analyze(Chain(4));

        Assert.Single(one.Segments);
        Assert.Single(four.Segments);
        Assert.Single(one.Segments[0].EdgeIds);
        Assert.Equal(4, four.Segments[0].EdgeIds.Count);           // 闸门反向：边确实是 4 条，不是碰巧只有 1 条
        Assert.Equal(400.0, one.Segments[0].LengthM, 6);
        Assert.Equal(400.0, four.Segments[0].LengthM, 6);
    }

    private static RoadGraph BuildSingle(double len)
    {
        var g = new RoadGraph();
        Add(g, "E0", "N0", P(0), "N1", P(len));
        return g;
    }

    [Fact]
    public void T6_路段中线拼接不吞里程_也不留重复点()
    {
        var t = RoadTopology.Analyze(Chain(4));
        var s = t.Segments[0];

        // 4 段共 5 个顶点：接缝处共享的那个点只能出现一次。
        Assert.Equal(5, s.Polyline.Count);
        double walked = 0;
        for (int i = 1; i < s.Polyline.Count; i++) walked += s.Polyline[i].DistanceTo(s.Polyline[i - 1]);
        Assert.Equal(s.LengthM, walked, 6);
        Assert.Equal(t.TotalLengthM, t.Segments.Sum(x => x.LengthM), 6);
    }

    [Fact]
    public void T7_边方向不一致时中线要倒过来拼_不许拼成锯齿()
    {
        // 第二条边故意反着存（N2→N1），拼出来仍必须是单调前进的 0→100→200。
        var g = new RoadGraph();
        Add(g, "E0", "N0", P(0), "N1", P(100));
        g.AddNode("N2", RoadNodeType.Junction, P(200));
        g.AddEdge(new RoadEdge("E1", "N2", "N1", new[] { P(200), P(100) }));

        var s = RoadTopology.Analyze(g).Segments.Single();
        var xs = s.Polyline.Select(p => p.X).ToList();
        Assert.Equal(new[] { 0.0, 100.0, 200.0 }, xs.OrderBy(v => v).ToList());
        for (int i = 1; i < xs.Count; i++)
            Assert.True(Math.Abs(xs[i] - xs[i - 1]) > 1e-9, "拼接后出现重复点（锯齿/回头）");
        Assert.Equal(200.0, s.LengthM, 6);
    }

    [Fact]
    public void T8_路段在路口处断开_三岔出三条路段()
    {
        var g = new RoadGraph();
        Add(g, "E0", "H", P(0), "A", P(100));
        Add(g, "E1", "H", P(0), "B", P(-100));
        Add(g, "E2", "H", P(0), "C", P(0, 100));

        var t = RoadTopology.Analyze(g);
        Assert.Equal(3, t.Segments.Count);
        Assert.All(t.Segments, s => Assert.True(s.FromId == "H" || s.ToId == "H"));
    }

    // ── T9~T12 路段分类（R-T3） ────────────────────────────────────────────

    [Fact]
    public void T9_三类各自归位_干线支线孤立段()
    {
        //        A          D
        //        |          |
        //  X ——— H1 ——————— H2 ——— Y      另有一条 P—Q 谁也不挨着
        var g = new RoadGraph();
        Add(g, "E0", "H1", P(0), "A", P(0, 100));
        Add(g, "E1", "H1", P(0), "X", P(-100));
        Add(g, "E2", "H1", P(0), "H2", P(200));
        Add(g, "E3", "H2", P(200), "D", P(200, 100));
        Add(g, "E4", "H2", P(200), "Y", P(300));
        Add(g, "E5", "P", P(1000), "Q", P(1100));

        var t = RoadTopology.Analyze(g);
        var byClass = t.Segments.GroupBy(s => s.Class).ToDictionary(k => k.Key, v => v.Select(s => s.Id).ToList());

        Assert.Equal(1, t.SegmentCountByClass[(int)RoadSegmentClass.Trunk]);      // H1—H2
        Assert.Equal(4, t.SegmentCountByClass[(int)RoadSegmentClass.Spur]);       // 四条伸出去的
        Assert.Equal(1, t.SegmentCountByClass[(int)RoadSegmentClass.Isolated]);   // P—Q
        Assert.Contains("E2", byClass[RoadSegmentClass.Trunk]);
        Assert.Contains("E5", byClass[RoadSegmentClass.Isolated]);
    }

    [Fact]
    public void T10_修到装卸点为止不算断头_是干线不是支线()
    {
        // H(三岔) ——— C(破碎站，度1)。终点是汇，不是缺口。
        var g = new RoadGraph();
        Add(g, "E0", "H", P(0), "A", P(0, 100));
        Add(g, "E1", "H", P(0), "B", P(0, -100));
        Add(g, "E2", "H", P(0), "C", P(200));
        g.GetNode("C")!.Type = RoadNodeType.Unloading;

        var t = RoadTopology.Analyze(g);
        var seg = t.Segments.Single(s => s.Id == "E2");
        Assert.Equal(RoadSegmentClass.Trunk, seg.Class);

        // 反向闸：C 若不是卸载点，同一张图这条必须掉成支线（否则这条规则等于没写）。
        g.GetNode("C")!.Type = RoadNodeType.Junction;
        Assert.Equal(RoadSegmentClass.Spur, RoadTopology.Analyze(g).Segments.Single(s => s.Id == "E2").Class);
    }

    [Fact]
    public void T11_全接缝的孤立环归孤立段_挂在路口上的环归干线()
    {
        // 孤立环：4 个点首尾相接，每个点度都是 2。
        var ring = new RoadGraph();
        Add(ring, "E0", "R0", P(0), "R1", P(100));
        Add(ring, "E1", "R1", P(100), "R2", P(100, 100));
        Add(ring, "E2", "R2", P(100, 100), "R3", P(0, 100));
        Add(ring, "E3", "R3", P(0, 100), "R0", P(0));

        var t = RoadTopology.Analyze(ring);
        var s = t.Segments.Single();
        Assert.True(s.IsLoop);
        Assert.Equal(RoadSegmentClass.Isolated, s.Class);
        Assert.Equal(400.0, s.LengthM, 6);

        // 同一个环挂到一个真路口上（R0 再引出两条腿 → 度 4）：环就成了从路口绕出去又回来的干线。
        Add(ring, "E4", "R0", P(0), "T1", P(-100));
        Add(ring, "E5", "R0", P(0), "T2", P(0, -100));
        var loop2 = RoadTopology.Analyze(ring).Segments.Single(x => x.IsLoop);
        Assert.Equal(RoadSegmentClass.Trunk, loop2.Class);
    }

    [Fact]
    public void T12_每条边只属于一条路段_不重不漏()
    {
        var g = new RoadGraph();
        Add(g, "E0", "H", P(0), "A", P(0, 100));
        Add(g, "E1", "H", P(0), "B", P(0, -100));
        Add(g, "E2", "H", P(0), "M", P(200));
        Add(g, "E3", "M", P(200), "K", P(400));      // M 是接缝
        Add(g, "E4", "K", P(400), "L", P(600));      // K 是接缝
        Add(g, "E5", "P", P(1000), "Q", P(1100));

        var t = RoadTopology.Analyze(g);
        var all = t.Segments.SelectMany(s => s.EdgeIds).ToList();
        Assert.Equal(g.EdgeCount, all.Count);                       // 不漏
        Assert.Equal(g.EdgeCount, all.Distinct().Count());          // 不重
        Assert.Equal(g.Edges.Select(e => e.Id).OrderBy(x => x), all.OrderBy(x => x));
    }

    // ── T13 配色闸（R-T4） ─────────────────────────────────────────────────

    [Fact]
    public void T13_路网预览层的语义色两两不撞_孤立段红是刻意复用缺口红()
    {
        var palette = new Dictionary<string, Rgb>
        {
            ["干线"] = RoadSymbology.ColorOf(RoadSegmentClass.Trunk),
            ["支线"] = RoadSymbology.ColorOf(RoadSegmentClass.Spur),
            ["路口白环"] = RoadSymbology.Junction,
            ["源"] = RoadSymbology.Loading,
            ["汇"] = RoadSymbology.Unloading,
        };
        var dup = palette.GroupBy(kv => kv.Value).Where(x => x.Count() > 1).ToList();
        Assert.True(dup.Count == 0,
            "同色不同义：" + string.Join(" / ", dup.Select(x => string.Join("==", x.Select(kv => kv.Key)))));

        // 孤立段线色 == 悬挂端点环色，是**同义复用**（都读作「没接上」），不是撞色 ——
        // 孤立段两端本来就各顶着一个缺口红环。写成正面断言，免得后人当 bug 改掉。
        Assert.Equal(RoadSymbology.Gap, RoadSymbology.ColorOf(RoadSegmentClass.Isolated));
        Assert.Equal(RoadSymbology.Trunk, RoadSymbology.ColorOf(RoadSegmentClass.Trunk));
    }

    /// <summary>
    /// T13b 寻径结果色必须**离路网底图足够远**（R-T4 的延伸）。
    ///
    /// 现场 2026-08-17：「计算出来的线路的显示不明显」。原因是结果线用的绿 (0,200,90) 与干线绿
    /// (60,200,90) 只差一个 R 分量、回程蓝 (0,120,255) 又贴着汇蓝/支线灰蓝 —— 而寻径结果<b>几乎总是
    /// 叠在「路网预览」上看</b>，等于在绿底上再描一遍绿。所以这一组不能只判"不完全相等"，
    /// 要判**通道距离**：任一分量差 ≥90 才算认得出来。
    /// </summary>
    [Fact]
    public void T13b_寻径结果色与路网底图色离得开_不是仅仅不相等()
    {
        var route = new Dictionary<string, Rgb>
        {
            ["去程"] = RoadSymbology.RouteOutbound,
            ["仅回程"] = RoadSymbology.RouteReturn,
        };
        var backdrop = new Dictionary<string, Rgb>
        {
            ["干线绿"] = RoadSymbology.Trunk,
            ["支线灰蓝"] = RoadSymbology.SegSpur,
            ["孤立段/缺口红"] = RoadSymbology.Gap,
            ["路口白"] = RoadSymbology.Junction,
            ["源"] = RoadSymbology.Loading,
            ["汇"] = RoadSymbology.Unloading,
            ["中线琥珀"] = new Rgb(242, 165, 23),
        };

        static int Far(Rgb a, Rgb b) => Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

        foreach (var (rn, rc) in route)
            foreach (var (bn, bc) in backdrop)
                Assert.True(Far(rc, bc) >= 90,
                    $"{rn} 与底图「{bn}」太近（最大通道差 {Far(rc, bc)}）—— 叠在路网预览上会认不出来");

        // 三条路线色两两也要分得开：它们会**同屏并排**（去程 / 仅回程 / 你指的那条）。
        foreach (var (an, ac) in route)
            foreach (var (bn, bc) in route)
                if (!string.Equals(an, bn, StringComparison.Ordinal))
                    Assert.True(Far(ac, bc) >= 50, $"路线色「{an}」与「{bn}」太近（{Far(ac, bc)}）—— 并排对照时分不出谁是谁");

        // 廊带必须是深色：它的作用是给亮芯线镶一条边，浅色等于没画。
        var casing = RoadSymbology.RouteCasing;
        Assert.True(casing.R + casing.G + casing.B <= 150, "廊带色不够深，压不住底图");
    }

    /// <summary>
    /// T13c 寻径结果**必须比它压着的底图粗**（第二次翻车的那条）。
    ///
    /// 换完色相现场还是说「路网比较粗，但是高亮很细」—— 光换色不够，宽度也得赢。
    /// 渲染端换算是 <c>widthPx = lineweight × 4</c> 且<b>封顶 16px</b>（<c>EntityGpuCache.cpp:222</c>）：
    /// 干线 2.0 → 8px，旧版高亮 0.8 → 3.2px，只有路网的四成宽。芯线拉到 4.0（=16px 上限、干线的两倍）
    /// 是这套引擎里"线"能做到的极限；再要更明显只能靠<b>世界尺度</b>的廊带（放大才起作用），
    /// 两者缺一不可 —— 这条判据只钉线宽那一半，廊带那一半在 DrawRoute 的注释里。
    /// </summary>
    [Fact]
    public void T13c_寻径芯线必须比路网干线粗_且不超过引擎上限()
    {
        static double Px(float lw) => Math.Min(lw * 4.0, 16.0);   // 与 EntityGpuCache 同一条换算

        double route = Px(RoadSymbology.RouteCoreLineweight);
        double trunk = Px(RoadSymbology.NetworkTrunkLineweight);

        Assert.True(route >= trunk * 2, $"芯线 {route:F0}px 不到干线 {trunk:F0}px 的两倍，压不住");
        Assert.Equal(16.0, route);   // 顶到上限：再调大只是写着好看，渲染端会裁掉
    }

    // ── T14~T16 可通行轴与 delta（给「边状态」「增量增删边」用） ─────────────

    [Fact]
    public void T14_封边只动可通行轴_结构轴纹丝不动()
    {
        // A—H—B 一条干线穿过路口 H，另加两条腿让 H 成为路口。
        var g = new RoadGraph();
        Add(g, "E0", "H1", P(0), "A", P(0, 100));
        Add(g, "E1", "H1", P(0), "B", P(0, -100));
        Add(g, "E2", "H1", P(0), "H2", P(200));
        Add(g, "E3", "H2", P(200), "C", P(200, 100));
        Add(g, "E4", "H2", P(200), "D", P(200, -100));

        var s0 = RoadTopology.Analyze(g);
        var p0 = RoadTopology.Analyze(g, passableOnly: true);
        Assert.Equal(s0.Summary, p0.Summary);                      // 全 Open 时两根轴一致

        g.GetEdge("E2")!.Status = RoadEdgeStatus.Closed;
        var s1 = RoadTopology.Analyze(g);
        var p1 = RoadTopology.Analyze(g, passableOnly: true);

        Assert.Equal("", RoadTopology.DescribeDelta(s0, s1));       // 结构轴：什么都没变
        Assert.NotEqual("", RoadTopology.DescribeDelta(p0, p1));    // 可通行轴：现形
        Assert.Equal(1, s1.ComponentCount);
        Assert.Equal(2, p1.ComponentCount);                         // 封了唯一的通道 → 两片
        Assert.Equal(0, p1.SegmentCountByClass[(int)RoadSegmentClass.Trunk]);
    }

    [Fact]
    public void T15_加一条边把孤立段接进网_delta要报出降级链()
    {
        // 一个三岔星（H1—A/B/C）＋ 一条谁也不挨着的 P—Q。
        var g = new RoadGraph();
        Add(g, "E0", "H1", P(0), "A", P(0, 100));
        Add(g, "E1", "H1", P(0), "B", P(0, -100));
        Add(g, "E2", "H1", P(0), "C", P(-100));
        Add(g, "E3", "P", P(500), "Q", P(600));

        var before = RoadTopology.Analyze(g);
        Assert.Equal(2, before.ComponentCount);
        Assert.Equal(3, before.SegmentCountByClass[(int)RoadSegmentClass.Spur]);
        Assert.Equal(1, before.SegmentCountByClass[(int)RoadSegmentClass.Isolated]);

        // 从路口接一条边到 P：P 由悬挂端点变成接缝，P—Q 被并进来，孤立段降级成支线。
        g.AddEdge(new RoadEdge("BR0", "H1", "P", new[] { P(0), P(500) }));
        var after = RoadTopology.Analyze(g);

        Assert.Equal(1, after.ComponentCount);
        Assert.Equal(0, after.SegmentCountByClass[(int)RoadSegmentClass.Isolated]);
        Assert.Equal(4, after.SegmentCountByClass[(int)RoadSegmentClass.Spur]);
        var joined = after.Segments.Single(s => s.EdgeIds.Contains("BR0"));
        Assert.Equal(new[] { "BR0", "E3" }, joined.EdgeIds);        // 两条边串成一条路段
        Assert.Equal("Q", joined.ToId);

        string d = RoadTopology.DescribeDelta(before, after);
        Assert.Contains("连通片 2→1", d);
        Assert.Contains("孤立段 1→0", d);
        Assert.Contains("支线 3→4", d);
    }

    [Fact]
    public void T15b_两条孤立段首尾接起来_仍然是孤立段()
    {
        // 反向闸：接上了 ≠ 接进网。A—B 与 C—D 用一条桥接边串起来，
        // 得到的还是一条两头悬空的线，必须仍判孤立段 —— 否则「孤立段」这一档就成了「连通片计数」的马甲。
        var g = new RoadGraph();
        Add(g, "E0", "A", P(0), "B", P(100));
        Add(g, "E1", "C", P(500), "D", P(600));
        g.AddEdge(new RoadEdge("BR0", "B", "C", new[] { P(100), P(500) }));

        var t = RoadTopology.Analyze(g);
        Assert.Equal(1, t.ComponentCount);
        Assert.Single(t.Segments);
        Assert.Equal(RoadSegmentClass.Isolated, t.Segments[0].Class);
        Assert.Equal(0, t.JunctionCount);
    }

    [Fact]
    public void T16_没动就不报_delta是空串()
    {
        var g = Chain(3);
        Assert.Equal("", RoadTopology.DescribeDelta(RoadTopology.Analyze(g), RoadTopology.Analyze(g)));
    }

    // ── T19~T24 人工改判（R-T7） ───────────────────────────────────────────

    /// <summary>三岔星 H—A/B/C，再从 H 拉一条长支线到 F（模拟"尽头停在工作面的主运输坡道"）。</summary>
    private static RoadGraph StarWithLongSpur()
    {
        var g = new RoadGraph();
        Add(g, "E0", "H", P(0), "A", P(0, 100));
        Add(g, "E1", "H", P(0), "B", P(0, -100));
        Add(g, "E2", "H", P(0), "M", P(400));      // M 是接缝
        Add(g, "E3", "M", P(400), "F", P(900));    // F 悬挂 —— 自动判据必判支线
        return g;
    }

    [Fact]
    public void T19_人工改判压过自动判据_并记下原判()
    {
        var g = StarWithLongSpur();
        var seg = RoadTopology.Analyze(g).SegmentByEdge["E2"];
        Assert.Equal(RoadSegmentClass.Spur, seg.Class);      // 自动：一端悬空 → 支线
        Assert.False(seg.IsManual);

        // 按路段下达：写到该路段每一条边。
        foreach (var id in seg.EdgeIds) g.GetEdge(id)!.RoadClass = RoadSegmentClass.Trunk;

        var after = RoadTopology.Analyze(g).SegmentByEdge["E2"];
        Assert.Equal(RoadSegmentClass.Trunk, after.Class);
        Assert.True(after.IsManual);
        Assert.Equal(RoadSegmentClass.Spur, after.AutoClass);   // 原判留着，说得清"改自哪一档"
        Assert.False(after.Stale);
    }

    [Fact]
    public void T20_改判置空即恢复自动()
    {
        var g = StarWithLongSpur();
        foreach (var e in g.Edges) e.RoadClass = RoadSegmentClass.Trunk;
        // 4 条边只并出 3 条路段（E2+E3 在接缝 M 处串成一条）——改判是按路段计的。
        Assert.Equal(3, RoadTopology.Analyze(g).ManualCount);

        foreach (var e in g.Edges) e.RoadClass = null;
        var t = RoadTopology.Analyze(g);
        Assert.Equal(0, t.ManualCount);
        Assert.Equal(RoadSegmentClass.Spur, t.SegmentByEdge["E2"].Class);
    }

    [Fact]
    public void T21_改判只盖住一小截时失效_退回自动而不是拿零头定性()
    {
        // 只给 E2(400m) 打改判，路段总长 900m —— 占 44%，不够 ManualQuorum，必须退回自动。
        var g = StarWithLongSpur();
        g.GetEdge("E2")!.RoadClass = RoadSegmentClass.Trunk;

        var seg = RoadTopology.Analyze(g).SegmentByEdge["E2"];
        Assert.Equal(RoadSegmentClass.Spur, seg.Class);     // 退回自动
        Assert.False(seg.IsManual);
        Assert.True(seg.Stale);                             // 但要说出来：这里有条失效的改判
        Assert.Equal(1, RoadTopology.Analyze(g).StaleManualCount);

        // 反向闸：补上另一半（500m），占比过半，改判就该生效 —— 否则这条阈值等于把改判整个关掉。
        g.GetEdge("E3")!.RoadClass = RoadSegmentClass.Trunk;
        var ok = RoadTopology.Analyze(g).SegmentByEdge["E2"];
        Assert.Equal(RoadSegmentClass.Trunk, ok.Class);
        Assert.True(ok.IsManual);
        Assert.False(ok.Stale);
    }

    [Fact]
    public void T22_改判在插交叉口后仍然活着_两半都继承()
    {
        var g = StarWithLongSpur();
        var seg = RoadTopology.Analyze(g).SegmentByEdge["E2"];
        foreach (var id in seg.EdgeIds) g.GetEdge(id)!.RoadClass = RoadSegmentClass.Trunk;

        // 在这条路段中间插一个交叉口：E3 被切成 E3_a / E3_b。
        g.SplitEdgeAtNearest("E3", P(650), "NEWJ");
        Assert.Equal(RoadSegmentClass.Trunk, g.GetEdge("E3_a")!.RoadClass);
        Assert.Equal(RoadSegmentClass.Trunk, g.GetEdge("E3_b")!.RoadClass);

        // 插的是度=2 的接缝，路段没被拆开，改判仍覆盖全长 → 依旧生效。
        var after = RoadTopology.Analyze(g).SegmentByEdge["E2"];
        Assert.True(after.IsManual);
        Assert.Equal(RoadSegmentClass.Trunk, after.Class);
    }

    [Fact]
    public void T23_改判随存档往返_旧存档没这个字段按自动读()
    {
        var g = StarWithLongSpur();
        g.GetEdge("E2")!.RoadClass = RoadSegmentClass.Trunk;
        g.GetEdge("E3")!.RoadClass = RoadSegmentClass.Trunk;

        var back = RoadGraphSerializer.FromJson(RoadGraphSerializer.ToJson(g));
        Assert.Equal(RoadSegmentClass.Trunk, back.GetEdge("E2")!.RoadClass);
        Assert.Null(back.GetEdge("E0")!.RoadClass);
        Assert.Equal(RoadSegmentClass.Trunk, RoadTopology.Analyze(back).SegmentByEdge["E2"].Class);

        // 旧存档（DTO 里根本没有 Cls 这个键）必须读成「未改判」，不能变成 (RoadSegmentClass)0 = 干线。
        string legacy = "{\"N\":[{\"Id\":\"A\",\"T\":2,\"X\":0,\"Y\":0,\"Z\":0},{\"Id\":\"B\",\"T\":2,\"X\":100,\"Y\":0,\"Z\":0}],"
                      + "\"E\":[{\"Id\":\"E0\",\"F\":\"A\",\"To\":\"B\",\"C\":[0,0,0,100,0,0],\"Len\":100}]}";
        var old = RoadGraphSerializer.FromJson(legacy);
        Assert.Null(old.GetEdge("E0")!.RoadClass);
        Assert.Equal(0, RoadTopology.Analyze(old).ManualCount);
    }

    [Fact]
    public void T24_改判在克隆与delta里都跟得上()
    {
        var g = StarWithLongSpur();
        var before = RoadTopology.Analyze(g);

        var draft = g.Clone();                                  // 编辑会话拿的是克隆
        var seg = RoadTopology.Analyze(draft).SegmentByEdge["E2"];
        foreach (var id in seg.EdgeIds) draft.GetEdge(id)!.RoadClass = RoadSegmentClass.Trunk;

        // 自动判据下三条路段全是支线（H 是路口，A/B/F 都悬空）；改判后那条长的成了干线。
        string d = RoadTopology.DescribeDelta(before, RoadTopology.Analyze(draft));
        Assert.Contains("干线 0→1", d);
        Assert.Contains("支线 3→2", d);
        Assert.Contains("人工改判 0→1", d);
    }

    // ── T17~T18 确定性与退化输入 ───────────────────────────────────────────

    [Fact]
    public void T17_同一张图两次分析结果逐字段一致()
    {
        var g = new RoadGraph();
        Add(g, "E0", "H", P(0), "A", P(0, 100));
        Add(g, "E1", "H", P(0), "B", P(0, -100));
        Add(g, "E2", "H", P(0), "M", P(200));
        Add(g, "E3", "M", P(200), "K", P(400));
        Add(g, "E4", "P", P(1000), "Q", P(1100));

        var a = RoadTopology.Analyze(g);
        var b = RoadTopology.Analyze(g);
        Assert.Equal(a.Segments.Select(s => s.Id), b.Segments.Select(s => s.Id));
        Assert.Equal(a.Segments.Select(s => s.Class), b.Segments.Select(s => s.Class));
        Assert.Equal(a.Segments.Select(s => string.Join(",", s.EdgeIds)), b.Segments.Select(s => string.Join(",", s.EdgeIds)));
        Assert.Equal(a.Summary, b.Summary);
    }

    [Fact]
    public void T18_空图与无边图不抛_各计数为零()
    {
        var empty = RoadTopology.Analyze(new RoadGraph());
        Assert.Empty(empty.Segments);
        Assert.Equal(0, empty.ComponentCount);
        Assert.Equal(0, empty.RealNodeCount);

        var nodesOnly = new RoadGraph();
        nodesOnly.AddNode("A", RoadNodeType.Junction, P(0));
        nodesOnly.AddNode("B", RoadNodeType.Junction, P(100));
        var t = RoadTopology.Analyze(nodesOnly);
        Assert.Empty(t.Segments);
        Assert.Equal(2, t.ComponentCount);
        Assert.Equal(2, t.DangleCount);           // 孤立点也算悬挂
        Assert.Equal(0, t.JunctionCount);
    }
}
