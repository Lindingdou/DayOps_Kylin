// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/TaskRescheduler.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  动态调整 = 实绩回灌 → 达成度评价 → 滚动重排 的闭环（响应条款 15）。
//  做法：以 fromHour 为界，扣掉各面已完成实绩得「剩余工作量」（含已欠量），
//  按原因码选定的调整策略改写 Config（补车 / 降产 / 切面 / 降效 / 备机顶替），
//  再跑一遍 TaskExploder → 得到此刻之后的新计划 + 新校核。止于班级（非实时 GPS 派车）。
//
//  ── 本轮升级（把占位改成有依据的重排）──
//  ① 换面 SwitchFace 必须过「去向兼容」这一关：目标面的去向接不接得住搬过去的物料
//     （物料白名单 / 排土库容占容方 / 卸点通过能力），接不住就换下一个候选面，
//     全不通过降级为 ReduceTarget —— 否则「换面」只是把无处可排的量换个地方无处可排。
//  ② 补车 AddTrucks 从「在籍空闲车」取，取不到才回落虚拟增援，并明记「无实际可调配车辆」。
//  ③ 备机顶替 ReassignBackup 从「在籍空闲主设备」取，取不到才回落占位名。
//  ④ 全盘降效系数不再硬写 0.8：优先按实绩测出的当班效率折算，测不出才用经验常量。
//  ⑤ 重排后去向侧一旦变化（运距变），按理论报告 §7「动态重编」重解编组：
//     ΔL → T_c = t_sd + 60·L_eq(1/v_h + 1/v_e) → n* = T_c/τ_L → 匹配系数回到 1 附近。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>原因码 → 调整策略（与 docs/日常生产组织_设计.md §6 路由一致）。</summary>
public enum AdjustStrategy
{
    RollForward,      // 仅把剩余量滚到后续班次
    AddTrucks,        // 补车到推荐车数（解运力不足）
    ReduceTarget,     // 削目标到剩余产能（避免硬欠产）
    ReassignBackup,   // 备机顶替主设备（故障）
    ReduceCapacity,   // 全盘降效（天气/缺勤）
    SwitchFace,       // 换面：把本面剩余转入有料的其它采装面（缺料）
}

public sealed class RescheduleResult
{
    public ExploderResult Plan { get; set; } = new();           // 重排后此刻之后的新计划
    public List<string> Notes { get; set; } = new();            // 人读的改动说明
    public double RolledShortfallM3 { get; set; }               // 回摊的总剩余量
    public double FromHour { get; set; }

    /// <summary>重排后重解的流向方案（去向侧有变化且有去向台账时才有值）。</summary>
    public FlowPlan? Flow { get; set; }
    /// <summary>是否触发了编组重解（运距/可用设备变化 → n* 重算）。</summary>
    public bool FleetRematched { get; set; }

    public string Summary => Notes.Count == 0
        ? "无可重排的剩余量"
        : $"自 {Hm(FromHour)} 起重排：回摊剩余 {RolledShortfallM3:0} m³，新计划 {Plan.Tasks.Count(t => t.Process != ProcessType.Idle)} 项，校核 {Plan.Violations.Count} 条。"
          + (FleetRematched ? "编组已按新运距重解。" : "");

    private static string Hm(double hh) { int h = (int)hh; int m = (int)Math.Round((hh - h) * 60); return $"{h:00}:{m:00}"; }
}

public static class TaskRescheduler
{
    // ── 可调参数 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 全盘降效缺省系数（天气/缺勤）。依据：露天矿雨雪/大风/大雾天，路面湿滑限速 + 卸点排队加剧 +
    /// 到岗率下降，班产经验折减 15%~25%，取中值 20% ⇒ 0.8。只有在无法从实绩测出当班效率时才用它
    /// （见 <see cref="EstimateDerate"/>：能测则一律按实测比例折算，比拍系数硬）。
    /// </summary>
    public const double DefaultDerateFactor = 0.80;
    /// <summary>实测折减的下限（低于此视为异常停产，不再按"降效"处理，应走停产/检修口径）。</summary>
    public const double MinDerateFactor = 0.50;

