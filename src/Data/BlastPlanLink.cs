using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;

namespace PitMine3D.Kylin.Data;

/// <summary>爆破台账一炮（对应表 <c>blast_event</c>）。</summary>
public sealed class BlastEventRow
{
    public string BlastDate { get; set; } = "";
    /// <summary>爆破时刻 HH:mm；空 = 台账没记（<b>排不进任何时窗</b>）。</summary>
    public string? BlastTime { get; set; }
    public int? BlastSeq { get; set; }
    public string? DrillId { get; set; }
    /// <summary>平盘编码（对作业面/穿孔计划的待爆区）。</summary>
    public string? LocationCode { get; set; }
    public string? Material { get; set; }
    public int? HoleCount { get; set; }
    public double? TotalHoleLengthM { get; set; }
    public double? ExplosiveKg { get; set; }
    public double? BlastVolumeM3 { get; set; }
    public double? UnitConsumptionKgM3 { get; set; }
}

/// <summary>一炮的排程行。</summary>
public sealed class BlastShotRow
{
    public int? Seq;
    /// <summary>爆破时刻（0..24）；null = 台账没记。</summary>
    public double? Hour;
    public string TimeText = "—";
    public string Location = "";
    public string DrillId = "";
    public string MaterialText = "";

    public int? HoleCount;
    public double? HoleMeters;
    public double? ExplosiveKg;
    public double? VolumeM3;
    public double? UnitKgM3;

    /// <summary>停产窗口文案（爆破 → 清场解除）。</summary>
    public string StopWindow = "—";
    /// <summary>落在哪个班（判不出为空）。</summary>
    public string Shift = "";
    /// <summary>本区的穿孔计划（"DR-1 01:00–08:00"）；没有就说没有。</summary>
    public string DrillText = "无穿孔计划";
    public bool HasDrill;
    /// <summary>衔接采装面（对上就是面名 + 台阶，对不上写为什么）。</summary>
    public string LinkedFace = "";
    public bool Linked;
    public string Status = "";

    /// <summary>装箱按这一炮扣了停产（B5）。<see cref="EngineJudged"/> 为 false 时本字段无意义。</summary>
    public bool DrivesEngine;

    /// <summary>
    /// 「进没进装箱时窗」这件事判得出来吗。false = <b>判不了</b>（当日盘子取不到）——
    /// 判不了不能当成"没进"，那会把一堆红字扣在一个根本没排过的计划上。
    /// </summary>
    public bool EngineJudged = true;

    public string Note = "";
}

/// <summary>Compose 的输入（判据直接构造，不碰库）。</summary>
public sealed class BlastPlanInputs
{
    public DateTime Date;

    /// <summary>爆破台账接不接得通。false ⇒ 只出兜底行 + 一条说明。</summary>
    public bool LedgerUsable = true;

    public List<BlastEventRow> Shots = new();

    /// <summary>当日盘子的作业面（衔接采装面按它对）。</summary>
    public List<FaceInput> Faces = new();

    /// <summary>当日班次窗口（判这一炮落在哪个班）。</summary>
    public List<ShiftWindow> Shifts = new();

    /// <summary>当日穿孔计划（V044 <c>drill_plan</c>）。「衔接」要看的第一件事：这个区的孔谁在打、打完没有。</summary>
    public List<DrillInput> Drills = new();

    /// <summary>此刻几点（状态判定；判据注入固定值）。</summary>
    public double NowHour;

    /// <summary>装箱真正扣掉的<b>全部</b>停产时窗（<c>ExploderConfig.BlastWindows()</c>）。</summary>
    public List<BlastWindow> EngineWindows = new();

    /// <summary>兼容视图：最早一段。<see cref="EngineWindows"/> 为空时用它兜底。</summary>
    public double EngineStart;
    public double EngineEnd;

    /// <summary>
    /// 当日盘子取得到吗。false ⇒ 「进装箱时窗」一列一律<b>判不了</b>，不是"未进"。
    /// <para>
    /// <b>Kylin 侧登记的差异</b>：原版这里有装配层（<c>ProductionPlanContext</c>）把当日盘子装出来，
    /// 那张盘子里的停产时窗就是装箱真正扣掉的。Kylin 的排产装配层尚未移植，
    /// 故 <see cref="BlastPlanLink.Build"/> 一律置 false —— 拿"由台账倒推出来的窗口"去判"进没进计划"
    /// 会永远判通过（自己判自己），那是条会骗人的死路。
    /// </para>
    /// </summary>
    public bool EngineKnown = true;

