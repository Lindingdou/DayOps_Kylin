using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Data;

// ─────────────────────────────────────────────────────────────────────────────
//  采运排设备智能编组优化（忠实移植原 GeoDataBase.Equipment.FleetOptimizer）。
//    ① 由物理参数（斗容/载重/循环/运距）算每条编组规则的「单组日产能 + 配车匹配系数」；
//       §3.3 物理产能子模型 + M/M/c 排队论内生匹配系数（替代 MF≡1）。
//    ② 无界 DP 最小总卡车数达标，再按在籍台数做可行性修复。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>编组规则（optimizer 输入 POCO；由 dispatch_rule join equipment_model 载重/斗容得）。</summary>
public sealed class FleetDispatchRule
{
    public string ShovelModel { get; set; } = "";
    public string TruckModel { get; set; } = "";
    public double CycleTimeMin { get; set; }
    public int RecommendedTruckCount { get; set; }
    public double TruckPayloadT { get; set; }
    public double BucketLoadsPerTruck { get; set; }
    public double ShovelBucketM3 { get; set; }
    public int EfficiencyScore { get; set; }
}

/// <summary>编组优化输入。</summary>
public sealed class FleetOptInput
{
    public double DailyTargetM3 { get; set; }              // 日产能目标 (m³/天)
    public double HaulDistanceKm { get; set; }             // 平均运距 (km，0=用规则循环时间)
    public double WorkHoursPerDay { get; set; } = 16.0;    // 日有效作业时 (双班 8h)
    public double Efficiency { get; set; } = 0.80;         // 作业效率 η（可由 KPI 可用率×利用率喂）
    public double DensityTPerM3 { get; set; } = 1.6;       // 物料松方密度 t/m³
    public double AvgSpeedKmh { get; set; } = 25.0;        // 重车平均车速（算运距循环用）
    public double SpotLoadDumpMin { get; set; } = 4.0;     // 调车+装+卸固定时间
    public double LoadSwingMin { get; set; } = 0.53;       // 电铲单斗回转周期(min/斗)
    public List<FleetDispatchRule> Rules { get; set; } = new();
    public Dictionary<string, int> ShovelInventory { get; set; } = new(StringComparer.OrdinalIgnoreCase); // 型号→在籍台数（空=不限）
}

/// <summary>一种电铲型号的最优编组方案。</summary>
public sealed class FleetGroup
{
    public FleetDispatchRule Rule { get; set; } = new();
    public int ShovelCount { get; set; }
    public int TrucksPerShovel { get; set; }
    public int TotalTrucks => ShovelCount * TrucksPerShovel;
    public double EffectiveCycleMin { get; set; }
    public double GroupDailyM3 { get; set; }
    public double TotalDailyM3 => ShovelCount * GroupDailyM3;
    public double MatchFactor { get; set; }
    public int Inventory { get; set; }
    public bool InventoryShort => Inventory >= 0 && ShovelCount > Inventory;
    public double LoadTimeMin { get; set; }
    public double ShovelUtil { get; set; }
    public double TruckUtil { get; set; }
    public double WaitProbability { get; set; }
    public string Bottleneck { get; set; } = "";
}

public sealed class FleetOptResult
{
    public List<FleetGroup> Groups { get; set; } = new();
    public double TargetM3 { get; set; }
    public double TotalDailyM3 => Groups.Sum(g => g.TotalDailyM3);
    public int TotalShovels => Groups.Sum(g => g.ShovelCount);
    public int TotalTrucks => Groups.Sum(g => g.TotalTrucks);
    public bool TargetMet => TotalDailyM3 >= TargetM3 - 1e-6;
    public double FillRatio => TargetM3 > 1e-6 ? TotalDailyM3 / TargetM3 : 0;
    public List<string> Notes { get; set; } = new();
}

