using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 创建工程位置（§三六四）：工作帮模板 ⊕ 端帮对接的几何层（忠实原 EngineeringPositionBuilder）。
/// 口径：采场范围 = 可采范围环；端帮断口 = 采场台阶被环<b>横切走向</b>的边分割出的断口（自带真实 XYZ + 退路）；
/// 模板节点 = 模板台阶线两端；逐端点档所有端点各成一个节点、一律开放；连线四条闸：不同节点 / 不是同一条线的两头 / 同角色 / 不成环。
/// </summary>
public class EngineeringPositionTests
{
    // 走向沿 x。范围环 x∈[100,200]、y∈[−50,50]：两条竖边 x=100 / x=200 横切走向 ⇒ 端；两条横边沿走向 ⇒ 不是端。
    private static double[] Ring() => new double[] { 100, -50, 200, -50, 200, 50, 100, 50 };

    private static EpPolyline Line(ulong h, string layer, double z, params (double x, double y)[] xy)
    {
        var xyz = new double[xy.Length * 3];
        for (int i = 0; i < xy.Length; i++) { xyz[i * 3] = xy[i].x; xyz[i * 3 + 1] = xy[i].y; xyz[i * 3 + 2] = z; }
        return new EpPolyline(h, layer, xyz, false);
    }

    private static (List<EpPolyline> tpl, List<EpPolyline> pit) Basic()
    {
        var tpl = new List<EpPolyline>
        {
            Line(1, "台阶_1200", 1200, (110, 0), (190, 0)),
            Line(2, "台阶_1210", 1210, (110, 10), (190, 10)),
        };
        var pit = new List<EpPolyline>
        {
            Line(3, "0", 1200, (0, 5), (300, 5)),
            Line(4, "0", 1211, (0, 15), (300, 15)),     // 与模板 1210 差 1m —— Δz 得看得见
        };
        return (tpl, pit);
    }

    // ── 聚组档（范围环切断口）──

    [Fact]
    public void 聚组档_端帮节点是台阶线被环横切边分割出的断口_两侧各两级()
    {
        var (tpl, pit) = Basic();
        var s = EngineeringPositionBuilder.Build(tpl, pit, Ring(), "采场");
        Assert.True(s.Success, s.Error);
        var walls = s.Nodes.Where(n => n.Kind == EpNodeKind.EndWall).ToList();
        Assert.Equal(4, walls.Count);
        Assert.Equal(2, walls.Count(n => n.Side == 0));
        Assert.Equal(2, walls.Count(n => n.Side == 1));
        Assert.All(walls.SelectMany(n => n.Members), m => Assert.True(Math.Abs(m.X - 100) < 1e-6 || Math.Abs(m.X - 200) < 1e-6, $"断口 x={m.X}"));
        Assert.Equal(0, s.WallUnmatched);
        Assert.Contains("断口 4 处，取端帮 4 处", s.Diag);
        // 断口的退路：自断口往环外走，末点是台阶线在环外的那一头
        var wB = walls.First(n => n.Side == 1);
        var trail = wB.Members[0].Trail!;
        Assert.Equal(200, trail[0].X, 6);
        Assert.Equal(300, trail[^1].X, 6);
    }

    [Fact]
    public void 聚组档_端帮组代表标高取真实均值_不拿模板标高顶替()
    {
        var (tpl, pit) = Basic();
        var s = EngineeringPositionBuilder.Build(tpl, pit, Ring(), "采场");
        var w1211 = s.Nodes.Where(n => n.Kind == EpNodeKind.EndWall && n.MatchedLevel == 1).ToList();
        Assert.Equal(2, w1211.Count);
        Assert.All(w1211, n => { Assert.Equal(1211, n.Z, 6); Assert.Equal(1210, n.LevelZ, 6); });
        Assert.Equal(5, s.WallLevelTol, 6);                                      // 级距 10 ⇒ 容差 5
    }

