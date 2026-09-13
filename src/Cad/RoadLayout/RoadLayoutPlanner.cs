using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Transport;

namespace PitMine3D.Kylin.Cad.RoadLayout;

/// <summary>
/// 方案构建器:选线 → 评估运量 → 出一份「可视化 / 可拖拽调整」的布置方案。
///
/// 范围(刻意轻):只「建立方案」——告诉每个位置设什么坡道(<see cref="RampForm"/>)、
/// 评估总运量能否满足(需几车道 / 缺口 / 哪段几何不可行)。
/// 不生成最终路体实体、不做重优化;产出 <see cref="RoadLayoutScheme"/> 数据,
/// 供视口箭头可视化 + 用户拖拽改位置后 <see cref="BuildScheme"/> 重评。
/// </summary>
public sealed class RoadLayoutPlanner
{
    private readonly IRampRouteGenerator _gen;

    public RoadLayoutPlanner(IRampRouteGenerator? generator = null)
        => _gen = generator ?? new RampRouteGenerator();

    /// <summary>选线 + 评估,产出一份方案(首版:候选段串成一条主下降链)。</summary>
    public RoadLayoutResult BuildScheme(RoadLayoutInput input)
    {
        if (input == null)
            return new RoadLayoutResult { Success = false, Error = "input 为空" };

        // 1) 选线:候选段(斜/转坡道 + 位置,锚台阶线)。用户拖拽改位置后可传回 input.Candidates 重评。
        var generated = input.Candidates ?? _gen.Generate(input);
        if (generated.Count == 0)
            return new RoadLayoutResult { Success = false, Error = "无候选坑线段(检查台阶线 / 非工作帮)" };

        // 每级择一条 → 串成"一条链"(口径与 RoadLayoutSolver 同一份实现,禁布区也在那里生效)。
        // 选线器每对相邻台阶产出 CandidatesPerLevel 个**位置不同**的候选,整表直接串起来
        // 会把展线总长/基建量按候选数成倍放大(3 候选 = 3 倍)。
        var chainWarnings = new List<string>();
        var candidates = RoadLayoutSolver.PickChainPerLevel(generated, input.NoGoZones, chainWarnings);

        // 2) 运量需求(t):优先用 OD,否则汇总采剥点产量。
        double demand = TotalDemandTons(input);

        // 3) 单车道运力(首版:取候选段最小值,作主下降链的瓶颈)。
        double perLane = double.MaxValue;
        foreach (var c in candidates)
            if (c.PerLaneCapacityTons > 0) perLane = Math.Min(perLane, c.PerLaneCapacityTons);
        if (perLane == double.MaxValue) perLane = 0;

        // 4) 运量驱动定车道:车道数 = ⌈需求 / 单车道运力⌉。
        int lanesNeeded = (perLane > 1e-6 && demand > 0)
            ? (int)Math.Ceiling(demand / perLane)
            : Math.Max(1, input.Constraints?.LaneCount ?? 1);

        // 5) 组装为一条主线路:候选段串成下降链,每段带形式 + 起坡位置。
        var segs = new List<RoadSegment>();
        var levels = new List<double>();
        var violations = new List<string>(chainWarnings);
        double totalLen = 0;
        foreach (var c in candidates)
        {
            if (!c.GeomFeasible)
                violations.Add($"{c.FromLevel:0}→{c.ToLevel:0}m: {c.Note}");
            segs.Add(new RoadSegment
            {
                FromLevel = c.FromLevel,
                ToLevel = c.ToLevel,
                Form = c.Form,
                GradePct = c.GradePct,
                LengthM = c.RequiredLengthM,
                TurnRadius = c.TurnRadius,
                // 沿帮真中心线(选线器给的点列);候选没给时退回只含起坡点的单点表,箭头仍锚得住。
                Centerline = RoadLayoutSolver.CenterlineOf(c)
            });
            if (levels.Count == 0) levels.Add(c.FromLevel);
            levels.Add(c.ToLevel);
            totalLen += c.RequiredLengthM;
        }

        double lineCapacity = perLane * lanesNeeded;
        var line = new RoadLine
        {
            Id = "L1",
            LevelSequence = levels,
            LaneCount = lanesNeeded,
            CapacityTons = lineCapacity,
            AssignedTons = demand,
            Utilization = lineCapacity > 1e-6 ? demand / lineCapacity : 0,
            LengthM = totalLen,
            Segments = segs
        };

        // 6) 评估"需求是否满足":几何已按可行构造(选型兜底);此处查运力 + 单线车道上限。
        const int MaxLanesPerRoad = 4; // 超上限说明单线扛不动,需加并行坑线(多线网络流待做)
        bool capacityOk = lineCapacity + 1e-6 >= demand;
        if (!capacityOk)
            violations.Add($"运力缺口:需 {demand:0} t/期,仅供 {lineCapacity:0} t/期");
        else if (lanesNeeded > MaxLanesPerRoad)
            violations.Add($"单线需 {lanesNeeded} 车道(＞建议上限 {MaxLanesPerRoad}),应加并行坑线(多线网络流待做)");
        bool feasible = violations.Count == 0;

        var scheme = new RoadLayoutScheme
        {
            Name = "方案1(自动)",
            Lines = new List<RoadLine> { line },
            Feasible = feasible,
            Violations = violations,
            TotalHaulCost = 0,          // 成本待接(运距×吨量×单价)
            TotalCapexProxy = totalLen, // 首版:用展线总长代理基建工程量
            SterilizedOreTons = 0,      // 压矿待接(坑线足迹×块体模型)
            TotalScore = 0
        };

        return new RoadLayoutResult
        {
            Success = true,
            Schemes = new List<RoadLayoutScheme> { scheme }
        };
    }

