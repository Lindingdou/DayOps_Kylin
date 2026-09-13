// 忠实移植自原 PitMine3D Modules/TaskLib/Domain/Dispatch.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;   // 消歧 System.Threading.Tasks.TaskStatus（ImplicitUsings 会引入）

namespace PitMine3D.Kylin.TaskLib.Domain;

// ─────────────────────────────────────────────────────────────────────────────
//  任务下达与执行调度的本体 —— 把「计划」变成「可追溯的单据」。
//
//  在此之前，任务是每次开窗现算出来的：SampleTaskBoard.Result() 一旦重算，
//  Id（＝前缀-主设备-班次）就可能随参数变化而漂移，"昨天下达的那条任务"根本对不上号。
//  单据必须有身份，本文件给出四层身份：
//
//    ① TaskInstance   任务实例 —— 一条业务任务的一个版本。稳定键把「同一件事」
//                     在重排前后钉死，版本号记录它被改过几次。
//    ② DispatchOrder  派车指令（L4 车次级）—— 调度的最小单元，也是三维运输动画的数据源。
//    ③ DispatchReceipt 回执 —— 下达/确认/开始/完成/异常都留痕，"下达了没有"才有得查。
//    ④ FaultEvent     故障记录 —— 让"液压故障 0.8h"从写死的文案变成可录入、可汇总的事实。
//
//  另附派工花名册（CrewMember/CrewAssignment）与实绩记录（ActualRecord），
//  它们与上述四类同属"执行期落盘的事实"，共用一套持久化（见 Engine/TaskPersistence）。
//
//  口径铁律（本文件涉及体积/吨量处一律遵守）：
//    储量=实方 · 卡车载重/配车=松方(×Ks) · 排土库容=占容方(×Kr) · 吨量是三者唯一的守恒中间量。
//    所有换算一律经 MaterialCatalog / MaterialMix，本文件不出现任何密度常量。
// ─────────────────────────────────────────────────────────────────────────────

// ═════════════════════════════════════════════════════════════════════════════
//  ① 稳定键 —— 让"同一条业务任务"在重排前后仍对得上号
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 任务稳定键。
/// <para>
/// 问题：<c>ProductionTask.Id</c> 是 <c>{IdPrefix}-{主设备}-{班次}</c>，滚动重排会把前缀改成
/// <c>D0617R</c>，改一个编制参数所有 Id 都跟着变；拿它做主键，实绩对账与版本追溯全断。
/// </para>
/// <para>
/// 解法：用「日期 + 班次 + 主设备 + 工序 + 作业面」这五元组做键——这五样才是
/// 「这件事是哪件事」的业务标识，前缀/重排轮次/临时改量都不在其中。
/// 五元组归一化后取 FNV-1a 64 位散列的前 12 位十六进制。
/// </para>
/// <para>
/// ★ 绝不能用 <c>string.GetHashCode()</c>：.NET Core 起它按进程随机加盐，
/// 今天存盘的键明天读出来对不上，落库场景下这是致命的。故此处自实现 FNV-1a。
/// </para>
/// </summary>
public static class TaskKey
{
    /// <summary>由一条任务算稳定键。planDate 传当日标签（如 "2026-06-17 周二"）。</summary>
    public static string Of(ProductionTask t, string planDate)
        => Compose(planDate, t.Shift, t.Group.MainEquipment, t.Process.ToString(), ZoneKeyOf(t));

    /// <summary>
    /// 稳定键里的「作业面」这一维。
    ///
    /// <para><b>运输笔要带物料码</b>（2026-08-20 修）：混采面（煤7∶岩3）一笔采装会派生出
    /// <b>两笔运输</b>——煤去破碎站、岩去排土场，而它俩的
    /// 日期/班次/车队/工序/作业面**五样全同** ⇒ 稳定键一模一样。
    /// 后果：单据按键存字典，后写的盖掉先写的；实绩按键对号会把煤那趟的量记到岩那趟上；
    /// 「已下达 N 条」的 N 也会少一条。<b>而每一条看着都正常。</b></para>
    ///
    /// <para>与 <c>HaulDumpDeriver</c> 给运输笔编 Id 时用的那一维（<c>面|物料码</c>）同源，
    /// 两处必须一起改 —— 不然 Id 分得开、稳定键分不开，查起来只会更难。</para>
    /// </summary>
    private static string ZoneKeyOf(ProductionTask t)
        => t.Process == ProcessType.Haul && !string.IsNullOrWhiteSpace(t.MaterialCode)
            ? t.WorkZone + "|" + t.MaterialCode
            : t.WorkZone;