    [Fact]
    public void 聚组档_归不进任何一级的端帮单列为接不上()
    {
        var tpl = new List<EpPolyline> { Line(1, "台阶_1200", 1200, (110, 0), (190, 0)) };
        var pit = new List<EpPolyline> { Line(2, "0", 1210, (98, 5), (202, 5)) };
        var s = EngineeringPositionBuilder.Build(tpl, pit, Ring(), "采场", zGroupTol: 0.5);
        Assert.Equal(2, s.WallUnmatched);                                        // 单级模板 ⇒ 容差退回 0.5 ⇒ 1210 归不进 1200
        Assert.All(s.Nodes.Where(n => n.Kind == EpNodeKind.EndWall), n => Assert.True(n.Unmatched));
        Assert.Contains("接不上", s.Diag);
    }

    [Fact]
    public void 沿走向的环边产生的断口不算端帮()
    {
        var tpl = new List<EpPolyline> { Line(1, "台阶_1200", 1200, (110, 0), (190, 0)) };
        var pit = new List<EpPolyline> { Line(2, "0", 1200, (150, -100), (150, 100)) };
        var s = EngineeringPositionBuilder.Build(tpl, pit, Ring(), "采场");
        Assert.True(s.Success);
        Assert.Contains("断口 2 处，取端帮 0 处", s.Diag);
        Assert.Empty(s.Nodes.Where(n => n.Kind == EpNodeKind.EndWall));
    }

    [Fact]
    public void 无范围环_退化按模板两端横切面切()
    {
        var (tpl, pit) = Basic();
        var s = EngineeringPositionBuilder.Build(tpl, pit, null, "");
        Assert.True(s.Success);
        Assert.Contains("无范围环", s.Diag);
        var walls = s.Nodes.Where(n => n.Kind == EpNodeKind.EndWall).SelectMany(n => n.Members).ToList();
        Assert.All(walls, m => Assert.True(Math.Abs(m.X - 110) < 1e-6 || Math.Abs(m.X - 190) < 1e-6, $"x={m.X}"));   // 模板 u 范围 [110,190]
    }

    // ── 逐端点档（窗口默认：所有端点开放）──

    [Fact]
    public void 逐端点档_所有台阶线首尾各成一个节点()
    {
        var (tpl, pit) = Basic();
        var s = EngineeringPositionBuilder.Build(tpl, pit, Ring(), "采场", allEndpointNodes: true);
        Assert.True(s.Success, s.Error);
        Assert.Equal(4, s.Nodes.Count(n => n.Kind == EpNodeKind.Template));
        Assert.Equal(4, s.Nodes.Count(n => n.Kind == EpNodeKind.EndWall));
        Assert.All(s.Nodes, n => Assert.Single(n.Members));
        Assert.Contains("逐端点成节点", s.Diag);
        // 侧别：x<150 的 A 侧，x>150 的 B 侧
        Assert.All(s.Nodes.Where(n => n.X < 150), n => Assert.Equal(0, n.Side));
        Assert.All(s.Nodes.Where(n => n.X > 150), n => Assert.Equal(1, n.Side));
    }

