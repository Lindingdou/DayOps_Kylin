// 忠实移植自原 PitMine3D Modules/TaskLib/Domain/ProductionTask.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.TaskLib.Domain;

// ─────────────────────────────────────────────────────────────────────────────
//  日常生产组织 —— 一条「生产任务」的完整记录契约
//
//  设计（见对话 / docs 待补）：一条 ProductionTask 正好回答现场六问——
//    设备怎么组合 / 在什么地方 / 干什么活 / 要干多久 / 实际作业 / 是否完成·为啥没完成。
//  它既是甘特图里的一根条，也是任务书里的一行；字段全部接已有数据源
//  （设备分析 RecommendGroup/PredictShiftCapacity、ShiftCalendar、ProductionRecord、FaultEvent、工程位置）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>工序类型（穿孔→爆破→采装→运输→排土，外加检修/空闲）。决定甘特条配色。</summary>
public enum ProcessType { Drill, Blast, Load, Haul, Dump, Idle }

/// <summary>任务生命周期状态。</summary>
public enum TaskStatus { Planned, Dispatched, Running, Done, Partial, Failed }

/// <summary>
/// 单据轴：这条任务此刻**下达没有**。与 <see cref="TaskStatus"/>（执行轴）正交 ——
/// 一条任务可以「已下达且已完成」，也可以「已撤回却已经有人干了」（那是要报出来的冲突）。
///
/// <para><b>为什么不塞进 TaskStatus</b>：塞进去就有两个地方在说同一件事。
/// 从前撤回时把 <c>Status</c> 写回 <c>Planned</c>，"下达过又撤回" 与 "从没下达过"
/// 在盘子上就此分不出来 —— 而下游三组全靠这个分界线。</para>
///
/// <para><b>唯一写方</b>是 <c>DispatchStateLink.ApplyPersistedInstances</c>（从
/// <c>task_instances/</c> 逐条对号回灌）。任何窗口都不许自己就地打这个值。</para>
/// </summary>
public enum DispatchState
{
    /// <summary>排出来了，但没签发过。</summary>
    NotIssued,
    /// <summary>此刻已下达（下达过且没被撤回）。</summary>
    Issued,
    /// <summary>下达过又撤回 —— 不算数了，但发生过。</summary>
    Withdrawn,
}

/// <summary>未完成原因码（达成度评价 → 动态调整 的输入；正偏差也记 OverAchieved）。</summary>
public enum IncompleteReason
{
    Fault, Maintenance, Weather, BlastWait, ProcessWait,
    TruckShortage, OreShortage, RoadCongestion, Absence, OverPlanned, OverAchieved
}

public static class TaskEnumLabels
{
    public static string Label(this ProcessType p) => p switch
    {
        ProcessType.Drill => "穿孔",
        ProcessType.Blast => "爆破",
        ProcessType.Load => "采装",
        ProcessType.Haul => "运输",
        ProcessType.Dump => "排土",
        _ => "检修/空闲"
    };

    public static string Label(this IncompleteReason r) => r switch
    {
        IncompleteReason.Fault => "设备故障",
        IncompleteReason.Maintenance => "计划检修",
        IncompleteReason.Weather => "天气恶劣",
        IncompleteReason.BlastWait => "爆破等待",
        IncompleteReason.ProcessWait => "工序未接续",
        IncompleteReason.TruckShortage => "运力不足",
        IncompleteReason.OreShortage => "欠料·采空",
        IncompleteReason.RoadCongestion => "道路·卸点拥堵",
        IncompleteReason.Absence => "人员缺勤",
        IncompleteReason.OverPlanned => "计划过满",
        IncompleteReason.OverAchieved => "超额完成",
        _ => ""
    };

    /// <summary>全部原因码文案（录入列的合法取值；顺序同枚举，供界面提示用）。</summary>
    public static IReadOnlyList<string> AllReasonLabels { get; } =
        Enum.GetValues(typeof(IncompleteReason)).Cast<IncompleteReason>().Select(r => Label(r)).ToList();

