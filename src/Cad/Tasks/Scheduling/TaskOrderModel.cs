using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Tasks.Scheduling;

/// <summary>任务书上的一行。</summary>
public sealed class OrderRow
{
    public int No { get; set; }
    /// <summary>编组（主设备 + 配车数）。</summary>
    public string Group { get; set; } = "";
    /// <summary>作业地点（面名 + 台阶标高）。</summary>
    public string Place { get; set; } = "";
    /// <summary>卸载地点。<b>必须紧跟作业地点</b> —— 单据的命门是「从哪采 → 拉到哪」。</summary>
    public string Destination { get; set; } = "";
    public string Process { get; set; } = "";
    public string Material { get; set; } = "";

    /// <summary>计划量（按工序取自己那本账；主量一行、折算量/孔数另一行）。没有出处写「—」，不写 0。</summary>
    public string Plan { get; set; } = "";
    /// <summary>量口径名（控制方量 / 原位实方 / 承运量 / 排弃占容；爆破为「—」）。</summary>
    public string Basis { get; set; } = "";
    /// <summary>量的悬停说明（含"为什么没有这个数"）。</summary>
    public string PlanTip { get; set; } = "";

    public string Haul { get; set; } = "";
    public string Span { get; set; } = "";
    /// <summary>作业人员（来自「班组派工」）。<b>没派工留「—」，不编名字</b>。</summary>
    public string Crew { get; set; } = "—";
    /// <summary>下达状态（从单据流水回显，本窗不签发）。</summary>
    public string Issue { get; set; } = "待下达";

    /// <summary>缺卸点 —— 该行标红、且不得下达。</summary>
    public bool NoDestination { get; set; }
    public string TaskId { get; set; } = "";
    public string Key { get; set; } = "";
}

/// <summary>一班的任务书。</summary>
public sealed class OrderDoc
{
    public string PlanDate { get; set; } = "";
    public string Shift { get; set; } = "";
    public List<OrderRow> Rows { get; set; } = new();

    /// <summary>抬头三行：单据元信息 / 分账指标 / 结论句。</summary>
    public string MetaText { get; set; } = "";
    public string MetricsText { get; set; } = "";
    public string SummaryText { get; set; } = "";

    /// <summary>缺卸点清单（有内容即<b>不得下达</b>）。</summary>
    public List<string> MissingSinks { get; set; } = new();

    public bool HasMissingSinks => MissingSinks.Count > 0;
}

/// <summary>
/// 生产任务书的取数与口径（移植原 <c>TaskLib.Order.TaskOrderWindow</c> 的计算部分）。
///
/// <para>
/// <b>单据的命门是「从哪采 → 拉到哪」</b>：作业地点旁必须紧跟卸载地点，计划量必须给出
/// 实方（作业口径）与吨量（考核口径）双口径 —— 这两样是调度签发时判断这班任务合不合理的依据。
/// <b>缺卸点的任务不得下达。</b>
/// </para>
///
/// <para>
/// <b>本层只出单据，不签发。</b>原版这里曾有一个「签发」按钮，走自己那套校验、只改内存状态、
/// 且明写"不落库"，于是"任务书上已签发"与"任务下达里待下达"可以同时为真，两个状态谁也不知道谁。
/// 签发唯一入口是「任务下达」（§三五五，那边落 <see cref="TaskInstance"/> + 回执），
/// 本层只<b>如实回显</b>那边的结果。
/// </para>
///
/// <para>
/// <b>★ 三本方量账不合并</b>：穿孔记控制方量、采装记原位实方、排土记排弃占容，运输记承运吨 ——
/// 任何一处把它们加在一起得到的都不是真实量。抬头一律走 <see cref="TaskQuantity.Sum"/> 分账列示。
/// 原版这里曾写成「计划工作量 = Σ(采装 + 排土) 的 TargetVolumeM3」，而排弃占容方也写在那个字段里，
/// 于是抬头那个"万m³实方"是原位实方与占容方加在一起的数，<b>不对应任何真实量</b>。
/// </para>
///
/// ── Kylin 侧登记的差异 ──
/// <list type="bullet">
///   <item><b>运输笔未移</b>（<c>HaulDumpDeriver</c>）：装箱只出穿孔/采装/排土。于是
///     承运吨、车次、<b>运输功与加权平均运距</b>本盘算不出来 —— 抬头如实写「—」，
///     <b>不拿采装笔的量反推</b>（原版正是从反推改成逐笔取的）。</item>
///   <item><b>作业人员</b>来自「班组派工」（§三五七 已接）；那一班没派工时仍是「—」，<b>不编名字</b>。</item>
///   <item><b>采掘单元号</b>（作业地点第二行小字）依赖未移植的采掘单元台账。</item>
///   <item>打印 / 导出 PDF 未移；本轮出 CSV。</item>
/// </list>
/// </summary>
public static class TaskOrderModel
{
    /// <summary>
    /// 调度任务 → 量核算任务（<b>按工序把量放进它自己那本账</b>）。
    /// <para>
    /// 采装的吨量按物料<b>原位密度</b>折（<see cref="MaterialCatalog"/>）；物料码认不出时按硬岩缺省，
    /// 那是 <c>MaterialCatalog.Resolve</c> 的既定行为，此处不再另立一份。
    /// </para>
    /// <para>
    /// 排土笔的量：装箱把排土面的当日目标写在 <c>TargetVolumeM3</c> 上，故落到
    /// <c>DumpVolumeM3</c>（排弃占容）。<b>这两个字段不是一回事</b> —— 混着加就得到那个
    /// "不对应任何真实量"的数。
    /// </para>
    /// </summary>
    public static ProductionTask ToQuantity(ShiftTask t)
    {
        if (t == null) return new ProductionTask();
        switch (t.Process)
        {
            case ProcessType.Drill:
                return new ProductionTask
                {
                    Process = ProcessType.Drill,
                    Drill = new DrillInfo { PlanHoles = t.DrillHoles ?? 0, PlanMeters = t.DrillMeters ?? 0 },
                };
            case ProcessType.Load:
            {
                var spec = MaterialCatalog.Resolve(MaterialCatalog.CodeFromText(t.Material));
                return new ProductionTask
                {
                    Process = ProcessType.Load,
                    TargetVolumeM3 = t.TargetVolumeM3,
                    TargetTonnageT = t.TargetVolumeM3 * spec.InSituDensityTPerM3,
                    EffectiveHaulKm = t.EquivHaulKm,
                };
            }
            case ProcessType.Dump:
                return new ProductionTask
                {
                    Process = ProcessType.Dump,
                    DumpVolumeM3 = t.TargetVolumeM3,
                    EffectiveHaulKm = t.EquivHaulKm,
                };
            case ProcessType.Haul:
                return new ProductionTask
                {
                    Process = ProcessType.Haul,
                    HaulTonnageT = t.TargetVolumeM3,     // 运输笔的量本就按吨记（装箱暂不产出运输笔）
                    EffectiveHaulKm = t.EquivHaulKm,
                };
            default:
                return new ProductionTask { Process = t.Process };
        }
    }