    /// <summary>装箱扣掉的时窗（列表优先，回落那一对标量）。</summary>
    public IReadOnlyList<BlastWindow> Windows()
        => EngineWindows.Count > 0
            ? BlastWindow.Merge(EngineWindows)
            : EngineEnd > EngineStart + 1e-9
                ? new List<BlastWindow> { new(EngineStart, EngineEnd) }
                : new List<BlastWindow>();

    /// <summary>引擎那个窗口的来源文案。</summary>
    public string EngineBasis = "";
}

/// <summary>本日钻爆排程。</summary>
public sealed class BlastPlanResult
{
    public DateTime Date;
    public List<BlastShotRow> Shots = new();
    public string Header = "";
    public List<string> Notes = new();

    /// <summary>台账里真有炮（false = 兜底行，数字不作数）。</summary>
    public bool FromLedger;

    public int HoleCountSum;
    public double HoleMetersSum;
    public double ExplosiveKgSum;
    public double VolumeM3Sum;

    /// <summary>综合单耗 kg/m³ = Σ装药 ÷ Σ方量（<b>不是</b>各行单耗的平均 —— 那是另一个数）。</summary>
    public double? UnitKgM3 => VolumeM3Sum > 1e-6 ? ExplosiveKgSum / VolumeM3Sum : null;

    /// <summary>几炮没进装箱时窗（B5）。判不了的<b>不计入</b>。</summary>
    public int NotInEngineWindow => Shots.Count(s => s.EngineJudged && s.Hour.HasValue && !s.DrivesEngine);

    /// <summary>几炮的「进没进计划」判不了。</summary>
    public int EngineUnjudged => Shots.Count(s => !s.EngineJudged);

    /// <summary>几炮没对上作业面（B4）。</summary>
    public int Unlinked => Shots.Count(s => !s.Linked);
}

/// <summary>
/// 钻爆计划衔接（移植原 <c>TaskLib.Engine.BlastPlanLink</c> 的 B1–B7 口径）。
///
/// <para>
/// 原「钻爆计划衔接」窗口读的是穿孔计划，而穿孔计划没有台账：真库上 <c>drill_plan</c> 一行都没有；
/// 样例盘子里那一行的「清场警戒」「衔接采装面」「状态」还是三个写死的常量，「爆破窗口」列每行都是同一个全局值。
/// 真正有台账的是 <c>blast_event</c>（炮次/钻机/平盘/物料/孔数/延米/装药/方量/单耗）—— 本类把这张表接进来。
/// </para>
///
/// <list type="bullet">
///   <item><b>B1</b> 一行 = 一炮（<c>blast_event</c>），不是一个"待爆区"。</item>
///   <item><b>B2</b> 停产窗口 = 爆破时刻 + 清场时长（<see cref="ClearanceH"/>，与装箱同一个常数，
///     台账不记恢复作业时刻）。逐炮各算各的，不再全表同一个值。</item>
///   <item><b>B3</b> 状态由「此刻 vs 本炮时刻」推：待爆(还差 x h) / 清场中 / 已爆 / 未记时刻。</item>
///   <item><b>B4</b> 衔接采装面 = 按平盘编码对当日盘子的作业面，对不上就写"未对上"并说明按什么对的 ——
///     不再写死"主采面·东"。</item>
///   <item><b>B5</b> 一炮"进没进计划" = 它在不在装箱真正扣掉的那些时窗里。<b>Kylin 侧目前判不了</b>
///     （排产装配层未移植），故如实报"判不了"而不是"未进" —— 见 <see cref="BlastPlanInputs.EngineKnown"/>。</item>
///   <item><b>B6</b> 火工品合计（孔数/延米/装药/方量/单耗）逐日汇总 —— 这正是任务编制要向火工品计划交的数。</item>
///   <item><b>B7</b> 台账读不通 / 本日无炮时，退回引擎当前采用的那个窗口并<b>标明是兜底</b>，不合成炮次。</item>
/// </list>
///
/// <para><b>登记的差异</b>（与原版逐条对照）：</para>
/// <list type="number">
///   <item>B4 的对法原版是三级降级「工程位置号 → 采掘单元号 → 面名互含」；Kylin 的作业面台账
///     （<c>working_face_routing</c>）没有采掘单元号那一列（V041 采矿模型导出表未移植），故是两级。
///     对不上时的说明文案照实写成两级，不写一个不存在的对法。</item>
///   <item>B5 在 Kylin 侧一律判不了（理由见上）。<see cref="BlastPlanResult.NotInEngineWindow"/>
///     因此恒为 0，另有 <see cref="BlastPlanResult.EngineUnjudged"/> 计数。</item>
///   <item>原版的「按本期计划生成穿孔计划」（<c>ShiftPlanAssembler</c> + <c>DrillPlanSync</c>）依赖
///     班组计划分解器，<b>未移植</b>；Kylin 侧穿孔计划只有手工录入这一条写入路径。</item>
/// </list>
///
/// <see cref="Compose"/> 是纯函数，判据直接打它；<see cref="Build"/> 只负责取数。
/// </summary>
public static class BlastPlanLink
{
    /// <summary>
    /// 爆破后的清场/警戒解除时长 h。<b>台账只记爆破时刻，不记恢复作业时刻</b>，故这是工程缺省。
    /// 装箱与本窗口共用这一个常数 —— 两处各写一个，界面上显示的停产窗口就会和计划里扣掉的那一段对不上。
    /// </summary>
    public const double ClearanceH = 0.67;

