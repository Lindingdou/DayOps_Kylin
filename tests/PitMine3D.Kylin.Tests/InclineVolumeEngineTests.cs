using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 「驱动量」量驱动引擎（§三七一，忠实原 InclineVolumeEngine / InclineTemplatePreprocess / InclineSurfaceBuilder / WorkLineCornerJoiner / InclineConstraint）：
/// 逐块判煤 → s 剖面 → Stage1 全局 d → Stage2 逐层 d_seam → 前界单调体检 → 推进联动保总量；斜面构面 + 交线；角部搭接；块体约束两阶段着色。
/// </summary>
public class InclineVolumeEngineTests
{
    /// <summary>水平面 z=const 的三角网（覆盖 x,y∈[-50,250]）。</summary>
    private static (double[] v, int[] t) Plane(double z, double x0 = -50, double x1 = 250, double y0 = -50, double y1 = 250)
        => (new[] { x0, y0, z, x1, y0, z, x1, y1, z, x0, y1, z }, new[] { 0, 1, 2, 0, 2, 3 });

    /// <summary>基线沿 y（x=0，z=1030），往 +x 推进。</summary>
    private static WorkLineSamples WorkLine(double x = 0) =>
        WorkLineSamples.FromWorkLine(new[] { (x, 0.0), (x, 100.0) }, 1030, fan: false, new[] { (x + 50, 0.0), (x + 50, 100.0) }, null);

    /// <summary>10m 块：x∈[0,200) y∈[0,100) z∈[1000,1030)；属性「岩性」：z<1010 → 1（煤 A），1010≤z<1020 → 2（煤 B），其余 0（岩）。</summary>
    private static InclineBlockSource Blocks()
    {
        var blocks = new List<BlockModel.Block>(); var lith = new List<double>();
        for (int i = 0; i < 20; i++) for (int j = 0; j < 10; j++) for (int k = 0; k < 3; k++)
        {
            blocks.Add(new BlockModel.Block { X = i * 10 + 5, Y = j * 10 + 5, Z = 1000 + k * 10 + 5, Size = 10, Grade = k == 0 ? 1 : k == 1 ? 2 : 0 });
            lith.Add(k == 0 ? 1 : k == 1 ? 2 : 0);
        }
        return new InclineBlockSource { Name = "T", Blocks = blocks, Attrs = new Dictionary<string, double[]> { ["岩性"] = lith.ToArray() },
                                        CategoryLabels = new Dictionary<string, IReadOnlyList<string>> { ["岩性"] = new[] { "岩", "煤A", "煤B" } } };
    }

    private static List<SeamSurfaces> Seams(double incA = 0, double incB = 0)
    {
        var (rv, rt) = Plane(1010); var (fv, ft) = Plane(1000);
        var (rv2, rt2) = Plane(1020); var (fv2, ft2) = Plane(1010);
        return new List<SeamSurfaces>
        {
            new() { Name = "煤A", Attribute = "岩性", Category = "煤A", Density = 1.0, IncrementWt = incA, Roof = TinSampler.TryBuild(rv, rt), Floor = TinSampler.TryBuild(fv, ft) },
            new() { Name = "煤B", Attribute = "岩性", Category = "煤B", Density = 1.0, IncrementWt = incB, Roof = TinSampler.TryBuild(rv2, rt2), Floor = TinSampler.TryBuild(fv2, ft2) },
        };
    }

    [Fact]
    public void TinSampler实例_网格加速采样与包围盒()
    {
        var (v, t) = Plane(1234);
        var s = TinSampler.TryBuild(v, t)!;
        Assert.NotNull(s);
        Assert.Equal(2, s.TriangleCount);
        Assert.True(s.TrySampleZ(100, 100, out double z)); Assert.Equal(1234, z, 9);
        Assert.False(s.TrySampleZ(1000, 1000, out _));
        Assert.Equal(-50, s.MinX, 9); Assert.Equal(250, s.MaxY, 9);
        Assert.Null(TinSampler.TryBuild(new double[] { 0, 0, 0 }, new int[] { 0, 0, 0 }));
    }

