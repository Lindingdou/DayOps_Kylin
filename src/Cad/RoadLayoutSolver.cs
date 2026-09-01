using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 运输道路布局求解 —— 忠实移植原 MineAssLib RoadLayoutSolver 的方案构建核(可验证切片)：
/// 给定坑线候选(展线长 + 几何可行) + 运量需求 + 单车道运力, 按「运量定总车道 → 拆线分车道」出
/// 紧凑/均衡/单线三方案, 逐方案核可行(几何可行 + 运力足 + 车道≤上限) + 运营成本(ton-km) + 基建代理(总展线长)。
/// 候选由 CSV/上游喂入(自动候选生成 = StraightRampAutoRouter 域, 记录); 本核不依赖引擎, 纯逻辑、可单测。
/// </summary>
public static class RoadLayoutSolver
{
    public const int MaxLanesPerRoad = 4;
    public const int MaxLanesPerLine = 2;   // "紧凑"方案的每线车道上限

    public readonly record struct RampCand(double FromLevel, double ToLevel, double RequiredLengthM, bool GeomFeasible, string Note);

    public sealed record RoadLine(string Id, int LaneCount, double CapacityTons, double AssignedTons, double Utilization, double LengthM);

    public sealed record LayoutScheme(string Name, IReadOnlyList<RoadLine> Lines, bool Feasible,
        IReadOnlyList<string> Violations, double TotalHaulCostYuan, double TotalCapexProxyM, double TotalLanes, double Score = 0);

    public sealed record LayoutResult(bool Success, IReadOnlyList<LayoutScheme> Schemes, LayoutScheme? Recommended);

    /// <summary>方案评分 0~100(忠实原 Score): 不可行=0; 否则 wCapex·(100/(1+capexKm)) + wUtil·(利用率·100)。objective 定权重。</summary>
    public static double ScoreOf(LayoutScheme s, string? objective = null)
    {
        if (!s.Feasible) return 0;
        (double wCapex, double wUtil) = objective switch { "均衡" => (0.6, 0.4), "最小运输功" => (1.0, 0.0), _ => (0.8, 0.2) };
        double capexKm = s.TotalCapexProxyM / 1000.0;
        double avgUtil = s.Lines.Count > 0 ? s.Lines.Average(l => l.Utilization) : 0;
        return wCapex * (100.0 / (1.0 + capexKm)) + wUtil * (avgUtil * 100.0);
    }

    /// <summary>三方案求解。demandTons=每期需求 t; perLaneTons=单车道运力 t/期; haulUnitCost=运输单价 元/(t·km); objective=目标(均衡/最小运输功/默认最小成本)。</summary>
    public static LayoutResult Solve(IReadOnlyList<RampCand> candidates, double demandTons, double perLaneTons, double haulUnitCost, string? objective = null)
    {
        if (candidates == null || candidates.Count == 0) return new LayoutResult(false, Array.Empty<LayoutScheme>(), null);
        var schemes = new List<LayoutScheme>
        {
            BuildScheme(candidates, demandTons, perLaneTons, haulUnitCost, "方案1·紧凑(少线宽路)", MaxLanesPerLine, forceSingle: false),
            BuildScheme(candidates, demandTons, perLaneTons, haulUnitCost, "方案2·均衡(多线窄路)", 1, forceSingle: false),
            BuildScheme(candidates, demandTons, perLaneTons, haulUnitCost, "方案3·单线(基线)", int.MaxValue, forceSingle: true),
        };
        for (int i = 0; i < schemes.Count; i++) schemes[i] = schemes[i] with { Score = ScoreOf(schemes[i], objective) };
        // 推荐: 得分最高的可行方案(忠实原按 Score 选优); 无可行则运力最大者。
        LayoutScheme? rec = schemes.Where(s => s.Feasible).OrderByDescending(s => s.Score).FirstOrDefault()
            ?? schemes.OrderByDescending(s => s.Lines.Sum(l => l.CapacityTons)).FirstOrDefault();
        return new LayoutResult(true, schemes, rec);
    }

    private static LayoutScheme BuildScheme(IReadOnlyList<RampCand> candidates, double demand, double perLane,
        double haulUnitCost, string name, int maxLanesPerLine, bool forceSingle)
    {
        var violations = new List<string>();
        double chainLenM = 0;
        foreach (var c in candidates)
        {
            chainLenM += c.RequiredLengthM;
            if (!c.GeomFeasible) violations.Add($"{c.FromLevel:0}→{c.ToLevel:0}m: {c.Note}");
        }

        int totalLanes = (perLane > 1e-6 && demand > 0) ? (int)Math.Ceiling(demand / perLane) : 1;   // 运量定总车道
        int nLines = forceSingle ? 1 : Math.Max(1, (int)Math.Ceiling((double)totalLanes / Math.Max(1, maxLanesPerLine)));
        var lines = new List<RoadLine>();
        int lanesLeft = totalLanes; double tonsLeft = demand;
        for (int i = 0; i < nLines; i++)
        {
            int remaining = nLines - i;
            int laneOfThis = forceSingle ? totalLanes : Math.Max(1, (int)Math.Ceiling((double)lanesLeft / remaining));
            lanesLeft -= laneOfThis;
            double cap = laneOfThis * perLane;
            double tons = remaining == 1 ? tonsLeft : demand * (laneOfThis / (double)Math.Max(1, totalLanes));
            tonsLeft -= tons;
            lines.Add(new RoadLine($"L{i + 1}", laneOfThis, cap, tons, cap > 1e-6 ? tons / cap : 0, chainLenM));
        }

        double totalCap = lines.Sum(l => l.CapacityTons);
        if (totalCap + 1e-6 < demand) violations.Add($"运力缺口:需 {demand:0} t/期,仅供 {totalCap:0} t/期");
        if (forceSingle && totalLanes > MaxLanesPerRoad) violations.Add($"单线需 {totalLanes} 车道(＞上限 {MaxLanesPerRoad}),应加并行坑线");
        foreach (var l in lines) if (l.LaneCount > MaxLanesPerRoad) violations.Add($"线路 {l.Id} 车道 {l.LaneCount} 超上限 {MaxLanesPerRoad}");

        double haulCost = demand * (chainLenM / 1000.0) * haulUnitCost;          // 运营: 需求×链运距(km)×单价
        double capexProxy = lines.Sum(l => l.LengthM);                          // 基建代理: 各线展线长之和
        return new LayoutScheme(name, lines, violations.Count == 0, violations, haulCost, capexProxy, totalLanes);
    }
}
