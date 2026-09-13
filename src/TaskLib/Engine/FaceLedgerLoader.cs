// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/FaceLedgerLoader.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PitMine3D.Kylin.Data;              // EquipmentDataContext（静态门面）
using PitMine3D.Kylin.Data.Entities;     // WorkingFace / WorkingFaceRouting
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

/// <summary>
/// 煤质目标草稿：灰 / 热 / 硫 / 水 四项**各自可空**。
///
/// <para>
/// 为什么不直接用 <see cref="CoalQuality"/> 装：那个契约四项都是非空 double，
/// 表达不了「只填了灰分，热值还没定」这种录入中间态；而 <c>working_face_routing</c>
/// 的四列本就各自可空，录一半就把已填的丢掉，是白丢用户的活。
/// </para>
/// <para>
/// 于是分两层：**存**——四项各自入库，半份如实存；**用**——四项齐全才组装成
/// <see cref="CoalQuality"/> 交给引擎（<see cref="ToQuality"/>）。半份若硬凑，
/// 没填的项会变成 0，配煤校核里「灰分 ≤ 0+容差」几乎恒假、「热值 ≥ 0−容差」恒真，
/// 一个半真半假的约束比没有约束更难查。
/// </para>
/// <para>
/// 本类原是 <c>WorkFaceLedgerWindow</c> 的嵌套类型，随读取层一并提到引擎层——
/// 装配盘子时也要用它，不能只有那一个窗口看得见。
/// </para>
/// </summary>
public sealed class FaceQualityDraft
{
    public double? Ash { get; set; }        // 灰分 %
    public double? Cv { get; set; }         // 热值 MJ/kg
    public double? Sulfur { get; set; }     // 硫分 %
    public double? Moisture { get; set; }   // 水分 %

    public bool IsComplete => Ash.HasValue && Cv.HasValue && Sulfur.HasValue && Moisture.HasValue;
    public bool IsEmpty => !Ash.HasValue && !Cv.HasValue && !Sulfur.HasValue && !Moisture.HasValue;

    /// <summary>填了一部分——**不构成**有效的煤质目标（如实存库、界面标出来，但不交给引擎）。</summary>
    public bool IsPartial => !IsEmpty && !IsComplete;

    /// <summary>四项齐全才给引擎；否则 null（= 该面无煤质目标，纯量矿口径）。</summary>
    public CoalQuality? ToQuality() => IsComplete
        ? new CoalQuality
        {
            AshPct = Ash!.Value,
            CalorificMJkg = Cv!.Value,
            SulfurPct = Sulfur!.Value,
            MoisturePct = Moisture!.Value,
        }
        : null;

    public static FaceQualityDraft From(CoalQuality? q) => q == null
        ? new FaceQualityDraft()
        : new FaceQualityDraft { Ash = q.AshPct, Cv = q.CalorificMJkg, Sulfur = q.SulfurPct, Moisture = q.MoisturePct };

    public FaceQualityDraft Clone() => new() { Ash = Ash, Cv = Cv, Sulfur = Sulfur, Moisture = Moisture };
}