    [Fact]
    public void 预处理_合并包围盒与就绪层数()
    {
        var inp = new InclineTemplateInput();
        var (cv, ct) = Plane(1100);
        inp.CurrentSurfaceVerts = cv; inp.CurrentSurfaceTris = ct;
        var (rv, rt) = Plane(1010); var (fv, ft) = Plane(1000);
        inp.Seams.Add(new InclineSeamInput { Name = "煤A", RoofVerts = rv, RoofTris = rt, FloorVerts = fv, FloorTris = ft });
        inp.Seams.Add(new InclineSeamInput { Name = "残", RoofVerts = rv, RoofTris = rt });   // 缺底板 → 不就绪
        var pre = InclineTemplatePreprocess.Run(inp);
        Assert.True(pre.Success, pre.Error);
        Assert.Equal(1010, pre.ZTop, 9); Assert.Equal(1000, pre.ZFloor, 9);
        Assert.Equal(1, pre.ReadySeamCount); Assert.Equal(2, pre.Seams.Count);
        Assert.NotNull(pre.CurrentSurface);

        var bad = new InclineTemplateInput(); bad.Seams.Add(new InclineSeamInput { Name = "x" });
        Assert.False(InclineTemplatePreprocess.Run(bad).Success);   // 无现状面
    }

    [Fact]
    public void 剖面_逐层判煤_类别码_岩量剖面()
    {
        var src = Blocks(); var wl = new[] { WorkLine() };
        var (cv, ct) = Plane(1100);
        var prof = InclineVolumeEngine.BuildProfile(src, wl, TinSampler.TryBuild(cv, ct), Seams(), 89, 0, 0, rockBenchHeight: 10);
        Assert.True(prof.Success, prof.Error);
        Assert.Equal(2, prof.SeamCount);
        Assert.Equal(400, prof.CoalCellCount);                                   // 20×10×2 层煤
        Assert.Equal(400 * 1000 * 1.0 / 1e4, prof.TotalMaxWt, 6);                 // 每块 1000m³ × 容重 1
        Assert.Empty(prof.Notes);
        Assert.NotNull(prof.Rock);
        Assert.True(prof.Rock!.Success);
        Assert.Equal(200, prof.Rock.RockCellCount);                                // 顶层岩 20×10
        Assert.Equal(200 * 1000.0, prof.Rock.TotalRockM3, 6);
        // 岩全在两层煤之上 → 上覆
        Assert.Equal(prof.Rock.TotalRockM3, prof.Rock.VolByGap()[GapCode.Overburden], 6);
        Assert.Equal(1, prof.LineCount);
        Assert.Equal(prof.TotalMaxWt, prof.LineMaxWt(0), 6);
    }

    [Fact]
    public void 剖面_现状面之下与工作线后方被剔除_类别解不出记注()
    {
        var src = Blocks();
        var (cv, ct) = Plane(1012);   // 现状面在 1012：z=1015 的煤B 块心 ≥ 面 → 已采；只剩煤A（不取 1015 整平：块心与面同高时插值舍入会两边跳）
        var prof = InclineVolumeEngine.BuildProfile(src, new[] { WorkLine(50) }, TinSampler.TryBuild(cv, ct), Seams(), 89, 0, 0);
        Assert.True(prof.Success, prof.Error);
        // 工作线在 x=50：x<50 的块 s<0 → 剔除；煤A 剩 15×10
        Assert.Equal(150, prof.CoalCellCount);
        Assert.Contains(prof.Notes, n => n.Contains("煤B") && n.Contains("一吨煤都没数到"));

        var seams = Seams(); seams[0].Category = "不存在";
        var p2 = InclineVolumeEngine.BuildProfile(src, new[] { WorkLine() }, null, seams, 89, 0, 0);
        Assert.Contains(p2.Notes, n => n.StartsWith("◆") && n.Contains("解不出类别码"));
        Assert.True(p2.Success);   // 煤B 照常有煤
    }

