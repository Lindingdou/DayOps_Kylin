// 忠实移植自原 PitMine3D Modules/TaskLib/Adjust/DayStagePlan.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;

namespace PitMine3D.Kylin.TaskLib.Adjust;

// ─────────────────────────────────────────────────────────────────────────────
//  日级作业环节时间轴 —— 「动态调整」从一天的盘子扩到一段日历天。
//
//  ── 为什么要单独一层，而不是把 ProductionTask 直接铺到日历上 ──
//  当日盘子（ProductionPlanContext.Day()）只有**一天**：作业面、班次、编组、实绩
//  全部围绕 ProjectScope.WorkDate 那一天装配。要按天看推进、按天点开演示，
//  就得有「第 d 天的第 k 个环节」这个坐标，而任务台账里没有这一维。
//
//  ── 三层来源（能拿真的绝不造假的；每一层都在 SourceLabel 里写明）──
//    ① 作业日当天：直接投影当日盘子的真任务（含实绩、原因码）—— Actual = true
//    ② 同月其它天：沿用当日盘子的**区域×工序结构**，按班次日历判该天是不是作业日。
//       量的口径是自洽的：当日盘子各面的日目标本来就是 ShortTermLink 用
//       「月计划量 ÷ 当月作业日数 × 面份额」摊出来的，所以别的作业日复用同一份量
//       不是编数据，而是同一个除法的另一天。这些天一律 Projected = true。
//    ③ 无月计划 / 无班次日历：不猜。该缺的天留空并把原因写进 Notes。
//
//  ── 一条纪律 ──
//  推算天与真实天在数据结构上必须分得开（Projected 标志），界面才有可能标出来。
//  把两者混成一样的对象，图上就再也看不出「这一天是有人排的，那一天是我算的」。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 一天里的一个作业环节：某区域 × 某工序 × 某时窗。
/// 甘特图上的一根条、三维演示的一次播放，都以它为单位。
/// </summary>
public sealed class DayStage
{
    public DateTime Date { get; set; }
    /// <summary>作业区域名（采场面 / 排土场）——与 <c>SimRegion.Name</c> 对名，三维演示据此定位。</summary>
    public string RegionName { get; set; } = "";
    public ProcessType Process { get; set; }
    /// <summary>班次名（早/中/夜 或 A/B/C）。跨班合并的环节为空串。</summary>
    public string Shift { get; set; } = "";
    /// <summary>当日起止 (0..24)。</summary>
    public double StartHour { get; set; }
    public double EndHour { get; set; }

    /// <summary>目标量：采装 = 实方 m³；排土 = 占容方 m³；穿孔/爆破/检修 = 0。</summary>
    public double TargetVolumeM3 { get; set; }
    /// <summary>实绩量（只有真实录入过的天才有；推算天恒为 0）。</summary>
    public double ActualVolumeM3 { get; set; }

    public string MainEquip { get; set; } = "";
    /// <summary>参与的设备台数（编组主设备 + 配属卡车）。</summary>
    public int EquipCount { get; set; }

    // ── 产能三件套：跨天顺延要靠它们判「后面那天还塞不塞得下」，不许拍系数 ──
    /// <summary>
    /// 编组班产 m³/h —— <see cref="EquipGroup.GroupCapacityM3PerH"/>，
    /// 由 <c>FleetMatcher</c> 按循环时间 T_c 解出（min(铲装能力, 车队运力)）。0 = 未解出。
    /// </summary>
    public double CapacityM3PerH { get; set; }
    /// <summary>本环节已排工时 h（= 工作量 ÷ 编组班产）。</summary>
    public double PlannedHours { get; set; }
    /// <summary>
    /// 本环节所占班次的**班时长合计** h（工时上限）。
    /// 余量工时 = <see cref="ShiftSpanH"/> − <see cref="PlannedHours"/>，
    /// 余量能力 = 余量工时 × <see cref="CapacityM3PerH"/> —— 这是"还能塞多少"的唯一诚实来源。
    /// </summary>
    public double ShiftSpanH { get; set; }

    /// <summary>
    /// 由跨天顺延追加进来的量 m³（<see cref="DayRollover"/> 写）。
    /// <b>刻意不并进 <see cref="TargetVolumeM3"/></b>：并进去之后就再也说不清
    /// 某天的量里哪部分是原计划、哪部分是补回来的。
    /// </summary>
    public double RolledInM3 { get; set; }

    /// <summary>本环节顺延后的计划量 = 原计划 + 顺延进来的。</summary>
    public double PlannedTotalM3 => TargetVolumeM3 + RolledInM3;

    /// <summary>本环节今天还能追加的量 m³（产能解不出时为 0，调用方须按「判不了」处理，不是按「塞不下」）。</summary>
    public double SpareCapacityM3 => CapacityM3PerH > 1e-6
        ? Math.Max(0, (ShiftSpanH - PlannedHours) * CapacityM3PerH)
        : 0;
    /// <summary>产能是否解得出（false ⇒ 上面那个 0 是"不知道"，不是"没有余量"）。</summary>
    public bool CapacityResolved => CapacityM3PerH > 1e-6 && ShiftSpanH > 1e-6;
    public string DestinationName { get; set; } = "";
    public string MaterialLabel { get; set; } = "";
    /// <summary>台阶标高 m（三维演示定层用；0 = 未知）。</summary>
    public double BenchElevationM { get; set; }

    /// <summary>未完成原因码（只有真实天才可能非空）。</summary>
    public List<IncompleteReason> Reasons { get; set; } = new();

    /// <summary>
    /// <b>计划侧</b>来自推算（沿用当日盘子结构摊到该天），不是有人排出来的。
    /// 与 <see cref="ActualFromLedger"/> 相互独立：一条环节完全可以「计划是推算的、实绩是真录的」——
    /// 历史某天没人排任务，但班末实绩确实录了。两个标志合成一个的话，
    /// 这类天要么被当成全假、要么被当成全真，两种都错。
    /// </summary>
    public bool Projected { get; set; }

    /// <summary>
    /// <b>实绩侧</b>来自已落盘的班末实绩（<c>actuals/{日期}_{班次}.json</c>）。
    /// 这是本仓库认定的**量类唯一可信源**（见 FactSource.Range.cs 头注：
    /// <c>production_record.output_m3</c> 四类设备各记一遍、量级对不上，默认不入量）。
    /// </summary>
    public bool ActualFromLedger { get; set; }