    // ═════════════════════════════════════════════════════════════════════════
    //  纯函数
    // ═════════════════════════════════════════════════════════════════════════

    public static BlastPlanResult Compose(BlastPlanInputs inp)
    {
        var res = new BlastPlanResult { Date = inp.Date.Date };

        var shots = (inp.Shots ?? new List<BlastEventRow>()).Where(s => s != null).ToList();
        res.FromLedger = inp.LedgerUsable && shots.Count > 0;

        if (!res.FromLedger)
        {
            res.Shots.Add(FallbackRow(inp));
            res.Notes.Add(inp.LedgerUsable
                ? $"{inp.Date:MM-dd} 爆破台账（blast_event）无记录 —— 表里这一行是「引擎当前采用的兜底窗口」，"
                  + "不是真炮次；孔数/装药/方量因此都是空的。录入当日炮次后本表即为真值。"
                : "爆破台账读不通 —— 表里这一行是引擎当前采用的兜底窗口，不是真炮次。");
            if (!inp.EngineKnown) res.Notes.Add(EngineUnknownNote);
            res.Header = HeaderOf(res, inp);
            return res;
        }

        // 按时刻排；没记时刻的排在最后（它们进不了任何时窗，最需要被看见）
        var ordered = shots
            .Select(s => (Shot: s, Hour: ParseHour(s.BlastTime)))
            .OrderBy(x => x.Hour ?? 99)
            .ThenBy(x => x.Shot.BlastSeq ?? 0)
            .ToList();

        foreach (var (shot, hour) in ordered)
        {
            var row = new BlastShotRow
            {
                Seq = shot.BlastSeq,
                Hour = hour,
                TimeText = hour.HasValue ? Hm(hour.Value) : "未记时刻",
                Location = (shot.LocationCode ?? "").Trim(),
                DrillId = (shot.DrillId ?? "").Trim(),
                MaterialText = MaterialText(shot.Material),
                HoleCount = shot.HoleCount,
                HoleMeters = shot.TotalHoleLengthM,
                ExplosiveKg = shot.ExplosiveKg,
                VolumeM3 = shot.BlastVolumeM3,
                UnitKgM3 = shot.UnitConsumptionKgM3,
                EngineJudged = inp.EngineKnown,
            };

            if (hour is { } h)
            {
                double end = Math.Min(24, h + ClearanceH);
                row.StopWindow = $"{Hm(h)}–{Hm(end)}";
                row.Shift = ShiftOf(inp.Shifts, h);
                row.Status = StatusOf(h, end, inp.NowHour);
                if (inp.EngineKnown)
                {
                    // 进没进计划 = 装箱扣掉的时窗里有没有盖住这一炮的（合并后可能一段盖住相邻两炮）
                    row.DrivesEngine = inp.Windows().Any(w => h >= w.Start - 1e-6 && h < w.End + 1e-6);
                    if (!row.DrivesEngine)
                        row.Note = "未进装箱时窗（这一炮的清场没有从计划里扣）";
                }
                else
                {
                    row.Note = "判不了：当日盘子取不到，无从知道计划里扣没扣这一炮的清场";
                }
            }
            else
            {
                row.Status = "未记时刻";
                row.Note = "没有爆破时刻 ⇒ 排不进任何班的时窗，装箱当它不存在";
            }

            LinkFace(row, inp.Faces);
            LinkDrill(row, inp.Drills);
            res.Shots.Add(row);
        }

        res.HoleCountSum = res.Shots.Sum(s => s.HoleCount ?? 0);
        res.HoleMetersSum = res.Shots.Sum(s => s.HoleMeters ?? 0);
        res.ExplosiveKgSum = res.Shots.Sum(s => s.ExplosiveKg ?? 0);
        res.VolumeM3Sum = res.Shots.Sum(s => s.VolumeM3 ?? 0);

        // B5：没进计划时窗的那些炮必须点名
        int extra = res.NotInEngineWindow;
        if (extra > 0)
            res.Notes.Add($"⚠ 本日 {res.Shots.Count} 炮，其中 {extra} 炮「没进计划时窗」—— "
                        + "那几个时段计划里仍在满负荷作业（改完盘子后重开本窗即可复核）。");
        if (!inp.EngineKnown) res.Notes.Add(EngineUnknownNote);

        int noTime = res.Shots.Count(s => s.Hour == null);
        if (noTime > 0)
            res.Notes.Add($"{noTime} 炮没记爆破时刻，排不进时窗（在爆破台账补 blast_time）。");

        if (res.Unlinked > 0)
            res.Notes.Add($"{res.Unlinked} 炮没对上当日作业面：按 平盘编码 → 工程位置号 → 面名 依次对，"
                        + "对不上多半是平盘编码与作业面台账用了两套命名。");

        int noDrill = res.Shots.Count(s => !s.HasDrill);
        if (noDrill > 0)
            res.Notes.Add(inp.Drills.Count == 0
                ? "本日没有穿孔计划（drill_plan 为空）—— 在下方「穿孔作业计划」里排一条，钻机才会进甘特与工序进度。"
                : $"{noDrill} 炮所在区当日没有穿孔计划 —— 孔要么早打完了（补一条已完成的），要么这一炮排早了。");

        res.Header = HeaderOf(res, inp);
        return res;
    }

