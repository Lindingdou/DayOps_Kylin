// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/FactSource.Range.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data;              // EquipmentDataContext（班次生产记录）
using PitMine3D.Kylin.Data;              // EquipmentCategory
using PitMine3D.Kylin.Data.Entities;     // ProductionRecord as DbProductionRecord
using PitMine3D.Kylin.Platform;                // ProjectPeriodFormat
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using DbShiftRecord = PitMine3D.Kylin.Data.Entities.ProductionRecord;

namespace PitMine3D.Kylin.TaskLib.Reporting;

// ─────────────────────────────────────────────────────────────────────────────
//  按日期区间取数 —— 报表的「日 / 周 / 月」从此是三份不同的数，不再是同一天出三遍。
//
//  ── 三个数据槽（谁能提供什么，一目了然）──────────────────────────────────
//   槽①【任务级实绩】actuals/{日期}_{班次}.json + 当日内存盘子
//        六元组齐全（期·源·物料·量·汇·运距），是**量类指标的唯一可信来源**。
//        只有录过实绩的日子才有——没录就是没录，不拿别的源去凑。
//
//   槽②【班次台账】production_record（设备 × 日 × 班）
//        只贡献【工时 / 故障工时】——这两列口径明确、单位是小时。
//        ★ 产量列 output_m3 暂不入量，原因写在 <see cref="ShiftRecordVolumeTrusted"/>：
//          它在钻机/电铲/卡车/前装机四类设备上都有值，四类相加等于把同一批料按
//          穿孔→采装→运输记三遍；且仅电铲一类的月合计就是月剥离计划的 7 倍以上，
//          单位口径未经确认。接错一个系数，报表会给出一个"看着很专业的错数"。
//
//   槽③【几何测量】面模型差分（期初面 ↔ 期末面，按块体模型分煤岩）
//        计划改用三维图纸表达后，实际完成量应由实测面算出，而不是人工填报。
//        此处只留注入点 <see cref="GeometrySource"/>，接上即生效，引擎无需改动。
//
//  ── 不叠加纪律 ────────────────────────────────────────────────────────────
//  同一天只认一个量源，优先级 槽③ > 槽① > 槽②(不计量)。
//  任务级实绩与班次台账都记着同一台铲同一个班，两边相加就是把当班产量记两遍——
//  这与本包一贯的「采装侧/排土侧不叠加」「源侧/汇侧不叠加」是同一条纪律。
// ─────────────────────────────────────────────────────────────────────────────

public static partial class FactSource
{
    /// <summary>
    /// 槽③注入点：给定日期区间 + <b>周期口径</b>返回几何测量事实（采掘单元台账 / 面模型差分）。
    ///
    /// <para><b>为什么要带 <see cref="PeriodKind"/></b>：几何测量是**按期**的，不是按日的。
    /// 光看 (from,to) 分不出「月报的 08-01~08-20」和「恰好从 1 号起的那一周」——
    /// 后者若也拿到整月的量，一周就报出一个月的产量，而每个数看上去都正常。
    /// 口径由调用方给，源自己决定供不供（见 <see cref="UnitLedgerFactSource.Supplies"/>）。</para>
    ///
    /// 未注入时为 null，取数自动退到任务级实绩。注入方负责保证事实的量口径为【实方 m³】。
    /// </summary>
    public static Func<DateTime, DateTime, PeriodKind?, IEnumerable<ProductionFact>>? GeometrySource { get; set; }

    /// <summary>
    /// 槽②（<c>production_record</c>）是否供数。<b>默认 false</b>。
    ///
    /// <para><b>2026-08-20 由用户判定作废</b>：库里这张表只有 <c>2026-05</c> 一个月，
    /// 而那批数据「是无效的，我只做了 2026-08」。默认关掉整个槽②，而不是只关产量列 ——
    /// 只关产量列的话，工时/故障工时还会照供，报表上就会出现一段
    /// 「看着很正常的历史工时」，而它同样出自那批作废数据。</para>
    ///
    /// <para>今后接进来的班次台账若口径已确认，置 true 即可启用，无需改别处。</para>
    /// </summary>
    public static bool ShiftRecordTrusted { get; set; } = false;

