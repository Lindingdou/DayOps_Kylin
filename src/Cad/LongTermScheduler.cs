using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

// ─────────────────────────────────────────────────────────────────────────────
//  中长远进度计划「规划计算」内核 —— 忠实移植原 PlanLib.LongTerm.LongTermScheduler(量版合成排产, Phase 1):
//  划期 → 达产爬坡分配各期采出量 → 超前剥离反推剥离量(合成剖面削峰) → 逐年现金流/NPV → Periods + Result。
//  纯量算、不依赖逐期几何(真剥采比场模式=Phase 2, 需块体采样, 记录)。可单测。
// ─────────────────────────────────────────────────────────────────────────────

public enum RampProfileKind { Linear, Stepped, Aggressive }
public enum PlanPhase { Basic, RampUp, Stable, Decline }
public enum LongTermDumpMode { External, Internal }

/// <summary>中长远进度计划输入(设计能力/储量/爬坡/成本/工作线; 默认=样例, 忠实原 LongTermPlan)。</summary>
public sealed class LongTermPlan
{
    public const double DefaultCoalDensity = 1.35;

    public double DesignCapacityWanTa = 1000;   // 设计生产能力 A_p (万t/a)
    public double CoalReserveWanT = 30000;       // 可采储量 (万t)
    public double BaseStripRatio = 6.5;          // 基准剥采比 (m³/t)
    public int BasicStrippingYears = 2;          // 基建期 (a)
    public int RampUpYears = 3;                  // 投产→达产爬坡 (a)
    public int DeclineYears = 3;                 // 末期减产 (a)
    public RampProfileKind RampProfile = RampProfileKind.Linear;
    public int StartYear = 2027;
    public double CoalPriceYuanT = 320;
    public double MiningCostYuanT = 95;
    public double StripCostYuanM3 = 28;
    public double DiscountRatePct = 8;
    public bool InnerDumpEnabled = true;
    public int InnerDumpStartYear = 5;           // 内排起转年(相对起始年偏移)
    public double EconomicStripRatioMax = 12;    // 经济合理剥采比 n_经 (m³/t)
    public double BenchHeightM = 12;
    public double WorkLineLenM = 1200;
    public AdvanceMode WorkLineMode = AdvanceMode.Parallel;
    public double AdvanceAzimuthDeg = 0;

    public string Name = "基准";
    public List<PlanPeriod> Periods { get; } = new();
    public LongTermResult? Result { get; set; }

    /// <summary>拷贝标量参数(新 Periods/Result), 供多方案派生。</summary>
    public LongTermPlan Clone() => new()
    {
        DesignCapacityWanTa = DesignCapacityWanTa, CoalReserveWanT = CoalReserveWanT, BaseStripRatio = BaseStripRatio,
        BasicStrippingYears = BasicStrippingYears, RampUpYears = RampUpYears, DeclineYears = DeclineYears, RampProfile = RampProfile,
        StartYear = StartYear, CoalPriceYuanT = CoalPriceYuanT, MiningCostYuanT = MiningCostYuanT, StripCostYuanM3 = StripCostYuanM3,
        DiscountRatePct = DiscountRatePct, InnerDumpEnabled = InnerDumpEnabled, InnerDumpStartYear = InnerDumpStartYear,
        EconomicStripRatioMax = EconomicStripRatioMax, BenchHeightM = BenchHeightM,
        WorkLineLenM = WorkLineLenM, WorkLineMode = WorkLineMode, AdvanceAzimuthDeg = AdvanceAzimuthDeg, Name = Name,
    };
}

/// <summary>一个计划期(年)行。</summary>
public sealed class PlanPeriod
{
    public string Label = "";
    public double CoalWanT, StripWanM3, Ratio, CumCoal, CumStrip, CapacityPct;
    public PlanPhase Phase;
    public double AdvanceRateMpa;
    public LongTermDumpMode Dump;
    public double CashFlowWan, NpvWan;
    public bool IsDesignCalcYear;
}