    /// <summary>
    /// 在籍卡车池提供者（由 <c>TaskLibPlugin</c> 接设备台账后注入）。
    /// <b>返回 null = 台账里一台都没有</b>，此时回落在籍花名册（<see cref="RosterOf"/>）；
    /// 返回空表会挡住兜底，故契约上明确允许 null。
    /// </summary>
    public static Func<IEnumerable<string>?>? TruckPoolProvider { get; set; }
    /// <summary>在籍主设备（电铲）池提供者，同上。</summary>
    public static Func<IEnumerable<string>?>? ShovelPoolProvider { get; set; }

    /// <summary>原因码 → 默认策略。</summary>
    public static AdjustStrategy StrategyFor(IncompleteReason r) => r switch
    {
        IncompleteReason.Fault => AdjustStrategy.ReassignBackup,
        IncompleteReason.TruckShortage => AdjustStrategy.AddTrucks,
        IncompleteReason.OreShortage => AdjustStrategy.SwitchFace,
        IncompleteReason.Weather => AdjustStrategy.ReduceCapacity,
        IncompleteReason.Absence => AdjustStrategy.ReduceCapacity,
        IncompleteReason.OverPlanned => AdjustStrategy.ReduceTarget,
        _ => AdjustStrategy.RollForward,                          // 工序未接续 / 拥堵 / 检修 → 顺延
    };

