using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Plan;
using Xunit;
using AdvanceMode = PitMine3D.Kylin.Cad.AdvanceMode;
using LongTermPlan = PitMine3D.Kylin.Cad.Plan.LongTermPlan;
using PlanPhase = PitMine3D.Kylin.Cad.Plan.PlanPhase;
using RampProfileKind = PitMine3D.Kylin.Cad.Plan.RampProfileKind;
using LongTermScheduler = PitMine3D.Kylin.Cad.Plan.LongTermScheduler;
using LongTermComparer = PitMine3D.Kylin.Cad.Plan.LongTermComparer;
using LongTermBase = PitMine3D.Kylin.Cad.Plan.LongTermBase;
using LongTermSchemeStore = PitMine3D.Kylin.Cad.Plan.LongTermSchemeStore;
using WorkLineAdvanceVariant = PitMine3D.Kylin.Cad.Plan.WorkLineAdvanceVariant;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 中长远进度计划编制家族（原 PlanLib.LongTerm）：块体沿工作线累计曲线 · 排产（划期/爬坡/均衡/现金流）· 派生笛卡尔积 · 联合评分 ·
/// 工作线拾取方位 · 排土桥（无库空池）· 方案库（LT3 初始为空 / LT4 无工作线拦住）· 转旧口径。合成块体，本机可跑。
/// </summary>
public class LongTermFamilyTests
{
    /// <summary>20 列(X 1000→1200) × 6 行 × 8 层，底 3 层煤：从西端工作线向东推进，每刀煤岩恒定 → 累计曲线线性。</summary>
    private static BlockModelMeta MakeModel(int coalLayers = 3)
    {
        var m = new BlockModelMeta { Name = "lt", Sx = 10, Sy = 10, Sz = 10, IsRegular = false };
        var codes = new List<double>();
        for (int ix = 0; ix < 20; ix++)
            for (int iy = 0; iy < 6; iy++)
                for (int iz = 0; iz < 8; iz++)
                {
                    m.Blocks.Add(new BlockModel.Block { X = 1000 + ix * 10 + 5, Y = 2000 + iy * 10 + 5, Z = 100 + iz * 10 + 5, Size = 10, Grade = 0 });
                    codes.Add(iz < coalLayers ? 1 : 0);
                }
        m.PropertySchema.Add(new BlockPropertyColumn { Name = "矿岩类型", IsCategorical = true, CategoryLabels = new List<string> { "岩石", "3-1煤" } });
        m.Attrs["矿岩类型"] = codes.ToArray();
        return m;
    }

    /// <summary>西端南北向工作线，逐段方向指东（+X）。</summary>
    private static WorkLineSamples WestLine(byte mode = 0)
    {
        var g = new WorkLineSamples { Success = true, AdvanceMode = mode };
        g.Baseline.Add((1000, 2000, 180)); g.Baseline.Add((1000, 2060, 180));
        g.Samples.Add((1000, 2030, 180, 1, 0));
        return g;
    }

    private static LongTermBase SmallBase() => new()
    {
        DesignCapacityWanTa = 8, BasicStrippingYears = 1, RampUpYears = 2, RampFirstYearPct = 50, StartYear = 2027,
        InnerDumpEnabled = true, InnerDumpStartYear = 2, BenchHeightM = 10,
    };

    [Fact]
    public void 工作线拾取_方位角与推进模式_零矢量拒绝()
    {
        var v = WorkLinePicker.FromGeometry(WestLine(), 0x2A, "W")!;
        Assert.NotNull(v);
        Assert.Equal(90, v.AdvanceAzimuthDeg, 6);                   // +X = 东 = 方位 90°
        Assert.Equal(AdvanceMode.Parallel, v.AdvanceMode);
        Assert.Equal(0x2A, v.SourceHandle);
        Assert.True(v.FromEntity);
        Assert.Equal(60, v.WorkLineLenM, 6);
        Assert.Equal(2, v.Baseline.Count);

        var fan = WestLine(2); fan.HasFanParams = true; fan.PivotX = 990; fan.PivotY = 2000;
        var f = WorkLinePicker.FromGeometry(fan, 1)!;
        Assert.Equal(AdvanceMode.FixedPivot, f.AdvanceMode);
        Assert.True(f.HasPivot); Assert.Equal(990, f.PivotX, 6);

        var zero = WestLine(); zero.Samples.Clear(); zero.Samples.Add((1000, 2030, 180, 0, 0));
        Assert.Null(WorkLinePicker.FromGeometry(zero, 1));
        Assert.Null(WorkLinePicker.FromGeometry(new WorkLineSamples { Success = false }, 1));
    }

