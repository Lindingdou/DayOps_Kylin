// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/IndicatorDef.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Reporting;

/// <summary>指标评价灯（红黄绿）。None = 无目标/不评价。</summary>
public enum ReportStatus { None, Ok, Warn, Bad }

/// <summary>
/// 指标层 —— 一个「指标定义」= 计算规则。报表模板的单元格按 <see cref="Id"/> 引用它。
/// P1：内置指标用 <see cref="Evaluate"/> 委托在事实宽表上聚合（采出量/剥采比/达成率/运输功/煤质…）；
/// 自定义公式（表达式引擎）见 <see cref="CustomIndicatorDef"/>。带 <see cref="Target"/> 的指标可出红黄绿评价。
///
/// 空值约定：算不出来（口径缺数据、分母为 0、运距未知）一律返回 <see cref="double.NaN"/>，
/// 渲染成"—"。**报表宁可显示"—"也不能显示编造的数。**
/// </summary>
public sealed class IndicatorDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Category { get; init; } = "";
    public string Format { get; init; } = "0";   // 默认数字格式

    /// <summary>聚合方式（人读：求和 / 比值 / 吨量加权平均 / 计数 …）。供指标库列表与文档展示。</summary>
    public string Agg { get; init; } = "求和";

    /// <summary>这个指标用来回答什么问题（一句话）。指标库/模板设计器里显示，避免"有指标不知道干嘛用"。</summary>
    public string Question { get; init; } = "";

    /// <summary>在给定事实集合上算出指标值（这就是"计算规则"）。</summary>
    public Func<IReadOnlyList<ProductionFact>, double> Evaluate { get; init; } = _ => 0;

    // ── 评价阈值（可选）──
    public double? Target { get; init; }
    public bool HigherIsBetter { get; init; } = true;
    /// <summary>黄灯带宽（偏离目标不超过此值算"接近达标"→黄；再差→红）。</summary>
    public double WarnBand { get; init; } = 10;

    /// <summary>按目标+方向判红黄绿。无目标 / 值为空 → None。</summary>
    public ReportStatus StatusOf(double value)
    {
        if (double.IsNaN(value)) return ReportStatus.None;
        if (Target is not double t) return ReportStatus.None;
        if (HigherIsBetter)
        {
            if (value >= t) return ReportStatus.Ok;
            if (value >= t - WarnBand) return ReportStatus.Warn;
            return ReportStatus.Bad;
        }
        else
        {
            if (value <= t) return ReportStatus.Ok;
            if (value <= t + WarnBand) return ReportStatus.Warn;
            return ReportStatus.Bad;
        }
    }

    public string FormatValue(double v) => double.IsNaN(v) || double.IsInfinity(v) ? "—" : v.ToString(Format);

    /// <summary>指标库/下拉里显示的一行说明。</summary>
    public string Caption => $"{Name}（{Unit}）· {Agg}" + (Question.Length > 0 ? $" · {Question}" : "");
}

/// <summary>
/// 指标注册表：内置一套露天煤矿标准指标（按 Id 引用、可复用），并对外提供解析/罗列。
/// 用户自定义公式在 <see cref="IndicatorLibrary"/> 接入（<see cref="Register"/> 已预留）。
/// </summary>
public sealed class IndicatorRegistry
{
    private readonly Dictionary<string, IndicatorDef> _map = new(StringComparer.Ordinal);

    public void Register(IndicatorDef d) => _map[d.Id] = d;
    public bool TryResolve(string id, out IndicatorDef def) => _map.TryGetValue(id ?? "", out def!);
    public IndicatorDef? Resolve(string id) => _map.TryGetValue(id ?? "", out var d) ? d : null;
    public IReadOnlyList<IndicatorDef> All() => _map.Values.ToList();

    // ── 车次估算参数 ────────────────────────────────────────────────────────
    /// <summary>★ 名义单车载重 t（大型矿用自卸卡车）。车次仅为**估算**，列名/说明里必须写明口径。
    /// 接设备台账的实配车型载重后由实测替换。</summary>
    private const double NominalTruckPayloadT = 90;

