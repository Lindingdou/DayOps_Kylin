using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 一条生产事实（宽表行）。粒度 = <b>日 × 物料</b>，量的基准口径一律【实方 m³】。
/// 所有报表都从这份宽表按维度聚合出来（移植原 <c>TaskLib.Reporting.ProductionFact</c> 的取数层）。
///
/// ── 本层的三条铁律（原版的话，照搬）──
/// ① <b>物料一律走物料本体</b> <see cref="MaterialSpec"/>：煤条与岩条各自带自己的密度/膨胀系数。
///    绝不用「工序==采装 ⇒ 煤」这种猜法 —— 那会把剥离的岩石统统算成煤，
///    采出量 / 剥离量 / 剥采比 三个核心指标同时失真。
/// ② <b>严禁写死密度</b>：吨量 = <c>Spec.ToTonnage(实方)</c>，松方 = ×Ks，占容方 = ×Kr，
///    一个常量都不许出现在本文件。
/// ③ 算不出来的一律留空（<c>null</c> / <c>NaN</c>），指标层据此渲染成「—」。
///    <b>报表宁可显示"—"也不能显示编造的数。</b>
///
/// ── 与原版的粒度差异（登记）──
/// 原版粒度是 <b>任务 × 物料</b>（班/设备/工序/作业面/去向都在维上），数据源是任务台账。
/// Kylin 侧的实绩事实表是 <c>daily_mine_summary</c> —— <b>日粒度、全矿口径、按外运通道分列</b>，
/// 没有班/设备/工序/作业面这些维。故本实现的维只到「日 × 物料」，
/// 按设备/作业面/去向展开的那几类报表**做不出来**，如实登记，不拿全矿数冒充分面数。
/// </summary>
public sealed class ProductionFact
{
    public DateTime Date { get; set; }

    /// <summary>物料码（<see cref="MaterialCatalog"/>）。</summary>
    public string MaterialCode { get; set; } = "";

    public MaterialSpec Spec => MaterialCatalog.Resolve(MaterialCode);

    /// <summary>实方 m³（基准口径）。</summary>
    public double InSituM3 { get; set; }

    /// <summary>吨量 t —— 走物料本体的密度，不写死。</summary>
    public double TonnageT => Spec.ToTonnage(InSituM3);

    /// <summary>松方 m³（卡车配车口径）。</summary>
    public double LooseM3 => Spec.ToLooseM3(InSituM3);

    /// <summary>排弃占容方 m³（排土库容口径）。</summary>
    public double DumpM3 => Spec.ToDumpM3(InSituM3);

    /// <summary>是不是矿（煤）—— 采出量与剥离量按它分。</summary>
    public bool IsOre => Spec.IsOre;

    /// <summary>筒仓存量 t（只有"当日库存"这类事实带它；不计入采出量）。</summary>
    public double SiloStockT { get; set; }
}

/// <summary>报表周期口径（与原版同名同义）。</summary>
public enum PeriodKind { Day, Week, Month }

/// <summary>
/// Kylin 侧的取数：<c>daily_mine_summary</c> → 生产事实。
///
/// 计划侧不在这里取 —— 计划是月量，日计划要经"月量 ÷ 本月作业日"裂解，
/// 那份口径只在 <see cref="WeekPlanSource.MonthBasisOf"/> 一处（§三三六 定的规矩），
/// 报表照读那一份，<b>不另算一遍</b>。
/// </summary>
public static class ProductionFactSource
{
    /// <summary>把一段日期的实绩摊成事实（每天两条：煤 + 岩）。没录的天不产生事实。</summary>
    public static List<ProductionFact> Load(DbConnection? conn, DateTime from, DateTime to)
    {
        var facts = new List<ProductionFact>();
        foreach (var r in DailyActuals.LoadRange(conn, from, to))
        {
            // 煤：出煤合计（六路外运，口径同周计划）折实方
            if (r.CoalTotalT > 0)
                facts.Add(new ProductionFact
                {
                    Date = r.Date,
                    MaterialCode = MaterialCatalog.Coal,
                    // 实方由吨量按**物料自身密度**反折，不写死常数
                    InSituM3 = r.CoalTotalT / MaterialCatalog.Resolve(MaterialCatalog.Coal).InSituDensityTPerM3,
                    SiloStockT = r.SiloTotalT,
                });
            // 岩：剥离本来就是实方
            if (r.StrippingM3 > 0)
                facts.Add(new ProductionFact
                {
                    Date = r.Date,
                    MaterialCode = MaterialCatalog.Rock,
                    InSituM3 = r.StrippingM3,
                });
        }
        return facts;
    }

    /// <summary>期间的起讫日（<paramref name="anchor"/> 落在哪个日/周/月里）。</summary>
    public static (DateTime From, DateTime To) RangeOf(PeriodKind kind, DateTime anchor)
    {
        var d = anchor.Date;
        return kind switch
        {
            PeriodKind.Day => (d, d),
            // 周一为周首 —— 与 §三三五 周计划同一处口径
            PeriodKind.Week => (WeekPlanLink.MondayOf(d), WeekPlanLink.MondayOf(d).AddDays(6)),
            _ => (new DateTime(d.Year, d.Month, 1), new DateTime(d.Year, d.Month, 1).AddMonths(1).AddDays(-1)),
        };
    }

    /// <summary>期间标题（"2026-09-08" / "2026-09-07 ~ 09-13 周" / "2026-09 月"）。</summary>
    public static string TitleOf(PeriodKind kind, DateTime anchor)
    {
        var (a, b) = RangeOf(kind, anchor);
        return kind switch
        {
            PeriodKind.Day => a.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            PeriodKind.Week => $"{a:yyyy-MM-dd} ~ {b:MM-dd} 周",
            _ => a.ToString("yyyy-MM", CultureInfo.InvariantCulture) + " 月",
        };
    }

    /// <summary>
    /// 期间内**实际录了几天** / 应有几天。报表必须把这个说出来 ——
    /// 只录了三天的周报，合计看着就是"这周产量很低"，而实际是数据没录全。
    /// </summary>
    public static (int Recorded, int Total) Coverage(DbConnection? conn, PeriodKind kind, DateTime anchor)
    {
        var (a, b) = RangeOf(kind, anchor);
        int total = (int)(b.Date - a.Date).TotalDays + 1;
        int rec = DailyActuals.LoadRange(conn, a, b).Count;
        return (rec, total);
    }
}
