// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/ProductionFact.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;

namespace PitMine3D.Kylin.TaskLib.Reporting;

// ─────────────────────────────────────────────────────────────────────────────
//  取数层 —— 一条「生产事实」= 某天某班、某设备、在某作业面某工序、对**一种物料**、
//  运往**一个去向**的一笔计划与实绩。所有报表都从这份宽表按维度聚合出来。
//
//  本层的三条铁律（对应本轮修掉的三处编造数据）：
//   ① 物料一律走物料本体 <see cref="MaterialSpec"/>：一条煤岩混采任务按 Mix 份额**拆成多条事实**，
//      煤条与岩条各自带自己的密度/膨胀系数/去向。绝不再用「工序==采装 ⇒ 煤」这种猜法——
//      那会把采装的岩石统统算成煤，coal_vol / waste_vol / strip_ratio 三个核心指标同时失真。
//   ② 严禁写死密度：吨量 = Spec.ToTonnage(实方)，松方 = ×Ks，占容方 = ×Kr，一个常量都不许出现在本文件。
//   ③ 运距只认任务上的真运距（<see cref="ProductionTask.EffectiveHaulKm"/>）。任务没给运距时
//      <see cref="ProductionFact.HaulKnown"/> = false，运距/运输功/单位油耗一律按"不可用"处理，
//      指标层据此返回空值让报表显示"—"。**宁可显示"—"也不显示编造的数**。
//
//  口径守则（露天矿）：储量/剥采比用实方；卡车配车用松方(×Ks)；排土库容用占容方(×Kr)；
//  吨量是三者间唯一守恒的中间量。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 一条生产事实（宽表行）。粒度 = 任务 × 物料（混采任务拆条），量的基准口径一律【实方 m³】。
/// P1 数据源 = <see cref="SampleTaskBoard"/>（引擎裂解装箱 + 实绩回灌）；真实接 GeoDataBase/ShortTerm 时只换 <see cref="FactSource"/>。
/// 设计见 docs/生产报表_定制化引擎_设计.md（四层：取数→指标→模板→渲染）。
/// </summary>
public sealed class ProductionFact
{
    // ── 溯源（拆条后仍能还原到任务）──
    /// <summary>来源任务 Id。混采任务会有多条事实共用同一个 TaskId。</summary>
    public string TaskId { get; set; } = "";
    /// <summary>本条在原任务中的物料份额 0..1（按实方体积占比）。非拆条事实为 1。</summary>
    public double MaterialFraction { get; set; } = 1;
    /// <summary>是否该任务的主物料条（份额最大者）。任务级计数指标用它去重。</summary>
    public bool IsPrimaryMaterial { get; set; } = true;

    // 时间维
    public DateTime Date { get; set; }
    public string Shift { get; set; } = "";

    // 空间维（源）
    public string Mine { get; set; } = "";
    public string Panel { get; set; } = "";        // 作业面/采区
    public double BenchElevationM { get; set; }
    /// <summary>
    /// 工程位置号（EP-xx）——事实与三维几何（面模型 / 块体模型）之间的连接键。
    /// 作业面名会改会合并，工程位置号不会；将来"这一方量对应图上哪一块"只能靠它对上。
    /// </summary>
    public string EngineeringPositionId { get; set; } = "";

    // 设备 / 工序
    public string Equipment { get; set; } = "";    // 主设备
    public string Process { get; set; } = "";      // 工序中文（穿孔/爆破/采装/运输/排土）
    public ProcessType ProcessKind { get; set; }   // 工序枚举（精确过滤用）

    /// <summary>
    /// 本条是否计入「采出量/剥离量/剥采比」。只有**采装(Load)**计——排土(Dump)是同一批料的
    /// 接收侧，再计一遍等于把剥离量翻倍；穿孔/爆破是准备工序，本身不产生采剥量。
    /// </summary>
    public bool CountsAsMined { get; set; }
    /// <summary>本条是否为排弃侧受排实绩（排土工序）。采装侧未接去向时，排弃量回落用它。</summary>
    public bool IsDumpReceipt { get; set; }
    /// <summary>
    /// 本条是否涉及**运输**（采装 / 运输工序）——决定要不要算运输功与运输变动油耗。
    /// 排土(Dump)不算：它是卸点侧的推排平整，运距已计在采装侧，再计一遍运输功就翻倍了。
    /// </summary>
    public bool MovesMaterial { get; set; }

