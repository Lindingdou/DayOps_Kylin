using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

// ─────────────────────────────────────────────────────────────────────────────
//  短期(月度)生产计划「月度计划编制」内核 —— 忠实移植原 PlanLib.ShortTerm.ShortTermScheduler:
//  划月 → 月权重(有效作业日 × 设备可用 × 作业组织形态) → 摊年采出目标 → 均衡平滑 → 月产上限裁剪回摊
//  → 剥采比月剖面反推月剥离 → 推进/设备利用/累计/完成率 → Months + Result。纯量算「年→月」均衡细化。
//  忠实取舍: 备采保有月数(Mineable.PreparedMonths)需备采储量配置, 略去该校核项(记录); 余全移。
// ─────────────────────────────────────────────────────────────────────────────

public enum DispatchStrategy { Balanced, MultiFace, Concentrated }
public enum CalendarScenario { Standard, Push, Conservative }

/// <summary>短期月度计划输入(年目标/月上限/作业历/设备/作业组织; 默认=样例, 忠实原 ShortTermPlan 展平)。</summary>
public sealed class ShortTermPlan
{
    public const double DefaultCoalDensity = 1.35;

    public int PlanYear = 2027;
    public double AnnualCoalTargetWanT = 1000;
    public double BaseRatio = 6.5;                 // 基准剥采比(原=年剥离目标/年煤目标)
    public int StartMonth = 1, MonthCount = 12;
    public double MonthlyCoalCeilingWanT = 120;    // 月采出上限(0=不限)
    public double MonthlyStripCeilingWanM3 = 800;  // 月剥离上限(0=不限)
    public double RatioCeiling = 12;               // 月生产剥采比上限(0=不限)
    public double CompletionTolerancePct = 3;
    public double BenchHeightM = 12, WorkLineLenM = 1100;
    public DispatchStrategy Dispatch = DispatchStrategy.Balanced;
    public CalendarScenario Calendar = CalendarScenario.Standard;
    // 作业历/设备(原 FieldParams)
    public double StandardWorkdays = 25;
    public int EquipmentCount = 4;
    public double EquipmentAvailabilityPct = 82, EquipMonthlyCapacityWanM3 = 28;
    public string WinterMonthsCsv = "12,1,2";
    public double WinterDeratePct = 20, MaintenanceDeratePct = 30;
    public int MaintenanceMonth = 7;
    // 均衡权重(原 StBalanceWeights): 份额 = 各 / Sum
    public double OutputSmooth = 0.45, RatioSmooth = 0.30, EquipSmooth = 0.25;
    public double BalanceSum => OutputSmooth + RatioSmooth + EquipSmooth;
    public List<string> Faces = new();             // 作业面(按次序)

    public string Name = "基准";
    public List<MonthPeriod> Months { get; } = new();
    public ShortTermResult? Result { get; set; }

    /// <summary>拷贝标量参数(新 Months/Result), 供多方案派生。</summary>
    public ShortTermPlan Clone() => new()
    {
        PlanYear = PlanYear, AnnualCoalTargetWanT = AnnualCoalTargetWanT, BaseRatio = BaseRatio, StartMonth = StartMonth, MonthCount = MonthCount,
        MonthlyCoalCeilingWanT = MonthlyCoalCeilingWanT, MonthlyStripCeilingWanM3 = MonthlyStripCeilingWanM3, RatioCeiling = RatioCeiling,
        CompletionTolerancePct = CompletionTolerancePct, BenchHeightM = BenchHeightM, WorkLineLenM = WorkLineLenM, Dispatch = Dispatch, Calendar = Calendar,
        StandardWorkdays = StandardWorkdays, EquipmentCount = EquipmentCount, EquipmentAvailabilityPct = EquipmentAvailabilityPct,
        EquipMonthlyCapacityWanM3 = EquipMonthlyCapacityWanM3, WinterMonthsCsv = WinterMonthsCsv, WinterDeratePct = WinterDeratePct,
        MaintenanceDeratePct = MaintenanceDeratePct, MaintenanceMonth = MaintenanceMonth,
        OutputSmooth = OutputSmooth, RatioSmooth = RatioSmooth, EquipSmooth = EquipSmooth, Faces = new List<string>(Faces), Name = Name,
    };