    /// <summary>实绩工时 h（来自已落盘实绩，或班次台账 <c>production_record</c>）。</summary>
    public double ActualHours { get; set; }
    /// <summary>故障工时 h（同上）。</summary>
    public double FaultHours { get; set; }

    /// <summary>
    /// 工时来自**班次台账**（<c>production_record</c>）而不是落盘实绩。
    /// <para>这两个源的可信度不一样：班次台账只有工时可信、量不可信；落盘实绩六元组齐全。
    /// 界面要能分辨「这条的工时是真的、但它旁边那个量是推算的」。</para>
    /// </summary>
    public bool HoursFromShiftLedger { get; set; }

    /// <summary>
    /// 本条是**实绩带出来的**：当日盘子的结构里没有这个「区域 × 工序 × 班次」，
    /// 但那天确实录了实绩。不建这条的话，这段真实作业量在图上整段消失。
    /// </summary>
    public bool ActualOnly { get; set; }

    /// <summary>真实天时挂着原任务（推算天为 null）。重排、明细卡走它。</summary>
    public ProductionTask? Task { get; set; }

    public double ShortfallM3 => Math.Max(0, TargetVolumeM3 - ActualVolumeM3);
    public bool HasShortfall => ActualVolumeM3 > 1e-6 && ShortfallM3 > 1e-6;
    public bool IsAbnormal => Reasons.Any(r => r != IncompleteReason.OverAchieved);
    public double DurationH => Math.Max(0, EndHour - StartHour);

    /// <summary>环节在甘特/详情里的标题。</summary>
    public string Caption => $"{Date:MM-dd} · {RegionName} · {Process.Label()}";

    /// <summary>区域 + 工序，是「同一条环节线」的键（跨天连成一行）。</summary>
    public string LaneKey => RegionName + "" + (int)Process;
}

/// <summary>一个日历天：日期 + 是否作业日 + 当天的全部环节。</summary>
public sealed class DayPlan
{
    public DateTime Date { get; set; }
    /// <summary>班次日历上有排班（或无日历时按日历天全排）。false = 检修/停产日，无量。</summary>
    public bool IsWorkday { get; set; } = true;
    /// <summary>当天排的班次名（来自班次日历；无日历时为空）。</summary>
    public List<string> Shifts { get; set; } = new();
    /// <summary>当天有爆破班。</summary>
    public bool HasBlast { get; set; }
    public string Weather { get; set; } = "";
    /// <summary>这一天是当日盘子那一天（真任务 + 实绩），不是推算的。</summary>
    public bool IsActualDay { get; set; }
    /// <summary>这一天有已落盘的班末实绩（可以是历史任意一天，不限于当日盘子那天）。</summary>
    public bool HasActuals { get; set; }
    public List<DayStage> Stages { get; set; } = new();

    /// <summary>当天计划总量 m³（采装实方 + 排土占容，与甘特/报表同口径）。</summary>
    public double PlanM3 => Stages.Where(s => s.Process is ProcessType.Load or ProcessType.Dump)
                                  .Sum(s => s.TargetVolumeM3);
    public double ActualM3 => Stages.Where(s => s.Process is ProcessType.Load or ProcessType.Dump)
                                    .Sum(s => s.ActualVolumeM3);
    public bool HasAbnormal => Stages.Any(s => s.IsAbnormal);
    public string Label => $"{Date:MM-dd} {WeekLabel}";
    public string WeekLabel => Date.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一", DayOfWeek.Tuesday => "周二", DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四", DayOfWeek.Friday => "周五", DayOfWeek.Saturday => "周六",
        _ => "周日",
    };
    public bool IsWeekend => Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}

/// <summary>一段日历天的作业环节时间轴。</summary>
public sealed class DayStageTimeline
{
    public List<DayPlan> Days { get; set; } = new();
    /// <summary>来源一句话（界面顶栏显示「这条时间轴可不可信」）。</summary>
    public string SourceLabel { get; set; } = "";
    /// <summary>降级 / 口径说明，一条都不吞。</summary>
    public List<string> Notes { get; set; } = new();
    /// <summary>作业日那一天在 <see cref="Days"/> 里的下标（-1 = 不在区间内）。</summary>
    public int ActualDayIndex { get; set; } = -1;

    public bool IsEmpty => Days.Count == 0 || Days.All(d => d.Stages.Count == 0);
    public IEnumerable<DayStage> AllStages => Days.SelectMany(d => d.Stages);

    /// <summary>全部环节线（区域 × 工序），按区域名再按工序序排。</summary>
    public List<(string Region, ProcessType Process)> Lanes()
        => AllStages.Select(s => (s.RegionName, s.Process))
                    .Distinct()
                    .OrderBy(x => x.RegionName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => (int)x.Process)
                    .ToList();

    /// <summary>某天某条环节线上的环节（一天内可能跨多班，故返回列表）。</summary>
    public List<DayStage> At(DateTime date, string region, ProcessType p)
        => Days.FirstOrDefault(d => d.Date.Date == date.Date)?.Stages
               .Where(s => string.Equals(s.RegionName, region, StringComparison.OrdinalIgnoreCase) && s.Process == p)
               .ToList() ?? new List<DayStage>();
}

// ═════════════════════════════════════════════════════════════════════════════
//  装配
// ═════════════════════════════════════════════════════════════════════════════

public static class DayStagePlanBuilder
{
    /// <summary>默认铺满作业日所在的自然月。</summary>
    public static DayStageTimeline BuildMonth() => Build(null, null);

    /// <summary>
    /// 装配日级时间轴。<paramref name="from"/>/<paramref name="to"/> 为 null 时取作业日所在自然月。
    /// 任何一层数据缺失都降级并把原因写进 <see cref="DayStageTimeline.Notes"/>，绝不抛。
    /// </summary>
    public static DayStageTimeline Build(DateTime? from, DateTime? to)
    {
        var tl = new DayStageTimeline();
        try
        {
            return BuildCore(tl, from, to);
        }
        catch (Exception ex)
        {
            tl.SourceLabel = $"日级时间轴构建失败：{ex.GetType().Name}";
            tl.Notes.Add("时间轴未能构建，界面已降级为空。请检查当日盘子与班次日历是否可读。");
            return tl;
        }
    }