    [Fact]
    public void 块体沿工作线累计曲线_线性且总量守恒_无块体无煤拦住()
    {
        var wl = WorkLinePicker.FromGeometry(WestLine(), 1, "W")!;
        var c = LongTermBlockSource.Build(MakeModel(), wl, 10, 75, 0, 1.35, sliceWidthM: 10);
        Assert.True(c.Ok, c.Error);
        // 总煤 = 3 层 × 120 柱 × 1000 m³ × 1.35 / 1e4 = 48.6 万t；总岩 = 5 层 × 120 × 1000 / 1e4 = 60 万m³
        Assert.Equal(48.6, c.TotalCoalWanT, 3);
        Assert.Equal(60.0, c.TotalStripWanM3, 3);
        Assert.Equal(60.0 / 48.6, c.OverallRatio, 6);
        Assert.True(c.Bins >= 20);
        // 累计单调不减
        for (int i = 1; i < c.CumCoalWanT.Count; i++) { Assert.True(c.CumCoalWanT[i] >= c.CumCoalWanT[i - 1] - 1e-9); Assert.True(c.CumStripWanM3[i] >= c.CumStripWanM3[i - 1] - 1e-9); }

        Assert.False(LongTermBlockSource.Build(null, wl, 10, 75, 0, 1.35).Ok);
        Assert.False(LongTermBlockSource.HasActiveBlockModel(null, out var n0)); Assert.Contains("未激活", n0);
        var noAttr = MakeModel(0); noAttr.Attrs.Remove("矿岩类型"); noAttr.PropertySchema.RemoveAt(0);
        Assert.False(LongTermBlockSource.HasActiveBlockModel(noAttr, out var n1)); Assert.Contains("煤", n1);
        Assert.True(LongTermBlockSource.HasActiveBlockModel(MakeModel(), out var n2)); Assert.Contains("lt", n2);
        // 几何不全
        var bad = wl.Copy(); bad.Samples.Clear();
        Assert.Contains("几何不全", LongTermBlockSource.Build(MakeModel(), bad, 10, 75, 0, 1.35).Error);
    }

    [Fact]
    public void 排产_划期爬坡均衡现金流_指标与时相自洽()
    {
        var b = SmallBase();
        var wl = WorkLinePicker.FromGeometry(WestLine(), 1, "W")!;
        var p = b.NewCandidate(wl, "W", b.DesignCapacityWanTa);   // 不传坡道档 → 沿用基础约束的首年 50%（传了档则按 RampPreset 预设，与原版一致）
        LongTermScheduler.Schedule(p, MakeModel(), null);
        Assert.NotNull(p.Result);
        Assert.True(p.Periods.Count >= 4, $"期数 {p.Periods.Count}: {p.ScheduleNote}");

        // 基建期：1 年、无煤、外排
        Assert.Equal(PlanPhase.Basic, p.Periods[0].Phase); Assert.Equal(0, p.Periods[0].CoalWanT); Assert.Equal("2027", p.Periods[0].Label);
        // 爬坡 2 年：首年 50% → 4 万t，第二年 100%
        Assert.Equal(PlanPhase.RampUp, p.Periods[1].Phase); Assert.Equal(50, p.Periods[1].CapacityPct, 6); Assert.Equal(4, p.Periods[1].CoalWanT, 0.51);
        Assert.Equal(PlanPhase.RampUp, p.Periods[2].Phase); Assert.Equal(100, p.Periods[2].CapacityPct, 6);
        Assert.True(p.Periods[2].IsDesignCalcYear);
        Assert.Equal("2029", p.Result!.DesignCalcYearLabel);
        // 末期减产
        Assert.Equal(PlanPhase.Decline, p.Periods[^1].Phase);
        // 累计守恒：煤 48.6，岩 60（四舍五入到整数后 ±1）
        Assert.Equal(48.6, p.Periods[^1].CumCoal, 1.0);
        Assert.Equal(60, p.Periods[^1].CumStrip, 1.5);
        // 内排从第 2 个生产年起
        Assert.Equal(DumpMode.External, p.Periods[1].Dump); Assert.Equal(DumpMode.Internal, p.Periods[3].Dump);
        // 指标
        Assert.Equal(p.Periods.Count(z => z.CoalWanT > 0), (int)p.Result.ServiceLifeYears);   // 服务年限只数生产年（原版同）
        Assert.True(p.Result.ProductionRatioPeak > 0);
        Assert.True(p.Result.InnerDumpPct > 0 && p.Result.InnerDumpPct <= 100);
        Assert.Contains("块体", p.Result.QuantitySourceText);
        Assert.True(p.Result.BalanceStages >= 1);
        Assert.Contains("排土形态未接", p.Result.DumpSourceText);
        Assert.Equal("已求解", p.SolvedText);
        // 现金流：基建年为负；折现累计 = NPV
        Assert.True(p.Periods[0].CashFlowWan <= 0);   // 首刀就见煤 → 基建剥离量为 0，现金流 0（不为正）
        Assert.Equal(p.Periods.Sum(z => z.NpvWan), p.Result.Npv, 1.0);
    }

