using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;             // EquipmentDataContext（排土场 / 装卸点台账；未就绪时静默回落空清单）
using PitMine3D.Kylin.Data.Entities;    // DumpSite / LoadUnloadPoint

namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  月计划的「去向维」本体 —— 排到哪、还能排多少。
//
//  把 dump_site(排土场) 与 load_unload_point(破碎站/煤仓/堆场) 统一成一类「去向」，
//  流向分配、库容校核、运距计算才能一视同仁地处理。
//
//  三条露天矿铁律落在这里：
//   · 剩余库容按【排弃占容方 V容 = V实×Kr】扣，不是按实方、更不是按松方扣。
//   · 内排土场要等采空区形成才能启用（启用日期 / 状态）——写死"永远外排"会让方案成本系统性偏高，
//     写死"永远内排"则会把还没形成的采空区当库容用。
//   · 表土只能进表土堆场、煤不能进排土场（物料侧 AllowedSinks 判定）。
//
//  跨模块读 GeoDataBase 全程 try/catch：数据库没准备好时返回**空清单**（
//  两边对接时不会因为样例不一致而对不上号），绝不让 PlanLib 崩。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一个受排/受矿点。容量口径一律是【占容方 万m³】—— 排土场吃的是沉降稳定后的体积。</summary>
public sealed class PlanDestination
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public PlanSinkKind Kind { get; set; } = PlanSinkKind.ExternalDump;

    /// <summary>设计容量（占容方 万m³）。破碎站/煤仓这类通过型去向留 0 表示不限。</summary>
    public double DesignCapacityWanM3 { get; set; }

    /// <summary>期初已排弃/已入库（占容方 万m³）。排产逐月在此之上累加。</summary>
    public double FilledWanM3 { get; set; }

    /// <summary>兜底运距 km（无路网求解时用）。</summary>
    public double FallbackHaulKm { get; set; } = 2.5;

    /// <summary>卸点通过能力 t/h（展示用；月粒度不做小时级校核）。</summary>
    public double AcceptTph { get; set; }

    /// <summary>状态 active / full / closed。</summary>
    public string Status { get; set; } = "active";

    /// <summary>启用日期（内排土场须等采空区形成）；null = 已启用。</summary>
    public DateTime? OpenFrom { get; set; }

    /// <summary>关闭日期（预计）；null = 不关闭。</summary>
    public DateTime? CloseAt { get; set; }

    /// <summary>关联台账实体（dump_site.dump_id / load_unload_point.id），供三维定位与下游对账。</summary>
    public string RefEntityId { get; set; } = "";

    public bool IsCapacityLimited => DesignCapacityWanM3 > 1e-9;
    /// <summary>剩余库容（占容方 万m³）。不限容量返回 +∞。</summary>
    public double RemainingWanM3 => IsCapacityLimited ? Math.Max(0, DesignCapacityWanM3 - FilledWanM3) : double.PositiveInfinity;
    public double FillPct => IsCapacityLimited ? Math.Min(100, FilledWanM3 / DesignCapacityWanM3 * 100) : 0;
    public bool IsActive => string.Equals(Status, "active", StringComparison.OrdinalIgnoreCase);
    public bool IsDumping => Kind.IsDumping();
    public bool IsInternalDump => Kind == PlanSinkKind.InternalDump;
    public string KindText => Kind.Label();

    /// <summary>等效运距 km（含坡度/路况折算）—— 内排下排折减、外排上排加成，运输功按它算。</summary>
    public double EquivHaulKm => FallbackHaulKm * Kind.EquivFactor();

    /// <summary>本去向能否接纳该物料（合规硬约束）。</summary>
    public bool Accepts(PlanMaterialSpec m) => m.Accepts(Kind);
    public bool Accepts(string? materialCode) => Accepts(PlanMaterialCatalog.Resolve(materialCode));

    /// <summary>指定年月是否已启用且未关闭（内排启用时机的判据）。</summary>
    public bool IsOpenAt(int year, int month)
    {
        if (!IsActive) return false;
        if (OpenFrom == null && CloseAt == null) return true;     // 无时窗约束，直接可用
        var monthStart = new DateTime(Math.Clamp(year, 1, 9999), Math.Clamp(month, 1, 12), 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);
        if (OpenFrom is { } o && o > monthEnd) return false;      // 采空区还没形成 / 尚未启用
        if (CloseAt is { } c && c < monthStart) return false;
        return true;
    }

    public string CapacityCaption => IsCapacityLimited
        ? $"余 {RemainingWanM3:N0} 万m³（填{FillPct:0.#}%）"
        : "容量不限";

    public string Caption => $"{Name}（{KindText}）";
    /// <summary>下拉/列头显示：名称 + 类别 + 运距。</summary>
    public string PickerText => $"{Name} · {KindText} · {FallbackHaulKm:0.#}km";

    public PlanDestination Copy() => (PlanDestination)MemberwiseClone();
}