    private static DayStageTimeline BuildCore(DayStageTimeline tl, DateTime? from, DateTime? to)
    {
        DateTime workDate;
        try { workDate = ProjectScope.WorkDate.Date; }
        catch { workDate = DateTime.Today; }

        DateTime d0 = (from ?? new DateTime(workDate.Year, workDate.Month, 1)).Date;
        DateTime d1 = (to ?? d0.AddMonths(1).AddDays(-1)).Date;
        if (d1 < d0) (d0, d1) = (d1, d0);
        // 上限一年：日级甘特再长就不是「动态调整」的尺度了，且会把渲染拖垮
        if ((d1 - d0).TotalDays > 366) d1 = d0.AddDays(366);

        // ── 当日盘子（结构来源 + 作业日那天的真数据）──
        List<ProductionTask> tasks;
        try { tasks = ProductionPlanContext.Day() ?? new List<ProductionTask>(); }
        catch (Exception ex)
        {
            tl.SourceLabel = $"当日任务盘子不可读（{ex.GetType().Name}）";
            tl.Notes.Add("日级时间轴以当日盘子的「区域 × 工序」结构为骨架，盘子读不出来就没有骨架。"
                       + "请先打开一次「生产任务编制」或检查引擎接线。");
            return tl;
        }
        if (tasks.Count == 0)
        {
            tl.SourceLabel = "当日盘子里没有任务";
            tl.Notes.Add("当日盘子为空 ⇒ 无「区域 × 工序」结构可铺到日历上，时间轴为空。");
            return tl;
        }

        // ── 班次窗口（算工时上限用；读不到就按任务自身时窗兜底）──
        var shiftSpan = ReadShiftSpans();

        // ── 班次日历（判哪些天是作业日）──
        var cal = ReadCalendar(d0, d1, out string calLabel, out bool calAvailable);
        // ── 月计划（只用来在文案里说明日均口径是怎么来的，不参与二次摊算）──
        var month = ReadMonth(out string monthLabel);

        bool inRange = workDate >= d0 && workDate <= d1;

        for (DateTime d = d0; d <= d1; d = d.AddDays(1))
        {
            cal.TryGetValue(d, out var cd);
            var day = new DayPlan
            {
                Date = d,
                // 无班次日历时不敢判停产 —— 一律按作业日排，并在 Notes 里说清楚
                IsWorkday = calAvailable ? cd is { Shifts.Count: > 0 } : true,
                Shifts = cd?.Shifts ?? new List<string>(),
                HasBlast = cd?.HasBlast ?? false,
                Weather = cd?.Weather ?? "",
                IsActualDay = inRange && d == workDate,
            };

            if (day.IsActualDay) day.Stages = ProjectReal(tasks, d, shiftSpan);
            else if (day.IsWorkday) day.Stages = ProjectPlanned(tasks, d, day.HasBlast, shiftSpan);
            // 非作业日：无环节（检修/停产），但这一天仍然占一列，图上是个空档

            tl.Days.Add(day);
            if (day.IsActualDay) tl.ActualDayIndex = tl.Days.Count - 1;
        }

        // ── 跨天实绩回灌（量的唯一可信源）──
        BackfillActuals(tl);
        // ── 跨天工时回灌（班次台账**只**贡献工时/故障工时，量一概不取）──
        BackfillEquipmentHours(tl, tasks);

        int workdays = tl.Days.Count(x => x.IsWorkday);
        tl.SourceLabel = $"日级时间轴 · {d0:yyyy-MM-dd} ~ {d1:MM-dd}（{tl.Days.Count} 天，其中作业日 {workdays} 天）"
                       + $"　·　{SampleOrLedgerTag()}　·　{calLabel}";

        // 盘子是样例还是台账，是整张图**可不可以拿去开会**的前提，必须顶在最前面
        AppendBoardProvenance(tl);

        if (inRange)
            tl.Notes.Add($"{workDate:MM-dd} 是当前作业日：该天的环节是**当日盘子的真任务**（含已录实绩与原因码）；"
                       + "其余天为推算（图上以浅色/虚边区分）。");
        else
            tl.Notes.Add($"当前作业日 {workDate:yyyy-MM-dd} 不在本区间内 ⇒ 全部天都是推算，没有任何一天带真实绩。");

        tl.Notes.Add("推算天的口径：沿用当日盘子的「区域 × 工序 × 班次 × 日目标」结构原样铺到该天。"
                   + "各面日目标本就是 ShortTermLink 按「月计划量 ÷ 当月作业日数 × 面份额」摊出来的，"
                   + "所以别的作业日复用同一份量是同一个除法的另一天，不是另编的数。");

        if (!calAvailable)
            tl.Notes.Add("⚠ 拿不到本区间的排班 ⇒ 判不出哪天检修停产，本区间**每一天都按作业日**铺。"
                       + "这会把月度总量算大（真实矿山一个月总有检修日），也会让跨天顺延以为后面有更多天可用。"
                       + $"具体是哪一种情况见上方来源文案。（同一份日历还喂着 WorkCalendar.MonthWorkdays，"
                       + $"那边拿不到就回落写死的 {WorkCalendar.FallbackMonthWorkdays:0} 天，月计划的日目标就是按那个数摊的。）");
        else
            tl.Notes.Add($"作业日判据取自班次日历：该日有排班即为作业日（本区间 {workdays}/{tl.Days.Count} 天）。"
                       + "非作业日不铺任何环节，图上留空档。");

        tl.Notes.Add(monthLabel);
        CrossCheckWorkdays(tl, d0, d1, calAvailable);
        return tl;
    }

    // ── 盘子出处 ─────────────────────────────────────────────────────────────

    /// <summary>顶栏那一小段：这盘数据是台账的还是样例的。</summary>
    private static string SampleOrLedgerTag()
    {
        try
        {
            return ProductionPlanContext.FacesFromLedger
                ? "盘子：作业面台账"
                : "盘子：**样例种子**（非真实台账）";
        }
        catch { return "盘子：来源未知"; }
    }