    [Fact]
    public void 四条闸_同一条线两头不许接_成环不许接_同角色才接()
    {
        var tpl = new List<EpPolyline>
        {
            Line(1, "台阶_1200", 1200, (110, 0), (190, 0)),
        };
        var pit = new List<EpPolyline>
        {
            Line(2, "采场_坡底线", 1200, (0, 5), (80, 5)),
            Line(3, "采场_坡顶线", 1200, (0, 25), (80, 25)),
            Line(4, "采场_坡底线", 1200, (220, 5), (300, 5)),
        };
        var s = EngineeringPositionBuilder.Build(tpl, pit, Ring(), "采场", allEndpointNodes: true);
        var tA = s.Nodes.First(n => n.Kind == EpNodeKind.Template && n.Side == 0);
        var tB = s.Nodes.First(n => n.Kind == EpNodeKind.Template && n.Side == 1);
        var toeA = s.Nodes.First(n => n.Members[0].Handle == 2 && n.X > 50);       // 坡底线靠近范围的那一头
        var toeAfar = s.Nodes.First(n => n.Members[0].Handle == 2 && n.X < 50);
        var crestA = s.Nodes.First(n => n.Members[0].Handle == 3 && n.X > 50);
        var toeB = s.Nodes.First(n => n.Members[0].Handle == 4 && n.X < 250);

        Assert.False(EngineeringPositionBuilder.IsValidPair(s, tA, tB));          // 同一条线的两头
        Assert.Contains("同一条台阶线", EngineeringPositionBuilder.PairRefusal(s, tA, tB));
        Assert.True(EngineeringPositionBuilder.IsValidPair(s, tA, toeA));          // 模板层名判不出角色 ⇒ 不拦
        Assert.False(EngineeringPositionBuilder.IsValidPair(s, toeA, crestA));     // 坡底 ⇄ 坡顶
        Assert.Contains("角色不同", EngineeringPositionBuilder.PairRefusal(s, toeA, crestA));

        EngineeringPositionBuilder.AddLink(s, tA, toeA);
        EngineeringPositionBuilder.AddLink(s, tB, toeB);
        Assert.Equal(2, s.Links.Count);
        // tA–toeA 已连，toeA 与 tA 同线的另一头 toeAfar：toeAfar ⇄ tA 同线不行；toeAfar ⇄ tB 会成环（toeAfar-toeA 同线不算边，但 tA-tB? 不：并查集只看连线）
        Assert.False(EngineeringPositionBuilder.WouldFormCycle(s, toeAfar.Id, tB.Id));
        // 把 tA 再连到 toeB：tA 已与 toeA 连，toeB 已与 tB 连 —— 两个连通片不同，不成环；但 1-1 让位：旧的两条都被顶掉
        EngineeringPositionBuilder.AddLink(s, tA, toeB);
        Assert.Single(s.Links);
        Assert.Equal(tA.Id, s.Links[0].TemplateId);
        Assert.Equal(toeB.Id, s.Links[0].WallId);
        // 成环：a-b、b-c 已连，再连 a-c
        s.Links.Clear();
        s.Links.Add(new EpLink { TemplateId = tA.Id, WallId = toeA.Id });
        s.Links.Add(new EpLink { TemplateId = toeA.Id, WallId = toeB.Id });
        Assert.True(EngineeringPositionBuilder.WouldFormCycle(s, tA.Id, toeB.Id));
        Assert.Contains("成环", EngineeringPositionBuilder.PairRefusal(s, tA, toeB));
    }

    [Fact]
    public void 就近自动配对_同侧同标高平距最近_建议线为虚()
    {
        var (tpl, pit) = Basic();
        var s = EngineeringPositionBuilder.Build(tpl, pit, Ring(), "采场", allEndpointNodes: true);
        int n = EngineeringPositionBuilder.AutoPairNearest(s, 0.5, 400);
        // 1200 级两侧各配上；1211 与 1210 差 1m > 0.5 ⇒ 不配
        Assert.Equal(2, n);
        Assert.All(s.Links, l => Assert.True(l.Suggested));
        Assert.All(s.Links, l => Assert.Equal(s.ById(l.TemplateId)!.Side, s.ById(l.WallId)!.Side));
        Assert.Equal(2, EngineeringPositionBuilder.AutoPairNearest(s, 1.5, 400));   // 放宽到 1.5 ⇒ 1211 也配上
        Assert.Equal(4, s.Links.Count);
    }

    [Fact]
    public void 既无环也无模板_报错不猜()
    {
        var s = EngineeringPositionBuilder.Build(new List<EpPolyline>(), new List<EpPolyline> { Line(1, "0", 0, (0, 0), (1, 0)) }, null, "");
        Assert.False(s.Success);
        Assert.Contains("无从定位", s.Error);
    }