    /// <summary>
    /// 原因码文案 → 枚举（<see cref="Label(IncompleteReason)"/> 的逆）。
    /// <para>
    /// 实绩录入的「未完成原因」列是顿号分隔的中文标签，存盘必须还原成 <see cref="IncompleteReason"/>：
    /// 达成度评价的归因、动态调整的策略路由认的都是枚举，不是那串字。
    /// 顺带认枚举名（"Fault"），便于从外部台账/导入文件灌进来。
    /// </para>
    /// 认不出来一律返回 false，由调用方点名报错——静默丢弃会让"我明明填了原因"变成无解的怪事。
    /// </summary>
    public static bool TryParseReason(string? text, out IncompleteReason reason)
    {
        reason = default;
        string s = (text ?? "").Trim();
        if (s.Length == 0) return false;
        foreach (IncompleteReason r in Enum.GetValues(typeof(IncompleteReason)))
        {
            if (string.Equals(Label(r), s, StringComparison.Ordinal)
             || string.Equals(r.ToString(), s, StringComparison.OrdinalIgnoreCase))
            { reason = r; return true; }
        }
        return false;
    }

    /// <summary>「未完成原因」录入列的分隔符（顿号为主，顺带容忍逗号/分号/斜杠/空格）。</summary>
    private static readonly char[] ReasonSeparators = { '、', ',', '，', ';', '；', '/', ' ' };

    /// <summary>
    /// 解析一整串「未完成原因」（顿号分隔可多选）→ 去重后的原因码列表；
    /// 认不出来的词从 <paramref name="unknown"/> 原样带回，由调用方点名报错。
    /// <para>
    /// 与 <c>CrewAssignWindow.SplitNames</c> 同一路数（自由文本 round-trip），差别是这里必须落到枚举上：
    /// 达成度评价的归因与动态调整的策略路由认的是 <see cref="IncompleteReason"/>，不是那串字。
    /// <b>静默丢弃是禁止的</b>——那会让"我明明填了原因"变成无解的怪事。
    /// </para>
    /// </summary>
    public static List<IncompleteReason> ParseReasons(string? text, out List<string> unknown)
    {
        var list = new List<IncompleteReason>();
        unknown = new List<string>();
        foreach (var tok in (text ?? "").Split(ReasonSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            string s = tok.Trim();
            if (s.Length == 0) continue;
            if (TryParseReason(s, out var r)) { if (!list.Contains(r)) list.Add(r); }
            else unknown.Add(s);
        }
        return list;
    }
}

/// <summary>煤质（配煤/品位）——目标与实测共用。纯量矿可全程不用（任务上为 null）。</summary>
public sealed class CoalQuality
{
    public double AshPct { get; set; }        // 灰分 %
    public double CalorificMJkg { get; set; } // 热值 MJ/kg
    public double SulfurPct { get; set; }     // 硫分 %
    public double MoisturePct { get; set; }   // 水分 %

    public string Caption => $"灰{AshPct:0.#}% · 热{CalorificMJkg:0.#}MJ/kg · 硫{SulfurPct:0.##}%";

    /// <summary>是否在目标允许带内（实测对目标，越限项返回 false）。</summary>
    public bool MeetsTarget(CoalQuality target, double ashTolPct = 1.0, double cvTolMJ = 1.0, double sTolPct = 0.1)
        => AshPct <= target.AshPct + ashTolPct
        && CalorificMJkg >= target.CalorificMJkg - cvTolMJ
        && SulfurPct <= target.SulfurPct + sTolPct;
}

/// <summary>
/// 穿孔任务的作业量。
/// <para>
/// 穿孔没有 m³ 口径 —— 它的量是<b>孔数与延米</b>。在这个类出现之前穿孔任务只带时窗，
/// 于是「工序进度跟踪」只能按 <c>Status == Done</c> 判，而 Done 只由实绩录入置位、
/// 实绩录入又只收采装/排土 ⇒ <b>穿孔进度恒 0%</b>，一条谁也走不通的死路。
/// </para>
/// <para>
/// 计划量来自 <c>drill_plan</c> 台账（孔数 / 单孔延米），实绩由实绩录入回填。
/// 未录一律 <c>null</c>，不拿 0 冒充"打了 0 米"。
/// </para>
/// </summary>
public sealed class DrillQuantity
{
    public int? PlanHoles { get; set; }
    public double? PlanMeters { get; set; }
    public int? ActualHoles { get; set; }
    public double? ActualMeters { get; set; }

    /// <summary>有没有可用的计划量（没有就退回"完成/未完成"二值口径）。</summary>
    public bool HasPlan => PlanMeters is > 0 || PlanHoles is > 0;

