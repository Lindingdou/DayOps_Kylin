using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Plan;
using BenchParameterExtractor = PitMine3D.Kylin.Cad.BenchParameterExtractor;
using Xunit;
using CalendarScenario = PitMine3D.Kylin.Cad.Plan.CalendarScenario;
using DispatchStrategy = PitMine3D.Kylin.Cad.Plan.DispatchStrategy;
using MonthPeriod = PitMine3D.Kylin.Cad.Plan.MonthPeriod;
using ShortTermComparer = PitMine3D.Kylin.Cad.Plan.ShortTermComparer;
using ShortTermPlan = PitMine3D.Kylin.Cad.Plan.ShortTermPlan;
using ShortTermScheduler = PitMine3D.Kylin.Cad.Plan.ShortTermScheduler;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 短期(月度)生产计划编制家族（原 PlanLib.ShortTerm 核心）：物料口径 · 物料流拆分与去向分配 · 月度排产（划月/工作历/组织形态/削峰/流/指标）·
/// 派生笛卡尔积 · 联合评分 · 逐月配置表（派生/覆盖保住/对账/CSV 往返）· 确定入库（无库时只写确定簿）· 期次换算。纯托管，无库。
/// </summary>
[Collection("ShortTermStatics")]
public class ShortTermFamilyTests
{
    private static ShortTermBase SmallBase() => new()
    {
        PlanYear = 2027, StartMonth = 1, MonthCount = 12, AnnualCoalTargetWanT = 1200, AnnualStripTargetWanM3 = 6000,
        MonthlyCoalCeilingWanT = 0, MonthlyStripCeilingWanM3 = 0, RatioCeiling = 12,
    };

    [Fact]
    public void 物料口径_密度膨胀去向兼容_混采构成解析()
    {
        var coal = PlanMaterialCatalog.Resolve(PlanMaterialCatalog.Coal);
        Assert.True(coal.IsOre); Assert.Equal(1.35, coal.InSituDensityTPerM3, 6);
        Assert.Equal(13.5, coal.ToTonnage(10), 6);
        Assert.False(coal.Accepts(PlanSinkKind.ExternalDump)); Assert.True(coal.Accepts(PlanSinkKind.Crusher));
        var topsoil = PlanMaterialCatalog.Resolve(PlanMaterialCatalog.Topsoil);
        Assert.True(topsoil.Accepts(PlanSinkKind.TopsoilYard)); Assert.False(topsoil.Accepts(PlanSinkKind.InternalDump));
        Assert.Equal("硬岩", PlanMaterialCatalog.NameOf("nonsense"));   // 未知码回落硬岩（保守按剥离）
        Assert.Equal(PlanMaterialCatalog.Coal, PlanMaterialCatalog.CodeFromText("c4"));
        Assert.Equal(PlanMaterialCatalog.Rock, PlanMaterialCatalog.CodeFromText("rh"));

        var mix = PlanMaterialMix.Parse("煤7∶岩3");
        Assert.Equal(2, mix.Shares.Count);
        Assert.Equal(0.7, mix.OreFraction, 6);
        Assert.Single(mix.WasteShares()); Assert.Equal(1.0, mix.WasteShares()[0].Fraction, 6);
        Assert.True(PlanMaterialMix.Parse("").IsEmpty);
        Assert.Equal(PlanSinkKind.InternalDump, PlanSinkKinds.FromText("内排土场"));
        Assert.Equal(0.85, PlanSinkKind.InternalDump.EquivFactor(), 6);
    }

