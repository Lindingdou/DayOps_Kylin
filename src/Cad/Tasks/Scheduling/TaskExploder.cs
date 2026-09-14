using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;   // ProcessType, CoalQuality（复用；调度任务用 ShiftTask 避与量核算 ProductionTask 重名）

namespace PitMine3D.Kylin.Cad.Tasks.Scheduling;

// ─────────────────────────── 调度域模型（忠实移植原 TaskLib.Domain / Engine.ExploderConfig）───────────────────────────

/// <summary>任务状态。</summary>
public enum TaskStatus { Planned, Dispatched, Running, Done, Partial, Failed }

/// <summary>欠产/空闲原因码。</summary>
public enum SchedReason { Fault, Maintenance, Weather, BlastWait, ProcessWait, TruckShortage, OreShortage, RoadCongestion, Absence, OverPlanned, OverAchieved }

/// <summary>校核违规等级。</summary>
public enum ViolationSeverity { Info, Warn, Error }

/// <summary>设备编组：主设备 + 配属卡车/辅助 + 编组班产。</summary>
public sealed class EquipmentGroup
{
    public string MainEquipment { get; set; } = "";
    public List<string> Trucks { get; set; } = new();
    public List<string> Aux { get; set; } = new();
    public int RecommendedTrucks { get; set; }
    public double GroupCapacityM3PerH { get; set; }   // 编组班产 = min(铲装能力, 车队运力)

    // ── 周期分解（分环节降效要用；由 FleetCycle 求解时带下来）───────────────────
    //  班产是 min(铲装能力 A, 车队运力 B) 压出来的一个标量，单看它无从分环节。
    //  没有这三个数就**不许凭空劈**（见 ExploderConfig.WeatherFactorFor）。

    /// <summary>装车节拍 τ_L（min/车）。0 = 编组没经周期求解。</summary>
    public double LoadTaktMin { get; set; }
    /// <summary>整循环时间 T_c（min）。0 = 没经周期求解。</summary>
    public double CycleTimeMin { get; set; }
    /// <summary>匹配系数 MF = 车队运力 ÷ 铲装能力。0 = 没经周期求解。</summary>
    public double MatchFactor { get; set; }

    /// <summary>
    /// 单车载重 t（编组规则台账里的车型载重）。0 = 台账没解出来。
    /// <b>吨是三口径间唯一的守恒量</b> —— 派车单以它为主，实方/松方/占容都由它折。
    /// </summary>
    public double TruckPayloadT { get; set; }

    /// <summary>拿得到周期分解（τ_L &lt; T_c 且 MF &gt; 0）。</summary>
    public bool HasCycleBreakdown =>
        LoadTaktMin > 1e-6 && CycleTimeMin > LoadTaktMin + 1e-6 && MatchFactor > 1e-6;
}

public sealed class ShiftWindow
{
    public string Name { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public ShiftWindow() { }
    public ShiftWindow(string name, double start, double end) { Name = name; Start = start; End = end; }
}

public sealed class FaceInput
{
    public string Zone { get; set; } = "";
    public double BenchElevationM { get; set; }
    public string EngineeringPositionId { get; set; } = "";
    public string Material { get; set; } = "";
    public double DayTargetM3 { get; set; }

    /// <summary>
    /// 备采储量 m³（原位实方）。<b>0 = 未录，不是"采空"</b>。
    /// 装箱里「备采用尽 → 该班空闲」判的是<b>当日目标</b>用尽；而"这个面还能采几天"要的是这个量。
    /// </summary>
    public double AvailableReserveM3 { get; set; }

    public CoalQuality? Quality { get; set; }
    public EquipmentGroup Group { get; set; } = new();
    public ProcessType Process { get; set; } = ProcessType.Load;

    // ── 去向（"这车拉到哪"）──────────────────────────────────────────────────
    //  下达闸门判的第一件事就是它：任务书写不出卸点，运距/配车/运输功都核算不了，
    //  单据发下去现场只能自己找地方倒，采排账当天就散。

    /// <summary>主去向 Id（<c>SinkNode.Id</c>）。空 = 没指派。</summary>
    public string DestinationId { get; set; } = "";
    /// <summary>主去向名（显示用）。</summary>
    public string DestinationName { get; set; } = "";
    /// <summary>等效运距 km（含坡度折算）。0 = 没录，车次时刻只能按兜底值估。</summary>
    public double EquivHaulKm { get; set; }