    /// <summary>达成率 %：优先按延米（那才是钻机的工作量），没有延米才按孔数。无计划量返回 null。</summary>
    public double? AttainmentPct
    {
        get
        {
            if (PlanMeters is > 0 && ActualMeters.HasValue) return Math.Round(ActualMeters.Value / PlanMeters.Value * 100, 0);
            if (PlanHoles is > 0 && ActualHoles.HasValue) return Math.Round(ActualHoles.Value * 100.0 / PlanHoles.Value, 0);
            return null;
        }
    }

    /// <summary>"12 孔 / 168 m" —— 计划量的一句话；两项都没有返回空串。</summary>
    public string PlanCaption =>
        (PlanHoles is > 0 ? $"{PlanHoles} 孔" : "")
        + (PlanHoles is > 0 && PlanMeters is > 0 ? " / " : "")
        + (PlanMeters is > 0 ? $"{PlanMeters:0.#} m" : "");
}

/// <summary>设备编组（"设备怎么组合"）：主设备 + 配属卡车 + 辅助；班产按采运瓶颈裁。</summary>
public sealed class EquipmentGroup
{
    public string MainEquipment { get; set; } = "";      // 主设备（电铲/钻机）；运输笔上是**车队名**（定车制，见 HaulDumpDeriver）
    public List<string> Trucks { get; set; } = new();    // 配属卡车
    public List<string> Aux { get; set; } = new();       // 辅助（洒水/平路/推土）
    public int RecommendedTrucks { get; set; }           // DispatchRule 推荐车数
    public double GroupCapacityM3PerH { get; set; }      // 编组班产 = min(铲装能力, 车队运力)

    /// <summary>
    /// 单车载重 W_t（t）—— 编组求解用的那一个（来自编组规则台账的车型）。
    /// <para><b>0 = 没解出来</b>：车次数因此不给（<b>不猜一个 100t</b>——猜出来的车次在派车单上看着完全正常）。</para>
    /// </summary>
    public double TruckPayloadT { get; set; }

    // ── 周期分解（由 FleetMatcher 解出时一并带出；未经求解的编组为 0）──
    //
    //  带这三个数只为一件事：**按环节降效**。班产是 min(铲装能力, 车队运力) 压出来的一个标量，
    //  只有它的话，「今天路面湿滑、采装照常」就无从表达 —— 只能全盘一起降。
    //  有了 τ_L / T_c / MF 就能把班产还原成两侧再分别降（见 ExploderConfig.WeatherFactorFor）：
    //    铲装侧 A ∝ 1/τ_L 　车队侧 B ∝ n/T_c 　MF = B/A 　班产 = min(A,B)
    //  于是"运输降效"对采装瓶颈的面自然不生效，对运力瓶颈的面全额生效 —— 这是物理，不是加权猜。

    /// <summary>装车节拍 τ_L（min/车）。0 = 该编组未经 FleetMatcher 求解。</summary>
    public double LoadTaktMin { get; set; }

    /// <summary>卡车循环时间 T_c（min）。0 = 未经求解。</summary>
    public double CycleTimeMin { get; set; }

    /// <summary>匹配系数 MF = n·τ_L/T_c = 车队运力/铲装能力。0 = 未经求解。</summary>
    public double MatchFactor { get; set; }

    /// <summary>周期分解齐备（三个数都有且 T_c &gt; τ_L）—— 按环节降效的前提。</summary>
    public bool HasCycleBreakdown => LoadTaktMin > 1e-6 && CycleTimeMin > LoadTaktMin + 1e-6 && MatchFactor > 1e-6;

    /// <summary>
    /// 深拷。<b>复制编组只许走这一处</b> —— 原先 TaskRescheduler 与 TaskExploder 各有一份
    /// 逐字段手抄的副本，加字段时必然漏：周期分解（τ_L/T_c/MF）刚加上就在这两处掉了，
    /// 表现是「重排之后分环节降效一点不生效」，而且从头到尾不报错
    /// （<see cref="HasCycleBreakdown"/> 变假 ⇒ 静默退回全盘值）。判据 D6 抓的就是它。
    /// </summary>
    public EquipmentGroup Clone() => new()
    {
        MainEquipment = MainEquipment,
        Trucks = new List<string>(Trucks),
        Aux = new List<string>(Aux),
        RecommendedTrucks = RecommendedTrucks,
        GroupCapacityM3PerH = GroupCapacityM3PerH,
        LoadTaktMin = LoadTaktMin, CycleTimeMin = CycleTimeMin, MatchFactor = MatchFactor,
    };