public static class FleetOptimizer
{
    public static FleetOptResult Optimize(FleetOptInput inp)
    {
        var res = new FleetOptResult { TargetM3 = inp.DailyTargetM3 };
        var rules = (inp.Rules ?? new()).Where(r => r.CycleTimeMin > 0 && r.RecommendedTruckCount > 0 && r.TruckPayloadT > 0).ToList();
        if (rules.Count == 0) { res.Notes.Add("无可用编组规则。"); return res; }
        if (inp.DailyTargetM3 <= 0) { res.Notes.Add("日产能目标为 0，请填月计划。"); return res; }

        var cap = rules.Select(r => Capacity(r, inp)).Where(c => c.DailyM3 > 0).ToList();
        if (cap.Count == 0) { res.Notes.Add("规则产能计算为 0（检查载重/循环参数）。"); return res; }

        var pick = SolveMinTrucks(cap, inp.DailyTargetM3, inp.ShovelInventory);

        foreach (var (c, count) in pick)
        {
            inp.ShovelInventory.TryGetValue(c.Rule.ShovelModel, out var inv);
            res.Groups.Add(new FleetGroup
            {
                Rule = c.Rule, ShovelCount = count, TrucksPerShovel = c.Rule.RecommendedTruckCount,
                EffectiveCycleMin = Math.Round(c.CycleMin, 1), GroupDailyM3 = Math.Round(c.DailyM3),
                MatchFactor = Math.Round(c.MatchFactor, 2),
                Inventory = inp.ShovelInventory.Count == 0 ? -1 : inv,
                LoadTimeMin = Math.Round(c.LoadMin, 2),
                ShovelUtil = Math.Round(c.ShovelUtil, 3),
                TruckUtil = Math.Round(c.TruckUtil, 3),
                WaitProbability = Math.Round(ErlangC(Math.Max(1, count), Math.Min(0.999, c.MatchFactor)), 3),
                Bottleneck = c.Bottleneck,
            });
        }
        res.Groups = res.Groups.OrderByDescending(g => g.Rule.EfficiencyScore).ToList();

        if (!res.TargetMet)
            res.Notes.Add($"在籍设备不足：可达 {res.TotalDailyM3 / 1e4:0.00} 万m³/天 < 目标 {inp.DailyTargetM3 / 1e4:0.00}，缺口 {(inp.DailyTargetM3 - res.TotalDailyM3) / 1e4:0.00} 万m³，建议增配或租赁。");
        foreach (var g in res.Groups.Where(x => x.InventoryShort))
            res.Notes.Add($"{g.Rule.ShovelModel}：需 {g.ShovelCount} 台 > 在籍 {g.Inventory} 台。");
        res.Notes.Add($"口径：日作业 {inp.WorkHoursPerDay:0.#}h · 效率 η={inp.Efficiency:0.00} · 密度 {inp.DensityTPerM3:0.0}t/m³"
                    + (inp.HaulDistanceKm > 0 ? $" · 运距 {inp.HaulDistanceKm:0.0}km@{inp.AvgSpeedKmh:0}km/h（参数可改）" : "（运距 0，用规则循环时间）"));
        return res;
    }

    private readonly record struct Cap(FleetDispatchRule Rule, double CycleMin, double DailyM3, double MatchFactor,
        double LoadMin, double ShovelUtil, double TruckUtil, double WaitProb, string Bottleneck);