    /// <summary>指派过去向。</summary>
    public bool HasDestination => DestinationId.Length > 0 || DestinationName.Length > 0;
}

public sealed class DrillInput
{
    public string EquipId { get; set; } = "";
    public string Zone { get; set; } = "";
    public double BenchElevationM { get; set; }
    public double Start { get; set; }
    public double End { get; set; }

    /// <summary>计划孔数。null = 台账没录（<b>0 是合法值</b>，不拿 0 冒充"未录"）。</summary>
    public int? HoleCount { get; set; }

    /// <summary>
    /// 计划<b>总</b>延米 m（= 孔数 × 单孔延米，换算在 <see cref="PitMine3D.Kylin.Data.DrillPlanStore"/>）。
    /// null = 未录：不带量的话穿孔任务只有时窗没有量，工序进度只能按"完成/未完成"判。
    /// </summary>
    public double? HoleLengthM { get; set; }
}

public sealed class MaintenanceWindow
{
    public string EquipId { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public string Label { get; set; } = "检修";
}

public sealed class BlendStandard
{
    public double MaxAshPct { get; set; } = 12.8;
    public double MinCalorificMJkg { get; set; } = 21.5;
    /// <summary>综合硫上限 %。</summary>
    public double MaxSulfurPct { get; set; } = 0.7;

    /// <summary>
    /// 面日产能的有效工时 h/日。<b>已迁到 <see cref="ExploderConfig.EffHoursPerDay"/></b>，
    /// 这里只作兼容视图（老盘子/老判据仍在这儿设值时读得到）。
    /// <para>
    /// 原版把它从配煤标准上摘下来的理由（照搬）：<c>Blend</c> 为 null 的语义是「不管配煤，纯量矿」，
    /// 把有效工时挂在这儿，"只想调有效工时"就必须先造一份配煤标准 —— 一造就把配煤约束整个打开了。
    /// <b>两件事的开关不许绑在一起。</b>
    /// </para>
    /// </summary>
    public double EffHoursPerDay { get; set; } = 20;
}

public sealed class ExploderConfig
{
    public string DateLabel { get; set; } = "";
    public string IdPrefix { get; set; } = "D";
    public double NowHour { get; set; }
    public double FromHour { get; set; }
    /// <summary>
    /// 最早一炮的停产窗口（<b>兼容视图</b>）。真源是 <see cref="Blasts"/> —— 一律用
    /// <see cref="BlastWindows"/> 取全部时窗，别只读这一对：一天两三炮时，只看这一对
    /// 就等于把后面的炮当作不存在。
    /// </summary>
    public double BlastStart { get; set; }
    public double BlastEnd { get; set; }

    /// <summary>
    /// 本日全部爆破停产时窗。空 = 走 <see cref="BlastStart"/>/<see cref="BlastEnd"/> 的兼容视图。
    /// 写入请走 <see cref="SetBlasts"/>，它顺手把兼容视图同步成最早那一炮（两处不许各说各的）。
    /// </summary>
    public List<PitMine3D.Kylin.Data.BlastWindow> Blasts { get; set; } = new();

    /// <summary>
    /// 本日全部停产时窗（合并重叠、按起点排）。<b>装箱、重排、甘特爆破带、钻爆衔接一律走这里。</b>
    /// </summary>
    public IReadOnlyList<PitMine3D.Kylin.Data.BlastWindow> BlastWindows()
        => Blasts.Count > 0
            ? PitMine3D.Kylin.Data.BlastWindow.Merge(Blasts)
            : BlastEnd > BlastStart + 1e-9
                ? new List<PitMine3D.Kylin.Data.BlastWindow> { new(BlastStart, BlastEnd) }
                : new List<PitMine3D.Kylin.Data.BlastWindow>();

    /// <summary>写入本日停产时窗，并把兼容视图同步成<b>最早</b>那一段（不出现"列表说三炮、标量说一炮"的分叉）。</summary>
    public void SetBlasts(IEnumerable<PitMine3D.Kylin.Data.BlastWindow>? windows)
    {
        Blasts = PitMine3D.Kylin.Data.BlastWindow.Merge(windows);
        if (Blasts.Count > 0) { BlastStart = Blasts[0].Start; BlastEnd = Blasts[0].End; }
        else { BlastStart = 0; BlastEnd = 0; }
    }
    public double HandoverRampH { get; set; } = 0.5;

