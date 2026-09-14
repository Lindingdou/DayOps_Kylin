using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 采区台阶模板生成（§三七二，忠实原 BenchTemplateBuilder / EndWallJoiner / EngineeringPositionHandoff / InclineCaseFile / SeamOutcropArea）：
/// 前界斜面卡位：煤台阶 = 前界 ∩ 顶/底板；岩台阶 = 前界在标高格的等值线；露煤带面积（口径 A）；控制线裁剪；端帮预判；交接单；离线用例往返。
/// </summary>
public class BenchTemplateBuilderTests
{
    private static (double[] v, int[] t) Plane(double z, double x0 = -100, double x1 = 400, double y0 = -100, double y1 = 300)
        => (new[] { x0, y0, z, x1, y0, z, x1, y1, z, x0, y1, z }, new[] { 0, 1, 2, 0, 2, 3 });

    /// <summary>基线沿 y（x=0，z=1060），往 +x 推进。</summary>
    private static WorkLineSamples WorkLine(double x = 0, double z = 1060) =>
        WorkLineSamples.FromWorkLine(new[] { (x, 0.0), (x, 100.0) }, z, fan: false, new[] { (x + 50, 0.0), (x + 50, 100.0) }, null);

    /// <summary>一层煤：底板 1000、顶板 1010（水平）；现状面 1100。</summary>
    private static List<SeamSurfaces> OneSeam()
    {
        var (rv, rt) = Plane(1010); var (fv, ft) = Plane(1000);
        return new List<SeamSurfaces> { new() { Name = "9煤", Roof = TinSampler.TryBuild(rv, rt), Floor = TinSampler.TryBuild(fv, ft), Density = 1.3 } };
    }
    private static TinSampler Surface(double z = 1100) { var (v, t) = Plane(z); return TinSampler.TryBuild(v, t)!; }

    [Fact]
    public void 一层煤_煤台阶锚真露头_岩台阶按标高格等值线()
    {
        var wls = new[] { WorkLine() };
        var p = new BenchTemplateParams { RockBenchH = 15, CoalFaceDeg = 65, RockFaceDeg = 65, MinBerm = 20, WidenBerm = false, CoalBenchHBySeam = new[] { -1.0 } };
        // α=45°：zDatum=1060，帮顶 zTopS ≈ 1100+15（R55 现状面最高点 + 一格）；前界 d=20
        var res = BenchTemplateBuilder.Build(wls, OneSeam(), Surface(), 20, new[] { 40.0 }, 45, 1040, 990, p, null);
        Assert.True(res.Success, res.Error);
        var coal = res.Benches.Where(b => b.Kind == 0).ToList();
        Assert.Single(coal);
        var c = coal[0];
        Assert.Equal("9煤", c.SeamName);
        // 采全高：坡底 z=1000（底板露头），坡顶 z=1010；底板露头 x = 20 − (1060−1000)/tan45 = −40
        Assert.All(c.Toe, pt => Assert.Equal(1000, pt.Z, 6));
        Assert.All(c.Crest, pt => Assert.Equal(1010, pt.Z, 6));
        Assert.Equal(-40, c.Toe[0].X, 1);
        Assert.Equal(-40 + 10 / Math.Tan(65 * Math.PI / 180), c.Crest[0].X, 1);   // 煤坡面角进尺
        Assert.True(c.Toe.Count >= 6);   // 基线 100m 按 20m 加密 ⇒ ≥6 站
        // 岩台阶：标高格 1020/1035/1050/... 的水平等高线，坡顶 x = 20 − (1060−z)/tan45
        var rock = res.Benches.Where(b => b.Kind == 1 && b.RockLevel >= 0).ToList();
        Assert.True(rock.Count >= 3, $"岩台阶 {rock.Count} 段");
        foreach (var rb in rock)
        {
            double zt = rb.RockLevel * 15;
            Assert.All(rb.Crest, pt => Assert.Equal(zt, pt.Z, 6));
            Assert.All(rb.Crest, pt => Assert.Equal(20 - (1060 - zt), pt.X, 1));
            Assert.True(zt > 1010 && zt <= 1115 + 1e-6);
        }
        // 露煤带：顶板∩前界①(d=20) → 顶板∩前界②(d=40)，宽 20m × 100m ⇒ 2000 m²（口径 A）
        Assert.True(res.ExposureAreaBySeam.TryGetValue("9煤", out double exp));
        Assert.Equal(2000, exp, 0);
        Assert.Single(res.ExposureBands);
        // 现状面 1100 在顶板之上 → 现状已露煤 0（面全在带外）
        Assert.False(res.OutcropAreaBySeam.ContainsKey("9煤"));
        Assert.Contains("露煤面积(口径A", res.Diag);
        Assert.Contains("平盘", res.Diag);
    }