    /// <summary>
    /// 从 fromHour 起对各面剩余工作量滚动重排；zoneStrategy 指定个别面的调整策略（缺省 RollForward）。
    /// 三趟：A 算剩余量 → B 施加策略（含去向/在籍校核）→ C 重解流向与编组 → 重跑装箱引擎。
    /// </summary>
    public static RescheduleResult Reschedule(
        ExploderConfig baseCfg,
        IReadOnlyList<ProductionTask> currentTasks,
        double fromHour,
        IReadOnlyDictionary<string, AdjustStrategy> zoneStrategy)
    {
        var res = new RescheduleResult { FromHour = fromHour };
        var vs = new List<PlanViolation>();          // 重排过程产生的校核（最后并入 Plan.Violations）

        // 1. 各面截至 fromHour 的已完成实绩（备采面/采装面合计）
        var doneByZone = currentTasks
            .Where(t => t.Process is ProcessType.Load or ProcessType.Dump)
            .GroupBy(t => t.WorkZone)
            .ToDictionary(g => g.Key, g => g.Sum(t => t.ActualVolumeM3));

        var cfg = CloneConfig(baseCfg);
        cfg.FromHour = fromHour;
        cfg.IdPrefix = baseCfg.IdPrefix + "R";                    // 重排标记，避免与原任务串号

        bool haveSinks = cfg.Sinks.All.Count > 0;                 // 有去向台账才谈得上去向校核
        var loadFaces = cfg.Faces.Where(f => f.Process == ProcessType.Load).ToList();

        // ── 趟 A：剩余 = 日目标 − 已完成（先全部算完，编组重解才有正确的 Q'）──
        foreach (var f in cfg.Faces)
        {
            double done = doneByZone.TryGetValue(f.Zone, out var d) ? d : 0;
            double remain = Math.Max(0, Math.Round(f.DayTargetM3 - done));
            f.DayTargetM3 = remain;
            res.RolledShortfallM3 += remain;
        }

        // 补车前先按「当前运距 + 剩余目标」重解一次建议车数 n*（FleetMatcher 可用时）
        bool needFleetFirst = cfg.Faces.Any(f => Strat(zoneStrategy, f) == AdjustStrategy.AddTrucks);
        if (needFleetFirst && PeerEngines.TryRematchFleet(cfg))
        {
            res.FleetRematched = true;
            res.Notes.Add("按当前运距与剩余目标重解编组（T_c 变 → n* 变），补车以重解后的建议车数为准。");
        }

        // 已被各面占用的设备（跨面不重复占用）
        var busyTrucks = new HashSet<string>(cfg.Faces.SelectMany(f => f.Group.Trucks), StringComparer.OrdinalIgnoreCase);
        var busyMains = new HashSet<string>(cfg.Faces.Select(f => f.Group.MainEquipment).Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);

        // ── 趟 B：逐面施加策略 ──
        bool globalDerate = false, destinationDirty = false, equipmentDirty = false;
        var derateZones = new List<string>();

        foreach (var f in cfg.Faces)
        {
            double remain = f.DayTargetM3;
            var strat = Strat(zoneStrategy, f);

            switch (strat)
            {
                case AdjustStrategy.AddTrucks:
                    equipmentDirty |= AddTrucks(f, res, vs, busyTrucks);
                    break;

                case AdjustStrategy.ReassignBackup:
                    equipmentDirty |= ReassignBackup(f, remain, res, vs, busyMains);
                    break;

                case AdjustStrategy.ReduceCapacity:
                    globalDerate = true;                          // 天气/缺勤 → 全盘降效（下方统一施加）
                    derateZones.Add(f.Zone);
                    break;

                case AdjustStrategy.SwitchFace:
                    if (f.Process == ProcessType.Load && remain > 0)
                    {
                        if (SwitchFace(cfg, f, loadFaces, remain, haveSinks, res, vs)) destinationDirty = true;
                        else
                        {
                            // 全部候选面都接不住 → 降级为削目标，并写清降级原因
                            ReduceTarget(cfg, f, remain, res, "换面不可行（无去向兼容且接得住的候选面）→ ");
                            vs.Add(V(ViolationSeverity.Warn, ViolationCodes.NoDestination,
                                $"{f.Zone} 缺料需换面，但无候选面的去向能接纳其物料/库容，已降级为削目标，余量转次日。"));
                        }
                    }
                    break;

                case AdjustStrategy.ReduceTarget:
                    ReduceTarget(cfg, f, remain, res, "");
                    break;

                case AdjustStrategy.RollForward:
                default:
                    if (remain > 0) res.Notes.Add($"{f.Zone}：剩余 {remain:0} m³ 顺延至后续班次。");
                    break;
            }
        }

        // ── 趟 C：去向侧重解 → 编组重解 → 最后才施加降效 ──
        // 换面后受影响面的「源—物料—汇」变了，必须重新分配流向：
        // 否则会出现「量搬到 A 面、A 面的排土场却接不住」这种纸面可行、现场不可行的计划。
        if (haveSinks && (destinationDirty || cfg.EnforceMassBalance))
        {
            try
            {
                var flow = FlowAssigner.ApplyToAndReport(cfg, overwriteManual: false);
                res.Flow = flow;
                vs.AddRange(flow.Violations.Where(v => v.Severity != ViolationSeverity.Info));
                if (!flow.Feasible || destinationDirty) res.Notes.Add("流向重解：" + flow.Explain);
            }
            catch (Exception ex)
            {
                vs.Add(V(ViolationSeverity.Info, ViolationCodes.NoDestination, $"流向重解未执行（{ex.GetType().Name}），沿用原去向。"));
            }
        }

        // 运距变 → T_c 变 → n* 变（理论报告 §7 动态重编）。
        // 编组求解器只写「建议车数 + 编组班产」，不动实配的 Trucks/MainEquipment，
        // 因此本轮刚借到的车、刚顶上的备机都不会被冲掉；重解后实配与建议之差即「铲待车」预警。
        if (destinationDirty || equipmentDirty)
        {
            if (PeerEngines.TryRematchFleet(cfg))
            {
                res.FleetRematched = true;
                res.Notes.Add("去向/设备变化 → 按新等效运距重解建议车数与编组班产（T_c → n* → 匹配系数回归 1）；实配车辆保持不变，差额由校核报「铲待车」。");
            }
            else if (destinationDirty)
                res.Notes.Add("提示：去向已变（运距随之变），编组求解器未接入，配车数仍按原 n*；接入 FleetMatcher 后自动重解。");
        }

        // 天气/缺勤降效最后施加：先让编组解出名义班产，再乘折减，否则重解会把折减冲掉
        if (globalDerate)
        {
            double k = EstimateDerate(currentTasks, derateZones, out string basis);
            foreach (var f in cfg.Faces) f.Group.GroupCapacityM3PerH *= k;
            res.Notes.Add($"全盘降效系数 {k:0.00}（{basis}）：各编组班产下调 {(1 - k) * 100:0.#}%。");
        }

        // 2. 重跑引擎 → 此刻之后的新计划 + 新校核
        res.Plan = TaskExploder.Explode(cfg);
        res.Plan.Violations.AddRange(vs);
        return res;
    }

