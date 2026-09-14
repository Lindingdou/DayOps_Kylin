using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;

namespace PitMine3D.Kylin.Data;

/// <summary>一张生产报表。</summary>
public sealed class ProductionReportDoc
{
    public string Title = "";
    public PeriodKind Period;
    public DateTime From, To;

    public List<IndicatorValue> Indicators = new();

    /// <summary>逐日明细（按日期升序；没录的天不出现）。</summary>
    public List<DailyActualRow> Days = new();

    /// <summary>口径与完整度提示 —— <b>一条不吞</b>。</summary>
    public List<string> Notes = new();

    /// <summary>期间应录天数 / 实录天数。</summary>
    public int TotalDays, RecordedDays;

    public IndicatorValue? this[string id]
        => Indicators.FirstOrDefault(v => string.Equals(v.Def.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// 「生产报告」的报表装配（移植原 <c>TaskLib.Reporting</c> 四层架构里的 <b>取数 → 指标</b> 两层
/// 落到 Kylin 的数据上）。
///
/// ── 与原版的范围差异（登记）──
/// 原版 <c>ReportHubWindow</c> 是个报表中心：模板设计器、叙述报告、源×汇交叉表、
/// 存档回溯与两期对比、PDF/Word 渲染与打印（`Reporting/` 二十余个文件、五千余行）。
/// 本轮移的是**口径那两层** —— 事实宽表与指标库（含目标评价与"算不出来显示—"的规矩），
/// 外加一个日/周/月的汇总窗与 CSV 导出。
/// <b>模板设计器 / 叙述报告 / 交叉表 / PDF·Word 渲染与存档对比均未移，如实登记。</b>
///
/// ── 计划侧口径 ──
/// 计划是**月量**，日/周计划要经"月量 ÷ 本月作业日"裂解 —— 那份口径只在
/// <see cref="WeekPlanSource.MonthBasisOf"/> 一处（§三三六 定的规矩），本类照读，<b>不另算一遍</b>。
/// 跨月的周按天各取各月的基准，与周计划同一条路。
/// </summary>
public static class ProductionReport
{
    /// <summary>装配一张报表。</summary>
    public static ProductionReportDoc Build(DbConnection? conn, PeriodKind kind, DateTime anchor)
    {
        var (from, to) = ProductionFactSource.RangeOf(kind, anchor);
        var doc = new ProductionReportDoc
        {
            Title = ProductionFactSource.TitleOf(kind, anchor),
            Period = kind,
            From = from,
            To = to,
        };

        var facts = ProductionFactSource.Load(conn, from, to);
        doc.Days = DailyActuals.LoadRange(conn, from, to);
        doc.TotalDays = (int)(to.Date - from.Date).TotalDays + 1;
        doc.RecordedDays = doc.Days.Count;

        var ctx = new IndicatorContext
        {
            Period = kind, From = from, To = to,
            RecordedDays = doc.RecordedDays, TotalDays = doc.TotalDays,
        };
        FillPlan(conn, ctx, from, to, doc.Notes);

        doc.Indicators = IndicatorLibrary.Evaluate(facts, ctx);

        if (conn == null) doc.Notes.Add("没有数据库连接 —— 全部指标显示「—」");
        else if (doc.RecordedDays == 0)
            doc.Notes.Add($"{doc.Title} 一天实绩都没录 —— 先用「实绩录入」补录，否则合计低不代表产量低");
        else if (doc.RecordedDays < doc.TotalDays)
            // 这条必须说：只录了三天的周报，合计看着就是"这周产量很低"
            doc.Notes.Add($"本期应录 {doc.TotalDays} 天、实录 {doc.RecordedDays} 天 —— "
                        + "合计与达成率都只覆盖已录的那几天，别当成全期实绩");

        doc.Notes.Add(WeekPlanSource.CoalBasisText);
        return doc;
    }

    /// <summary>
    /// 计划侧：把期间逐日的计划量累起来。跨月的周按天各取各月基准（与周计划同一条路）。
    /// 任何一天判不了就整段留 null（<b>不是 0</b>）—— 半段计划算出来的达成率是误导。
    /// </summary>
    private static void FillPlan(DbConnection? conn, IndicatorContext ctx, DateTime from, DateTime to, List<string> notes)
    {
        if (conn == null) return;
        var basisByMonth = new Dictionary<string, MonthBasis>(StringComparer.Ordinal);
        double coal = 0, strip = 0;
        int planned = 0, unknown = 0;

        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
        {
            string key = WeekPlanLink.MonthKeyOf(d);
            if (!basisByMonth.TryGetValue(key, out var b))
                basisByMonth[key] = b = WeekPlanSource.MonthBasisOf(conn, key);

            if (!b.Usable) { unknown++; continue; }
            // 日计划 = 月量 ÷ 本月作业日（DayLoadM3 是折方后的实方, 这里要的是万t/万m³）
            coal += b.DayLoadM3 * MaterialCatalog0Density() / 1e4;
            strip += b.DayDumpM3 / 1e4;
            planned++;
        }

        ctx.PlannedDays = planned;
        if (planned > 0 && unknown == 0)
        {
            ctx.PlanCoalWanT = coal;
            ctx.PlanStripWanM3 = strip;
        }
        else if (unknown > 0)
        {
            notes.Add(unknown == ctx.TotalDays
                ? "没有可用的月计划 —— 达成率显示「—」"
                : $"{unknown} 天没有可用的月计划 —— 达成率整段显示「—」（半段计划算出来的达成率是误导）");
        }

        if (basisByMonth.Count > 1) notes.Add("本期跨月，计划按天各取各月基准");
    }

    /// <summary>煤密度：与周计划/实绩录入同一处常量。</summary>
    private static double MaterialCatalog0Density() => WeekPlanSource.CoalDensity;

    /// <summary>报表导出 CSV（指标表 + 逐日明细）。</summary>
    public static string ToCsv(ProductionReportDoc doc)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"生产报表,{doc.Title}\n");
        sb.Append($"期间,{doc.From:yyyy-MM-dd},{doc.To:yyyy-MM-dd},应录 {doc.TotalDays} 天,实录 {doc.RecordedDays} 天\n\n");

        sb.Append("指标,值,单位,目标,评价,口径\n");
        foreach (var v in doc.Indicators)
            sb.Append($"{v.Def.Name},{Csv(v.Text)},{v.Def.Unit},")
              .Append(v.Target is { } t ? t.ToString("N2", CultureInfo.InvariantCulture) : "—").Append(',')
              .Append(StatusCn(v.Status)).Append(',').Append(Csv(v.Def.Question)).Append('\n');

        sb.Append("\n日期,出煤合计t,折实方m³,剥离m³实方,筒仓存量t\n");
        foreach (var d in doc.Days)
            sb.Append($"{d.Date:yyyy-MM-dd},{d.CoalTotalT.ToString("N1", CultureInfo.InvariantCulture)},")
              .Append(d.CoalM3.ToString("N1", CultureInfo.InvariantCulture)).Append(',')
              .Append(d.StrippingM3.ToString("N1", CultureInfo.InvariantCulture)).Append(',')
              .Append(d.SiloTotalT.ToString("N1", CultureInfo.InvariantCulture)).Append('\n');

        if (doc.Notes.Count > 0)
        {
            sb.Append("\n口径与提示\n");
            foreach (var n in doc.Notes) sb.Append(Csv(n)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>评价灯中文名。</summary>
    public static string StatusCn(ReportStatus s) => s switch
    {
        ReportStatus.Ok => "达标",
        ReportStatus.Warn => "接近",
        ReportStatus.Bad => "未达标",
        _ => "—",
    };

    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
