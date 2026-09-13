using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  中长远进度计划编制 —— 核心数据契约（逐字移植原 PlanLib.LongTerm.LongTermPlan）
//   · 时间维的「薄装配层」——把上游空间结果(开采程序/分期境界)按 达产爬坡 + 均衡 + 能力约束
//     摊到日历，产出逐期采掘进度表 + 推进序列 + 逐年现金流；复用「剥采比均衡」VP 曲线削峰。
//   · Base = 不随方案变的给定条件；方案一律由「派生计划方案」在 Base 上按【人为指定的工作线】生成。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>达产爬坡曲线类型。</summary>
public enum RampProfileKind { Linear, Stepped, Aggressive }

/// <summary>生产时相。</summary>
public enum PlanPhase { Basic, RampUp, Stable, Decline }

/// <summary>达产节奏档 → 投产年达产率 (%) 的预填值（原先写死在排产 switch 里的 0.35/0.45/0.70）。</summary>
public static class RampPreset
{
    public static double FirstYearPct(RampProfileKind kind) => kind switch
    {
        RampProfileKind.Aggressive => 70,
        RampProfileKind.Stepped => 45,
        _ => 35,
    };
}

/// <summary>方案综合对比权重（归一，联合对比加权评分用）。</summary>
public sealed class DecisionWeights
{
    public double StablePlateau { get; set; } = 0.18;
    public double PeakShaving { get; set; } = 0.22;
    public double EarlyCapacity { get; set; } = 0.15;
    public double InnerDumpRate { get; set; } = 0.15;
    public double Npv { get; set; } = 0.18;
    public double ReserveBalance { get; set; } = 0.12;

    public double Sum => StablePlateau + PeakShaving + EarlyCapacity + InnerDumpRate + Npv + ReserveBalance;
    public static DecisionWeights CreateDefault() => new();
    public DecisionWeights Copy() => new()
    {
        StablePlateau = StablePlateau, PeakShaving = PeakShaving, EarlyCapacity = EarlyCapacity,
        InnerDumpRate = InnerDumpRate, Npv = Npv, ReserveBalance = ReserveBalance
    };
}

/// <summary>
/// 一条【人为指定的工作线】（一条 = 一套方案的推进决策）。【LT1】一律从图上的工作线实体量取，
/// 不再有代码里的候选档。<see cref="SourceHandle"/> 存源实体，排产/确认时按 handle 重读（LT5）。
/// </summary>
public sealed class WorkLineAdvanceVariant
{
    public string Label { get; set; } = "工作线1";
    public double WorkLineLenM { get; set; } = 1000;
    public double AdvanceAzimuthDeg { get; set; }        // 0=正北/+Y，90=正东/+X
    public AdvanceMode AdvanceMode { get; set; } = AdvanceMode.Parallel;

    /// <summary>源工作线实体 handle。0 = 没接实体。</summary>
    public long SourceHandle { get; set; }
    public bool FromEntity => SourceHandle != 0;

    public bool HasPivot { get; set; }
    public double PivotX { get; set; }
    public double PivotY { get; set; }

    /// <summary>基线点（实体基线，XY 用于沿线切片与出图）。</summary>
    public List<(double X, double Y, double Z)> Baseline { get; } = new();
    /// <summary>逐段推进方向（锚点=段中点 + 单位方向 XY）——排产沿工作线在块体里分箱靠它（LT8）。</summary>
    public List<(double Ax, double Ay, double Az, double Dx, double Dy)> Samples { get; } = new();
    public bool Closed { get; set; }

    public string AdvanceModeText => AdvanceMode switch
    {
        AdvanceMode.Parallel => "平行推进", AdvanceMode.FixedPivot => "定点回转", AdvanceMode.MovingPivot => "动点回转", _ => "—"
    };
    public string Caption => $"L={WorkLineLenM:0}m · {AdvanceAzimuthDeg:0}° · {AdvanceModeText}";
    public string SourceText => FromEntity ? $"图上工作线 #{SourceHandle:X}" : "未接实体";
    public string Display => $"{Label} · {Caption} · {SourceText}";

    public WorkLineAdvanceVariant Copy()
    {
        var c = new WorkLineAdvanceVariant
        {
            Label = Label, WorkLineLenM = WorkLineLenM, AdvanceAzimuthDeg = AdvanceAzimuthDeg,
            AdvanceMode = AdvanceMode, SourceHandle = SourceHandle,
            HasPivot = HasPivot, PivotX = PivotX, PivotY = PivotY, Closed = Closed,
        };
        c.Baseline.AddRange(Baseline);
        c.Samples.AddRange(Samples);
        return c;
    }
}