    /// <summary>盘子取不到时那句话（只此一份，两条路都用它）。</summary>
    internal const string EngineUnknownNote =
        "「进装箱时窗」一列本日判不了：当日排产盘子取不到（Kylin 侧排产装配层尚未移植）。"
      + "判不了不等于没进 —— 拿本表自己倒推出来的窗口去判自己，会永远判通过。";

    /// <summary>台账没有炮时的那一行：如实标成兜底，数字列一律留空。</summary>
    private static BlastShotRow FallbackRow(BlastPlanInputs inp)
    {
        var ws = inp.Windows();
        bool has = ws.Count > 0;
        if (has) { inp.EngineStart = ws[0].Start; inp.EngineEnd = ws[0].End; }
        return new BlastShotRow
        {
            Location = "（无炮次记录）",
            TimeText = has ? Hm(inp.EngineStart) : "—",
            Hour = has ? inp.EngineStart : null,
            StopWindow = has ? $"{Hm(inp.EngineStart)}–{Hm(inp.EngineEnd)}" : "本日无停产窗口",
            Shift = has ? ShiftOf(inp.Shifts, inp.EngineStart) : "",
            Status = has ? StatusOf(inp.EngineStart, inp.EngineEnd, inp.NowHour) : "—",
            DrivesEngine = has && inp.EngineKnown,
            EngineJudged = inp.EngineKnown,
            LinkedFace = "—",
            Note = has ? "引擎兜底窗口（非台账炮次）" : "引擎本日未扣任何停产时段",
        };
    }

