// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/BlastPlanLink.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data;              // EquipmentDataContext（静态门面）
using PitMine3D.Kylin.Data.Entities;     // BlastEvent
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  钻爆计划衔接的口径（B1–B7）。
//
//  原「钻爆计划衔接」窗口读的是 cfg.Drills（穿孔计划）。而穿孔计划**没有台账**：
//  台账模式下 ProductionPlanContext 明写"穿孔计划暂无台账，本日不排"，于是那张表在真库上
//  一行都没有；样例盘子里有一行，那一行的「清场警戒」「衔接采装面」「状态」三列还是写死的
//  常量文本，「爆破窗口」列每一行都显示同一个全局窗口。
//
//  真正有台账的是 blast_event（炮次/钻机/平盘编码/物料/孔径/孔数/总延米/装药量/爆破方量/单耗），
//  它此前只被读走一个数：最早一炮的时刻，用来定引擎的停产窗口。本类把这张表接进来：
//
//    B1 一行 = 一炮（blast_event），不是一个"待爆区"。
//    B2 停产窗口 = 爆破时刻 + 清场时长（<see cref="ClearanceH"/>，与引擎同一个常数，
//       台账不记恢复作业时刻）。逐炮各算各的，不再全表同一个值。
//    B3 状态由「此刻 vs 本炮时刻」推：待爆(还差 x h) / 清场中 / 已爆 / 未记时刻。
//    B4 衔接采装面 = 按平盘编码对当日盘子的作业面（工程位置号 → 采掘单元号 → 面名互含），
//       对不上就写"未对上"并说明按什么对的 —— 不再写死"主采面·东"。
//    B5 ★ 引擎当前只吃**最早一炮**（ExploderConfig.BlastStart/End 是一对标量）。
//       第二炮起在装箱里根本不存在：那些班照样排满，到点却要清场。这条必须在界面上说出来，
//       不能让人以为"排了就进了计划"（见 [[always-firing-warning-is-a-dead-path]] 的反面：
//       这里是"界面上看得见、引擎里没有"）。
//    B6 火工品合计（孔数/延米/装药量/方量/单耗）逐日汇总——这正是任务编制要向火工品计划交的数。
//    B7 台账读不通 / 本日无炮时，退回引擎当前采用的那个窗口并**标明是兜底**，不合成炮次。
//
//  Compose 是纯函数，判据直接打它；Build 只负责取数。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一炮。</summary>
public sealed class BlastShotRow
{
    public int? Seq;                    // 当年炮次
    public double? Hour;                // 爆破时刻（0..24）；null = 台账没记
    public string TimeText = "—";
    public string Location = "";        // 平盘编码
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

    /// <summary>引擎当前采用的停产窗口就是这一炮（B5）。</summary>
    public bool DrivesEngine;

    /// <summary>逐行提示（未进装箱时窗 / 没记时刻 …）。</summary>
    public string Note = "";
}

/// <summary>Compose 的输入（判据直接构造，不碰库）。</summary>
public sealed class BlastPlanInputs
{
    public DateTime Date;

    /// <summary>爆破台账接不接得通。false ⇒ 只出兜底行 + 一条说明。</summary>
    public bool LedgerUsable = true;

    public List<BlastEvent> Shots = new();

    /// <summary>当日盘子的作业面（衔接采装面按它对）。</summary>
    public List<FaceInput> Faces = new();

    /// <summary>当日班次窗口（判这一炮落在哪个班）。</summary>
    public List<ShiftWindow> Shifts = new();

    /// <summary>
    /// 当日穿孔计划（V044 <c>drill_plan</c> → <see cref="ExploderConfig.Drills"/>）。
    /// 「衔接」二字要看的第一件事就是这个：这个区的孔谁在打、打完没有。
    /// </summary>
    public List<DrillInput> Drills = new();

    /// <summary>此刻几点（状态判定；判据注入固定值）。</summary>
    public double NowHour;

    /// <summary>
    /// 装箱真正扣掉的<b>全部</b>停产时窗（<c>ExploderConfig.BlastWindows()</c>）。
    /// 一炮"进没进计划"就是判它在不在这张表里——2026-08-11 之前引擎只装得下一段，
    /// 那时这里只有一条，第二炮起全部报"未进"。
    /// </summary>
    public List<BlastWindow> EngineWindows = new();

    /// <summary>兼容视图：最早一段。<see cref="EngineWindows"/> 为空时用它兜底。</summary>
    public double EngineStart;
    public double EngineEnd;

    /// <summary>装箱扣掉的时窗（列表优先，回落那一对标量）。</summary>
    public IReadOnlyList<BlastWindow> Windows()
        => EngineWindows.Count > 0
            ? EngineWindows
            : EngineEnd > EngineStart + 1e-9
                ? new List<BlastWindow> { new(EngineStart, EngineEnd) }
                : new List<BlastWindow>();

