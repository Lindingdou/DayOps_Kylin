using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>
/// 月计划的一条「物料流」（一条 O-D：从哪个作业面、什么物料、多少方、排/送到哪、运多远）。
///
/// 它是月计划行的**唯一事实来源** —— 月采出量、月剥离量、排弃占容、内排率、运输功全部由一堆 PlanFlow 聚合出来，
/// 不再由各处各写一个标量。一行月计划只给「煤量 + 剥离量 + 内/外排二值」是答不了
/// 「从哪采剥、排弃到哪、排不排得下」的。
///
/// 三个方量口径严格区分（换算一律走 <see cref="PlanMaterialCatalog"/>，此处不写死任何系数）：
///   · <see cref="InSituWanM3"/>  原位实方 —— 储量/剥采比口径，是本条流的**存量本体**
///   · <see cref="LooseWanM3"/>   运输松方 = V实×Ks —— 配车口径
///   · <see cref="DumpWanM3"/>    排弃占容 = V实×Kr —— 排土场扣库容口径
/// </summary>
public sealed class PlanFlow
{
    // ── 源 ──
    /// <summary>源作业面名（与 <see cref="WorkingFace.Name"/> 对应）。</summary>
    public string SourceName { get; set; } = "";
    /// <summary>源的空间身份：mineable_region.id 或工程位置 groupKey（把被截断的空间身份接回来）。</summary>
    public string SourceRefId { get; set; } = "";

    // ── 物料 ──
    public string MaterialCode { get; set; } = PlanMaterialCatalog.Rock;
    /// <summary>本条流的原位实方量（万m³）—— 编辑分配量时改的就是它。</summary>
    public double InSituWanM3 { get; set; }

    // ── 汇（去向）──
    public string DestinationId { get; set; } = "";
    public string DestinationName { get; set; } = "";
    /// <summary>去向类型（内排土场/外排土场/破碎站/原煤仓/储煤场/表土堆场）。</summary>
    public PlanSinkKind DestinationKind { get; set; } = PlanSinkKind.ExternalDump;

    // ── 运距 ──
    /// <summary>实际运距 km。</summary>
    public double HaulKm { get; set; }
    /// <summary>等效运距 km（含坡度/路况折算）；0 表示未给，运输功退回按实际运距算。</summary>
    public double EquivHaulKm { get; set; }

    // ── 派生 ──
    public PlanMaterialSpec Spec => PlanMaterialCatalog.Resolve(MaterialCode);
    public string MaterialName => Spec.Name;
    /// <summary>是否计入采出量（煤/低质煤）；false 者计入剥离量。</summary>
    public bool IsOre => Spec.IsOre;

    public bool HasDestination => DestinationId.Length > 0 || DestinationName.Length > 0;
    /// <summary>去向类型文案（无去向时为空，不假装是外排）。</summary>
    public string DestinationKindText => HasDestination ? DestinationKind.Label() : "";
    /// <summary>是否落到排弃类去向（占排土库容）。</summary>
    public bool IsDumping => HasDestination && DestinationKind.IsDumping();
    /// <summary>是否内排（降本核心指标 内排率 的分子）。</summary>
    public bool IsInternalDump => HasDestination && DestinationKind == PlanSinkKind.InternalDump;

    /// <summary>吨量（万t）—— 三个体积口径之间唯一守恒的中间量。</summary>
    public double TonnageWanT => Spec.ToTonnage(InSituWanM3);
    /// <summary>运输松方（万m³）—— 卡车载重/配车口径。</summary>
    public double LooseWanM3 => Spec.ToLoose(InSituWanM3);
    /// <summary>排弃占容方（万m³）—— 排土场剩余库容按它扣（Kr 口径，不是 Ks）。</summary>
    public double DumpWanM3 => Spec.ToDump(InSituWanM3);

    /// <summary>参与运输功计算的运距：给了等效运距用等效，否则用实际。</summary>
    public double EffectiveHaulKm => EquivHaulKm > 1e-9 ? EquivHaulKm : HaulKm;
    /// <summary>运输功（万t·km）= 吨量 × 等效运距 —— 流向分配的目标函数。</summary>
    public double TransportWorkWanTKm => TonnageWanT * EffectiveHaulKm;

    public string DestinationCaption => HasDestination
        ? $"{DestinationName}（{DestinationKindText}）"
        : "（未指定去向）";

    public string Caption => $"{SourceName} · {MaterialName} {InSituWanM3:0.#}万m³ → {DestinationCaption} {HaulKm:0.#}km";

    public PlanFlow Copy() => (PlanFlow)MemberwiseClone();
}
