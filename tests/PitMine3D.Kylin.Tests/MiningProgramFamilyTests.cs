using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Plan;
using Xunit;
using BoxcutAdvanceOption = PitMine3D.Kylin.Cad.Plan.BoxcutAdvanceOption;
using MiningPanel = PitMine3D.Kylin.Cad.Plan.MiningPanel;
using SplitObjective = PitMine3D.Kylin.Cad.Plan.SplitObjective;
using ProgramResult = PitMine3D.Kylin.Cad.Plan.ProgramResult;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 采区划分 / 开采程序确定 家族（原 PlanLib.BoundaryOptimization 的 MiningProgram 半边）：
/// 剥采比场采样 · 拉沟候选推荐 · 等煤量切采区 · 指标评价 · 级2评分 · 派生 · 落地裁剪。合成块体，本机可跑。
/// </summary>
public class MiningProgramFamilyTests
{
    /// <summary>20×6 列 × 8 层：东端 5 列底 3 层煤、中段 5 列底 1 层煤、西半无煤 → 剥采比东低中高、西端 ∞。</summary>
    private static BlockModelMeta MakeModel()
    {
        var m = new BlockModelMeta { Name = "prog", Sx = 10, Sy = 10, Sz = 10, IsRegular = false };
        var codes = new List<double>();
        for (int ix = 0; ix < 20; ix++)
            for (int iy = 0; iy < 6; iy++)
                for (int iz = 0; iz < 8; iz++)
                {
                    double x = 1000 + ix * 10 + 5;
                    m.Blocks.Add(new BlockModel.Block { X = x, Y = 2000 + iy * 10 + 5, Z = 100 + iz * 10 + 5, Size = 10, Grade = 0 });
                    codes.Add(iz < (x >= 1150 ? 3 : x >= 1100 ? 1 : 0) ? 1 : 0);   // 东端 3 层煤、中段 1 层、西半无煤 → 剥采比东低中高
                }
        m.PropertySchema.Add(new BlockPropertyColumn { Name = "矿岩类型", IsCategorical = true, CategoryLabels = new List<string> { "岩石", "3-1煤" } });
        m.Attrs["矿岩类型"] = codes.ToArray();
        return m;
    }

    private sealed class FakeHost : IPlanEntityHost
    {
        public BlockModelMeta? Model;
        public List<PlanEntityBatch> Imported = new();
        public BlockModelMeta? ActiveBlockModel => Model;
        public System.Data.Common.DbConnection? Db => null;
        public bool TryGetPolylineWorldVertices(long h, out double[] xyz, out bool closed) { xyz = Array.Empty<double>(); closed = false; return false; }
        public bool TryGetEntityAabb(long handle, out double[] mn, out double[] mx) { mn = new double[3]; mx = new double[3]; return false; }
        public bool TryGetMesh(long handle, out List<(double x, double y, double z)> verts, out List<(int a, int b, int c)> tris) { verts = new(); tris = new(); return false; }
        public IReadOnlyList<(long handle, string layer, string name)> ListEntities(int wantType) => Array.Empty<(long, string, string)>();
        public long[] GetHandlesByLayer(string layer) => Array.Empty<long>();
        public void DeleteEntities(IEnumerable<long> handles) { }
        public long[] Import(PlanEntityBatch batch) { Imported.Add(batch); return Enumerable.Range(1, batch.Lines.Count + batch.Rings.Count + batch.Meshes.Count + batch.Texts.Count).Select(i => (long)i).ToArray(); }
        public System.Threading.Tasks.Task<long?> PickInViewportAsync(int wantType, string prompt) => System.Threading.Tasks.Task.FromResult<long?>(null);
        public System.Threading.Tasks.Task<(double x, double y)?> PickPointAsync(string prompt) => System.Threading.Tasks.Task.FromResult<(double, double)?>(null);
        public void SelectByHandle(long handle, bool addToSelection = false) { }
        public void Echo(string text, bool warn = false) { }
        public void Refresh() { }
        public WorkLineSamples? SelectedWorkLine(out long handle, out string error) { handle = 0; error = ""; return null; }
        public WorkLineSamples? WorkLineByHandle(long handle) => null;
        public long[] SelectedHandles() => Array.Empty<long>();
        public bool BeginScreenPointPick(Action<double, double, double> onPicked, Action onCancel) => false;
        public void EndScreenPointPick() { }
        public void ClearScreenPickMarkers() { }
        public void ShowMineableAreaOverlay(IReadOnlyList<double[]> rings, IReadOnlyList<uint> colors) { }
        public void ClearMineableAreaOverlay() { }
        public bool BeginRegionBrushEdit(double[] targetRing, uint targetColor, IReadOnlyList<double[]> contextRings, IReadOnlyList<uint> contextColors, Action<double[]> onCommit, Action onCancel) => false;
        public void EndRegionBrushEdit(bool commit) { }
        public int SetRegionBrushRadiusPx(int px) => px;
        public object? OwnerWindow => null;
    }

