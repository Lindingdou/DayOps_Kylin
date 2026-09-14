using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 创建工程位置 ④⑤（§三六八）：用新台阶线替换模板影响区域内的老台阶线（忠实原 BenchLineReplacer / WorkLineProjector）。
/// 口径：覆盖范围 = 推进区间 × 走向区间（工作线坐标系）；跨界二分切在边界上；保留长 + 丢弃长 = 原长；
/// 逐级替换：归不进级一条都不删；改判走同一套记账。
/// </summary>
public class BenchLineReplacerTests
{
    private static EpPolyline Line(ulong h, string layer, double z, params (double x, double y)[] xy)
    {
        var xyz = new double[xy.Length * 3];
        for (int i = 0; i < xy.Length; i++) { xyz[i * 3] = xy[i].x; xyz[i * 3 + 1] = xy[i].y; xyz[i * 3 + 2] = z; }
        return new EpPolyline(h, layer, xyz, false);
    }

    /// <summary>工作线：基线 y=−30、x∈[110,190]，往 +y 推进（结束线 y=20）。</summary>
    private static WorkLineSamples WorkLine() =>
        WorkLineSamples.FromWorkLine(new[] { (110.0, -30.0), (190.0, -30.0) }, 1200, fan: false, new[] { (110.0, 20.0), (190.0, 20.0) }, null);

    private static List<EpPolyline> Templates() => new()
    {
        Line(1, "台阶_1200", 1200, (110, 0), (190, 0)),
        Line(2, "台阶_1210", 1210, (110, 10), (190, 10)),
    };

    [Fact]
    public void 工作线反算_直线各段同向_扇形绕中心切向()
    {
        var wl = WorkLine();
        Assert.True(wl.Success, wl.Error);
        Assert.Single(wl.Samples);
        Assert.Equal((0, 1), (Math.Round(wl.Samples[0].Dx, 9), Math.Round(wl.Samples[0].Dy, 9)));
        // 扇形：基线 (0,0)-(100,0) 绕中心 (-25,0) 转 +30°，段中点 (50,0) 的切向 = +y
        var fan = WorkLineSamples.FromWorkLine(new[] { (0.0, 0.0), (100.0, 0.0) }, 0, fan: true,
            new[] { (25.0 * Math.Cos(Math.PI / 6) - 25, 25.0 * Math.Sin(Math.PI / 6)), (125.0 * Math.Cos(Math.PI / 6) - 25, 125.0 * Math.Sin(Math.PI / 6)) }, (-25.0, 0.0));
        Assert.True(fan.Success);
        Assert.True(fan.HasFanParams); Assert.Equal(2, fan.AdvanceMode); Assert.Equal(0, fan.RotDir);
        Assert.Equal((0, 1), (Math.Round(fan.Samples[0].Dx, 9), Math.Round(fan.Samples[0].Dy, 9)));
        Assert.False(WorkLineSamples.FromWorkLine(new[] { (0.0, 0.0) }, 0, false, null, null).Success);
    }

    [Fact]
    public void 投影器_推进坐标与走向坐标_纵向超出容差不投()
    {
        var proj = WorkLineProjector.Build(new[] { WorkLine() }, 5);
        Assert.True(proj.HasAny);
        Assert.True(proj.TryProject(150, 0, out double a0, out double z, out int line, out double s));
        Assert.Equal(30, a0, 6); Assert.Equal(1200, z, 6); Assert.Equal(0, line); Assert.Equal(40, s, 6);
        Assert.True(proj.TryProject(194, -40, out a0, out _, out _, out s));         // 端点外 4m ≤ 容差 5
        Assert.Equal(-10, a0, 6); Assert.Equal(84, s, 6);
        Assert.False(proj.TryProject(200, 0, out _, out _));                           // 端点外 10m > 容差
        Assert.Equal(80, proj.StrikeLength(0), 6);
    }

    [Fact]
    public void 方案_跨界裁剪_保留长加丢弃长等于原长_断口带退路()
    {
        var olds = new List<EpPolyline> { Line(10, "现状_坡底线", 1200, (60, 5), (240, 5)) };
        var p = BenchLineReplacer.Plan(new[] { WorkLine() }, Templates(), olds, margin: 5);
        Assert.True(p.Success, p.Error);
        var b = Assert.Single(p.Bands);
        Assert.Equal(-5, b.Lo, 6); Assert.Equal(45, b.Hi, 6);                        // 下界回溯到工作线 min(0,30)−5，上界 40+5
        Assert.Equal(-5, b.LatLo, 6); Assert.Equal(85, b.LatHi, 6);                  // 走向 [0,80] ± 5
        var it = Assert.Single(p.Items);
        Assert.Equal(BenchReplaceVerdict.Clipped, it.Verdict);
        Assert.Equal(180, it.Length, 6);
        Assert.Equal(90, it.DropLength, 2);                                          // x∈[105,195] 被取代
        Assert.Equal(it.Length, it.KeptLength + it.DropLength, 6);
        Assert.Equal(2, it.Keep.Count); Assert.Single(it.Drop);
        Assert.Equal(2, it.Cuts.Count);
        // 断口在边界上、退路从断口往外到线头
        var cutL = it.Cuts.First(c => c.X < 150); var cutR = it.Cuts.First(c => c.X > 150);
        Assert.Equal(105, cutL.X, 2); Assert.Equal(195, cutR.X, 2);
        Assert.Equal(60, cutL.Trail[^1].X, 6); Assert.Equal(240, cutR.Trail[^1].X, 6);
        Assert.Equal(45, cutL.TrailLength, 2);
        Assert.Equal(new ulong[] { 10 }, p.HandlesToDelete());
        Assert.Equal(2, p.KeepSegments().Count());
        Assert.Contains("取代 0 条 + 裁剪 1 条", p.Diag);
    }

