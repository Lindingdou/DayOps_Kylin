// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/DispatchEngine.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Gantt;                            // DailyGanttModel.NeedsDestination：与甘特/任务书共用同一条「哪些工序必须有卸点」口径
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;   // 消歧 System.Threading.Tasks.TaskStatus

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  派车引擎 —— 把「班次任务」下沉到「车次指令」，并管住下达这道闸门。
//
//  四件事：
//   A. 车次展开（固定配车）：任务 → N 条 DispatchOrder。
//   B. 动态派车：卡车卸完实时指派下一台铲（最小铲饱和度 / 最早可装车）。
//   C. 签发前校验：缺去向、缺主设备、有 Error 级校核 ⇒ 不得下达。
//   D. 事件驱动重排：故障 / 卸点拥堵 / 实绩偏差 → 判定是否触发 TaskRescheduler。
//
//  ── A. 车次展开公式（编组理论标准展开式，符号沿用 FleetMatcher 与理论报告 §3）──
//    τ_L  装车节拍 min/车 = m·t_s   （m = 每车斗数 = W_t/(V_b·ρ松·η_b)，由 FleetMatcher 解出）
//    T_c  循环时间 min     = t_装 + 60·L_eq/v_重 + t_卸 + 60·L_eq/v_空 + t_调
//
//    ★ 第 i 台车（i 从 1 起）第 k 趟的装车时刻：
//          s(i,k) = 起始 + (i−1)·τ_L + (k−1)·T_c
//
//      (i−1)·τ_L 这个错峰项不能省：n 台车同时压到铲下就得排队，现场是按装车节拍
//      依次进场的；错峰后展开出来的车次数才与匹配系数 MF = n·τ_L/T_c 的物理含义自洽。
//
//    车次数取两条路的小者（两者都是上界，谁先见底听谁的）：
//      时间法 N_time = Σ_i ⌊(有效时长 − (i−1)τ_L − τ_L − t_重车) / T_c⌋ + 1
//              （一趟算数的条件是**能把料卸掉**：装完 + 重车行驶 ≤ 班末；空车返程可以压过班末）
//      量  法 N_vol  = ⌈目标吨量 / 单车载重⌉
//
//  ── 混采面：一条任务两条线 ────────────────────────────────────────────────
//    一台铲同一时窗挖「煤7∶岩3」，煤去破碎站、岩去内排场：车队跑的是两条运距不同的线。
//    N 趟按【车次份额】分摊到各线（TruckTripPlanner.AllocateTripLegs，交错分配而非前后分段），
//    每趟按它自己那条线的 T_c 往后推、按它自己那条线填物料/卸点/运距，
//    实方与松方按该趟拉的那种物料折算（吨量是守恒量，故载重 W_t 与线无关）。
//
//  ── 与 Gantt/TruckTripPlanner 的关系（同一物理模型，两种口径，不许分家）──
//    · 求解层【完全复用】：去向 SinkOf / 运距 LegOf / 编组 MatchOf / 运输线 LegPlanOf /
//      车次分摊 AllocateTripLegs 一律调 TruckTripPlanner，连缓存都共用它那一份。
//      T_c、τ_L、等效运距三个数在甘特与派车单上必须是同一个值；
//      分摊函数更必须是同一个——两边各写一套，立刻就会出现"图上第 3 趟去破碎站、单子上去排土场"。
//      （AllocateTripLegs 的序列具前缀稳定性，故两边车次数不同也不影响"第 k 趟"指同一条线。）
//    · 计数口径【刻意不同】，因为两者交付物不同：
//        甘特 Trips()  —— 画图口径：剩得下半趟就画（s + T_c/2 ≤ 班末），末趟允许截断（Partial=true），
//                        目的是让"这台车这段时间在跑几趟"看得见，画半根条不算撒谎。
//        本引擎 Expand —— 单据口径：必须**能把料卸掉**才算一趟，且额外受量法 N_vol 封顶。
//                        派车指令是承诺，半趟不是一车料；备采量只够 7 趟就不能签发 8 趟。
//      故同一条任务上 Expand 的车次数 ≤ Trips 的车次数，这个差是口径差不是算法错。
//
//  ── 载重的口径（本文件最容易搞错的地方）──
//    单车载重 W_t 是【吨】。吨量是实方/松方/占容方三口径间唯一的守恒量，故一律以吨为主：
//      · 实方 V实 = W_t / ρ实      （回记作业量、扣备采量用）
//      · 松方 V松 = V实 × Ks       （车厢容积校核用——卡车拉的是爆破后的松散料）
//      · 占容 V容 = V实 × Kr       （排土库容用）
//    ρ实 / Ks / Kr 一律经 MaterialCatalog，本文件不出现任何密度常量。
//    车厢容积校核：dispatch_rule 台账没有车厢容积列，故当前以【载重】为准（矿卡多为重载受限），
//    松方量随指令一并落盘（PayloadLooseM3），台账补上容积列后即可在此加一条 min() 取小。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>展不开车次的四种原因。<b>判定条件只在引擎里，界面不许再判一遍。</b></summary>
public enum TripSkipKind
{
    /// <summary>缺去向 —— 没有卸点就没有运距，没有运距就没有 T_c。</summary>
    NoDestination,
    /// <summary>运距未解出（路网 / 手填 / 兜底三层全空）。</summary>
    NoHaulDistance,
    /// <summary>编组未解出（τ_L / T_c / 载重）。</summary>
    NoFleetSolution,
    /// <summary>尚未配车（<c>Group.Trucks</c> 为空）——有铲没车，排不出趟。</summary>
    NoTrucks,
}

/// <summary>一条展不开的任务：是哪一笔、被哪一条卡的、引擎原话。</summary>
public sealed class TripSkip
{
    public string TaskId { get; set; } = "";
    public string WorkZone { get; set; } = "";
    public TripSkipKind Kind { get; set; }
    /// <summary>引擎给的原话（界面直接展示，不改写）。</summary>
    public string Reason { get; set; } = "";
    /// <summary>建议配车数（<see cref="TripSkipKind.NoTrucks"/> 时有意义）。</summary>
    public int RecommendedTrucks { get; set; }
}

/// <summary>车次展开/动态派车的产物。</summary>
public sealed class DispatchPlan
{
    public List<DispatchOrder> Orders { get; set; } = new();
    /// <summary>人读的展开说明（每条任务一行；解不出的会说清为什么）。</summary>
    public List<string> Notes { get; set; } = new();

    /// <summary>
    /// 展不开车次的任务，**结构化**记一笔（2026-08-20 补）。
    ///
    /// <para><b>为什么不让界面自己判</b>：展不开只有 4 种原因（缺去向 / 运距解不出 /
    /// 编组解不出 / 尚未配车），判定条件全在 <see cref="DispatchEngine.Solve"/> 与
    /// <see cref="DispatchEngine.ExpandOne"/> 里。界面再写一份"我猜它是被什么卡的"，
    /// 立刻就会出现<b>两处说同一件事而说的不一样</b> —— 实测就踩了：
    /// 空态面板说「解不出单车载重」，而下面引擎自己逐条写着「尚未配车」。
    /// 原因只许有一个出处，界面按它分组即可。</para>
    /// </summary>
    public List<TripSkip> Skips { get; set; } = new();
    /// <summary>本次采用的派车规则。</summary>
    public DispatchRuleKind Rule { get; set; } = DispatchRuleKind.FixedAssignment;

