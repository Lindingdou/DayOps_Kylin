using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Plan;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 境界优化（境界圈定 / 确定境界）家族：PitScheme 经济四式 · 块体煤口径 · 逐层采样 · 净值最大定坑底 · 评价 · 对比矩阵 · 放样落地。
/// 全部对原 PlanLib.BoundaryOptimization 的语义做不变量校验（合成块体，本机可跑）。
/// </summary>
public class PitSchemeFamilyTests
{
    // ── 合成块体：20×6 列 × 8 层，下 3 层煤(码 1)、上 5 层岩；10 m 立方块 ──
    private static BlockModelMeta MakeModel(int nx = 20, int ny = 6, int nz = 8, int coalLayers = 3)
    {
        var m = new BlockModelMeta { Name = "pit", Sx = 10, Sy = 10, Sz = 10, IsRegular = false };
        var codes = new List<double>();
        var ash = new List<double>();
        for (int ix = 0; ix < nx; ix++)
            for (int iy = 0; iy < ny; iy++)
                for (int iz = 0; iz < nz; iz++)
                {
                    m.Blocks.Add(new BlockModel.Block { X = 1000 + ix * 10 + 5, Y = 2000 + iy * 10 + 5, Z = 100 + iz * 10 + 5, Size = 10, Grade = 0 });
                    codes.Add(iz < coalLayers ? 1 : 0);
                    ash.Add(iz < coalLayers ? 12 + iz : 0);
                }
        m.PropertySchema.Add(new BlockPropertyColumn { Name = "矿岩类型", IsCategorical = true, CategoryLabels = new List<string> { "岩石", "3-1煤" } });
        m.Attrs["矿岩类型"] = codes.ToArray();
        m.PropertySchema.Add(new BlockPropertyColumn { Name = "ash" });
        m.Attrs["ash"] = ash.ToArray();
        return m;
    }

    private sealed class FakeHost : IPlanEntityHost
    {
        public BlockModelMeta? Model;
        public Dictionary<long, (double[] xyz, bool closed)> Polys = new();
        public List<PlanEntityBatch> Imported = new();
        public BlockModelMeta? ActiveBlockModel => Model;
        public System.Data.Common.DbConnection? Db => null;
        public bool TryGetPolylineWorldVertices(long h, out double[] xyz, out bool closed)
        { if (Polys.TryGetValue(h, out var p)) { xyz = p.xyz; closed = p.closed; return true; } xyz = Array.Empty<double>(); closed = false; return false; }
        public bool TryGetEntityAabb(long handle, out double[] mn, out double[] mx) { mn = new double[3]; mx = new double[3]; return false; }
        public bool TryGetMesh(long handle, out List<(double x, double y, double z)> verts, out List<(int a, int b, int c)> tris) { verts = new(); tris = new(); return false; }
        public IReadOnlyList<(long handle, string layer, string name)> ListEntities(int wantType) => Array.Empty<(long, string, string)>();
        public long[] GetHandlesByLayer(string layer) => Array.Empty<long>();
        public void DeleteEntities(IEnumerable<long> handles) { }
        public long[] Import(PlanEntityBatch batch) { Imported.Add(batch); return Enumerable.Range(1, batch.Lines.Count + batch.Rings.Count + batch.Meshes.Count).Select(i => (long)i).ToArray(); }
        public System.Threading.Tasks.Task<long?> PickInViewportAsync(int wantType, string prompt) => System.Threading.Tasks.Task.FromResult<long?>(null);
        public System.Threading.Tasks.Task<(double x, double y)?> PickPointAsync(string prompt) => System.Threading.Tasks.Task.FromResult<(double, double)?>(null);
        public void SelectByHandle(long handle, bool addToSelection = false) { }
        public void Echo(string text, bool warn = false) { }
        public void Refresh() { }
        public WorkLineSamples? SelectedWorkLine(out long handle, out string error) { handle = 0; error = ""; return null; }
        public WorkLineSamples? WorkLineByHandle(long handle) => null;
    }