    /// <summary>§3.3 物理产能子模型 + M/M/c 排队论内生匹配系数（替代 MF≡1）。</summary>
    private static Cap Capacity(FleetDispatchRule r, FleetOptInput inp)
    {
        double cycle = r.CycleTimeMin;
        if (inp.HaulDistanceKm > 0 && inp.AvgSpeedKmh > 0)
        {
            double haul = 2.0 * inp.HaulDistanceKm / inp.AvgSpeedKmh * 60.0;
            cycle = Math.Max(r.CycleTimeMin, inp.SpotLoadDumpMin + haul);
        }
        int n = r.RecommendedTruckCount;

        double loadsPerTruck = r.BucketLoadsPerTruck > 0 ? r.BucketLoadsPerTruck
            : (r.ShovelBucketM3 > 0 ? r.TruckPayloadT / Math.Max(0.1, r.ShovelBucketM3 * inp.DensityTPerM3 * 0.85) : 4.0);
        double tLoad = Math.Max(0.1, loadsPerTruck * inp.LoadSwingMin);

        double mf = n * tLoad / cycle;
        double shovelMaxTPerH = r.TruckPayloadT * 60.0 / tLoad;
        double truckFleetTPerH = n * (r.TruckPayloadT * 60.0 / cycle);
        double effTPerH = Math.Min(shovelMaxTPerH, truckFleetTPerH);
        double dailyM3 = effTPerH / Math.Max(0.1, inp.DensityTPerM3) * inp.WorkHoursPerDay * inp.Efficiency;

        double shovelUtil = Math.Min(1.0, mf);
        double truckUtil = mf > 1 ? 1.0 / mf : 1.0;
        double waitProb = ErlangC(1, Math.Min(0.999, mf));
        string bneck = mf < 0.9 ? "运力不足·铲待车" : mf <= 1.1 ? "配置均衡" : "铲能力瓶颈·车排队";

        return new Cap(r, cycle, dailyM3, mf, tLoad, shovelUtil, truckUtil, waitProb, bneck);
    }

    /// <summary>Erlang-C：M/M/c 中顾客到达需排队的概率。ρ=a/c 为每台利用率(&lt;1 稳定)。</summary>
    public static double ErlangC(int c, double rhoPerServer)
    {
        if (rhoPerServer >= 1) return 1.0;
        double a = rhoPerServer * c;
        double sum = 0, term = 1;
        for (int k = 0; k < c; k++) { if (k > 0) term *= a / k; sum += term; }
        double last = term * a / c;
        double top = last / (1 - rhoPerServer);
        return top / (sum + top);
    }

    /// <summary>无界 DP 最小总卡车数达标，再按在籍台数做可行性修复。</summary>
    private static List<(Cap c, int count)> SolveMinTrucks(List<Cap> cap, double targetM3, Dictionary<string, int> inv)
    {
        double unit = Math.Max(targetM3 / 400.0, 1.0);
        int cells = Math.Min(1500, (int)Math.Ceiling(targetM3 / unit));
        var capCells = cap.Select(c => Math.Max(1, (int)Math.Round(c.DailyM3 / unit))).ToArray();
        var cost = cap.Select(c => c.Rule.RecommendedTruckCount * 10 + 1).ToArray();

        var dp = new long[cells + 1];
        var choice = new int[cells + 1];
        Array.Fill(dp, long.MaxValue);
        dp[0] = 0;
        for (int cIdx = 1; cIdx <= cells; cIdx++)
            for (int s = 0; s < cap.Count; s++)
            {
                int prev = Math.Max(0, cIdx - capCells[s]);
                if (dp[prev] == long.MaxValue) continue;
                long v = dp[prev] + cost[s];
                if (v < dp[cIdx]) { dp[cIdx] = v; choice[cIdx] = s; }
            }

        var counts = new int[cap.Count];
        for (int c = cells; c > 0;)
        {
            int s = choice[c];
            counts[s]++;
            c = Math.Max(0, c - capCells[s]);
        }

        if (inv.Count > 0)
        {
            for (int s = 0; s < cap.Count; s++)
            {
                inv.TryGetValue(cap[s].Rule.ShovelModel, out var avail);
                if (counts[s] > avail) counts[s] = avail;
            }
            double have = counts.Select((n, s) => n * cap[s].DailyM3).Sum();
            foreach (var s in Enumerable.Range(0, cap.Count).OrderByDescending(s => cap[s].DailyM3))
            {
                inv.TryGetValue(cap[s].Rule.ShovelModel, out var avail);
                while (have < targetM3 && counts[s] < avail) { counts[s]++; have += cap[s].DailyM3; }
            }
        }

        return Enumerable.Range(0, cap.Count).Where(s => counts[s] > 0).Select(s => (cap[s], counts[s])).ToList();
    }
}