    /// <summary>动态派车的目标函数值：Σ卡车等待 min（车在铲下排队）。</summary>
    public double TruckWaitMin { get; set; }
    /// <summary>Σ电铲等待 min（铲空等车）。</summary>
    public double ShovelWaitMin { get; set; }

    public int TripCount => Orders.Count;
    public double TotalTonnageT => Orders.Sum(o => o.PayloadT);
    public double TotalLooseM3 => Orders.Sum(o => o.PayloadLooseM3);
    public double TotalWorkTKm => Orders.Sum(o => o.TransportWorkTKm);
    public int TruckCount => Orders.Select(o => o.TruckId).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public int ShovelCount => Orders.Select(o => o.ShovelId).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public string Summary => Orders.Count == 0
        ? "未展开出车次（原因见展开说明）"
        : $"{Rule.Label()}：{TripCount} 车次 · {TruckCount} 台车 / {ShovelCount} 台铲 · "
          + $"{TotalTonnageT / 1e4:0.###} 万t · 运输功 {TotalWorkTKm / 1e4:0.###} 万t·km"
          + (Rule == DispatchRuleKind.FixedAssignment ? "" : $" · 等待 车{TruckWaitMin:0}min + 铲{ShovelWaitMin:0}min");
}

/// <summary>签发前校验结论。<see cref="Blocks"/> 非空即不得下达。</summary>
public sealed class IssueCheck
{
    /// <summary>阻止项（Error 级校核 / 缺去向 / 缺主设备）。</summary>
    public List<PlanViolation> Blocks { get; set; } = new();
    /// <summary>提醒项（不阻止下达，但调度员应当知道）。</summary>
    public List<PlanViolation> Warnings { get; set; } = new();
    /// <summary>本次参与校验的任务数。</summary>
    public int TaskCount { get; set; }

    public bool CanIssue => Blocks.Count == 0;

    public string Summary => CanIssue
        ? $"校验通过（{TaskCount} 项任务" + (Warnings.Count > 0 ? $" · {Warnings.Count} 条提醒）" : "）")
        : $"校验未通过：{Blocks.Count} 条阻止项，任务不得下达";

    /// <summary>弹窗直接用的多行文案。</summary>
    public string BlockText => string.Join(Environment.NewLine,
        Blocks.Select((b, i) => $"{i + 1}. [{b.Code}]{(string.IsNullOrWhiteSpace(b.TaskId) ? "" : $" {b.TaskId}")} {b.Message}"));

    public string WarnText => string.Join(Environment.NewLine,
        Warnings.Select((b, i) => $"{i + 1}. [{b.Code}]{(string.IsNullOrWhiteSpace(b.TaskId) ? "" : $" {b.TaskId}")} {b.Message}"));
}

/// <summary>事件驱动重排的建议。</summary>
public sealed class DispatchAdvice
{
    /// <summary>是否达到触发重排的门槛。</summary>
    public bool Triggered { get; set; }
    /// <summary>触发源文案（"WK-10 故障 1.2h"）。</summary>
    public string Trigger { get; set; } = "";
    /// <summary>判定依据（为什么触发 / 为什么不触发）。</summary>
    public string Judgement { get; set; } = "";
    /// <summary>调整建议（逐条，人读）。</summary>
    public List<string> Suggestions { get; set; } = new();
    /// <summary>重排后的新计划（未触发时为 null）。</summary>
    public RescheduleResult? Plan { get; set; }

    public string Summary
    {
        get
        {
            var head = $"{Trigger}：{Judgement}";
            if (!Triggered) return head;
            var body = Suggestions.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, Suggestions.Select(s => "· " + s)) : "";
            var tail = Plan != null ? Environment.NewLine + "· " + Plan.Summary : "";
            return head + body + tail;
        }
    }
}

public static class DispatchEngine
{
    // ── 展开护栏（脏台账保护，不是业务常数）────────────────────────────────
    /// <summary>单车单任务车次上限。超过说明 T_c 异常小（台账脏数据），展开出来只是噪声。</summary>
    private const int MaxTripsPerTruck = 60;
    /// <summary>单次展开的车次总量上限（防一次展开几万条把 UI 卡死）。</summary>
    private const int MaxTripsTotal = 4000;

    /// <summary>
    /// 动态派车时每台铲预生成的「车次→运输线」分摊序列长度。
    /// 动态派车下一台铲的车次由全池卡车贡献，趟数比固定配车多得多；
    /// 用尽后按该序列回绕（份额是周期性的，回绕不改变各线的占比）。
    /// </summary>
    private const int DynamicAllocLen = 512;

    /// <summary>
    /// 动态派车的采出配比容差（份额，绝对值）。某台铲的累计吨量份额超出其目标份额
    /// 这么多时，该铲降级排在后面——否则"哪台铲空就往哪派"会把配矿/配煤比例带跑偏。
    /// </summary>
    private const double MixShareTolerance = 0.05;

    /// <summary>
    /// 换了一天 / 载入新快照 / 台账刷新后丢缓存。
    /// 求解层与甘特共用 <see cref="TruckTripPlanner"/> 的那一份缓存，故直接转调它——
    /// 两边各留一份缓存，就会出现「甘特刷新了、派车单还在用旧运距」的错位。
    /// </summary>
    public static void Invalidate() => TruckTripPlanner.Invalidate();