    /// <summary>由五元组算稳定键：TK-{日期}-{班}-{12位散列}。</summary>
    public static string Compose(string? planDate, string? shift, string? mainEquip, string? process, string? workZone)
    {
        string payload = string.Join("|", Norm(planDate), Norm(shift), Norm(mainEquip), Norm(process), Norm(workZone));
        return $"TK-{DateTag(planDate)}-{ShiftTag(shift)}-{Fnv1a(payload)}";
    }

    /// <summary>日期标签：取首个空白分隔片段并去掉文件名非法字符（"2026-06-17 周二" → "2026-06-17"）。</summary>
    public static string DateTag(string? planDate)
    {
        string s = (planDate ?? "").Trim();
        if (s.Length == 0) return "nodate";
        int sp = s.IndexOfAny(new[] { ' ', '\t', '　' });
        if (sp > 0) s = s[..sp];
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-');
        return sb.ToString();
    }

    /// <summary>班次单字标签（早/中/夜；其它班制取首字）。</summary>
    public static string ShiftTag(string? shift)
    {
        string s = (shift ?? "").Trim();
        return s.Length == 0 ? "全" : s[..1];
    }

    /// <summary>归一化：去空白、统一小写（设备号大小写混录在现场很常见）。</summary>
    private static string Norm(string? s) => (s ?? "").Trim().ToLowerInvariant();

    /// <summary>FNV-1a 64 位散列 → 12 位十六进制。跨进程、跨机器稳定。</summary>
    private static string Fnv1a(string s)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        ulong h = offset;
        foreach (byte b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= prime; }
        return h.ToString("x16")[..12];
    }
}

/// <summary>时刻文案小工具（0..24 小时制 → HH:mm）。派车单/回执/看板共用一套写法。</summary>
public static class DispatchClock
{
    /// <summary>小时数 → "HH:mm"。跨日（&gt;24）自动回绕并加"+1"标记。</summary>
    public static string Hm(double hh)
    {
        bool next = hh >= 24;
        double v = next ? hh - 24 : hh;
        int h = (int)v;
        int m = (int)Math.Round((v - h) * 60);
        if (m >= 60) { m -= 60; h += 1; }
        return $"{h:00}:{m:00}" + (next ? "+1" : "");
    }