    [Fact]
    public void 煤台阶分层_跟岩台阶高_与采全高()
    {
        var (rv, rt) = Plane(1030); var (fv, ft) = Plane(1000);   // 30m 厚煤
        var seams = new List<SeamSurfaces> { new() { Name = "厚煤", Roof = TinSampler.TryBuild(rv, rt), Floor = TinSampler.TryBuild(fv, ft) } };
        var wls = new[] { WorkLine() };
        var p = new BenchTemplateParams { RockBenchH = 10, WidenBerm = false, CoalBenchHBySeam = new[] { 0.0 } };   // 0 = 跟岩台阶高 10 ⇒ 3 级
        var res = BenchTemplateBuilder.Build(wls, seams, Surface(), 20, null, 45, 1040, 990, p, null);
        Assert.True(res.Success, res.Error);
        var levels = res.Benches.Where(b => b.Kind == 0).Select(b => b.Level).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(new[] { 0, 1, 2 }, levels);
        var lv1 = res.Benches.First(b => b.Kind == 0 && b.Level == 1);
        Assert.All(lv1.Toe, pt => Assert.Equal(1010, pt.Z, 6));
        Assert.All(lv1.Crest, pt => Assert.Equal(1020, pt.Z, 6));
        Assert.Contains("3 级/站", res.Diag.Replace(" ", " "));

        p.CoalBenchHBySeam = new[] { -1.0 };   // 采全高 ⇒ 1 级
        var res2 = BenchTemplateBuilder.Build(wls, seams, Surface(), 20, null, 45, 1040, 990, p, null);
        Assert.Single(res2.Benches.Where(b => b.Kind == 0).Select(b => b.Level).Distinct());
    }

