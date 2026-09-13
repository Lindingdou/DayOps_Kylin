using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 一份待分配的「供给」：某作业面某物料本月要采剥的原位实方量，以及面上钉死的去向（可空）。
/// </summary>
public sealed class PlanSupply
{
    public string SourceName { get; set; } = "";
    public string SourceRefId { get; set; } = "";
    public string MaterialCode { get; set; } = PlanMaterialCatalog.Rock;
    /// <summary>原位实方量（万m³）。</summary>
    public double InSituWanM3 { get; set; }

    /// <summary>面上指定的去向 Id（空=交给贪心分配）。</summary>
    public string PinnedDestinationId { get; set; } = "";
    /// <summary>
    /// 该去向是面上为哪种物料指定的（= 面的 MaterialCode）。
    /// 采煤面钉的是煤的去向，它伴生的剥离量当然进不了破碎站 —— 这种"不适用"要静默转贪心；
    /// 只有**本物料自己**被钉了不兼容的去向才值得报警，否则一个月能刷出几十条无效告警。
    /// </summary>
    public string PinnedForMaterialCode { get; set; } = "";
    /// <summary>面上给的实际运距 km（0=取去向兜底运距）。</summary>
    public double PinnedHaulKm { get; set; }
    /// <summary>面上给的等效运距 km（0=按去向类型折算）。</summary>
    public double PinnedEquivHaulKm { get; set; }
}

/// <summary>
/// 采排配对内核：把月采出量 / 月剥离量拆成一条条 <see cref="PlanFlow"/>，并给没指定去向的流做分配。
///
/// 分配目标：**运输功最小**（Σ 吨量×等效运距）。约束三条，缺一不可：
///   ① 物料兼容 —— 表土只进表土堆场、煤不进排土场（<see cref="PlanMaterialSpec.AllowedSinks"/>）。
///   ② 库容够   —— 按【占容方 V容 = V实×Kr】扣，不是按实方也不是按松方（Kr 远小于 Ks）。
///   ③ 时机对   —— 内排土场要等采空区形成才启用（<see cref="PlanDestination.IsOpenAt"/>）。
///
/// 几十条流的规模用贪心足够（不引第三方求解器）：同一条供给对所有候选去向的吨量相同，
/// 所以「运输功最小」退化成「等效运距最短优先」；受库容约束时按运距从近到远依次填满，
/// 天然形成"先内排、内排满了转外排"的排弃时序 —— 这正是露天矿降本的实际做法。
/// 排不下时**必须**给出警示，绝不静默丢量。
/// </summary>
public static class PlanFlowAllocator
{
    /// <summary>
    /// 分配一组供给，返回物料流；同时按占容方在 <paramref name="ledger"/> 上逐条扣减库容。
    /// </summary>
    /// <param name="ignorePinned">true=忽略面上钉死的去向，全部按运输功最小重排（面板上的「重新分配」按钮用）。</param>
    public static List<PlanFlow> Assign(IEnumerable<PlanSupply> supplies, PlanDumpLedger ledger,
                                        int year, int month, string periodLabel,
                                        IList<string>? warnings = null, bool ignorePinned = false)
    {
        var flows = new List<PlanFlow>();
        foreach (var s in supplies)
        {
            if (s.InSituWanM3 <= 1e-9) continue;
            AssignOne(s, ledger, year, month, periodLabel, flows, warnings, ignorePinned);
        }
        return flows;
    }

