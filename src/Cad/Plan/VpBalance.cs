using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>VP（剥采比均衡）曲线中的"一期"（一年/一月）数据（原 <c>VpPeriod</c>）。P/V 可编辑，其余派生只读由 <see cref="VpBalanceSession.Recompute"/> 回填。基建年填 P=0。</summary>
public sealed class VpPeriod
{
    public string Label { get; set; } = "";
    /// <summary>采出量 P（万 t）。基建年填 0。</summary>
    public double Coal { get; set; }
    /// <summary>剥离量 V（万 m³）。</summary>
    public double Strip { get; set; }
    /// <summary>本期剥采比 V/P（m³/t）——派生只读。</summary>
    public double Ratio { get; set; }
    public double CumCoal { get; set; }
    public double CumStrip { get; set; }
    /// <summary>累计偏离 = 均衡折线在该累计P处的V − 累计V（万 m³）：正=超前剥离，负=欠剥报警。</summary>
    public double Deviation { get; set; }
    /// <summary>阶段标记：基建 / 过渡 / 稳产 / 减产。</summary>
    public string Phase { get; set; } = "";

    public string CoalText => Coal.ToString("N0");
    public string StripText => Strip.ToString("N0");
    public string RatioText => Ratio.ToString("F2");
    public string DeviationText => Deviation.ToString("N0");
}

/// <summary>一个均衡阶段（均衡折线的一段直线）（原 <c>VpStage</c>）。</summary>
public sealed class VpStage
{
    public int No { get; init; }
    public string FromLabel { get; init; } = "";
    public string ToLabel { get; init; } = "";
    /// <summary>该阶段均衡生产剥采比（m³/t）= 该段折线斜率。</summary>
    public double Ratio { get; init; }
    public int Years { get; init; }
    /// <summary>该阶段峰值超前剥离量（万 m³）。</summary>
    public double AdvanceStrip { get; init; }
    public bool OverEco { get; init; }
    public string RatioText => Ratio.ToString("F2");
    public string AdvanceText => AdvanceStrip.ToString("N0");
}

/// <summary>
/// 剥采比均衡的取数与计算（原 <c>VpCurveWindow.Recompute</c> 那一段抽成纯逻辑，窗口只画图）：
/// 累计/本期剥采比 → 投产点（年产首达 投产系数×A_p，基建剥离=投产前累计剥离）→ 达产点（年产首达 A_p）→ 阶段标记 →
/// 产出曲线顶点(含投产锚点 A=(0,基建剥离)) → <see cref="VpBalanceSolver"/> DP 分 K 段均衡折线 → 逐期偏离 → 指标与校核。
/// </summary>
public sealed class VpBalanceSession
{
    public List<VpPeriod> Periods { get; } = new();
    public double? DesignCapacity { get; set; } = 1000;   // 设计生产能力 A_p(万t/年)
    public double? EcoRatio { get; set; }                  // 经济合理剥采比(m³/t)，阶段上限
    public int? StageCount { get; set; }                   // 用户指定均衡期数；null=自动建议
    public double CommFrac { get; set; } = 1.0 / 3;        // 投产产能系数 A_g/A_p

    // ── 结果 ──
    public List<double> Xs { get; private set; } = new();   // 产出曲线顶点(含锚点)
    public List<double> Ys { get; private set; } = new();
    public List<int> Breakpoints { get; private set; } = new() { 0 };
    public List<VpStage> Stages { get; private set; } = new();
    public int UsedK { get; private set; }
    public int CommIdx { get; private set; } = -1;
    public int CapIdx { get; private set; } = -1;
    public double BaseStrip { get; private set; }
    public double TotalCoal { get; private set; }
    public double TotalStrip { get; private set; }
    public double AvgRatio { get; private set; }
    public double ProdRatio { get; private set; }
    public double AheadMonths { get; private set; }
    public double PeakAdvance { get; private set; }
    public List<string> Issues { get; private set; } = new();
    public bool CheckOk => Issues.Count == 0;

    /// <summary>模拟物料量(设计产量 A_p=1000 万t/年)：2 年基建 + 投产 + 过渡 + 稳产 + 末期减产，剥采比 3.6→7.0 逐年递增（原 LoadSampleData）。</summary>
    public static IReadOnlyList<(string Label, double Coal, double Strip)> SampleSeed { get; } = new (string, double, double)[]
    {
        ("2023", 0,    2200), ("2024", 0,    2600), ("2025", 500,  1800), ("2026", 750,  3000),
        ("2027", 1000, 4500), ("2028", 1000, 5000), ("2029", 1000, 5500), ("2030", 1000, 6000),
        ("2031", 1000, 6500), ("2032", 1000, 7000), ("2033", 800,  5600),
    };

    public void LoadSample()
    {
        Periods.Clear();
        foreach (var s in SampleSeed) Periods.Add(new VpPeriod { Label = s.Label, Coal = s.Coal, Strip = s.Strip });
        Recompute();
    }

    public void Load(IEnumerable<(string Label, double Coal, double Strip)> rows)
    {
        Periods.Clear();
        foreach (var r in rows) Periods.Add(new VpPeriod { Label = r.Label, Coal = r.Coal, Strip = r.Strip });
        Recompute();
    }

    /// <summary>新增一期（期号顺延、量沿用末期）。</summary>
    public VpPeriod AddPeriod()
    {
        var last = Periods.LastOrDefault();
        string label = (last != null && int.TryParse(last.Label, out int y)) ? (y + 1).ToString() : $"期{Periods.Count + 1}";
        var p = new VpPeriod { Label = label, Coal = last?.Coal ?? 1000, Strip = last?.Strip ?? 5000 };
        Periods.Add(p);
        Recompute();
        return p;
    }