    [Fact]
    public void 物料流_按面份额拆供给_吨量与实方守恒_贪心配去向扣库容()
    {
        var p = SmallBase().NewCandidate(DispatchStrategy.Balanced, CalendarScenario.Standard, "t");
        var sup = PlanFlowAllocator.BuildSupplies(p, 100, 500);   // 100 万t 煤、500 万m³ 剥离
        double coalT = sup.Where(s => PlanMaterialCatalog.Resolve(s.MaterialCode).IsOre).Sum(s => PlanMaterialCatalog.Resolve(s.MaterialCode).ToTonnage(s.InSituWanM3));
        double waste = sup.Where(s => !PlanMaterialCatalog.Resolve(s.MaterialCode).IsOre).Sum(s => s.InSituWanM3);
        Assert.Equal(100, coalT, 3);
        Assert.Equal(500, waste, 3);
        Assert.Contains(sup, s => s.MaterialCode == PlanMaterialCatalog.Topsoil);   // 默认剥离岩性构成拆出表土

        var dests = new[]
        {
            new PlanDestination { Id = "D-IN", Name = "内排", Kind = PlanSinkKind.InternalDump, DesignCapacityWanM3 = 100, FallbackHaulKm = 1.4 },
            new PlanDestination { Id = "D-EX", Name = "外排", Kind = PlanSinkKind.ExternalDump, DesignCapacityWanM3 = 100000, FallbackHaulKm = 3.2 },
            new PlanDestination { Id = "TS", Name = "表土场", Kind = PlanSinkKind.TopsoilYard, DesignCapacityWanM3 = 1000, FallbackHaulKm = 2.0 },
            new PlanDestination { Id = "CR", Name = "破碎站", Kind = PlanSinkKind.Crusher, FallbackHaulKm = 2.6 },
        };
        var ledger = new PlanDumpLedger(dests);
        var warns = new List<string>();
        var flows = PlanFlowAllocator.Assign(sup, ledger, 2027, 1, "2027-01", warns);
        Assert.All(flows, f => Assert.True(f.HasDestination));
        Assert.All(flows.Where(f => f.MaterialCode == PlanMaterialCatalog.Topsoil), f => Assert.Equal("TS", f.DestinationId));
        Assert.All(flows.Where(f => f.IsOre), f => Assert.Equal(PlanSinkKind.Crusher, f.DestinationKind));
        // 内排先填满（100 万m³ 占容 ÷ Kr），余量转外排
        double innerFilled = ledger.Find("D-IN")!.FilledWanM3;
        Assert.InRange(innerFilled, 99.9, 100.01);
        Assert.Contains(flows, f => f.DestinationId == "D-EX");
        Assert.Contains(warns, w => w.Contains("剩余库容只吃得下"));
        Assert.Equal(waste, flows.Where(f => !f.IsOre).Sum(f => f.InSituWanM3), 1);
        // 没有兼容去向：量不丢 + 警示
        var none = new PlanDumpLedger(Array.Empty<PlanDestination>());
        var w2 = new List<string>();
        var f2 = PlanFlowAllocator.Assign(sup, none, 2027, 1, "2027-01", w2);
        Assert.Equal(sup.Count, f2.Count); Assert.All(f2, f => Assert.False(f.HasDestination)); Assert.NotEmpty(w2);
    }

    [Fact]
    public void 月度排产_12月守恒_检修月峰值月_指标与三量三态()
    {
        var b = SmallBase();
        var p = b.NewCandidate(DispatchStrategy.Balanced, CalendarScenario.Standard, "均衡·标准");
        ShortTermScheduler.Schedule(p, MonthlyTargetTable.NoOverride());
        Assert.Equal(12, p.Months.Count);
        Assert.NotNull(p.Result);
        var r = p.Result!;
        Assert.InRange(r.CompletionRatePct, 99, 101);                 // 年目标守恒
        Assert.Equal(1200, p.Months.Sum(m => m.CoalWanT), 1.0);   // 逐月四舍五入到 0.1
        Assert.Equal("2027-01", p.Months[0].Label);
        Assert.True(p.Months.Single(m => m.Month == 7).IsMaintenance);   // 默认检修月 7
        Assert.Contains(p.Months, m => m.IsPeak);   // 均衡型多月并列最大 → 并列都标峰值（原版同）
        Assert.Equal(p.Months.Max(m => m.CoalWanT), r.PeakMonthCoalWanT, 0.06);
        Assert.Equal(100, p.Months[^1].CompletionPct, 0.5);
        Assert.Equal(PrepCheckState.NotEntered, r.PrepState);          // 备采没录 ≠ 不达标
        Assert.Equal("通过（三量未校核）", r.OkText);
        Assert.Contains(r.Warnings, w => w.Contains("三量保有未校核"));
        // 均衡型：月产量变异系数小；剥采比不超上限
        Assert.True(r.OutputCv < 0.25, $"cv={r.OutputCv}");
        Assert.All(p.Months, m => Assert.True(m.Ratio <= 12 + 1e-6));
        // 三量录了但不够 → Fail；够 → Pass
        b.Mineable.PreparedReserveWanT = 50;   // 月均 100 → 0.5 月 < 2
        var p2 = b.NewCandidate(DispatchStrategy.Balanced, CalendarScenario.Standard, "x"); ShortTermScheduler.Schedule(p2, MonthlyTargetTable.NoOverride());
        Assert.Equal(PrepCheckState.Fail, p2.Result!.PrepState); Assert.False(p2.Result.Ok);
        b.Mineable.PreparedReserveWanT = 500;
        var p3 = b.NewCandidate(DispatchStrategy.Balanced, CalendarScenario.Standard, "y"); ShortTermScheduler.Schedule(p3, MonthlyTargetTable.NoOverride());
        Assert.Equal(PrepCheckState.Pass, p3.Result!.PrepState); Assert.Equal("通过", p3.Result.OkText);
    }