    /// <summary>这一笔要不要卸点（与下达闸门同一条判据，不在两处各写一遍）。</summary>
    public static bool NeedsDestination(ShiftTask t) => DispatchEngine.NeedsDestination(t);

    /// <summary>缺卸点：要卸点、有量、却没指派。</summary>
    public static bool MissingDestination(ShiftTask t)
        => NeedsDestination(t) && t.TargetVolumeM3 > 1 && !t.HasDestination;

    /// <summary>
    /// 编一班的任务书。<paramref name="issueOf"/> 按稳定键回显下达状态（本层不签发）。
    /// </summary>
    /// <param name="crewOf">(班次, 主设备) → 作业人员文案；null 或查不到时留「—」，<b>不编名字</b>。</param>
    public static OrderDoc Build(IEnumerable<ShiftTask>? tasks, string planDate, string shift,
                                 double nowHour, Func<string, string>? issueOf = null,
                                 Func<string, string, string>? crewOf = null)
    {
        var doc = new OrderDoc { PlanDate = planDate, Shift = shift };

        var list = (tasks ?? Enumerable.Empty<ShiftTask>())
            .Where(t => t != null && t.Process != ProcessType.Idle)
            .Where(t => string.IsNullOrWhiteSpace(shift) || string.Equals(t.Shift, shift, StringComparison.Ordinal))
            .OrderBy(t => t.StartHour)
            .ToList();

        int no = 1;
        foreach (var t in list)
        {
            var q = TaskQuantity.Of(ToQuantity(t));
            string key = TaskKey.Of(t, planDate);
            doc.Rows.Add(new OrderRow
            {
                No = no++,
                TaskId = t.Id,
                Key = key,
                Group = GroupText(t),
                Place = PlaceText(t),
                Destination = DestinationText(t),
                Process = t.Process.Label(),
                Material = MaterialText(t),
                Plan = q.Cell,
                Basis = q.Basis.Length > 0 ? q.Basis : "—",
                PlanTip = q.Why.Length > 0 ? q.Why : q.CaptionWithBasis,
                Haul = t.EquivHaulKm > 1e-6 ? $"{t.EquivHaulKm:0.##} km" : "—",
                Span = $"{Hm(t.StartHour)}–{Hm(t.EndHour)}" + (t.PlannedHours > 0 ? $" / {t.PlannedHours:0.#} h" : ""),
                Crew = crewOf?.Invoke(t.Shift, t.Group.MainEquipment) is { Length: > 0 } c ? c : "—",
                Issue = issueOf?.Invoke(key) ?? "待下达",
                NoDestination = MissingDestination(t),
            });
        }

        doc.MetaText = $"日期：{planDate}　　班次：{(shift.Length > 0 ? shift : "全部")}　　编制时间：{Hm(nowHour)}";

        // 抬头分账（穿孔控制方量 / 采装实方·吨 / 运输承运吨·车次·运输功 / 排土占容）—— 不给总数
        var sum = TaskQuantity.Sum(list.Select(ToQuantity));
        doc.MetricsText = sum.Caption;

        // 去向分布：本盘没有运输笔，故按**采装笔的实方**统计并写明是按什么算的 ——
        // 不折成吨去冒充"承运量"，那是运输笔那本账。
        string sinkBrief = string.Join("、", list
            .Where(t => t.Process == ProcessType.Load && t.TargetVolumeM3 > 1e-6 && t.HasDestination)
            .GroupBy(t => t.DestinationName.Length > 0 ? t.DestinationName : t.DestinationId)
            .OrderByDescending(g => g.Sum(x => x.TargetVolumeM3))
            .Select(g => $"{g.Key} {g.Sum(x => x.TargetVolumeM3) / 1e4:0.##}万m³实方"));

        doc.SummaryText =
            $"本班任务 {doc.Rows.Count} 项（{ProcessBrief(list)}）。"
          + (sinkBrief.Length > 0 ? $" 去向分布（按采装笔实方计）：{sinkBrief}。" : "")
          + " 三本方量账不合并：穿孔记控制方量、采装记原位实方、排土记排弃占容，运输记承运吨 ——"
          + "任何一处把它们加在一起得到的都不是真实量。"
          + " 请各班组按时段、按编组、按卸载地点组织作业，运距与车次以调度指令为准；"
          + "遇故障 / 缺料 / 卸点拥堵及时报调度。";

        foreach (var t in list.Where(MissingDestination))
            doc.MissingSinks.Add($"{PlaceText(t)} {t.Process.Label()} {t.TargetVolumeM3:N0} m³ —— 未指定卸点");

        return doc;
    }