    [Fact]
    public void 搭接线不算模板台阶线_图层判据()
    {
        var (tpl, pit) = Basic();
        tpl.Add(Line(9, "台阶_搭接线", 1205, (110, 5), (190, 5)));
        var s = EngineeringPositionBuilder.Build(tpl, pit, Ring(), "采场");
        Assert.Equal(4, s.Nodes.Count(n => n.Kind == EpNodeKind.Template));
        Assert.Contains("跳过搭接线 1 条", s.Diag);
        Assert.True(EngineeringPositionBuilder.IsBenchLine("台阶_9煤"));
        Assert.True(EngineeringPositionBuilder.IsBenchLine("创建工程位置_台阶"));
        Assert.False(EngineeringPositionBuilder.IsBenchLine("采场_坡顶线"));
        Assert.True(EngineeringPositionBuilder.IsDumpBenchLine("排土场_裁剪_坡顶线"));
        Assert.Equal(EngineeringPositionBuilder.BenchRole.Toe, EngineeringPositionBuilder.RoleOf("现状_坡脚线"));
        Assert.Equal(EngineeringPositionBuilder.BenchRole.Crest, EngineeringPositionBuilder.RoleOf("采场_L+1_坡顶线"));
        Assert.Equal(EngineeringPositionBuilder.BenchRole.Unknown, EngineeringPositionBuilder.RoleOf("台阶_1200"));
    }

    [Fact]
    public void 模板图层白名单_trustTemplateList不再按台阶前缀筛()
    {
        var tpl = new List<EpPolyline> { Line(1, "采场_坡顶线", 1200, (110, 0), (190, 0)) };
        var pit = new List<EpPolyline> { Line(2, "现状_坡顶线", 1200, (0, 5), (300, 5)) };
        var bad = EngineeringPositionBuilder.Build(tpl, pit, null, "", trustTemplateList: false);
        Assert.False(bad.Success);
        var ok = EngineeringPositionBuilder.Build(tpl, pit, null, "", trustTemplateList: true, allEndpointNodes: true);
        Assert.True(ok.Success, ok.Error);
        Assert.Equal(2, ok.Nodes.Count(n => n.Kind == EpNodeKind.Template));
    }

    [Fact]
    public void 归级容差是半个级距_级数不足退回兜底()
    {
        Assert.Equal(5, EngineeringPositionBuilder.LevelTol(new List<double> { 1200, 1210, 1220 }, 0.5), 9);
        Assert.Equal(6, EngineeringPositionBuilder.LevelTol(new List<double> { 1200, 1212, 1224, 1236 }, 0.5), 9);
        Assert.Equal(0.5, EngineeringPositionBuilder.LevelTol(new List<double> { 1200 }, 0.5), 9);
        Assert.Equal(0.5, EngineeringPositionBuilder.LevelTol(new List<double>(), -1), 9);
    }

    [Fact]
    public void 端名按走向方位给八向汉字()
    {
        var (tpl, pit) = Basic();
        var s = EngineeringPositionBuilder.Build(tpl, pit, Ring(), "采场");
        Assert.Equal("西端", s.SideAName);
        Assert.Equal("东端", s.SideBName);
        Assert.Contains("方位 90°", s.Diag);
    }
}

/// <summary>衔接段的渐进过渡（忠实原 EpConnectorBuilder）：限坡定长、两端相切、不够缓沿端帮吃、吃不动报超闸；同一级走角部相交。</summary>
public class EpConnectorTests
{
    private static double[] Ring() => new double[] { 100, -50, 200, -50, 200, 50, 100, 50 };
    private static EpPolyline Line(ulong h, string layer, double z, params (double x, double y)[] xy)
    {
        var xyz = new double[xy.Length * 3];
        for (int i = 0; i < xy.Length; i++) { xyz[i * 3] = xy[i].x; xyz[i * 3 + 1] = xy[i].y; xyz[i * 3 + 2] = z; }
        return new EpPolyline(h, layer, xyz, false);
    }

    private static EpScene SceneWithLink(double wallZ, double wallEndX = 300)
    {
        var tpl = new List<EpPolyline> { Line(1, "台阶_1200", 1200, (110, 0), (190, 0)) };
        var pit = new List<EpPolyline> { Line(2, "0", wallZ, (220, 0), (wallEndX, 0)) };
        var s = EngineeringPositionBuilder.Build(tpl, pit, Ring(), "采场", allEndpointNodes: true);
        var t = s.Nodes.First(n => n.Kind == EpNodeKind.Template && n.Side == 1);
        var w = s.Nodes.First(n => n.Kind == EpNodeKind.EndWall && n.X < 250);
        EngineeringPositionBuilder.AddLink(s, t, w);
        return s;
    }