    [Fact]
    public void 工作历与组织形态_产能闸关着只报不改_开了才削_集中强采峰值更高()
    {
        var b = SmallBase();
        b.Field.EquipmentCount = 2; b.Field.EquipMonthlyCapacityWanM3 = 28;   // 能力远小于需求
        var off = b.NewCandidate(DispatchStrategy.Balanced, CalendarScenario.Push, "off"); ShortTermScheduler.Schedule(off, MonthlyTargetTable.NoOverride());
        Assert.Equal(1200, off.Months.Sum(m => m.CoalWanT), 1.0);
        Assert.Contains(off.Result!.Warnings, w => w.Contains("产能上限闸没开"));
        b.EnforceCapacityCeiling = true;
        var on = b.NewCandidate(DispatchStrategy.Balanced, CalendarScenario.Push, "on"); ShortTermScheduler.Schedule(on, MonthlyTargetTable.NoOverride());
        Assert.True(on.Months.Sum(m => m.CoalWanT) < 1200 - 1, "开闸后应被削减且不回摊");
        Assert.Contains(on.Result!.Warnings, w => w.Contains("产能上限**卡住"));
        b.EnforceCapacityCeiling = false; b.Field.EquipmentCount = 60;
        var bal = b.NewCandidate(DispatchStrategy.Balanced, CalendarScenario.Standard, "bal"); ShortTermScheduler.Schedule(bal, MonthlyTargetTable.NoOverride());
        var con = b.NewCandidate(DispatchStrategy.Concentrated, CalendarScenario.Standard, "con"); ShortTermScheduler.Schedule(con, MonthlyTargetTable.NoOverride());
        Assert.True(con.Result!.PeakMonthCoalWanT > bal.Result!.PeakMonthCoalWanT);
        Assert.True(con.Result.OutputCv > bal.Result.OutputCv);
        Assert.Equal("集中强采型", con.DispatchText); Assert.Equal("抢产", on.CalendarText);
    }

    [Fact]
    public void 派生_组织x工作历笛卡尔积_评分推荐()
    {
        var b = SmallBase();
        var list = ShortTermScheduler.Generate(b, ShortTermScheduler.DefaultDispatches(), ShortTermScheduler.DefaultCalendars(), schedule: false);
        Assert.Equal(9, list.Count);
        Assert.Contains(list, p => p.Name == "均衡型·标准" && p.Dispatch == DispatchStrategy.Balanced && p.Calendar == CalendarScenario.Standard);
        Assert.Contains(list, p => p.Name == "集中强采·保守");
        foreach (var p in list) ShortTermScheduler.Schedule(p, MonthlyTargetTable.NoOverride());
        string best = ShortTermComparer.Score(list);
        Assert.Contains(list, p => p.Name == best);
        Assert.All(list, p => Assert.InRange(p.Result!.CompositeScore, 0, 100));
        Assert.Equal("—", ShortTermComparer.Score(Array.Empty<ShortTermPlan>()));
        // 单工作历时名字不带后缀
        var one = ShortTermScheduler.Generate(b, new[] { new ShortTermScheduler.DispatchSpec("均衡型", DispatchStrategy.Balanced) }, new[] { new ShortTermScheduler.CalendarSpec("标准", CalendarScenario.Standard) }, schedule: false);
        Assert.Single(one); Assert.Equal("均衡型", one[0].Name);
    }