    /// <summary>导出 CSV（BOM 由调用方加；这里只出文本）。</summary>
    public static string ToCsv(OrderDoc doc)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("# ").Append(doc.MetaText.Replace('　', ' ')).Append('\n');
        sb.Append("# ").Append(doc.MetricsText.Replace('　', ' ')).Append('\n');
        sb.Append("序号,编组,作业地点,卸载地点,工序,物料,计划量,量口径,运距,时段,作业人员,下达状态\n");
        foreach (var r in doc.Rows)
            sb.Append(string.Join(",", new[]
            {
                r.No.ToString(CultureInfo.InvariantCulture), Q(r.Group), Q(r.Place), Q(r.Destination), Q(r.Process),
                Q(r.Material), Q(r.Plan.Replace("\n", " ")), Q(r.Basis), Q(r.Haul), Q(r.Span), Q(r.Crew), Q(r.Issue),
            })).Append('\n');
        return sb.ToString();
    }

    private static string Q(string? s)
    {
        string v = s ?? "";
        return v.Contains(',') || v.Contains('"') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    // ── 文案 ────────────────────────────────────────────────

    private static string GroupText(ShiftTask t)
    {
        string main = t.Group.MainEquipment.Length > 0 ? t.Group.MainEquipment : "—";
        return t.Group.Trucks.Count > 0 ? $"{main}（配 {t.Group.Trucks.Count} 车）" : main;
    }

    private static string PlaceText(ShiftTask t)
        => Math.Abs(t.BenchElevationM) > 1e-6
            ? $"{t.WorkZone}（{(t.BenchElevationM >= 0 ? "+" : "")}{t.BenchElevationM:0}）"
            : t.WorkZone;

    private static string DestinationText(ShiftTask t)
        => t.HasDestination ? (t.DestinationName.Length > 0 ? t.DestinationName : t.DestinationId)
         : NeedsDestination(t) ? "未指定卸点"
         : "—";

    private static string MaterialText(ShiftTask t)
    {
        string raw = (t.Material ?? "").Trim();
        if (raw.Length == 0) return "—";
        string code = MaterialCatalog.CodeFromText(raw);
        if (code.Length == 0) return raw;
        var spec = MaterialCatalog.Resolve(code);
        return string.Equals(spec.Name, raw, StringComparison.OrdinalIgnoreCase) ? spec.Name : $"{spec.Name}（{raw}）";
    }

    private static string ProcessBrief(IReadOnlyList<ShiftTask> list)
    {
        var parts = new List<string>();
        foreach (var g in list.GroupBy(t => t.Process).OrderBy(g => (int)g.Key))
            parts.Add($"{g.Key.Label()} {g.Count()}");
        return parts.Count == 0 ? "无" : string.Join(" · ", parts);
    }

    private static string Hm(double hh)
    {
        int h = (int)hh;
        int m = (int)Math.Round((hh - h) * 60);
        if (m == 60) { h++; m = 0; }
        return $"{h:00}:{m:00}";
    }
}