    // ── 聚合工具 ──────────────────────────────────────────
    private static double Sum(IReadOnlyList<ProductionFact> f, Func<ProductionFact, double> s) => f.Sum(s);
    private static double SafeRatio(double num, double den) => den > 1e-9 ? num / den : double.NaN;

    /// <summary>选集为空 → NaN（"—"）；否则求和。用于"没有可用数据"与"合计为 0"的区分。</summary>
    private static double SumOrNaN(IEnumerable<ProductionFact> sel, Func<ProductionFact, double> s)
    {
        double total = 0; bool any = false;
        foreach (var x in sel) { total += s(x); any = true; }
        return any ? total : double.NaN;
    }

    private static double WeightedAvg(IEnumerable<ProductionFact> f, Func<ProductionFact, double> v, Func<ProductionFact, double> w)
    {
        double sw = 0, svw = 0;
        foreach (var x in f) { double ww = w(x); sw += ww; svw += v(x) * ww; }
        return sw > 1e-9 ? svw / sw : double.NaN;
    }

    // ── 口径工具（露天矿采排物流）─────────────────────────────────────────

    /// <summary>采装侧事实（唯一计入采出/剥离的一侧；排土是同批料的接收侧，再计一遍会翻倍）。</summary>
    private static IEnumerable<ProductionFact> Mined(IReadOnlyList<ProductionFact> f) => f.Where(x => x.CountsAsMined);

    /// <summary>有真运距的搬运事实（运距/运输功类指标的唯一合法样本）。</summary>
    private static IEnumerable<ProductionFact> Hauled(IReadOnlyList<ProductionFact> f) => f.Where(x => x.HaulKnown);

    /// <summary>搬运物料且有工作量的事实（判"运距口径全不全"用）。</summary>
    private static IEnumerable<ProductionFact> Moving(IReadOnlyList<ProductionFact> f)
        => f.Where(x => x.MovesMaterial && x.ActualVolumeM3 > 1e-6);

    /// <summary>运距口径是否完整（所有搬运事实都有运距）。不完整时比值型运距指标返回空值。</summary>
    private static bool HaulComplete(IReadOnlyList<ProductionFact> f) => Moving(f).All(x => x.HaulKnown);

    /// <summary>
    /// 排弃占容方的计量基准。以**采装侧（源侧）**为准：源侧知道实方→占容方的准确换算（×Kr），
    /// 与 TaskExploder 的采排守恒同源。采装侧一条去向都没接线时回落**排土侧受排实绩**，
    /// 免得在"去向尚未接线"的过渡期整张物流报表空白。两侧不叠加，杜绝重复计量。
    /// </summary>
    private static List<ProductionFact> DumpBasis(IReadOnlyList<ProductionFact> f)
    {
        var src = f.Where(x => x.CountsAsMined && x.IsDumpingDestination).ToList();
        if (src.Sum(x => x.DumpVolumeM3) > 1e-6) return src;
        return f.Where(x => x.IsDumpReceipt).ToList();
    }

    /// <summary>本期排弃占容合计 m³（空 → NaN）。</summary>
    private static double DumpM3(IReadOnlyList<ProductionFact> f)
    {
        var basis = DumpBasis(f);
        return basis.Count == 0 ? double.NaN : basis.Sum(x => x.DumpVolumeM3);
    }

    /// <summary>各去向的期初剩余库容合计 m³（按去向去重——同一排土场的多条事实带的是同一个库容）。</summary>
    private static double SinkRemainingM3(IReadOnlyList<ProductionFact> f)
        => f.Where(x => x.IsDumpingDestination && x.SinkRemainingM3 > 1e-6)
            .GroupBy(x => x.DestinationKey, StringComparer.OrdinalIgnoreCase)
            .Sum(g => g.First().SinkRemainingM3);