    public void Recompute()
    {
        double cumCoal = 0, cumStrip = 0;
        foreach (var p in Periods)
        {
            cumCoal += p.Coal; cumStrip += p.Strip;
            p.Ratio = p.Coal > 1e-9 ? p.Strip / p.Coal : 0;
            p.CumCoal = cumCoal; p.CumStrip = cumStrip;
        }
        TotalCoal = cumCoal; TotalStrip = cumStrip;

        double commThresh = DesignCapacity is double pdc ? CommFrac * pdc : 0;
        CommIdx = -1;
        for (int i = 0; i < Periods.Count; i++)
            if (Periods[i].Coal > 1e-9 && Periods[i].Coal + 1e-9 >= commThresh) { CommIdx = i; break; }
        BaseStrip = CommIdx >= 0 ? (CommIdx == 0 ? 0 : Periods[CommIdx - 1].CumStrip) : TotalStrip;

        CapIdx = -1;
        if (DesignCapacity is double pd)
            for (int i = (CommIdx < 0 ? Periods.Count : CommIdx); i < Periods.Count; i++)
                if (Periods[i].Coal + 1e-9 >= pd) { CapIdx = i; break; }

        for (int i = 0; i < Periods.Count; i++)
        {
            var p = Periods[i];
            if (i < CommIdx || CommIdx < 0 || p.Coal <= 1e-9) p.Phase = "基建";
            else if (CapIdx < 0 || i < CapIdx) p.Phase = "过渡";
            else if (DesignCapacity is double d && p.Coal + 1e-9 < d) p.Phase = "减产";
            else p.Phase = "稳产";
        }

        var xs = new List<double> { 0 };
        var ys = new List<double> { BaseStrip };
        if (CommIdx >= 0)
            for (int i = CommIdx; i < Periods.Count; i++) { xs.Add(Periods[i].CumCoal); ys.Add(Periods[i].CumStrip); }
        Xs = xs; Ys = ys;
        int M = xs.Count - 1;

        Breakpoints = new List<int> { 0 };
        Stages = new List<VpStage>();
        UsedK = 0;
        if (M >= 1)
        {
            var sol = VpBalanceSolver.Solve(xs, ys, StageCount);
            Breakpoints = sol.Breakpoints;
            UsedK = sol.UsedK;
            Stages = BuildStages(xs, ys, Breakpoints);
        }

        foreach (var p in Periods)
            p.Deviation = VpBalanceSolver.BalanceY(xs, ys, Breakpoints, p.CumCoal) - p.CumStrip;

        AvgRatio = TotalCoal > 1e-9 ? TotalStrip / TotalCoal : 0;
        ProdRatio = TotalCoal > 1e-9 ? (TotalStrip - BaseStrip) / TotalCoal : 0;
        AheadMonths = 0;
        if (CommIdx >= 0 && ProdRatio > 1e-9)
        {
            var c = Periods[CommIdx];
            double exposedAhead = c.CumStrip / ProdRatio - c.CumCoal;
            if (c.Coal > 1e-9) AheadMonths = exposedAhead / (c.Coal / 12.0);
        }
        PeakAdvance = Periods.Count > 0 ? Math.Max(0, Periods.Max(p => p.Deviation)) : 0;

        var issues = new List<string>();
        if (Periods.Any(p => p.Deviation < -1e-6)) issues.Add("欠剥");
        if (CommIdx >= 0 && AheadMonths < 2) issues.Add("投产超前<2月");
        if (Stages.Any(s => s.OverEco)) issues.Add("超经济比");
        for (int i = 1; i < Stages.Count; i++)
            if (Stages[i].Ratio + 1e-6 < Stages[i - 1].Ratio) { issues.Add("剥采比非递增"); break; }
        Issues = issues;
    }

    private List<VpStage> BuildStages(IReadOnlyList<double> xs, IReadOnlyList<double> ys, List<int> bps)
    {
        var list = new List<VpStage>();
        string LabelOf(int v) =>
            v == 0 ? (CommIdx >= 0 ? Periods[CommIdx].Label : "投产")
                   : (CommIdx + v - 1 < Periods.Count ? Periods[CommIdx + v - 1].Label : "终");
        for (int s = 0; s < bps.Count - 1; s++)
        {
            int a = bps[s], b = bps[s + 1];
            double dx = xs[b] - xs[a];
            double ratio = dx > 1e-9 ? (ys[b] - ys[a]) / dx : 0;
            list.Add(new VpStage
            {
                No = s + 1, FromLabel = LabelOf(a), ToLabel = LabelOf(b), Ratio = ratio, Years = b - a,
                AdvanceStrip = VpBalanceSolver.SegPeakGap(xs, ys, a, b),
                OverEco = EcoRatio is double n && ratio > n + 1e-6,
            });
        }
        return list;
    }

    /// <summary>每个产出期所属阶段的均衡剥采比（顶点 v 对应期 CommIdx+v−1；基建期 null）——剥采比阶梯图用。</summary>
    public double?[] StageRatioPerPeriod()
    {
        var r = new double?[Periods.Count];
        for (int s = 0; s + 1 < Breakpoints.Count && s < Stages.Count; s++)
            for (int v = Breakpoints[s] + 1; v <= Breakpoints[s + 1]; v++)
            {
                int per = CommIdx + v - 1;
                if (per >= 0 && per < Periods.Count) r[per] = Stages[s].Ratio;
            }
        return r;
    }
}