    [Fact]
    public void 排产_无块体或无煤时不出结果并说明()
    {
        var b = SmallBase();
        var p = b.NewCandidate(WorkLinePicker.FromGeometry(WestLine(), 1, "W")!, "W", 8, RampProfileKind.Linear);
        LongTermScheduler.Schedule(p, null, null);
        Assert.Null(p.Result); Assert.Contains("块体", p.ScheduleNote); Assert.Equal("未求解", p.SolvedText);
        LongTermScheduler.Schedule(p, MakeModel(0), null);
        Assert.Null(p.Result); Assert.Contains("煤", p.ScheduleNote);
    }

    [Fact]
    public void 派生_工作线x产能x坡道笛卡尔积_命名与独立排产()
    {
        var b = SmallBase();
        var w1 = WorkLinePicker.FromGeometry(WestLine(), 1, "W1")!;
        var w2 = WorkLinePicker.FromGeometry(WestLine(), 2, "W2")!;
        var caps = new[] { new LongTermScheduler.CapacitySpec("基准", 1.0), new LongTermScheduler.CapacitySpec("+25%", 1.25) };
        var ramps = new[] { new LongTermScheduler.RampSpec("线性", RampProfileKind.Linear), new LongTermScheduler.RampSpec("尽快", RampProfileKind.Aggressive) };
        var list = LongTermScheduler.Generate(b, new[] { w1, w2 }, MakeModel(), null, caps, ramps, schedule: true);
        Assert.Equal(8, list.Count);
        Assert.All(list, p => Assert.NotNull(p.Result));
        Assert.Contains(list, p => p.Name == "W1·8万t·线性");
        Assert.Contains(list, p => p.Name == "W2·10万t·尽快" && p.DesignCapacityWanTa == 10 && p.RampProfile == RampProfileKind.Aggressive);
        Assert.Equal(2, list.Select(p => p.WorkLine.SourceHandle).Distinct().Count());
        // 单档时名字不带后缀
        var one = LongTermScheduler.Generate(b, new[] { w1 }, MakeModel(), null, schedule: false);
        Assert.Single(one); Assert.Equal("W1", one[0].Name); Assert.Null(one[0].Result);
    }

    [Fact]
    public void 联合评分_推荐得分最高_单套亦有分_空集为破折号()
    {
        var b = SmallBase();
        var w1 = WorkLinePicker.FromGeometry(WestLine(), 1, "W1")!;
        var caps = new[] { new LongTermScheduler.CapacitySpec("基准", 1.0), new LongTermScheduler.CapacitySpec("×2", 2.0) };
        var list = LongTermScheduler.Generate(b, new[] { w1 }, MakeModel(), null, caps, null, schedule: true);
        string top = LongTermComparer.Score(list);
        Assert.All(list, p => Assert.InRange(p.Result!.CompositeScore, 0, 100));
        Assert.Equal(list.OrderByDescending(p => p.Result!.CompositeScore).First().Name, top);
        Assert.Equal("—", LongTermComparer.Score(Array.Empty<LongTermPlan>()));
        Assert.NotEqual("—", LongTermComparer.Score(new[] { list[0] }));
    }

    [Fact]
    public void 一键排产比选_按已指定工作线一线一套_推荐可行最高分()
    {
        var b = SmallBase();
        var wls = new[] { WorkLinePicker.FromGeometry(WestLine(), 1, "W1")!, WorkLinePicker.FromGeometry(WestLine(), 2, "W2")! };
        var (schemes, best) = LongTermScheduler.ComposeFrom(b, wls, MakeModel(), null);
        Assert.Equal(2, schemes.Count);
        Assert.NotNull(best);
        Assert.Equal(schemes.Where(s => s.Result != null).Max(s => s.Result!.CompositeScore), best!.Result!.CompositeScore);
        var (none, nb) = LongTermScheduler.ComposeFrom(b, Array.Empty<WorkLineAdvanceVariant>(), MakeModel(), null);
        Assert.Empty(none); Assert.Null(nb);
    }