    // ── 六策略实现 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 补车：补到建议车数 n*，车从「在籍空闲车」取（跨面不重复占用）；
    /// 池子不够才回落虚拟增援 T+借i，并明记「无实际可调配车辆」——不静默造假车。
    /// </summary>
    private static bool AddTrucks(FaceInput f, RescheduleResult res, List<PlanViolation> vs, HashSet<string> busy)
    {
        int need = Math.Max(0, f.Group.RecommendedTrucks - f.Group.Trucks.Count);
        if (need <= 0) return false;

        var pool = IdlePool(TruckPoolProvider, "卡车", busy);
        int real = 0;
        foreach (var id in pool)
        {
            if (real >= need) break;
            f.Group.Trucks.Add(id); busy.Add(id); real++;
        }
        int virt = need - real;
        for (int i = 0; i < virt; i++) f.Group.Trucks.Add($"T+借{i + 1}");

        if (real > 0)
            res.Notes.Add($"{f.Zone}：从在籍空闲车调入 {real} 辆（{string.Join("/", f.Group.Trucks.TakeLast(real + virt).Take(real))}），补至建议 {f.Group.RecommendedTrucks} 辆。");
        if (virt > 0)
        {
            res.Notes.Add($"{f.Zone}：在籍空闲车不足，尚缺 {virt} 辆按虚拟增援估算（T+借i），需外部调车/租赁落实。");
            vs.Add(V(ViolationSeverity.Warn, ViolationCodes.TruckShortage,
                $"{f.Zone} 无实际可调配车辆 {virt} 辆，计划按虚拟增援估算，落实前该面运力仍不足（铲待车）。"));
        }
        return true;
    }

    /// <summary>
    /// 备机顶替：从「在籍空闲主设备」取一台顶替故障铲；取不到才回落占位名并报警。
    /// 换型号会改变斗容/装车节拍 τ_L，配车数应随之重解（由趟 C 的编组重解承接）。
    /// </summary>
    private static bool ReassignBackup(FaceInput f, double remain, RescheduleResult res, List<PlanViolation> vs, HashSet<string> busyMains)
    {
        string old = f.Group.MainEquipment;
        var pool = IdlePool(ShovelPoolProvider, "电铲", busyMains);
        string? backup = pool.FirstOrDefault();

        if (backup != null)
        {
            f.Group.MainEquipment = backup;
            busyMains.Add(backup);
            res.Notes.Add($"{f.Zone}：主设备 {old} 故障，在籍空闲 {backup} 顶替接管剩余 {remain:0} m³（型号变 → 需重解配车）。");
        }
        else
        {
            f.Group.MainEquipment = old + "·备";
            res.Notes.Add($"{f.Zone}：主设备 {old} 故障，无在籍空闲主设备，按虚拟备机估算接管剩余 {remain:0} m³。");
            vs.Add(V(ViolationSeverity.Warn, ViolationCodes.FleetMismatch,
                $"{f.Zone} 无实际可调配备用主设备，「{f.Group.MainEquipment}」为虚拟备机，班产按原编组估算，落实前存在硬缺口。"));
        }
        return true;
    }