    // 物料维
    public string Material { get; set; } = "";       // 原始物料描述（历史自由文本）
    public string MaterialCode { get; set; } = "";   // 结构化物料码（MaterialCatalog）；空=本条无物料（穿孔/检修）
    public string MaterialName { get; set; } = "—";  // 物料名（煤/硬岩/表土…）
    public string MaterialKind { get; set; } = "—";  // 物料大类中文（MaterialKind.Label()）
    /// <summary>是否计入采出量（煤/低质煤）。来自 <see cref="MaterialSpec.IsOre"/>，不再靠工序猜。</summary>
    public bool IsOre { get; set; }
    public bool HasMaterial { get; set; }
    /// <summary>本条换算所用的原位密度 t/m³（来自物料本体，写在事实上便于追溯口径）。</summary>
    public double DensityTPerM3 { get; set; }

    // 去向维（"排弃/送到哪"）
    public string DestinationId { get; set; } = "";
    public string DestinationName { get; set; } = "";
    public SinkKind DestinationKind { get; set; } = SinkKind.ExternalDump;
    /// <summary>去向类型中文（一律走 SinkKind.Label()）。无去向时为"—"。</summary>
    public string DestinationKindLabel { get; set; } = "—";
    /// <summary>是否已定去向。为 false 时 <see cref="DestinationKind"/> 无意义（枚举默认值是外排土场，会误判）。</summary>
    public bool HasDestination { get; set; }
    /// <summary>
    /// 本条物料自己的去向不接纳这种物料 —— 真·配错（表土进岩石排土场、煤进排土场）。
    /// 此时本条按「去向未定」处理：宁可缺一条去向，也不能编造「岩石运进破碎站」这种不存在的通道。
    /// <para>
    /// 去向一律取 <see cref="ProductionTask.DestinationFor"/>（分项优先、无分项才回落主去向），
    /// 所以混采任务的次要物料不会再因为"主去向不收它"而被误判成配错——那是结构缺陷，不是配错。
    /// </para>
    /// </summary>
    public bool DestinationRejectedForMaterial { get; set; }
    /// <summary>去向是否为排弃类（占排土库容）。</summary>
    public bool IsDumpingDestination { get; set; }

    // 去向的库容台账（同一去向的多条事实带同样的值，聚合时须按去向去重）
    public double SinkDesignCapacityM3 { get; set; }
    public double SinkRemainingM3 { get; set; }
    public double SinkFillRate { get; set; }

    // ── 量（基准口径：实方 m³）──
    public double PlanVolumeM3 { get; set; }
    public double ActualVolumeM3 { get; set; }
    public double ShortfallM3 { get; set; }
    public double PlanTonnage { get; set; }        // 计划吨 = 实方 × 物料密度
    public double ActualTonnage { get; set; }      // 实绩吨
    public double PlanLooseM3 { get; set; }        // 计划松方 = 实方 × Ks（配车/车厢校核）
    public double ActualLooseM3 { get; set; }      // 实绩松方
    public double PlanDumpM3 { get; set; }         // 计划占容方 = 实方 × Kr
    /// <summary>实绩排弃占容方 m³ —— 排土场库容按这个扣（本工作包契约字段名）。</summary>
    public double DumpVolumeM3 { get; set; }

    // 工时
    public double PlannedHours { get; set; }
    public double ActualHours { get; set; }

    // ── 运输（真运距；HaulKnown=false 时以下全部不可用）──
    /// <summary>任务上是否给了运距。false ⇒ 运距/运输功/单位油耗一律不可用，指标须按它过滤。</summary>
    public bool HaulKnown { get; set; }
    public double HaulDistanceKm { get; set; }     // 实际运距 km
    public double EquivHaulKm { get; set; }        // 等效运距 km（含坡度折算）
    public double EffectiveHaulKm { get; set; }    // 有效运距 = 等效优先，回落实距
    public double PlanTransportWorkTKm { get; set; }   // 计划运输功 t·km
    /// <summary>实绩运输功 t·km = 实绩吨 × 有效运距 —— 流向方案比选的核心成本代理。</summary>
    public double TransportWorkTKm { get; set; }
    /// <summary>兼容旧名（自定义指标字段 HaulTKm / 内置指标 haul_tkm 仍按此读）。</summary>
    public double HaulTKm => TransportWorkTKm;