    [Fact]
    public void Stage1_目标内自适应到切片并容差半程插值()
    {
        var src = Blocks();
        var prof = InclineVolumeEngine.BuildProfile(src, new[] { WorkLine() }, null, Seams(), 89, 0, 0);
        Assert.True(prof.Success);
        // 每 10m 切片 = 10 列 × 2 层 × 1000m³ = 2 万t；目标 8 万t → d=40（整切片刚好），容差 5%
        var s1 = InclineVolumeEngine.SolveStage1(prof, 8, 5);
        Assert.True(s1.Success); Assert.True(s1.Reached);
        Assert.Equal(40, s1.Advance, 6); Assert.Equal(8, s1.TotalCoalWt, 6); Assert.True(s1.WithinTol);
        // 目标 7 万t：第 4 片累到 8 > 7×1.05 → 插值到 7×1.025=7.175 → d=35.875
        var s1b = InclineVolumeEngine.SolveStage1(prof, 7, 5);
        Assert.Equal(35.875, s1b.Advance, 6); Assert.Equal(7.175, s1b.TotalCoalWt, 6); Assert.True(s1b.WithinTol);
        // 目标超上限 → 推到底不达标
        var s1c = InclineVolumeEngine.SolveStage1(prof, 100, 5);
        Assert.False(s1c.Reached); Assert.Equal(200, s1c.Advance, 6); Assert.False(s1c.WithinTol);
    }

    [Fact]
    public void Stage2_逐层增量与总量校验_前界单调体检()
    {
        var src = Blocks();
        var prof = InclineVolumeEngine.BuildProfile(src, new[] { WorkLine() }, null, Seams(incA: 3, incB: 1), 89, 0, 0);
        var s1 = InclineVolumeEngine.SolveStage1(prof, 8, 5);
        var s2 = InclineVolumeEngine.SolveStage2(prof, s1.Advance, 5, 4);
        Assert.True(s2.Success); Assert.Equal(2, s2.Seams.Count);
        // 每层每片 1 万t：煤A +3 → d=70；煤B +1 → d=50
        Assert.Equal(70, s2.Seams[0].Stage2Advance, 6); Assert.Equal(3, s2.Seams[0].AchievedWt, 6); Assert.True(s2.Seams[0].WithinTol);
        Assert.Equal(50, s2.Seams[1].Stage2Advance, 6); Assert.Equal(1, s2.Seams[1].AchievedWt, 6);
        Assert.Equal(4, s2.Seams[0].Stage1Wt, 6);
        Assert.True(s2.SeamTargetsMatchTotal); Assert.Contains("校验✓", s2.TotalCheckText);
        var s2b = InclineVolumeEngine.SolveStage2(prof, s1.Advance, 5, 10);
        Assert.False(s2b.SeamTargetsMatchTotal); Assert.Contains("求解按各层填的数走", s2b.TotalCheckText);

        // 下伏煤A 推 70 > 上覆煤B 50 → 倒挂 20m；按底板标高排（不按下标）
        var mono = InclineVolumeEngine.CheckFrontMonotonicity(prof, s2, Seams(), 50, 50, s1.Advance);
        Assert.False(mono.Ok); Assert.Single(mono.Items);
        Assert.Equal("煤A", mono.Items[0].LowerName); Assert.Equal(20, mono.Items[0].OverhangM, 6);
        Assert.Equal(1, mono.Items[0].LowerCappedWt, 6);   // 不倒挂最多只能到 50 → 增量 1 万t
        // 层序倒着填也报得出（按标高排）
        var seamsRev = Seams(); seamsRev.Reverse();
        var monoRev = InclineVolumeEngine.CheckFrontMonotonicity(seamsRev, new[] { 50.0, 70.0 }, 50, 50);
        Assert.False(monoRev.Ok);
        Assert.True(InclineVolumeEngine.CheckFrontMonotonicity(Seams(), new[] { 50.0, 70.0 }, 50, 50).Ok);
    }

    [Fact]
    public void 推进联动_固定被拖线其余保总量()
    {
        var src = Blocks();
        var wls = new[] { WorkLine(), WorkLineSamples.FromWorkLine(new[] { (0.0, 100.0), (0.0, 200.0) }, 1030, false, new[] { (50.0, 100.0), (50.0, 200.0) }, null) };
        // 第二条线扫 y∈[100,200)：块体只到 y<100 → 无煤；只有线 0 有煤
        var prof = InclineVolumeEngine.BuildProfile(src, wls, null, Seams(), 89, 0, 0);
        Assert.Equal(2, prof.LineCount);
        Assert.Equal(40, prof.LineMaxWt(0), 6); Assert.Equal(0, prof.LineMaxWt(1), 6);
        Assert.Equal(200, InclineVolumeEngine.MaxAdvance(prof, 0, prof.SliceWidth), 6);
        Assert.Equal(4, prof.CoalAtLine(0, 20), 6); Assert.Equal(5, prof.CoalAtLine(0, 25), 6);   // 半片线性插值
        // 拖线 1 到 30（无煤）→ 其余线要凑 8 万t → 线 0 = 40
        var d = InclineVolumeEngine.RelinkKeepTotal(prof, 8, 1, 30);
        Assert.Equal(30, d[1], 6); Assert.Equal(40, d[0], 3);
        // 拖线 0 到 60（12 万t ≥ 目标）→ 其余 0
        var d2 = InclineVolumeEngine.RelinkKeepTotal(prof, 8, 0, 60);
        Assert.Equal(60, d2[0], 6); Assert.Equal(0, d2[1], 6);
    }