    [Fact]
    public void 经济合理剥采比_四式与原版公式一致()
    {
        var e = new EconParams { Price = 320, MiningCost = 95, StripCost = 28, MinProfit = 15, ReclaimCost = 6, UndergroundCost = 400 };
        e.Method = EconRatioMethod.Price; Assert.Equal((320 - 95) / 28.0, e.ComputeEconRatio()!.Value, 9);
        e.Method = EconRatioMethod.PriceProfit; Assert.Equal((320 - 110) / 28.0, e.ComputeEconRatio()!.Value, 9);
        e.Method = EconRatioMethod.PriceProfitReclaim; Assert.Equal((320 - 116) / 28.0, e.ComputeEconRatio()!.Value, 9);
        e.Method = EconRatioMethod.CostComparison; Assert.Equal((400 - 95) / 28.0, e.ComputeEconRatio()!.Value, 9);
        e.StripCost = 0; Assert.Null(e.ComputeEconRatio());
    }

    [Fact]
    public void 经济参数缺省取自生产成本口径()
    {
        var e = new EconParams();
        Assert.Equal(ProductionCostBook.Current.CoalPriceYuanT, e.Price);
        Assert.Equal(ProductionCostBook.Current.StripCostYuanM3, e.StripCost);
        Assert.Equal(1.35, e.CoalDensity);
        Assert.Contains(nameof(ProductionCostBook.CoalPriceYuanT), ProductionCostBook.Current.UnapprovedItems());
        var b = ProductionCostBook.CreateDefault();
        Assert.False(b.Set("StripCostYuanM3", -1, CostSource.Approved));
        Assert.True(b.Set("StripCostYuanM3", 33, CostSource.Approved));
        Assert.Equal(33, b.StripCostYuanM3);
        Assert.Equal(CostSource.Approved, b.SourceOf("StripCostYuanM3"));
    }

    [Fact]
    public void 方案克隆_深拷帮角与几何引用且结果作废()
    {
        var a = PitScheme.CreateSamples()[0];
        a.Geometry.WallSegments.Add(new SegmentBeta { Index = 0, BetaDeg = 42 });
        var c = a.Clone();
        Assert.EndsWith("·副本", c.Name);
        Assert.Null(c.Result);
        Assert.False(c.IsConfirmed);
        c.Walls[0].BetaDeg = 99; Assert.NotEqual(99, a.Walls[0].BetaDeg);
        c.Econ.Price = 1; Assert.NotEqual(1, a.Econ.Price);
        Assert.Single(c.Geometry.WallSegments);
        Assert.NotSame(a.Geometry.WallSegments[0], c.Geometry.WallSegments[0]);
    }

    [Fact]
    public void 块体煤口径_分类列含煤标签取该码_找不到返回false()
    {
        var m = MakeModel();
        Assert.True(BlockModelCoal.TryGuessCoal(m, out var attr, out var val, out var tol));
        Assert.Equal("矿岩类型", attr); Assert.Equal(1, val); Assert.Equal(0.5, tol);
        Assert.True(BlockModelCoal.TryGetClassifier(m, out _, out var cls));
        Assert.True(cls.IsCoal(1)); Assert.True(cls.IsRock(0));
        Assert.Equal("ash", BlockModelCoal.FindAshAttr(m));

        var empty = new BlockModelMeta { IsRegular = false };
        empty.PropertySchema.Add(new BlockPropertyColumn { Name = "grade" });
        empty.Attrs["grade"] = new double[0];
        Assert.False(BlockModelCoal.TryGuessCoal(empty, out _, out _, out _));
        Assert.Null(BlockModelCoal.DetectAuto(empty));
    }

    [Fact]
    public void 矿床自动识别_水平煤层判近水平且走向沿长边()
    {
        var m = MakeModel();
        var r = BlockModelCoal.DetectAuto(m);
        Assert.NotNull(r);
        Assert.True(r!.Value.sig.DipDeg < 5, $"dip={r.Value.sig.DipDeg}");
        Assert.Equal(DepositType.NearHorizontal, PitSchemeConfigWindow.ClassifyDeposit(r.Value.sig.DipDeg, r.Value.sig.SeamCount));
        Assert.Equal(StripRatioPrinciple.Average, PitSchemeConfigWindow.PrincipleFor(DepositType.NearHorizontal));
        Assert.Equal(DepositType.MultiSeam, PitSchemeConfigWindow.ClassifyDeposit(10, 2));
        Assert.Equal(DepositType.SteepDip, PitSchemeConfigWindow.ClassifyDeposit(50, 1));
        Assert.Equal(DelineationMethod.FloatingCone, PitSchemeConfigWindow.MethodFor(DepositType.IrregularMassive));
    }