    /// <summary>引擎那个窗口的来源文案（ProductionPlanContext.BlastSourceLabel）。</summary>
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

    /// <summary>综合单耗 kg/m³ = Σ装药 ÷ Σ方量（不是各行单耗的平均——那是另一个数）。</summary>
    public double? UnitKgM3 => VolumeM3Sum > 1e-6 ? ExplosiveKgSum / VolumeM3Sum : null;

    /// <summary>几炮没进装箱时窗（B5）。</summary>
    public int NotInEngineWindow => Shots.Count(s => s.Hour.HasValue && !s.DrivesEngine);

    /// <summary>几炮没对上作业面（B4）。</summary>
    public int Unlinked => Shots.Count(s => !s.Linked);
}

/// <summary>钻爆计划衔接。</summary>
public static class BlastPlanLink
{
    /// <summary>
    /// 爆破后的清场/警戒解除时长 h。<b>台账只记爆破时刻，不记恢复作业时刻</b>，故这是工程缺省。
    /// 装箱（<see cref="ProductionPlanContext"/>）与本窗口共用这一个常数——两处各写一个，
    /// 界面上显示的停产窗口就会和计划里扣掉的那一段对不上。
    /// </summary>
    public const double ClearanceH = 0.67;

    // ═════════════════════════════════════════════════════════════════════════
    //  纯函数
    // ═════════════════════════════════════════════════════════════════════════