    // ── 降效（人工锚点，在「编制配置」里改，见 CompileOverrides）────────────────
    //
    //  降效落在**能力**上而不是时窗上：雨雪影响的是路面车速与能见度（每小时干得少），
    //  而检修/爆破清场/交接班影响的是能开工的**时段**。两者混在一处，「今天雨大」就会被
    //  记成「今天少上了两小时班」，甘特上的条形位置全错。
    //  降效后当日干不完，装箱照常报「当日欠产」—— 这正是"全盘降效回摊"。

    /// <summary>天气/路况降效 %（0 = 不降效）。</summary>
    public double WeatherDeratePct { get; set; }

    /// <summary>降效后的能力系数（1 = 不降效）。截到 95%：全停产是不排班，不是降效 100%。</summary>
    public double WeatherFactor => 1 - Math.Clamp(WeatherDeratePct, 0, 95) / 100.0;

    /// <summary>采装环节降效 %（电铲装车）。null = 跟随 <see cref="WeatherDeratePct"/>。</summary>
    public double? LoadDeratePct { get; set; }
    /// <summary>运输环节降效 %（重车/空车行驶、卸点排队）。null = 跟随全盘值。</summary>
    public double? HaulDeratePct { get; set; }
    /// <summary>排土环节降效 %（推土机平整）。null = 跟随全盘值。</summary>
    public double? DumpDeratePct { get; set; }

    /// <summary>三个环节给了各不相同的值（给了才谈得上"分环节"）。</summary>
    public bool HasLinkDerate =>
        Math.Abs(DerateOf(LoadDeratePct) - DerateOf(HaulDeratePct)) > 1e-9
     || Math.Abs(DerateOf(LoadDeratePct) - DerateOf(DumpDeratePct)) > 1e-9;

    private double DerateOf(double? link) => Math.Clamp(link ?? WeatherDeratePct, 0, 95);

    /// <summary>
    /// <b>某个作业面</b>的能力系数（1 = 不降效）。按环节降效的唯一实现，装箱与配煤重分配共用。
    /// 排土面直吃排土降效；采装面按编组周期分解还原铲装/车队两侧再分别降
    /// （算式在 <see cref="Tasks.LinkDerate"/>，那是本仓已有的纯函数，此前一直没有调用方）。
    /// <para><b>没有周期分解时不许凭空劈</b>：退回全盘值。编个份额出来分，比不分更坏 ——
    /// 那是给一个不存在的精度。</para>
    /// </summary>
    public double WeatherFactorFor(FaceInput? face)
    {
        if (face == null) return WeatherFactor;
        var g = face.Group;
        return LinkDerate.Factor(face.Process,
            g.HasCycleBreakdown ? g.LoadTaktMin : 0,
            g.HasCycleBreakdown ? g.CycleTimeMin : 0,
            g.HasCycleBreakdown ? g.MatchFactor : 0,
            DerateOf(LoadDeratePct), DerateOf(HaulDeratePct), DerateOf(DumpDeratePct));
    }

    /// <summary>
    /// 备采保有下限（天）。<b>0 = 不校核</b>。人工锚点。
    /// 某面按当日强度采下去、剩余备采不足这个天数就该预警 —— 等真采空那天再报，
    /// 采准（穿孔/爆破）根本来不及跟上，那个面就得停。只对<b>录了备采储量</b>的面生效。
    /// </summary>
    public double MinPreparedDays { get; set; }

    /// <summary>
    /// 面日产能的有效工时 h/日。<b>面日产能上限 = 编组班产 × 本值 × 降效系数</b>。
    /// 刻意不挂在 <see cref="BlendStandard"/> 上（理由见 <see cref="BlendStandard.EffHoursPerDay"/>）；
    /// <see cref="EffHours"/> 是读取口，会回落到那个兼容视图。
    /// </summary>
    public double? EffHoursPerDay { get; set; }

    /// <summary>有效工时的**唯一读取口**：本级 → 配煤标准上的兼容视图 → 工程缺省 20h。</summary>
    public double EffHours => EffHoursPerDay ?? Blend?.EffHoursPerDay ?? 20;