/// <summary>进度计划「一期」（一年或一计划期）——排产产物。</summary>
public sealed class PlanPeriod
{
    public string Label { get; set; } = "";
    public double CoalWanT { get; set; }
    public double StripWanM3 { get; set; }
    public double Ratio { get; set; }
    public double CumCoal { get; set; }
    public double CumStrip { get; set; }
    public double CapacityPct { get; set; }
    public PlanPhase Phase { get; set; }
    public bool IsDesignCalcYear { get; set; }
    public double AdvanceM { get; set; }
    public double AdvanceRateMpa { get; set; }
    public DumpMode Dump { get; set; } = DumpMode.External;
    public double CashFlowWan { get; set; }
    public double NpvWan { get; set; }

    // ── 均衡（由真实 VP 曲线解出）──
    public int StageNo { get; set; }
    public double StageRatio { get; set; }
    public double LeadStripWanM3 { get; set; }

    // ── 排土侧（LT6；没接排土形态时全为 0/空）──
    public double InnerDumpWanM3 { get; set; }
    public double OuterDumpWanM3 { get; set; }
    public double DumpAdvanceM { get; set; }
    public string DumpCellText { get; set; } = "";
    public double DumpOverflowWanM3 { get; set; }

    public string PhaseText => Phase switch
    {
        PlanPhase.Basic => "基建", PlanPhase.RampUp => "爬坡", PlanPhase.Stable => "稳产", PlanPhase.Decline => "减产", _ => "—"
    };
    public string DumpText => Dump == DumpMode.Internal ? "内排" : "外排";
    public string FlagText => IsDesignCalcYear ? "◆达产" : "";
    // 表格显示列
    public string CoalText => CoalWanT.ToString("N0");
    public string StripText => StripWanM3.ToString("N0");
    public string RatioText => Ratio.ToString("F2");
    public string CumCoalText => CumCoal.ToString("N0");
    public string CumStripText => CumStrip.ToString("N0");
    public string CapText => CapacityPct.ToString("F0");
    public string AdvText => AdvanceRateMpa.ToString("F0");
    public string StageRatioText => StageRatio.ToString("F2");
    public string LeadText => LeadStripWanM3.ToString("N0");
    public string InnerText => InnerDumpWanM3.ToString("N0");
    public string OuterText => OuterDumpWanM3.ToString("N0");
    public string DumpAdvText => DumpAdvanceM.ToString("F1");
    public string OverflowText => DumpOverflowWanM3.ToString("N0");
    public string CashText => CashFlowWan.ToString("N0");
    public string NpvText => NpvWan.ToString("N0");
}

/// <summary>进度计划求解结果（系统级指标，对标 ProgramResult，方向感知）。</summary>
public sealed class LongTermResult
{
    public double ServiceLifeYears { get; set; }
    public double TimeToCapacityYears { get; set; }
    public string DesignCalcYearLabel { get; set; } = "—";
    public double StablePlateauYears { get; set; }
    public double ProductionRatioPeak { get; set; }
    public double BasicStrippingYiM3 { get; set; }
    public double InnerDumpPct { get; set; }
    public double AvgHaulKm { get; set; }
    public double Npv { get; set; }
    public double PaybackYears { get; set; }
    public double OutputCv { get; set; }
    public double RatioCv { get; set; }
    public double ReserveBalanceCoef { get; set; }
    public double CompositeScore { get; set; }
    public bool Ok { get; set; }
    public string OkText => Ok ? "通过" : "待校核";

    public string QuantitySourceText { get; set; } = "";
    public int BalanceStages { get; set; }
    public double PeakLeadStripWanM3 { get; set; }

    public string DumpSourceText { get; set; } = "排土形态未接（内排率按内排起转年估算，不是几何反算）";
    public string DumpFullYearLabel { get; set; } = "—";
    public double DumpOverflowWanM3 { get; set; }
}

/// <summary>「中长远进度计划方案」—— 一组完整的排产输入 + 工作线·推进变体 + 求解产物。</summary>
public sealed class LongTermPlan
{
    public const double DefaultCoalDensity = MiningProgramPlan.DefaultCoalDensity;

    public string Name { get; set; } = "进度计划1";
    public string Note { get; set; } = "";
    public bool Participate { get; set; } = true;

    // ① 来源
    public string SourceProgramName { get; set; } = "";
    public MiningProgramPlan? SourceMiningProgram { get; set; }
    public PitResult? SourcePitResult { get; set; }
    public double CoalReserveWanT { get; set; } = 30000;
    public double BaseStripRatio { get; set; } = 6.5;