    public string Caption =>
        $"{MainEquipment}（主）" +
        (Trucks.Count > 0 ? $"＋卡车 {string.Join(" / ", Trucks)}（配{Trucks.Count}·荐{RecommendedTrucks}）" : "") +
        (Aux.Count > 0 ? $"＋{string.Join("/", Aux)}" : "");
}

/// <summary>一条生产任务（周/日/班三粒度通用；甘特条 = 班粒度任务）。</summary>
public sealed class ProductionTask
{
    public string Id { get; set; } = "";
    public ProcessType Process { get; set; }
    public EquipmentGroup Group { get; set; } = new();   // 设备怎么组合

    // 在什么地方
    public string WorkZone { get; set; } = "";           // 作业面/作业区
    public double BenchElevationM { get; set; }          // 台阶标高
    public string EngineeringPositionId { get; set; } = ""; // 关联工程位置（MineAssLib）

    /// <summary>
    /// 采掘单元号（V041）—— 这条任务在图上是哪个体（<c>MiningUnitLedger.Row.UnitId</c> 口径）。
    /// <para>
    /// 上面三个字段都答不了"点这条任务，图上该亮哪个体"：<see cref="WorkZone"/> 是自由文本面名，
    /// 台阶标高是个数，工程位置是 MineAssLib 的另一套编号。UnitId 是与采矿模型**同一套**编号，
    /// 任务书上写它、影像底图上按它画边界、实绩按它核销备采——都指着同一个体。
    /// 空 = 该面没绑单元（合法：装箱不依赖它）。
    /// </para>
    /// </summary>
    public string UnitId { get; set; } = "";

    // 排到哪（去向）—— 与「在什么地方」对称；采装任务的卸点、排土任务的受排点
    // 混采时以下五项代表【主去向】（份额最大的物料的去向），完整分项见 Splits。
    public string DestinationId { get; set; } = "";      // SinkNode.Id
    public string DestinationName { get; set; } = "";    // 显示名（北排土场 / 1号破碎站）
    public SinkKind DestinationKind { get; set; } = SinkKind.ExternalDump;
    public double HaulDistanceKm { get; set; }           // 实际运距 km
    public double EquivHaulKm { get; set; }              // 等效运距 km（含坡度折算；0 时回落实距）

    /// <summary>
    /// 按物料分项的去向。混采任务（煤7∶岩3）煤去破碎站、岩去排土场——一条任务多个去向，
    /// 不能拆成两条（同一台铲会撞成"设备双占"）。空 = 单一去向，走上面五个字段。
    /// </summary>
    public List<MaterialDestination> Splits { get; set; } = new();
    public bool HasSplits => Splits.Count > 0;

    // 干什么活
    public string Material { get; set; } = "";           // 物料显示文本（历史自由文本，兼容保留）
    public string MaterialCode { get; set; } = "";       // 结构化物料码（MaterialCatalog）
    public MaterialMix? Mix { get; set; }                // 物料构成（煤岩混采时按份额拆分）
    public double TargetVolumeM3 { get; set; }           // 目标工作量 (m³ 原位实方)——**只有采装笔用它**

    /// <summary>
    /// 承运量（t）—— <b>运输笔专用的一本账</b>。
    /// <para>吨是原位实方/松方/占容方三者之间**唯一守恒**的量，所以运输按吨记。
    /// 与 <see cref="TargetVolumeM3"/> 并成一列求和，得到的数不对应任何真实量。</para>
    /// </summary>
    public double HaulTonnageT { get; set; }

    /// <summary>车次数（承运吨 ÷ 单车载重）。<b>0 = 载重没解出来，不给这个数</b>，不是"不用拉"。</summary>
    public int TripCount { get; set; }

    /// <summary>排弃占容方（m³）—— <b>排土笔专用的一本账</b>（松散堆置后占掉的库容，不是原位实方）。</summary>
    public double DumpVolumeM3 { get; set; }

    /// <summary>
    /// 控制方量（m³）—— <b>穿孔笔专用的一本账</b>：一次穿爆管住的那一方岩。
    /// <para>
    /// 与 <see cref="TargetVolumeM3"/> 的原位实方**不是同一本账**，与 <see cref="DumpVolumeM3"/>
    /// 的排弃占容也不是 —— 三者并成一列求和得到的数不对应任何真实量（同 M5 纪律）。
    /// </para>
    /// <para>
    /// 逐班分解器（<c>MonthlyShiftDecomposer</c>）早就把它算出来了（穿孔行的 <c>VolumeM3</c>，
    /// 口径名 "控制方量"），但映射成 <see cref="ProductionTask"/> 时**整段丢弃** ——
    /// 于是正式单据上穿孔那一行的量永远是「—」，而进度跟踪只能退回"完成/未完成"二值。
    /// 0 = 没有出处（<b>不是"控制方量为零"</b>），单据上一律写「—」。
    /// </para>
    /// </summary>
    public double ControlVolumeM3 { get; set; }