    /// <summary>当前钟点（0..24），用于报修/复机的默认时刻。</summary>
    public static double NowHourOfDay()
    {
        var n = DateTime.Now;
        return n.Hour + n.Minute / 60.0;
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  ② 任务实例
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 一条业务任务的一个版本。计划每次重算/重排都产生新版本，但 <see cref="StableKey"/> 不变——
/// 靠它把「第 1 版下达的任务」与「第 3 版实绩录入的任务」对上号。
/// </summary>
public sealed class TaskInstance
{
    /// <summary>本实例唯一标识（GUID 无横线）。跨版本不同。</summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>业务编号 = <c>ProductionTask.Id</c>（会随重排前缀变，仅作显示与人工对话用）。</summary>
    public string TaskId { get; set; } = "";

    /// <summary>稳定键（见 <see cref="TaskKey"/>）。版本追溯与实绩对账的真主键。</summary>
    public string StableKey { get; set; } = "";

    /// <summary>版本号，1 起。同一 StableKey 每次重新下达/重排 +1。</summary>
    public int Version { get; set; } = 1;

    public string PlanDate { get; set; } = "";
    public string Shift { get; set; } = "";

    // ── 下达 / 确认 ──
    public string IssuedBy { get; set; } = "";
    public DateTime? IssuedAt { get; set; }
    public string AckedBy { get; set; } = "";
    public DateTime? AckedAt { get; set; }

    // ── 撤回 ──
    //  ★ 撤回**不许抹掉** IssuedBy/IssuedAt。原先撤回时把这两个字段清空，
    //    "谁在几点下达过这条任务"就此消失（只剩回执里那一条），而本类的注释与设计
    //    通篇宣称"单据流水只增不改"。撤回是**又发生了一件事**，不是把上一件事擦掉。
    public string WithdrawnBy { get; set; } = "";
    public DateTime? WithdrawnAt { get; set; }

    public TaskStatus Status { get; set; } = TaskStatus.Planned;

    /// <summary>下达那一刻的任务快照（去向/物料/量/时段全带走）。计划再变也不影响已下达单据。</summary>
    public ProductionTask? Snapshot { get; set; }

    public string Notes { get; set; } = "";

    /// <summary>此刻处于已下达状态（下达过且没被撤回）。</summary>
    public bool IsIssued => IssuedAt.HasValue && !WithdrawnAt.HasValue;

    /// <summary>曾经下达过 —— 撤回之后仍为真。追溯问的是这个，不是 <see cref="IsIssued"/>。</summary>
    public bool WasIssued => IssuedAt.HasValue;

    /// <summary>撤回：留痕不抹痕。下达时刻原样保留，靠 <see cref="WithdrawnAt"/> 表达"现在不算数了"。</summary>
    public void Withdraw(string by, DateTime at)
    {
        WithdrawnBy = by;
        WithdrawnAt = at;
        AckedBy = "";
        AckedAt = null;          // 撤回 ⇒ 班组的确认作废
        Status = TaskStatus.Planned;
    }

    public string IssueCaption => IsIssued
        ? $"✓ 已下达 v{Version}（{IssuedBy}·{IssuedAt:MM-dd HH:mm}）"
        : WasIssued
            ? $"待下达（v{Version} 由 {IssuedBy} {IssuedAt:MM-dd HH:mm} 下达，{WithdrawnBy} {WithdrawnAt:MM-dd HH:mm} 撤回）"
            : "待下达";

    /// <summary>由一条任务生成新实例（下达时调用）。</summary>
    public static TaskInstance From(ProductionTask t, string planDate, int version = 1) => new()
    {
        TaskId = t.Id,
        StableKey = TaskKey.Of(t, planDate),
        Version = version,
        PlanDate = planDate,
        Shift = t.Shift,
        Status = t.Status,
        Snapshot = t,
    };
}

// ═════════════════════════════════════════════════════════════════════════════
//  ③ 派车指令（L4 车次级）
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>派车指令状态机：计划 → 已下达 → 装车 → 重车运行 → 已卸（或取消）。</summary>
public enum DispatchOrderStatus { Planned, Issued, Loading, Hauling, Dumped, Cancelled }

/// <summary>动态派车规则族。</summary>
public enum DispatchRuleKind
{
    /// <summary>固定配车：卡车绑定一台铲，按 s(i,k)=起始+(i−1)τ_L+(k−1)T_c 展开。</summary>
    FixedAssignment,
    /// <summary>最小铲饱和度：卸完的车派给「已到位车数 / 建议车数」最小的铲。</summary>
    MinShovelSaturation,
    /// <summary>最早可装车时刻：卸完的车派给最早能开始装车的铲。</summary>
    EarliestLoad,
}

public static class DispatchEnumLabels
{
    public static string Label(this DispatchOrderStatus s) => s switch
    {
        DispatchOrderStatus.Planned => "计划",
        DispatchOrderStatus.Issued => "已下达",
        DispatchOrderStatus.Loading => "装车中",
        DispatchOrderStatus.Hauling => "重车运行",
        DispatchOrderStatus.Dumped => "已卸载",
        DispatchOrderStatus.Cancelled => "已取消",
        _ => "",
    };

    public static string Label(this DispatchRuleKind k) => k switch
    {
        DispatchRuleKind.FixedAssignment => "固定配车",
        DispatchRuleKind.MinShovelSaturation => "最小铲饱和度",
        DispatchRuleKind.EarliestLoad => "最早可装车",
        _ => "",
    };

    public static string Label(this ReceiptKind k) => k switch
    {
        ReceiptKind.Issue => "下达",
        ReceiptKind.Ack => "确认",
        ReceiptKind.Start => "开始",
        ReceiptKind.Finish => "完成",
        ReceiptKind.Exception => "异常",
        ReceiptKind.Withdraw => "撤回",
        _ => "",
    };

    public static string Label(this FaultCategory c) => c switch
    {
        FaultCategory.Mechanical => "机械",
        FaultCategory.Electrical => "电气",
        FaultCategory.Hydraulic => "液压",
        FaultCategory.Tyre => "轮胎",
        FaultCategory.Other => "其它",
        _ => "",
    };

    public static string Label(this FaultStatus s) => s switch
    {
        FaultStatus.Reported => "报修",
        FaultStatus.Repairing => "维修中",
        FaultStatus.Resumed => "已复机",
        _ => "",
    };
}

/// <summary>
/// 一条派车指令 = 一个车次（一趟：装 → 重车 → 卸 → 空车返）。
/// <para>
/// 这是调度的最小单元：任务说"这个班采 2450 m³"，派车单说"T-01 第 3 趟 09:12 装车、09:26 卸到破碎站"。
/// 也是三维运输动画的数据源（每条指令即一辆车的一次往返轨迹）。
/// </para>
/// </summary>
public sealed class DispatchOrder
{
    public string OrderId { get; set; } = "";
    public string TruckId { get; set; } = "";
    public string ShovelId { get; set; } = "";

    /// <summary>所属任务的业务编号。</summary>
    public string TaskId { get; set; } = "";
    /// <summary>所属任务的稳定键（重排后仍能归堆）。</summary>
    public string StableKey { get; set; } = "";
    /// <summary>所属任务实例（未下达时为空）。</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>本车本任务内的第几趟（1 起）。</summary>
    public int TripNo { get; set; }

    public string PlanDate { get; set; } = "";
    public string Shift { get; set; } = "";
    public string WorkZone { get; set; } = "";

    // ── 物料与去向 ──
    public string MaterialCode { get; set; } = "";
    public string SinkId { get; set; } = "";
    public string SinkName { get; set; } = "";
    public SinkKind SinkKind { get; set; } = SinkKind.ExternalDump;

    // ── 时刻（当日 0..24 小时制；跨零点按 >24 记）──
    /// <summary>预计装车时刻。</summary>
    public double PlannedLoadHour { get; set; }
    /// <summary>预计卸车时刻 = 装车 + 装车节拍 + 重车行驶。</summary>
    public double PlannedDumpHour { get; set; }
    /// <summary>循环时间 T_c（min）。</summary>
    public double CycleMin { get; set; }

    // ── 载重（三口径都记，谁也别再去猜）──
    /// <summary>单车载重 t —— 吨量是实方/松方/占容方之间唯一的守恒中间量，故以它为主口径。</summary>
    public double PayloadT { get; set; }
    /// <summary>该车装载的松方 m³（车厢容积校核口径：卡车拉的是爆破后的松散料）。</summary>
    public double PayloadLooseM3 { get; set; }
    /// <summary>该车对应的原位实方 m³（回记作业量口径）。</summary>
    public double PayloadInSituM3 { get; set; }
    /// <summary>本趟运距 km（等效运距优先）。</summary>
    public double HaulKm { get; set; }

    public DispatchOrderStatus Status { get; set; } = DispatchOrderStatus.Planned;

    // ── 实绩时刻（回填；未回填为 null，绝不用 0 冒充）──
    public double? ActualLoadHour { get; set; }
    public double? ActualDumpHour { get; set; }

    /// <summary>本趟运输功 t·km。</summary>
    public double TransportWorkTKm => PayloadT * HaulKm;

    // ── 状态机 ────────────────────────────────────────────────────────────────
    //  计划 → 已下达 → 装车中 → 重车运行 → 已卸（或取消）。
    //  ★ 在这几个方法出现之前，全仓只有 DispatchEngine 写过一次 Planned，六个状态里另外五个
    //    一个也没人写过，ActualLoadHour / ActualDumpHour 零写入。后果是三处**恒定值**：
    //    看板「车次执行」的已卸数恒为 0、派车单「状态」列恒显示"计划"、
    //    三维运输动画拿不到任何实绩时刻。同 [[always-firing-warning-is-a-dead-path]]。

    /// <summary>已回填任一实绩时刻。</summary>
    public bool HasActual => ActualLoadHour.HasValue || ActualDumpHour.HasValue;

    /// <summary>终态：已卸或已取消，不再参与"应完成"统计。</summary>
    public bool IsClosed => Status is DispatchOrderStatus.Dumped or DispatchOrderStatus.Cancelled;

    /// <summary>下达：挂上任务实例时把计划态推成已下达。已经往后走过的不回退。</summary>
    public void MarkIssued()
    {
        if (Status == DispatchOrderStatus.Planned) Status = DispatchOrderStatus.Issued;
    }

    /// <summary>
    /// 标记装车：回填实际装车时刻并置「装车中」。已取消的车次不接受标记（先撤销再说）。
    /// 返回是否真的改了状态。
    /// </summary>
    public bool MarkLoaded(double atHour)
    {
        if (Status == DispatchOrderStatus.Cancelled) return false;
        ActualLoadHour = Math.Round(SameDayAs(atHour, PlannedLoadHour), 3);
        Status = DispatchOrderStatus.Loading;
        return true;
    }

    /// <summary>
    /// 标记卸载：回填实际卸车时刻并置「已卸载」。
    /// <b>没标过装车就直接卸</b>是允许的（现场常态：装车那下没人点），
    /// 此时 <see cref="ActualLoadHour"/> 保持 null —— 不倒推一个装车时刻出来充数。
    /// </summary>
    public bool MarkDumped(double atHour)
    {
        if (Status == DispatchOrderStatus.Cancelled) return false;
        ActualDumpHour = Math.Round(SameDayAs(atHour, PlannedDumpHour), 3);
        Status = DispatchOrderStatus.Dumped;
        return true;
    }

    /// <summary>取消本趟（车坏了 / 卸点封了 / 计划改了）。已回填的实绩时刻保留，作为取消前的痕迹。</summary>
    public void Cancel() => Status = DispatchOrderStatus.Cancelled;

    /// <summary>撤销实绩标记（点错了）：清掉两个实际时刻，退回计划/已下达。</summary>
    public void ClearActual()
    {
        ActualLoadHour = null;
        ActualDumpHour = null;
        Status = string.IsNullOrWhiteSpace(InstanceId) ? DispatchOrderStatus.Planned : DispatchOrderStatus.Issued;
    }

    /// <summary>装车延误 min（实际 − 计划，负数=提前）；未回填返回 null，不拿 0 冒充。</summary>
    public double? LoadDelayMin => ActualLoadHour.HasValue ? (ActualLoadHour.Value - PlannedLoadHour) * 60 : null;

    /// <summary>卸车延误 min。</summary>
    public double? DumpDelayMin => ActualDumpHour.HasValue ? (ActualDumpHour.Value - PlannedDumpHour) * 60 : null;

    /// <summary>
    /// 把实际时刻摆到与计划时刻同一天上：计划 23:40 装、实际 00:10 卸，
    /// 直接相减是 −23.5h（提前近一天），加一圈才是真的迟了 30min。
    /// 判据是"差得比半天还多就是跨了零点"——单趟车次不可能真差 12h 以上。
    /// </summary>
    private static double SameDayAs(double actual, double planned)
    {
        double a = actual;
        while (a < planned - 12) a += 24;
        while (a > planned + 12) a -= 24;
        return a;
    }

    public string MaterialName => MaterialCatalog.Resolve(MaterialCode).Name;

    public string LoadText => DispatchClock.Hm(PlannedLoadHour);
    public string DumpText => DispatchClock.Hm(PlannedDumpHour);

    /// <summary>"T-01 第3趟 → 1号破碎站 09:12/09:26 · 100t"。</summary>
    public string Caption =>
        $"{TruckId} 第{TripNo}趟 → {(string.IsNullOrWhiteSpace(SinkName) ? SinkId : SinkName)} " +
        $"{LoadText}/{DumpText} · {PayloadT:0.#}t";
}

// ═════════════════════════════════════════════════════════════════════════════
//  ④ 回执
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>回执类型。下达必须有回执，否则"下达了没有"无从追溯。</summary>
public enum ReceiptKind { Issue, Ack, Start, Finish, Exception, Withdraw }

/// <summary>一条回执（追加式流水，只增不改）。</summary>
public sealed class DispatchReceipt
{
    // ReceiptId 已删（2026-08-11 逐字段核对）：既不写也不读，全仓只有那一行声明。
    // 回执是纯追加流水，既不按 id upsert、也没有任何地方引用单条回执 —— 一个谁也不认的 GUID。
    // 真要按条引用时再加不迟；旧文件里多出来的 receiptId 会被反序列化忽略，不影响回读。

    /// <summary>派车指令号（任务级回执留空；车次级回执填 <see cref="DispatchOrder.OrderId"/>）。</summary>
    public string OrderId { get; set; } = "";
    /// <summary>任务实例号。</summary>
    public string InstanceId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string StableKey { get; set; } = "";
    public string PlanDate { get; set; } = "";
    public string Shift { get; set; } = "";

    public ReceiptKind Kind { get; set; } = ReceiptKind.Issue;
    public DateTime At { get; set; } = DateTime.Now;
    public string By { get; set; } = "";
    public string Message { get; set; } = "";

    public string Caption => $"{At:MM-dd HH:mm} · {Kind.Label()} · {By} · {Message}";

    public static DispatchReceipt For(TaskInstance inst, ReceiptKind kind, string by, string message) => new()
    {
        InstanceId = inst.InstanceId,
        TaskId = inst.TaskId,
        StableKey = inst.StableKey,
        PlanDate = inst.PlanDate,
        Shift = inst.Shift,
        Kind = kind,
        By = by,
        Message = message,
    };

    /// <summary>
    /// 车次级回执（L4）。<see cref="OrderId"/> 原先声明了却从来没人填 ——
    /// 流水只覆盖到"任务下达/确认/撤回"，而真正在现场发生的事（这一趟装了没有、卸了没有、取消了没有）
    /// 一条痕迹都没有。车次状态机做出来之后，标记装/卸/取消就该在同一本流水上留痕。
    /// </summary>
    public static DispatchReceipt For(DispatchOrder o, ReceiptKind kind, string by, string message) => new()
    {
        OrderId = o.OrderId,
        InstanceId = o.InstanceId,
        TaskId = o.TaskId,
        StableKey = o.StableKey,
        PlanDate = o.PlanDate,
        Shift = o.Shift,
        Kind = kind,
        By = by,
        Message = message,
    };
}

// ═════════════════════════════════════════════════════════════════════════════
//  ⑤ 故障记录
// ═════════════════════════════════════════════════════════════════════════════

public enum FaultCategory { Mechanical, Electrical, Hydraulic, Tyre, Other }
public enum FaultStatus { Reported, Repairing, Resumed }

/// <summary>
/// 一条设备故障记录。取代原先写死在 UI 里的"液压故障 0.8h"文案——
/// 故障时长要能被实绩录入汇总、被重排引擎当作输入，就必须是一条可录入的事实而不是一句话。
/// </summary>
public sealed class FaultEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public string EquipId { get; set; } = "";
    public string PlanDate { get; set; } = "";
    public string Shift { get; set; } = "";

    /// <summary>故障起始钟点（当日 0..24）。</summary>
    public double StartHour { get; set; }
    /// <summary>复机钟点；未复机时等于 StartHour（时长 0），由 <see cref="EstimatedHours"/> 给预估时长。</summary>
    public double EndHour { get; set; }
    /// <summary>报修时给出的预估停机时长 h（未复机期间的时长口径）。</summary>
    public double EstimatedHours { get; set; }

    public FaultCategory Category { get; set; } = FaultCategory.Other;
    public string Description { get; set; } = "";
    public string Reporter { get; set; } = "";
    public FaultStatus Status { get; set; } = FaultStatus.Reported;

    /// <summary>
    /// 计划检修（true）还是非计划故障（false）。
    /// <para>
    /// 两者的停机<b>不是一回事</b>：<see cref="IncompleteReason"/> 里 Fault 与 Maintenance 一直是分开的，
    /// 而本类原先不分，于是实绩录入把检修停机也汇总进"故障工时"——设备可用率、MTBF、
    /// 达成度归因全被计划检修污染，而这几个数正是评价一个班干得怎么样的依据。
    /// </para>
    /// </summary>
    public bool IsPlanned { get; set; }

    /// <summary>开始维修的计划轴时刻；<c>&lt; 0</c> = 还没开修（不能用 0，0 点是合法时刻）。</summary>
    public double RepairStartHour { get; set; } = -1;

    public DateTime ReportedAt { get; set; } = DateTime.Now;
    /// <summary>开始维修的挂钟时刻（算响应时长用）。</summary>
    public DateTime? RepairStartedAt { get; set; }
    public DateTime? ResumedAt { get; set; }

    /// <summary>响应时长 h：报修 → 开始维修。没开修返回 null，不拿 0 冒充"立刻就修了"。</summary>
    public double? ResponseHours => RepairStartedAt.HasValue
        ? Math.Max(0, (RepairStartedAt.Value - ReportedAt).TotalHours) : null;

    /// <summary>修复时长 h（MTTR 的分子）：开始维修 → 复机。任一端缺失返回 null。</summary>
    public double? RepairHours => RepairStartedAt.HasValue && ResumedAt.HasValue
        ? Math.Max(0, (ResumedAt.Value - RepairStartedAt.Value).TotalHours) : null;

    /// <summary>开始维修：两条轴各记一个时刻（计划轴用于与任务时段对齐，挂钟用于算响应/修复时长）。</summary>
    public void BeginRepair(double planHour, DateTime now)
    {
        RepairStartHour = Math.Round(planHour, 2);
        RepairStartedAt = now;
        Status = FaultStatus.Repairing;
    }

    /// <summary>停机时长 h：已复机取实际区间，未复机取报修时的预估值。</summary>
    public double DurationHours => Status == FaultStatus.Resumed
        ? Math.Max(0, EndHour - StartHour)
        : Math.Max(0, EstimatedHours);

    /// <summary>
    /// 复机：把本条记录结到 <paramref name="now"/>，返回实际停机时长 h。
    /// <para>
    /// 时长取【<see cref="ReportedAt"/> → now 的真实经过时长】，<b>不是</b>"计划时钟此刻 − StartHour"：
    /// <see cref="StartHour"/> 落在<b>计划时间轴</b>上（<see cref="OverlapHours"/> 要靠它对上任务时段），
    /// 而计划时钟在样例盘子里是钉死的、台账盘子里也曾是装配那一刻的快照——两头相减恒等于 0，
    /// 于是"复机"这个动作反而把停机时长清零，实绩录入汇总出来的故障工时全线归零。
    /// 走经过时长还顺带管住两件事：跨进程重启仍然对（ReportedAt 是落盘的挂钟），
    /// 跨零点也不用特例（StartHour 22 + 4h = 26，<see cref="DispatchClock.Hm"/> 写成 "02:00+1"）。
    /// </para>
    /// </summary>
    /// <param name="overrideHours">
    /// 补录历史停机 / 复机登记晚了时填的实际时长；<c>null</c> 或非正数 = 按经过时长。
    /// </param>
    public double Resume(DateTime now, double? overrideHours = null)
    {
        double elapsed = Math.Max(0, (now - ReportedAt).TotalHours);
        double dur = overrideHours is > 0 ? overrideHours.Value : elapsed;
        EndHour = Math.Round(StartHour + dur, 2);
        Status = FaultStatus.Resumed;
        ResumedAt = now;
        return DurationHours;
    }

    /// <summary>本故障与 [from,to) 时窗的重叠小时数（实绩录入按任务时段汇总故障工时用）。</summary>
    public double OverlapHours(double from, double to)
    {
        double s = StartHour, e = Status == FaultStatus.Resumed ? EndHour : StartHour + EstimatedHours;
        return Math.Max(0, Math.Min(e, to) - Math.Max(s, from));
    }

    public bool IsOpen => Status != FaultStatus.Resumed;

    /// <summary>"液压故障 1.2h（维修中·张三 10:30 报）"——文案由记录生成，不再写死。</summary>
    public string Caption
    {
        get
        {
            string head = $"{Category.Label()}故障 {DurationHours:0.#}h";
            string tail = Status == FaultStatus.Resumed
                ? $"（已复机 {DispatchClock.Hm(EndHour)}）"
                : $"（{Status.Label()}{(string.IsNullOrWhiteSpace(Reporter) ? "" : "·" + Reporter)} {DispatchClock.Hm(StartHour)} 报）";
            return head + tail + (string.IsNullOrWhiteSpace(Description) ? "" : $" · {Description}");
        }
    }

    /// <summary>
    /// 追溯文案：报修与复机的<b>挂钟</b>时刻。
    /// <para>
    /// <see cref="StartHour"/> / <see cref="EndHour"/> 是**计划时间轴**上的钟点（要与任务时段求重叠，
    /// 跨零点还会记成 26:00），拿它去回答"到底是哪天几点报的"会错。挂钟这一对
    /// （<see cref="ReportedAt"/> / <see cref="ResumedAt"/>）原先写了没人读——
    /// 落盘里躺着、界面上一个字看不到。
    /// </para>
    /// </summary>
    public string AuditCaption
    {
        get
        {
            string who = string.IsNullOrWhiteSpace(Reporter) ? "" : Reporter + " ";
            string s = $"{who}{ReportedAt:MM-dd HH:mm} 报修";
            if (ResumedAt.HasValue) s += $" → {ResumedAt.Value:MM-dd HH:mm} 复机（挂钟 {(ResumedAt.Value - ReportedAt).TotalHours:0.##} h）";
            else s += "（未复机）";
            return s;
        }
    }
}

// ═════════════════════════════════════════════════════════════════════════════
//  ⑥ 班组花名册与派工
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>一名在册人员。持证类别决定他能不能上某类设备。</summary>
public sealed class CrewMember
{
    public string PersonId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>工种："操作手" / "司机" / "辅助"。</summary>
    public string Job { get; set; } = "";
    /// <summary>持证类别："电铲" / "液压铲" / "钻机" / "推土机" / "矿卡"。空=无证。</summary>
    public string CertFor { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>是否在岗（请假/休班置 false，派工时不参与自动匹配）。</summary>
    public bool OnDuty { get; set; } = true;

    public string Caption => $"{Name}（{Job}{(string.IsNullOrWhiteSpace(CertFor) ? "" : "·证:" + CertFor)}{(OnDuty ? "" : "·休")}）";
}

/// <summary>一辆车配到的司机。<b>车号 ↔ 司机是显式配对</b>，不靠"两个列表按下标对齐"。</summary>
public sealed class TruckDriver
{
    public string TruckId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>工号。姓名会重（花名册里两个"张建国"很常见），对号要认它。</summary>
    public string PersonId { get; set; } = "";
}

/// <summary>一个编组一个班次的派工记录。</summary>
public sealed class CrewAssignment
{
    public string PlanDate { get; set; } = "";
    public string Shift { get; set; } = "";
    public string MainEquipment { get; set; } = "";