    [Fact]
    public void 两层煤_层间岩台阶落在标高格_煤尖灭不截断链()
    {
        var (r1, t1) = Plane(1010); var (f1, u1) = Plane(1000);
        var (r2, t2) = Plane(1050); var (f2, u2) = Plane(1046);   // 上层 4m 厚
        var seams = new List<SeamSurfaces>
        {
            new() { Name = "上煤", Roof = TinSampler.TryBuild(r2, t2), Floor = TinSampler.TryBuild(f2, u2) },   // 故意倒序：Build 按底板标高排
            new() { Name = "下煤", Roof = TinSampler.TryBuild(r1, t1), Floor = TinSampler.TryBuild(f1, u1) },
        };
        var wls = new[] { WorkLine(z: 1080) };
        var p = new BenchTemplateParams { RockBenchH = 10, WidenBerm = false, CoalBenchHBySeam = new[] { -1.0, -1.0 } };
        var res = BenchTemplateBuilder.Build(wls, seams, Surface(1120), 20, new[] { 40.0, 40.0 }, 45, 1060, 990, p, null);
        Assert.True(res.Success, res.Error);
        var coalNames = res.Benches.Where(b => b.Kind == 0).Select(b => b.SeamName).Distinct().ToList();
        Assert.Contains("下煤", coalNames); Assert.Contains("上煤", coalNames);
        // 层间（1010→1046）：标高格 1020/1030/1040 三级（R61：不出贴界面半台阶）
        var inter = res.Benches.Where(b => b.Kind == 1 && b.RockLevel >= 0 && b.GapSeam == 1).Select(b => b.RockLevel * 10).Distinct().OrderBy(z => z).ToList();
        Assert.Equal(new[] { 1020, 1030, 1040 }, inter);
        Assert.Empty(res.Benches.Where(b => b.Kind == 1 && b.RockLevel < 0 && b.GapSeam == 1));   // 层间无半台阶
        Assert.Contains("层间可堆岩厚 min=36", res.Diag);
        // 封顶那一摞堆到帮顶（R54）
        Assert.True(res.Benches.Any(b => b.Kind == 1 && b.GapSeam == 2));
        // 交接单：层名册两层、级线带身份
        var ho = EngineeringPositionHandoff.Build(res, wls, p, 20, new[] { 40.0, 40.0 }, 45, 1060, 990, null, "test", seams);
        Assert.NotNull(ho);
        Assert.Equal(2, ho!.SeamRoster.Count);
        Assert.Contains(ho.Levels, l => l.Kind == 0 && l.SeamName == "下煤" && !l.IsCrest && Math.Abs(l.Z - 1000) < 1e-6);
        Assert.Contains(ho.Levels, l => l.Kind == 1 && l.Layer == "台阶_岩台阶");
        Assert.Single(ho.Cover);
        Assert.True(ho.Cover[0].ALo < ho.Cover[0].AHi);
        var grid = BenchLevelGrid.FromHandoff(ho);
        Assert.True(grid.Count >= 5);
        Assert.True(grid.TryMatch(1000.3, out int idx, out _)); Assert.Equal("下煤", grid.LevelSeam[idx]);
        EngineeringPositionHandoff.Publish(ho);
        Assert.Same(ho, EngineeringPositionHandoff.Latest);
        EngineeringPositionHandoff.Clear();
        Assert.Null(EngineeringPositionHandoff.Latest);
    }

    [Fact]
    public void 控制线裁剪_只留模板侧并切在线上()
    {
        var wls = new[] { WorkLine() };
        var p = new BenchTemplateParams { RockBenchH = 15, WidenBerm = false, CoalBenchHBySeam = new[] { -1.0 } };
        var res = BenchTemplateBuilder.Build(wls, OneSeam(), Surface(), 20, null, 45, 1040, 990, p, null);
        int before = res.Benches.Count;
        // 边界线 y=60（沿 x）：工作线质心 y=50 在 −侧 ⇒ 只留 y≤60
        var bnd = new List<(double[] Xyz, int Line)> { (new[] { -500.0, 60, 0, 500, 60, 0 }, -1) };
        EndWallJoiner.ClipBenchesByBoundaries(res, bnd, wls);
        Assert.True(res.Benches.Count >= 1);
        foreach (var bl in res.Benches)
        {
            Assert.All(bl.Toe, pt => Assert.True(pt.Y <= 60 + 0.01));
            Assert.Equal(60, bl.Toe.Max(pt => pt.Y), 1);   // 切在边界线上（二分 14 轮 ⇒ 毫米级）
        }
        Assert.Equal(res.CoalCount + res.RockCount, res.Benches.Count);
        Assert.True(before >= res.Benches.Count);
    }