/// <summary>
/// 去向台账。优先读 GeoDataBase（dump_site + load_unload_point 的卸载点），
/// 读不到 / 为空则返回**空清单**（不再补内置样例 —— 假去向比没有去向坏得多）。
/// </summary>
public static class PlanDestinationCatalog
{
    private static List<PlanDestination>? _cache;

    /// <summary>数据来源文案（面板上要显示清楚这批去向是台账还是样例）。</summary>
    public static string SourceText { get; private set; } = "（尚未读取）";

    /// <summary>当前去向集（首次访问自动读一次；<see cref="Reload"/> 强制重读）。</summary>
    public static IReadOnlyList<PlanDestination> Current => _cache ??= Load();

    /// <summary>强制重读台账（面板上「重新读取去向台账」按钮用）。</summary>
    public static IReadOnlyList<PlanDestination> Reload() { _cache = Load(); return _cache; }

    /// <summary>自检直通：用一批合成去向顶替台账（只在 PITMINE_SELFTEST 脚本里用，让空库上也能核对矩阵列/库容条/改投；下次 <see cref="Reload"/> 即恢复台账）。</summary>
    internal static void SelftestOverride(List<PlanDestination> list, string sourceText) { _cache = list; SourceText = sourceText; }

    public static PlanDestination? Find(string? id)
        => string.IsNullOrWhiteSpace(id) ? null
         : Current.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Current.FirstOrDefault(d => string.Equals(d.Name, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>某物料可选的去向（UI 下拉按此过滤 —— 让合规约束在界面上就是硬约束）。</summary>
    public static List<PlanDestination> CandidatesFor(string? materialCode)
    {
        var spec = PlanMaterialCatalog.Resolve(materialCode);
        return Current.Where(d => d.Accepts(spec)).ToList();
    }

    /// <summary>按作业面上的 DestinationId 回填去向名/类别/运距（下拉只选 Id，其余字段由台账补齐）。</summary>
    public static void ApplyTo(WorkingFace f)
    {
        var d = Find(f.DestinationId);
        if (d == null) return;
        f.DestinationId = d.Id;
        f.DestinationName = d.Name;
        f.DestinationKindText = d.KindText;
        if (f.HaulDistanceKm <= 1e-6) f.HaulDistanceKm = Math.Round(d.FallbackHaulKm, 2);
        if (f.EquivHaulKm <= 1e-6) f.EquivHaulKm = Math.Round(f.HaulDistanceKm * d.Kind.EquivFactor(), 2);
    }

    /// <summary>清掉作业面上与物料不兼容的去向（改物料时调用）。返回是否清过。</summary>
    public static bool DropIncompatibleDestination(WorkingFace f)
    {
        if (string.IsNullOrWhiteSpace(f.DestinationId)) return false;
        var d = Find(f.DestinationId);
        if (d != null && d.Accepts(f.MaterialCode)) return false;
        f.DestinationId = ""; f.DestinationName = ""; f.DestinationKindText = "";
        f.HaulDistanceKm = 0; f.EquivHaulKm = 0;
        return true;
    }

    // ── 台账读取（全程容错）──

    private static List<PlanDestination> Load()
    {
        var list = new List<PlanDestination>();
        int nDump = 0, nPoint = 0;

        // ① 排土场台账 dump_site：容量/已填是本包最关键的两个数（按占容方口径）
        try
        {
            foreach (var s in EquipmentDataContext.DumpSites.All(activeOnly: false))
            {
                if (s == null || string.IsNullOrWhiteSpace(s.DumpId)) continue;
                var kind = PlanSinkKinds.FromText(s.DumpType);
                list.Add(new PlanDestination
                {
                    Id = s.DumpId,
                    Name = string.IsNullOrWhiteSpace(s.Name) ? s.DumpId : s.Name,
                    Kind = kind,
                    DesignCapacityWanM3 = Math.Max(0, s.DesignCapacityWanM3),
                    FilledWanM3 = Math.Max(0, s.CurrentFilledWanM3),
                    FallbackHaulKm = DefaultHaulKm(kind),
                    Status = string.IsNullOrWhiteSpace(s.Status) ? "active" : s.Status,
                    OpenFrom = s.StartDate,
                    CloseAt = s.CloseDate,
                    RefEntityId = s.DumpId,
                });
                nDump++;
            }
        }
        catch { /* GeoDataBase 未就绪 → 跳过，下面还有装卸点与样例兜底 */ }

        // ② 装卸点台账 load_unload_point 的**卸载点**：破碎站 / 煤仓 / 堆场（通过型，不占排土库容）
        try
        {
            foreach (var p in EquipmentDataContext.LoadUnloadPoints.All())
            {
                if (p == null) continue;
                if (!string.Equals(p.Kind, "unloading", StringComparison.OrdinalIgnoreCase)) continue;
                var kind = MapUnloadSub(p.UnloadSub);
                string id = $"LU-{p.Id}";
                if (list.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new PlanDestination
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(p.Name) ? id : p.Name,
                    Kind = kind,
                    DesignCapacityWanM3 = 0,                 // 通过型去向：库容不限，只受通过能力约束
                    AcceptTph = Math.Max(0, p.ThroughputTph),
                    FallbackHaulKm = DefaultHaulKm(kind),
                    Status = "active",
                    RefEntityId = p.Id.ToString(),
                });
                nPoint++;
            }
        }
        catch { /* 同上 */ }

        if (list.Count == 0)
        {
            SourceText = "◆ 去向台账读不到或为空 —— **一个去向都没有**（不再补内置样例：假去向会让排产、运输功、库容闸全都算得出数而说的是另一个矿）。到「去向台账」建档，或点「按台账重建排土场」。";
            return Sample();
        }

        SourceText = $"GeoDataBase 台账（排土场 {nDump} · 卸载点 {nPoint}）";
        return list;
    }

    private static PlanSinkKind MapUnloadSub(string? sub) => (sub ?? "").Trim().ToLowerInvariant() switch
    {
        "crusher" => PlanSinkKind.Crusher,
        "dump" => PlanSinkKind.ExternalDump,
        "stockpile" => PlanSinkKind.Stockpile,
        "silo" => PlanSinkKind.Silo,
        _ => PlanSinkKinds.FromText(sub),
    };

    /// <summary>按去向类型的兜底运距 km（无路网求解时用；内排最短、原煤仓最远）。</summary>
    private static double DefaultHaulKm(PlanSinkKind k) => k switch
    {
        PlanSinkKind.InternalDump => 1.4,
        PlanSinkKind.TopsoilYard => 2.0,
        PlanSinkKind.Crusher => 2.6,
        PlanSinkKind.ExternalDump => 3.2,
        PlanSinkKind.Stockpile => 3.0,
        PlanSinkKind.Silo => 3.8,
        _ => 2.5,
    };

    /// <summary>
    /// <b>空清单</b> —— 台账读不到时的兜底。
    ///
    /// <para><b>这里原来返回五条编出来的去向</b>（D-N1 北排土场 / D-IN1 内排场 /
    /// TS-1 表土堆场 / CR-1 1号破碎站 / SL-1 原煤仓），容量、通过能力、兜底运距全是写死的数。</para>
    ///
    /// <para><b>假去向比没有去向坏得多</b>：排产会把量排到「表土堆场」上、
    /// 运输功按 2.0km 兜底运距算、库容闸按 400 万m³ 卡 ——
    /// 每一个数都算得出来、每一张报表都自洽，而它们说的是另一个矿。
    /// 空着则一路报「一个去向都没有」，人知道该去建档。</para>
    /// </summary>
    public static List<PlanDestination> Sample() => new();
}


/// <summary>
/// 库容台账（排产用的**可写副本**）。逐月按占容方 V容 扣减，不动 <see cref="PlanDestinationCatalog"/> 里的原始台账。
/// "第 8 个月北排土场排满"这种事就是靠它在月计划阶段暴露出来的。
/// </summary>
public sealed class PlanDumpLedger
{
    private readonly List<PlanDestination> _items;