    private static string HeaderOf(BlastPlanResult res, BlastPlanInputs inp)
    {
        var ws = inp.Windows();
        double stopH = ws.Sum(w => w.Hours);
        string engine = !inp.EngineKnown
            ? "装箱停产时段：判不了（当日盘子取不到）"
            : ws.Count > 0
                ? $"装箱停产 {ws.Count} 段 / 共 {stopH:0.#} h（"
                  + string.Join("、", ws.Select(w => $"{Hm(w.Start)}–{Hm(w.End)}"))
                  + $"；清场按缺省 {ClearanceH * 60:0} 分钟）"
                : "装箱本日未扣停产时段";

        if (!res.FromLedger)
            return $"{res.Date:yyyy-MM-dd}　本日无爆破台账记录　|　{engine}"
                 + (inp.EngineBasis.Length > 0 ? $"　|　{inp.EngineBasis}" : "");

        return $"{res.Date:yyyy-MM-dd}　本日 {res.Shots.Count} 炮　·　孔 {res.HoleCountSum} 个 / 延米 {res.HoleMetersSum:0} m"
             + $"　·　装药 {res.ExplosiveKgSum:N0} kg　·　爆破方量 {res.VolumeM3Sum:N0} m³"
             + (res.UnitKgM3 is { } u ? $"　·　综合单耗 {u:0.###} kg/m³" : "")
             + $"　|　{engine}";
    }

    /// <summary>
    /// B4：平盘编码 → 作业面。工程位置号 → 面名互含，逐级降级。
    /// （原版中间还有一级"采掘单元号"，Kylin 的作业面台账没有那一列 —— 登记在类文档。）
    /// </summary>
    private static void LinkFace(BlastShotRow row, List<FaceInput>? faces)
    {
        string loc = row.Location;
        if (loc.Length == 0)
        {
            row.LinkedFace = "未对上（本炮没记平盘编码）";
            return;
        }

        var loads = (faces ?? new List<FaceInput>()).Where(f => f != null && f.Process == ProcessType.Load).ToList();
        if (loads.Count == 0)
        {
            row.LinkedFace = "未对上（当日盘子里没有采装面）";
            return;
        }

        var hit = loads.FirstOrDefault(f => Eq(f.EngineeringPositionId, loc))
               ?? loads.FirstOrDefault(f => Contains(f.Zone, loc) || Contains(loc, f.Zone));

        if (hit == null)
        {
            row.LinkedFace = $"未对上（平盘 {loc}）";
            return;
        }

        row.Linked = true;
        row.LinkedFace = Math.Abs(hit.BenchElevationM) > 1e-6
            ? $"{hit.Zone}（{(hit.BenchElevationM >= 0 ? "+" : "")}{hit.BenchElevationM:0}）"
            : hit.Zone;
    }

    /// <summary>
    /// 本炮所在待爆区当天的穿孔计划（V044）。<b>这才是「衔接」的第一段</b>：
    /// 有炮无孔 = 采准脱节（要么孔早打完了没记，要么这一炮排早了）。同一个区可能几台钻机分段打，全列出来。
    /// </summary>
    private static void LinkDrill(BlastShotRow row, List<DrillInput>? drills)
    {
        var all = (drills ?? new List<DrillInput>()).Where(d => d != null).ToList();
        if (all.Count == 0) { row.DrillText = "无穿孔计划"; return; }

        string loc = row.Location;
        var hits = loc.Length == 0
            ? new List<DrillInput>()
            : all.Where(d => Eq(d.Zone, loc) || Contains(d.Zone, loc) || Contains(loc, d.Zone)).ToList();

        if (hits.Count == 0) { row.DrillText = "本区无穿孔计划"; return; }

        row.HasDrill = true;
        row.DrillText = string.Join("、", hits.Select(d =>
            $"{(d.EquipId.Length > 0 ? d.EquipId : "钻机")} {Hm(d.Start)}–{Hm(d.End)}"));
    }

    /// <summary>B3：此刻 vs 本炮。</summary>
    private static string StatusOf(double start, double end, double now)
        => now < start ? $"待爆 {start - now:0.#}h"
         : now < end ? "清场中"
         : "已爆";