    /// <summary>
    /// 把「这张图能不能拿去开会」讲清楚。
    ///
    /// <para><b>为什么值得单列一条</b>：作业面档案（<c>working_face_routing</c>）为空时，
    /// 盘子回落样例种子面，而样例盘子会**合成实绩**（<c>SampleTaskBoard.ApplySampleActuals</c>，
    /// 那是它的老行为、演示用）。于是甘特上会出现欠量、异常角标、跨天顺延全套画面 ——
    /// 每一个数都自洽，但**没有一个来自现场**。不说这一句，看图的人没有任何办法分辨。</para>
    /// </summary>
    private static void AppendBoardProvenance(DayStageTimeline tl)
    {
        bool fromLedger;
        string faceLabel = "";
        try { fromLedger = ProductionPlanContext.FacesFromLedger; faceLabel = ProductionPlanContext.FaceSourceLabel; }
        catch { return; }

        if (fromLedger)
        {
            tl.Notes.Add($"盘子出处：作业面来自台账。{faceLabel}"
                       + " 实绩只认「实绩录入」里真填过的那些，没填就保持计划状态（不合成）。");
            return;
        }

        tl.Notes.Add("⚠ **这盘数据是样例种子，不是现场数据。**"
                   + $"作业面档案为空 ⇒ 回落样例面（{faceLabel}），而样例盘子会**合成实绩** —— "
                   + "所以图上的欠量、异常角标、跨天顺延全都成立且自洽，但没有一个来自现场。"
                   + "要看真数据，需要补齐三样：①「作业面台账」录作业面；②「班次日历」排班；"
                   + "③「实绩录入」逐班存班末实绩。三样缺一，这张图就只是演示。");
    }