    /// <summary>构建内置指标注册表（7 大类）。</summary>
    public static IndicatorRegistry BuiltIn()
    {
        var r = new IndicatorRegistry();

        // ─────────────────────────────────────────────────────────────────────
        //  1 采剥（实方 m³）—— 采出/剥离一律按**物料本体 IsOre** 划分，
        //  不再用"工序==采装 ⇒ 煤"这种猜法（那会把采装的岩石全算成煤）。
        //  且只统计**采装侧**：排土是同一批料的接收侧，计进来剥离量直接翻倍。
        // ─────────────────────────────────────────────────────────────────────
        r.Register(new IndicatorDef { Id = "plan_vol", Name = "计划量", Unit = "m³", Category = "1采剥", Format = "N0",
            Agg = "求和", Question = "本期计划的作业总工作量（含各工序）是多少？",
            Evaluate = f => Sum(f, x => x.PlanVolumeM3) });
        r.Register(new IndicatorDef { Id = "actual_vol", Name = "实绩量", Unit = "m³", Category = "1采剥", Format = "N0",
            Agg = "求和", Question = "本期实际完成的作业总工作量是多少？",
            Evaluate = f => Sum(f, x => x.ActualVolumeM3) });
        r.Register(new IndicatorDef { Id = "coal_vol", Name = "采出量(实方)", Unit = "m³", Category = "1采剥", Format = "N0",
            Agg = "求和（采装侧·矿物料）", Question = "本期采出了多少方煤/矿（实方）？",
            Evaluate = f => Mined(f).Where(x => x.IsOre).Sum(x => x.ActualVolumeM3) });
        r.Register(new IndicatorDef { Id = "waste_vol", Name = "剥离量(实方)", Unit = "m³", Category = "1采剥", Format = "N0",
            Agg = "求和（采装侧·非矿物料）", Question = "本期剥了多少方岩土（实方）？",
            Evaluate = f => Mined(f).Where(x => x.IsWaste).Sum(x => x.ActualVolumeM3) });
        r.Register(new IndicatorDef { Id = "strip_ratio", Name = "剥采比(实方比)", Unit = "m³/m³", Category = "1采剥", Format = "0.00",
            Agg = "比值 = 剥离实方 ÷ 采出实方", Question = "每采一方煤要剥几方岩（体积口径，用于与设计体积比对照）？",
            Evaluate = f => SafeRatio(Mined(f).Where(x => x.IsWaste).Sum(x => x.ActualVolumeM3),
                                      Mined(f).Where(x => x.IsOre).Sum(x => x.ActualVolumeM3)) });
        r.Register(new IndicatorDef { Id = "loose_vol", Name = "运输松方", Unit = "m³", Category = "1采剥", Format = "N0",
            Agg = "求和（×Ks）", Question = "要拉走的松散体积是多少（配车/车厢容积校核口径）？",
            Evaluate = f => Mined(f).Sum(x => x.ActualLooseM3) });

        // ── 2 产量（吨 t）──
        r.Register(new IndicatorDef { Id = "plan_t", Name = "计划量(吨)", Unit = "t", Category = "2产量", Format = "N0",
            Agg = "求和", Question = "本期计划工作量折成吨是多少？",
            Evaluate = f => Sum(f, x => x.PlanTonnage) });
        r.Register(new IndicatorDef { Id = "actual_t", Name = "实绩量(吨)", Unit = "t", Category = "2产量", Format = "N0",
            Agg = "求和", Question = "本期实际完成折成吨是多少？",
            Evaluate = f => Sum(f, x => x.ActualTonnage) });
        r.Register(new IndicatorDef { Id = "coal_t", Name = "原煤产量", Unit = "t", Category = "2产量", Format = "N0",
            Agg = "求和（采装侧·矿物料）", Question = "本期采出多少吨原煤——考核与销售的第一口径？",
            Evaluate = f => Mined(f).Where(x => x.IsOre).Sum(x => x.ActualTonnage) });
        r.Register(new IndicatorDef { Id = "waste_t", Name = "剥离量(吨)", Unit = "t", Category = "2产量", Format = "N0",
            Agg = "求和（采装侧·非矿物料）", Question = "本期剥离了多少吨岩土（运输能力核算口径）？",
            Evaluate = f => Mined(f).Where(x => x.IsWaste).Sum(x => x.ActualTonnage) });
        r.Register(new IndicatorDef { Id = "strip_ratio_t", Name = "剥采比(m³/t)", Unit = "m³/t", Category = "2产量", Format = "0.00",
            Agg = "比值 = 剥离实方 ÷ 采出吨量",
            Question = "露天煤矿标准剥采比口径：每采出 1 吨煤要剥离几立方米（实方）岩土？",
            Evaluate = f => SafeRatio(Mined(f).Where(x => x.IsWaste).Sum(x => x.ActualVolumeM3),
                                      Mined(f).Where(x => x.IsOre).Sum(x => x.ActualTonnage)) });

        // ── 3 计划达成 ──
        r.Register(new IndicatorDef { Id = "attain", Name = "达成率", Unit = "%", Category = "3达成", Format = "0.0",
            Target = 100, HigherIsBetter = true, WarnBand = 10, Agg = "比值 = 实绩 ÷ 计划",
            Question = "本期计划完成得怎么样？",
            Evaluate = f => SafeRatio(Sum(f, x => x.ActualVolumeM3), Sum(f, x => x.PlanVolumeM3)) * 100 });
        r.Register(new IndicatorDef { Id = "attain_t", Name = "达成率(吨)", Unit = "%", Category = "3达成", Format = "0.0",
            Target = 100, HigherIsBetter = true, WarnBand = 10, Agg = "比值（吨口径）",
            Question = "按吨量口径的达成率（煤岩密度差异已折进去）？",
            Evaluate = f => SafeRatio(Sum(f, x => x.ActualTonnage), Sum(f, x => x.PlanTonnage)) * 100 });
        r.Register(new IndicatorDef { Id = "shortfall", Name = "欠产量", Unit = "m³", Category = "3达成", Format = "N0",
            Agg = "求和", Question = "本期欠了多少方需要回摊到后续期次？",
            Evaluate = f => Sum(f, x => x.ShortfallM3) });
        r.Register(new IndicatorDef { Id = "over", Name = "超产量", Unit = "m³", Category = "3达成", Format = "N0",
            Agg = "求和", Question = "本期超额完成多少方？",
            Evaluate = f => Sum(f, x => Math.Max(0, x.ActualVolumeM3 - x.PlanVolumeM3)) });
        // 计数类按**任务**去重：混采任务在宽表里是多条（煤条+岩条），直接数行数会虚高。
        r.Register(new IndicatorDef { Id = "task_count", Name = "任务数", Unit = "项", Category = "3达成", Format = "0",
            Agg = "计数（按任务去重）", Question = "本期安排了几项生产任务？",
            Evaluate = f => f.Count(x => x.IsPrimaryMaterial) });
        r.Register(new IndicatorDef { Id = "done_count", Name = "完成任务数", Unit = "项", Category = "3达成", Format = "0",
            Agg = "计数（按任务去重）", Question = "其中几项已完成？",
            Evaluate = f => f.Count(x => x.IsPrimaryMaterial && x.Status == "完成") });

        // ── 4 设备效率 / 时间 ──
        r.Register(new IndicatorDef { Id = "plan_hours", Name = "计划工时", Unit = "h", Category = "4设备", Format = "0.#",
            Agg = "求和", Question = "本期排了多少工时？",
            Evaluate = f => Sum(f, x => x.PlannedHours) });
        r.Register(new IndicatorDef { Id = "actual_hours", Name = "实际工时", Unit = "h", Category = "4设备", Format = "0.#",
            Agg = "求和", Question = "设备实际干了多少小时？",
            Evaluate = f => Sum(f, x => x.ActualHours) });
        r.Register(new IndicatorDef { Id = "util", Name = "工时利用率", Unit = "%", Category = "4设备", Format = "0.0",
            Target = 90, HigherIsBetter = true, WarnBand = 10, Agg = "比值 = 实际工时 ÷ 计划工时",
            Question = "排出去的工时有多少真正干上了活？",
            Evaluate = f => SafeRatio(Sum(f, x => x.ActualHours), Sum(f, x => x.PlannedHours)) * 100 });
        r.Register(new IndicatorDef { Id = "shift_count", Name = "台班数", Unit = "台班", Category = "4设备", Format = "0.0",
            Agg = "求和 ÷ 8", Question = "折合多少个台班？",
            Evaluate = f => Sum(f, x => x.PlannedHours) / 8.0 });
        r.Register(new IndicatorDef { Id = "shift_output", Name = "台班产量", Unit = "m³/台班", Category = "4设备", Format = "N0",
            Agg = "比值", Question = "单台班能出多少方——设备效率的横向可比口径？",
            Evaluate = f => SafeRatio(Sum(f, x => x.ActualVolumeM3), Sum(f, x => x.PlannedHours) / 8.0) });
        r.Register(new IndicatorDef { Id = "hourly_output", Name = "小时产量", Unit = "m³/h", Category = "4设备", Format = "N0",
            Agg = "比值", Question = "实际作业时每小时出多少方？",
            Evaluate = f => SafeRatio(Sum(f, x => x.ActualVolumeM3), Sum(f, x => x.ActualHours)) });

        // ─────────────────────────────────────────────────────────────────────
        //  5 单耗 / 运输 —— 全部改吃**真运距**。任务上没运距的事实一律排除在样本外；
        //  样本为空返回 NaN（显示"—"）。绝不再用作业面名散列编运距。
        // ─────────────────────────────────────────────────────────────────────
        r.Register(new IndicatorDef { Id = "fuel_total", Name = "柴油耗", Unit = "L", Category = "5运耗", Format = "N0",
            Agg = "求和（★经验系数估算）",
            Question = "本期烧了多少柴油（工序固定项 + 运距变动项，经验系数估算，非实测）？",
            Evaluate = f => Sum(f, x => x.FuelL) });
        r.Register(new IndicatorDef { Id = "fuel_per_m3", Name = "单位油耗", Unit = "L/m³", Category = "5运耗", Format = "0.00",
            Agg = "比值（运距口径不全时为空）",
            Question = "每方料烧多少油——运距一变它就变，是运输方案优劣的直接体现（★经验系数）？",
            // 运输油耗含 t·km 变动项：只要有一条搬运事实缺运距，这个比值就必然偏低。
            // 比值不像求和那样能"部分正确"，故口径不全一律返回空值。
            Evaluate = f => HaulComplete(f)
                ? SafeRatio(Sum(f, x => x.FuelL), Sum(f, x => x.ActualVolumeM3))
                : double.NaN });
        r.Register(new IndicatorDef { Id = "power_total", Name = "电耗", Unit = "kWh", Category = "5运耗", Format = "N0",
            Agg = "求和（★经验系数估算）", Question = "电铲本期耗电多少（经验系数估算）？",
            Evaluate = f => Sum(f, x => x.PowerKwh) });
        r.Register(new IndicatorDef { Id = "haul_tkm", Name = "运输功", Unit = "t·km", Category = "5运耗", Format = "N0",
            Agg = "求和（仅有运距的事实）", Question = "本期完成多少 t·km 的运输工作量？",
            Evaluate = f => SumOrNaN(Hauled(f), x => x.TransportWorkTKm) });
        r.Register(new IndicatorDef { Id = "avg_haul_dist", Name = "平均运距", Unit = "km", Category = "5运耗", Format = "0.00",
            Agg = "吨量加权平均（仅有运距的事实）", Question = "平均要拉多远？",
            Evaluate = f => WeightedAvg(Hauled(f), x => x.EffectiveHaulKm, x => Math.Max(1e-6, x.ActualTonnage)) });
        r.Register(new IndicatorDef { Id = "haul_coverage_pct", Name = "运距覆盖率", Unit = "%", Category = "5运耗", Format = "0.0",
            Target = 100, HigherIsBetter = true, WarnBand = 20, Agg = "比值（吨量口径）",
            Question = "有多少搬运量是有真运距的？低于 100% 说明运输类指标只统计了一部分。",
            Evaluate = f =>
            {
                double all = Moving(f).Sum(x => x.ActualTonnage);
                return all <= 1e-9 ? double.NaN : Moving(f).Where(x => x.HaulKnown).Sum(x => x.ActualTonnage) / all * 100;
            } });

        // ── 6 煤质（按吨量加权——配煤本就是按吨掺配的）──
        r.Register(new IndicatorDef { Id = "ash", Name = "灰分", Unit = "%", Category = "6煤质", Format = "0.0",
            Agg = "吨量加权平均", Question = "综合灰分是多少，进仓煤达不达标？",
            Evaluate = f => WeightedAvg(f.Where(x => x.HasQuality && x.IsOre), x => x.Ash, x => Math.Max(1e-6, x.ActualTonnage)) });
        r.Register(new IndicatorDef { Id = "cv", Name = "发热量", Unit = "MJ/kg", Category = "6煤质", Format = "0.0",
            Agg = "吨量加权平均", Question = "综合热值是多少？",
            Evaluate = f => WeightedAvg(f.Where(x => x.HasQuality && x.IsOre), x => x.Calorific, x => Math.Max(1e-6, x.ActualTonnage)) });
        r.Register(new IndicatorDef { Id = "sulfur", Name = "硫分", Unit = "%", Category = "6煤质", Format = "0.00",
            Agg = "吨量加权平均", Question = "综合硫分是否越限？",
            Evaluate = f => WeightedAvg(f.Where(x => x.HasQuality && x.IsOre), x => x.Sulfur, x => Math.Max(1e-6, x.ActualTonnage)) });
        r.Register(new IndicatorDef { Id = "moisture", Name = "水分", Unit = "%", Category = "6煤质", Format = "0.0",
            Agg = "吨量加权平均", Question = "综合水分是多少？",
            Evaluate = f => WeightedAvg(f.Where(x => x.HasQuality && x.IsOre), x => x.Moisture, x => Math.Max(1e-6, x.ActualTonnage)) });

        // ─────────────────────────────────────────────────────────────────────
        //  7 采排物流 —— 回答「从哪采剥、排弃到哪、拉多远、还排得下吗」。
        //  这一组是流向方案比选与排土场接续决策的直接依据。
        // ─────────────────────────────────────────────────────────────────────
        r.Register(new IndicatorDef { Id = "transport_work", Name = "运输功", Unit = "万t·km", Category = "7物流", Format = "0.00",
            Agg = "求和 ÷ 1e4（仅有运距的事实）",
            Question = "全矿本期的运输工作量有多大——油耗/轮胎/折旧几乎线性于它，是流向方案比选的核心成本代理。",
            Evaluate = f => SumOrNaN(Hauled(f), x => x.TransportWorkTKm) / 1e4 });
        r.Register(new IndicatorDef { Id = "avg_haul_weighted", Name = "加权平均运距", Unit = "km", Category = "7物流", Format = "0.00",
            Agg = "吨量加权平均 = 运输功 ÷ 总吨量",
            Question = "全矿平均要拉多远？必须按吨量加权——简单平均会让一条 0.1 万吨的短运距与一条 10 万吨的长运距等权，结论正好相反。",
            Evaluate = f =>
            {
                var sel = Hauled(f).ToList();
                if (sel.Count == 0) return double.NaN;
                return SafeRatio(sel.Sum(x => x.TransportWorkTKm), sel.Sum(x => x.ActualTonnage));
            } });
        r.Register(new IndicatorDef { Id = "internal_dump_pct", Name = "内排率", Unit = "%", Category = "7物流", Format = "0.0",
            Agg = "比值 = 内排占容 ÷ 全部排弃占容",
            Question = "剥离料有多大比例排进了采空区？内排运距短且多为下排，是露天矿降本的第一杠杆。",
            Evaluate = f =>
            {
                var basis = DumpBasis(f);
                double all = basis.Sum(x => x.DumpVolumeM3);
                if (all <= 1e-6) return double.NaN;
                // 去向未定的排弃量判不出内/外排，硬算必然偏低——宁可返回空值显示"—"
                if (basis.Any(x => !x.HasDestination && x.DumpVolumeM3 > 1e-6)) return double.NaN;
                return basis.Where(x => x.DestinationKind == SinkKind.InternalDump).Sum(x => x.DumpVolumeM3) / all * 100;
            } });
        r.Register(new IndicatorDef { Id = "dump_volume", Name = "排弃占容量", Unit = "万m³", Category = "7物流", Format = "0.00",
            Agg = "求和 ÷ 1e4（占容方 = 实方×Kr）",
            Question = "本期往排土场排了多少方？必须用占容方——排土库容吃的是沉降稳定后的体积，用实方扣会少扣 10~20%。",
            Evaluate = f => DumpM3(f) / 1e4 });
        r.Register(new IndicatorDef { Id = "dump_capacity_used_pct", Name = "库容消耗率", Unit = "%", Category = "7物流", Format = "0.00",
            Agg = "比值 = 本期排弃占容 ÷ 去向剩余库容（台账当前值，按去向去重）",
            Question = "本期吃掉了剩余库容的百分之几——除一下就知道排土场还能撑多少期。",
            Evaluate = f =>
            {
                double left = SinkRemainingM3(f);
                double need = DumpM3(f);
                return left <= 1e-6 || double.IsNaN(need) ? double.NaN : need / left * 100;
            } });
        r.Register(new IndicatorDef { Id = "sink_alert_count", Name = "库容预警数", Unit = "个", Category = "7物流", Format = "0",
            Target = 0, HigherIsBetter = false, WarnBand = 0, Agg = "计数（按去向去重）",
            Question = "有几个排土场已填 90% 以上、或本期就要吃掉八成剩余库容？这些必须马上安排接续。",
            Evaluate = f => f.Where(x => x.IsDumpingDestination)
                             .GroupBy(x => x.DestinationKey, StringComparer.OrdinalIgnoreCase)
                             .Count(g =>
                             {
                                 var head = g.First();
                                 if (head.SinkFillRate >= 0.9) return true;
                                 double left = head.SinkRemainingM3;
                                 return left > 1e-6 && g.Sum(x => x.DumpVolumeM3) > left * 0.8;
                             }) });
        r.Register(new IndicatorDef { Id = "ore_ratio", Name = "采出占比", Unit = "%", Category = "7物流", Format = "0.0",
            Agg = "比值 = 采出实方 ÷ 采剥总实方（采装侧）",
            Question = "采装出来的料里有多大比例是煤/矿？它与剥采比互为倒影，也是配车与卸点分流的依据。",
            Evaluate = f =>
            {
                double all = Mined(f).Sum(x => x.ActualVolumeM3);
                return all <= 1e-6 ? double.NaN : Mined(f).Where(x => x.IsOre).Sum(x => x.ActualVolumeM3) / all * 100;
            } });
        r.Register(new IndicatorDef { Id = "topsoil_volume", Name = "表土量", Unit = "万m³", Category = "7物流", Format = "0.00",
            Agg = "求和 ÷ 1e4（采装侧·表土物料）",
            Question = "本期剥了多少表土？表土是复垦资源、必须单独堆存，量与去向都要单独考核。",
            Evaluate = f => Mined(f).Where(x => x.IsTopsoil).Sum(x => x.ActualVolumeM3) / 1e4 });
        r.Register(new IndicatorDef { Id = "truck_trips", Name = $"车次(按{NominalTruckPayloadT:0}t估)", Unit = "车次", Category = "7物流", Format = "N0",
            Agg = "比值 = 吨量 ÷ 名义单车载重（★估算）",
            Question = "大致要跑多少车——卸点排队与配车数的量级感。名义载重口径已写在指标名里，不是实测车数。",
            Evaluate = f =>
            {
                double t = f.Where(x => x.MovesMaterial).Sum(x => x.ActualTonnage);
                return t <= 1e-6 ? double.NaN : t / NominalTruckPayloadT;
            } });

        return r;
    }