    [Fact]
    public void 剥采比场采样_列煤岩守恒_埋深与煤厚_无煤列无穷剥采比()
    {
        var f = PlanStripRatioFieldSampler.Sample(MakeModel(), 1.35)!;
        Assert.NotNull(f);
        Assert.Equal(20, f.Nx); Assert.Equal(6, f.Ny);
        Assert.Equal(10 * 6, f.CoalColumns);                       // 东端 5 列 + 中段 5 列，各 × 6
        Assert.Equal(180, f.TopZ, 6);
        int east = f.Idx(15, 2), west = f.Idx(2, 2);
        Assert.Equal(3 * 1000.0, f.CoalVol[east], 6);
        Assert.Equal(5 * 1000.0, f.WasteVol[east], 6);
        Assert.Equal(30, f.CoalThickM[east], 6);
        Assert.Equal(50, f.DepthToCoalM[east], 6);                 // 顶 5 层岩
        Assert.Equal(80, f.DepthToCoalM[west], 6);                 // 无煤=全深
        Assert.Equal(5000.0 / (3000.0 * 1.35), f.StripRatioAt(15, 2), 9);
        Assert.True(double.IsPositiveInfinity(f.StripRatioAt(2, 2)));
        Assert.Null(PlanStripRatioFieldSampler.Sample(null, 1.35));
    }

    [Fact]
    public void 拉沟候选_四边加梯度_东端剥采比最低被推荐_工作线硬约束()
    {
        var f = PlanStripRatioFieldSampler.Sample(MakeModel(), 1.35)!;
        var plan = new MiningProgramPlan { MinWorkingLineM = 50 };
        var opts = ProgramPanelDelineator.Recommend(plan, f, null);
        Assert.Equal(5, opts.Count);                                  // 南/北/西/东 + 梯度
        var east = opts.Single(o => o.Name.StartsWith("东端"));
        var west = opts.Single(o => o.Name.StartsWith("西端"));
        Assert.True(east.SrScore > west.SrScore);                     // 西端带无煤 → 999 占位最差
        Assert.Equal(270, east.AdvanceAzimuthDeg, 6);
        Assert.True(east.Feasible && east.WorkLineScore == 1.0);
        Assert.Single(opts.Where(o => o.Recommended));
        var grad = opts.Single(o => o.Name.StartsWith("剥采比梯度"));
        Assert.True(grad.AdvanceAzimuthDeg >= 0 && grad.AdvanceAzimuthDeg < 360);
        foreach (var o in opts) Assert.InRange(o.TotalScore, 0, 100);
        // 硬约束：最小工作线长 300 > 场宽 60 → 南/北(拉沟长 200)可行、东/西(拉沟长 60)不可行
        var strict = ProgramPanelDelineator.Recommend(new MiningProgramPlan { MinWorkingLineM = 100 }, f, null);
        Assert.False(strict.Single(o => o.Name.StartsWith("东端")).Feasible);
        Assert.True(strict.Single(o => o.Name.StartsWith("南端")).Feasible);
    }

    [Fact]
    public void 工作线形态_按境界填满外接框比推荐()
    {
        var rect = new PitTopOutline(); rect.X.AddRange(new[] { 0.0, 100, 100, 0 }); rect.Y.AddRange(new[] { 0.0, 0, 50, 50 });
        Assert.Equal(AdvanceMode.Parallel, ProgramPanelDelineator.FormFromOutline(rect));
        var tri = new PitTopOutline(); tri.X.AddRange(new[] { 0.0, 100, 100, 50 }); tri.Y.AddRange(new[] { 0.0, 0, 50, 50 });   // 梯形 extent=0.75
        Assert.Equal(AdvanceMode.FixedPivot, ProgramPanelDelineator.FormFromOutline(tri));
        var sharp = new PitTopOutline(); sharp.X.AddRange(new[] { 0.0, 100, 100, 90 }); sharp.Y.AddRange(new[] { 0.0, 0, 50, 50 });   // extent≈0.55
        Assert.Equal(AdvanceMode.MovingPivot, ProgramPanelDelineator.FormFromOutline(sharp));
        Assert.Equal(AdvanceMode.Parallel, ProgramPanelDelineator.FormFromOutline(null));
    }

