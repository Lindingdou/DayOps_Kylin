using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.Cad.Tasks.Scheduling;

/// <summary>
/// 任务的<b>稳定键</b>（移植原 <c>TaskLib.Domain.TaskKey</c>）。
///
/// <para>
/// 它解决的是这个老问题：任务是每次开窗<b>现算</b>的，<c>Id</c> 随编制参数漂移
/// （前缀会因重排变成 <c>D0911R-…</c>、班次简写会变、改一次量就换一个号）。
/// 拿 Id 当主键，昨天下达的任务今天就对不上号 —— 而对不上号时它<b>不报错</b>，
/// 只是显示成"待下达"。
/// </para>
/// <para>
/// 解法：用「日期 + 班次 + 主设备 + 工序 + 作业面」这五元组做键 —— 这五样才是
/// "这件事是哪件事"的业务标识，前缀/重排轮次/临时改量都不在其中。
/// </para>
/// <para>
/// <b>★ 绝不能用 <c>string.GetHashCode()</c></b>：.NET Core 起它按进程随机加盐，
/// 今天存盘的键明天读出来对不上 —— 落盘场景下这是致命的。故此处自实现 FNV-1a。
/// </para>
/// </summary>
public static class TaskKey
{
    /// <summary>由一条任务算稳定键。<paramref name="planDate"/> 传当日标签（如 <c>2026-09-11</c>）。</summary>
    public static string Of(ShiftTask t, string planDate)
        => t == null ? Compose(planDate, "", "", "", "")
         : Compose(planDate, t.Shift, t.Group.MainEquipment, t.Process.ToString(), ZoneKeyOf(t));

    /// <summary>
    /// 稳定键里的「作业面」这一维。
    /// <para>
    /// <b>运输笔要带物料码</b>：混采面（煤7∶岩3）一笔采装会派生出<b>两笔运输</b> ——
    /// 煤去破碎站、岩去排土场，而它俩的 日期/班次/车队/工序/作业面<b>五样全同</b> ⇒ 稳定键一模一样。
    /// 后果：单据按键存字典，后写的盖掉先写的；实绩按键对号会把煤那趟的量记到岩那趟上；
    /// 「已下达 N 条」的 N 也会少一条。<b>而每一条看着都正常。</b>
    /// </para>
    /// <para>
    /// Kylin 侧登记：装箱目前还不派生运输笔（<c>HaulDumpDeriver</c> 未移植），
    /// 这一维<b>先按原版留着</b> —— 等运输笔接进来时不必回头改键，否则旧单据会整体对不上号。
    /// </para>
    /// </summary>
    private static string ZoneKeyOf(ShiftTask t)
        => t.Process == ProcessType.Haul && !string.IsNullOrWhiteSpace(t.Material)
            ? t.WorkZone + "|" + t.Material
            : t.WorkZone;

    /// <summary>由五元组算稳定键：<c>TK-{日期}-{班}-{12位散列}</c>。</summary>
    public static string Compose(string? planDate, string? shift, string? mainEquip, string? process, string? workZone)
    {
        string payload = string.Join("|", Norm(planDate), Norm(shift), Norm(mainEquip), Norm(process), Norm(workZone));
        return $"TK-{DateTag(planDate)}-{ShiftTag(shift)}-{Fnv1a(payload)}";
    }

    /// <summary>日期标签：取首个空白分隔片段并去掉文件名非法字符（"2026-09-11 周五" → "2026-09-11"）。</summary>
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

/// <summary>一条任务的下达快照（存进单据的那一份，计划再变也不影响它）。</summary>
public sealed class TaskSnapshot
{
    public string TaskId { get; set; } = "";
    public string WorkZone { get; set; } = "";
    public string MainEquipment { get; set; } = "";
    public string Process { get; set; } = "";
    public string Material { get; set; } = "";
    public double TargetVolumeM3 { get; set; }
    public string DestinationName { get; set; } = "";
    public double EquivHaulKm { get; set; }
    public double StartHour { get; set; }
    public double EndHour { get; set; }

    public static TaskSnapshot Of(ShiftTask t) => new()
    {
        TaskId = t.Id, WorkZone = t.WorkZone, MainEquipment = t.Group.MainEquipment,
        Process = t.Process.ToString(), Material = t.Material, TargetVolumeM3 = t.TargetVolumeM3,
        DestinationName = t.DestinationName, EquivHaulKm = t.EquivHaulKm,
        StartHour = t.StartHour, EndHour = t.EndHour,
    };
}

/// <summary>
/// 一条业务任务的一个版本。计划每次重算/重排都产生新版本，但 <see cref="StableKey"/> 不变 ——
/// 靠它把「第 1 版下达的任务」与「第 3 版实绩录入的任务」对上号。
/// </summary>
public sealed class TaskInstance
{
    /// <summary>本实例唯一标识。跨版本不同。</summary>
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>业务编号（会随重排前缀变，仅作显示与人工对话用）。</summary>
    public string TaskId { get; set; } = "";