    [Fact]
    public void 逐层采样_煤岩体积守恒且灰分体积加权()
    {
        var m = MakeModel();
        var p = PlanSectionSampler.SampleLayers(m, "矿岩类型", 1, 0.5, 1.35, ashAttr: "ash");
        Assert.Equal(8, p.Nz);
        double cell = 1000;
        Assert.Equal(20 * 6 * 3 * cell, p.TotalCoalVol, 6);
        Assert.Equal(20 * 6 * 3 * cell, p.CoalVol.Sum(), 6);
        Assert.Equal(20 * 6 * 5 * cell, p.WasteVol.Sum(), 6);
        Assert.True(p.HasAsh);
        Assert.Equal(13.0, p.AvgAshPct, 6);   // (12+13+14)/3
        Assert.Equal(200.0 * 60.0, p.PlanAreaM2, 6);
        // 层序：k=0 底(煤)，k=7 顶(岩)
        Assert.True(p.CoalVol[0] > 0 && p.WasteVol[0] == 0);
        Assert.True(p.CoalVol[7] == 0 && p.WasteVol[7] > 0);
    }

    [Fact]
    public void 逐层采样_逐层裁剪_收口层出圈_圈外不计入但总煤不受裁()
    {
        var m = MakeModel();
        // 顶 4 层用左半足迹裁，底 4 层置空（坑已收口）
        var half = new[] { 1000.0, 2000, 1100, 2000, 1100, 2060, 1000, 2060 };
        var clips = new double[8][];
        for (int k = 0; k < 8; k++) clips[k] = k >= 4 ? half : Array.Empty<double>();
        var p = PlanSectionSampler.SampleLayers(m, "矿岩类型", 1, 0.5, 1.35, layerClipsXY: clips);
        Assert.Equal(20 * 6 * 3 * 1000.0, p.TotalCoalVol, 6);   // 分母不受裁
        Assert.Equal(0, p.CoalVol.Sum(), 6);                     // 煤全在底 3 层，被收口
        Assert.Equal(10 * 6 * 4 * 1000.0, p.WasteVol.Sum(), 6);  // 顶 4 层左半
    }

    [Fact]
    public void 净值最大定坑底_收益足够时挖穿全部煤_剥离过贵时不挖()
    {
        var m = MakeModel();
        var p = PlanSectionSampler.SampleLayers(m, "矿岩类型", 1, 0.5, 1.35);
        var s = PitSectionSolver.SolveDepth(p, revenuePerCoalT: 225, stripCostPerM3: 28);
        Assert.Equal(0, s.BottomK);
        Assert.Equal(p.TotalCoalVol * 1.35, s.CoalT, 3);
        Assert.Equal(p.WasteVol.Sum(), s.WasteM3, 3);
        Assert.Equal(8, s.CurveDepthM.Length);
        Assert.Equal(80, s.CurveDepthM[^1], 6);
        Assert.True(s.NetValueYuan > 0);

        // 剥离过贵：净值处处为负 → 原版语义 bestNet 从 −∞ 起, 首层(顶层)必被选中 ⇒ 坑底=顶层、净值夹 0（不是"不挖"）
        var bad = PitSectionSolver.SolveDepth(p, revenuePerCoalT: 1, stripCostPerM3: 1000);
        Assert.Equal(p.Nz - 1, bad.BottomK);
        Assert.Equal(0, bad.NetValueYuan);

        // 几何限深 30 m → 只能到第 5 层(k=5)，煤在底 3 层碰不到 → 净值 ≤0 → 不挖
        var capped = PitSectionSolver.SolveDepth(p, 225, 28, maxDepthM: 30);
        Assert.True(capped.BottomK >= 5);
    }