    /// <summary>
    /// 槽②的产量列是否可信。<b>默认 false</b>——<c>production_record.output_m3</c> 的口径未经确认：
    /// 钻机/电铲/卡车/前装机四类设备都有值（同一批料被按工序记了多遍），
    /// 且仅电铲一类的月合计远超月剥离计划量级。确认换算口径后置 true 即可启用，
    /// 在此之前它只贡献工时与故障工时。
    /// <para>它在 <see cref="ShiftRecordTrusted"/> 之下：整个槽②关着的时候这一项无意义。</para>
    /// </summary>
    public static bool ShiftRecordVolumeTrusted { get; set; } = false;

    /// <summary>最近一次 <see cref="ForRange"/> 的取数说明（哪几天有量、量从哪来、哪几天只有工时）。</summary>
    public static string LastRangeLabel { get; private set; } = "";

    /// <summary>本次区间里"有量"的日期数（供 UI 与达成评价判断样本够不够）。</summary>
    public static int LastRangeDaysWithVolume { get; private set; }

    /// <summary>本次区间的日历天数。</summary>
    public static int LastRangeDays { get; private set; }

    /// <summary>
    /// 本次取数是不是【期级】的（量来自槽③采掘单元台账，整期一个数，落不到具体某一天）。
    /// <para>调用方据此措辞：期级口径下说「已录 N 天」是错的 —— 一天都没"录"，
    /// 那是按期实测出来的。</para>
    /// </summary>
    public static bool LastRangeIsPeriodLevel { get; private set; }

    // ═════════════════════════════════════════════════════════════════════════
    //  主入口
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 取 [from, to]（含两端）内的全部生产事实。逐日取数、逐日选源，跨日不叠加。
    /// 区间内一天数据都没有时返回空表（报表会整片显示"—"并附缺口脚注，这比补零诚实）。
    /// </summary>
    public static List<ProductionFact> ForRange(DateTime from, DateTime to) => ForRange(from, to, null);

    /// <summary>
    /// 同上，但带上**周期口径**。槽③（期级实测）只在月口径下供数，见
    /// <see cref="UnitLedgerFactSource.Supplies"/>；不给口径 ⇒ 槽③不供。
    /// </summary>
    public static List<ProductionFact> ForRange(DateTime from, DateTime to, PeriodKind? period)
    {
        var d0 = from.Date;
        var d1 = to.Date;
        if (d1 < d0) (d0, d1) = (d1, d0);

        var facts = new List<ProductionFact>();
        var fromBoard = new List<string>();
        var fromEntry = new List<string>();
        var hoursOnly = new List<string>();
        var fromGeometry = new List<string>();
        int emptyDays = 0;

        string mine = ProjectScope.MineName;
        var today = ProjectScope.WorkDate;

        // 槽③：几何测量整段一次性取（面模型是按期的，不是按日的）
        var geometry = TryGeometry(d0, d1, period);
        var geometryDays = new HashSet<DateTime>(geometry.Select(f => f.Date.Date));
        facts.AddRange(geometry);

        // ★ 期级供数 ⇒ **整个区间**都算覆盖过了，逐日循环整段跳过（EV7 不叠加）。
        //   原先只把"事实落在哪一天"标成已覆盖，于是一份供了整月量的月报
        //   末尾还会挂一句「无数据 19 天」—— 一个月的量摆在上面、下面写着大半个月没数据，
        //   两句都出自同一次取数，读的人只能挑一句信。
        //   更要紧的是那 19 天会去翻槽①/②，翻到什么就叠加什么，期级量当场被记两遍。
        bool periodLevel = geometry.Count > 0 && UnitLedgerFactSource.Supplies(period);
        if (periodLevel) fromGeometry.Add($"整期覆盖（{RangeLabel(d0, d1)}）");
        else if (geometryDays.Count > 0) fromGeometry.Add($"{geometryDays.Count} 天");

        for (var d = d0; !periodLevel && d <= d1; d = d.AddDays(1))
        {
            if (geometryDays.Contains(d)) continue;   // 已由几何测量覆盖，不再取别的量源

            // 当前作业日：直接用内存盘子（含计划量与已回灌的实绩，六元组最全）
            if (d == today)
            {
                var board = FromCurrentBoard();
                if (board.Count > 0)
                {
                    facts.AddRange(board);
                    fromBoard.Add(d.ToString("MM-dd"));
                    continue;
                }
            }

            // 历史日：任务级实绩
            var recs = LoadActuals(d);
            if (recs.Count > 0)
            {
                facts.AddRange(FromActualRecords(recs, d, mine));
                fromEntry.Add(d.ToString("MM-dd"));
                continue;
            }

            // 兜底：班次台账（只有工时/故障，没有量）
            var shiftFacts = FromShiftRecords(d, mine);
            if (shiftFacts.Count > 0)
            {
                facts.AddRange(shiftFacts);
                hoursOnly.Add(d.ToString("MM-dd"));
                continue;
            }

            emptyDays++;
        }

        LastRangeDays = (int)(d1 - d0).TotalDays + 1;
        LastRangeIsPeriodLevel = periodLevel;
        // 期级供数时"有量的天"就是整个区间 —— 事实虽然都戳在截至日那一天，
        // 它说的却是整期的量。按 geometryDays 数会得到"有量 1 天 / 共 20 天"，
        // 而调用方（达成度月对账）拿这个数去写"已录 N 天"，就成了一句和量对不上的话。
        LastRangeDaysWithVolume = periodLevel
            ? LastRangeDays
            : fromGeometry.Count > 0
                ? geometryDays.Count + fromBoard.Count + fromEntry.Count
                : fromBoard.Count + fromEntry.Count;
        LastRangeLabel = BuildRangeLabel(d0, d1, fromGeometry, fromBoard, fromEntry, hoursOnly, emptyDays);
        return facts;
    }