    /// <summary>稳定键（见 <see cref="TaskKey"/>）。版本追溯与实绩对账的真主键。</summary>
    public string StableKey { get; set; } = "";

    /// <summary>版本号，1 起。同一 StableKey 每次重新下达 +1。</summary>
    public int Version { get; set; } = 1;

    public string PlanDate { get; set; } = "";
    public string Shift { get; set; } = "";

    public string IssuedBy { get; set; } = "";
    public DateTime? IssuedAt { get; set; }
    public string AckedBy { get; set; } = "";
    public DateTime? AckedAt { get; set; }

    // ── 撤回 ──
    //  ★ 撤回**不许抹掉** IssuedBy/IssuedAt。把这两个字段清空，"谁在几点下达过这条任务"
    //    就此消失（只剩回执里那一条），而单据流水的原则是**只增不改**。
    //    撤回是"又发生了一件事"，不是把上一件事擦掉。
    public string WithdrawnBy { get; set; } = "";
    public DateTime? WithdrawnAt { get; set; }

    public TaskSnapshot? Snapshot { get; set; }
    public string Notes { get; set; } = "";

    /// <summary>此刻处于已下达状态（下达过且没被撤回）。</summary>
    public bool IsIssued => IssuedAt.HasValue && !WithdrawnAt.HasValue;

    /// <summary>曾经下达过 —— <b>撤回之后仍为真</b>。追溯问的是这个，不是 <see cref="IsIssued"/>。</summary>
    public bool WasIssued => IssuedAt.HasValue;

    /// <summary>撤回：留痕不抹痕。下达时刻原样保留，靠 <see cref="WithdrawnAt"/> 表达"现在不算数了"。</summary>
    public void Withdraw(string by, DateTime at)
    {
        WithdrawnBy = by;
        WithdrawnAt = at;
        AckedBy = "";
        AckedAt = null;          // 撤回 ⇒ 班组的确认作废
    }

    public string IssueCaption => IsIssued
        ? $"✓ 已下达 v{Version}（{IssuedBy}·{IssuedAt:MM-dd HH:mm}）"
        : WasIssued
            ? $"待下达（v{Version} 由 {IssuedBy} {IssuedAt:MM-dd HH:mm} 下达，{WithdrawnBy} {WithdrawnAt:MM-dd HH:mm} 撤回）"
            : "待下达";

    /// <summary>由一条任务生成新实例（下达时调用）。</summary>
    public static TaskInstance From(ShiftTask t, string planDate, int version = 1) => new()
    {
        TaskId = t.Id,
        StableKey = TaskKey.Of(t, planDate),
        Version = version,
        PlanDate = planDate,
        Shift = t.Shift,
        Snapshot = TaskSnapshot.Of(t),
    };
}

public enum ReceiptKind { Issue, Ack, Start, Finish, Exception, Withdraw }

public static class DispatchEnumLabels
{
    public static string Label(this ReceiptKind k) => k switch
    {
        ReceiptKind.Issue => "下达",
        ReceiptKind.Ack => "确认",
        ReceiptKind.Start => "开工",
        ReceiptKind.Finish => "完工",
        ReceiptKind.Exception => "异常",
        ReceiptKind.Withdraw => "撤回",
        _ => k.ToString(),
    };

    public static string Label(this ProcessType p) => p switch
    {
        ProcessType.Drill => "穿孔", ProcessType.Blast => "爆破", ProcessType.Load => "采装",
        ProcessType.Haul => "运输", ProcessType.Dump => "排土", ProcessType.Idle => "空闲",
        _ => p.ToString(),
    };
}

/// <summary>一条回执（<b>追加式流水，只增不改</b>）。</summary>
public sealed class DispatchReceipt
{
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
}

/// <summary>下达前校验的结果。</summary>
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

    public string BlockText => string.Join(Environment.NewLine,
        Blocks.Select((b, i) => $"{i + 1}. [{b.Code}]{(string.IsNullOrWhiteSpace(b.TaskId) ? "" : $" {b.TaskId}")} {b.Message}"));

    public string WarnText => string.Join(Environment.NewLine,
        Warnings.Select((b, i) => $"{i + 1}. [{b.Code}]{(string.IsNullOrWhiteSpace(b.TaskId) ? "" : $" {b.TaskId}")} {b.Message}"));
}