    [Fact]
    public void 评价_回收率满_校核与单位成本与泰勒年限()
    {
        var m = MakeModel();
        var p = PlanSectionSampler.SampleLayers(m, "矿岩类型", 1, 0.5, 1.35, ashAttr: "ash");
        var sol = PitSectionSolver.SolveDepth(p, 225, 28);
        var s = new PitScheme { BenchHeightM = 12, BottomWidthM = 60 };
        var r = PitEvaluator.Evaluate(p, sol, s, 8.04);
        Assert.Equal(100, r.RecoveryPct, 6);
        Assert.Equal(80, r.DepthM, 6);
        Assert.Equal(7, r.BenchCount);   // ceil(80/12)
        Assert.Equal(13.0, r.AvgAshPct, 6);
        double coalWanT = sol.CoalT / 1e4, wasteWanM3 = sol.WasteM3 / 1e4;
        Assert.Equal(wasteWanM3 / coalWanT, r.AvgRatio, 9);
        Assert.Equal(r.AvgRatio <= 8.04, r.Ok);
        Assert.Equal(s.Econ.MiningCost + r.AvgRatio * s.Econ.StripCost, r.UnitCostYuanPerT, 9);
        double life = Math.Min(60, Math.Max(5, 6.5 * Math.Pow(coalWanT / 100.0, 0.25)));
        Assert.Equal(life, r.ServiceLifeYears, 9);
        Assert.Equal(coalWanT / life, r.AnnualCoalWanT, 9);
        Assert.Equal(1.18, r.MinSafetyF, 9);   // 默认四帮最小 F
        Assert.Equal(8, r.CurveDepthM.Length);

        var s2 = new PitScheme { TargetAnnualWanT = 5 };
        var r2 = PitEvaluator.Evaluate(p, sol, s2, 8.04);
        Assert.Equal(5, r2.AnnualCoalWanT, 9);
        Assert.Equal(coalWanT / 5, r2.ServiceLifeYears, 9);
    }