    /// <summary>内置 + 用户自定义（<see cref="IndicatorLibrary"/>）合并的完整注册表。模板/引擎统一用这个。</summary>
    public static IndicatorRegistry Load()
    {
        var r = BuiltIn();
        foreach (var c in IndicatorLibrary.LoadAll())
        {
            if (r.Resolve(c.Id) != null) continue;   // 自定义不覆盖内置 Id（防撞规则）
            r.Register(r.CompileCustom(c));
        }
        return r;
    }

    [ThreadStatic] private static HashSet<string>? _evalStack;

    /// <summary>把一条自定义指标（计算规则）编译成引擎可用的 <see cref="IndicatorDef"/>（结构化 or 公式）。</summary>
    public IndicatorDef CompileCustom(CustomIndicatorDef d)
    {
        Func<IReadOnlyList<ProductionFact>, double> ev;
        if (d.Mode == IndicatorMode.Formula)
        {
            string formula = d.Formula;
            ev = facts => ExprEval.Eval(formula, name => ResolveForEval(name, facts));
        }
        else
        {
            var def = d;
            ev = facts => StructuredEval(def, facts);
        }
        return new IndicatorDef
        {
            Id = d.Id, Name = d.Name, Unit = d.Unit, Category = d.Category, Format = d.Format,
            Agg = "自定义", Question = d.Mode == IndicatorMode.Formula ? "自定义公式：" + d.Formula : "自定义结构化指标",
            Evaluate = ev, Target = d.Target, HigherIsBetter = d.HigherIsBetter, WarnBand = d.WarnBand,
        };
    }