    [Fact]
    public void 端帮预判_跨模板边界的线被裁并可衔接()
    {
        var wls = new[] { WorkLine() };
        var p = new BenchTemplateParams { RockBenchH = 15, WidenBerm = false, CoalBenchHBySeam = new[] { -1.0 } };
        var res = BenchTemplateBuilder.Build(wls, OneSeam(), Surface(), 20, null, 45, 1040, 990, p, null);
        // 一条端帮台阶线 y=50 从 x=−300 到 x=300，z=1000（与煤台阶坡底同高）：横穿模板带 [a0=20−5, walkMax+5]
        // 端帮线按 20m 一个顶点（带内/带外按顶点判，与原版同口径：只有两个端点的长线会被当成整条在带外）
        double[] Dense(double y) { var l = new List<double>(); for (double x = -300; x <= 300 + 1e-9; x += 20) { l.Add(x); l.Add(y); l.Add(1000); } return l.ToArray(); }
        var src = new List<(double[] Xyz, bool Closed)>
        {
            (Dense(50), false),
            (Dense(500), false),   // 带外（横向 |t|>半长+5）→ 不相干
        };
        var jw = EndWallJoiner.Run(src, wls, 20, res, zTol: 7.5, joinRadius: 50, null);
        Assert.True(jw.Success, jw.Error);
        Assert.Equal(2, jw.SourceCount);
        Assert.Equal(1, jw.ClippedLineCount);
        Assert.True(jw.Clipped.Count >= 1);
        Assert.All(jw.Clipped, seg => Assert.True(seg.Count >= 2));
        // 保留段的裁剪端落在带边界 a0 ≈ 15 或 walkMax+5 附近
        var cutXs = jw.Clipped.SelectMany(seg => new[] { seg[0].X, seg[^1].X }).Where(x => Math.Abs(x) < 299).ToList();
        Assert.NotEmpty(cutXs);
        Assert.True(jw.JoinCount >= 0);
        Assert.False(EndWallJoiner.Run(src, wls, 20, new BenchTemplateResult(), 7.5, 50).Success);
    }

    [Fact]
    public void 离线用例_往返一致_复跑同一条链()
    {
        var wls = new[] { WorkLine() };
        var seams = OneSeam(); seams[0].Attribute = "岩性"; seams[0].Category = "煤"; seams[0].IncrementWt = 3;
        var kase = new InclineCase
        {
            Note = "单测", AlphaDeg = 45, DStage1 = 20, ZTop = 1040, ZFloor = 990, CurrentSurface = Surface(),
            Bench = new BenchTemplateParams { RockBenchH = 15, WidenBerm = false, CoalBenchHBySeam = new[] { -1.0 }, JoinEndWall = true },
            AnnualProductionWt = 8, RecoveryTotalWt = 3, TolPct = 5,
        };
        kase.WorkLines.AddRange(wls); kase.Seams.AddRange(seams); kase.DSeamBySeam.Add(40);
        kase.Boundaries.Add((new[] { -500.0, 60, 0, 500, 60, 0 }, -1));
        kase.CornerLinks.Add(new WorkLineCornerJoiner.CornerLink(0, true, 0, false));
        string path = Path.Combine(Path.GetTempPath(), "pitmine_tests", $"case_{Guid.NewGuid():N}.case");
        try
        {
            Assert.True(InclineCaseFile.TrySave(path, kase, out string err), err);
            Assert.True(File.Exists(Path.ChangeExtension(path, ".txt")));
            var back = InclineCaseFile.TryLoad(path, out err);
            Assert.NotNull(back);
            Assert.Equal("单测", back!.Note);
            Assert.Equal(45, back.AlphaDeg, 9); Assert.Equal(20, back.DStage1, 9);
            Assert.Single(back.WorkLines); Assert.Equal(2, back.WorkLines[0].Baseline.Count); Assert.True(back.WorkLines[0].Success);
            Assert.Single(back.Seams); Assert.Equal("9煤", back.Seams[0].Name); Assert.Equal("煤", back.Seams[0].Category); Assert.True(back.Seams[0].Ready);
            Assert.Equal(40, back.DSeamBySeam[0], 9);
            Assert.NotNull(back.CurrentSurface); Assert.True(back.CurrentSurface!.TrySampleZ(0, 0, out double z)); Assert.Equal(1100, z, 6);
            Assert.Single(back.Boundaries); Assert.Equal(-1, back.Boundaries[0].Line);
            Assert.Single(back.CornerLinks);
            Assert.True(back.Bench.JoinEndWall); Assert.Equal(-1.0, back.Bench.CoalBenchHBySeam![0]);
            Assert.Null(back.Profile);
            Assert.Null(back.RunStage1());
            var a = kase.RunBenches(applyBoundaryClip: false); var b = back.RunBenches(applyBoundaryClip: false);
            Assert.True(a.Success && b.Success);
            Assert.Equal(a.Benches.Count, b.Benches.Count);
            var clipped = back.RunBenches();
            Assert.All(clipped.Benches, bl => Assert.All(bl.Toe, pt => Assert.True(pt.Y <= 60 + 0.01)));
            Assert.Equal(1, back.RunSurfaces().Count);
            Assert.Contains("量驱动开采模板 离线用例", back.Summary());
        }
        finally { try { File.Delete(path); File.Delete(Path.ChangeExtension(path, ".txt")); } catch { } }
        Assert.Null(InclineCaseFile.TryLoad(path + ".nope", out string e2)); Assert.Contains("不存在", e2);
    }