// ─────────────────────────────────────────────────────────────────────────────
//  作业面台账「读」侧 —— working_face_routing(+working_face) → FaceInput。
//
//  两条路径，供两类调用者：
//    · <see cref="LoadFaces"/>  从台账**直接建**作业面清单 —— 装配当日盘子用。
//      台账有几个面就是几个面，不再受样例盘子里那七个硬编码面的约束。
//    · <see cref="ApplySaved"/> 把台账档案**合并到**已有作业面上 —— 台账窗口与
//      「台账为空、只能用样例种子」时用。
//
//  为什么要提到引擎层：这段逻辑原先是 WorkFaceLedgerWindow 的私有嵌套类，
//  于是「在台账里改的去向/运距/日目标」只在那一个窗口内生效，甘特、任务书、派车单、
//  报表、达成评价拿到的仍是样例盘子——同一天的去向在两个窗口能显示成两个样。
//  写侧（Upsert 回库）仍留在窗口：那是录入行为，只有一个调用者。
//
//  容错：DB 未接通 / 表为空 / 档案坏 一律静默降级（返回 null 或空表 + 一句来源文案），
//  绝不抛——盘子装不上台账时要能退回样例继续干活。
// ─────────────────────────────────────────────────────────────────────────────
public static class FaceLedgerLoader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 排土面的推排能力缺省 m³/h。排土侧没有编组求解器（<see cref="FleetMatcher"/> 只解采装面），
    /// 台账里也没有这一列（它是求解派生值，不该入库），故给一个工程缺省值：
    /// 中型推土机连续推排 200~250 m³/h，取 220。★ 它是缺省不是实测，来源文案里会写明。
    /// </summary>
    public const double DefaultDozerCapacityM3PerH = 220;

    /// <summary>
    /// 采装面在编组求解不可用时的兜底班产 m³/h（规则表缺失/运距全无）。
    /// 只为不让装箱除以 0，一旦 <see cref="FleetMatcher"/> 解得出就会被覆盖。
    /// </summary>
    public const double FallbackLoadCapacityM3PerH = 180;

    /// <summary>最近一次 <see cref="LoadFaces"/> 的来源文案（供 UI 显示"这盘面是哪来的"）。</summary>
    public static string LastSourceLabel { get; private set; } = "";

    // ═════════════════════════════════════════════════════════════════════════
    //  ① 从台账直接建作业面清单（装配盘子的主路径）
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 读 <c>working_face_routing</c> 建作业面清单；<paramref name="drafts"/> 非空时顺带带出煤质草稿。
    /// 返回 null 表示**台账不可用或为空**（调用方应回落样例种子）——返回空表则表示读到了但一条有效面都没有。
    /// </summary>
    public static List<FaceInput>? LoadFaces(Dictionary<string, FaceQualityDraft>? drafts = null)
    {
        List<WorkingFaceRouting> rows;
        try { rows = EquipmentDataContext.WorkingFaceRoutings.All().Where(r => r != null).ToList(); }
        catch (Exception ex)
        {
            LastSourceLabel = $"作业面：台账未接通（{Short(ex)}），用样例盘子";
            return null;
        }

        if (rows.Count == 0)
        {
            LastSourceLabel = "作业面：台账尚无档案，用样例盘子（在「作业面台账」保存一次即入库）";
            return null;
        }

        // working_face 只用来补主设备（台账没填 main_equipment 时的第二来源）。读不到不影响。
        var bodyByCode = new Dictionary<string, WorkingFace>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var wf in EquipmentDataContext.WorkingFaces.All())
                if (wf != null && !string.IsNullOrWhiteSpace(wf.FaceCode)) bodyByCode[wf.FaceCode.Trim()] = wf;
        }
        catch { /* 台阶几何读不到不妨碍排产 */ }

        var faces = new List<FaceInput>();
        int dumps = 0, withDest = 0, noEquip = 0;

        foreach (var r in rows)
        {
            string code = (r.FaceCode ?? "").Trim();
            if (code.Length == 0) continue;

            var f = new FaceInput { Zone = code, Process = ParseProcess(r.Process) };
            ApplyOne(f, r, fromScratch: true);

            // 主设备：台账列 → working_face.equipment_id → 空（下游会报「无主设备」而不是悄悄跑）
            if (string.IsNullOrWhiteSpace(f.Group.MainEquipment)
                && bodyByCode.TryGetValue(code, out var body)
                && !string.IsNullOrWhiteSpace(body.EquipmentId))
                f.Group.MainEquipment = body.EquipmentId!.Trim();
            if (string.IsNullOrWhiteSpace(f.Group.MainEquipment)) noEquip++;

            // 班产：采装面交 FleetMatcher 解（此处留 0），排土面无求解器 → 工程缺省
            if (f.Process == ProcessType.Dump)
            {
                f.Group.GroupCapacityM3PerH = DefaultDozerCapacityM3PerH;
                dumps++;
            }

            if (f.HasDestination) withDest++;
            if (drafts != null) drafts[code] = DraftOf(r);

            faces.Add(f);
        }

        if (faces.Count == 0)
        {
            LastSourceLabel = $"作业面：台账 {rows.Count} 条档案均无有效编号，用样例盘子";
            return null;
        }

        LastSourceLabel = $"作业面：台账 {faces.Count} 个面（{withDest} 个带去向 · 排土 {dumps} 个按推排缺省 {DefaultDozerCapacityM3PerH:0} m³/h"
                        + (noEquip > 0 ? $" · {noEquip} 个缺主设备" : "") + "）";
        return faces;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ② 把台账档案合并到已有作业面（台账窗口 / 样例种子路径）
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 反向合并：<c>working_face_routing</c> → 已有 <see cref="FaceInput"/>（按 face_code = Zone 匹配）。
    /// 返回一句来源文案；DB 不可用一律静默回落。
    ///
    /// <para>覆盖纪律（谁说了算）：</para>
    /// <list type="bullet">
    /// <item>去向 / 运距 / 分项 / 煤质 / 实配车 —— <b>存档优先</b>。那是上一次在台账窗口做的人工决策，
    /// 应当压过流向分配的自动解，否则用户改完一关窗就白改。</item>
    /// <item>日目标 —— <b>计划优先</b>。月计划摊到日的目标是当天的事实，存档里的是上一次的；
    /// 只有计划没给（且不是排土面的入方推导）时才用存档兜底。</item>
    /// <item>物料 —— 只在盘子里没有物料时才用存档补。</item>
    /// </list>
    /// </summary>
    /// <param name="resolveAfter">合并后是否立刻重解运距与编组（台账窗口要，装配链不要——它后面本就会跑一遍）。</param>
    public static string ApplySaved(ExploderConfig? cfg,
                                    Dictionary<string, FaceQualityDraft>? drafts = null,
                                    bool resolveAfter = true)
    {
        if (cfg == null || cfg.Faces.Count == 0) return "";

        List<WorkingFaceRouting> rows;
        try { rows = EquipmentDataContext.WorkingFaceRoutings.All().ToList(); }
        catch (Exception ex) { return $"作业面台账未接通（{Short(ex)}），本次仅用计划盘子"; }

        if (rows.Count == 0) return "作业面台账：尚无已存去向档案（改完点「保存台账」即入库）";

        var byCode = new Dictionary<string, WorkingFaceRouting>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            if (r != null && !string.IsNullOrWhiteSpace(r.FaceCode)) byCode[r.FaceCode.Trim()] = r;

        int applied = 0, dest = 0;
        foreach (var f in cfg.Faces)
        {
            if (string.IsNullOrWhiteSpace(f.Zone)) continue;
            if (!byCode.TryGetValue(f.Zone.Trim(), out var r)) continue;

            applied++;
            if (ApplyOne(f, r, fromScratch: false)) dest++;

            if (drafts != null) drafts[f.Zone.Trim()] = DraftOf(r);
        }

        if (applied == 0) return $"作业面台账：已存 {rows.Count} 条档案，但与当日盘子无同名作业面";

        // 去向变了 ⇒ 运距与编组必须跟着重解，否则会出现「去向是 A、运距还是 B」的台账。
        // 两个 ApplyTo 都不覆盖已有非零值，故存档带回来的运距会被尊重。
        if (resolveAfter)
        {
            try { HaulResolver.ApplyTo(cfg); } catch { }
            try { FleetMatcher.ApplyTo(cfg); } catch { }
        }

        return $"作业面台账：已套用 {applied} 个面的存档（其中 {dest} 个带去向）";
    }

    /// <summary>
    /// 把一条档案合并进一个作业面；返回是否带回了去向。
    /// <paramref name="fromScratch"/> = 从台账新建这个面（此时档案是唯一数据源，不存在"盘子里已有值"的让位问题）。
    /// </summary>
    internal static bool ApplyOne(FaceInput f, WorkingFaceRouting r, bool fromScratch)
    {
        bool hasDest = !string.IsNullOrWhiteSpace(r.DestinationId) || !string.IsNullOrWhiteSpace(r.DestinationName);

        if (fromScratch || string.IsNullOrWhiteSpace(f.EngineeringPositionId))
            if (!string.IsNullOrWhiteSpace(r.EngineeringPositionId))
                f.EngineeringPositionId = r.EngineeringPositionId.Trim();

        if (fromScratch || Math.Abs(f.BenchElevationM) < 1e-6)
            if (Math.Abs(r.BenchElevationM) > 1e-6) f.BenchElevationM = r.BenchElevationM;

        // ── 源坐标（V037）：存档优先，与去向同一条纪律 ──
        //  判据只看 X、Y：库里 (0,0) = 从没录过（Z=0 是合法标高，不参与判定）。
        //  录过 ⇒ 是人填的，清掉"系统按区域质心猜的"标记，免得后续被自动推导覆盖。
        if (Math.Abs(r.SourceX) > 1e-9 || Math.Abs(r.SourceY) > 1e-9)
        {
            f.SourceX = r.SourceX;
            f.SourceY = r.SourceY;
            f.SourceZ = r.SourceZ;
            try { HaulResolver.ClearAutoSource(f); } catch { }
        }

        // ── 空间身份与备采家底（V041）：全是人填的，档案就是唯一来源，不存在"盘子里已有值"的让位 ──
        //  与坐标同一条纪律：录过才盖，没录过一律不动（0/null 是"未录"，不是"值就是 0"）。
        if (!string.IsNullOrWhiteSpace(r.UnitId) && (fromScratch || string.IsNullOrWhiteSpace(f.UnitId)))
            f.UnitId = r.UnitId.Trim();

        if (r.AvailableReserveM3 > 1e-6 && (fromScratch || f.AvailableReserveM3 <= 1e-6))
            f.AvailableReserveM3 = r.AvailableReserveM3;

        if (r.AdvanceAzimuthDeg is { } az && (fromScratch || f.AdvanceAzimuthDeg == null))
            f.AdvanceAzimuthDeg = az;

        if (r.MiningWidthM is { } mw && mw > 1e-6 && (fromScratch || f.MiningWidthM == null))
            f.MiningWidthM = mw;

        if (!string.IsNullOrWhiteSpace(r.ShovelModelPref) && (fromScratch || string.IsNullOrWhiteSpace(f.ShovelModelPref)))
            f.ShovelModelPref = r.ShovelModelPref.Trim();

        if (!string.IsNullOrWhiteSpace(r.MainEquipment) && (fromScratch || string.IsNullOrWhiteSpace(f.Group.MainEquipment)))
            f.Group.MainEquipment = r.MainEquipment.Trim();

        // 物料：新建时档案就是唯一来源；合并时只在盘子没给物料时才补
        if (fromScratch || (f.Mix == null && !MaterialCatalog.Exists(f.MaterialCode)))
        {
            if (!string.IsNullOrWhiteSpace(r.MaterialMix))
            {
                f.Mix = MaterialMix.Parse(r.MaterialMix);
                f.Material = r.MaterialMix.Trim();
            }
            else if (MaterialCatalog.Exists(r.MaterialCode))
            {
                f.MaterialCode = r.MaterialCode.Trim();
                f.Material = MaterialCatalog.Resolve(f.MaterialCode).Name;
            }
        }

        // 排土面的日目标：档案说了算，档案没说才自动推导。
        //  · 档案显式置了推导位 ⇒ 推导（采排守恒）；
        //  · 档案给了日目标 ⇒ 尊重人工填的那个数，不擅自改成推导；
        //  · 排土面既没推导位也没日目标 ⇒ 按入方推导，而不是留 0 ——
        //    留 0 会让这个排土场今天一条任务都没有，采出来的岩看着凭空消失了。
        if (fromScratch)
            f.DerivedFromInbound = r.DerivedFromInbound != 0
                                || (f.Process == ProcessType.Dump && r.DayTargetM3 <= 1e-6);

        // 日目标：计划优先，存档只在计划没给且不是入方推导面时兜底
        if (!f.DerivedFromInbound && r.DayTargetM3 > 1e-6 && (fromScratch || f.DayTargetM3 <= 1e-6))
            f.DayTargetM3 = r.DayTargetM3;

        // 煤质目标：四项齐全才组装；半份 ⇒ null（半份本身由草稿另行带出）
        f.Quality = DraftOf(r).ToQuality();

        var trucks = SplitTrucks(r.AssignedTrucks);
        f.Group.Trucks.Clear();
        f.Group.Trucks.AddRange(trucks);

        if (!hasDest) return false;

        f.DestinationId = (r.DestinationId ?? "").Trim();
        f.DestinationName = (r.DestinationName ?? "").Trim();
        if (Enum.TryParse<SinkKind>(r.DestinationKind, ignoreCase: true, out var kind))
            f.DestinationKind = kind;

        // 运距：存档有就用（HaulResolver 不覆盖非零值）；没有就清零让三层兜底重解
        f.HaulDistanceKm = r.HaulDistanceKm > 1e-6 ? r.HaulDistanceKm : 0;
        f.EquivHaulKm = r.EquivHaulKm > 1e-6 ? r.EquivHaulKm : 0;

        // 混采分项：一条任务多个去向，整份取回；解不出来就当没有，绝不半份合并
        var splits = ParseSplits(r.SplitsJson);
        if (splits != null && splits.Count > 0) f.Splits = splits;

        return true;
    }

    /// <summary>档案 → 煤质草稿（四项各自可空，NULL 原样带回来，不折成 0）。</summary>
    internal static FaceQualityDraft DraftOf(WorkingFaceRouting r) => new()
    {
        Ash = Clean(r.QualityAshPct),
        Cv = Clean(r.QualityCvMjKg),
        Sulfur = Clean(r.QualitySulfurPct),
        Moisture = Clean(r.QualityMoisturePct),
    };

    /// <summary>库里的脏值（NaN/无穷/负数）一律当没填——比带着它去做配煤校核安全。</summary>
    private static double? Clean(double? v)
        => v.HasValue && !double.IsNaN(v.Value) && !double.IsInfinity(v.Value) && v.Value >= 0 ? v : null;

    /// <summary>
    /// 实配车号串 → 车号表：中英文逗号/分号/斜杠/顿号/空格都收，去空白、去重
    /// （同一辆车录两遍不该算两辆——车数是「运力够不够」的判据，多算一辆就少报一次运力不足）。
    /// </summary>
    public static List<string> SplitTrucks(string? raw)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(new[] { ',', '，', ';', '；', '/', '、', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string id = part.Trim();
            if (id.Length > 0 && seen.Add(id)) list.Add(id);
        }
        return list;
    }

    private static List<MaterialDestination>? ParseSplits(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json!.Trim() is "[]" or "{}") return null;
        try { return JsonSerializer.Deserialize<List<MaterialDestination>>(json, Json); }
        catch { return null; }   // 档案坏了不能拖垮整个台账，退化成"没有分项"
    }

    /// <summary>工序：档案里存的是枚举名（Load/Dump）；解不出按采装（大多数面是采装）。</summary>
    private static ProcessType ParseProcess(string? s)
        => Enum.TryParse<ProcessType>((s ?? "").Trim(), ignoreCase: true, out var p) ? p : ProcessType.Load;

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