    [Fact]
    public void 方案_整条在内取代_整条在外不动_两点直线横穿也判得出()
    {
        var olds = new List<EpPolyline>
        {
            Line(11, "a", 1200, (120, 5), (180, 5)),           // 整条在内
            Line(12, "a", 1200, (300, 5), (400, 5)),           // 整条在外
            Line(13, "a", 1200, (0, 5), (300, 5)),             // 两端都在外、中间横穿 —— 段内探针必须采到
        };
        var p = BenchLineReplacer.Plan(new[] { WorkLine() }, Templates(), olds);
        Assert.True(p.Success, p.Error);
        Assert.Equal(BenchReplaceVerdict.Superseded, p.Items[0].Verdict);
        Assert.Equal(0, p.Items[0].KeptLength, 6);
        Assert.Equal(BenchReplaceVerdict.Untouched, p.Items[1].Verdict);
        Assert.Equal(p.Items[1].Length, p.Items[1].KeptLength, 6);
        Assert.Equal(BenchReplaceVerdict.Clipped, p.Items[2].Verdict);
        Assert.Equal(90, p.Items[2].DropLength, 2);
        Assert.Equal(1, p.SupersededCount); Assert.Equal(1, p.UntouchedCount); Assert.Equal(1, p.ClippedCount);
    }

    [Fact]
    public void 方案_替换可重复执行_保留段再跑一次不再跨界()
    {
        var olds = new List<EpPolyline> { Line(10, "a", 1200, (60, 5), (240, 5)) };
        var p = BenchLineReplacer.Plan(new[] { WorkLine() }, Templates(), olds);
        var again = new List<EpPolyline>();
        ulong h = 20;
        foreach (var (lay, pts) in p.KeepSegments())
        {
            var xyz = new double[pts.Count * 3];
            for (int i = 0; i < pts.Count; i++) { xyz[i * 3] = pts[i].X; xyz[i * 3 + 1] = pts[i].Y; xyz[i * 3 + 2] = pts[i].Z; }
            again.Add(new EpPolyline(h++, lay, xyz, false));
        }
        var p2 = BenchLineReplacer.Plan(new[] { WorkLine() }, Templates(), again);
        Assert.All(p2.Items, it => Assert.Equal(BenchReplaceVerdict.Untouched, it.Verdict));
    }

    [Fact]
    public void 逐级_归不进级一条都不删_改判走同一套记账()
    {
        var olds = new List<EpPolyline>
        {
            Line(10, "a", 1200, (60, 5), (240, 5)),
            Line(11, "a", 1222, (60, 8), (240, 8)),            // 离最近级 12m > 容差 5
        };
        var grid = BenchLevelGrid.FromTemplateLines(Templates(), 0);
        Assert.Equal(2, grid.Count); Assert.Equal(10, grid.Step, 6); Assert.Equal(5, grid.Tol, 6);
        Assert.True(grid.TryMatch(1204, out int li, out double dz)); Assert.Equal(0, li); Assert.Equal(-4, dz, 6);
        Assert.False(grid.TryMatch(1222, out _, out _));

        var p = BenchLineReplacer.Plan(new[] { WorkLine() }, Templates(), olds, levels: grid);
        Assert.True(p.Success, p.Error);
        Assert.Equal(BenchReplaceVerdict.Clipped, p.Items[0].Verdict);
        Assert.Equal(0, p.Items[0].MatchedLevel); Assert.Equal("台阶_1200", p.Items[0].MatchedTag);
        Assert.Equal(BenchReplaceVerdict.Unmatched, p.Items[1].Verdict);
        Assert.Equal(p.Items[1].Length, p.Items[1].KeptLength, 6);
        Assert.Empty(p.Items[1].Cuts);
        Assert.Single(p.HandlesToDelete());
        // 记账的另一侧：1200 级触及 1 条，1210 级碰不到
        Assert.Equal(2, p.LevelUsage.Count);
        Assert.True(p.LevelUsage[0].Touched); Assert.Equal(1, p.LevelUsage[0].OldCount);
        Assert.False(p.LevelUsage[1].Touched);
        Assert.Equal(1, p.UntouchedLevelCount);
        Assert.Contains("归不进级的 1 条", p.Diag);

        // 改判：#11 强制替换 ⇒ 整条取代；#10 强制保留 ⇒ Unmatched 且标 Overridden
        var ov = new Dictionary<ulong, bool> { [11] = true, [10] = false };
        var p2 = BenchLineReplacer.Plan(new[] { WorkLine() }, Templates(), olds, levels: grid, overrides: ov);
        Assert.Equal(BenchReplaceVerdict.Unmatched, p2.Items[0].Verdict); Assert.True(p2.Items[0].Overridden);
        Assert.Equal(BenchReplaceVerdict.Superseded, p2.Items[1].Verdict); Assert.True(p2.Items[1].Overridden);
        Assert.Equal(0, p2.Items[1].KeptLength, 6);
        Assert.Equal(new ulong[] { 11 }, p2.HandlesToDelete());
        Assert.Contains("人工改判 2 条", p2.Diag);
        var tally = p2.ByLayer();
        Assert.Single(tally); Assert.Equal(1, tally[0].Superseded); Assert.Equal(1, tally[0].Unmatched);
    }