    [Fact]
    public void 逐月配置表_派生_覆盖保住_对账不缩放_重置_CSV往返()
    {
        var b = SmallBase();
        var t = MonthlyTargetTable.BuildDefaults(b);
        Assert.Equal(12, t.Rows.Count);
        Assert.Equal("2027-01", t.Rows[0].PeriodKey);
        Assert.Equal(0, t.ManualRowCount);
        Assert.Equal(1200, t.Rows.Sum(r => r.CoalWanT), 1.0);
        Assert.All(t.Rows, r => Assert.Null(r.FleetCapWanTKm));            // 车队能力无台账来源 → null 不是 0
        Assert.True(t.Rows.Single(r => r.Month == 7).IsMaintenance);

        // 人工覆盖 3 月采出 → 行级来源变人工；重派后保住；对账如实报差额
        var m3 = t.Rows[2];
        double derived = m3.CoalWanT;
        m3.CoalWanT = derived + 50;
        Assert.True(m3.IsManual); Assert.Equal("采出", m3.OverriddenText);
        Assert.Equal(1, t.ManualRowCount);
        b.AnnualCoalTargetWanT = 1300;
        t.Rebuild(b);
        Assert.Equal(derived + 50, t.Rows[2].CoalWanT, 6);               // 覆盖保住
        Assert.True(t.Rows[2].DerivedOf(MonthlyTargetField.Coal) > derived);   // 派生值已跟新目标走
        var rec = t.Reconcile();
        Assert.Equal(1300, rec.AnnualCoalTargetWanT, 6);
        Assert.Contains("人工覆盖", rec.Report());
        Assert.True(t.MatchesBasis(b, out _));
        b.AnnualCoalTargetWanT = 1400;
        Assert.False(t.MatchesBasis(b, out string why)); Assert.Contains("年采出", why);

        // CSV 往返：覆盖标记与派生值都带走
        string csv = t.ToCsv("t");
        Assert.True(MonthlyTargetTable.TryRead(csv, out var back, out var issues), string.Join("\n", issues));
        Assert.Equal(12, back.Rows.Count);
        Assert.True(back.Rows[2].IsManual);
        Assert.Equal(derived + 50, back.Rows[2].CoalWanT, 3);
        Assert.Equal(1300, back.AnnualCoalTargetWanT, 6);
        // 重置为派生值：唯一冲掉人工值的动作
        Assert.Equal(MonthlyTargetField.Coal, t.Rows[2].ResetToDerived());
        Assert.False(t.Rows[2].IsManual);
        Assert.Equal(0, t.ManualRowCount);
        // 空表 / 剥离能力 0 的口径提示
        Assert.False(MonthlyTargetTable.TryRead("", out _, out var i2)); Assert.NotEmpty(i2);
        t.Rows[0].StripCapWanM3 = 0;
        Assert.Contains(t.Reconcile().Issues, s => s.Contains("0 = 该月不能剥"));
        Assert.Equal(0, MonthlyTargetTable.StripCapM3ForUnitEngine(t.Rows[0], out var note)); Assert.NotNull(note);
        Assert.Equal(-1.0, MonthlyTargetTable.NoOverride().StripCapM3Array(out bool allMissing).DefaultIfEmpty(-1.0).First()); Assert.True(allMissing);
    }

    [Fact]
    public void 排产吃逐月表覆盖_覆盖月按人填_不回摊()
    {
        var b = SmallBase();
        var t = MonthlyTargetTable.BuildDefaults(b);
        t.Rows[0].CoalWanT = 30;    // 1 月人工压到 30
        var p = b.NewCandidate(DispatchStrategy.Balanced, CalendarScenario.Standard, "ov");
        ShortTermScheduler.Schedule(p, t);
        Assert.Equal(30, p.Months[0].CoalWanT, 0.06);
        Assert.Contains(p.Result!.Warnings, w => w.Contains("逐月配置表覆盖 2027-01"));
        Assert.True(p.Months.Sum(m => m.CoalWanT) < 1200 - 1, "差额进完成率，不回摊");
    }

    [Fact]
    public void 确定入库_无库只写确定簿_阻断项拒绝_下游就绪自检()
    {
        ShortTermSchemeStore.Schemes.Clear(); ShortTermSchemeStore.Confirmed = null;
        var p = SmallBase().NewCandidate(DispatchStrategy.Balanced, CalendarScenario.Standard, "c");
        ShortTermScheduler.Schedule(p, MonthlyTargetTable.NoOverride());
        var o = ShortTermConfirmService.Confirm(p);
        Assert.True(o.Ok);
        Assert.Same(p, ShortTermSchemeStore.Confirmed);
        Assert.Contains(p, ShortTermSchemeStore.Schemes);
        Assert.Equal("★已确定", p.Note);
        Assert.Equal(0, o.LedgerMonths); Assert.Contains("写库失败", o.LedgerErr);   // 无 GeoDataBase
        Assert.Contains("没有写进月度计划台账", o.Message);
        Assert.Contains("下游就绪", o.Readiness);
        Assert.Contains("放坡参数 ◆ 缺", o.Readiness);
        var bad = ShortTermConfirmService.Confirm(p, new[] { "库容不够" });
        Assert.False(bad.Ok); Assert.Contains("阻断性问题", bad.Err);
        Assert.False(ShortTermConfirmService.Confirm(null).Ok);
        ShortTermSchemeStore.Schemes.Remove(p);
        Assert.True(ShortTermConfirmService.ClearIfDropped()); Assert.Null(ShortTermSchemeStore.Confirmed);
    }