    private static string ShiftOf(List<ShiftWindow>? shifts, double hour)
    {
        var list = shifts ?? new List<ShiftWindow>();
        foreach (var s in list)
            if (hour >= s.Start && hour < s.End) return $"{s.Name} {s.Start:00.##}–{s.End:00.##}";
        return "";
    }

    /// <summary>台账物料列是 c4/rh 这类编码，统一经 <see cref="MaterialCatalog"/> 翻成中文名。</summary>
    private static string MaterialText(string? raw)
    {
        string t = (raw ?? "").Trim();
        if (t.Length == 0) return "";
        string code = MaterialCatalog.CodeFromText(t);
        if (code.Length == 0) return t;
        var spec = MaterialCatalog.Resolve(code);
        return string.Equals(spec.Name, t, StringComparison.OrdinalIgnoreCase) ? spec.Name : $"{spec.Name}（{t}）";
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  取数
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 本日爆破停产时窗 = 逐炮「爆破时刻 → +清场」合并。
    /// <b>这是 <see cref="WorkWindowCalc"/> 那一维此前缺的数据源</b>（它的类文档里登记过"暂无数据源"）。
    /// 没记时刻的炮不产生时窗（不按 0 点算），由调用方计数报出来。
    /// </summary>
    public static List<BlastWindow> BlastWindowsOf(IEnumerable<BlastEventRow>? shots, out int noTime)
    {
        noTime = 0;
        var list = new List<BlastWindow>();
        foreach (var s in shots ?? Enumerable.Empty<BlastEventRow>())
        {
            if (s == null) continue;
            double? h = ParseHour(s.BlastTime);
            if (h is null) { noTime++; continue; }
            double end = Math.Min(24, h.Value + ClearanceH);
            if (end > h.Value + 1e-9)
                list.Add(new BlastWindow(h.Value, end, s.BlastSeq is { } q ? $"第 {q} 炮" : ""));
        }
        return BlastWindow.Merge(list);
    }

    /// <summary>爆破台账按日读。</summary>
    public static List<BlastEventRow> ShotsByDate(DbConnection? conn, DateTime date, out string error)
    {
        error = "";
        var list = new List<BlastEventRow>();
        if (conn == null) { error = "没有数据库连接"; return list; }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT blast_date, blast_time, blast_seq, drill_id, location_code, material, "
              + "hole_count, total_hole_length_m, explosive_kg, blast_volume_m3, unit_consumption_kg_m3 "
              + "FROM blast_event WHERE blast_date = " + Lit(DrillPlanStore.D(date))
              + " ORDER BY blast_time, blast_seq";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new BlastEventRow
                {
                    BlastDate = Str(rd, 0),
                    BlastTime = rd.IsDBNull(1) ? null : Str(rd, 1),
                    BlastSeq = Dbl(rd, 2) is { } q ? (int)Math.Round(q) : null,
                    DrillId = rd.IsDBNull(3) ? null : Str(rd, 3),
                    LocationCode = rd.IsDBNull(4) ? null : Str(rd, 4),
                    Material = rd.IsDBNull(5) ? null : Str(rd, 5),
                    HoleCount = Dbl(rd, 6) is { } hc ? (int)Math.Round(hc) : null,
                    TotalHoleLengthM = Dbl(rd, 7),
                    ExplosiveKg = Dbl(rd, 8),
                    BlastVolumeM3 = Dbl(rd, 9),
                    UnitConsumptionKgM3 = Dbl(rd, 10),
                });
        }
        catch (Exception ex) { error = Short(ex); }
        return list;
    }

    /// <summary>
    /// 当日作业面 ← <c>working_face_routing</c>（V035，那张表存的正是"当日怎么干"）。
    /// <b>只取采装面</b>不是这里筛的：B4 自己按 <see cref="ProcessType.Load"/> 筛，排土面照读进来
    /// （将来别的判据要用）。<c>process</c> 认不出的按采装算 —— 那一列的默认值就是 Load。
    /// </summary>
    public static List<FaceInput> FacesOfDay(DbConnection? conn, out string error)
    {
        error = "";
        var list = new List<FaceInput>();
        if (conn == null) { error = "没有数据库连接"; return list; }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT face_code, process, engineering_position_id, bench_elevation_m, material_code, day_target_m3, "
              + "main_equipment, destination_id, destination_name, equiv_haul_km, haul_distance_km "
              + "FROM working_face_routing ORDER BY face_code";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new FaceInput
                {
                    Zone = Str(rd, 0),
                    Process = Enum.TryParse<ProcessType>(Str(rd, 1), true, out var p) ? p : ProcessType.Load,
                    EngineeringPositionId = Str(rd, 2),
                    BenchElevationM = Dbl(rd, 3) ?? 0,
                    Material = Str(rd, 4),
                    DayTargetM3 = Dbl(rd, 5) ?? 0,
                    // 主设备带下来：当日盘子装配要靠它对编组规则算班产（§三五四）
                    Group = new EquipmentGroup { MainEquipment = Str(rd, 6) },
                    // 去向带下来：下达闸门判的第一件事就是"这车拉到哪"（§三五五）
                    DestinationId = Str(rd, 7),
                    DestinationName = Str(rd, 8),
                    // 等效运距优先（含坡度折算）；没录就退回直线运距，两个都没有才是 0
                    EquivHaulKm = Dbl(rd, 9) is > 1e-9 ? Dbl(rd, 9)!.Value : Dbl(rd, 10) ?? 0,
                });
        }
        catch (Exception ex) { error = Short(ex); }
        return list;
    }

    /// <summary>装一天的输入并算出排程。任何一处取不到都只降级，不抛。</summary>
    public static BlastPlanResult Build(DbConnection? conn, DateTime date, double nowHour)
    {
        var inp = new BlastPlanInputs { Date = date.Date, NowHour = nowHour };

        inp.Shots = ShotsByDate(conn, date, out string shotErr);
        if (shotErr.Length > 0) inp.LedgerUsable = false;

        inp.Faces = FacesOfDay(conn, out _);
        inp.Shifts = MaintenanceWindows.ShiftWindowsOf(WorkCalendar.Day(conn!, date, out _));
        inp.Drills = DrillPlanStore.ToDrillInputs(DrillPlanStore.ByDate(conn, date, out _), date).Drills;

        // B5：Kylin 侧没有排产装配层 —— 判不了，不假装判得了（理由见 BlastPlanInputs.EngineKnown）
        inp.EngineKnown = false;
        var derived = BlastWindowsOf(inp.Shots, out int noTime);
        inp.EngineWindows = derived;                 // 只用来显示"按台账推出来的停产时段"
        inp.EngineBasis = derived.Count > 0
            ? $"按台账推出 {derived.Count} 段停产时段（清场 {ClearanceH * 60:0} 分钟）"
              + (noTime > 0 ? $"；{noTime} 炮没记时刻推不出" : "")
            : "";

        var res = Compose(inp);
        if (shotErr.Length > 0) res.Notes.Insert(0, "爆破台账读不通：" + shotErr);
        return res;
    }

    // ── 小工具 ───────────────────────────────────────────────────────────────

    private static bool Eq(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? outer, string? inner)
        => !string.IsNullOrWhiteSpace(outer) && !string.IsNullOrWhiteSpace(inner)
        && outer.Trim().Contains(inner.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>"07:30" / "7:30:00" / "7.5" → 小时数；解析不了返回 null。口径与检修档期同一份。</summary>
    internal static double? ParseHour(string? s) => MaintenanceWindows.Hour(s);

    internal static string Hm(double hh)
    {
        int h = (int)hh;
        int m = (int)Math.Round((hh - h) * 60);
        if (m == 60) { h++; m = 0; }
        return $"{h:00}:{m:00}";
    }

    private static string Str(DbDataReader rd, int i) => rd.IsDBNull(i) ? "" : rd.GetValue(i)?.ToString() ?? "";
    private static double? Dbl(DbDataReader rd, int i)
        => rd.IsDBNull(i) ? null
         : double.TryParse(rd.GetValue(i)?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
    private static string Lit(string? s) => string.IsNullOrEmpty(s) ? "NULL" : "'" + s.Replace("'", "''") + "'";
    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