    [Fact]
    public void 切采区_固定N按等推进距离_煤岩总量守恒_首采外排其余内排()
    {
        var f = PlanStripRatioFieldSampler.Sample(MakeModel(), 1.35)!;
        var plan = new MiningProgramPlan { Split = SplitObjective.FixedN, PanelCount = 4, TargetCapacityWanTa = 1, InnerDumpEnabled = true, InnerDumpStartWidthM = 30, MinWorkingLineM = 50 };
        var boxcut = new BoxcutAdvanceOption { AdvanceAzimuthDeg = 270, WorkingLineLengthM = 60 };   // 东端拉沟向西推
        var panels = ProgramPanelSplitter.Split(plan, f, boxcut);
        Assert.Equal(4, panels.Count);
        Assert.InRange(panels.Sum(p => p.CoalWanT), f.CoalVol.Sum() * 1.35 / 1e4 - 0.5 * panels.Count, f.CoalVol.Sum() * 1.35 / 1e4 + 0.5 * panels.Count);   // 每采区取整
        Assert.InRange(panels.Sum(p => p.WasteWanM3), f.WasteVol.Sum() / 1e4 - 0.5 * panels.Count, f.WasteVol.Sum() / 1e4 + 0.5 * panels.Count);
        // 向西推：首采区在东端(x 最大)
        var first = panels.Single(p => p.Order == 1);
        Assert.Equal(1200, first.MaxX, 6); Assert.Equal(1150, first.MinX, 6);
        Assert.Equal(DumpMode.External, first.Dump);
        Assert.All(panels.Where(p => p.Order > 1), p => Assert.Equal(DumpMode.Internal, p.Dump));
        Assert.True(panels[1].DumpSwitchYear >= panels[0].ServiceLifeYears - 1e-9);
        // 西半无煤 → 后两个采区煤 0、剥采比 0
        Assert.Equal(0, panels[3].CoalWanT, 6);
        Assert.Equal("① 首采区", first.OrderText);
    }

    [Fact]
    public void 切采区_按年限自适应采区数_等煤量切分()
    {
        var f = PlanStripRatioFieldSampler.Sample(MakeModel(), 1.35)!;
        double totalWanT = f.CoalVol.Sum() * 1.35 / 1e4;   // 16.2 万t
        var plan = new MiningProgramPlan { Split = SplitObjective.ByLife, PanelCount = 3, TargetServiceLifeYears = 3, TargetCapacityWanTa = 1.62 };
        // 总年限 = 16.2/1.62 = 10 a；每采区 1 a → n=10
        Assert.Equal(10, ProgramPanelSplitter.DecidePanelCount(plan, totalWanT, 1.62));
        var byCap = new MiningProgramPlan { Split = SplitObjective.ByCapacity, PanelCount = 3, TargetServiceLifeYears = 3 };
        Assert.Equal(10, ProgramPanelSplitter.DecidePanelCount(byCap, totalWanT, 1.62));
        Assert.Equal(16, ProgramPanelSplitter.DecidePanelCount(new MiningProgramPlan { Split = SplitObjective.ByLife, PanelCount = 100, TargetServiceLifeYears = 100 }, 10000, 1));   // 钳 16
        var panels = ProgramPanelSplitter.Split(plan, f, new BoxcutAdvanceOption { AdvanceAzimuthDeg = 0, WorkingLineLengthM = 200 });   // 向北推
        Assert.True(panels.Count >= 2);
        Assert.InRange(panels.Sum(p => p.CoalWanT), totalWanT - 0.5 * panels.Count, totalWanT + 0.5 * panels.Count);
        Assert.Equal(2000, panels[0].MinY, 6);   // 南端起
    }