    [Fact]
    public void 剖面文件_往返含岩剖面与指纹校验()
    {
        var prof = new CoalProfile { Success = true, SliceWidth = 10, SeamCount = 1, SeamNames = new[] { "9煤" }, SeamIncrementWt = new[] { 3.0 }, TotalMaxWt = 4, CoalCellCount = 40, LineCount = 1 };
        prof.SeamBins = new[] { new Dictionary<long, double> { [0] = 20000, [1] = 20000 } };
        prof.LineBins = new[] { new Dictionary<long, double> { [0] = 20000, [1] = 20000 } };
        var rock = new RockProfile { Success = true, SliceWidth = 10, BenchHeight = 15, SeamCount = 1, SeamNames = new[] { "9煤" }, GapNames = GapCode.Names(new[] { "9煤" }), SeamDensity = new[] { 1.3 },
                                     CoalVolBins = new[] { new Dictionary<long, double>() }, CoalZMoment = new[] { new Dictionary<long, double>() } };
        rock.AddRock(68, GapCode.Overburden, 2, 1000);
        rock.Provenance = new ProfileProvenance { BlockModelName = "T", BlockCellCount = 100, AlphaDeg = 45, SliceWidth = 10, BenchHeight = 15, WorkLineKey = 123, SurfaceKey = 456, SeamSurfaceKey = 789, SeamRuleKey = 1, CreatedUtc = "now", Software = "x" };
        prof.Rock = rock;
        string path = Path.Combine(Path.GetTempPath(), "pitmine_tests", $"prof_{Guid.NewGuid():N}.pmrp");
        try
        {
            Assert.True(MineProfileFile.TrySave(path, prof, out string err), err);
            var back = MineProfileFile.TryLoad(path, out err, rock.Provenance);
            Assert.NotNull(back);
            Assert.Equal(40000, back!.SeamBins[0].Values.Sum(), 6);
            Assert.NotNull(back.Rock); Assert.Equal(1000, back.Rock!.TotalRockM3, 6);
            Assert.Equal(1, back.Rock.Bins.Count);
            Assert.Equal("T", back.Rock.Provenance!.BlockModelName);
            var other = new ProfileProvenance { BlockModelName = "T", BlockCellCount = 100, AlphaDeg = 30, SliceWidth = 10, BenchHeight = 15, WorkLineKey = 123, SurfaceKey = 456, SeamSurfaceKey = 789, SeamRuleKey = 1 };
            Assert.Null(MineProfileFile.TryLoad(path, out err, other)); Assert.Contains("剖面缓存失效", err);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void 现状已露煤_面积与边界环()
    {
        // 现状面从 z=990 (x=-100) 线性升到 z=1040 (x=400)：落在 [1000,1010] 的带 = x∈[0,100]，宽 100 × y 长 400
        var v = new[] { -100.0, -100, 990, 400, -100, 1040, 400, 300, 1040, -100, 300, 990 };
        var surf = TinSampler.TryBuild(v, new[] { 0, 1, 2, 0, 2, 3 });
        var oc = SeamOutcropArea.Compute(surf, OneSeam());
        Assert.True(oc.Success, oc.Error);
        Assert.InRange(oc.AreaXY[0], 100 * 400 - 50, 100 * 400 + 50);   // 投影面积 4 万 m²
        Assert.True(oc.Area3D[0] > oc.AreaXY[0]);             // 斜面真实面积更大
        Assert.NotEmpty(oc.Rings);
        Assert.Equal("9煤", oc.Rings[0].SeamName);
        Assert.False(SeamOutcropArea.Compute(null, OneSeam()).Success);
    }
}