    /// <summary>
    /// 换面：把本面剩余量转入其它采装面。**必须过去向这一关**——
    /// 目标面的去向要接得住搬过去的物料（物料白名单 + 排土库容占容方 + 卸点通过能力），
    /// 否则只是把「无处可排」搬了个地方。逐候选面校核，全不通过返回 false 由调用方降级。
    /// </summary>
    private static bool SwitchFace(ExploderConfig cfg, FaceInput from, List<FaceInput> loadFaces,
                                   double remain, bool haveSinks, RescheduleResult res, List<PlanViolation> vs)
    {
        var mix = from.ResolvedMix;
        var rejected = new List<string>();

        // 候选排序：同主物料优先（去向天然兼容）→ 本日仍有计划量（有料）优先 → 产能富余多者优先
        var candidates = loadFaces
            .Where(o => !ReferenceEquals(o, from))
            .OrderByDescending(o => string.Equals(o.ResolvedMix.PrimaryCode, mix.PrimaryCode, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(o => o.DayTargetM3 > 0)
            .ThenByDescending(o => AvailableCapacityM3(cfg, o) - o.DayTargetM3)
            .ToList();

        foreach (var t in candidates)
        {
            if (!CanAbsorb(cfg, t, mix, remain, haveSinks, out string why))
            {
                rejected.Add($"{t.Zone}（{why}）");
                continue;
            }

            t.DayTargetM3 += remain;
            from.DayTargetM3 = 0;

            double spare = AvailableCapacityM3(cfg, t) - t.DayTargetM3;
            res.Notes.Add($"{from.Zone}：缺料 → 剩余 {remain:0} m³ 切至「{t.Zone}」" +
                          (t.HasDestination ? $"（去向 {t.DestinationName}，已校核物料/库容/卸点能力）" : "（该面暂无去向，随后统一分配）") +
                          (spare < 0 ? $"；注意目标面本时窗产能尚差 {-spare:0} m³，将转次日。" : "。"));
            if (spare < 0)
                vs.Add(V(ViolationSeverity.Warn, ViolationCodes.DayShortfall,
                    $"{t.Zone} 接收换面量后超本时窗产能 {-spare:0} m³，需回摊次日或再补编组。"));
            if (rejected.Count > 0)
                vs.Add(V(ViolationSeverity.Info, ViolationCodes.NoDestination,
                    $"{from.Zone} 换面候选中已排除：{string.Join("、", rejected)}。"));
            return true;
        }

        if (rejected.Count > 0)
            vs.Add(V(ViolationSeverity.Warn, ViolationCodes.MaterialRejected,
                $"{from.Zone} 全部换面候选均不可行：{string.Join("、", rejected)}。"));
        return false;
    }

    /// <summary>
    /// 目标面能否吃下这批物料：① 去向接纳该物料；② 去向在用；
    /// ③ 排土库容（占容方 V容=V实×Kr）够，且要扣掉已投向同一汇的其它面的量；④ 卸点通过能力（吨/时窗）够。
    /// 无去向台账时只做「物料—去向类型」这一层软校核，不阻断（现场常见去向未录入）。
    /// </summary>
    private static bool CanAbsorb(ExploderConfig cfg, FaceInput target, MaterialMix mix, double addM3, bool haveSinks, out string why)
    {
        why = "";
        var sink = ResolveSink(cfg, target);

        if (sink == null)
        {
            if (!haveSinks || !target.HasDestination) return true;   // 去向未录 → 不做硬校核，交趟 C 统一分配
            why = "去向不在登记簿内";
            return false;
        }
        if (!sink.IsActive) { why = $"去向 {sink.Name} 状态 {sink.Status}"; return false; }

        // ① 物料白名单
        foreach (var (spec, m3) in mix.Split(addM3))
        {
            if (m3 <= 1e-6) continue;
            if (!sink.Accepts(spec)) { why = $"{sink.Name} 不接纳{spec.Name}"; return false; }
        }

        // ② 剩余库容（只有排弃类去向吃库容；已投向同一汇的其它面先占坑）
        if (sink.IsDumping && sink.IsCapacityLimited)
        {
            double committed = cfg.Faces
                .Where(f => f.Process == ProcessType.Load && ReferenceEquals(ResolveSink(cfg, f), sink))
                .Sum(f => f.WasteDumpM3);
            double add = mix.Split(addM3).Where(x => !x.Spec.IsOre).Sum(x => x.Spec.ToDumpM3(x.InSituM3));
            if (committed + add > sink.RemainingM3 + 1e-6)
            {
                why = $"{sink.Name} 库容不足（需 {(committed + add) / 1e4:0.##}万m³ > 余 {sink.RemainingM3 / 1e4:0.##}万m³）";
                return false;
            }
        }

        // ③ 卸点通过能力（按吨量 / 本时窗有效小时）
        double hours = RemainingHours(cfg);
        double capT = sink.ThroughputCapT(hours);
        if (!double.IsInfinity(capT))
        {
            double committedT = cfg.Faces
                .Where(f => f.Process == ProcessType.Load && ReferenceEquals(ResolveSink(cfg, f), sink))
                .Sum(f => f.TargetTonnageT);
            double addT = mix.ToTonnage(addM3);
            if (committedT + addT > capT + 1e-6)
            {
                why = $"{sink.Name} 卸点能力不足（需 {(committedT + addT) / 1e4:0.##}万t > 上限 {capT / 1e4:0.##}万t/{hours:0.#}h）";
                return false;
            }
        }
        return true;
    }

    /// <summary>削目标到本时窗产能上限，余量转次日。</summary>
    private static void ReduceTarget(ExploderConfig cfg, FaceInput f, double remain, RescheduleResult res, string prefix)
    {
        double cap = AvailableCapacityM3(cfg, f);     // 剩余时窗内本面产能上限
        if (remain > cap && cap > 0)
        {
            res.Notes.Add($"{f.Zone}：{prefix}目标由 {remain:0} 削至产能上限 {cap:0} m³，余量 {remain - cap:0} m³ 转次日。");
            f.DayTargetM3 = Math.Round(cap);
        }
        else if (prefix.Length > 0)
        {
            res.Notes.Add($"{f.Zone}：{prefix}剩余 {remain:0} m³ 顺延至后续班次。");
        }
    }

    // ── 支撑函数 ───────────────────────────────────────────────────────────

    private static AdjustStrategy Strat(IReadOnlyDictionary<string, AdjustStrategy> map, FaceInput f)
        => map.TryGetValue(f.Zone, out var s) ? s : AdjustStrategy.RollForward;

    /// <summary>
    /// 全盘降效系数：优先按实绩测算（Σ实际方量 ÷ Σ(编组班产×实际工时)），测不出才用经验常量 0.8。
    /// 实测口径的好处：天气影响是可观测的，"今天到现在只干出七成"比拍一个 0.8 更硬。
    /// </summary>
    private static double EstimateDerate(IReadOnlyList<ProductionTask> tasks, List<string> zones, out string basis)
    {
        var zoneSet = new HashSet<string>(zones, StringComparer.OrdinalIgnoreCase);
        double act = 0, exp = 0;
        foreach (var t in tasks)
        {
            if (t.Process is not (ProcessType.Load or ProcessType.Dump)) continue;
            if (zoneSet.Count > 0 && !zoneSet.Contains(t.WorkZone)) continue;
            if (t.ActualHours <= 1e-6 || t.Group.GroupCapacityM3PerH <= 1e-6) continue;
            act += t.ActualVolumeM3;
            exp += t.Group.GroupCapacityM3PerH * t.ActualHours;
        }
        if (exp > 1e-6)
        {
            double k = Math.Clamp(act / exp, MinDerateFactor, 1.0);
            basis = $"按实绩测算：已完成 {act:0} m³ ÷ 应完成 {exp:0} m³";
            return k;
        }
        basis = "无可用实绩，按天气/缺勤经验折减 15%~25% 取中值";
        return DefaultDerateFactor;
    }

    /// <summary>作业面 → 去向节点：先认 DestinationId，再按名字反查登记簿。</summary>
    private static SinkNode? ResolveSink(ExploderConfig cfg, FaceInput f)
    {
        var reg = cfg.Sinks;
        if (reg == null) return null;
        if (!string.IsNullOrWhiteSpace(f.DestinationId))
        {
            var byId = reg.Find(f.DestinationId);
            if (byId != null) return byId;
        }
        if (!string.IsNullOrWhiteSpace(f.DestinationName))
            return reg.All.FirstOrDefault(s => string.Equals(s.Name, f.DestinationName, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>某面在 fromHour..24 时窗内、扣检修/爆破后的产能上限（用于 ReduceTarget 削峰）。</summary>
    private static double AvailableCapacityM3(ExploderConfig cfg, FaceInput f)
    {
        double cap = Math.Max(1e-6, f.Group.GroupCapacityM3PerH), total = 0;
        var blasts = cfg.BlastWindows();
        foreach (var sh in cfg.Shifts)
        {
            double ws = Math.Max(sh.Start, cfg.FromHour), we = sh.End;
            if (sh.Start > 0) ws += cfg.HandoverRampH;
            if (we <= ws) continue;

            // 与装箱同一口径：逐炮扣清场，一个班只用得上最长的那一段（见 TaskExploder.WorkWindow）。
            // 这里若按"全部段之和"算，削峰会以为还有一段能用，削出来的目标装箱装不下。
            var segs = BlastWindow.Subtract(ws, we, blasts);
            if (segs.Count == 0) continue;
            total += segs.Max(s => s.End - s.Start) * cap;
        }
        return total;
    }

    /// <summary>本次重排覆盖的有效作业小时（卸点通过能力按此折吨量上限）。</summary>
    private static double RemainingHours(ExploderConfig cfg)
    {
        double h = 0;
        foreach (var sh in cfg.Shifts) h += Math.Max(0, sh.End - Math.Max(sh.Start, cfg.FromHour));
        if (h <= 1e-6) h = cfg.FromHour > 0 ? Math.Max(0, 24 - cfg.FromHour) : 24;
        return h;
    }

    /// <summary>
    /// 在籍空闲设备：优先取宿主注入的台账池，未注入时软读取样例台账 Roster（同程序集反射，容错回落）。
    /// 「空闲」= 在籍池 − 本盘子已占用。取不到任何在籍数据时返回空表 → 上层回落虚拟增援并明记。
    /// </summary>
    private static List<string> IdlePool(Func<IEnumerable<string>?>? provider, string category, HashSet<string> busy)
    {
        IEnumerable<string>? pool = null;
        try { pool = provider?.Invoke(); } catch { pool = null; }
        pool ??= RosterOf(category);
        return pool.Where(id => !string.IsNullOrWhiteSpace(id) && !busy.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 按类别取【全矿在籍】可调配设备：走 <see cref="ProductionPlanContext.FleetByCategoryLabel"/>
    /// （设备台账优先、样例花名册兜底）。
    /// <b>不能用当日出勤清单</b>——那上面的设备本来就都在干活，取出来必然全是"已占用"，
    /// 补车与备机顶替就永远只能落到虚拟增援上。
    /// </summary>
    private static IEnumerable<string> RosterOf(string category)
    {
        try { return ProductionPlanContext.FleetByCategoryLabel(category); }
        catch { return Array.Empty<string>(); }
    }

    private static PlanViolation V(ViolationSeverity sev, string code, string msg)
        => new() { Severity = sev, Code = code, Message = msg };

    /// <summary>
    /// 深拷贝盘子。**必须整份带走**：物料构成/去向/运距/去向登记簿/配煤标准 少带一样，
    /// 重排出来的计划就会退化成「无物料无去向」的空壳（历史 bug：只拷了 Material/DayTarget/Group）。
    /// 去向登记簿也要深拷（Clone），否则重排推演会把库容扣到原盘子上。
    /// </summary>
    private static ExploderConfig CloneConfig(ExploderConfig s) => new()
    {
        DateLabel = s.DateLabel, IdPrefix = s.IdPrefix, NowHour = s.NowHour, FromHour = s.FromHour,
        BlastStart = s.BlastStart, BlastEnd = s.BlastEnd, HandoverRampH = s.HandoverRampH,
        EffHoursPerDay = s.EffHoursPerDay, WeatherDeratePct = s.WeatherDeratePct,
        LoadDeratePct = s.LoadDeratePct, HaulDeratePct = s.HaulDeratePct, DumpDeratePct = s.DumpDeratePct,
        MinPreparedDays = s.MinPreparedDays, Organization = s.Organization,
        // 有效工时/降效/备采下限/组织策略是**盘子级**的口径，重排必须整份带走：
        // 少带一样，重排出来的计划就会按另一套闸算面日产能，与原盘子对不上账。
        // 停产时窗整份带走：只拷那对标量，重排出来的计划就只认最早一炮，
        // 后面几炮的清场在重排里又消失一次（原盘子刚补上的那件事）
        Blasts = s.Blasts.Select(b => new BlastWindow(b.Start, b.End, b.Label)).ToList(),
        Shifts = s.Shifts.Select(x => new ShiftWindow(x.Name, x.Start, x.End)).ToList(),
        Faces = s.Faces.Select(CloneFace).ToList(),
        Drills = s.Drills.Select(x => new DrillInput { EquipId = x.EquipId, Zone = x.Zone, BenchElevationM = x.BenchElevationM, Start = x.Start, End = x.End }).ToList(),
        Maintenance = s.Maintenance.Select(x => new MaintenanceWindow { EquipId = x.EquipId, Start = x.Start, End = x.End, Label = x.Label }).ToList(),
        Blend = s.Blend == null ? null : new BlendStandard
        {
            MaxAshPct = s.Blend.MaxAshPct, MinCalorificMJkg = s.Blend.MinCalorificMJkg,
            MaxSulfurPct = s.Blend.MaxSulfurPct,
        },
        Sinks = s.Sinks?.Clone() ?? new SinkRegistry(),
        EnforceMassBalance = s.EnforceMassBalance,
    };

    private static FaceInput CloneFace(FaceInput f) => new()
    {
        Zone = f.Zone, BenchElevationM = f.BenchElevationM, EngineeringPositionId = f.EngineeringPositionId,
        Material = f.Material, MaterialCode = f.MaterialCode,
        Mix = f.Mix == null ? null : new MaterialMix
        {
            Shares = f.Mix.Shares.Select(x => new MaterialShare { MaterialCode = x.MaterialCode, Fraction = x.Fraction }).ToList(),
        },
        DestinationId = f.DestinationId, DestinationName = f.DestinationName, DestinationKind = f.DestinationKind,
        HaulDistanceKm = f.HaulDistanceKm, EquivHaulKm = f.EquivHaulKm,
        DayTargetM3 = f.DayTargetM3, Quality = f.Quality, Process = f.Process,
        ShovelModelPref = f.ShovelModelPref, DerivedFromInbound = f.DerivedFromInbound,
        Group = f.Group.Clone(),
    };
}