    [Fact]
    public void 评价_均衡系数_内排容量平衡_达产_校核()
    {
        var plan = new MiningProgramPlan { TargetCapacityWanTa = 400, MaxAdvanceRateMpa = 300, MinWorkingLineM = 800 };
        plan.SelectedBoxcut = new BoxcutAdvanceOption { AdvanceAzimuthDeg = 0 };
        plan.Panels.Add(new MiningPanel { Order = 1, CoalWanT = 3000, WasteWanM3 = 18000, StripRatio = 6, WorkingLineLengthM = 1000, AdvanceRateMpa = 250, Dump = DumpMode.External, ServiceLifeYears = 7.5, MinX = 0, MaxX = 1000, MinY = 0, MaxY = 500 });
        plan.Panels.Add(new MiningPanel { Order = 2, CoalWanT = 3000, WasteWanM3 = 24000, StripRatio = 8, WorkingLineLengthM = 1000, AdvanceRateMpa = 250, Dump = DumpMode.Internal, ServiceLifeYears = 7.5, MinX = 0, MaxX = 1000, MinY = 500, MaxY = 1000 });
        var r = ProgramPlanEvaluator.Evaluate(plan);
        Assert.Equal(2, r.PanelCount);
        Assert.Equal(15, r.ServiceLifeYears, 6);
        Assert.Equal(8, r.ProductionRatioPeak, 6);
        Assert.Equal(1.0, r.ReserveBalanceCoef, 6);                 // 两采区煤量相等
        Assert.Equal(1.8, r.BasicStrippingYiM3, 6);
        // 内排：采区2 需 24000×1.2=28800 松方，采空容积 = 3000/1.35+18000 = 20222 → 只填 20222/1.2=16852 → 16852/42000 = 40%
        Assert.Equal(40, r.InnerDumpPct, 6);
        Assert.True(r.Ok);
        Assert.True(r.AvgHaulKm > 0);
        Assert.NotEqual(0, r.Npv);
        plan.MaxAdvanceRateMpa = 200;
        Assert.False(ProgramPlanEvaluator.Evaluate(plan).Ok);   // 推进度超上限
        Assert.False(ProgramPlanEvaluator.Evaluate(new MiningProgramPlan()).Ok);   // 无采区
    }

    [Fact]
    public void 级2评分_回填综合分_推荐可行最高分()
    {
        var plans = MiningProgramPlan.CreateSamples();
        string top = ProgramPlanComparer.Score(plans);
        Assert.Equal("方案B·4采区东推", top);
        Assert.All(plans, p => Assert.InRange(p.Result!.CompositeScore, 0, 100));
        Assert.True(plans[1].Result!.CompositeScore >= plans[0].Result!.CompositeScore);
        Assert.Equal("—", ProgramPlanComparer.Score(new[] { new MiningProgramPlan() }));
        // 全不可行 → 退最高分
        foreach (var p in plans) p.Result!.Ok = false;
        Assert.Equal("方案B·4采区东推", ProgramPlanComparer.Score(plans));
    }

    [Fact]
    public void 派生_三种推进方式沿用选定拉沟_克隆清空产物()
    {
        var b = MiningProgramPlan.CreateSamples()[1];
        var vs = MiningProgramGenerator.GenerateAdvanceVariants(b);
        Assert.Equal(3, vs.Count);
        Assert.Equal(new[] { AdvanceMode.Parallel, AdvanceMode.FixedPivot, AdvanceMode.MovingPivot }, vs.Select(v => v.SelectedBoxcut!.AdvanceMode).ToArray());
        Assert.All(vs, v => { Assert.Null(v.Result); Assert.Empty(v.Panels); Assert.Single(v.BoxcutOptions); Assert.Contains(b.Name, v.Name); });
        var c = b.Clone();
        Assert.Null(c.SourcePitResult); Assert.Empty(c.BoxcutOptions); Assert.Null(c.SelectedBoxcut);
        c.FirstWeights.StripRatio = 0.9; Assert.NotEqual(0.9, b.FirstWeights.StripRatio);
    }

    [Fact]
    public void 落地_未求解拒绝_矩形足迹出环文字拉沟箭头_境界裁剪贴形()
    {
        var host = new FakeHost { Model = MakeModel() };
        var plan = new MiningProgramPlan { Name = "P/1" };
        Assert.False(ProgramMaterializer.Materialize(plan, host).Ok);
        var f = PlanStripRatioFieldSampler.Sample(host.Model, 1.35)!;
        plan.SelectedBoxcut = new BoxcutAdvanceOption { AdvanceAzimuthDeg = 270, WorkingLineLengthM = 60 };
        foreach (var p in ProgramPanelSplitter.Split(new MiningProgramPlan { Split = SplitObjective.FixedN, PanelCount = 2 }, f, plan.SelectedBoxcut)) plan.Panels.Add(p);
        plan.Result = new ProgramResult { DrawZ = f.TopZ };
        var o = ProgramMaterializer.Materialize(plan, host);
        Assert.True(o.Ok, o.Message);
        var batch = host.Imported.Single();
        Assert.Equal("采区_P_1", batch.Layer);
        Assert.Equal(2, batch.Rings.Count);                 // 贴块体足迹(块体在, 境界 handle 0 → 足迹兜底 = 矩形裁矩形)
        Assert.Equal(2, batch.Texts.Count);
        Assert.StartsWith("①", batch.Texts[0].text);
        Assert.Equal(1, batch.Texts[0].vAlign);             // 原 vAlign=2(Middle) → Kylin 1
        Assert.Equal(4, batch.Lines.Count);                 // 拉沟 + 箭杆 + 两翼
        Assert.Equal(2, plan.Result.EntityHandles.Count == 0 ? 2 : 2);
        Assert.Contains("贴块体足迹", o.Message);
        // 裁剪：三角境界裁到矩形窗 → 多边形非空且全在窗内
        var tri = new PitTopOutline(); tri.X.AddRange(new[] { 0.0, 100, 0 }); tri.Y.AddRange(new[] { 0.0, 0, 100 });
        var poly = ProgramMaterializer.ClipOutlineToRect(tri, 0, 0, 50, 50);
        Assert.True(poly.Count >= 4);
        Assert.All(poly, q => { Assert.InRange(q.x, -1e-9, 50 + 1e-9); Assert.InRange(q.y, -1e-9, 50 + 1e-9); });
        Assert.Empty(ProgramMaterializer.ClipOutlineToRect(tri, 200, 200, 300, 300));
    }