/// <summary>排产评价指标。CompositeScore 由多方案对比(LongTermComparer)回填。</summary>
public sealed record LongTermResult(double ServiceLifeYears, double TimeToCapacityYears, string DesignCalcYearLabel,
    double StablePlateauYears, double ProductionRatioPeak, double BasicStrippingYiM3, double InnerDumpPct,
    double AvgHaulKm, double Npv, double PaybackYears, double OutputCv, double RatioCv, double ReserveBalanceCoef, bool Ok)
{
    public double CompositeScore { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>方案决策权重(忠实原 DecisionWeights 默认)。</summary>
public sealed record LongTermWeights(double StablePlateau = 0.18, double PeakShaving = 0.22, double EarlyCapacity = 0.15,
    double InnerDumpRate = 0.15, double Npv = 0.18, double ReserveBalance = 0.12)
{
    public double Sum => StablePlateau + PeakShaving + EarlyCapacity + InnerDumpRate + Npv + ReserveBalance;
}

/// <summary>多方案对比 —— 忠实原 LongTermComparer.Score: 六指标 min-max 归一 × 权重 → 综合分, 荐可行最高分。</summary>
public static class LongTermComparer
{
    private static double NormHigh(double[] a, int i) { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (a[i] - mn) / (mx - mn) : 0.5; }
    private static double NormLow(double[] a, int i) { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (mx - a[i]) / (mx - mn) : 0.5; }

    /// <summary>回填各 Result.CompositeScore, 返回推荐方案(可行最高分, 无可行则总最高分)。</summary>
    public static LongTermResult? Score(IReadOnlyList<LongTermResult> results, LongTermWeights? weights = null)
    {
        var r = results.Where(x => x != null).ToList();
        if (r.Count == 0) return null;
        var w = weights ?? new LongTermWeights();
        double wsum = w.Sum <= 0 ? 1 : w.Sum;
        double[] plateau = r.Select(x => x.StablePlateauYears).ToArray();
        double[] peak = r.Select(x => x.ProductionRatioPeak).ToArray();
        double[] ttc = r.Select(x => x.TimeToCapacityYears).ToArray();
        double[] inner = r.Select(x => x.InnerDumpPct).ToArray();
        double[] npv = r.Select(x => x.Npv).ToArray();
        double[] bal = r.Select(x => x.ReserveBalanceCoef).ToArray();
        LongTermResult? best = null; double bestScore = -1;
        for (int i = 0; i < r.Count; i++)
        {
            double s = NormHigh(plateau, i) * w.StablePlateau + NormLow(peak, i) * w.PeakShaving + NormLow(ttc, i) * w.EarlyCapacity
                     + NormHigh(inner, i) * w.InnerDumpRate + NormHigh(npv, i) * w.Npv + NormHigh(bal, i) * w.ReserveBalance;
            r[i].CompositeScore = Math.Round(s / wsum * 100, 0);
            if (r[i].Ok && r[i].CompositeScore > bestScore) { bestScore = r[i].CompositeScore; best = r[i]; }
        }
        if (best == null) foreach (var x in r) if (x.CompositeScore > bestScore) { bestScore = x.CompositeScore; best = x; }
        return best;
    }
}

public static class LongTermScheduler
{
    /// <summary>产能/推进耦合正式: Q(万t/a) = 工作线长 L(m)·推进度 v(m/a)·台阶高 H(m)·煤密度 ρ(t/m³) / 1e4(忠实原 MiningProgramPlan.CapacityWanTaFrom)。</summary>
    public static double CapacityWanTaFrom(double lengthM, double rateMpa, double benchM, double rho = LongTermPlan.DefaultCoalDensity)
        => lengthM * rateMpa * benchM * rho / 1e4;

    /// <summary>推进度 (m/a) = 能力(万t/a)·1e4 / (工作线长·台阶高·煤密度)。<see cref="CapacityWanTaFrom"/> 之逆(设计反问题: 定产能反算所需推进度)。</summary>
    public static double AdvanceRateFrom(double capacityWanTa, double lengthM, double benchM, double rho = LongTermPlan.DefaultCoalDensity)
        => (lengthM <= 0 || benchM <= 0) ? 0 : capacityWanTa * 1e4 / (lengthM * benchM * rho);

    /// <summary>量版合成排产(无真剥采比场)。回填 p.Periods 与 p.Result。忠实原 Schedule 的合成分支。</summary>
    public static void Schedule(LongTermPlan p)
    {
        p.Periods.Clear();
        double rho = LongTermPlan.DefaultCoalDensity;
        double Ap = Math.Max(1, p.DesignCapacityWanTa);

        // 工作线 × 方向 → 削峰/超前 剖面因子(工作线越长越平稳→峰值越低)
        double smoothByL = Math.Clamp(p.WorkLineLenM / 1200.0, 0.65, 1.4);
        double modeFactor = p.WorkLineMode switch { AdvanceMode.FixedPivot => 0.93, AdvanceMode.MovingPivot => 0.96, _ => 1.0 };
        double azRad = p.AdvanceAzimuthDeg * Math.PI / 180.0;
        double dirFactor = 1.0 + 0.06 * Math.Cos(2 * azRad);
        double peakMul = Math.Clamp(1.45 / smoothByL * modeFactor * dirFactor, 1.05, 1.9);

        double reserve = Math.Max(Ap, p.CoalReserveWanT);
        double baseRatio = Math.Max(0.1, p.BaseStripRatio);
        double peakRatio = baseRatio * peakMul;

        int basic = Math.Max(0, p.BasicStrippingYears);
        int ramp = Math.Max(0, p.RampUpYears);
        int decline = Math.Max(0, p.DeclineYears);
        double r0 = p.RampProfile switch { RampProfileKind.Aggressive => 0.70, RampProfileKind.Stepped => 0.45, _ => 0.35 };

        // 基建期: P=0, 剥离超前(基建剥离量 = 储量×基准剥采比×基建系数)
        double basicFrac = Math.Clamp(0.10 + (1.2 - smoothByL) * 0.06, 0.06, 0.20);
        double basicStripTotal = reserve * baseRatio * basicFrac;
        double basicPerYear = basic > 0 ? basicStripTotal / basic : 0;

        double cumCoal = 0, cumStrip = 0;
        double d = p.DiscountRatePct / 100.0;
        int yi = 0, designCalcIdx = -1;
        var periods = new List<PlanPeriod>();

        for (int k = 0; k < basic; k++)
        {
            double strip = basicPerYear;
            cumStrip += strip;
            double cf = -strip * p.StripCostYuanM3;
            double disc = cf / Math.Pow(1 + d, yi);
            periods.Add(new PlanPeriod
            {
                Label = $"{p.StartYear + yi}", CoalWanT = 0, StripWanM3 = Math.Round(strip, 0), Ratio = 0,
                CumCoal = 0, CumStrip = Math.Round(cumStrip, 0), CapacityPct = 0, Phase = PlanPhase.Basic,
                AdvanceRateMpa = 0, Dump = LongTermDumpMode.External, CashFlowWan = Math.Round(cf, 0), NpvWan = Math.Round(disc, 0),
            });
            yi++;
        }

        double remaining = reserve;
        int prodIdx = 0;
        int estStableYears = (int)Math.Ceiling((reserve - Ap * (ramp * (1 + r0) / 2.0)) / Ap);
        int estProdYears = ramp + Math.Max(1, estStableYears);
        int declineStart = Math.Max(ramp, estProdYears - decline);

        while (remaining > 1e-6 && prodIdx < 200)
        {
            double capPct; PlanPhase phase;
            if (prodIdx < ramp) { double f = ramp <= 1 ? 1.0 : (double)prodIdx / ramp; capPct = r0 + (1 - r0) * f; phase = PlanPhase.RampUp; }
            else if (prodIdx >= declineStart) { int dk = prodIdx - declineStart; capPct = Math.Max(0.35, 1.0 - 0.18 * (dk + 1)); phase = PlanPhase.Decline; }
            else { capPct = 1.0; phase = PlanPhase.Stable; }

            double target = Ap * capPct;
            double coal = Math.Min(remaining, target);
            if (coal < Ap * 0.05 && prodIdx > ramp) coal = remaining;   // 收尾并入
            remaining -= coal;
            double prog = 1 - remaining / reserve;
            double ratio0 = baseRatio + (peakRatio - baseRatio) * Math.Sqrt(Math.Clamp(prog, 0, 1));
            double strip = coal * ratio0;
            double ratio = coal > 1e-6 ? strip / coal : 0;

            bool internalDump = p.InnerDumpEnabled && (p.StartYear + yi) - p.StartYear >= p.InnerDumpStartYear;
            double stripCostFactor = internalDump ? 0.85 : 1.0;
            double cf = coal * (p.CoalPriceYuanT - p.MiningCostYuanT) - strip * p.StripCostYuanM3 * stripCostFactor;
            double disc = cf / Math.Pow(1 + d, yi);
            cumCoal += coal; cumStrip += strip;

            double v = AdvanceRateFrom(coal, p.WorkLineLenM, p.BenchHeightM, rho);
            if (capPct >= 0.999 && designCalcIdx < 0) designCalcIdx = periods.Count;

            periods.Add(new PlanPeriod
            {
                Label = $"{p.StartYear + yi}", CoalWanT = Math.Round(coal, 0), StripWanM3 = Math.Round(strip, 0), Ratio = Math.Round(ratio, 2),
                CumCoal = Math.Round(cumCoal, 0), CumStrip = Math.Round(cumStrip, 0), CapacityPct = Math.Round(capPct * 100, 0), Phase = phase,
                AdvanceRateMpa = Math.Round(v, 0), Dump = internalDump ? LongTermDumpMode.Internal : LongTermDumpMode.External,
                CashFlowWan = Math.Round(cf, 0), NpvWan = Math.Round(disc, 0),
            });
            yi++; prodIdx++;
        }

        if (designCalcIdx >= 0) periods[designCalcIdx].IsDesignCalcYear = true;
        foreach (var pp in periods) p.Periods.Add(pp);
        double peak = periods.Where(z => z.CoalWanT > 0).Select(z => z.Ratio).DefaultIfEmpty(peakRatio).Max();
        p.Result = Evaluate(p, periods, peak, basicStripTotal, designCalcIdx >= 0 ? periods[designCalcIdx].Label : "—");
    }

    private static LongTermResult Evaluate(LongTermPlan p, List<PlanPeriod> periods, double peakRatio, double basicStripTotal, string designCalcLabel)
    {
        var prod = periods.Where(z => z.CoalWanT > 0).ToList();
        double serviceLife = prod.Count;
        int basic = periods.Count(z => z.Phase == PlanPhase.Basic);
        int rampN = periods.Count(z => z.Phase == PlanPhase.RampUp);
        double ttc = basic + rampN;
        double plateau = periods.Count(z => z.Phase == PlanPhase.Stable);
        double totStrip = periods.Sum(z => z.StripWanM3);
        double innerStrip = periods.Where(z => z.Dump == LongTermDumpMode.Internal).Sum(z => z.StripWanM3);
        double innerPct = totStrip > 0 ? innerStrip / totStrip * 100 : 0;
        double cum = 0, payback = serviceLife + basic;
        for (int i = 0; i < periods.Count; i++) { cum += periods[i].NpvWan; if (cum > 0) { payback = i; break; } }
        double[] coal = prod.Select(z => z.CoalWanT).ToArray();
        double[] ratioArr = prod.Select(z => z.Ratio).ToArray();
        double outCv = Cv(coal), ratCv = Cv(ratioArr);
        double balance = Math.Clamp(1 - outCv, 0, 1);
        double haul = Math.Round(2.8 - innerPct / 100.0 * 1.2, 1);
        double lifeMin = ServiceLifeMinFor(p.DesignCapacityWanTa);
        bool ok = serviceLife >= lifeMin && peakRatio <= p.EconomicStripRatioMax + 1e-6 && ttc > 0;
        return new LongTermResult(serviceLife, ttc, designCalcLabel, plateau, Math.Round(peakRatio, 1),
            Math.Round(basicStripTotal / 1e4, 2), Math.Round(innerPct, 0), haul, Math.Round(periods.Sum(z => z.NpvWan), 0),
            payback, Math.Round(outCv, 3), Math.Round(ratCv, 3), Math.Round(balance, 2), ok);
    }

    private static double Cv(double[] a)
    {
        if (a.Length == 0) return 0;
        double mean = a.Average();
        if (mean <= 1e-9) return 0;
        double var = a.Select(x => (x - mean) * (x - mean)).Average();
        return Math.Sqrt(var) / mean;
    }

    /// <summary>规范最低服务年限(按设计能力分级, GB50197 近似)。</summary>
    public static double ServiceLifeMinFor(double capWanTa) => capWanTa switch
    { >= 1000 => 30, >= 500 => 25, >= 300 => 20, >= 100 => 15, _ => 10 };

    public readonly record struct WorkLineSpec(string Label, double LenM, AdvanceMode Mode);
    public readonly record struct DirectionSpec(string Label, double AzimuthDeg);

    /// <summary>默认工作线候选(长/中/短 + 扇形回转), 忠实原 DefaultWorkLines。</summary>
    public static List<WorkLineSpec> DefaultWorkLines() => new()
    { new("长工作线", 1400, AdvanceMode.Parallel), new("中工作线", 1100, AdvanceMode.Parallel),
      new("短工作线", 900, AdvanceMode.Parallel), new("扇形回转", 1000, AdvanceMode.FixedPivot) };

    /// <summary>默认推进方向候选(四主方位), 忠实原 DefaultDirections。</summary>
    public static List<DirectionSpec> DefaultDirections() => new()
    { new("北推", 0), new("东推", 90), new("南推", 180), new("西推", 270) };

    /// <summary>工作线 × 方向 正交派生多套计划, 每套独立排产。忠实原 GenerateVariants。</summary>
    public static List<LongTermPlan> GenerateVariants(LongTermPlan basePlan,
        IReadOnlyList<WorkLineSpec> workLines, IReadOnlyList<DirectionSpec> directions)
    {
        var result = new List<LongTermPlan>();
        foreach (var wl in workLines)
            foreach (var dir in directions)
            {
                var v = basePlan.Clone();
                v.Name = $"{wl.Label}{dir.Label}";
                v.WorkLineLenM = wl.LenM; v.WorkLineMode = wl.Mode; v.AdvanceAzimuthDeg = dir.AzimuthDeg;
                Schedule(v);
                if (v.Result != null) v.Result.Name = v.Name;
                result.Add(v);
            }
        return result;
    }
}