    [Fact]
    public async System.Threading.Tasks.Task 求解编排_无块体或无煤属性报失败_有块体出结果()
    {
        var host = new FakeHost();
        var s = new PitScheme { Name = "T" };
        var o = await PitSolveRunner.SolveAsync(s, host);
        Assert.False(o.Ok); Assert.Contains("无激活块体", o.Message);

        host.Model = MakeModel();
        host.Model.Attrs.Remove("矿岩类型"); host.Model.PropertySchema.RemoveAt(0);
        o = await PitSolveRunner.SolveAsync(s, host);
        Assert.False(o.Ok); Assert.Contains("煤/岩属性", o.Message);

        host.Model = MakeModel();
        s.Econ.StripCost = 0;
        o = await PitSolveRunner.SolveAsync(s, host);
        Assert.False(o.Ok); Assert.Contains("经济合理剥采比无效", o.Message);

        s.Econ.StripCost = 28; s.BottomWidthM = 20;
        o = await PitSolveRunner.SolveAsync(s, host);
        if (!o.Ok) throw new Exception("求解失败: " + o.Message);
        Assert.NotNull(o.Result);
        // 默认四帮 β(18/38/38/35°) + 底宽 20 对 60 m 宽足迹：几何限深 ≈ 十几米, 够不着底 3 层煤 → 煤 0、深度只到岩层
        Assert.True(o.Result!.DepthM > 0 && o.Result.DepthM < 30, $"depth={o.Result.DepthM}");
        Assert.Equal(0, o.Result.CoalWanT, 9);
        Assert.Contains("几何限深", o.Message);
        Assert.Equal(s.Econ.ComputeEconRatio()!.Value, o.Result.EconRatio, 9);
        // 帮全部立到 80° → 几何不再限深 → 挖穿煤层
        foreach (var w in s.Walls) w.BetaDeg = 80;
        o = await PitSolveRunner.SolveAsync(s, host);
        if (!o.Ok) throw new Exception("求解失败: " + o.Message);
        Assert.Equal(80, o.Result!.DepthM, 6);
        Assert.True(o.Result.CoalWanT > 0);
        Assert.Contains("经济限深", o.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task 求解编排_地表界限深_底宽越大坑越浅()
    {
        var host = new FakeHost { Model = MakeModel() };
        // 地表界 = 块体足迹矩形（闭合），β 全 40°
        host.Polys[7] = (new[] { 1000.0, 2000, 180, 1200, 2000, 180, 1200, 2060, 180, 1000, 2060, 180 }, true);
        var wide = new PitScheme { Name = "宽底", BottomWidthM = 10 };
        wide.Geometry.SurfaceLimitHandle = 7;
        var narrow = new PitScheme { Name = "窄底", BottomWidthM = 50 };
        narrow.Geometry.SurfaceLimitHandle = 7;
        var ow = await PitSolveRunner.SolveAsync(wide, host);
        var on = await PitSolveRunner.SolveAsync(narrow, host);
        Assert.True(ow.Ok, ow.Message); Assert.True(on.Ok, on.Message);
        Assert.True(on.Result!.DepthM <= ow.Result!.DepthM, $"{on.Result.DepthM} vs {ow.Result.DepthM}");
        Assert.Contains("几何限深", on.Message);
    }

    [Fact]
    public void 顶口解析_地表界优先_否则块体足迹兜底()
    {
        var host = new FakeHost { Model = MakeModel() };
        var t = PitSchemeEnvelope.ResolveTopOutline(host, 0, host.Model);
        Assert.NotNull(t); Assert.False(t!.FromSurfaceLimit); Assert.Equal(4, t.Count); Assert.Equal(180, t.Zsurface, 6);
        host.Polys[3] = (new[] { 0.0, 0, 50, 100, 0, 50, 100, 100, 50 }, true);
        var u = PitSchemeEnvelope.ResolveTopOutline(host, 3, host.Model);
        Assert.True(u!.FromSurfaceLimit); Assert.Equal(3, u.Count); Assert.Equal(50, u.Zsurface, 6);
        Assert.Null(PitSchemeEnvelope.ResolveTopOutline(host, 0, null));
    }

    [Fact]
    public void 分帮β_段绑定优先_否则按方位映射_收缩量叠加()
    {
        var host = new FakeHost();
        host.Polys[1] = (new[] { 0.0, 0, 0, 100, 0, 0, 100, 100, 0, 0, 100, 0 }, true);
        var top = PitSchemeEnvelope.ResolveTopOutline(host, 1, null)!;
        var s = new PitScheme { ContractionM = 5 };
        s.Walls.Clear();
        s.Walls.Add(new Cad.Plan.WallAngle { SideName = "东帮", BetaDeg = 45 });
        s.Walls.Add(new Cad.Plan.WallAngle { SideName = "西帮", BetaDeg = 35 });
        var b = PitSchemeEnvelope.ResolveEdgeBetas(top, s);
        Assert.Equal(4, b.Length);
        // 边 1 (100,0)→(100,100) 外法向 +X = 东 → 45；边 3 外法向 −X = 西 → 35；南/北无匹配 → 平均 40
        Assert.Equal(45, b[1], 6); Assert.Equal(35, b[3], 6); Assert.Equal(40, b[0], 6);
        for (int i = 0; i < 4; i++) s.Geometry.WallSegments.Add(new SegmentBeta { Index = i, BetaDeg = 50 + i, InsetExtraM = i });
        var b2 = PitSchemeEnvelope.ResolveEdgeBetas(top, s);
        Assert.Equal(new[] { 50.0, 51, 52, 53 }, b2);
        var c = PitSchemeEnvelope.ResolveEdgeContraction(top, s);
        Assert.Equal(new[] { 5.0, 6, 7, 8 }, c);
    }

    [Fact]
    public void 落地_未求解拒绝_已求解出台阶面与首尾相接的环()
    {
        var host = new FakeHost { Model = MakeModel() };
        var s = new PitScheme { Name = "A/B", BenchHeightM = 10, BenchFaceAngleDeg = 70, BottomWidthM = 10 };
        Assert.False(PitMaterializer.Materialize(s, host).Ok);
        s.Result = new PitResult { DepthM = 40 };
        var o = PitMaterializer.Materialize(s, host);
        Assert.True(o.Ok, o.Message);
        var batch = host.Imported.Single();
        Assert.Equal("境界_A_B", batch.Layer);
        Assert.Single(batch.Meshes);
        Assert.True(batch.Rings.Count >= 3);
        // 环首个是坡顶(地表标高)，环标高递减且坡顶/坡底交替
        Assert.Equal(180, batch.Rings[0].z, 6);
        Assert.True(batch.Rings[1].z < batch.Rings[0].z);
        // 放样网：顶点数 = 环数 × 4，三角数 = (环数−1) × 4 × 2
        var mesh = batch.Meshes[0];
        Assert.Equal(batch.Rings.Count * 4, mesh.verts.Count);
        Assert.Equal((batch.Rings.Count - 1) * 8, mesh.tris.Count);
        Assert.Equal(o.Levels, batch.Rings.Count);
    }

    [Fact]
    public void 对比矩阵_方向感知最优标记_综合评分与推荐()
    {
        var schemes = PitScheme.CreateSamples();
        var cmp = ComparisonBuilder.Build(schemes);
        Assert.Equal(3, cmp.Names.Length);
        var coal = cmp.Rows.Single(r => r.Label.StartsWith("煤量"));
        Assert.StartsWith("▲", coal.Cells[2]);            // 方案C 煤量最大(高优)
        Assert.Equal("方案C·高煤价", coal.Best);
        var avg = cmp.Rows.Single(r => r.Label.StartsWith("平均剥采比"));
        Assert.StartsWith("▲", avg.Cells[0]);             // 方案A 最小(低优)
        var contour = cmp.Rows.Single(r => r.Label.StartsWith("境界剥采比"));
        Assert.Equal("—", contour.Best);                   // 仅展示不计分
        Assert.True(cmp.RecommendIndex >= 0);
        var score = cmp.Rows.Single(r => r.Label.StartsWith("综合评分"));
        Assert.StartsWith("▲", score.Cells[cmp.RecommendIndex]);
        var rank = cmp.Rows.Single(r => r.Label == "排名");
        Assert.Contains("第 1 名", rank.Cells[cmp.RecommendIndex]);
        Assert.Equal("✔ 推荐", cmp.Rows.Single(r => r.Label == "推荐").Cells[cmp.RecommendIndex]);
        // 无已解方案 → 空矩阵
        var none = ComparisonBuilder.Build(new[] { new PitScheme() });
        Assert.Empty(none.Rows); Assert.Equal(-1, none.RecommendIndex);
        // 分组标题行
        var grouped = ComparisonBuilder.Build(schemes, withGroupHeaders: true);
        Assert.Equal(7, grouped.Rows.Count(r => r.IsGroupHeader));
        var csv = ComparisonBuilder.ToCsv(cmp);
        Assert.Contains("指标,方案A·基准,方案B·陡帮,方案C·高煤价,最优", csv);
    }

    [Fact]
    public void 剖面线布置_沿走向按间距贯穿足迹_间距推荐钳位取整()
    {
        var top = new PitTopOutline { Cx = 50, Cy = 25, Zsurface = 0 };
        top.X.AddRange(new[] { 0.0, 100, 100, 0 }); top.Y.AddRange(new[] { 0.0, 0, 50, 50 });
        var lines = PitSchemeConfigWindow.SectionLines(top, 0, 25);   // 走向沿 +X，每 25 m 一条
        Assert.Equal(5, lines.Count);                                   // −50..+50 → 5 条
        foreach (var l in lines) { Assert.Equal(l.x0, l.x1, 6); Assert.Equal(50, Math.Abs(l.y1 - l.y0), 6); }   // 垂直走向贯穿 y 范围
        Assert.Equal(25, PitSchemeConfigWindow.RecommendSpacing(100));   // 100/30≈3.3→5 倍数→5 → 钳 25
        Assert.Equal(60, PitSchemeConfigWindow.RecommendSpacing(1850));  // 61.7→60
        Assert.Equal(100, PitSchemeConfigWindow.RecommendSpacing(9000)); // 钳 100
        Assert.Equal(80, PitSchemeConfigWindow.RecommendBottomWidth("WK-20 电铲"));
        Assert.Equal(60, PitSchemeConfigWindow.RecommendBottomWidth("未知"));
    }
}