    /// <summary>
    /// 把方案格式化成命令行 / 面板可读的多行报告:
    /// 每段坡道列「连接台阶 / 类型 / 纵坡 / 起坡位置」,末尾给总运量评估与告警。
    /// 供"看"用(箭头落地前先有文字读数);拖拽改位置后重跑可重新格式化。
    /// </summary>
    public static IReadOnlyList<string> FormatReport(RoadLayoutResult result)
    {
        var lines = new List<string>();
        if (result == null || !result.Success)
        {
            lines.Add($"✗ 方案失败:{result?.Error ?? "未知"}");
            return lines;
        }
        foreach (var sch in result.Schemes)
        {
            lines.Add($"【{sch.Name}】{(sch.Feasible ? "✓ 运量可满足" : "✗ 需调整")}  线路 {sch.Lines.Count} 条");
            foreach (var ln in sch.Lines)
            {
                lines.Add($"  线路 {ln.Id}:{ln.LaneCount} 车道 / 年运力 {ln.CapacityTons / 10000.0:0} 万t / 利用率 {ln.Utilization * 100:0}% / 展线 {ln.LengthM:0} m");
                foreach (var sg in ln.Segments)
                {
                    double px = 0, py = 0, pz = 0;
                    if (sg.Centerline.Count > 0) { var p = sg.Centerline[0]; px = p.X; py = p.Y; pz = p.Z; }
                    lines.Add($"    {sg.FromLevel:0}→{sg.ToLevel:0}m  {RampRouteGenerator.FormLabel(sg.Form)}  纵坡{sg.GradePct:0.#}%  起坡({px:0.#},{py:0.#},{pz:0.#})");
                }
            }
            foreach (var v in sch.Violations) lines.Add($"    ⚠ {v}");
        }
        return lines;
    }

    private static double TotalDemandTons(RoadLayoutInput input)
    {
        double d = 0;
        if (input.Demands != null && input.Demands.Count > 0)
        {
            foreach (var h in input.Demands) d += h.Tons;
        }
        else if (input.Sources != null)
        {
            foreach (var s in input.Sources) d += s.OreTons + s.WasteTons;
        }
        return d;
    }
}