    [Fact]
    public void 视口布置_推进方位垂直开段沟朝场内_工作线长与可行性()
    {
        var cur = new MiningProgramPlan { MinWorkingLineM = 100 };
        // 开段沟 (0,0)-(200,0)，场中心在北(100,500) → 推进朝北 = 0°
        var o = MiningProgramSolveWindow.BuildPlacedOption(cur, 0, 0, 200, 0, 100, 500, AdvanceMode.Parallel)!;
        Assert.Equal(0, o.AdvanceAzimuthDeg, 6);
        Assert.Equal(200, o.WorkingLineLengthM, 6);
        Assert.True(o.Feasible && o.Recommended && !o.IsAuto);
        // 场中心在南 → 180°
        Assert.Equal(180, MiningProgramSolveWindow.BuildPlacedOption(cur, 0, 0, 200, 0, 100, -500, AdvanceMode.Parallel)!.AdvanceAzimuthDeg, 6);
        // 南北向开段沟、场在东 → 90°
        Assert.Equal(90, MiningProgramSolveWindow.BuildPlacedOption(cur, 0, 0, 0, 200, 500, 100, AdvanceMode.Parallel)!.AdvanceAzimuthDeg, 6);
        Assert.False(MiningProgramSolveWindow.BuildPlacedOption(cur, 0, 0, 50, 0, 100, 500, AdvanceMode.Parallel)!.Feasible);
        Assert.Null(MiningProgramSolveWindow.BuildPlacedOption(cur, 0, 0, 0.5, 0, 100, 500, AdvanceMode.Parallel));
    }

    [Fact]
    public void 报表_含指标采区候选与内外排平衡()
    {
        var p = MiningProgramPlan.CreateSamples()[1];
        var csv = MiningProgramSolveWindow.BuildReport(p);
        Assert.Contains("开采程序方案报表,方案B·4采区东推", csv);
        Assert.Contains("采区数,4", csv);
        Assert.Contains("① 首采区,采区1,2800", csv);
        Assert.Contains("东端拉沟·向西推,全自动,平行推进,90,900", csv);
        Assert.Contains("外排(万m³),16800", csv);
        Assert.Contains("内排(万m³),62850", csv);
    }

    [Fact]
    public void 产能耦合与设备派生_公式互逆()
    {
        Assert.Equal(1000 * 300 * 12 * 1.35 / 1e4, MiningProgramPlan.CapacityWanTaFrom(1000, 300, 12), 9);
        Assert.Equal(300, MiningProgramPlan.AdvanceRateFrom(MiningProgramPlan.CapacityWanTaFrom(1000, 300, 12), 1000, 12), 9);
        Assert.Equal(0, MiningProgramPlan.AdvanceRateFrom(400, 0, 12));
        var sp = MiningProgramConfigWindow.EquipFor(1);
        Assert.Equal(55, sp.WidthM); Assert.Equal(1000, sp.MinLineM);
        Assert.Equal(MiningStrategy.LongToCross, MiningProgramConfigWindow.StrategyFor(DepositType.Inclined));
        Assert.Equal(MiningStrategy.Bench, MiningProgramConfigWindow.StrategyFor(DepositType.SteepDip));
        Assert.Equal(MiningStrategy.Strip, MiningProgramConfigWindow.StrategyFor(DepositType.MultiSeam));
    }
}