    // ═════════════════════════════════════════════════════════════════════════
    //  A. 车次展开（固定配车）
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 把一批任务展开成派车单。<paramref name="shift"/> 为空则全天；
    /// <paramref name="rule"/> 为 FixedAssignment 走固定配车展开，否则走动态派车模拟。
    /// </summary>
    public static DispatchPlan Expand(IEnumerable<ProductionTask> tasks, string planDate,
                                      string? shift = null,
                                      DispatchRuleKind rule = DispatchRuleKind.FixedAssignment)
    {
        var list = (tasks ?? Enumerable.Empty<ProductionTask>())
            .Where(t => t.Process == ProcessType.Load && t.TargetVolumeM3 > 1)
            .Where(t => string.IsNullOrWhiteSpace(shift) || string.Equals(t.Shift, shift, StringComparison.Ordinal))
            .OrderBy(t => t.StartHour).ThenBy(t => t.Group.MainEquipment, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return rule == DispatchRuleKind.FixedAssignment
            ? ExpandFixed(list, planDate)
            : ExpandDynamic(list, planDate, rule);
    }

    /// <summary>固定配车展开：卡车绑定一台铲，按 s(i,k) = 起始 + (i−1)τ_L + (k−1)T_c 排车次。</summary>
    private static DispatchPlan ExpandFixed(List<ProductionTask> tasks, string planDate)
    {
        var plan = new DispatchPlan { Rule = DispatchRuleKind.FixedAssignment };

        foreach (var t in tasks)
        {
            if (plan.Orders.Count >= MaxTripsTotal)
            {
                plan.Notes.Add($"车次总数已达上限 {MaxTripsTotal}，其余任务未展开（请缩小班次范围）。");
                break;
            }

            var ctx = Solve(t);
            if (ctx.Fail != null)
            {
                plan.Notes.Add($"{t.Id} {t.WorkZone}：{ctx.Fail}");
                plan.Skips.Add(new TripSkip { TaskId = t.Id, WorkZone = t.WorkZone, Kind = ctx.FailKind, Reason = ctx.Fail });
                continue;
            }

            var orders = ExpandOne(t, ctx, planDate, out string note);
            plan.Orders.AddRange(orders);
            plan.Notes.Add(note);
            // 解得出却一趟也排不出来 —— 目前只有「尚未配车」这一种
            if (orders.Count == 0 && t.Group.Trucks.Count == 0)
                plan.Skips.Add(new TripSkip
                {
                    TaskId = t.Id, WorkZone = t.WorkZone, Kind = TripSkipKind.NoTrucks,
                    Reason = note, RecommendedTrucks = t.Group.RecommendedTrucks,
                });
        }

        if (plan.Orders.Count == 0 && plan.Notes.Count == 0)
            plan.Notes.Add("本班无采装任务可展开车次。");
        return plan;
    }

    /// <summary>展开单条任务的车次。混采任务按车次份额分摊到各条运输线（与甘特同一套分摊函数）。</summary>
    private static List<DispatchOrder> ExpandOne(ProductionTask t, SolveCtx ctx, string planDate, out string note)
    {
        var result = new List<DispatchOrder>();
        var m = ctx.Match!;
        var leg = ctx.Leg!;

        double taktW = Math.Max(0.05, m.LoadTaktMin);          // τ_L(加权) —— 错峰进场用
        double tcW = Math.Max(taktW, m.CycleTimeMin);          // T_c(加权)
        double winMin = Math.Max(0, (t.EndHour - t.StartHour) * 60.0);

        var trucks = t.Group.Trucks.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (trucks.Count == 0)
        {
            note = $"{t.Id} {t.WorkZone}：尚未配车（建议 {t.Group.RecommendedTrucks} 台），无法展开车次——请先在「作业面台账 / 班组派工」配车。";
            return result;
        }

        // ★ 运输线台账 + 车次分摊：与甘特【共用同一个函数】（TruckTripPlanner）。
        //   两边各写一套分摊，立刻就会出现"图上第 3 趟去破碎站、单子上第 3 趟去排土场"。
        var lines = LinesOf(t, m, leg);
        var alloc = TruckTripPlanner.AllocateTripLegs(lines, MaxTripsPerTruck + 2);
        bool multi = lines.Count > 1;

        // ── 时间法：逐车算能跑几趟（一趟算数的条件是**能把料卸掉**）──
        //    每趟按它自己那条线的 T_c 往后推，不是拿加权值均摊——两条线运距不同，节拍本就不同。
        var slots = new List<(string Truck, double LoadMin, int Line)>();
        for (int i = 0; i < trucks.Count; i++)
        {
            double startMin = i * taktW;                       // (i−1)·τ_L 错峰进场
            for (int k = 1; k <= MaxTripsPerTruck; k++)
            {
                int li = alloc[k - 1];
                var ln = lines[li];
                double taktK = Math.Max(0.05, ln.TaktMin);
                double tcK = Math.Max(taktK, ln.CycleMin);
                double loadedK = ln.LoadedMin > 0 ? ln.LoadedMin : Math.Max(0, (tcK - taktK) / 2);

                if (startMin + taktK + loadedK > winMin + 1e-6) break;
                slots.Add((trucks[i], startMin, li));
                startMin += tcK;                               // s(i,k+1) = s(i,k) + T_c(本趟那条线)
            }
        }
        if (slots.Count == 0)
        {
            note = $"{t.Id} {t.WorkZone}：时窗 {DispatchClock.Hm(t.StartHour)}–{DispatchClock.Hm(t.EndHour)}（{winMin:0}min）不足一个循环 T_c {tcW:0.0}min，本班无完整车次。";
            return result;
        }

        // ── 单车载重 W_t ──
        double payloadT = PayloadFromRule(m.RuleCaption);
        string payloadSrc = "编组规则台账";
        if (payloadT <= 0)
        {
            // 台账没解出载重 ⇒ 按「目标吨量 ÷ 时间法车次数」反推，两条路自洽（此时量法不再另设上界）
            payloadT = t.TargetTonnageT / Math.Max(1, slots.Count);
            payloadSrc = "按目标吨量反推";
        }
        payloadT = Math.Max(0.1, payloadT);

        // ── 量法上界，与时间法取小 ──
        int nVol = (int)Math.Ceiling(t.TargetTonnageT / payloadT - 1e-6);
        int n = Math.Max(0, Math.Min(slots.Count, Math.Max(1, nVol)));

        // 削哪些：按装车时刻排序后砍最晚的几趟（现场也是班末不再发车）。
        // 每台车留下的都是它最早的那几趟 ⇒ 仍是分摊序列的前缀 ⇒ 与甘特的第 k 趟指同一条线。
        var taken = slots.OrderBy(x => x.LoadMin).ThenBy(x => x.Truck, StringComparer.OrdinalIgnoreCase).Take(n).ToList();

        var mix = t.ResolvedMix.Normalized();
        double rhoBlend = Math.Max(0.1, mix.BlendedDensity);   // ρ实（混合物）—— 单去向口径用它
        string key = TaskKey.Of(t, planDate);
        double haulKmMain = t.EffectiveHaulKm > 1e-6 ? t.EffectiveHaulKm : leg.EquivKm;

        // 每车重新编趟号（1 起，按该车的装车时刻顺序）
        foreach (var g in taken.GroupBy(x => x.Truck, StringComparer.OrdinalIgnoreCase))
        {
            int trip = 0;
            foreach (var slot in g.OrderBy(x => x.LoadMin))
            {
                trip++;
                var ln = lines[slot.Line];
                double taktK = Math.Max(0.05, ln.TaktMin);
                double tcK = Math.Max(taktK, ln.CycleMin);
                double loadedK = ln.LoadedMin > 0 ? ln.LoadedMin : Math.Max(0, (tcK - taktK) / 2);
                double loadHour = t.StartHour + slot.LoadMin / 60.0;

                // 载重是吨（守恒量），三口径按【本趟拉的那种物料】折算：
                // 混采时煤车与岩车的实方/松方本就不同，用混合密度会把两种车算成一种。
                double rho = multi ? Math.Max(0.1, ln.Spec.InSituDensityTPerM3) : rhoBlend;
                double inSitu = payloadT / rho;
                double loose = multi ? ln.Spec.ToLooseM3(inSitu) : mix.ToLooseM3(inSitu);

                result.Add(new DispatchOrder
                {
                    OrderId = $"DO-{TaskKey.DateTag(planDate)}-{TaskKey.ShiftTag(t.Shift)}-{g.Key}-{trip:000}",
                    TruckId = g.Key,
                    ShovelId = t.Group.MainEquipment,
                    TaskId = t.Id,
                    StableKey = key,
                    TripNo = trip,
                    PlanDate = planDate,
                    Shift = t.Shift,
                    WorkZone = t.WorkZone,
                    MaterialCode = multi ? ln.MaterialCode
                                 : string.IsNullOrWhiteSpace(t.MaterialCode) ? mix.PrimaryCode : t.MaterialCode,
                    SinkId = multi ? ln.SinkId : t.DestinationId,
                    SinkName = multi ? ln.SinkName : t.DestinationName,
                    SinkKind = multi ? ln.SinkKind : t.DestinationKind,
                    PlannedLoadHour = Math.Round(loadHour, 3),
                    PlannedDumpHour = Math.Round(loadHour + (taktK + loadedK) / 60.0, 3),
                    CycleMin = Math.Round(multi ? tcK : tcW, 1),
                    PayloadT = Math.Round(payloadT, 2),
                    PayloadLooseM3 = Math.Round(loose, 2),
                    PayloadInSituM3 = Math.Round(inSitu, 2),
                    HaulKm = Math.Round(multi && ln.HaulKm > 1e-6 ? ln.HaulKm : haulKmMain, 3),
                    Status = DispatchOrderStatus.Planned,
                });
            }
        }

        string cut = n < slots.Count ? $"（时间法可跑 {slots.Count} 趟，按目标量 {nVol} 趟取小）" : "";
        string route = multi
            ? "混采 " + string.Join(" ｜ ", lines.Select(l =>
                  $"{l.Spec.Name}→{(l.SinkText.Length > 0 ? l.SinkText : "（未定）")} {l.HaulKm:0.##}km T_c {l.CycleMin:0.0}min"))
            : (string.IsNullOrWhiteSpace(t.DestinationName) ? t.DestinationId : t.DestinationName);

        string split = multi
            ? "；实发 " + string.Join(" / ", lines.Select(l =>
                  $"{l.Spec.Name} {result.Count(o => string.Equals(o.MaterialCode, l.MaterialCode, StringComparison.OrdinalIgnoreCase))} 趟"
                + $"（份额 {l.TripShare * 100:0.#}%）"))
            : "";

        note = $"{t.Id} {t.WorkZone} → {route}："
             + $"{trucksCaption(trucks)} · τ_L {taktW:0.0}min · T_c {tcW:0.0}min{(multi ? "（加权）" : "")} · 载重 {payloadT:0.#}t（{payloadSrc}）"
             + $" → {result.Count} 车次{cut}，合计 {result.Sum(o => o.PayloadT):0} t / 目标 {t.TargetTonnageT:0} t{split}。";
        return result;

        static string trucksCaption(List<string> ts) => $"{ts.Count} 台车（{string.Join("/", ts)}）";
    }

    /// <summary>
    /// 一条任务的运输线台账。走 <see cref="TruckTripPlanner.LegPlanOf"/> —— 与甘特同源；
    /// 万一那边解不出（不该发生：编组已经解出来了），退回主去向的单线口径，绝不让派车单空手。
    /// </summary>
    private static IReadOnlyList<TripLeg> LinesOf(ProductionTask t, FleetMatchResult m, HaulLeg leg)
    {
        IReadOnlyList<TripLeg> plan;
        try { plan = TruckTripPlanner.LegPlanOf(t); }
        catch { plan = Array.Empty<TripLeg>(); }
        if (plan.Count > 0) return plan;

        double takt = Math.Max(0.05, m.LoadTaktMin);
        double tc = Math.Max(takt, m.CycleTimeMin);
        return new List<TripLeg>
        {
            new()
            {
                MaterialCode = string.IsNullOrWhiteSpace(t.MaterialCode) ? t.ResolvedMix.PrimaryCode : t.MaterialCode,
                TripShare = 1, VolumeShare = 1,
                SinkId = t.DestinationId, SinkName = t.DestinationName, SinkKind = t.DestinationKind,
                HaulKm = t.EffectiveHaulKm > 1e-6 ? t.EffectiveHaulKm : leg.EquivKm,
                CycleMin = tc, TaktMin = takt,
                LoadedMin = leg.LoadedMin > 0 ? leg.LoadedMin : Math.Max(0, (tc - takt) / 2),
            }
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  B. 动态派车 —— 卡车卸完实时指派下一台铲
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 动态派车模拟（离散事件）。目标 min Σ(卡车等待 + 铲等待)：
    /// <list type="bullet">
    /// <item><b>最小铲饱和度</b>：把车派给「当前在途车数 / 建议车数 n*」最小的铲——
    ///   饱和度低意味着这台铲正处于"待车"状态，补一台车的边际收益最大。</item>
    /// <item><b>最早可装车</b>：把车派给 max(车到达时刻, 铲空闲时刻) 最小的铲——直接最小化本次等待。</item>
    /// </list>
    /// 两条规则都受【采出配比约束】：某铲累计吨量份额超出其目标份额 <see cref="MixShareTolerance"/>
    /// 时降级排后。没有这条约束，"哪台铲空往哪派"会让煤岩比例、配煤指标全跑偏。
    /// </summary>
    private static DispatchPlan ExpandDynamic(List<ProductionTask> tasks, string planDate, DispatchRuleKind rule)
    {
        var plan = new DispatchPlan { Rule = rule };
        var states = new List<ShovelState>();

        foreach (var t in tasks)
        {
            var ctx = Solve(t);
            if (ctx.Fail != null) { plan.Notes.Add($"{t.Id} {t.WorkZone}：{ctx.Fail}"); continue; }

            var m = ctx.Match!;
            var leg = ctx.Leg!;
            double takt = Math.Max(0.05, m.LoadTaktMin);
            double tc = Math.Max(takt, m.CycleTimeMin);
            double loadedMin = leg.LoadedMin > 0 ? leg.LoadedMin : Math.Max(0, (tc - takt) / 2);

            double payloadT = PayloadFromRule(m.RuleCaption);
            if (payloadT <= 0)
            {
                double winMin = Math.Max(0, (t.EndHour - t.StartHour) * 60.0);
                int rough = Math.Max(1, (int)(winMin / tc) * Math.Max(1, t.Group.Trucks.Count));
                payloadT = Math.Max(0.1, t.TargetTonnageT / rough);
            }

            var mix = t.ResolvedMix.Normalized();

            // ★ 运输线与车次分摊：与固定配车、与甘特【同一个函数】（TruckTripPlanner.AllocateTripLegs）。
            //   动态派车下"第几趟"是按铲累计的（车不绑铲），故分摊序列挂在铲上而不是车上。
            var lines = LinesOf(t, m, leg);
            states.Add(new ShovelState
            {
                Task = t,
                Mix = mix,
                TaktMin = takt,
                CycleMin = tc,
                LoadedMin = loadedMin,
                PayloadT = payloadT,
                TargetT = t.TargetTonnageT,
                Recommended = Math.Max(1, m.OptimalTrucks > 0 ? m.OptimalTrucks : t.Group.RecommendedTrucks),
                FreeAt = t.StartHour * 60.0,
                WindowEnd = t.EndHour * 60.0,
                HaulKm = t.EffectiveHaulKm > 1e-6 ? t.EffectiveHaulKm : leg.EquivKm,
                StableKey = TaskKey.Of(t, planDate),
                Lines = lines,
                Alloc = TruckTripPlanner.AllocateTripLegs(lines, DynamicAllocLen),
            });
        }

        if (states.Count == 0)
        {
            plan.Notes.Add("无可参与动态派车的采装任务（全部未解出运距/编组，见上方说明）。");
            return plan;
        }

        // 卡车池 = 全部任务上的实配车去重（动态派车的前提就是车不再绑死在一台铲上）
        var trucks = states.SelectMany(s => s.Task.Group.Trucks)
                           .Where(x => !string.IsNullOrWhiteSpace(x))
                           .Distinct(StringComparer.OrdinalIgnoreCase)
                           .Select(id => new TruckState { Id = id, FreeAt = states.Min(s => s.FreeAt) })
                           .ToList();
        if (trucks.Count == 0)
        {
            plan.Notes.Add("卡车池为空（各面均未配车），动态派车无从执行。");
            return plan;
        }

        double totalTargetT = Math.Max(1e-6, states.Sum(s => s.TargetT));
        foreach (var s in states) s.TargetShare = s.TargetT / totalTargetT;

        var seq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // 每车累计趟号
        int guard = 0;

        while (guard++ < MaxTripsTotal)
        {
            var truck = trucks.OrderBy(x => x.FreeAt).FirstOrDefault(x => !x.Done);
            if (truck == null) break;

            double assignedT = states.Sum(s => s.AssignedT);
            var pick = Choose(states, truck, rule, assignedT);
            if (pick == null) { truck.Done = true; continue; }

            // 本趟走哪条线（混采面才有区别）：按铲上的分摊序列取下一条，装成了才推进指针
            var line = pick.PeekLine();
            bool multi = pick.Lines.Count > 1;
            double taktK = multi ? Math.Max(0.05, line.TaktMin) : pick.TaktMin;
            double tcK = multi ? Math.Max(taktK, line.CycleMin) : pick.CycleMin;
            double loadedK = multi
                ? (line.LoadedMin > 0 ? line.LoadedMin : Math.Max(0, (tcK - taktK) / 2))
                : pick.LoadedMin;

            double loadStart = Math.Max(truck.FreeAt, pick.FreeAt);
            // 一趟算数的条件同固定配车：装完 + 重车行驶 ≤ 本面时窗末
            if (loadStart + taktK + loadedK > pick.WindowEnd + 1e-6)
            {
                pick.Closed = true;
                if (states.All(s => s.Closed || s.RemainingT <= 1e-6)) { truck.Done = true; }
                continue;
            }

            plan.TruckWaitMin += Math.Max(0, pick.FreeAt - truck.FreeAt);    // 车在铲下排队
            plan.ShovelWaitMin += Math.Max(0, truck.FreeAt - pick.FreeAt);   // 铲空等车

            int trip = seq.TryGetValue(truck.Id, out int v) ? v + 1 : 1;
            seq[truck.Id] = trip;

            var t = pick.Task;
            // 载重是吨（守恒量）；实方/松方按【本趟拉的那种物料】折算，混采时煤车与岩车本就不同
            double rho = multi ? Math.Max(0.1, line.Spec.InSituDensityTPerM3) : Math.Max(0.1, pick.Mix.BlendedDensity);
            double inSitu = pick.PayloadT / rho;
            double loose = multi ? line.Spec.ToLooseM3(inSitu) : pick.Mix.ToLooseM3(inSitu);

            plan.Orders.Add(new DispatchOrder
            {
                OrderId = $"DO-{TaskKey.DateTag(planDate)}-{TaskKey.ShiftTag(t.Shift)}-{truck.Id}-{trip:000}",
                TruckId = truck.Id,
                ShovelId = t.Group.MainEquipment,
                TaskId = t.Id,
                StableKey = pick.StableKey,
                TripNo = trip,
                PlanDate = planDate,
                Shift = t.Shift,
                WorkZone = t.WorkZone,
                MaterialCode = multi ? line.MaterialCode
                             : string.IsNullOrWhiteSpace(t.MaterialCode) ? pick.Mix.PrimaryCode : t.MaterialCode,
                SinkId = multi ? line.SinkId : t.DestinationId,
                SinkName = multi ? line.SinkName : t.DestinationName,
                SinkKind = multi ? line.SinkKind : t.DestinationKind,
                PlannedLoadHour = Math.Round(loadStart / 60.0, 3),
                PlannedDumpHour = Math.Round((loadStart + taktK + loadedK) / 60.0, 3),
                CycleMin = Math.Round(tcK, 1),
                PayloadT = Math.Round(pick.PayloadT, 2),
                PayloadLooseM3 = Math.Round(loose, 2),
                PayloadInSituM3 = Math.Round(inSitu, 2),
                HaulKm = Math.Round(multi && line.HaulKm > 1e-6 ? line.HaulKm : pick.HaulKm, 3),
                Status = DispatchOrderStatus.Planned,
            });

            pick.TakeLine();                                      // 本趟落定 → 分摊指针 +1
            pick.FreeAt = loadStart + taktK;                      // 铲装完这台车才能装下一台
            pick.AssignedT += pick.PayloadT;
            pick.InFlight.Add((truck.Id, loadStart + tcK));
            truck.FreeAt = loadStart + tcK;                       // 车跑完一个 T_c 才回来
        }

        plan.Notes.Add($"动态派车（{rule.Label()}）：{states.Count} 台铲 / {trucks.Count} 台车，"
                     + $"车次 {plan.Orders.Count}，Σ等待 = 车 {plan.TruckWaitMin:0} min + 铲 {plan.ShovelWaitMin:0} min。");
        foreach (var s in states)
            plan.Notes.Add($"  · {s.Task.Group.MainEquipment} {s.Task.WorkZone}：派 {s.AssignedT:0} t / 目标 {s.TargetT:0} t"
                         + $"（份额 {(s.AssignedT / Math.Max(1e-6, plan.TotalTonnageT)) * 100:0.#}% vs 目标 {s.TargetShare * 100:0.#}%）"
                         + (s.Lines.Count > 1
                             ? "；混采分线 " + string.Join(" / ", s.Lines.Select(l =>
                                   $"{l.Spec.Name}→{(l.SinkText.Length > 0 ? l.SinkText : "（未定）")} {l.HaulKm:0.##}km"
                                 + $"（份额 {l.TripShare * 100:0.#}%）"))
                             : ""));
        return plan;
    }

    /// <summary>按规则挑一台铲；受采出配比约束。全不可派返回 null。</summary>
    private static ShovelState? Choose(List<ShovelState> states, TruckState truck, DispatchRuleKind rule, double assignedTotalT)
    {
        var open = states.Where(s => !s.Closed && s.RemainingT > 1e-6 && truck.FreeAt < s.WindowEnd).ToList();
        if (open.Count == 0) return null;

        // 配比守门：已超目标份额 + 容差的铲先剔除；若全超（说明容差设得紧）则不剔除，回落纯规则。
        if (assignedTotalT > 1e-6)
        {
            var within = open.Where(s => s.AssignedT / assignedTotalT <= s.TargetShare + MixShareTolerance).ToList();
            if (within.Count > 0) open = within;
        }

        return rule switch
        {
            // 饱和度 = 在途车数 / 建议车数 n*。在途 = 已派给它、且尚未跑完 T_c 的车。
            DispatchRuleKind.MinShovelSaturation => open
                .OrderBy(s => s.Saturation(truck.FreeAt))
                .ThenBy(s => Math.Max(truck.FreeAt, s.FreeAt))
                .First(),

            // 最早可装车时刻 = max(车到达, 铲空闲)
            DispatchRuleKind.EarliestLoad => open
                .OrderBy(s => Math.Max(truck.FreeAt, s.FreeAt))
                .ThenBy(s => s.Saturation(truck.FreeAt))
                .First(),

            _ => open.OrderBy(s => s.FreeAt).First(),
        };
    }

    private sealed class ShovelState
    {
        public ProductionTask Task = null!;
        public MaterialMix Mix = new();
        public string StableKey = "";
        public double TaktMin, CycleMin, LoadedMin, PayloadT, TargetT, AssignedT, FreeAt, WindowEnd, HaulKm, TargetShare;
        public int Recommended = 1;
        public bool Closed;
        public List<(string Truck, double BackAt)> InFlight = new();

        // ── 混采面的多条运输线 + 车次分摊序列（与甘特/固定配车共用同一个分摊函数）──
        public IReadOnlyList<TripLeg> Lines = Array.Empty<TripLeg>();
        public int[] Alloc = Array.Empty<int>();
        /// <summary>本铲已派出的车次数 —— 分摊序列的游标。</summary>
        public int Served;

        public double RemainingT => Math.Max(0, TargetT - AssignedT);

        /// <summary>下一趟该走哪条线（不推进游标；装不成就不算一趟）。序列用尽后回绕，份额仍保持。</summary>
        public TripLeg PeekLine()
        {
            if (Lines.Count == 0) return new TripLeg();
            if (Alloc.Length == 0) return Lines[0];
            return Lines[Alloc[Served % Alloc.Length]];
        }

        /// <summary>本趟落定，游标 +1。</summary>
        public void TakeLine() => Served++;

        /// <summary>当前饱和度 = 在途车数 / 建议车数。</summary>
        public double Saturation(double now)
            => InFlight.Count(x => x.BackAt > now) / (double)Math.Max(1, Recommended);
    }

    private sealed class TruckState
    {
        public string Id = "";
        public double FreeAt;
        public bool Done;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  C. 签发前校验 —— 任务书是正式单据，缺去向的任务不得下达
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 下达前校验。返回阻止清单：
    /// <list type="number">
    /// <item>计划里有 <b>Error 级</b> 校核（库容不足 / 物料不兼容 / 去向封场 / 设备双占…）；</item>
    /// <item>有<b>需要运输</b>的任务未<b>逐物料</b>指定去向——运距、配车数、运输功全无法核算，任务书写不出"拉到哪"；</item>
    /// <item>有任务<b>未指定主设备</b>——单据下达不到人头上。</item>
    /// </list>
    /// 穿孔/检修类任务本就没有去向，故第②条只对「需要卸点的工序」判定，
    /// 口径与甘特/任务书共用 <c>DailyGanttModel.NeedsDestination</c> + <c>AllMaterialsRouted</c> 这一对，
    /// 三处永不打架——★ 两个都要用：只共用 NeedsDestination、这边却拿 <c>HasDestination</c> 收口，
    /// 混采面缺次要物料去向时任务书拦、下达放行，而放行的这侧才是真落盘的那一侧。
    /// </summary>
    /// <param name="taskIds">
    /// 只校验这些任务（"下达选中"用）。给了它，与本批无关的、带 TaskId 的计划校核就不再阻止下达——
    /// 别的面库容不足不该拦住这台铲的任务；但**不带 TaskId 的全局 Error**（如采排总账不平）永远阻止。
    /// </param>
    public static IssueCheck ValidateForIssue(ExploderResult result, string? shift = null, IEnumerable<string>? taskIds = null)
    {
        var chk = new IssueCheck();
        if (result == null) { chk.Blocks.Add(V(ViolationSeverity.Error, ViolationCodes.DispatchIssued, "", "无计划可下达（引擎未产出结果）。")); return chk; }

        var scope = taskIds == null ? null : new HashSet<string>(taskIds, StringComparer.OrdinalIgnoreCase);
        var tasks = result.Tasks
            .Where(t => t.Process != ProcessType.Idle)
            .Where(t => string.IsNullOrWhiteSpace(shift) || string.Equals(t.Shift, shift, StringComparison.Ordinal))
            .Where(t => scope == null || scope.Contains(t.Id))
            .ToList();
        chk.TaskCount = tasks.Count;

        if (tasks.Count == 0)
        {
            chk.Blocks.Add(V(ViolationSeverity.Error, ViolationCodes.DispatchIssued, "",
                string.IsNullOrWhiteSpace(shift) ? "当日无可下达任务。" : $"{shift} 无可下达任务。"));
            return chk;
        }

        // ① Error 级校核（班次筛选时只看与本班任务相关或全局的那些）
        var ids = new HashSet<string>(tasks.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var v in result.Violations)
        {
            bool related = string.IsNullOrWhiteSpace(v.TaskId) || ids.Contains(v.TaskId);
            if (!related) continue;
            if (v.Severity == ViolationSeverity.Error) chk.Blocks.Add(v);
            else if (v.Severity == ViolationSeverity.Warn) chk.Warnings.Add(v);
        }

        // ② 缺去向 —— 判据是**逐物料都定了**（AllMaterialsRouted），与任务书、甘特同一条。
        //    原先这里只判 t.HasDestination（主去向）：混采面"煤定了破碎站、岩没人管"会静默过关，
        //    于是任务书那侧标红不许签发、下达这侧照样放行——而放行的这侧才是真落盘的那一侧，
        //    单据发下去现场就把岩也拉进破碎站了。
        //    ★ 量的判据必须**按工序取账**（2026-08-20 修）：运输笔的量在 `HaulTonnageT`，
        //      `TargetVolumeM3` 恒为 0 ⇒ 原来这条 `> 1` 把**全部运输笔静默跳过**了。
        //      而运输笔正是"这车拉到哪"最直接的那一笔 —— 判去向的闸偏偏漏掉了它。
        foreach (var t in tasks.Where(t => DailyGanttModel.NeedsDestination(t) && HasQuantity(t)
                                        && !DailyGanttModel.AllMaterialsRouted(t)))
        {
            var miss = DailyGanttModel.UnroutedMaterials(t);
            // 混采面点名是哪种物料缺去向——"这条任务没定去向"和"这条任务的岩没定去向"是两回事
            string why = miss.Count > 0 && DailyGanttModel.RoutesOf(t).Count > 1
                ? $"混采面的 {string.Join("、", miss)} 未指定卸点"
                : "未指定卸点";
            chk.Blocks.Add(V(ViolationSeverity.Error, ViolationCodes.NoDestination, t.Id,
                $"{t.WorkZone} {t.Process.Label()} {TaskQuantity.Of(t).CaptionWithBasis}"
              + $"（{t.ResolvedMix.Caption}）{why}——"
              + "任务书写不出「这车拉到哪」，运距/配车/运输功均无法核算；请先在「流向分配 / 作业面台账」指派去向。"));
        }

        // ③ 缺主设备
        //
        //  ★ 爆破笔豁免（2026-08-20 修）：按已定口径**爆破不指人**（爆破队台账还没有，
        //    编一个队号出来是假的），`MonthlyShiftDecomposer` 排的爆破行 MachineId 恒为空。
        //    原来这一条不分工序地判，于是**只要本班有一炮，整盘就一条都下达不了**，
        //    而给出的理由是"未指定主设备"——照着它去查会跑去设备台账里找爆破队，
        //    那张表根本不存在。闸拦的是"漏填"，不该拦"按口径就不填"。
        foreach (var t in tasks.Where(t => string.IsNullOrWhiteSpace(t.Group.MainEquipment)
                                        && t.Process != ProcessType.Blast))
            chk.Blocks.Add(V(ViolationSeverity.Error, ViolationCodes.FleetMismatch, t.Id,
                $"{t.WorkZone} {t.Process.Label()} 未指定主设备，任务无法下达到设备。"));

        // ── 提醒（不阻止）：需运输却没配车、运距缺失 ──
        foreach (var t in tasks.Where(t => t.Process == ProcessType.Load && t.TargetVolumeM3 > 1 && t.Group.Trucks.Count == 0))
            chk.Warnings.Add(V(ViolationSeverity.Warn, ViolationCodes.TruckShortage, t.Id,
                $"{t.WorkZone} 尚未配车（建议 {t.Group.RecommendedTrucks} 台），下达后无法展开派车单。"));

        foreach (var t in tasks.Where(t => DailyGanttModel.NeedsDestination(t) && t.HasDestination && t.EffectiveHaulKm <= 1e-6))
            chk.Warnings.Add(V(ViolationSeverity.Warn, ViolationCodes.HaulMissing, t.Id,
                $"{t.WorkZone} → {t.DestinationCaption} 无运距，车次时刻按兜底值估算。"));

        return chk;
    }

    /// <summary>
    /// 这条任务有没有量 —— <b>按工序取自己那本账</b>（见 <see cref="TaskQuantity"/>）。
    /// <para>爆破笔按口径没有方量，故恒为假：它不该被"有量才判"的那些闸判到。</para>
    /// </summary>
    private static bool HasQuantity(ProductionTask t) => TaskQuantity.Of(t).Value > 1;

    // ═════════════════════════════════════════════════════════════════════════
    //  D. 事件驱动重排
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 故障触发：停机 <paramref name="hours"/> h 且该设备本班之后还有活 ⇒ 触发备机顶替重排。
    /// 门槛 <paramref name="minHours"/> 以下按班内自行消化处理，不惊动全盘计划。
    /// </summary>
    public static DispatchAdvice OnFault(string equipId, double hours,
                                         ExploderConfig cfg, IReadOnlyList<ProductionTask> tasks,
                                         double fromHour, double minHours = 0.5)
    {
        var adv = new DispatchAdvice { Trigger = $"{equipId} 故障 {hours:0.#}h" };

        var mine = tasks.Where(t => string.Equals(t.Group.MainEquipment, equipId, StringComparison.OrdinalIgnoreCase)
                                 && t.Process is ProcessType.Load or ProcessType.Dump or ProcessType.Drill).ToList();
        double remain = mine.Where(t => t.EndHour > fromHour).Sum(t => Math.Max(0, t.TargetVolumeM3 - t.ActualVolumeM3));

        if (hours < minHours)
        {
            adv.Judgement = $"停机 {hours:0.#}h 低于重排门槛 {minHours:0.#}h，班内自行消化，不触发滚动重排。";
            return adv;
        }
        if (mine.Count == 0)
        {
            adv.Judgement = $"该设备当日无生产任务，故障不影响计划，仅登记 FaultEvent。";
            return adv;
        }

        adv.Triggered = true;
        adv.Judgement = $"停机 {hours:0.#}h ≥ 门槛 {minHours:0.#}h，且 {DispatchClock.Hm(fromHour)} 后尚有 {remain:0} m³ 未完成 ⇒ 触发滚动重排。";

        var zones = mine.Select(t => t.WorkZone).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var strat = zones.ToDictionary(z => z, _ => TaskRescheduler.StrategyFor(IncompleteReason.Fault), StringComparer.OrdinalIgnoreCase);

        adv.Plan = SafeReschedule(cfg, tasks, fromHour, strat, adv);
        if (adv.Plan != null)
        {
            adv.Suggestions.AddRange(adv.Plan.Notes);
            double rolled = adv.Plan.RolledShortfallM3;
            adv.Suggestions.Add($"本班影响面：{string.Join("、", zones)}；未完成 {remain:0} m³，重排回摊 {rolled:0} m³。");
        }
        return adv;
    }

    /// <summary>
    /// 卸点拥堵触发：投向该卸点的面改走「换面」策略（换面会重新校核物料/库容/卸点能力）。
    /// 该卸点当日无入方则不触发。
    /// </summary>
    public static DispatchAdvice OnSinkCongested(string sinkId,
                                                 ExploderConfig cfg, IReadOnlyList<ProductionTask> tasks,
                                                 double fromHour)
    {
        string name = cfg?.Sinks?.Find(sinkId)?.Name ?? sinkId;
        var adv = new DispatchAdvice { Trigger = $"{name} 卸点拥堵" };
        if (cfg == null)
        {
            adv.Judgement = "缺少编制盘子（ExploderConfig），无法重排——请先在「编制配置」生成计划。";
            return adv;
        }

        var hit = tasks.Where(t => t.Process == ProcessType.Load && t.TargetVolumeM3 > 1
                                && (string.Equals(t.DestinationId, sinkId, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(t.DestinationName, name, StringComparison.OrdinalIgnoreCase)))
                       .ToList();
        if (hit.Count == 0)
        {
            adv.Judgement = "当日无采装面卸向该点，拥堵不影响本班计划。";
            return adv;
        }

        adv.Triggered = true;
        double t2 = hit.Sum(x => x.TargetTonnageT);
        adv.Judgement = $"当日有 {hit.Count} 个面卸向该点（{t2 / 1e4:0.###} 万t）⇒ 触发分流重排。";

        var zones = hit.Select(t => t.WorkZone).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var strat = zones.ToDictionary(z => z, _ => AdjustStrategy.SwitchFace, StringComparer.OrdinalIgnoreCase);

        adv.Plan = SafeReschedule(cfg, tasks, fromHour, strat, adv);
        if (adv.Plan != null)
        {
            adv.Suggestions.AddRange(adv.Plan.Notes);
            if (adv.Plan.Flow != null) adv.Suggestions.Add("流向已重解：" + adv.Plan.Flow.Explain);
        }
        return adv;
    }

    /// <summary>
    /// 实绩偏差触发：达成度低于 <paramref name="lowPct"/> 或高于 <paramref name="highPct"/> ⇒ 按原因码路由策略重排。
    /// </summary>
    public static DispatchAdvice OnActualDeviation(string taskId,
                                                   ExploderConfig cfg, IReadOnlyList<ProductionTask> tasks,
                                                   double fromHour, double lowPct = 90, double highPct = 115)
    {
        var t = tasks.FirstOrDefault(x => string.Equals(x.Id, taskId, StringComparison.OrdinalIgnoreCase));
        var adv = new DispatchAdvice { Trigger = $"任务 {taskId} 实绩偏差" };
        if (t == null) { adv.Judgement = "任务不存在（可能已被重排替换），无法判定。"; return adv; }

        double att = t.AttainmentPct;
        adv.Trigger = $"{t.Id} {t.WorkZone} 达成 {att:0}%";

        if (att >= lowPct && att <= highPct)
        {
            adv.Judgement = $"达成度 {att:0}% 在 [{lowPct:0},{highPct:0}]% 带内，属正常波动，不触发重排。";
            return adv;
        }

        adv.Triggered = true;
        var reason = t.Reasons.FirstOrDefault();
        if (t.Reasons.Count == 0) reason = att < lowPct ? IncompleteReason.OverPlanned : IncompleteReason.OverAchieved;
        var strategy = TaskRescheduler.StrategyFor(reason);

        adv.Judgement = $"达成度 {att:0}%（欠 {t.ShortfallM3:0} m³），原因码「{reason.Label()}」⇒ 路由策略 {strategy} 并重排。";
        var strat = new Dictionary<string, AdjustStrategy>(StringComparer.OrdinalIgnoreCase) { [t.WorkZone] = strategy };

        adv.Plan = SafeReschedule(cfg, tasks, fromHour, strat, adv);
        if (adv.Plan != null) adv.Suggestions.AddRange(adv.Plan.Notes);
        return adv;
    }

    /// <summary>重排调用的统一容错壳：引擎抛异常只降级给建议，不让 UI 崩。</summary>
    private static RescheduleResult? SafeReschedule(ExploderConfig cfg, IReadOnlyList<ProductionTask> tasks,
                                                    double fromHour, Dictionary<string, AdjustStrategy> strat,
                                                    DispatchAdvice adv)
    {
        try { return TaskRescheduler.Reschedule(cfg, tasks, fromHour, strat); }
        catch (Exception ex)
        {
            adv.Suggestions.Add($"滚动重排未执行（{Short(ex)}），请在「生产任务动态调整」手动重排。");
            return null;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  支撑：去向 / 运距 / 编组求解
    //
    //  ★ 一律走 TruckTripPlanner 的求解层（SinkOf / LegOf / MatchOf）——
    //    甘特画的车次条与本引擎签发的派车指令必须出自同一个 T_c / τ_L / 运距解，
    //    各算各的迟早会出现「图上 8 趟、单子上 7 趟」这种对不上账的事。
    //    缓存也共用它那一份（键是求解相关字段的指纹，不是 Id）。
    // ═════════════════════════════════════════════════════════════════════════

    private sealed class SolveCtx
    {
        public HaulLeg? Leg;
        public FleetMatchResult? Match;
        /// <summary>解不出时的人读原因；为 null 表示解出来了。</summary>
        public string? Fail;
        /// <summary>解不出时的分类（<see cref="Fail"/> 非空时有意义）——界面按它分组，不自己再判一遍。</summary>
        public TripSkipKind FailKind;
    }

    /// <summary>解一条任务的「汇 → 运距 → 编组」三层；任一层解不出都给出人读原因。</summary>
    private static SolveCtx Solve(ProductionTask t)
    {
        var ctx = new SolveCtx();

        if (!t.HasDestination)
        {
            ctx.Fail = "未指定卸点，无法展开车次（缺去向即无运距、无循环时间 T_c）。";
            ctx.FailKind = TripSkipKind.NoDestination;
            return ctx;
        }

        // LegOf 内部已把 Feasible==false 归一成 null
        ctx.Leg = TruckTripPlanner.LegOf(t);
        if (ctx.Leg is not { Feasible: true })
        {
            ctx.Fail = "运距未解出（路网/手填/兜底三层全空），无法算循环时间。";
            ctx.FailKind = TripSkipKind.NoHaulDistance;
            return ctx;
        }

        // MatchOf 内部已把 CycleTimeMin<=0 归一成 null
        ctx.Match = TruckTripPlanner.MatchOf(t);
        if (ctx.Match is null or { CycleTimeMin: <= 0 })
        {
            ctx.Fail = "编组未解出（dispatch_rule 台账不可用且物理反推失败），无法算装车节拍。";
            ctx.FailKind = TripSkipKind.NoFleetSolution;
            return ctx;
        }
        return ctx;
    }

    /// <summary>
    /// 从编组规则文案里取单车载重 W_t（t）。
    /// FleetMatchResult.RuleCaption 的格式是
    /// <c>{铲型号}（{斗容}m³）＋{车型号}（{载重}t）· {斗数}斗/车</c>，
    /// 取「(数字)t」那一段。取不到返回 0，由调用方按目标吨量反推。
    /// </summary>
    private static double PayloadFromRule(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption)) return 0;
        var m = Regex.Match(caption, @"[（(]\s*([0-9]+(?:\.[0-9]+)?)\s*t\s*[）)]", RegexOptions.CultureInvariant);
        if (!m.Success) return 0;
        return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 ? v : 0;
    }

    private static PlanViolation V(ViolationSeverity sev, string code, string taskId, string msg)
        => new() { Severity = sev, Code = code, TaskId = taskId, Message = msg };

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