    // ② 时间骨架
    public int StartYear { get; set; } = 2027;
    public int HorizonYears { get; set; } = 15;
    public int PeriodLenYears { get; set; } = 1;
    public double DesignCapacityWanTa { get; set; } = 1000;
    public double TargetServiceLifeYears { get; set; } = 30;

    // ③ 达产爬坡
    public RampProfileKind RampProfile { get; set; } = RampProfileKind.Linear;
    public int BasicStrippingYears { get; set; } = 2;
    public int RampUpYears { get; set; } = 3;
    public int DeclineYears { get; set; } = 3;

    // ⑤ 经济
    public double CoalPriceYuanT { get; set; } = 320;
    public double MiningCostYuanT { get; set; } = 95;
    public double StripCostYuanM3 { get; set; } = 28;
    public double DiscountRatePct { get; set; } = 8;

    // ⑥ 内排
    public bool InnerDumpEnabled { get; set; } = true;
    public int InnerDumpStartYear { get; set; } = 5;

    /// <summary>均衡期数 K —— 均衡里唯一要人拿主意的指标。null = 自动建议。</summary>
    public int? BalanceStageCount { get; set; }
    /// <summary>投产年达产率 (%)。</summary>
    public double RampFirstYearPct { get; set; } = 35;
    /// <summary>排产说明 / 失败原因。空 = 没排过。</summary>
    public string ScheduleNote { get; set; } = "";

    // ⑦ 约束
    public double EconomicStripRatioMax { get; set; } = 12;
    public double BenchHeightM { get; set; } = 12;
    public double MaxAdvanceRateMpa { get; set; } = 360;
    public double MinAdvanceRateMpa { get; set; } = 80;

    // ⑧ 比选权重
    public DecisionWeights Decision { get; set; } = DecisionWeights.CreateDefault();

    public WorkLineAdvanceVariant WorkLine { get; set; } = new();

    public ObservableCollection<PlanPeriod> Periods { get; set; } = new();
    public LongTermResult? Result { get; set; }

    public double AdvanceRateFrom(double capacityWanTa)
        => MiningProgramPlan.AdvanceRateFrom(capacityWanTa, WorkLine.WorkLineLenM, BenchHeightM, DefaultCoalDensity);

    public string RampText => RampProfile switch
    {
        RampProfileKind.Linear => "线性爬坡", RampProfileKind.Stepped => "阶梯爬坡", RampProfileKind.Aggressive => "尽快达产", _ => "—"
    };
    public string SolvedText => Result == null ? "未求解" : "已求解";
    public string SourceText => string.IsNullOrEmpty(SourceProgramName) ? "（样例储量）" : SourceProgramName;
    // 表格显示列
    public string WorkLineCaption => WorkLine.Caption;
    public string CapText => DesignCapacityWanTa.ToString("F0");
    public string RLife => Result?.ServiceLifeYears.ToString("F0") ?? "";
    public string RTtc => Result?.TimeToCapacityYears.ToString("F0") ?? "";
    public string RPlateau => Result?.StablePlateauYears.ToString("F0") ?? "";
    public string RPeak => Result?.ProductionRatioPeak.ToString("F1") ?? "";
    public string RInner => Result?.InnerDumpPct.ToString("F0") ?? "";
    public string RFullYear => Result?.DumpFullYearLabel ?? "";
    public string ROverflow => Result?.DumpOverflowWanM3.ToString("N0") ?? "";
    public string RNpv => Result?.Npv.ToString("N0") ?? "";
    public string RBalance => Result?.ReserveBalanceCoef.ToString("F2") ?? "";
    public string RScore => Result?.CompositeScore.ToString("F0") ?? "";