    // EquipCategory / GroupCaption 已删（2026-08-11 逐字段核对）：两个都是现场数据的**字符串快照**，
    // 写了零读。设备类别每次都从在籍清单现取（改了就该跟着改），编组文案是显示串、随时能拼。
    // 真要留"派工那一刻编组长什么样"的证据，应当照 TaskInstance.Snapshot 的路数存整份快照，
    // 而不是两个散落的显示串 —— 那种半吊子快照过一阵就和现场对不上，比没有更误事。

    public string Operator { get; set; } = "";
    /// <summary>操作手工号（姓名重名时唯一能对准的东西；花名册里认不出来时为空）。</summary>
    public string OperatorId { get; set; } = "";

    /// <summary>
    /// 司机姓名列表 —— <b>界面编辑态与旧档案的形态</b>（顿号分隔的那一列存下来就是它）。
    /// 下游要"这辆车谁开"时不要用它：它没有车号，只能靠下标去猜配对。
    /// </summary>
    public List<string> Drivers { get; set; } = new();

    /// <summary>
    /// 车号 ↔ 司机的显式配对，<b>下游唯一该读的那份</b>（派车单要在每一趟上写司机名）。
    /// 旧档案里没有这一段时，<c>CrewLookup</c> 会按 <see cref="Drivers"/> 与编组配车顺序
    /// 逐位配对兜底，并在来源文案里说明那是推出来的、不是派工时定下的。
    /// </summary>
    public List<TruckDriver> TruckDrivers { get; set; } = new();