    /// <summary>公式里引用的指标 Id → 其在当前事实集上的值；带循环保护（自引用/环 → NaN）。</summary>
    private double ResolveForEval(string id, IReadOnlyList<ProductionFact> facts)
    {
        _evalStack ??= new HashSet<string>(StringComparer.Ordinal);
        if (!_evalStack.Add(id)) return double.NaN;   // 检测到环
        try { return Resolve(id)?.Evaluate(facts) ?? double.NaN; }
        finally { _evalStack.Remove(id); }
    }

    private static double StructuredEval(CustomIndicatorDef d, IReadOnlyList<ProductionFact> facts)
    {
        var sel = facts.Where(f => CustomIndicatorDef.Pass(f, d.Filter)).ToList();
        if (sel.Count == 0) return d.Agg == AggOp.Count ? 0 : double.NaN;
        if (d.Agg == AggOp.Count) return sel.Count;

        // 运距/运输功字段只有"有真运距"的事实才有意义，否则 0 会把平均值拉低成假数。
        if (d.Field is FactField.HaulDistance or FactField.HaulTKm)
        {
            sel = sel.Where(f => f.HaulKnown).ToList();
            if (sel.Count == 0) return double.NaN;
        }

        Func<ProductionFact, double> val = f => CustomIndicatorDef.FieldValue(f, d.Field);
        return d.Agg switch
        {
            AggOp.Sum => sel.Sum(val),
            AggOp.Avg => sel.Average(val),
            AggOp.Min => sel.Min(val),
            AggOp.Max => sel.Max(val),
            AggOp.WeightedAvgByActualVol => WeightedAvg(sel, val, f => Math.Max(1e-6, f.ActualVolumeM3)),
            _ => sel.Sum(val),
        };
    }
}