    /// <summary>
    /// 这一笔是从哪一笔**采装**派生来的（运输/排土笔才有）。
    /// <para>守恒要**逐笔**核得动：没有这一列，"承运吨 = 采装吨"只能按总量比，
    /// 而总量对得上不等于每一笔都对得上（多算一笔、少算一笔可以互相抵消）。</para>
    /// </summary>
    public string SourceTaskId { get; set; } = "";
    public CoalQuality? QualityTarget { get; set; }      // 质量/配煤目标（纯量矿留空）

    /// <summary>穿孔量（孔数/延米）。只有 <see cref="ProcessType.Drill"/> 任务有；其余为 null。</summary>
    public DrillQuantity? Drill { get; set; }

    // 要干多久
    public string Shift { get; set; } = "";              // 班次 A/B/C 或 早/中/夜
    public double StartHour { get; set; }                // 当日起 (0..24)
    public double EndHour { get; set; }                  // 当日止 (0..24)
    public double PlannedHours { get; set; }             // 计划工时 = 工作量 ÷ 编组班产

    // 实际作业（回灌自 ProductionRecord）
    public double ActualVolumeM3 { get; set; }
    public double ActualHours { get; set; }
    public int TrucksOnSite { get; set; }                // 实际到位车数
    public CoalQuality? QualityActual { get; set; }      // 实测煤质（回灌，配质达成评价用）

    // 是否完成·为啥没完成
    public TaskStatus Status { get; set; } = TaskStatus.Planned;

    /// <summary>
    /// 单据状态（见 <see cref="DispatchState"/>）。<b>由 <c>DispatchStateLink</c> 从
    /// <c>task_instances/</c> 回灌，与实绩回灌在同一条路上</b> —— 不是窗口自己现打的。
    /// </summary>
    public DispatchState Dispatch { get; set; } = DispatchState.NotIssued;

    /// <summary>此刻已下达。达成度/产量统计的分母口径就是它（用户 2026-08-20 定：只算下达过的）。</summary>
    public bool IsIssued => Dispatch == DispatchState.Issued;

    /// <summary>已撤回 —— 不上看板、不进分母、不该有人给它填实绩。</summary>
    public bool IsWithdrawn => Dispatch == DispatchState.Withdrawn;

    /// <summary>单据轴的显示文案（甘特/看板/报表共用，别各写各的）。</summary>
    public string DispatchCaption => Dispatch switch
    {
        DispatchState.Issued    => "已下达",
        DispatchState.Withdrawn => "已撤回",
        _                       => "未下达",
    };
    public List<IncompleteReason> Reasons { get; set; } = new();

    public double AttainmentPct => TargetVolumeM3 > 1e-6 ? Math.Round(ActualVolumeM3 / TargetVolumeM3 * 100, 0) : 0;
    public double ShortfallM3 => Math.Max(0, TargetVolumeM3 - ActualVolumeM3);

    // ── 物料流派生量（一律经 MaterialCatalog，严禁在别处写死密度）──

    /// <summary>物料构成：优先 Mix，其次 MaterialCode，最后解析历史文本 Material。</summary>
    public MaterialMix ResolvedMix =>
        Mix ?? (MaterialCatalog.Exists(MaterialCode) ? MaterialMix.Single(MaterialCode) : MaterialMix.Parse(Material));

    /// <summary>计划吨量 t（按物料密度换算，煤岩混采按份额加权）。</summary>
    public double TargetTonnageT => ResolvedMix.ToTonnage(TargetVolumeM3);
    /// <summary>实绩吨量 t。</summary>
    public double ActualTonnageT => ResolvedMix.ToTonnage(ActualVolumeM3);
    /// <summary>运输松方 m³（卡车车厢/载重校核用）。</summary>
    public double TargetLooseM3 => ResolvedMix.ToLooseM3(TargetVolumeM3);
    /// <summary>排弃占容方 m³（排土场库容按此扣）。</summary>
    public double TargetDumpM3 => ResolvedMix.ToDumpM3(TargetVolumeM3);
    /// <summary>计入采出量的部分（矿/煤）m³ 实方。</summary>
    public double OreVolumeM3 => TargetVolumeM3 * ResolvedMix.OreFraction;
    /// <summary>计入剥离量的部分 m³ 实方。</summary>
    public double WasteVolumeM3 => TargetVolumeM3 * (1 - ResolvedMix.OreFraction);