/// <summary>
/// 「计划 → 执行」那道闸（移植原 <c>TaskLib.Engine.DispatchEngine.ValidateForIssue</c>）。
///
/// <para>任务书是正式单据。三条阻止项照搬原版，每一条都有具体后果：</para>
/// <list type="number">
///   <item><b>Error 级校核</b>不得下达（设备双占这类，排出来本身就是错的）。</item>
///   <item><b>缺去向</b>不得下达 ——「这车拉到哪」都没定就发单，现场只能自己找地方倒，
///     采排账当天就散。</item>
///   <item><b>缺主设备</b>不得下达 —— 任务落不到设备上。
///     <b>爆破笔豁免</b>：按已定口径爆破不指人（爆破队台账根本没有，编一个队号出来是假的）。
///     原版这一条不分工序地判过，于是"只要本班有一炮，整盘一条都下达不了"，
///     而给的理由是"未指定主设备"—— 照着它去查会跑去设备台账里找爆破队。
///     闸拦的是"漏填"，不该拦"按口径就不填"。</item>
/// </list>
/// </summary>
public static class DispatchEngine
{
    public const string CodeNoPlan = "无计划";
    public const string CodeNoDestination = "缺去向";
    public const string CodeNoEquipment = "缺主设备";
    public const string CodeTruckShortage = "运力不足";
    public const string CodeHaulMissing = "缺运距";

    public static IssueCheck ValidateForIssue(ExploderResult? result, string? shift = null,
                                              IEnumerable<string>? taskIds = null)
    {
        var chk = new IssueCheck();
        if (result == null)
        {
            chk.Blocks.Add(V(CodeNoPlan, "", "无计划可下达（引擎未产出结果）。"));
            return chk;
        }

        var scope = taskIds == null ? null : new HashSet<string>(taskIds, StringComparer.OrdinalIgnoreCase);
        var tasks = result.Tasks
            .Where(t => t.Process != ProcessType.Idle)
            .Where(t => string.IsNullOrWhiteSpace(shift) || string.Equals(t.Shift, shift, StringComparison.Ordinal))
            .Where(t => scope == null || scope.Contains(t.Id))
            .ToList();
        chk.TaskCount = tasks.Count;

        if (tasks.Count == 0)
        {
            chk.Blocks.Add(V(CodeNoPlan, "",
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

        // ② 缺去向
        foreach (var t in tasks.Where(NeedsDestination).Where(t => t.TargetVolumeM3 > 1 && !t.HasDestination))
            chk.Blocks.Add(V(CodeNoDestination, t.Id,
                $"{t.WorkZone} {t.Process.Label()} {t.TargetVolumeM3:N0} m³ 未指定卸点 —— "
              + "任务书写不出「这车拉到哪」，运距/配车/运输功均无法核算；"
              + "请先在「作业面台账」给这个面指派去向。"));

        // ③ 缺主设备（爆破笔豁免，理由见类文档）
        foreach (var t in tasks.Where(t => string.IsNullOrWhiteSpace(t.Group.MainEquipment)
                                        && t.Process != ProcessType.Blast))
            chk.Blocks.Add(V(CodeNoEquipment, t.Id,
                $"{t.WorkZone} {t.Process.Label()} 未指定主设备，任务无法下达到设备。"));

        // ── 提醒（不阻止）：需运输却没配车、去向有了但没运距 ──
        foreach (var t in tasks.Where(t => t.Process == ProcessType.Load && t.TargetVolumeM3 > 1 && t.Group.Trucks.Count == 0))
            chk.Warnings.Add(W(CodeTruckShortage, t.Id,
                $"{t.WorkZone} 尚未配车（建议 {t.Group.RecommendedTrucks} 台），下达后无法展开派车单。"));

        foreach (var t in tasks.Where(t => NeedsDestination(t) && t.HasDestination && t.EquivHaulKm <= 1e-6))
            chk.Warnings.Add(W(CodeHaulMissing, t.Id,
                $"{t.WorkZone} → {t.DestinationName} 无运距，车次时刻只能按兜底值估算。"));

        return chk;
    }

    /// <summary>这一笔要不要卸点：采装/运输/排土要，穿孔/爆破不要。</summary>
    public static bool NeedsDestination(ShiftTask t)
        => t.Process is ProcessType.Load or ProcessType.Haul or ProcessType.Dump;

    private static PlanViolation V(string code, string taskId, string msg) => new()
    { Severity = ViolationSeverity.Error, Code = code, TaskId = taskId, Message = msg };

    private static PlanViolation W(string code, string taskId, string msg) => new()
    { Severity = ViolationSeverity.Warn, Code = code, TaskId = taskId, Message = msg };
}