    [Fact]
    public void 构面_直纹面与顶底板交线_端部延长()
    {
        var wl = WorkLine();
        var surf = InclineSurfaceBuilder.BuildForWorkLine(wl, 45, 1050, 1000, advance: 10, Seams(), extStart: 0, extEnd: 20);
        Assert.True(surf.Success, surf.Error);
        // zDatum=1030：坡顶退距 20，坡底退距 30；推进 10 → crest x=30，toe x=-20；末端延长 20 → 3 站
        Assert.Equal(3, surf.Crest.Count); Assert.Equal(3, surf.Toe.Count);
        Assert.Equal(30, surf.Crest[0].X, 6); Assert.Equal(1050, surf.Crest[0].Z, 6);
        Assert.Equal(-20, surf.Toe[0].X, 6); Assert.Equal(1000, surf.Toe[0].Z, 6);
        Assert.Equal(120, surf.Crest[2].Y, 6);   // 沿末段切向延 20
        Assert.Equal(4 * 3, surf.Indices.Count);  // 2 段 × 2 三角
        Assert.Equal(2, surf.Intersections.Count);
        // 煤A 顶板 z=1010 与斜面交点：z=1010 处 x = 30 - (1050-1010)/tan45 = -10
        var ra = surf.Intersections[0];
        Assert.Equal(3, ra.RoofLine.Count);
        Assert.Equal(-10, ra.RoofLine[0].X, 1); Assert.Equal(1010, ra.RoofLine[0].Z, 1);
        Assert.Equal(-20, ra.FloorLine[0].X, 1);
        var (v, t) = surf.ToWorldMesh();
        Assert.Equal(6, v.Count); Assert.Equal(4, t.Count);
        Assert.False(InclineSurfaceBuilder.BuildForWorkLine(wl, 45, 1000, 1050, 0, null).Success);   // z_top ≤ z_floor
    }

    [Fact]
    public void 角部搭接_成角两线外推到交点并给端部延长量()
    {
        // 线 A 沿 y（x=0, y 0→100，推进 +x）；线 B 沿 x（y=110→ 从 (10,110) 到 (110,110)，推进 +y）→ A 末端与 B 首端相邻成角
        var a = WorkLineSamples.FromWorkLine(new[] { (0.0, 0.0), (0.0, 100.0) }, 1030, false, new[] { (50.0, 0.0), (50.0, 100.0) }, null);
        var b = WorkLineSamples.FromWorkLine(new[] { (10.0, 110.0), (110.0, 110.0) }, 1030, false, new[] { (10.0, 160.0), (110.0, 160.0) }, null);
        var links = WorkLineCornerJoiner.JoinCorners(new[] { a, b });
        Assert.Single(links);
        Assert.Equal(0, links[0].A); Assert.False(links[0].AStart); Assert.Equal(1, links[0].B); Assert.True(links[0].BStart);
        // 切向线交点 (0,110)：A 末端外推 +y 到 110，B 首端外推 −x 到 0
        Assert.Equal(3, a.Baseline.Count); Assert.Equal((0.0, 110.0, 1030.0), a.Baseline[2]);
        Assert.Equal(3, b.Baseline.Count); Assert.Equal((0.0, 110.0, 1030.0), b.Baseline[0]);
        Assert.Equal(2, a.Samples.Count); Assert.Equal(2, b.Samples.Count);
        var ext = WorkLineCornerJoiner.ComputeEndExtensions(new[] { a, b }, links, 45, 1050, 1000, new[] { 10.0, 10.0 });
        Assert.True(ext[0].End > 0); Assert.Equal(0, ext[0].Start, 9);
        Assert.True(ext[1].Start > 0); Assert.Equal(0, ext[1].End, 9);
        // 平行两线不成角
        var c = WorkLineSamples.FromWorkLine(new[] { (500.0, 0.0), (500.0, 100.0) }, 1030, false, new[] { (550.0, 0.0), (550.0, 100.0) }, null);
        Assert.Empty(WorkLineCornerJoiner.JoinCorners(new[] { WorkLine(), c }));
    }