    [Fact]
    public void 有高差_限坡定长_不够缓沿端帮吃()
    {
        // Δz=4m、闸 8% ⇒ 需 1.5×4/0.08 = 75m；直连 30m ⇒ 沿端帮吃 45m
        var s = SceneWithLink(1204);
        var cs = EpConnectorBuilder.Build(s, 8);
        var c = Assert.Single(cs);
        Assert.False(c.ByIntersection);
        Assert.Equal(75, c.NeedLength, 6);
        Assert.Equal(30, c.DirectLength, 3);
        Assert.Equal(45, c.WallEaten, 3);
        Assert.Equal(80, c.WallAvail, 3);
        Assert.False(c.OverGrade);
        Assert.Equal(8, c.MaxGradePct, 3);
        // 两端相切：首末点标高 = 模板 / 端帮；中点最陡
        Assert.Equal(1200, c.Pts[0].Z, 6);
        Assert.Equal(1204, c.Pts[^1].Z, 6);
        Assert.Equal((190, 0), (Math.Round(c.Pts[0].X, 6), Math.Round(c.Pts[0].Y, 6)));
        Assert.Equal(265, c.Pts[^1].X, 3);                                        // 220 + 45
        Assert.True(c.Pts.Select(p => p.Z).Zip(c.Pts.Skip(1).Select(p => p.Z), (a, b) => b - a).All(d => d >= -1e-9), "标高单调");
    }

    [Fact]
    public void 吃到尽头还不够缓_报超闸不静默拉平()
    {
        // Δz=10m ⇒ 需 187.5m；直连 30 + 可吃 20 = 50m ⇒ 超闸
        var s = SceneWithLink(1210, wallEndX: 240);
        var c = Assert.Single(EpConnectorBuilder.Build(s, 8));
        Assert.True(c.OverGrade);
        Assert.Equal(20, c.WallEaten, 3);
        Assert.Equal(50, c.PlanLength, 3);
        Assert.Equal(1.5 * 10 / 50 * 100, c.MaxGradePct, 3);
        Assert.Contains("超闸", c.Describe("东端"));
        Assert.Contains("⚠ 1 段超闸", EpConnectorBuilder.Summarize(new[] { c }, 8));
    }

    [Fact]
    public void 同一级_角部相交折接_接头与原线共线()
    {
        // 模板沿 x 到 (190,0)，端帮沿 y 从 (220,-30)→(220,-100)：向外延伸 ⇒ 交点 (220, 0)
        var tpl = new List<EpPolyline> { Line(1, "台阶_1200", 1200, (110, 0), (190, 0)) };
        var pit = new List<EpPolyline> { Line(2, "0", 1200, (220, -30), (220, -100)) };
        var s = EngineeringPositionBuilder.Build(tpl, pit, Ring(), "采场", allEndpointNodes: true);
        var t = s.Nodes.First(n => n.Kind == EpNodeKind.Template && n.Side == 1);
        var w = s.Nodes.First(n => n.Kind == EpNodeKind.EndWall && n.Y > -50);
        EngineeringPositionBuilder.AddLink(s, t, w);
        var c = Assert.Single(EpConnectorBuilder.Build(s, 8));
        Assert.True(c.ByIntersection);
        Assert.False(c.NoJoin); Assert.False(c.Overlapped);
        Assert.Equal((220, 0), (Math.Round(c.CornerX, 6), Math.Round(c.CornerY, 6)));
        Assert.Equal(30, c.ExtendA, 6); Assert.Equal(30, c.ExtendB, 6);
        Assert.Equal(0, c.FilletR, 9);
        Assert.Contains(c.Pts, p => Math.Abs(p.X - 220) < 1e-6 && Math.Abs(p.Y) < 1e-6);   // 折点在线上
        Assert.All(c.Pts, p => Assert.Equal(1200, p.Z, 6));
        Assert.Contains("角部折接", c.Describe("东端"));

        // 圆弧倒角 R=10：切点距交点 T = R·tan(45°) = 10，折点本身不再在线上
        var arc = Assert.Single(EpConnectorBuilder.Build(s, 8, filletRadius: 10));
        Assert.Equal(10, arc.FilletR, 6);
        Assert.DoesNotContain(arc.Pts, p => Math.Abs(p.X - 220) < 1e-6 && Math.Abs(p.Y) < 1e-6);
        Assert.Contains(arc.Pts, p => Math.Abs(p.X - 210) < 1e-6 && Math.Abs(p.Y) < 1e-6);   // 切点 1
        Assert.Contains(arc.Pts, p => Math.Abs(p.X - 220) < 1e-6 && Math.Abs(p.Y + 10) < 1e-6);   // 切点 2
        Assert.Contains("圆弧倒角 R=10", arc.Describe("东端"));
        // 让不出切线长就压小 R：R=100 ⇒ T 压到 30×0.98
        var big = Assert.Single(EpConnectorBuilder.Build(s, 8, filletRadius: 100));
        Assert.True(big.FilletR < 100 && big.FilletR > 0, $"R={big.FilletR}");
        Assert.Equal(29.4, big.FilletR, 3);
    }

