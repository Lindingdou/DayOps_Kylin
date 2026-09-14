using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;

namespace PitMine3D.Kylin.Data;

/// <summary>指标评价灯（红黄绿）。<see cref="None"/> = 无目标/不评价。</summary>
public enum ReportStatus { None, Ok, Warn, Bad }

/// <summary>指标算出来的一个值 + 评价。</summary>
public sealed class IndicatorValue
{
    public IndicatorDef Def = null!;
    /// <summary>算不出来 = <see cref="double.NaN"/>，渲染成「—」。</summary>
    public double Value = double.NaN;
    public double? Target;
    public ReportStatus Status = ReportStatus.None;

    public bool Known => !double.IsNaN(Value) && !double.IsInfinity(Value);

    /// <summary>按指标自带格式渲染；算不出来一律「—」。</summary>
    public string Text => Known ? Value.ToString(Def.Format, CultureInfo.InvariantCulture) : "—";

    public string TextWithUnit => Known && Def.Unit.Length > 0 ? Text + " " + Def.Unit : Text;
}

/// <summary>
/// 一个「指标定义」= 计算规则（移植原 <c>TaskLib.Reporting.IndicatorDef</c> 的形状）。
///
/// <b>空值约定</b>：算不出来（口径缺数据、分母为 0）一律返回 <see cref="double.NaN"/>，渲染成「—」。
/// <b>报表宁可显示"—"也不能显示编造的数。</b>
/// </summary>
public sealed class IndicatorDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Category { get; init; } = "";
    public string Format { get; init; } = "N1";

    /// <summary>聚合方式（人读）。</summary>
    public string Agg { get; init; } = "求和";

    /// <summary>这个指标用来回答什么问题（一句话）—— 免得"有指标不知道干嘛用"。</summary>
    public string Question { get; init; } = "";

    /// <summary>在给定事实集合上算出指标值。<paramref name="ctx"/> 带计划侧等事实以外的输入。</summary>
    public Func<IReadOnlyList<ProductionFact>, IndicatorContext, double> Evaluate { get; init; }
        = (_, _) => double.NaN;

    /// <summary>越大越好 / 越小越好。带 <see cref="IndicatorContext.TargetOf"/> 时出红黄绿。</summary>
    public bool HigherIsBetter { get; init; } = true;

    /// <summary>黄灯带宽 %：偏离目标不超过此值算"接近达标"→黄；再差→红。</summary>
    public double WarnBandPct { get; init; } = 10;

    /// <summary>按目标评价（无目标 = 不评价）。</summary>
    public ReportStatus Judge(double value, double? target)
    {
        if (target is not { } t || double.IsNaN(value) || double.IsNaN(t) || Math.Abs(t) < 1e-9)
            return ReportStatus.None;
        double devPct = (value - t) / Math.Abs(t) * 100.0;
        if (!HigherIsBetter) devPct = -devPct;
        if (devPct >= 0) return ReportStatus.Ok;
        return devPct >= -WarnBandPct ? ReportStatus.Warn : ReportStatus.Bad;
    }
}

/// <summary>指标计算的上下文（事实之外的输入：计划侧、期间、口径）。</summary>
public sealed class IndicatorContext
{
    public PeriodKind Period = PeriodKind.Day;
    public DateTime From, To;

    /// <summary>期间内**有计划可依**的天数（月计划裂解得到日计划的那些天）。</summary>
    public int PlannedDays;

    /// <summary>期间计划采出 万t / 计划剥离 万m³实方；判不了留 null（<b>不是 0</b>）。</summary>
    public double? PlanCoalWanT, PlanStripWanM3;

    /// <summary>期间实际录了几天 / 应有几天。</summary>
    public int RecordedDays, TotalDays;

    public double? TargetOf(string indicatorId) => indicatorId switch
    {
        "coal_wan_t" => PlanCoalWanT,
        "strip_wan_m3" => PlanStripWanM3,
        _ => null,
    };
}

/// <summary>
/// 指标库（移植原 <c>TaskLib.Reporting.IndicatorLibrary</c> 的形状，
/// 只收 <b>Kylin 的事实确实喂得出来</b> 的那些）。
///
/// ── 登记的取舍 ──
/// 原版指标里还有运输功 / 吨量加权运距 / 单位油耗 / 煤质（灰分·热值·硫）/ 设备台效 等，
/// 它们要的是**任务粒度**的运距、设备、煤质字段，而 Kylin 的实绩事实表是
/// <b>日粒度、全矿口径</b>（<c>daily_mine_summary</c>）—— 那些字段一个都没有。
/// 按"宁可显示—也不编数"的规矩，这些指标**不收进库**，而不是收进来后恒返回 NaN
/// （收进来会让报表上凭空多出一排永远是「—」的行，看着像坏了）。
/// </summary>
public static class IndicatorLibrary
{
    private static double Sum(IReadOnlyList<ProductionFact> f, Func<ProductionFact, double> pick)
        => f.Count == 0 ? double.NaN : f.Sum(pick);