    /// <summary>持证校核结论（由花名册比对生成，不是手填）。</summary>
    public string CertNote { get; set; } = "";
    public string AttendNote { get; set; } = "";

    public string SavedBy { get; set; } = "";
    public DateTime SavedAt { get; set; } = DateTime.Now;
}

// ═════════════════════════════════════════════════════════════════════════════
//  ⑦ 实绩记录
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 一条班末实绩。落盘的是「事实」，不是「界面上的字符串」——
/// 吨量/占容方在存盘时就按物料换算好，下游报表与去向台账不必再各算各的。
/// </summary>
public sealed class ActualRecord
{
    public string TaskId { get; set; } = "";
    public string StableKey { get; set; } = "";
    public string PlanDate { get; set; } = "";
    public string Shift { get; set; } = "";
    public string EquipId { get; set; } = "";
    public string WorkZone { get; set; } = "";
    public ProcessType Process { get; set; }

    /// <summary>
    /// 工程位置号（EP-xx）—— <b>业务事实与三维几何之间的连接键</b>。
    /// 计划的三维表达（面模型 + 块体模型）落地时，"这条实绩对应图上哪一块"就靠它对上；
    /// 现在不存，将来几何进来就再也补不回历史了（作业面名会改、会合并，工程位置号不会）。
    /// </summary>
    public string EngineeringPositionId { get; set; } = "";