    /// <summary>从上游开采程序方案继承储量/剥采比/经济/工作线。</summary>
    public void InheritFrom(MiningProgramPlan mp)
    {
        SourceProgramName = mp.Name; SourceMiningProgram = mp; SourcePitResult = mp.SourcePitResult;
        if (mp.SourcePitResult is { } r)
        {
            if (r.CoalWanT > 0) CoalReserveWanT = r.CoalWanT;
            if (r.AvgRatio > 0) BaseStripRatio = r.AvgRatio;
        }
        else if (mp.Panels.Count > 0)
        {
            double coal = mp.Panels.Sum(p => p.CoalWanT);
            double waste = mp.Panels.Sum(p => p.WasteWanM3);
            if (coal > 0) { CoalReserveWanT = coal; BaseStripRatio = waste / coal; }
        }
        CoalPriceYuanT = mp.CoalPriceYuanT; MiningCostYuanT = mp.MiningCostYuanT;
        StripCostYuanM3 = mp.StripCostYuanM3; DiscountRatePct = mp.DiscountRatePct;
        BenchHeightM = mp.BenchHeightM > 0 ? mp.BenchHeightM : BenchHeightM;
        if (mp.MaxAdvanceRateMpa > 0) MaxAdvanceRateMpa = mp.MaxAdvanceRateMpa;
        WorkLine.WorkLineLenM = mp.MinWorkingLineM > 0 ? mp.MinWorkingLineM : WorkLine.WorkLineLenM;
        if (mp.SelectedBoxcut is { } b)
        {
            WorkLine.AdvanceAzimuthDeg = b.AdvanceAzimuthDeg;
            WorkLine.AdvanceMode = b.AdvanceMode;
            if (b.WorkingLineLengthM > 0) WorkLine.WorkLineLenM = b.WorkingLineLengthM;
        }
    }

    public LongTermPlan Clone()
    {
        var c = (LongTermPlan)MemberwiseClone();
        c.Name = Name + "·副本";
        c.Decision = Decision.Copy();
        c.WorkLine = WorkLine.Copy();
        c.SourceMiningProgram = SourceMiningProgram;
        c.Periods = new ObservableCollection<PlanPeriod>();
        c.Result = null;
        return c;
    }
}

/// <summary>
/// 中长远进度计划的「基础约束信息」（基准/盘子）—— 不随候选方案变的给定条件。
/// 「中长远进度计划编制」窗口只编它一套；候选「进度计划方案」一律由「派生计划方案」在它之上变决策变量生成。
/// </summary>
public sealed class LongTermBase
{
    public string SourceProgramName { get; set; } = "";
    public MiningProgramPlan? SourceMiningProgram { get; set; }
    public PitResult? SourcePitResult { get; set; }
    public double CoalReserveWanT { get; set; } = 30000;
    public double BaseStripRatio { get; set; } = 6.5;

    public int StartYear { get; set; } = 2027;
    public int HorizonYears { get; set; } = 15;
    public int PeriodLenYears { get; set; } = 1;
    public double DesignCapacityWanTa { get; set; } = 1000;
    public double TargetServiceLifeYears { get; set; } = 30;

    public RampProfileKind RampProfile { get; set; } = RampProfileKind.Linear;
    public int BasicStrippingYears { get; set; } = 2;
    public int RampUpYears { get; set; } = 3;
    public int DeclineYears { get; set; } = 3;

    public double CoalPriceYuanT { get; set; } = 320;
    public double MiningCostYuanT { get; set; } = 95;
    public double StripCostYuanM3 { get; set; } = 28;
    public double DiscountRatePct { get; set; } = 8;

    public bool InnerDumpEnabled { get; set; } = true;
    public int InnerDumpStartYear { get; set; } = 5;

    public int? BalanceStageCount { get; set; }
    public double RampFirstYearPct { get; set; } = 35;

    public double EconomicStripRatioMax { get; set; } = 12;
    public double BenchHeightM { get; set; } = 12;
    public double MaxAdvanceRateMpa { get; set; } = 360;
    public double MinAdvanceRateMpa { get; set; } = 80;

    public DecisionWeights Decision { get; set; } = DecisionWeights.CreateDefault();

    public string SourceText => string.IsNullOrEmpty(SourceProgramName) ? "（无来源：用样例储量）" : SourceProgramName;
    public string RampText => RampProfile switch
    {
        RampProfileKind.Linear => "线性爬坡", RampProfileKind.Stepped => "阶梯爬坡", RampProfileKind.Aggressive => "尽快达产", _ => "—"
    };

    public void InheritFrom(MiningProgramPlan mp)
    {
        SourceProgramName = mp.Name; SourceMiningProgram = mp; SourcePitResult = mp.SourcePitResult;
        if (mp.SourcePitResult is { } r)
        {
            if (r.CoalWanT > 0) CoalReserveWanT = r.CoalWanT;
            if (r.AvgRatio > 0) BaseStripRatio = r.AvgRatio;
        }
        else if (mp.Panels.Count > 0)
        {
            double coal = mp.Panels.Sum(p => p.CoalWanT);
            double waste = mp.Panels.Sum(p => p.WasteWanM3);
            if (coal > 0) { CoalReserveWanT = coal; BaseStripRatio = waste / coal; }
        }
        CoalPriceYuanT = mp.CoalPriceYuanT; MiningCostYuanT = mp.MiningCostYuanT;
        StripCostYuanM3 = mp.StripCostYuanM3; DiscountRatePct = mp.DiscountRatePct;
        if (mp.BenchHeightM > 0) BenchHeightM = mp.BenchHeightM;
        if (mp.MaxAdvanceRateMpa > 0) MaxAdvanceRateMpa = mp.MaxAdvanceRateMpa;
    }