    [Fact]
    public void 方案库_初始为空_未指定工作线拦住_按源实体去重()
    {
        LongTermSchemeStore.Reset();
        Assert.Empty(LongTermSchemeStore.Schemes);
        Assert.False(LongTermSchemeStore.HasSpecifiedWorkLine);
        Assert.Empty(LongTermSchemeStore.SpecifiedWorkLines());
        Assert.Contains("派生计划方案", LongTermSchemeStore.NeedWorkLineHint);
        var b = LongTermSchemeStore.Base;
        var w1 = WorkLinePicker.FromGeometry(WestLine(), 7, "W")!;
        LongTermSchemeStore.Schemes.Add(b.NewCandidate(w1.Copy(), "a", 8, RampProfileKind.Linear));
        LongTermSchemeStore.Schemes.Add(b.NewCandidate(w1.Copy(), "b", 10, RampProfileKind.Stepped));
        Assert.Single(LongTermSchemeStore.SpecifiedWorkLines());
        Assert.True(LongTermSchemeStore.HasSpecifiedWorkLine);
        // 自动续源：无上游程序 → false；有则继承名字
        Assert.False(LongTermSchemeStore.AutoInheritIfNeeded(null));
        var mp = new MiningProgramPlan { Name = "P1" };
        Assert.True(LongTermSchemeStore.AutoInheritIfNeeded(new[] { mp }));
        Assert.Equal("P1", LongTermSchemeStore.Base.SourceProgramName);
        Assert.False(LongTermSchemeStore.AutoInheritIfNeeded(new[] { mp }));   // 已续上不再覆盖
        LongTermSchemeStore.Reset();
    }

    [Fact]
    public void 排土桥_无库空池_空池不改内排率来源()
    {
        var f = LongTermDumpBridge.TryReadForm(null);
        Assert.False(f.HasAny); Assert.Contains("未接", f.Text);
        var b = SmallBase();
        var p = b.NewCandidate(WorkLinePicker.FromGeometry(WestLine(), 1, "W")!, "W", 8, RampProfileKind.Linear);
        LongTermScheduler.Schedule(p, MakeModel(), f);
        Assert.NotNull(p.Result);
        Assert.Contains("未接", p.Result!.DumpSourceText);
        Assert.Equal("—", p.Result.DumpFullYearLabel);
        Assert.All(p.Periods, z => Assert.Equal(0, z.DumpOverflowWanM3));
    }

    [Fact]
    public void 转旧口径_期与指标逐项对应()
    {
        var b = SmallBase();
        var p = b.NewCandidate(WorkLinePicker.FromGeometry(WestLine(), 1, "W")!, "W", 8, RampProfileKind.Linear);
        LongTermScheduler.Schedule(p, MakeModel(), null);
        var l = LongTermScheduler.ToLegacy(p);
        Assert.Equal(p.Name, l.Name);
        Assert.Equal(p.Periods.Count, l.Periods.Count);
        for (int i = 0; i < p.Periods.Count; i++)
        {
            Assert.Equal(p.Periods[i].CoalWanT, l.Periods[i].CoalWanT, 9);
            Assert.Equal(p.Periods[i].StripWanM3, l.Periods[i].StripWanM3, 9);
            Assert.Equal((int)p.Periods[i].Phase, (int)l.Periods[i].Phase);
        }
        Assert.NotNull(l.Result);
        Assert.Equal(p.Result!.Npv, l.Result!.Npv, 9);
        Assert.Equal(p.Result.ServiceLifeYears, l.Result.ServiceLifeYears, 9);
        Assert.Equal(90, l.AdvanceAzimuthDeg, 6);
    }

    [Fact]
    public void 报表_含指标段与逐年表()
    {
        var b = SmallBase();
        var p = b.NewCandidate(WorkLinePicker.FromGeometry(WestLine(), 1, "W")!, "W", 8, RampProfileKind.Linear);
        LongTermScheduler.Schedule(p, MakeModel(), null);
        string csv = Views.Plan.LongTermSolveWindow.BuildReport(p);
        Assert.Contains("=== 系统指标 ===", csv);
        Assert.Contains("=== 逐年采掘进度表 ===", csv);
        Assert.Contains("2027,0,", csv);
        Assert.Equal(p.Periods.Count, csv.Split('\n').Count(l => l.StartsWith("20") && l.Contains(',')));
        var empty = b.NewCandidate(WorkLinePicker.FromGeometry(WestLine(), 1, "W")!, "E", 8, RampProfileKind.Linear);
        Assert.Contains("（未排产）", Views.Plan.LongTermSolveWindow.BuildReport(empty));
    }
}