    private static void AssignOne(PlanSupply s, PlanDumpLedger ledger, int year, int month, string periodLabel,
                                  List<PlanFlow> flows, IList<string>? warnings, bool ignorePinned)
    {
        var spec = PlanMaterialCatalog.Resolve(s.MaterialCode);
        double left = s.InSituWanM3;

        // 候选去向：物料兼容 + 已启用未关闭；按等效运距升序（= 单位吨量运输功最小）。
        var candidates = ledger.CandidatesFor(s.MaterialCode, year, month)
                               .OrderBy(d => d.EquivHaulKm)
                               .ToList();

        // ① 面上钉死的去向优先（且必须物料兼容、当期已启用），排在候选队首。
        PlanDestination? pinned = null;
        if (!ignorePinned && !string.IsNullOrWhiteSpace(s.PinnedDestinationId))
        {
            pinned = ledger.Find(s.PinnedDestinationId);
            if (pinned != null && (!pinned.Accepts(spec) || !pinned.IsOpenAt(year, month)))
            {
                // 只有"面上就是为这种物料钉的去向"却不兼容/未启用时才报警（如采煤面把煤钉去了排土场）；
                // 采煤面伴生剥离量进不了破碎站属于正常，静默转贪心。
                bool pinnedForThisMaterial = string.Equals(s.MaterialCode, s.PinnedForMaterialCode, StringComparison.OrdinalIgnoreCase);
                if (pinnedForThisMaterial && !pinned.Accepts(spec))
                    Warn(warnings, $"{periodLabel} {s.SourceName}·{spec.Name}：面上指定去向「{pinned.Name}（{pinned.KindText}）」不接纳该物料，已改按运输功最小分配。");
                else if (pinnedForThisMaterial)
                    Warn(warnings, $"{periodLabel} {s.SourceName}·{spec.Name}：面上指定去向「{pinned.Name}」本月尚未启用/已关闭，已改按运输功最小分配。");
                pinned = null;
            }
            if (pinned != null)
            {
                candidates.Remove(pinned);
                candidates.Insert(0, pinned);
            }
        }

        if (candidates.Count == 0)
        {
            // 一个兼容去向都没有（如台账里根本没有表土堆场）——留空去向 + 警示，量仍留在表里不丢。
            flows.Add(new PlanFlow
            {
                SourceName = s.SourceName, SourceRefId = s.SourceRefId,
                MaterialCode = s.MaterialCode, InSituWanM3 = Math.Round(left, 2),
            });
            Warn(warnings, $"{periodLabel} {s.SourceName}·{spec.Name} {left:0.#}万m³实方：台账里没有可接纳该物料的去向（{spec.Name}只能进{AllowedText(spec)}），本月无处可去。");
            return;
        }

        foreach (var d in candidates)
        {
            if (left <= 1e-9) break;

            // 该去向本月还能吃下多少**实方**：占容方剩余 ÷ Kr。通过型去向（破碎站/煤仓）不限。
            double roomInSitu = d.IsDumping && d.IsCapacityLimited
                ? d.RemainingWanM3 / Math.Max(1e-6, spec.ResidualSwellFactor)
                : double.PositiveInfinity;
            if (roomInSitu <= 1e-9) continue;

            double take = Math.Min(left, roomInSitu);
            double takeDump = spec.ToDump(take);
            flows.Add(MakeFlow(s, d, take, pinned == d));
            ledger.AddDump(d.Id, takeDump);       // 按占容方扣，不是按实方
            left -= take;

            if (roomInSitu < double.PositiveInfinity && left > 1e-9)
                Warn(warnings, $"{periodLabel} {d.Name} 剩余库容只吃得下 {s.SourceName}·{spec.Name} 的 {take:0.#}万m³实方（{takeDump:0.#}万m³占容，已排满），" +
                               $"余 {left:0.#}万m³实方转下一去向。");
        }

        if (left > 1e-6)
        {
            // 所有兼容去向都排满了：量落到运距最近的那个（超容），并给出硬警示 —— 这就是月计划阶段
            // 要暴露的"第 N 个月排土场排满"。
            var fallback = candidates[0];
            flows.Add(MakeFlow(s, fallback, left, pinned == fallback));
            ledger.AddDump(fallback.Id, spec.ToDump(left));
            Warn(warnings, $"◆{periodLabel} 排弃能力不足：{s.SourceName}·{spec.Name} 尚有 {left:0.#}万m³实方（{spec.ToDump(left):0.#}万m³占容）排不下，" +
                           $"全部兼容去向已满，暂挂「{fallback.Name}」超容 —— 须扩容/新辟排土场或调整剥离节奏。");
        }
    }

    private static PlanFlow MakeFlow(PlanSupply s, PlanDestination d, double inSitu, bool usePinnedHaul)
    {
        double haul = usePinnedHaul && s.PinnedHaulKm > 1e-6 ? s.PinnedHaulKm : d.FallbackHaulKm;
        double equiv = usePinnedHaul && s.PinnedEquivHaulKm > 1e-6 ? s.PinnedEquivHaulKm : haul * d.Kind.EquivFactor();
        return new PlanFlow
        {
            SourceName = s.SourceName, SourceRefId = s.SourceRefId,
            MaterialCode = s.MaterialCode, InSituWanM3 = Math.Round(inSitu, 2),
            DestinationId = d.Id, DestinationName = d.Name, DestinationKind = d.Kind,
            HaulKm = Math.Round(haul, 2), EquivHaulKm = Math.Round(equiv, 2),
        };
    }

    private static string AllowedText(PlanMaterialSpec spec)
        => spec.AllowedSinks.Count == 0 ? "任意去向" : string.Join("/", spec.AllowedSinks.Select(k => k.Label()));