    // ── 单耗（★ 经验系数，非实测；见 FactSource 的系数常量注释）──
    /// <summary>柴油耗 L = 工序固定项(L/m³) × 实方 + 运输变动项(L/t·km) × 运输功。</summary>
    public double FuelL { get; set; }
    /// <summary>电耗 kWh（电铲）。</summary>
    public double PowerKwh { get; set; }
    /// <summary>
    /// 油耗口径是否完整：搬运物料却没有运距时，变动项算不出来，<see cref="FuelL"/> 系偏低值。
    /// 单位油耗指标遇到不完整口径一律返回空值。
    /// </summary>
    public bool FuelBasisComplete => !MovesMaterial || HaulKnown;

    // 状态 / 归因
    public string Status { get; set; } = "";
    public string TopReason { get; set; } = "";

    // 煤质（按量加权聚合用；只挂在矿/煤条上）
    public bool HasQuality { get; set; }
    public double Ash { get; set; }        // 灰分 %
    public double Calorific { get; set; }  // 发热量 MJ/kg
    public double Sulfur { get; set; }     // 硫分 %
    public double Moisture { get; set; }   // 水分 %

    // ── 口径谓词（一律基于物料本体，不再基于工序或中文串）──
    public bool IsCoal => IsOre;
    public bool IsWaste => HasMaterial && !IsOre;
    public bool IsTopsoil => string.Equals(MaterialCode, MaterialCatalog.Topsoil, StringComparison.OrdinalIgnoreCase);

    /// <summary>去向维分组键（无去向时归到"未指定去向"，不伪造成外排土场）。</summary>
    public string DestinationKey => !HasDestination
        ? "未指定去向"
        : (string.IsNullOrWhiteSpace(DestinationName) ? DestinationId : DestinationName);

    /// <summary>物料维分组键。</summary>
    public string MaterialKey => HasMaterial ? MaterialName : "无物料";
}

/// <summary>
/// 取数适配器：把上游数据源投影成 <see cref="ProductionFact"/> 宽表。
/// 多源与按日期区间取数见 <c>FactSource.Range.cs</c>（同一个类的另一半）。
/// </summary>
public static partial class FactSource
{
    // ─────────────────────────────────────────────────────────────────────────
    //  单耗经验系数 —— ★ 全部为工程经验值，非实测。接油料/电量台账后由实测值替换。
    //
    //  为什么要拆成「固定项 + 运距变动项」：运输油耗随运距近似线性增长，
    //  用 L/m³ 做基数会把 1km 与 5km 的两趟车算成一样多；t·km 才是运输油耗的正确基数
    //  （载重×里程，与轮胎/折旧/油耗同量纲）。原实现按工序单一查表，运距一变就失真。
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>采装固定油耗 L/m³实方（铲装、就位、怠速）。★经验系数。</summary>
    private const double FuelLoadLPerM3 = 0.32;
    /// <summary>运输固定油耗 L/m³实方（装车等待、卸载、调头等与里程无关的部分）。★经验系数。
    /// 原实现的 0.25 是"含运距的全程系数"，此处只保留固定部分，随里程增长的部分改由 t·km 项承担。</summary>
    private const double FuelHaulFixedLPerM3 = 0.10;
    /// <summary>排土固定油耗 L/m³实方（推土机推排、平整）。★经验系数。</summary>
    private const double FuelDumpLPerM3 = 0.30;
    /// <summary>穿孔油耗 L/m³实方。★经验系数。</summary>
    private const double FuelDrillLPerM3 = 0.20;
    /// <summary>运输变动油耗 L/(t·km) —— 运距修正项。露天矿卡车常见区间 0.04~0.10。★经验系数。</summary>
    private const double FuelHaulLPerTKm = 0.06;
    /// <summary>电铲电耗 kWh/m³实方。★经验系数。</summary>
    private const double PowerShovelKwhPerM3 = 0.45;