    /// <summary>按周期口径推出区间：日 = 当天；周 = 含当天的自然周（周一起）；月 = 当月 1 日至当天。</summary>
    public static (DateTime From, DateTime To) RangeOf(PeriodKind period, DateTime anchor)
    {
        var a = anchor.Date;
        return period switch
        {
            // 月报按「月初 → 锚点日」而不是整月：报的是"到今天为止本月干了多少"，
            // 把未来那些还没干的日子算进来只会让达成率无端偏低。
            PeriodKind.Month => (new DateTime(a.Year, a.Month, 1), a),
            PeriodKind.Week => (a.AddDays(-(((int)a.DayOfWeek + 6) % 7)), a),
            _ => (a, a),
        };
    }

    /// <summary>区间文案「2026-08-01 ~ 2026-08-04（4 天）」。</summary>
    public static string RangeLabel(DateTime from, DateTime to)
    {
        var d0 = from.Date; var d1 = to.Date;
        if (d0 == d1) return ProjectPeriodFormat.DateLabel(d0);
        return $"{d0:yyyy-MM-dd} ~ {d1:yyyy-MM-dd}（{(int)(d1 - d0).TotalDays + 1} 天）";
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  槽① 任务级实绩（历史日）
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 当日内存盘子（计划 + 已回灌实绩）。
    ///
    /// <para><b>口径</b>：与评价分析组其余两窗一致 —— **只算下达过的**（用户 2026-08-20 定）。
    /// 生产报告与达成度、产量统计摆在同一组里，三份数出自同一个盘子却按不同口径算，
    /// 是最难查的一类对不上（见 [[done-column-plan-vs-actual]]）。
    /// 未下达 / 已撤回的条数由 <c>ProductionPlanContext.DispatchSourceLabel</c> 报出来，
    /// 不是悄悄扣掉的。</para>
    /// </summary>
    public static List<ProductionFact> FromCurrentBoard()
        => FromTasks(SampleTaskBoard.Day().Where(DispatchStateLink.CountsForEvaluation).ToList(),
                     ParseDate(SampleTaskBoard.DateLabel), ProjectScope.MineName);

    private static List<ActualRecord> LoadActuals(DateTime d)
    {
        try { return TaskPersistence.LoadActualsOfDay(ProjectPeriodFormat.DateLabel(d)); }
        catch { return new List<ActualRecord>(); }
    }

    /// <summary>
    /// 一条已落盘的班末实绩 → 一条事实。
    ///
    /// <para>
    /// 与 <see cref="FromTasks"/> 的差别只在**混采拆条**：单据只记了主物料
    /// （<c>ActualRecord.MaterialCode</c> 是份额最大的那一项），历史事实因此按单一物料计。
    /// 这会让混采面的煤/岩在历史报表里并到主物料上——如实以脚注说明，不做份额猜测：
    /// 猜出来的煤岩比会直接污染剥采比这个核心指标。
    /// </para>
    /// </summary>
    private static List<ProductionFact> FromActualRecords(IEnumerable<ActualRecord> records, DateTime date, string mine)
    {
        var list = new List<ProductionFact>();
        foreach (var r in records)
        {
            if (r == null) continue;

            var spec = MaterialCatalog.Exists(r.MaterialCode) ? MaterialCatalog.Resolve(r.MaterialCode) : null;
            bool movesMaterial = r.Process is ProcessType.Load or ProcessType.Haul;
            bool hasDest = !string.IsNullOrWhiteSpace(r.DestinationId) || !string.IsNullOrWhiteSpace(r.DestinationName);
            bool haulKnown = movesMaterial && r.EffectiveHaulKm > 1e-6;

            var f = new ProductionFact
            {
                TaskId = r.TaskId,
                MaterialFraction = 1,
                IsPrimaryMaterial = true,

                Date = date,
                Shift = r.Shift,
                Mine = mine,
                Panel = r.WorkZone,
                EngineeringPositionId = r.EngineeringPositionId ?? "",

                Equipment = r.EquipId,
                Process = r.Process.Label(),
                ProcessKind = r.Process,
                CountsAsMined = r.Process == ProcessType.Load,
                IsDumpReceipt = r.Process == ProcessType.Dump,
                MovesMaterial = movesMaterial,

                Material = spec?.Name ?? "",
                MaterialCode = spec?.Code ?? "",
                MaterialName = spec?.Name ?? "—",
                MaterialKind = spec == null ? "—" : spec.Kind.Label(),
                IsOre = spec?.IsOre ?? false,
                HasMaterial = spec != null,
                DensityTPerM3 = spec?.InSituDensityTPerM3 ?? 0,

                DestinationId = hasDest ? r.DestinationId : "",
                DestinationName = hasDest ? r.DestinationName : "",
                DestinationKind = r.DestinationKind,
                DestinationKindLabel = hasDest ? r.DestinationKind.Label() : "—",
                HasDestination = hasDest,
                IsDumpingDestination = hasDest && r.DestinationKind.IsDumping(),

                PlanVolumeM3 = r.PlanVolumeM3,
                ActualVolumeM3 = r.ActualVolumeM3,
                ShortfallM3 = Math.Max(0, r.PlanVolumeM3 - r.ActualVolumeM3),
                // 吨量与占容方存盘即定，此处直接采信，不再按今天的物料表重算
                ActualTonnage = r.ActualTonnageT,
                PlanTonnage = spec?.ToTonnage(r.PlanVolumeM3) ?? 0,
                PlanLooseM3 = spec?.ToLooseM3(r.PlanVolumeM3) ?? 0,
                ActualLooseM3 = spec?.ToLooseM3(r.ActualVolumeM3) ?? 0,
                PlanDumpM3 = spec?.ToDumpM3(r.PlanVolumeM3) ?? 0,
                DumpVolumeM3 = r.ActualDumpM3,

                PlannedHours = r.PlanHours,
                ActualHours = r.ActualHours,

                HaulKnown = haulKnown,
                HaulDistanceKm = haulKnown ? r.EffectiveHaulKm : 0,
                EquivHaulKm = haulKnown ? r.EffectiveHaulKm : 0,
                EffectiveHaulKm = haulKnown ? r.EffectiveHaulKm : 0,

                Status = r.ActualVolumeM3 <= 1e-6 ? "计划" : r.AttainmentPct >= 98 ? "完成" : "部分完成",
                TopReason = FirstReason(r.Reasons),
            };

            f.PlanTransportWorkTKm = haulKnown ? f.PlanTonnage * r.EffectiveHaulKm : 0;
            f.TransportWorkTKm = haulKnown ? f.ActualTonnage * r.EffectiveHaulKm : 0;
            f.FuelL = FuelOf(r.Process, r.ActualVolumeM3, f.TransportWorkTKm);
            f.PowerKwh = r.Process == ProcessType.Load ? r.ActualVolumeM3 * PowerShovelKwhPerM3 : 0;

            if (r.AshPct is > 0 && f.IsOre)
            {
                f.HasQuality = true;
                f.Ash = r.AshPct.Value;
            }

            list.Add(f);
        }
        return list;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  槽② 班次台账（只贡献工时 / 故障）
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>production_record</c>（设备 × 日 × 班）→ 事实。**默认不带量**（见
    /// <see cref="ShiftRecordVolumeTrusted"/>）：只把工时与故障工时接进来，
    /// 让"设备干了多少小时、坏了多少小时"这类指标在历史日期上出真值。
    /// </summary>
    private static List<ProductionFact> FromShiftRecords(DateTime date, string mine)
    {
        // EV8：整个槽②默认关着（库里那批 production_record 已判定作废）。
        // 关在最外层而不是逐列置零 —— 逐列置零仍会产出一批"有设备、有工序、量为 0"的事实，
        // 设备维报表照样列出这些设备，看着像"这些设备当天没干活"，而事实是这批数据不作数。
        if (!ShiftRecordTrusted) return new List<ProductionFact>();

        List<DbShiftRecord> rows;
        try
        {
            rows = EquipmentDataContext.Production.ByDate(date)?.Where(r => r != null).ToList()
                   ?? new List<DbShiftRecord>();
        }
        catch { return new List<ProductionFact>(); }

        if (rows.Count == 0) return new List<ProductionFact>();

        var category = EquipmentCategories();
        var list = new List<ProductionFact>();

        foreach (var r in rows)
        {
            var cat = category.TryGetValue(r.EquipmentId ?? "", out var c) ? c : "";
            var process = ProcessOf(cat);

            var f = new ProductionFact
            {
                TaskId = $"SR-{r.EquipmentId}-{date:yyyyMMdd}-{r.Shift}",
                MaterialFraction = 1,
                IsPrimaryMaterial = true,

                Date = date,
                Shift = ShiftName(r.Shift),
                Mine = mine,
                Panel = "（班次台账·未记作业面）",

                Equipment = r.EquipmentId ?? "",
                Process = process.Label(),
                ProcessKind = process,

                // 量一律不计：本条没有物料、没有去向、没有运距，
                // 且产量列口径未确认（见 ShiftRecordVolumeTrusted）。
                CountsAsMined = false,
                IsDumpReceipt = false,
                MovesMaterial = false,
                HasMaterial = false,
                MaterialName = "—",
                MaterialKind = "—",
                HasDestination = false,
                DestinationKindLabel = "—",
                HaulKnown = false,

                ActualHours = Math.Max(0, r.WorkHours),
                // 计划工时按班次时长兜底：本表没有计划工时列，
                // 用它算出的"工时利用率"是【出勤工时 ÷ 班时长】，口径写在报表脚注里。
                PlannedHours = ShiftLengthH,
                Status = "完成",
                TopReason = string.IsNullOrWhiteSpace(r.FaultReason) ? "" : r.FaultReason!.Trim(),
            };

            if (ShiftRecordVolumeTrusted && process == ProcessType.Load)
            {
                f.CountsAsMined = true;
                f.MovesMaterial = true;
                f.ActualVolumeM3 = Math.Max(0, r.OutputM3);
            }

            list.Add(f);
        }
        return list;
    }

    /// <summary>一个班的名义时长 h（班次台账没有计划工时列时的分母）。</summary>
    private const double ShiftLengthH = 8;

    /// <summary>
    /// 首要未完成原因。空表不能直接 FirstOrDefault ——枚举默认值是「设备故障」，
    /// 会把"根本没有异常"的任务统统标成故障，归因分析随即全错（与 <see cref="FromTasks"/> 同一处坑）。
    /// </summary>
    private static string FirstReason(List<IncompleteReason>? reasons)
    {
        if (reasons == null) return "";
        foreach (var r in reasons)
            if (r != IncompleteReason.OverAchieved) return r.Label();
        return "";
    }

    private static Dictionary<string, string> EquipmentCategories()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var e in EquipmentDataContext.Equipment.All())
                if (e != null && !string.IsNullOrWhiteSpace(e.EquipmentId)) map[e.EquipmentId.Trim()] = e.Category ?? "";
        }
        catch { /* 设备台账读不到 → 工序按"其它"计 */ }
        return map;
    }

    /// <summary>设备类别 → 工序（班次台账只记设备，工序靠类别推）。</summary>
    private static ProcessType ProcessOf(string category) => category switch
    {
        nameof(EquipmentCategory.Shovel) or nameof(EquipmentCategory.Loader) => ProcessType.Load,
        nameof(EquipmentCategory.Truck) => ProcessType.Haul,
        nameof(EquipmentCategory.Dozer) => ProcessType.Dump,
        nameof(EquipmentCategory.Drill) => ProcessType.Drill,
        _ => ProcessType.Idle,
    };

    /// <summary>
    /// 班次码 → 中文名。<b>转发 <see cref="Engine.WorkCalendar.ShiftName"/></b>，不在这儿再抄一份
    /// A/B/C 对照表 —— 这已经是全仓第三份了，加一个班制就得记得三处都改，而漏改不会有任何报错。
    /// 只有"空码"这一档本处口径不同：报表里空码是「全天」，日历里是「班次」。
    /// </summary>
    private static string ShiftName(string? code)
    {
        string c = (code ?? "").Trim();
        return c.Length == 0 ? "全天" : Engine.WorkCalendar.ShiftName(c);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  槽③ 几何测量
    // ═════════════════════════════════════════════════════════════════════════

    private static List<ProductionFact> TryGeometry(DateTime from, DateTime to, PeriodKind? period)
    {
        var src = GeometrySource;
        if (src == null) return new List<ProductionFact>();
        try { return src(from, to, period)?.Where(f => f != null).ToList() ?? new List<ProductionFact>(); }
        catch { return new List<ProductionFact>(); }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  取数说明
    // ═════════════════════════════════════════════════════════════════════════

    private static string BuildRangeLabel(DateTime d0, DateTime d1,
        List<string> geometry, List<string> board, List<string> entry, List<string> hoursOnly, int emptyDays)
    {
        var parts = new List<string> { $"区间 {RangeLabel(d0, d1)}" };

        // 槽③供了数就把它的出处整句带出来（哪一期、哪个目录、推定了几笔去向）——
        // 「几何测量 1 天」这种说法看不出量是从哪来的，而这一源恰恰是整月的量。
        if (geometry.Count > 0) parts.Add(UnitLedgerFactSource.LastLabel);
        if (board.Count > 0) parts.Add($"当日盘子 {Join(board)}");
        if (entry.Count > 0) parts.Add($"已录实绩 {entry.Count} 天（{Join(entry)}）");
        if (hoursOnly.Count > 0) parts.Add($"仅工时 {hoursOnly.Count} 天（班次台账，无量）");
        if (emptyDays > 0) parts.Add($"无数据 {emptyDays} 天");

        if (board.Count + entry.Count + geometry.Count == 0)
        {
            parts.Add("★ 本区间没有任何量类数据，量指标一律显示「—」");
            // 空的时候必须说清是「没供」还是「供了但没有」—— 这两件事的补法完全不同。
            if (UnitLedgerFactSource.LastLabel.Length > 0) parts.Add("槽③：" + UnitLedgerFactSource.LastLabel);
            if (!ShiftRecordTrusted) parts.Add("槽②：班次台账已判定作废，默认不供数");
        }

        return string.Join(" · ", parts);
    }

    private static string Join(List<string> days)
        => days.Count <= 4 ? string.Join("、", days) : string.Join("、", days.Take(4)) + $"… 共 {days.Count} 天";

    private static DateTime ParseDate(string label)
    {
        var head = ProjectPeriodFormat.DateKey(label);
        return DateTime.TryParse(head, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Today;
    }
}