    private static void Warn(IList<string>? warnings, string msg)
    {
        if (warnings == null) return;
        if (!warnings.Contains(msg)) warnings.Add(msg);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  供给构造：把「月采出量(万t) + 月剥离量(万m³实方)」按作业面份额 × 物料构成拆成供给
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 按各作业面的产能份额把当月采出量与剥离量拆成供给。
    /// · 采出侧按吨量分（吨量是守恒量），再由各面物料密度折回实方 —— 保证 Σ吨量 == 月采出量。
    /// · 剥离侧按实方分，再按面上混采构成 / 默认剥离岩性构成拆到各岩性 —— 保证 Σ实方 == 月剥离量。
    ///   拆岩性不是为了好看：表土必须单独进表土堆场，不拆就看不出表土堆场会先于排土场排满。
    /// </summary>
    public static List<PlanSupply> BuildSupplies(ShortTermPlan p, double coalWanT, double stripWanM3)
    {
        var list = new List<PlanSupply>();
        var faces = p.Faces.Count > 0
            ? p.Faces.ToList()
            : new List<WorkingFace> { new() { Name = "全矿", SharePct = 100 } };

        // 采出份额只在**出煤的面**之间分（纯剥离面不该分到煤）；剥离份额按全部面分（每个面头上都有覆盖岩）。
        // 一个出煤面都没有时退化为全部面共担，免得月采出量凭空消失。
        var oreFaces = faces.Where(FaceHasOre).ToList();
        if (oreFaces.Count == 0) oreFaces = faces;
        double oreSum = oreFaces.Sum(f => Math.Max(0, f.SharePct));
        double wasteSum = faces.Sum(f => Math.Max(0, f.SharePct));

        foreach (var f in faces)
        {
            var mix = PlanMaterialMix.Parse(f.MaterialMixText);
            var faceSpec = PlanMaterialCatalog.Resolve(f.MaterialCode);

            // ── 采出侧（万t → 各煤种实方）：按吨量分，吨量守恒 ──
            if (oreFaces.Contains(f))
            {
                double oreShare = oreSum > 1e-9 ? Math.Max(0, f.SharePct) / oreSum : 1.0 / oreFaces.Count;
                var oreShares = mix.OreShares();
                if (oreShares.Count == 0)
                    oreShares = new List<(string, double)> { (faceSpec.IsOre ? faceSpec.Code : PlanMaterialCatalog.Coal, 1.0) };
                double faceCoalT = coalWanT * oreShare;
                foreach (var (code, frac) in oreShares)
                {
                    var spec = PlanMaterialCatalog.Resolve(code);
                    double vol = spec.FromTonnage(faceCoalT * frac);
                    if (vol > 1e-9) list.Add(NewSupply(f, code, vol));
                }
            }

            // ── 剥离侧（万m³实方 → 各岩性实方）：按实方分，实方守恒 ──
            double wasteShare = wasteSum > 1e-9 ? Math.Max(0, f.SharePct) / wasteSum : 1.0 / faces.Count;
            if (wasteShare <= 1e-9) continue;
            var wasteShares = mix.WasteShares();
            if (wasteShares.Count == 0)
                wasteShares = faceSpec.IsOre
                    ? PlanMaterialCatalog.DefaultWasteMix.ToList()          // 采煤面：按默认剥离岩性构成拆
                    : new List<(string, double)> { (faceSpec.Code, 1.0) };  // 剥离面：全按本面岩性
            double faceStrip = stripWanM3 * wasteShare;
            foreach (var (code, frac) in wasteShares)
            {
                double vol = faceStrip * frac;
                if (vol > 1e-9) list.Add(NewSupply(f, code, vol));
            }
        }
        return list;
    }

    /// <summary>该作业面是否出煤（有混采构成看构成，否则看面的物料码）。</summary>
    private static bool FaceHasOre(WorkingFace f)
    {
        var mix = PlanMaterialMix.Parse(f.MaterialMixText);
        return mix.IsEmpty ? PlanMaterialCatalog.Resolve(f.MaterialCode).IsOre : mix.OreShares().Count > 0;
    }

    private static PlanSupply NewSupply(WorkingFace f, string code, double inSitu) => new()
    {
        SourceName = f.Name,
        SourceRefId = f.SourceRefId,
        MaterialCode = code,
        InSituWanM3 = inSitu,
        PinnedDestinationId = f.DestinationId,
        PinnedForMaterialCode = f.MaterialCode,
        PinnedHaulKm = f.HaulDistanceKm,
        PinnedEquivHaulKm = f.EquivHaulKm,
    };

    /// <summary>
    /// 排一个月：拆供给 → 分配去向 → 扣库容，返回该月物料流。
    /// </summary>
    public static List<PlanFlow> BuildMonthFlows(ShortTermPlan p, double coalWanT, double stripWanM3,
                                                 PlanDumpLedger ledger, int year, int month, string periodLabel,
                                                 IList<string>? warnings = null)
        => Assign(BuildSupplies(p, coalWanT, stripWanM3), ledger, year, month, periodLabel, warnings);

    /// <summary>
    /// 对已有的一批流按运输功最小重新分配（面板上的「重新分配」按钮）：
    /// 保留源/物料/量，忽略面上钉死的去向，全部重排。传入的 ledger 应是**本月期初**状态。
    /// </summary>
    public static List<PlanFlow> Reassign(IEnumerable<PlanFlow> flows, PlanDumpLedger ledger,
                                          int year, int month, string periodLabel, IList<string>? warnings = null)
    {
        // 同一「作业面·物料」若已被拆到多个去向，先合回一份供给再整体重排，否则会按碎片逐份贪心。
        var supplies = flows.Where(z => z.InSituWanM3 > 1e-9)
            .GroupBy(z => (z.SourceName, z.SourceRefId, z.MaterialCode))
            .Select(g => new PlanSupply
            {
                SourceName = g.Key.SourceName, SourceRefId = g.Key.SourceRefId,
                MaterialCode = g.Key.MaterialCode, InSituWanM3 = g.Sum(z => z.InSituWanM3),
            }).ToList();
        return Assign(supplies, ledger, year, month, periodLabel, warnings, ignorePinned: true);
    }
}