    // 「当日盘子取数」已并入多源取数层：见 FactSource.Range.cs 的 FromCurrentBoard()。

    /// <summary>
    /// 把任务列表投影成事实宽表。**混采任务按物料份额拆成多条**——煤与岩的密度、膨胀系数、
    /// 去向、指标口径全都不同，压成一条就必然有一头是错的。
    /// </summary>
    public static List<ProductionFact> FromTasks(IEnumerable<ProductionTask> tasks, DateTime date, string mine)
    {
        var list = new List<ProductionFact>();
        var sinks = TryLoadSinks();

        foreach (var t in tasks ?? Enumerable.Empty<ProductionTask>())
        {
            if (t == null || t.Process == ProcessType.Idle) continue;

            // 「需要运输」只含采装与运输两个工序。排土是卸点侧的推排平整，运距早已计在采装侧——
            // 而 HaulResolver 会给**所有**作业面（含排土面）填上兜底运距，若把排土也算成运输，
            // 同一批料的运输功会被记两遍（一次「采装面→内排场」，一次「内排场→内排场」）。
            bool movesMaterial = t.Process is ProcessType.Load or ProcessType.Haul;

            foreach (var part in SplitMaterials(t))
            {
                var spec = part.Spec;

                // ── 混采任务的去向归属：取**本条物料自己的去向** ──
                //  ProductionTask.DestinationFor 优先给分项（Splits），无分项才回落主去向。
                //  此前一律读主去向，混采任务的岩条就会撞上「破碎站不接纳硬岩」→ 被记成"未指定去向"，
                //  haul_coverage_pct 卡在 83%、fuel_per_m3 显示"—"、交叉表里岩落不到排土场那一列。
                //  现在每条流各认各的汇，被拒才是真的配错了。
                var d = spec == null ? null : t.DestinationFor(spec.Code);
                var sink = ResolveSink(sinks, t, d);

                bool partHasDest = (d?.HasDestination ?? t.HasDestination) || sink != null;
                var partDestKind = sink?.Kind ?? d?.DestinationKind ?? t.DestinationKind;
                string partDestId = sink?.Id ?? d?.DestinationId ?? t.DestinationId;
                string partDestName = sink?.Name ?? d?.DestinationName ?? t.DestinationName;

                // 运距：只认本条自己的真运距（混采时煤走 2.6km、岩走 1.4km 是两条腿；
                // 无分项时 DestinationFor 已回落主去向的运距）。为 0 = 不可用，不编造、不回落兜底值。
                double effKm = d?.EffectiveHaulKm ?? t.EffectiveHaulKm;

                // 去向真的不接纳本条物料时才置「配错」——保留这个标记，但它现在是实打实的错配，
                // 不再是"混采任务只装得下一个去向"的结构性副作用。
                bool destAccepts = spec == null || (sink != null ? sink.Accepts(spec) : spec.Accepts(partDestKind));
                bool rejected = partHasDest && !destAccepts;
                bool hasDest = partHasDest && destAccepts;
                var destKind = hasDest ? partDestKind : SinkKind.ExternalDump;
                string destId = hasDest ? partDestId : "";
                string destName = hasDest ? partDestName : "";

                // 去向被否 ⇒ 那条运距是别的物料的路线，对本条无效。
                bool haulKnown = movesMaterial && effKm > 1e-6 && !rejected;
                // 未完成原因：Reasons 为空时不能直接 FirstOrDefault ——枚举默认值是 Fault，
                // 会把"根本没有异常"的任务统统标成「设备故障」，归因分析随即全错。
                IncompleteReason? reason = t.Reasons
                    .Where(r => r != IncompleteReason.OverAchieved)
                    .Select(r => (IncompleteReason?)r)
                    .FirstOrDefault();

                var f = new ProductionFact
                {
                    TaskId = t.Id,
                    MaterialFraction = part.Fraction,
                    IsPrimaryMaterial = part.IsPrimary,

                    Date = date,
                    Shift = t.Shift,
                    Mine = mine,
                    Panel = t.WorkZone,
                    BenchElevationM = t.BenchElevationM,
                    EngineeringPositionId = t.EngineeringPositionId ?? "",

                    Equipment = t.Group.MainEquipment,
                    Process = t.Process.Label(),
                    ProcessKind = t.Process,
                    CountsAsMined = t.Process == ProcessType.Load,
                    IsDumpReceipt = t.Process == ProcessType.Dump,
                    MovesMaterial = movesMaterial,

                    Material = t.Material,
                    MaterialCode = spec?.Code ?? "",
                    MaterialName = spec?.Name ?? "—",
                    MaterialKind = spec == null ? "—" : spec.Kind.Label(),
                    IsOre = spec?.IsOre ?? false,
                    HasMaterial = spec != null,
                    DensityTPerM3 = spec?.InSituDensityTPerM3 ?? 0,

                    DestinationId = destId,
                    DestinationName = destName,
                    DestinationKind = destKind,
                    DestinationKindLabel = hasDest ? destKind.Label() : "—",
                    HasDestination = hasDest,
                    DestinationRejectedForMaterial = rejected,
                    IsDumpingDestination = hasDest && destKind.IsDumping(),
                    SinkDesignCapacityM3 = sink?.DesignCapacityM3 ?? 0,
                    SinkRemainingM3 = sink is { IsCapacityLimited: true } ? sink.RemainingM3 : 0,
                    SinkFillRate = sink?.FillRate ?? 0,

                    PlannedHours = t.PlannedHours * part.Fraction,
                    ActualHours = t.ActualHours * part.Fraction,

                    Status = StatusLabel(t.Status),
                    TopReason = reason?.Label() ?? "",

                    HaulKnown = haulKnown,
                    HaulDistanceKm = haulKnown ? (d?.HaulKm ?? t.HaulDistanceKm) : 0,
                    EquivHaulKm = haulKnown ? (d?.EquivHaulKm ?? t.EquivHaulKm) : 0,
                    EffectiveHaulKm = haulKnown ? effKm : 0,
                };

                // ── 量：一律经物料本体换算，本文件不出现任何密度/膨胀系数常量 ──
                double planM3 = t.TargetVolumeM3 * part.Fraction;
                double actualM3 = t.ActualVolumeM3 * part.Fraction;

                // 口径校正：排土面的目标量本就是【排弃占容方】（见 TaskExploder.DeriveDumpTargets），
                // 而本宽表的量基准是【实方】。不先折回实方就直接乘密度，吨量会虚高 Kr 倍（岩约 +15%）。
                if (t.Process == ProcessType.Dump && spec != null && spec.ResidualSwellFactor > 1e-6)
                {
                    planM3 /= spec.ResidualSwellFactor;
                    actualM3 /= spec.ResidualSwellFactor;
                }

                f.PlanVolumeM3 = planM3;
                f.ActualVolumeM3 = actualM3;
                f.ShortfallM3 = Math.Max(0, planM3 - actualM3);
                f.PlanTonnage = spec?.ToTonnage(planM3) ?? 0;
                f.ActualTonnage = spec?.ToTonnage(actualM3) ?? 0;
                f.PlanLooseM3 = spec?.ToLooseM3(planM3) ?? 0;
                f.ActualLooseM3 = spec?.ToLooseM3(actualM3) ?? 0;
                f.PlanDumpM3 = spec?.ToDumpM3(planM3) ?? 0;
                f.DumpVolumeM3 = spec?.ToDumpM3(actualM3) ?? 0;

                f.PlanTransportWorkTKm = haulKnown ? f.PlanTonnage * effKm : 0;
                f.TransportWorkTKm = haulKnown ? f.ActualTonnage * effKm : 0;

                f.FuelL = FuelOf(t.Process, actualM3, f.TransportWorkTKm);
                f.PowerKwh = t.Process == ProcessType.Load ? actualM3 * PowerShovelKwhPerM3 : 0;

                // 煤质只挂矿/煤条：把灰分/热值挂到岩条上，按量加权时会被岩量稀释成假数。
                var q = t.QualityActual ?? t.QualityTarget;
                f.HasQuality = q != null && f.IsOre;
                f.Ash = f.HasQuality ? q!.AshPct : 0;
                f.Calorific = f.HasQuality ? q!.CalorificMJkg : 0;
                f.Sulfur = f.HasQuality ? q!.SulfurPct : 0;
                f.Moisture = f.HasQuality ? q!.MoisturePct : 0;

                list.Add(f);
            }
        }
        return list;
    }