    public List<ShiftWindow> Shifts { get; set; } = new();
    public List<FaceInput> Faces { get; set; } = new();
    public List<DrillInput> Drills { get; set; } = new();
    public List<MaintenanceWindow> Maintenance { get; set; } = new();
    public BlendStandard? Blend { get; set; }
}

/// <summary>调度任务（原 ProductionTask；此处 ShiftTask 避与 <see cref="Tasks.ProductionTask"/> 量核算模型重名）。</summary>
public sealed class ShiftTask
{
    public string Id { get; set; } = "";
    public ProcessType Process { get; set; }
    public EquipmentGroup Group { get; set; } = new();
    public string WorkZone { get; set; } = "";
    public double BenchElevationM { get; set; }
    public string EngineeringPositionId { get; set; } = "";
    public string Material { get; set; } = "";
    public double TargetVolumeM3 { get; set; }

    /// <summary>去向（由作业面带下来）。下达闸门与任务书都读它。</summary>
    public string DestinationId { get; set; } = "";
    public string DestinationName { get; set; } = "";
    /// <summary>等效运距 km。0 = 没录。</summary>
    public double EquivHaulKm { get; set; }
    public bool HasDestination => DestinationId.Length > 0 || DestinationName.Length > 0;

    /// <summary>穿孔笔的计划孔数（由穿孔计划带下来）。null = 台账没录。</summary>
    public int? DrillHoles { get; set; }
    /// <summary>穿孔笔的计划<b>总</b>延米 m。null = 未录。</summary>
    public double? DrillMeters { get; set; }

    public CoalQuality? QualityTarget { get; set; }
    public string Shift { get; set; } = "";
    public double StartHour { get; set; }
    public double EndHour { get; set; }
    public double PlannedHours { get; set; }
    public TaskStatus Status { get; set; } = TaskStatus.Planned;
    public List<SchedReason> Reasons { get; set; } = new();
    // 实绩回灌(达成度评价/滚动重排用)
    public double ActualVolumeM3 { get; set; }
    public double ActualHours { get; set; }
    public int TrucksOnSite { get; set; }
    public CoalQuality? QualityActual { get; set; }
    public double AttainmentPct => TargetVolumeM3 > 1e-6 ? Math.Round(ActualVolumeM3 / TargetVolumeM3 * 100, 0) : 0;
}

public sealed class PlanViolation
{
    public ViolationSeverity Severity { get; set; }
    public string Code { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ExploderResult
{
    public List<ShiftTask> Tasks { get; set; } = new();
    public List<PlanViolation> Violations { get; set; } = new();
}

// ─────────────────────────── 裂解装箱引擎（忠实移植原 TaskLib.Engine.TaskExploder，纯托管）───────────────────────────

/// <summary>
/// 把各作业面当日目标量按班次有效时窗 × 编组班产 装箱成班次任务，备采用尽则空闲、当日能力不足则报欠产(守恒回摊),
/// 并校核运力/设备双占/工序接续 + 综合配煤约束。产物 = ShiftTask[] + PlanViolation[]。纯逻辑、可单测。
/// </summary>
public static class TaskExploder
{
    public static ExploderResult Explode(ExploderConfig cfg)
    {
        var res = new ExploderResult();
        ApplyBlendConstraint(cfg, res);
        foreach (var face in cfg.Faces) ExplodeFace(cfg, face, res);

        foreach (var d in cfg.Drills)
            res.Tasks.Add(new ShiftTask
            {
                Id = $"{cfg.IdPrefix}-{d.EquipId}-穿", Process = ProcessType.Drill,
                Group = new EquipmentGroup { MainEquipment = d.EquipId },
                WorkZone = d.Zone, BenchElevationM = d.BenchElevationM, Material = d.Zone,
                // 孔数/总延米带下来：不带的话穿孔任务只有时窗没有量，任务书那一列只能写「—」
                DrillHoles = d.HoleCount, DrillMeters = d.HoleLengthM,
                Shift = ShiftOf(cfg, d.Start), StartHour = d.Start, EndHour = d.End, PlannedHours = Math.Round(d.End - d.Start, 1),
                Status = TaskStatus.Planned,
            });

        foreach (var mw in cfg.Maintenance)
            res.Tasks.Add(new ShiftTask
            {
                Id = $"{cfg.IdPrefix}-{mw.EquipId}-检", Process = ProcessType.Idle,
                Group = new EquipmentGroup { MainEquipment = mw.EquipId },
                Material = mw.Label, Shift = ShiftOf(cfg, mw.Start), StartHour = mw.Start, EndHour = mw.End,
                Status = TaskStatus.Planned, Reasons = new() { SchedReason.Maintenance },
            });

        CheckConstraints(cfg, res);
        CheckBlastSegmentation(cfg, res);
        return res;
    }

    private static void ExplodeFace(ExploderConfig cfg, FaceInput face, ExploderResult res)
    {
        // 降效落在能力上（不是时窗上）：班产先乘本面的能力系数，干不完自然由「当日欠产」报出来
        double cap = Math.Max(1e-6, face.Group.GroupCapacityM3PerH * cfg.WeatherFactorFor(face));
        CheckPreparedReserve(cfg, face, res);
        if (face.Process == ProcessType.Load && face.Group.Trucks.Count < face.Group.RecommendedTrucks)
            res.Violations.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Warn, Code = "运力不足",
                Message = $"{face.Zone} {face.Group.MainEquipment} 配 {face.Group.Trucks.Count} 车 < 荐 {face.Group.RecommendedTrucks}，铲将待车",
            });

        double remaining = face.DayTargetM3;
        foreach (var sh in cfg.Shifts)
        {
            var (ws, we) = WorkWindow(cfg, face.Group.MainEquipment, sh);
            double avail = Math.Max(0, we - ws);
            if (remaining <= 1)
            {
                if (avail >= 0.5) res.Tasks.Add(IdleTask(cfg, face, ws, we, sh, "空闲", SchedReason.OreShortage));
                continue;
            }
            if (avail < 0.5) continue;

            double maxVol = cap * avail;
            double vol = Math.Min(maxVol, remaining);
            double hours = vol / cap;
            double end = ws + hours;
            res.Tasks.Add(new ShiftTask
            {
                Id = $"{cfg.IdPrefix}-{face.Group.MainEquipment}-{ShiftShort(sh.Name)}",
                Process = face.Process, Group = Clone(face.Group),
                WorkZone = face.Zone, BenchElevationM = face.BenchElevationM, EngineeringPositionId = face.EngineeringPositionId,
                Material = face.Material, TargetVolumeM3 = Math.Round(vol), QualityTarget = face.Quality,
                // 去向随面带下来：下达闸门要判"这车拉到哪"，任务书要写卸载地点
                DestinationId = face.DestinationId, DestinationName = face.DestinationName,
                EquivHaulKm = face.EquivHaulKm,
                Shift = sh.Name, StartHour = Math.Round(ws, 2), EndHour = Math.Round(end, 2), PlannedHours = Math.Round(hours, 1),
                Status = TaskStatus.Planned,
            });
            remaining -= vol;
        }

        if (remaining > 1)
            res.Violations.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Warn, Code = "当日欠产",
                Message = $"{face.Zone} 当日能力不足，欠 {remaining:0} m³ 需回摊次日",
            });
    }