    // ── 跨天实绩回灌 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 把已落盘的班末实绩（<c>actuals/{日期}_{班次}.json</c>）灌到对应的环节上。
    ///
    /// <para><b>为什么只认这一个源</b>：本仓库已经查过 <c>production_record.output_m3</c> ——
    /// 钻机/电铲/卡车/前装机四类设备都有值（同一批料按穿孔→采装→运输记了三遍），
    /// 且仅电铲一类的月合计就是月剥离计划的 7 倍以上，单位口径未确认，
    /// 所以 <c>FactSource.ShiftRecordVolumeTrusted</c> 默认 false，那张表**只贡献工时**。
    /// 拿它回灌实绩量会得到一个「看着很专业的错数」，这正是本仓库最不该做的事。</para>
    ///
    /// <para><b>量的口径随极性走</b>（与 <see cref="VolumeOf"/> 同一套）：
    /// 排土取 <c>ActualDumpM3</c>（占容方），其余取 <c>ActualVolumeM3</c>（实方）。
    /// 混用会让排土侧的达成率凭空多出一个 Kr 倍。</para>
    ///
    /// <para><b>对不上的实绩要建条，不能丢</b>：某天录了实绩、但当日盘子的结构里没有那个
    /// 「区域 × 工序 × 班次」（作业面换过、当时排的面今天不排了），若只做匹配更新，
    /// 这段真实作业量在图上会整段消失，而画面看着完全正常。所以对不上就补一条并记账。</para>
    /// </summary>
    private static void BackfillActuals(DayStageTimeline tl)
    {
        int daysHit = 0, matched = 0, added = 0, unmatchedSkipped = 0;

        foreach (var day in tl.Days)
        {
            List<ActualRecord> recs;
            try
            {
                recs = TaskPersistence.LoadActualsOfDay(PitMine3D.Kylin.Platform.ProjectPeriodFormat.DateLabel(day.Date))
                       ?? new List<ActualRecord>();
            }
            catch { continue; }
            if (recs.Count == 0) continue;

            daysHit++;
            day.HasActuals = true;

            foreach (var r in recs)
            {
                string region = RegionOfActual(r);
                if (region.Length == 0) { unmatchedSkipped++; continue; }

                double vol = r.Process == ProcessType.Dump ? r.ActualDumpM3 : r.ActualVolumeM3;

                // 先按「区域 + 工序 + 班次」找；找不到放宽到「区域 + 工序」（班次名可能改过）
                var hit = day.Stages.FirstOrDefault(s => s.Process == r.Process
                            && string.Equals(s.RegionName, region, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(s.Shift, r.Shift, StringComparison.OrdinalIgnoreCase))
                       ?? day.Stages.FirstOrDefault(s => s.Process == r.Process
                            && string.Equals(s.RegionName, region, StringComparison.OrdinalIgnoreCase));

                if (hit != null)
                {
                    hit.ActualVolumeM3 += Math.Max(0, vol);
                    hit.ActualHours += Math.Max(0, r.ActualHours);
                    hit.FaultHours += Math.Max(0, r.FaultHours);
                    hit.ActualFromLedger = true;
                    foreach (var rs in r.Reasons)
                        if (!hit.Reasons.Contains(rs)) hit.Reasons.Add(rs);
                    matched++;
                }
                else
                {
                    day.Stages.Add(new DayStage
                    {
                        Date = day.Date,
                        RegionName = region,
                        Process = r.Process,
                        Shift = r.Shift,
                        TargetVolumeM3 = Math.Max(0, r.PlanVolumeM3),
                        ActualVolumeM3 = Math.Max(0, vol),
                        ActualHours = Math.Max(0, r.ActualHours),
                        FaultHours = Math.Max(0, r.FaultHours),
                        MainEquip = r.EquipId,
                        EquipCount = 1,
                        DestinationName = r.DestinationName,
                        Reasons = new List<IncompleteReason>(r.Reasons),
                        Projected = true,           // 计划侧确实没人排过这一条
                        ActualFromLedger = true,
                        ActualOnly = true,
                    });
                    added++;
                }
            }
        }

        if (daysHit == 0)
        {
            tl.Notes.Add("跨天实绩：本区间内**一天都没有已落盘的班末实绩**（actuals/），"
                       + "所以除作业日外全是光计划。实绩要在「实绩录入」里逐班保存才会进这条时间轴。"
                       + "注：production_record 台账**不作数** —— 它的产量列四类设备各记一遍、量级对不上，"
                       + "本仓库已判定为不可信（FactSource.ShiftRecordVolumeTrusted = false）。");
            return;
        }

        tl.Notes.Add($"跨天实绩：{daysHit} 天有已落盘的班末实绩（量的唯一可信源 actuals/），"
                   + $"其中 {matched} 条对上了计划环节、{added} 条**计划里没有**（已补建为独立环节，不丢账）"
                   + (unmatchedSkipped > 0 ? $"、{unmatchedSkipped} 条无作业面无去向已跳过" : "")
                   + "。排土侧取占容方、其余取实方，与计划侧同口径。");
    }

    // ── 跨天工时回灌（班次台账）───────────────────────────────────────────────

    /// <summary>
    /// 把 <c>production_record</c>（设备 × 日 × 班）的**工时与故障工时**灌到对应环节上。
    ///
    /// <para><b>只取这两列，量一概不取。</b>该表的产量列 <c>output_m3</c> 在钻机/电铲/卡车/前装机
    /// 四类设备上都有值（同一批料按穿孔→采装→运输记了三遍），且仅电铲一类的月合计就是
    /// 月剥离计划的 7 倍以上，单位口径未确认 —— 仓库已判定不可信
    /// （<c>FactSource.ShiftRecordVolumeTrusted</c> 默认 false）。工时与故障工时不同：
    /// 口径明确、单位是小时，是这张表唯一能用的东西。</para>
    ///
    /// <para><b>设备 → 区域靠当日盘子的编组结构反推</b>：台账按设备记，不记作业面。
    /// 一台铲在盘子里配在哪个面，就把它的工时算到那个面上。盘子结构本身若是样例
    /// （作业面档案为空时），这层映射也就是样例的 —— 已在盘子出处那条里说明。</para>
    ///
    /// <para><b>已落盘班末实绩优先</b>：<c>actuals/</c> 里录过的环节不再被本表覆盖 ——
    /// 那边的工时是按任务时段汇总的，与量同源；两边相加就是把当班工时记两遍。</para>
    /// </summary>
    private static void BackfillEquipmentHours(DayStageTimeline tl, List<ProductionTask> tasks)
    {
        // 设备 → (区域, 工序)。主设备算它自己的面；配属卡车算它服务的那个采装面。
        var owner = new Dictionary<string, (string Region, ProcessType Process)>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tasks)
        {
            if (!Countable(t)) continue;
            string region = RegionOf(t);
            if (!string.IsNullOrWhiteSpace(t.Group.MainEquipment))
                owner[t.Group.MainEquipment] = (region, t.Process);
            if (t.Process == ProcessType.Load)
                foreach (var truck in t.Group.Trucks)
                    if (!string.IsNullOrWhiteSpace(truck) && !owner.ContainsKey(truck))
                        owner[truck] = (region, ProcessType.Load);
        }
        if (owner.Count == 0)
        {
            tl.Notes.Add("班次台账工时：当日盘子里没有编组 ⇒ 设备对不到作业面，工时无处可落。");
            return;
        }

        int daysHit = 0, matched = 0, unmapped = 0, skippedHasActual = 0;
        double faultH = 0, workH = 0;
        var unknownEquip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool anyRead = false;

        _probeFrom = tl.Days.Count > 0 ? tl.Days[0].Date.Date : DateTime.MinValue;
        _probeTo = tl.Days.Count > 0 ? tl.Days[^1].Date.Date : DateTime.MaxValue;

        foreach (var day in tl.Days)
        {
            if (!day.IsWorkday) continue;

            IReadOnlyList<PitMine3D.Kylin.Data.Entities.ProductionRecord> recs;
            try { recs = PitMine3D.Kylin.Data.EquipmentDataContext.Production.ByDate(day.Date); }
            catch { return; }          // 台账未接通：一次失败就整体放弃，不逐天重试
            anyRead = true;
            if (recs == null || recs.Count == 0) continue;

            bool used = false;
            foreach (var r in recs)
            {
                if (r == null || string.IsNullOrWhiteSpace(r.EquipmentId)) continue;
                if (!owner.TryGetValue(r.EquipmentId, out var key))
                { unmapped++; unknownEquip.Add(r.EquipmentId); continue; }

                var hit = day.Stages.FirstOrDefault(s => s.Process == key.Process
                    && string.Equals(s.RegionName, key.Region, StringComparison.OrdinalIgnoreCase));
                if (hit == null) { unmapped++; continue; }

                // 已落盘实绩的环节不许再叠 —— 那边的工时与量同源，叠上去就是记两遍
                if (hit.ActualFromLedger) { skippedHasActual++; continue; }

                hit.ActualHours += Math.Max(0, r.WorkHours);
                hit.FaultHours += Math.Max(0, r.FaultHours);
                hit.HoursFromShiftLedger = true;
                workH += Math.Max(0, r.WorkHours);
                faultH += Math.Max(0, r.FaultHours);
                matched++; used = true;
            }
            if (used) daysHit++;
        }

        if (!anyRead) return;

        if (matched == 0)
        {
            // 一条都没对上时，把「表里到底有什么」也说出来 —— 只说"没对上"会让人以为表是空的，
            // 而实际常见的情况是：表有数据，只是**日期不在本区间**或**设备号是另一套编号**。
            // 这两种的下一步动作完全不同，不指出来人就只能瞎找。
            tl.Notes.Add("班次台账工时：本区间内一条都没对上。" + WhyNoShiftRows(unmapped, unknownEquip));
            return;
        }

        tl.Notes.Add($"班次台账工时：{daysHit} 天 / {matched} 条记录已灌入（累计出勤 {workH:N0} h，故障 {faultH:N0} h）。"
                   + "**只取工时与故障工时**，该表的产量列口径未确认（四类设备各记一遍、量级对不上），一概不取。"
                   + (skippedHasActual > 0 ? $" 另有 {skippedHasActual} 条因该环节已有落盘实绩而跳过（不重复记账）。" : "")
                   + (unmapped > 0
                       ? $" ⚠ {unmapped} 条记录的设备不在当日盘子的编组里，工时落不到面上："
                         + $"{string.Join("、", unknownEquip.Take(6))}{(unknownEquip.Count > 6 ? " 等" : "")}。"
                       : ""));
    }