    /// <summary>柴油耗 L = 工序固定项 × 实方 + 运输变动项 × 运输功（运距未知时变动项为 0，值偏低）。</summary>
    private static double FuelOf(ProcessType p, double inSituM3, double transportWorkTKm)
    {
        double fixedPart = p switch
        {
            ProcessType.Load => FuelLoadLPerM3,
            ProcessType.Haul => FuelHaulFixedLPerM3,
            ProcessType.Dump => FuelDumpLPerM3,
            ProcessType.Drill => FuelDrillLPerM3,
            _ => 0.0,
        };
        return inSituM3 * fixedPart + transportWorkTKm * FuelHaulLPerTKm;
    }

    /// <summary>一条事实对应的物料份额。Spec=null 表示本工序无物料（穿孔/爆破）。</summary>
    private readonly record struct MaterialPart(MaterialSpec? Spec, double Fraction, bool IsPrimary);

    /// <summary>
    /// 把任务拆成「一物料一条」。只有搬运物料的工序才拆——穿孔/爆破的 Material 存的是待爆区名，
    /// 拿去解析会被兜底成硬岩，凭空造出一条剥离事实。
    /// </summary>
    private static List<MaterialPart> SplitMaterials(ProductionTask t)
    {
        var parts = new List<MaterialPart>();
        if (t.Process is not (ProcessType.Load or ProcessType.Haul or ProcessType.Dump))
        {
            parts.Add(new MaterialPart(null, 1, true));
            return parts;
        }

        var mix = t.ResolvedMix.Normalized();
        var shares = mix.Shares.Where(s => s.Fraction > 1e-6).OrderByDescending(s => s.Fraction).ToList();
        if (shares.Count == 0)
        {
            parts.Add(new MaterialPart(null, 1, true));
            return parts;
        }

        for (int i = 0; i < shares.Count; i++)
            parts.Add(new MaterialPart(MaterialCatalog.Resolve(shares[i].MaterialCode), shares[i].Fraction, i == 0));
        return parts;
    }