    /// <summary>在本基础约束之上 + 一个工作线·推进变体（+能力/爬坡覆盖）造一个候选进度计划方案。</summary>
    public LongTermPlan NewCandidate(WorkLineAdvanceVariant wl, string name, double capacityWanTa, RampProfileKind? ramp = null)
        => new()
        {
            Name = name,
            SourceProgramName = SourceProgramName, SourceMiningProgram = SourceMiningProgram, SourcePitResult = SourcePitResult,
            CoalReserveWanT = CoalReserveWanT, BaseStripRatio = BaseStripRatio,
            StartYear = StartYear, HorizonYears = HorizonYears, PeriodLenYears = PeriodLenYears,
            DesignCapacityWanTa = capacityWanTa, TargetServiceLifeYears = TargetServiceLifeYears,
            RampProfile = ramp ?? RampProfile, BasicStrippingYears = BasicStrippingYears,
            RampFirstYearPct = ramp.HasValue ? RampPreset.FirstYearPct(ramp.Value) : RampFirstYearPct,
            RampUpYears = RampUpYears, DeclineYears = DeclineYears,
            CoalPriceYuanT = CoalPriceYuanT, MiningCostYuanT = MiningCostYuanT,
            StripCostYuanM3 = StripCostYuanM3, DiscountRatePct = DiscountRatePct,
            InnerDumpEnabled = InnerDumpEnabled, InnerDumpStartYear = InnerDumpStartYear,
            BalanceStageCount = BalanceStageCount,
            EconomicStripRatioMax = EconomicStripRatioMax, BenchHeightM = BenchHeightM,
            MaxAdvanceRateMpa = MaxAdvanceRateMpa, MinAdvanceRateMpa = MinAdvanceRateMpa,
            Decision = Decision.Copy(),
            WorkLine = wl.Copy(),
        };
}

/// <summary>
/// 会话级共享：中长远进度计划的「基础约束」(Base，单一) + 候选「进度计划方案」集(Schemes)。
/// 五个入口共用同一实例。方案库初始为空（LT3：不造演示方案）。
/// </summary>
public static class LongTermSchemeStore
{
    private static LongTermBase? _base;
    private static ObservableCollection<LongTermPlan>? _schemes;

    public static LongTermBase Base => _base ??= new LongTermBase();
    public static ObservableCollection<LongTermPlan> Schemes => _schemes ??= new ObservableCollection<LongTermPlan>();
    /// <summary>「确定进度计划」选定的中长远主方案（供出图/下游默认选中；可为空）。</summary>
    public static LongTermPlan? Confirmed { get; set; }

    /// <summary>库里已指定的工作线（按源实体去重；空 = 三个下游窗口一律拦住）。</summary>
    public static List<WorkLineAdvanceVariant> SpecifiedWorkLines()
        => Schemes.Select(s => s.WorkLine).Where(w => w.FromEntity)
                  .GroupBy(w => w.SourceHandle).Select(g => g.First()).ToList();

    public static bool HasSpecifiedWorkLine => Schemes.Any(s => s.WorkLine.FromEntity);

    public const string NeedWorkLineHint =
        "尚未指定工作线 —— 请到「派生计划方案」：在图上选中一条工作线 → 「拾取选中工作线」→ 「生成多套方案」。"
      + "本窗口不再自动编制（代码里没有默认工作线，自动编出来的是图上不存在的线）";

    /// <summary>自动续源：基础约束尚未续上开采程序时，自动继承上游（采区划分③）的开采程序方案（优先已求解的）。</summary>
    public static bool AutoInheritIfNeeded(IReadOnlyList<MiningProgramPlan>? programs)
    {
        if (!string.IsNullOrEmpty(Base.SourceProgramName)) return false;
        if (programs is not { Count: > 0 }) return false;
        var mp = programs.FirstOrDefault(p => p.SourcePitResult != null) ?? programs[0];
        Base.InheritFrom(mp);
        return true;
    }

    public static void Reset() { _base = null; _schemes = null; Confirmed = null; }
}