    private static void ApplyBlendConstraint(ExploderConfig cfg, ExploderResult res)
    {
        if (cfg.Blend is not { } std) return;
        var loads = cfg.Faces.Where(f => f.Process == ProcessType.Load && f.Quality != null && f.DayTargetM3 > 0).ToList();
        if (loads.Count < 2) return;

        double Blend() { double q = loads.Sum(f => f.DayTargetM3); return q > 1e-6 ? loads.Sum(f => f.DayTargetM3 * f.Quality!.AshPct) / q : 0; }
        // ★ 面日产能上限 = 班产 × 有效工时 × 降效系数 —— 与装箱那一步取**同一个闸**，
        //   否则会出现"配煤说移得动、装箱那边排不下"这种自相矛盾，而且谁都不报错。
        double Cap(FaceInput f) => f.Group.GroupCapacityM3PerH * cfg.EffHours * cfg.WeatherFactorFor(f);

        double before = Blend();
        if (before <= std.MaxAshPct + 0.01)
        {
            res.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Info, Code = "配煤达标", Message = $"综合灰分 {before:0.0}% ≤ 上限 {std.MaxAshPct:0.0}%" });
            return;
        }

        var orig = loads.ToDictionary(f => f, f => f.DayTargetM3);
        for (int guard = 0; guard < 200 && Blend() > std.MaxAshPct; guard++)
        {
            var hi = loads.OrderByDescending(f => f.Quality!.AshPct).First();
            var lo = loads.Where(f => f != hi).OrderBy(f => f.Quality!.AshPct).First();
            double step = Math.Min(Math.Min(Cap(lo) - lo.DayTargetM3, hi.DayTargetM3), 50);
            if (step <= 1) break;
            hi.DayTargetM3 -= step;
            lo.DayTargetM3 += step;
        }
        foreach (var f in loads) f.DayTargetM3 = Math.Round(f.DayTargetM3);
        double after = Blend();

        string moves = string.Join("、", loads.Where(f => Math.Abs(f.DayTargetM3 - orig[f]) >= 1).Select(f => $"{f.Zone} {orig[f]:0}→{f.DayTargetM3:0}"));
        if (after <= std.MaxAshPct + 0.05)
            res.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Info, Code = "配煤调整", Message = $"综合灰分 {before:0.00}→{after:0.00}% 达标（上限 {std.MaxAshPct:0.0}）：{moves}" });
        else
            res.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Warn, Code = "配煤不达标", Message = $"综合灰分 {after:0.00}% 仍 > 上限 {std.MaxAshPct:0.0}%（低灰面产能见顶）" });
    }

    /// <summary>
    /// 某设备某班的有效作业时窗：扣检修（班首）+ <b>全部</b>爆破清场 + 交接班损失 + 滚动重排起点。
    ///
    /// <para>
    /// <b>爆破从"一炮"改成"逐炮"</b>（原版 2026-08-11 补齐，Kylin 侧此前一直是补齐前那一版）：
    /// 原式只认 <c>cfg.BlastStart</c>（装配层取最早一炮），于是一天三炮时<b>后两炮在装箱里根本不存在</b> ——
    /// 界面上排得整整齐齐，计划里那几个时段仍在满负荷作业。现在按 <see cref="ExploderConfig.BlastWindows"/> 切段。
    /// </para>
    /// <para>
    /// 顺带修好一个老行为：爆破窗口盖住班首时，原式 <c>we = min(we, BlastStart)</c> 会把整个班压成 0 长度
    /// （相当于整班停产）；现在那种情况是"清场解除后开工"，取得回后面那一段。
    /// </para>
    /// </summary>
    private static (double ws, double we) WorkWindow(ExploderConfig cfg, string equipId, ShiftWindow sh)
    {
        double ws = sh.Start, we = sh.End;
        foreach (var mw in cfg.Maintenance)
            if (mw.EquipId == equipId && mw.Start < sh.End && mw.End > sh.Start)
                ws = Math.Max(ws, mw.End);
        if (sh.Start > 0) ws += cfg.HandoverRampH;
        if (cfg.FromHour > 0) ws = Math.Max(ws, cfg.FromHour);
        if (we <= ws) return (ws, ws);

        // 爆破把班切成几段时只取**最长的一段**：一条 (面 × 班) 只能出一条任务
        // （任务 Id 是「前缀-主设备-班次」，同班两条会撞 Id 还会被判「设备双占」）。
        // 放弃掉的段有多少小时由 CheckBlastSegmentation 如实报出来，不闷声吞掉。
        var segs = PitMine3D.Kylin.Data.BlastWindow.Subtract(ws, we, cfg.BlastWindows());
        if (segs.Count == 0) return (ws, ws);
        var best = segs[0];
        foreach (var sg in segs) if (sg.End - sg.Start > best.End - best.Start) best = sg;
        return (best.Start, Math.Max(best.Start, best.End));
    }

    private static ShiftTask IdleTask(ExploderConfig cfg, FaceInput face, double ws, double we, ShiftWindow sh, string label, SchedReason reason)
        => new()
        {
            Id = $"{cfg.IdPrefix}-{face.Group.MainEquipment}-{ShiftShort(sh.Name)}空",
            Process = ProcessType.Idle, Group = Clone(face.Group),
            WorkZone = face.Zone, Material = label, Shift = sh.Name,
            StartHour = Math.Round(ws, 2), EndHour = Math.Round(we, 2), Status = TaskStatus.Planned, Reasons = new() { reason },
        };

    private static void CheckConstraints(ExploderConfig cfg, ExploderResult res)
    {
        foreach (var g in res.Tasks.Where(t => t.Process != ProcessType.Idle).GroupBy(t => t.Group.MainEquipment))
        {
            var ordered = g.OrderBy(t => t.StartHour).ToList();
            for (int i = 1; i < ordered.Count; i++)
                if (ordered[i].StartHour < ordered[i - 1].EndHour - 0.01)
                    res.Violations.Add(new PlanViolation
                    {
                        Severity = ViolationSeverity.Error, Code = "设备双占", TaskId = ordered[i].Id,
                        Message = $"{g.Key} 时段重叠：{ordered[i - 1].Id} ∩ {ordered[i].Id}",
                    });
        }
        bool hasLoad = res.Tasks.Any(t => t.Process == ProcessType.Load);
        bool hasDrill = res.Tasks.Any(t => t.Process == ProcessType.Drill);
        if (hasLoad && !hasDrill)
            res.Violations.Add(new PlanViolation { Severity = ViolationSeverity.Warn, Code = "工序接续", Message = "有采装但无穿孔任务，备采接续存疑" });
    }

    /// <summary>
    /// 备采保有下限校核：按当日强度采下去，这个面还能撑几天。
    /// <b>只对录了备采储量的面生效</b>（0 = 未录，不是采空）；下限为 0 = 人工确认不校核。
    /// </summary>
    private static void CheckPreparedReserve(ExploderConfig cfg, FaceInput face, ExploderResult res)
    {
        if (cfg.MinPreparedDays <= 1e-6) return;
        if (face == null || face.Process != ProcessType.Load) return;
        if (face.AvailableReserveM3 <= 1e-6 || face.DayTargetM3 <= 1e-6) return;

        double days = face.AvailableReserveM3 / face.DayTargetM3;
        if (days < cfg.MinPreparedDays)
            res.Violations.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Warn, Code = "备采保有",
                Message = $"{face.Zone} 备采仅够 {days:0.0} 天 < 保有下限 {cfg.MinPreparedDays:0.#} 天 —— "
                        + "采准（穿孔/爆破）需前赶，否则该面将断档停采",
            });
    }

    /// <summary>
    /// 爆破把某个班切成多段时如实报账：装箱每班只取最长的一段（理由见 <see cref="WorkWindow"/>），
    /// 放弃掉的小时数必须写出来 —— 否则计划看着排满了，实际少排了几小时的活，谁也看不出来。
    /// 一个班一条，不逐面刷屏。
    /// </summary>
    private static void CheckBlastSegmentation(ExploderConfig cfg, ExploderResult res)
    {
        var windows = cfg.BlastWindows();
        if (windows.Count == 0) return;

        foreach (var sh in cfg.Shifts)
        {
            var segs = PitMine3D.Kylin.Data.BlastWindow.Subtract(sh.Start, sh.End, windows);
            double inShift = windows.Sum(w => Math.Max(0, Math.Min(w.End, sh.End) - Math.Max(w.Start, sh.Start)));

            if (segs.Count == 0)
            {
                if (inShift > 1e-6)
                    res.Violations.Add(new PlanViolation
                    {
                        Severity = ViolationSeverity.Warn, Code = "爆破清场",
                        Message = $"{sh.Name} 整班落在爆破清场内（{inShift:0.#} h），本班排不出作业",
                    });
                continue;
            }

            double longest = segs.Max(x => x.End - x.Start);
            double dropped = segs.Sum(x => x.End - x.Start) - longest;
            if (segs.Count > 1 && dropped > 0.25)
                res.Violations.Add(new PlanViolation
                {
                    Severity = ViolationSeverity.Info, Code = "爆破清场",
                    Message = $"{sh.Name} 被爆破清场切成 {segs.Count} 段，装箱只用最长的一段，"
                            + $"放弃 {dropped:0.#} h（这部分能力没进计划）",
                });
        }
    }

    private static EquipmentGroup Clone(EquipmentGroup g) => new()
    {
        MainEquipment = g.MainEquipment, Trucks = new List<string>(g.Trucks), Aux = new List<string>(g.Aux),
        RecommendedTrucks = g.RecommendedTrucks, GroupCapacityM3PerH = g.GroupCapacityM3PerH,
        // ★ 周期分解与单车载重必须跟着克隆：任务上的编组是这里克隆出来的，
        //   漏掉它们的话，派车单在任务上拿不到 τ_L/T_c 就一趟也展不开，
        //   而面上那份是全的 —— 于是"降效算得对、派车单空白"，两边各自都看不出问题。
        LoadTaktMin = g.LoadTaktMin, CycleTimeMin = g.CycleTimeMin,
        MatchFactor = g.MatchFactor, TruckPayloadT = g.TruckPayloadT,
    };

    private static string ShiftShort(string name) => name.StartsWith("早") ? "早" : name.StartsWith("中") ? "中" : name.StartsWith("夜") ? "夜" : name;
    private static string ShiftOf(ExploderConfig cfg, double hour) => cfg.Shifts.FirstOrDefault(s => hour >= s.Start && hour < s.End)?.Name ?? "";
}