    public PlanDumpLedger(IEnumerable<PlanDestination> source)
        => _items = source.Select(d => d.Copy()).ToList();

    /// <summary>按当前台账建账（读不到台账即样例）。</summary>
    public static PlanDumpLedger FromCatalog() => new(PlanDestinationCatalog.Current);

    public IReadOnlyList<PlanDestination> Destinations => _items;

    public PlanDestination? Find(string? id)
        => string.IsNullOrWhiteSpace(id) ? null
         : _items.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? _items.FirstOrDefault(d => string.Equals(d.Name, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>剩余库容（占容方 万m³）；不限容量或找不到返回 +∞。</summary>
    public double Remaining(string? id)
    {
        var d = Find(id);
        return d == null ? double.PositiveInfinity : d.RemainingWanM3;
    }

    /// <summary>累加排弃占容方（只有排弃类去向占库容；通过型去向不扣）。</summary>
    public void AddDump(string? id, double dumpWanM3)
    {
        var d = Find(id);
        if (d == null || dumpWanM3 <= 0) return;
        if (!d.IsDumping || !d.IsCapacityLimited) return;
        d.FilledWanM3 += dumpWanM3;
    }

    /// <summary>某年月可用（已启用 / 未关闭 / 未排满）且接纳该物料的去向。</summary>
    public List<PlanDestination> CandidatesFor(string? materialCode, int year, int month)
    {
        var spec = PlanMaterialCatalog.Resolve(materialCode);
        return _items.Where(d => d.Accepts(spec) && d.IsOpenAt(year, month)).ToList();
    }

    public PlanDumpLedger Clone() => new(_items);
}