    public HashSet<int> WinterMonths()
    {
        var s = new HashSet<int>();
        foreach (var t in (WinterMonthsCsv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(t, out var m)) s.Add(m);
        return s;
    }

    /// <summary>某月有效作业日 = 标准作业日 × 冬季降效 × 检修降效 × 工作历情景。忠实原 WorkdaysFor。</summary>
    public double WorkdaysFor(int month)
    {
        double wd = StandardWorkdays;
        if (WinterMonths().Contains(month)) wd *= 1 - WinterDeratePct / 100.0;
        if (MaintenanceMonth == month) wd *= 1 - MaintenanceDeratePct / 100.0;
        wd *= Calendar switch { CalendarScenario.Push => 1.10, CalendarScenario.Conservative => 0.92, _ => 1.0 };
        return Math.Max(1, Math.Round(wd, 1));
    }
}

public sealed class MonthPeriod
{
    public string Label = ""; public int Month;
    public double Workdays, CoalWanT, StripWanM3, Ratio, CumCoal, CumStrip, AdvanceM, EquipUtilPct, CompletionPct;
    public LongTermDumpMode Dump; public string ActiveFace = ""; public bool IsMaintenance, IsPeak;
}

public sealed record ShortTermResult(double TotalCoalWanT, double TotalStripWanM3, double CompletionRatePct,
    double AvgRatio, double PeakMonthCoalWanT, string PeakMonthLabel, double OutputCv, double RatioCv,
    double AvgEquipUtilPct, double AdvanceTotalM, double BalanceCoef, bool Ok)
{
    public double CompositeScore { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>短期方案决策权重(忠实原默认)。</summary>
public sealed record ShortTermWeights(double Completion = 0.28, double OutputBalance = 0.22, double EquipUtil = 0.18,
    double PeakShaving = 0.16, double AdvanceAttain = 0.16)
{
    public double Sum => Completion + OutputBalance + EquipUtil + PeakShaving + AdvanceAttain;
}

/// <summary>短期多方案对比 —— 忠实原 ShortTermComparer.Score(五指标归一×权重; 设备利用率目标 90%)。</summary>
public static class ShortTermComparer
{
    public const double UtilTarget = 90;
    private static double NormHigh(double[] a, int i) { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (a[i] - mn) / (mx - mn) : 0.5; }
    private static double NormLow(double[] a, int i) { double mn = a.Min(), mx = a.Max(); return mx > mn + 1e-9 ? (mx - a[i]) / (mx - mn) : 0.5; }

    public static ShortTermResult? Score(IReadOnlyList<ShortTermResult> results, ShortTermWeights? weights = null)
    {
        var r = results.Where(x => x != null).ToList();
        if (r.Count == 0) return null;
        var w = weights ?? new ShortTermWeights();
        double wsum = w.Sum <= 0 ? 1 : w.Sum;
        double[] compDev = r.Select(x => Math.Abs(x.CompletionRatePct - 100)).ToArray();
        double[] bal = r.Select(x => x.BalanceCoef).ToArray();
        double[] utilFit = r.Select(x => -Math.Abs(x.AvgEquipUtilPct - UtilTarget)).ToArray();
        double[] peak = r.Select(x => x.PeakMonthCoalWanT).ToArray();
        double[] adv = r.Select(x => x.AdvanceTotalM).ToArray();
        ShortTermResult? best = null; double bestScore = -1;
        for (int i = 0; i < r.Count; i++)
        {
            double s = NormLow(compDev, i) * w.Completion + NormHigh(bal, i) * w.OutputBalance + NormHigh(utilFit, i) * w.EquipUtil
                     + NormLow(peak, i) * w.PeakShaving + NormHigh(adv, i) * w.AdvanceAttain;
            r[i].CompositeScore = Math.Round(s / wsum * 100, 0);
            if (r[i].Ok && r[i].CompositeScore > bestScore) { bestScore = r[i].CompositeScore; best = r[i]; }
        }
        if (best == null) foreach (var x in r) if (x.CompositeScore > bestScore) { bestScore = x.CompositeScore; best = x; }
        return best;
    }
}

public static class ShortTermScheduler
{
    /// <summary>对一个月度计划方案排产, 回填 Months 与 Result。</summary>
    public static void Schedule(ShortTermPlan p)
    {
        p.Months.Clear();
        int n = Math.Clamp(p.MonthCount, 1, 24);
        int start = Math.Clamp(p.StartMonth, 1, 12);
        double rho = ShortTermPlan.DefaultCoalDensity;
        double annualCoal = Math.Max(1, p.AnnualCoalTargetWanT);
        double baseRatio = Math.Max(0.1, p.BaseRatio);

        double avail = Math.Clamp(p.EquipmentAvailabilityPct / 100.0, 0.3, 1.0);
        var months = Enumerable.Range(0, n).Select(i => ((start - 1 + i) % 12) + 1).ToArray();
        double[] wd = months.Select(m => p.WorkdaysFor(m)).ToArray();
        double[] shape = DispatchShape(p.Dispatch, n);
        double[] w = new double[n];
        for (int i = 0; i < n; i++) w[i] = Math.Max(0.05, wd[i] * avail * shape[i]);
        double wsum = w.Sum();

        double uniform = annualCoal / n;
        double lambda = Math.Clamp(p.OutputSmooth / Math.Max(1e-6, p.BalanceSum), 0, 1);
        double[] coal = new double[n];
        for (int i = 0; i < n; i++) coal[i] = annualCoal * w[i] / wsum * (1 - lambda) + uniform * lambda;

        if (p.MonthlyCoalCeilingWanT > 0) RedistributeCeiling(coal, p.MonthlyCoalCeilingWanT);

        double ratioLambda = Math.Clamp(p.RatioSmooth / Math.Max(1e-6, p.BalanceSum), 0, 1);
        double amp = p.Dispatch switch { DispatchStrategy.Concentrated => 0.22, DispatchStrategy.MultiFace => 0.08, _ => 0.12 };
        amp *= 1 - ratioLambda;

        double cumCoal = 0, cumStrip = 0, advTot = 0, utilSum = 0; int utilCnt = 0;
        double peakCoal = coal.Max();
        var rows = new List<MonthPeriod>();
        for (int i = 0; i < n; i++)
        {
            int m = months[i];
            double dev = uniform > 1e-6 ? (coal[i] - uniform) / uniform : 0;
            double ratio = baseRatio * (1 + amp * dev);
            if (p.RatioCeiling > 0) ratio = Math.Min(ratio, p.RatioCeiling);
            ratio = Math.Max(0.1, ratio);
            double strip = coal[i] * ratio;
            if (p.MonthlyStripCeilingWanM3 > 0 && strip > p.MonthlyStripCeilingWanM3)
            { strip = p.MonthlyStripCeilingWanM3; ratio = coal[i] > 1e-6 ? strip / coal[i] : ratio; }
            cumCoal += coal[i]; cumStrip += strip;
            double v = LongTermScheduler.AdvanceRateFrom(coal[i] * 12, p.WorkLineLenM, p.BenchHeightM, rho) / 12.0;
            advTot += v;
            double volM3 = coal[i] / rho + strip;
            double capThisMonth = p.EquipmentCount * p.EquipMonthlyCapacityWanM3 * (wd[i] / Math.Max(1, p.StandardWorkdays)) * avail;
            double util = capThisMonth > 1e-6 ? volM3 / capThisMonth * 100 : 0;
            utilSum += util; utilCnt++;
            rows.Add(new MonthPeriod
            {
                Label = $"{p.PlanYear}-{m:00}", Month = m, Workdays = Math.Round(wd[i], 1),
                CoalWanT = Math.Round(coal[i], 1), StripWanM3 = Math.Round(strip, 0), Ratio = Math.Round(ratio, 2),
                CumCoal = Math.Round(cumCoal, 1), CumStrip = Math.Round(cumStrip, 0), AdvanceM = Math.Round(v, 1),
                EquipUtilPct = Math.Round(util, 0), CompletionPct = Math.Round(cumCoal / annualCoal * 100, 1),
                Dump = LongTermDumpMode.External, ActiveFace = ActiveFaceFor(p, i, n),
                IsMaintenance = p.MaintenanceMonth == m, IsPeak = Math.Abs(coal[i] - peakCoal) < 1e-6,
            });
        }
        foreach (var r in rows) p.Months.Add(r);
        p.Result = Evaluate(p, rows, advTot, utilCnt > 0 ? utilSum / utilCnt : 0);
    }

    private static double[] DispatchShape(DispatchStrategy d, int n)
    {
        var s = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = n <= 1 ? 0.5 : (double)i / (n - 1);
            s[i] = d switch
            {
                DispatchStrategy.Concentrated => 1.0 + 0.35 * Math.Sin(Math.PI * Math.Clamp(t * 1.1, 0, 1)),
                DispatchStrategy.MultiFace => 1.0 + 0.05 * Math.Sin(Math.PI * t),
                _ => 1.0
            };
        }
        return s;
    }

    private static string ActiveFaceFor(ShortTermPlan p, int i, int n)
    {
        if (p.Faces.Count == 0) return "";
        if (p.Dispatch == DispatchStrategy.MultiFace)
            return string.Join("+", p.Faces.Take(Math.Min(2, p.Faces.Count)));
        int idx = Math.Clamp((int)Math.Floor((double)i / Math.Max(1, n) * p.Faces.Count), 0, p.Faces.Count - 1);
        return p.Faces[idx];
    }

    private static void RedistributeCeiling(double[] coal, double ceiling)
    {
        for (int iter = 0; iter < 6; iter++)
        {
            double overflow = 0; var room = new List<int>();
            for (int i = 0; i < coal.Length; i++)
            {
                if (coal[i] > ceiling) { overflow += coal[i] - ceiling; coal[i] = ceiling; }
                else if (coal[i] < ceiling - 1e-6) room.Add(i);
            }
            if (overflow < 1e-6 || room.Count == 0) break;
            double each = overflow / room.Count;
            foreach (var i in room) coal[i] = Math.Min(ceiling, coal[i] + each);
        }
    }

    private static ShortTermResult Evaluate(ShortTermPlan p, List<MonthPeriod> rows, double advTot, double avgUtil)
    {
        double totCoal = rows.Sum(z => z.CoalWanT), totStrip = rows.Sum(z => z.StripWanM3);
        double completion = p.AnnualCoalTargetWanT > 1e-6 ? totCoal / p.AnnualCoalTargetWanT * 100 : 0;
        double avgRatio = totCoal > 1e-6 ? totStrip / totCoal : 0;
        double[] coal = rows.Select(z => z.CoalWanT).ToArray();
        double[] ratio = rows.Select(z => z.Ratio).ToArray();
        double outCv = Cv(coal), ratCv = Cv(ratio);
        var peak = rows.OrderByDescending(z => z.CoalWanT).First();
        double balance = Math.Clamp(1 - outCv, 0, 1);
        bool completionOk = Math.Abs(completion - 100) <= p.CompletionTolerancePct + 1e-6;
        bool ceilOk = p.MonthlyCoalCeilingWanT <= 0 || coal.Max() <= p.MonthlyCoalCeilingWanT + 1e-6;
        bool ratioOk = p.RatioCeiling <= 0 || ratio.Max() <= p.RatioCeiling + 1e-6;
        bool ok = completionOk && ceilOk && ratioOk;   // 备采保有月数校核略去(记录)
        return new ShortTermResult(Math.Round(totCoal, 0), Math.Round(totStrip, 0), Math.Round(completion, 1),
            Math.Round(avgRatio, 2), Math.Round(peak.CoalWanT, 1), peak.Label, Math.Round(outCv, 3), Math.Round(ratCv, 3),
            Math.Round(avgUtil, 0), Math.Round(advTot, 0), Math.Round(balance, 2), ok);
    }

    private static double Cv(double[] a)
    {
        if (a.Length == 0) return 0;
        double mean = a.Average();
        if (mean <= 1e-9) return 0;
        return Math.Sqrt(a.Select(x => (x - mean) * (x - mean)).Average()) / mean;
    }

    public readonly record struct DispatchSpec(string Label, DispatchStrategy Strategy);
    public readonly record struct CalendarSpec(string Label, CalendarScenario Scenario);

    public static List<DispatchSpec> DefaultDispatches() => new()
    { new("均衡型", DispatchStrategy.Balanced), new("多面展开", DispatchStrategy.MultiFace), new("集中强采", DispatchStrategy.Concentrated) };

    public static List<CalendarSpec> DefaultCalendars() => new()
    { new("标准", CalendarScenario.Standard), new("抢产", CalendarScenario.Push), new("保守", CalendarScenario.Conservative) };

    /// <summary>作业组织 × 工作历 正交派生多套月度计划, 各独立排产。忠实原 GenerateVariants。</summary>
    public static List<ShortTermPlan> GenerateVariants(ShortTermPlan basePlan,
        IReadOnlyList<DispatchSpec> dispatches, IReadOnlyList<CalendarSpec> calendars)
    {
        var result = new List<ShortTermPlan>();
        foreach (var d in dispatches)
            foreach (var c in calendars)
            {
                var v = basePlan.Clone();
                v.Name = $"{d.Label}·{c.Label}"; v.Dispatch = d.Strategy; v.Calendar = c.Scenario;
                Schedule(v);
                if (v.Result != null) v.Result.Name = v.Name;
                result.Add(v);
            }
        return result;
    }
}