    public static BlastPlanResult Compose(BlastPlanInputs inp)
    {
        var res = new BlastPlanResult { Date = inp.Date.Date };

        var shots = (inp.Shots ?? new List<BlastEvent>()).Where(s => s != null).ToList();
        res.FromLedger = inp.LedgerUsable && shots.Count > 0;

        if (!res.FromLedger)
        {
            res.Shots.Add(FallbackRow(inp));
            res.Notes.Add(inp.LedgerUsable
                ? $"{inp.Date:MM-dd} 爆破台账（blast_event）无记录 —— 表里这一行是**引擎当前采用的兜底窗口**，"
                  + "不是真炮次；孔数/装药/方量因此都是空的。录入当日炮次后本表即为真值。"
                : "爆破台账读不通 —— 表里这一行是引擎当前采用的兜底窗口，不是真炮次。");
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
            };

            if (hour is { } h)
            {
                double end = Math.Min(24, h + ClearanceH);
                row.StopWindow = $"{Hm(h)}–{Hm(end)}";
                row.Shift = ShiftOf(inp.Shifts, h);
                row.Status = StatusOf(h, end, inp.NowHour);
                // 进没进计划 = 装箱扣掉的时窗里有没有盖住这一炮的（合并后可能一段盖住相邻两炮）
                row.DrivesEngine = inp.Windows().Any(w => h >= w.Start - 1e-6 && h < w.End + 1e-6);
                if (!row.DrivesEngine)
                    row.Note = "未进装箱时窗（这一炮的清场没有从计划里扣）";
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

        // B5：没进计划时窗的那些炮必须点名（2026-08-11 前引擎只装得下一段，那时这里必然报一堆）
        int extra = res.NotInEngineWindow;
        if (extra > 0)
            res.Notes.Add($"⚠ 本日 {res.Shots.Count} 炮，其中 {extra} 炮**没进计划时窗** —— "
                        + "那几个时段计划里仍在满负荷作业（改完盘子后重开本窗即可复核）。");

        int noTime = res.Shots.Count(s => s.Hour == null);
        if (noTime > 0)
            res.Notes.Add($"{noTime} 炮没记爆破时刻，排不进时窗（在爆破台账补 blast_time）。");

        if (res.Unlinked > 0)
            res.Notes.Add($"{res.Unlinked} 炮没对上当日作业面：按 平盘编码 → 工程位置号/采掘单元号/面名 依次对，"
                        + "对不上多半是平盘编码与作业面台账用了两套命名。");

        int noDrill = res.Shots.Count(s => !s.HasDrill);
        if (noDrill > 0)
            res.Notes.Add(inp.Drills.Count == 0
                ? $"本日没有穿孔计划（drill_plan 为空）—— 下方「穿孔作业计划」里排一条，钻机才会进甘特与工序进度。"
                : $"{noDrill} 炮所在区当日没有穿孔计划 —— 孔要么早打完了（补一条已完成的），要么这一炮排早了。");

        res.Header = HeaderOf(res, inp);
        return res;
    }

    /// <summary>台账没有炮时的那一行：如实标成兜底，数字列一律留空。</summary>
    private static BlastShotRow FallbackRow(BlastPlanInputs inp)
    {
        var ws = inp.Windows();
        bool has = ws.Count > 0;
        if (has) { inp.EngineStart = ws[0].Start; inp.EngineEnd = ws[0].End; }
        var row = new BlastShotRow
        {
            Location = "（无炮次记录）",
            TimeText = has ? Hm(inp.EngineStart) : "—",
            Hour = has ? inp.EngineStart : null,
            StopWindow = has ? $"{Hm(inp.EngineStart)}–{Hm(inp.EngineEnd)}" : "本日无停产窗口",
            Shift = has ? ShiftOf(inp.Shifts, inp.EngineStart) : "",
            Status = has ? StatusOf(inp.EngineStart, inp.EngineEnd, inp.NowHour) : "—",
            DrivesEngine = has,
            LinkedFace = "—",
            Note = has ? "引擎兜底窗口（非台账炮次）" : "引擎本日未扣任何停产时段",
        };
        return row;
    }

    private static string HeaderOf(BlastPlanResult res, BlastPlanInputs inp)
    {
        var ws = inp.Windows();
        double stopH = ws.Sum(w => w.Hours);
        string engine = ws.Count > 0
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

    /// <summary>B4：平盘编码 → 作业面。工程位置号 → 采掘单元号 → 面名互含，逐级降级。</summary>
    private static void LinkFace(BlastShotRow row, List<FaceInput> faces)
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
               ?? loads.FirstOrDefault(f => Eq(f.UnitId, loc))
               ?? loads.FirstOrDefault(f => Contains(f.Zone, loc) || Contains(loc, f.Zone));

        if (hit == null)
        {
            row.LinkedFace = $"未对上（平盘 {loc}）";
            return;
        }

        row.Linked = true;
        row.LinkedFace = hit.BenchElevationM > 1e-6
            ? $"{hit.Zone}（+{hit.BenchElevationM:0}）"
            : hit.Zone;
    }

    /// <summary>
    /// 本炮所在待爆区当天的穿孔计划（V044）。<b>这才是「衔接」的第一段</b>：
    /// 有炮无孔 = 采准脱节（要么孔早打完了没记，要么这一炮排早了）。
    /// 同一个区可能几台钻机分段打，全列出来。
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
        => now < start ? $"待爆 {start - now:0.#}h"   // 短文案：这一列只有 82 DIP，长了会被切
         : now < end ? "清场中"
         : "已爆";

    private static string ShiftOf(List<ShiftWindow> shifts, double hour)
    {
        var s = (shifts ?? new List<ShiftWindow>()).FirstOrDefault(x => hour >= x.Start && hour < x.End);
        return s == null ? "" : $"{s.Name} {s.Start:00.##}–{s.End:00.##}";
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

    /// <summary>装一天的输入并算出排程。任何一处取不到都只降级，不抛。</summary>
    public static BlastPlanResult Build(DateTime date)
    {
        var inp = new BlastPlanInputs { Date = date.Date };

        try
        {
            inp.Shots = EquipmentDataContext.Blast.ByDate(date).Where(b => b != null).ToList();
        }
        catch { inp.LedgerUsable = false; }

        try
        {
            var cfg = ProductionPlanContext.Config();
            inp.Faces = cfg.Faces;
            inp.Shifts = cfg.Shifts;
            inp.EngineWindows = new List<BlastWindow>(cfg.BlastWindows());
            inp.EngineStart = cfg.BlastStart;
            inp.EngineEnd = cfg.BlastEnd;
            inp.NowHour = cfg.NowHour;
            inp.EngineBasis = ProductionPlanContext.BlastSourceLabel;
            inp.Drills = cfg.Drills;
        }
        catch { /* 盘子装不出来 → 只出台账那半边 */ }

        return Compose(inp);
    }

    // ── 小工具 ───────────────────────────────────────────────────────────────

    private static bool Eq(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? outer, string? inner)
        => !string.IsNullOrWhiteSpace(outer) && !string.IsNullOrWhiteSpace(inner)
        && outer.Trim().Contains(inner.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>"07:30" / "7:30:00" / "7.5" → 小时数；解析不了返回 null（与装配层同一套写法）。</summary>
    private static double? ParseHour(string? s)
    {
        string v = (s ?? "").Trim();
        if (v.Length == 0) return null;
        if (TimeSpan.TryParse(v, CultureInfo.InvariantCulture, out var ts) && ts.TotalHours is >= 0 and <= 24)
            return Math.Round(ts.TotalHours, 3);
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double h) && h is >= 0 and <= 24)
            return h;
        return null;
    }

    private static string Hm(double hh)
    {
        int h = (int)hh;
        int m = (int)Math.Round((hh - h) * 60);
        if (m == 60) { h++; m = 0; }
        return $"{h:00}:{m:00}";
    }
}