    [Fact]
    public void 期次换算_月序滚年_标签退路()
    {
        Assert.True(PlanPeriodKeys.TrySplitYearMonth(2027, 13, null, out int y, out int m)); Assert.Equal(2028, y); Assert.Equal(1, m);
        Assert.True(PlanPeriodKeys.TrySplitYearMonth(2027, 0, "2027年3月", out y, out m)); Assert.Equal(2027, y); Assert.Equal(3, m);
        Assert.True(PlanPeriodKeys.TrySplitYearMonth(2027, 0, "M14", out y, out m)); Assert.Equal(2028, y); Assert.Equal(2, m);
        Assert.False(PlanPeriodKeys.TrySplitYearMonth(2027, 0, "x", out _, out _));
        Assert.False(PlanPeriodKeys.TrySplitYearMonth(0, 1, null, out _, out _));
    }

    [Fact]
    public void 主体案例_缺省自洽_设备台数按盘子反推()
    {
        Assert.Equal(2000, PlanCase.AnnualCoalWanT, 6);
        Assert.True(PlanCase.EquipmentCount >= 25 && PlanCase.EquipmentCount <= 35);
        var b = PlanCase.NewBase();
        Assert.Equal(PlanCase.PlanYear, b.PlanYear); Assert.Equal(PlanCase.EquipmentCount, b.Field.EquipmentCount);
        Assert.Contains("主体案例", PlanCase.Caption);
        var probe = b.NewCandidate(DispatchStrategy.Balanced, CalendarScenario.Standard, "案例");
        ShortTermScheduler.Schedule(probe, MonthlyTargetTable.NoOverride());
        Assert.DoesNotContain(probe.Result!.Warnings, w => w.Contains("产能上限闸没开"));   // 缺省机队够用
    }
}

/// <summary>采场参数识别：校核器（规范默认兜底 / 本地判定 / 稳定性下界 / 回写记录无库为空）。</summary>
public class ParameterVerifierTests
{
    private static BenchParameterExtractor.Result Measure(double h, double a, double w)
    {
        var lines = new List<BenchParameterExtractor.Line>();
        double run = h / Math.Tan(a * Math.PI / 180);
        for (int k = 0; k < 3; k++)
        {
            double top = 300 - k * (run + w);
            var crest = new double[24 * 3]; var toe = new double[24 * 3];
            for (int i = 0; i < 24; i++)
            {
                double t = 2 * Math.PI * i / 24;
                crest[i * 3] = top * Math.Cos(t); crest[i * 3 + 1] = top * Math.Sin(t); crest[i * 3 + 2] = 100 - k * h;
                toe[i * 3] = (top - run) * Math.Cos(t); toe[i * 3 + 1] = (top - run) * Math.Sin(t); toe[i * 3 + 2] = 100 - (k + 1) * h;
            }
            lines.Add(BenchParameterExtractor.Line.From(crest, true)); lines.Add(BenchParameterExtractor.Line.From(toe, false));
        }
        return BenchParameterExtractor.Extract(lines);
    }

    [Fact]
    public void 校核_规范默认兜底_本地判定_稳定性下界_回写无库为空()
    {
        var m = Measure(12, 70, 4);
        Assert.True(m.Ok, m.Message);
        Assert.Equal(12, m.BenchHeight, 0.6);
        var rep = ParameterVerifier.Verify(m, isDump: false, frictionAngleDeg: 35);
        Assert.Equal(4, rep.Rows.Count);
        Assert.Contains("规范默认", rep.DesignProvenance);
        Assert.Equal("台阶高", rep.Rows[0].Name);
        Assert.Contains(rep.Rows, r => r.Code == "safety_platform_width");
        Assert.All(rep.Rows, r => Assert.True(r.Status is "pass" or "warning" or "fail" or "pending"));
        Assert.NotNull(rep.StabilityF);
        Assert.Contains(rep.Notes, n => n.Contains("稳定性 F="));
        Assert.Contains(rep.OverallStatus, new[] { "pass", "warning", "fail" });
        // 排土场基准不同（提供来源文案）
        var dump = ParameterVerifier.Verify(m, isDump: true);
        Assert.Contains("排土场", dump.DesignProvenance);
        // 无 GeoDataBase：无参数定义 → 回写记录为空、来源标"本地兜底·无规范"
        Assert.Empty(ParameterVerifier.ToAcceptanceRecords(rep, "现状面", DateTime.Now));
        Assert.All(rep.Rows, r => Assert.Contains("本地兜底", r.Source));
        // 偏差 >15% 判偏差；一致判合格
        var design = ParameterVerifier.ResolveDesign(false, null, null);
        var exact = Measure(design.H, design.A, design.W);
        var rep2 = ParameterVerifier.Verify(exact, false);
        Assert.Equal("pass", rep2.Rows[0].Status);
    }
}