    /// <summary>
    /// 去向登记簿（排土场/破碎站/煤仓台账）。读不到一律返回 null，取数继续跑——
    /// 报表可以没有去向维，但不能因为台账没接通就崩。
    /// </summary>
    private static SinkRegistry? TryLoadSinks()
    {
        try
        {
            var reg = SinkRegistryLoader.Current;
            return reg != null && reg.All.Count > 0 ? reg : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 任务（某物料分项）→ 去向登记簿里的汇。线索依次为：本条分项的去向 Id/名 → 任务主去向 Id/名
    /// → （仅排土工序）作业区名——排土作业的"作业地点"本身就是排土场，历史盘子里排土面
    /// 只有 Zone="内排场" 这一个线索。对不上返回 null（此时只用原样填的去向，绝不猜类型）。
    /// </summary>
    private static SinkNode? ResolveSink(SinkRegistry? reg, ProductionTask t, MaterialDestination? d = null)
    {
        if (reg == null) return null;

        // 分项给出了去向就只认它：混采任务的岩条不能回落到主去向（破碎站）上去找汇。
        var hints = d is { HasDestination: true }
            ? new[] { d.DestinationId, d.DestinationName }
            : t.Process == ProcessType.Dump
                ? new[] { t.DestinationId, t.DestinationName, t.WorkZone }
                : new[] { t.DestinationId, t.DestinationName };

        foreach (var h in hints)
        {
            if (string.IsNullOrWhiteSpace(h)) continue;
            var byId = reg.Find(h.Trim());
            if (byId != null) return byId;
            var byName = reg.All.FirstOrDefault(s => string.Equals(s.Name, h.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;
        }
        return null;
    }

    private static string StatusLabel(TaskStatus s) => s switch
    {
        TaskStatus.Planned => "计划",
        TaskStatus.Dispatched => "已下达",
        TaskStatus.Running => "执行中",
        TaskStatus.Done => "完成",
        TaskStatus.Partial => "部分完成",
        TaskStatus.Failed => "未完成",
        _ => "—",
    };

    // 日期标签解析（ParseDate）在 FactSource.Range.cs —— 与区间取数同处，口径唯一。
}