    [Fact]
    public void 横向不限时吃满整条工作线_下界不回溯只按模板区间()
    {
        var olds = new List<EpPolyline> { Line(10, "a", 1200, (60, 5), (240, 5)) };
        var wide = WorkLineSamples.FromWorkLine(new[] { (0.0, -30.0), (300.0, -30.0) }, 1200, false, new[] { (0.0, 20.0), (300.0, 20.0) }, null);
        var open = BenchLineReplacer.Plan(new[] { wide }, Templates(), olds, lateralToTemplate: false, backToWorkLine: false);
        Assert.True(open.Success, open.Error);
        Assert.False(open.Bands[0].LatClipped);
        Assert.Equal(25, open.Bands[0].Lo, 6);                                        // 模板前界 30 − 5
        Assert.Equal(BenchReplaceVerdict.Superseded, open.Items[0].Verdict);          // 横向没限住 ⇒ 整条被判取代
        Assert.Contains("横向吃满整条工作线宽", open.Diag);
        var clipped = BenchLineReplacer.Plan(new[] { wide }, Templates(), olds, lateralToTemplate: true);
        Assert.Equal(BenchReplaceVerdict.Clipped, clipped.Items[0].Verdict);
        Assert.Equal(90, clipped.Items[0].DropLength, 2);
    }

    [Fact]
    public void 没工作线或模板投不上_报错不猜()
    {
        var olds = new List<EpPolyline> { Line(10, "a", 1200, (60, 5), (240, 5)) };
        var none = BenchLineReplacer.Plan(Array.Empty<WorkLineSamples>(), Templates(), olds);
        Assert.False(none.Success); Assert.Contains("没有工作线", none.Error);
        var far = WorkLineSamples.FromWorkLine(new[] { (1000.0, -30.0), (1080.0, -30.0) }, 1200, false, new[] { (1000.0, 20.0), (1080.0, 20.0) }, null);
        var miss = BenchLineReplacer.Plan(new[] { far }, Templates(), olds);
        Assert.False(miss.Success); Assert.Contains("投不到", miss.Error);
        Assert.False(BenchLineReplacer.Plan(new[] { WorkLine() }, new List<EpPolyline>(), olds).Success);
    }

    [Fact]
    public void 断口灌成端帮节点_归到最近模板级_已有连线按标高认回()
    {
        var tpl = Templates();
        var olds = new List<EpPolyline> { Line(10, "现状_坡底线", 1200, (60, 5), (240, 5)) };
        var s = EngineeringPositionBuilder.Build(tpl, olds, null, "", allEndpointNodes: true);
        Assert.True(s.Success, s.Error);
        var p = BenchLineReplacer.Plan(new[] { WorkLine() }, tpl, olds);
        var cuts = p.Items.SelectMany(it => it.Cuts.Select(c => new EpCutPoint(c.X, c.Y, c.Z, it.Handle, it.Layer, c.Trail))).ToList();
        Assert.Equal(2, cuts.Count);
        int added = EngineeringPositionBuilder.AddEndWallNodesFromCuts(s, cuts, 0.5);
        Assert.Equal(2, added);
        Assert.True(s.EndWallFromCuts);
        var walls = s.Nodes.Where(n => n.Kind == EpNodeKind.EndWall).ToList();
        Assert.Equal(2, walls.Count);
        Assert.All(walls, w => Assert.True(Math.Abs(w.X - 105) < 0.01 || Math.Abs(w.X - 195) < 0.01, $"x={w.X}"));
        Assert.Contains(walls, w => w.Side == 0); Assert.Contains(walls, w => w.Side == 1);
        Assert.All(walls, w => Assert.Equal(1200, w.Z, 6));
        Assert.All(walls, w => Assert.True(w.Members[0].Trail!.Count >= 2));
    }
}