    /// <summary>
    /// 「班次台账一条都没对上」的具体原因。两种常见情形要分开说，下一步动作完全不同：
    /// <list type="bullet">
    /// <item><b>设备号是另一套编号</b>：表里有本区间的记录，但设备号与盘子里的编组对不上
    ///       （台账用工号如 <c>1647</c>，样例盘子用 <c>WK-10</c> 这种）⇒ 去把作业面台账的编组填成真设备号。</item>
    /// <item><b>日期不在本区间</b>：表非空但覆盖的是别的月份 ⇒ 把区间切到那几个月，或补录本月。</item>
    /// </list>
    /// </summary>
    private static string WhyNoShiftRows(int unmapped, HashSet<string> unknownEquip)
    {
        if (unmapped > 0)
            return $"本区间里有 {unmapped} 条记录，但设备号与当日盘子的编组对不上："
                 + $"{string.Join("、", unknownEquip.Take(8))}{(unknownEquip.Count > 8 ? " 等" : "")}。"
                 + "班次台账按设备工号记，盘子里的编组得填成同一套编号才对得起来。";

        // 逐日查一行都没取到 —— 到底是"这张表没有这段时间的数据"，还是"按日查询本身取不到"？
        // 这两种的下一步完全不同，必须靠**整表覆盖范围**当场分开，不能猜。
        try
        {
            var all = PitMine3D.Kylin.Data.EquipmentDataContext.Production.All();
            if (all == null || all.Count == 0)
                return "该表整体为空（一条班次记录都没有）。";

            var min = all.Min(r => r.Date.Date);
            var max = all.Max(r => r.Date.Date);
            int equips = all.Select(r => r.EquipmentId ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count();
            string cover = $"该表**有** {all.Count:N0} 条记录、{equips} 台设备，覆盖 {min:yyyy-MM-dd} ~ {max:yyyy-MM-dd}";

            bool overlaps = min <= _probeTo && max >= _probeFrom;
            if (!overlaps)
                return cover + "，**与本区间不重叠** ⇒ 把区间切到那一段就能看到真实工时与故障工时；本月的要去「实绩录入」补。";

            // 重叠却一条都取不到 ⇒ 是查询取不到，不是没有数据。
            return cover + "，**与本区间是重叠的，却一条都取不到** —— 这不是数据缺失，是"
                 + "`IProductionService.ByDate(DateTime)` 按日取不到行。"
                 + "该表 `date` 列存的是**只有日期的文本**（如 `2026-05-01`），而按日查询传的是 DateTime，"
                 + "会被序列化成带时分秒的形式，等值比对必然落空（实测 `date='2026-05-04'` 有 918 行，"
                 + "`'2026-05-04 00:00:00'` 为 0 行）。**同一条通路上的报表工时/故障指标也会静默为零。**";
        }
        catch { return "本区间内该表没有记录（整体覆盖范围读取失败）。"; }
    }

    /// <summary>本次回灌探查的区间（只供 <see cref="WhyNoShiftRows"/> 判「重不重叠」）。</summary>
    private static DateTime _probeFrom, _probeTo;

    /// <summary>
    /// 一条实绩归到哪块区域。与 <see cref="RegionOf"/> 同一套规则：
    /// 排土认去向名（排土场才是它作业的地方），其余认作业面名。两者皆空返回空串（调用方跳过并记账）。
    /// </summary>
    private static string RegionOfActual(ActualRecord r)
    {
        if (r.Process == ProcessType.Dump)
        {
            if (!string.IsNullOrWhiteSpace(r.DestinationName)) return r.DestinationName.Trim();
            if (!string.IsNullOrWhiteSpace(r.WorkZone)) return r.WorkZone.Trim();
            return "";
        }
        return string.IsNullOrWhiteSpace(r.WorkZone) ? "" : r.WorkZone.Trim();
    }

    // ── 真实天：当日盘子逐任务投影 ────────────────────────────────────────────

    /// <summary>作业日那天：一条任务 = 一个环节，原样带过来（含实绩与原因码）。</summary>
    private static List<DayStage> ProjectReal(List<ProductionTask> tasks, DateTime date,
                                              Dictionary<string, double> shiftSpan)
    {
        var list = new List<DayStage>();
        foreach (var t in tasks)
        {
            if (!Countable(t)) continue;
            list.Add(new DayStage
            {
                CapacityM3PerH = t.Group.GroupCapacityM3PerH,
                PlannedHours = t.PlannedHours,
                ShiftSpanH = SpanOf(t, shiftSpan),
                Date = date,
                RegionName = RegionOf(t),
                Process = t.Process,
                Shift = t.Shift,
                StartHour = t.StartHour,
                EndHour = t.EndHour,
                TargetVolumeM3 = VolumeOf(t),
                ActualVolumeM3 = t.ActualVolumeM3,
                MainEquip = t.Group.MainEquipment,
                EquipCount = 1 + (t.Process == ProcessType.Load ? t.Group.Trucks.Count : 0),
                DestinationName = t.DestinationName,
                MaterialLabel = t.ResolvedMix.Caption,
                BenchElevationM = t.BenchElevationM,
                Reasons = new List<IncompleteReason>(t.Reasons),
                Projected = false,
                Task = t,
            });
        }
        return Merge(list);
    }

    // ── 推算天：同一套结构换个日期 ───────────────────────────────────────────

    /// <summary>
    /// 非作业日之外的其它天：沿用当日盘子的结构，量原样带过来，实绩清零、原因码清空。
    /// <para>爆破环节只在班次日历标了爆破班的那天保留 —— 爆破不是天天有的工序，
    /// 天天铺一条爆破条会让图上看起来每天都在放炮。</para>
    /// </summary>
    private static List<DayStage> ProjectPlanned(List<ProductionTask> tasks, DateTime date, bool hasBlast,
                                                 Dictionary<string, double> shiftSpan)
    {
        var list = new List<DayStage>();
        foreach (var t in tasks)
        {
            if (!Countable(t)) continue;
            if (t.Process == ProcessType.Blast && !hasBlast) continue;
            list.Add(new DayStage
            {
                CapacityM3PerH = t.Group.GroupCapacityM3PerH,
                PlannedHours = t.PlannedHours,
                ShiftSpanH = SpanOf(t, shiftSpan),
                Date = date,
                RegionName = RegionOf(t),
                Process = t.Process,
                Shift = t.Shift,
                StartHour = t.StartHour,
                EndHour = t.EndHour,
                TargetVolumeM3 = VolumeOf(t),
                ActualVolumeM3 = 0,                 // 推算天没有实绩，留 0 而不是复制真实绩
                MainEquip = t.Group.MainEquipment,
                EquipCount = 1 + (t.Process == ProcessType.Load ? t.Group.Trucks.Count : 0),
                DestinationName = t.DestinationName,
                MaterialLabel = t.ResolvedMix.Caption,
                BenchElevationM = t.BenchElevationM,
                Projected = true,
                Task = null,                        // 推算条不挂真任务：重排只能动真实那天
            });
        }
        return Merge(list);
    }

    /// <summary>
    /// 同区域 + 同工序 + 同班次的条合并成一根 —— 一个面一个班可能派了两台铲，
    /// 甘特上画成两根重叠的条既看不清也点不准。设备名合成「WK-10 等 2 台」。
    /// </summary>
    private static List<DayStage> Merge(List<DayStage> raw)
    {
        var outp = new List<DayStage>();
        foreach (var g in raw.GroupBy(s => s.RegionName + "" + (int)s.Process + "" + s.Shift))
        {
            var items = g.ToList();
            if (items.Count == 1) { outp.Add(items[0]); continue; }

            var head = items[0];
            var m = new DayStage
            {
                Date = head.Date,
                RegionName = head.RegionName,
                Process = head.Process,
                Shift = head.Shift,
                StartHour = items.Min(s => s.StartHour),
                EndHour = items.Max(s => s.EndHour),
                TargetVolumeM3 = items.Sum(s => s.TargetVolumeM3),
                ActualVolumeM3 = items.Sum(s => s.ActualVolumeM3),
                MainEquip = $"{head.MainEquip} 等 {items.Count} 台",
                EquipCount = items.Sum(s => s.EquipCount),
                DestinationName = string.Join("、", items.Select(s => s.DestinationName)
                                                        .Where(x => x.Length > 0).Distinct()),
                MaterialLabel = string.Join("、", items.Select(s => s.MaterialLabel)
                                                      .Where(x => x.Length > 0).Distinct()),
                BenchElevationM = head.BenchElevationM,
                Projected = head.Projected,
                Reasons = items.SelectMany(s => s.Reasons).Distinct().ToList(),
                Task = items.FirstOrDefault(s => s.Task != null)?.Task,

                // 产能可加（两台铲在同一个面同一个班 = 两份编组班产），工时上限取同一个班窗不重复计
                CapacityM3PerH = items.Sum(s => s.CapacityM3PerH),
                PlannedHours = items.Max(s => s.PlannedHours),
                ShiftSpanH = items.Max(s => s.ShiftSpanH),
            };
            outp.Add(m);
        }
        return outp.OrderBy(s => s.StartHour).ThenBy(s => (int)s.Process).ToList();
    }

    // ── 口径小工具 ───────────────────────────────────────────────────────────

    /// <summary>空闲条不进时间轴（检修要进 —— 那是真占设备的环节）。</summary>
    private static bool Countable(ProductionTask t)
        => t.Process != ProcessType.Idle || t.TargetVolumeM3 > 1e-6 || !string.IsNullOrWhiteSpace(t.Material);

    /// <summary>
    /// 环节的量：排土取**占容方**，其余取实方 —— 两个口径不可混加，
    /// 与 SimBuilder / 甘特 FillTotals 保持同一套。
    ///
    /// <para>★ 2026-08-22 修：排土那一支原来写的是 <c>t.TargetDumpM3</c>，
    /// 而那个属性是 <c>ToDumpM3(TargetVolumeM3)</c> —— 拿实方折占容。两条产出路径都不对：
    /// 分解器路的排土量在 <c>DumpVolumeM3</c>、<c>TargetVolumeM3</c> 是 0 ⇒ 这里恒得 <b>0</b>；
    /// 装箱路那时把**已经是占容方**的数写在 <c>TargetVolumeM3</c> 里 ⇒ 这里**又乘一次 Kr**（≈+13%）。
    /// 一条恒零、一条虚胀，而两条都不报错。改走 <see cref="TaskQuantity"/> —— 四本账在那里只定义一次。</para>
    /// </summary>
    private static double VolumeOf(ProductionTask t)
        => t.Process == ProcessType.Dump ? TaskQuantity.Of(t).Value : t.TargetVolumeM3;

    /// <summary>
    /// 环节归到哪块区域：排土类先认去向名（排土场才是它作业的地方，WorkZone 常写的是来料面），
    /// 其余认 WorkZone。两个都空时归「未命名区域」而不是丢掉。
    /// </summary>
    private static string RegionOf(ProductionTask t)
    {
        if (t.Process == ProcessType.Dump)
        {
            if (!string.IsNullOrWhiteSpace(t.DestinationName)) return t.DestinationName.Trim();
            if (!string.IsNullOrWhiteSpace(t.WorkZone)) return t.WorkZone.Trim();
            return "未命名排土场";
        }
        if (!string.IsNullOrWhiteSpace(t.WorkZone)) return t.WorkZone.Trim();
        return "未命名区域";
    }

    /// <summary>
    /// 一条任务的工时上限 h：优先取它所在班次的**班窗长度**，班次名对不上就退回任务自身时窗。
    /// <para>不用 24h 兜底 —— 那会让"余量工时"凭空多出十几个小时，跨天顺延就会算出一个
    /// 谁都执行不了的追加量。</para>
    /// </summary>
    private static double SpanOf(ProductionTask t, Dictionary<string, double> shiftSpan)
    {
        if (!string.IsNullOrWhiteSpace(t.Shift) && shiftSpan.TryGetValue(t.Shift, out double h) && h > 1e-6)
            return h;
        double own = t.EndHour - t.StartHour;
        return own > 1e-6 ? own : 0;
    }

    /// <summary>班次名 → 班窗长度 h。读不到返回空表（调用方按任务自身时窗兜底）。</summary>
    private static Dictionary<string, double> ReadShiftSpans()
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cfg = ProductionPlanContext.Config();
            foreach (var s in cfg.Shifts)
            {
                double len = s.End - s.Start;
                if (len <= 1e-6) len += 24;          // 跨零点班（16→0）
                if (len > 1e-6 && !string.IsNullOrWhiteSpace(s.Name)) map[s.Name] = len;
            }
        }
        catch { }
        return map;
    }

    // ── 班次日历 ─────────────────────────────────────────────────────────────

    private sealed class CalDay
    {
        public List<string> Shifts = new();
        public bool HasBlast;
        public string Weather = "";
    }

    /// <summary>
    /// 读区间内的班次日历。读不到时 available=false，调用方按「每天都是作业日」降级。
    ///
    /// <para><b>作业日口径与 <see cref="WorkCalendar"/> 是同一条</b>：某日只要有一条班次记录就算作业日。
    /// 那边是月级（<c>MonthWorkdays</c> 只给一个天数），这边要逐日明细（哪天有班、有没有爆破班、天气），
    /// 所以各读各的；但**规则只有一条**，班次名也统一走 <see cref="WorkCalendar.ShiftName"/> 规范化
    /// （台账存的是 A/B/C，界面和任务盘子用的是早/中/夜，不归一两边显示就对不上）。</para>
    /// </summary>
    private static Dictionary<DateTime, CalDay> ReadCalendar(DateTime d0, DateTime d1,
                                                             out string label, out bool available)
    {
        var map = new Dictionary<DateTime, CalDay>();
        // ⚠ 「读不到台账」与「台账通了但这段时间没排班」是两件事，给的建议完全不同：
        //   前者要去查接线，后者要去排班。早先两种都显示成"未接通"，看的人会往错方向找。
        label = "班次日历：**读不到台账**（接线问题）→ 按日历天全排";
        available = false;
        try
        {
            var rows = PitMine3D.Kylin.Data.EquipmentDataContext.ShiftCalendar.InRange(d0, d1);
            if (rows == null) return map;
            label = $"班次日历：台账可读，但 {d0:yyyy-MM-dd} ~ {d1:MM-dd} **一天都没排班** → 按日历天全排"
                  + "（去「班次日历」把本月排一遍，作业日才算得准）";
            foreach (var r in rows)
            {
                var key = r.Date.Date;
                if (!map.TryGetValue(key, out var cd)) map[key] = cd = new CalDay();
                string sh = WorkCalendar.ShiftName(r.Shift);
                if (sh.Length > 0 && !cd.Shifts.Contains(sh)) cd.Shifts.Add(sh);
                if (r.IsBlastShift) cd.HasBlast = true;
                if (cd.Weather.Length == 0 && !string.IsNullOrWhiteSpace(r.Weather)) cd.Weather = r.Weather!;
            }
            if (map.Count > 0)
            {
                available = true;
                int blast = map.Values.Count(v => v.HasBlast);
                label = $"班次日历：{map.Count} 天有排班"
                      + (blast > 0 ? $"，其中 {blast} 天有爆破班" : "，无爆破班记录")
                      + "（口径同 WorkCalendar：有排班即作业日）";
            }
        }
        catch { /* 台账未接通 → available 保持 false，由调用方如实说明 */ }
        return map;
    }

    /// <summary>
    /// 与 <see cref="WorkCalendar.MonthWorkdays"/> 交叉核对作业日数。
    ///
    /// <para>两处各按同一条规则数一遍，**数出来就得一样**。对不上说明有一边的实现漂了 ——
    /// 而这种漂移不会报错：月计划按 A 的天数摊日目标、甘特按 B 的天数排格子，
    /// 两张图各自都自洽，只有月末对账才发现差了一截。所以这里当场比一次并写进 Notes。</para>
    /// <para>只在区间正好是一个完整自然月时比（<c>MonthWorkdays</c> 只认自然月）。</para>
    /// </summary>
    private static void CrossCheckWorkdays(DayStageTimeline tl, DateTime d0, DateTime d1, bool calAvailable)
    {
        if (!calAvailable) return;
        if (d0.Day != 1 || d1 != d0.AddMonths(1).AddDays(-1)) return;   // 不是整月，不比

        MonthWorkdayInfo info;
        try { info = WorkCalendar.MonthWorkdays(d0); }
        catch { return; }
        if (!info.FromLedger) return;

        int mine = tl.Days.Count(d => d.IsWorkday);
        if (mine == info.Workdays)
            tl.Notes.Add($"作业日交叉核对：本窗数出 {mine} 天，WorkCalendar 数出 {info.Workdays} 天 —— 一致 ✓。");
        else
            tl.Notes.Add($"⚠ **作业日口径不一致**：本窗数出 {mine} 天，而 WorkCalendar.MonthWorkdays 数出 {info.Workdays} 天。"
                       + "两处按的是同一条规则（有排班即作业日），数出来就该一样 —— 不一样说明有一边漂了。"
                       + $"（{info.Label}）月计划的日目标是按 WorkCalendar 那个数摊的，本窗按自己的数排格子，"
                       + "两张图会各自自洽而月末对不上账。");
    }

    /// <summary>读当月计划，只为在界面上讲清「日目标是从哪个月量除出来的」。</summary>
    private static ShortTermLink.MonthInfo? ReadMonth(out string label)
    {
        try
        {
            var m = ShortTermLink.GetMonthInfo(ProjectScope.DateLabel);
            if (m is { HasPlan: true })
            {
                label = $"月计划口径：{m.PlanName}　采出 {m.CoalWanT:0.##}万t · 剥离 {m.StripWanM3:0.##}万m³"
                      + $"　÷ 作业日 {m.Workdays:0.#} 天 ⇒ 日均材料 {m.MaterialWanM3 / Math.Max(1, m.Workdays):0.###}万m³"
                      + "（各面日目标即由此摊出，本时间轴不再二次摊算）。";
                return m;
            }
            label = "月计划口径：**未确定月度方案，台账里也没有本月** ⇒ 当日盘子的日目标来自作业面档案/样例，"
                  + "把它铺满整月得到的月合计不代表任何一份已批准的月计划。";
            return m;
        }
        catch
        {
            label = "月计划口径：读取失败 ⇒ 日目标来源未知，月合计仅供参考。";
            return null;
        }
    }
}