    [Fact]
    public void 块体约束_只留采出区的煤并两阶段着色_可撤销()
    {
        var src = Blocks();
        var meta = BlockModelMeta.FromBlocks("T", src.Blocks.ToList(), new Dictionary<string, double[]>(src.Attrs!));
        var col = meta.FindColumn("岩性")!; col.IsCategorical = true; col.CategoryLabels = new[] { "岩", "煤A", "煤B" };   // 类别名表随模型（FromMeta 从 schema 取）
        var wls = new[] { WorkLine() };
        var seams = Seams(incA: 3, incB: 1);
        var prof = InclineVolumeEngine.BuildProfile(InclineBlockSource.FromMeta(meta), wls, null, seams, 89, 0, 0);
        var s1 = InclineVolumeEngine.SolveStage1(prof, 8, 5);
        var s2 = InclineVolumeEngine.SolveStage2(prof, s1.Advance, 5, 4);
        var r = InclineConstraint.Apply(meta, InclineBlockSource.FromMeta(meta), wls, null, seams, 89, 0, 0, s1.Advance, s2.Seams.Select(x => x.Stage2Advance).ToList());
        Assert.Equal(200, r.RockDeleted);                        // 顶层岩全删
        // 煤A 保留 x<70 → 7 列×10；煤B 保留 x<50 → 5 列×10 ⇒ 120 留，280 删
        Assert.Equal(400 - 120, r.CoalDeleted);
        Assert.Equal(120, r.Live);
        Assert.Equal("阶段1+2 分色", r.ModeNote);
        Assert.Equal(InclineConstraint.StageAttr, meta.ActiveColormapAttribute);
        var stage = meta.GetAttr(InclineConstraint.StageAttr)!;
        int live1 = 0, live2 = 0;
        for (int i = 0; i < meta.Blocks.Count; i++) { if (meta.DeletedIds.Contains(i)) continue; if (stage[i] == 1) live1++; else if (stage[i] == 2) live2++; }
        Assert.Equal(80, live1);   // s≤40：两层各 4 列
        Assert.Equal(40, live2);   // 煤A 40<s≤70 三列 + 煤B 40<s≤50 一列
        Assert.True(meta.FindColumn(InclineConstraint.StageAttr)!.IsCategorical);
        Assert.NotNull(meta.LastInclineUndo);
        // 重跑幂等：先回退再切，块数不变
        var r2 = InclineConstraint.Apply(meta, InclineBlockSource.FromMeta(meta), wls, null, seams, 89, 0, 0, s1.Advance, null);
        Assert.Equal(480, r2.RestoredPrevious); Assert.Equal("仅阶段1", r2.ModeNote); Assert.Equal(80, r2.Live);
        Assert.Equal(520, InclineConstraint.Undo(meta)); Assert.Equal(0, meta.DeletedIds.Count); Assert.Null(meta.LastInclineUndo);
    }

    [Fact]
    public void 现状面估算工作帮坡角()
    {
        // 现状面沿 +x 以 30° 上升：z = 1000 + x·tan30
        double t30 = Math.Tan(30 * Math.PI / 180);
        var v = new[] { -300.0, -300, 1000 - 300 * t30, 300, -300, 1000 + 300 * t30, 300, 300, 1000 + 300 * t30, -300, 300, 1000 - 300 * t30 };
        var surf = TinSampler.TryBuild(v, new[] { 0, 1, 2, 0, 2, 3 });
        var r = WorkingSlopeEstimator.Estimate(surf, new[] { WorkLine() });
        Assert.True(r.Ok, r.Note);
        Assert.Equal(30, r.AngleDeg, 1);
        Assert.False(WorkingSlopeEstimator.Estimate(null, new[] { WorkLine() }).Ok);
    }
}