    /// <summary>有效运距 km（等效优先）。</summary>
    public double EffectiveHaulKm => EquivHaulKm > 1e-6 ? EquivHaulKm : HaulDistanceKm;
    /// <summary>计划运输功 t·km。</summary>
    public double TransportWorkTKm => TargetTonnageT * EffectiveHaulKm;
    /// <summary>实绩运输功 t·km。</summary>
    public double ActualTransportWorkTKm => ActualTonnageT * EffectiveHaulKm;

    public bool HasDestination => !string.IsNullOrWhiteSpace(DestinationId) || !string.IsNullOrWhiteSpace(DestinationName);

    /// <summary>作业地点文案："主采面·东 +60m（EP-12）"。</summary>
    public string LocationCaption
    {
        get
        {
            string s = WorkZone;
            if (Math.Abs(BenchElevationM) > 1e-6) s += $" +{BenchElevationM:0}m";
            if (!string.IsNullOrWhiteSpace(EngineeringPositionId)) s += $"（{EngineeringPositionId}）";
            return s;
        }
    }

    /// <summary>卸载地点文案："北排土场（外排土场）3.2km"。</summary>
    public string DestinationCaption
    {
        get
        {
            if (!HasDestination) return "—";
            string s = string.IsNullOrWhiteSpace(DestinationName) ? DestinationId : DestinationName;
            s += $"（{DestinationKind.Label()}）";
            if (HaulDistanceKm > 1e-6) s += $" {HaulDistanceKm:0.##}km";
            return s;
        }
    }

    /// <summary>采→排一句话："主采面·东 → 1号破碎站"。</summary>
    public string RouteCaption => HasDestination
        ? $"{WorkZone} → {(string.IsNullOrWhiteSpace(DestinationName) ? DestinationId : DestinationName)}"
        : WorkZone;

    /// <summary>
    /// 该物料在本任务上的去向：优先取 Splits 里的分项，没有分项才回落主去向。
    /// 混采任务的次要物料（如煤面里的岩）靠这个才不会被记成"岩石运进破碎站"。
    /// </summary>
    public MaterialDestination DestinationFor(string materialCode)
    {
        foreach (var s in Splits)
            if (string.Equals(s.MaterialCode, materialCode, StringComparison.OrdinalIgnoreCase))
                return s;
        return new MaterialDestination
        {
            MaterialCode = materialCode,
            Fraction = ResolvedMix.FractionOf(materialCode),
            DestinationId = DestinationId, DestinationName = DestinationName, DestinationKind = DestinationKind,
            HaulKm = HaulDistanceKm, EquivHaulKm = EquivHaulKm,
        };
    }

    /// <summary>转成物料流六元组（按物料拆条，每条挂各自的去向）。</summary>
    public IEnumerable<MaterialFlow> ToFlows(string period) => ToFlows(period, TargetVolumeM3);

    /// <summary>按指定体积（计划量或实绩量）拆出物料流。</summary>
    public IEnumerable<MaterialFlow> ToFlows(string period, double inSituM3)
    {
        foreach (var (spec, m3) in ResolvedMix.Split(inSituM3))
        {
            if (m3 <= 1e-6) continue;
            var d = DestinationFor(spec.Code);
            yield return new MaterialFlow
            {
                Period = period,
                SourceId = string.IsNullOrWhiteSpace(EngineeringPositionId) ? WorkZone : EngineeringPositionId,
                SourceName = WorkZone,
                SourceBenchElevationM = BenchElevationM,
                MaterialCode = spec.Code,
                InSituM3 = m3,
                SinkId = d.DestinationId,
                SinkName = d.DestinationName,
                SinkKind = d.DestinationKind,
                HaulKm = d.HaulKm,
                EquivHaulKm = d.EquivHaulKm,
            };
        }
    }

    /// <summary>计划运输功 t·km —— 混采时按各物料各自的运距分别算，不能用主去向的运距一刀切。</summary>
    public double TransportWorkBySplitTKm
        => ToFlows("", TargetVolumeM3).Sum(f => f.TransportWorkTKm);
}