    [Fact]
    public void 同一级_走势平行或交点在背后_不出线记接不上()
    {
        // 端帮与模板平行（都沿 x）：射线永不相交
        var s = SceneWithLink(1200);
        var c = Assert.Single(EpConnectorBuilder.Build(s, 8));
        Assert.True(c.NoJoin);
        Assert.False(c.HasGeometry);
        Assert.Contains("接不上", c.Describe("东端"));
        Assert.Contains("接不上", EpConnectorBuilder.Summarize(new[] { c }, 8));
    }

    [Fact]
    public void 同一级_两线已交叉_只记裁尾账不出线()
    {
        // 模板到 (230,0) 越过了端帮线 x=220（端帮从 (220,20)→(220,-100)，越过 y=0）
        var tpl = new List<EpPolyline> { Line(1, "台阶_1200", 1200, (110, 0), (230, 0)) };
        var pit = new List<EpPolyline> { Line(2, "0", 1200, (220, 20), (220, -100)) };
        var s = EngineeringPositionBuilder.Build(tpl, pit, Ring(), "采场", allEndpointNodes: true);
        var t = s.Nodes.First(n => n.Kind == EpNodeKind.Template && n.Side == 1);
        var w = s.Nodes.First(n => n.Kind == EpNodeKind.EndWall && n.Y > 0);
        EngineeringPositionBuilder.AddLink(s, t, w);
        var c = Assert.Single(EpConnectorBuilder.Build(s, 8));
        Assert.True(c.Overlapped);
        Assert.False(c.HasGeometry);
        Assert.Equal((220, 0), (Math.Round(c.CornerX, 6), Math.Round(c.CornerY, 6)));
        Assert.Equal(10, c.TrimA, 6);      // 模板越过 10m
        Assert.Equal(20, c.TrimB, 6);      // 端帮越过 20m
        Assert.Contains("两线已交叉", c.Describe("东端"));
    }

    [Fact]
    public void 裁尾_TrimFrom按平面长度裁_整段吃光返回空()
    {
        var pts = new List<(double X, double Y, double Z)> { (0, 0, 1), (10, 0, 1), (20, 0, 1) };
        var a = EpConnectorBuilder.TrimFrom(pts, fromHead: true, 5)!;
        Assert.Equal((5, 0), (a[0].X, a[0].Y)); Assert.Equal(3, a.Count);
        var b = EpConnectorBuilder.TrimFrom(pts, fromHead: false, 15)!;
        Assert.Equal((5, 0), (b[^1].X, b[^1].Y)); Assert.Equal(2, b.Count);
        Assert.Null(EpConnectorBuilder.TrimFrom(pts, true, 20));
        Assert.Null(EpConnectorBuilder.TrimFrom(pts, true, 25));
        Assert.Equal(3, EpConnectorBuilder.TrimFrom(pts, true, 0)!.Count);
    }
}