    public string MaterialCode { get; set; } = "";
    public string DestinationId { get; set; } = "";
    public string DestinationName { get; set; } = "";
    public SinkKind DestinationKind { get; set; } = SinkKind.ExternalDump;

    /// <summary>
    /// 本条实绩的有效运距 km（等效优先、回落实距）——<b>存盘即定</b>，与吨量同理。
    /// 不存的话历史运输功永远算不出来：路网会随采排推进逐期改，
    /// 拿今天的路网去解上个月那趟车的运距，算出来的是另一件事。
    /// 0 = 当时就没有可用运距（运距类指标据此显示"—"，不做估算填充）。
    /// </summary>
    public double EffectiveHaulKm { get; set; }

    /// <summary>计划工时 h（存盘即定；工时利用率 = 实际 ÷ 计划，历史报表要靠它）。</summary>
    public double PlanHours { get; set; }

    // ── 穿孔的量（延米）──
    //  穿孔没有 m³ 口径，它的量走这两项；采装/排土任务为 null。
    //  不存的话，班末录的延米一关窗就没了 —— 与 [[always-firing-warning-is-a-dead-path]] 同一类坑。
    public double? PlanDrillMeters { get; set; }
    public double? ActualDrillMeters { get; set; }

    /// <summary>计划量 m³ 实方。</summary>
    public double PlanVolumeM3 { get; set; }
    /// <summary>实绩量 m³ 实方。</summary>
    public double ActualVolumeM3 { get; set; }
    /// <summary>实绩吨量 t（按物料密度换算，存盘即定，避免下游重算口径漂移）。</summary>
    public double ActualTonnageT { get; set; }
    /// <summary>实绩排弃占容方 m³（排土库容按此扣；非排弃去向为 0）。</summary>
    public double ActualDumpM3 { get; set; }