    public static readonly IReadOnlyList<IndicatorDef> All = new List<IndicatorDef>
    {
        new()
        {
            Id = "coal_wan_t", Name = "采出量", Unit = "万t", Category = "产量", Format = "N2",
            Question = "本期采出了多少煤（按六路外运通道合计）",
            Evaluate = (f, _) => Sum(f.Where(x => x.IsOre).ToList(), x => x.TonnageT) / 1e4,
        },
        new()
        {
            Id = "coal_wan_m3", Name = "采出量(实方)", Unit = "万m³", Category = "产量", Format = "N2",
            Question = "同上，折成实方，与剥离量同口径好相加",
            Evaluate = (f, _) => Sum(f.Where(x => x.IsOre).ToList(), x => x.InSituM3) / 1e4,
        },
        new()
        {
            Id = "strip_wan_m3", Name = "剥离量", Unit = "万m³实方", Category = "产量", Format = "N2",
            Question = "本期剥了多少岩（实方）",
            Evaluate = (f, _) => Sum(f.Where(x => !x.IsOre).ToList(), x => x.InSituM3) / 1e4,
        },
        new()
        {
            Id = "dump_wan_m3", Name = "排弃占容", Unit = "万m³", Category = "产量", Format = "N2",
            Question = "这些剥离物排到排土场要占多少库容（V容=V实×Kr）",
            Evaluate = (f, _) => Sum(f.Where(x => !x.IsOre).ToList(), x => x.DumpM3) / 1e4,
        },
        new()
        {
            Id = "strip_ratio", Name = "生产剥采比", Unit = "m³/t", Category = "口径", Format = "N2",
            Question = "本期每采一吨煤要剥多少方岩",
            HigherIsBetter = false,
            Evaluate = (f, _) =>
            {
                double coalT = f.Where(x => x.IsOre).Sum(x => x.TonnageT);
                double stripM3 = f.Where(x => !x.IsOre).Sum(x => x.InSituM3);
                // 分母为 0 = 算不出来，不是 0 —— 没采煤的日子剥采比无定义
                return coalT > 1e-9 ? stripM3 / coalT : double.NaN;
            },
        },
        new()
        {
            Id = "coal_attain_pct", Name = "采出达成率", Unit = "%", Category = "达成", Format = "N1",
            Question = "本期采出量占计划的百分之多少",
            WarnBandPct = 5,
            Evaluate = (f, c) =>
            {
                if (c.PlanCoalWanT is not { } plan || plan <= 1e-9) return double.NaN;
                double act = f.Where(x => x.IsOre).Sum(x => x.TonnageT) / 1e4;
                return act / plan * 100.0;
            },
        },
        new()
        {
            Id = "strip_attain_pct", Name = "剥离达成率", Unit = "%", Category = "达成", Format = "N1",
            Question = "本期剥离量占计划的百分之多少",
            WarnBandPct = 5,
            Evaluate = (f, c) =>
            {
                if (c.PlanStripWanM3 is not { } plan || plan <= 1e-9) return double.NaN;
                double act = f.Where(x => !x.IsOre).Sum(x => x.InSituM3) / 1e4;
                return act / plan * 100.0;
            },
        },
        new()
        {
            Id = "silo_stock_t", Name = "筒仓存量(期末)", Unit = "t", Category = "库存", Format = "N1",
            Question = "期末三个筒仓里还存着多少煤（是库存，不计入采出量）",
            Evaluate = (f, _) =>
            {
                var last = f.Where(x => x.IsOre).OrderBy(x => x.Date).LastOrDefault();
                return last == null ? double.NaN : last.SiloStockT;
            },
        },
        new()
        {
            Id = "coverage_pct", Name = "实绩录入完整度", Unit = "%", Category = "口径", Format = "N0",
            Question = "本期应录几天、实际录了几天 —— 只录了三天的周报，合计低不是产量低",
            WarnBandPct = 20,
            Evaluate = (_, c) => c.TotalDays <= 0 ? double.NaN : c.RecordedDays * 100.0 / c.TotalDays,
        },
    };

    public static IndicatorDef? Find(string id)
        => All.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));

    /// <summary>算一整套（默认全部指标，按库中顺序）。</summary>
    public static List<IndicatorValue> Evaluate(IReadOnlyList<ProductionFact> facts, IndicatorContext ctx,
                                                IEnumerable<string>? only = null)
    {
        var defs = only == null
            ? All.ToList()
            : only.Select(Find).Where(d => d != null).Select(d => d!).ToList();
        var list = new List<IndicatorValue>(defs.Count);
        foreach (var d in defs)
        {
            double v;
            try { v = d.Evaluate(facts, ctx); }
            catch { v = double.NaN; }   // 单个指标算炸了不该带塌整张报表
            double? t = ctx.TargetOf(d.Id);
            list.Add(new IndicatorValue { Def = d, Value = v, Target = t, Status = d.Judge(v, t) });
        }
        return list;
    }
}