    public double ActualHours { get; set; }

    /// <summary>
    /// <b>非计划</b>故障工时 h —— 由 <see cref="FaultEvent"/>（<c>IsPlanned=false</c> 的那些）
    /// 按任务时段汇总而来，不是手填的常数。
    /// </summary>
    public double FaultHours { get; set; }

    /// <summary>
    /// <b>计划检修</b>停机 h。与 <see cref="FaultHours"/> 分开记：
    /// 检修是排好的、不该计入设备可用率的扣分项，混在一起会把"按计划保养"记成"设备不行"。
    /// </summary>
    public double MaintenanceHours { get; set; }
    public int TrucksOnSite { get; set; }

    // ── 实测煤质：三项配齐 ──
    //  ★ 原先只有灰分一项，而「质量标准」定的是 灰/热/硫 三项、CoalQuality.MeetsTarget 也判三项。
    //    于是配煤达标只能判灰分，热值不够、硫超标的煤在系统里全是"达标"。
    //    没测就是 null（不拿 0 冒充"测了是 0"）。
    public double? AshPct { get; set; }
    /// <summary>实测热值 MJ/kg。</summary>
    public double? CalorificMJkg { get; set; }
    /// <summary>实测硫分 %。</summary>
    public double? SulfurPct { get; set; }
    public List<IncompleteReason> Reasons { get; set; } = new();
    public string Note { get; set; } = "";

    public string EnteredBy { get; set; } = "";
    public DateTime EnteredAt { get; set; } = DateTime.Now;

    public double AttainmentPct => PlanVolumeM3 > 1e-6 ? Math.Round(ActualVolumeM3 / PlanVolumeM3 * 100, 0) : 0;
}
