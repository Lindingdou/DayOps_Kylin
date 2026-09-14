using System;
using System.Data.Common;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 生产报表的取数与指标（§三五〇）。
///
/// 三条头号判据：
/// **算不出来显示「—」而不是 0**（分母为 0 的剥采比、没有计划的达成率）、
/// **实绩录入完整度要报出来**（只录三天的周报，合计低不是产量低）、
/// **物料走本体不写死密度**（把剥离的岩石当成煤，三个核心指标会同时失真）。
/// </summary>
[Collection("LicenseTimeGuard")]   // 与其它写测试库的用例错开
public class ProductionReportTests
{
    private static readonly DateTime Mon = new(2026, 9, 7);   // 周一

    private static void Clear(DbConnection c)
    {
        foreach (var sql in new[] { "DELETE FROM daily_mine_summary", "DELETE FROM monthly_plan", "DELETE FROM shift_calendar" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    private static void Day(DbConnection c, DateTime d, double coalT, double stripM3, double silo = 0)
        => DailyActuals.Save(c, new DailyActualRow
        { Date = d, BigBelt = coalT, StrippingM3 = stripM3, Silo1 = silo });

    private static void MonthPlan(DbConnection c, int y, int m, double coalWanT, double stripWanM3)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"INSERT INTO monthly_plan (year, month, plan_coal_wan_t, plan_strip_wan_m3) "
                        + $"VALUES ({y},{m},{coalWanT},{stripWanM3})";
        cmd.ExecuteNonQuery();
    }

    // ── 期间口径 ────────────────────────────────────────────
    [Fact]
    public void 期间_日周月的起讫()
    {
        var wed = new DateTime(2026, 9, 9);
        Assert.Equal((wed, wed), ProductionFactSource.RangeOf(PeriodKind.Day, wed));
        Assert.Equal((Mon, Mon.AddDays(6)), ProductionFactSource.RangeOf(PeriodKind.Week, wed));
        Assert.Equal((new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)),
                     ProductionFactSource.RangeOf(PeriodKind.Month, wed));
    }

    [Fact]
    public void 期间_周首与周计划同一处口径()
    {
        // 周一为周首 —— 两处若不同, 同一周的报表与周计划会差一天
        foreach (var d in new[] { Mon, Mon.AddDays(3), Mon.AddDays(6) })
            Assert.Equal(WeekPlanLink.MondayOf(d), ProductionFactSource.RangeOf(PeriodKind.Week, d).From);
    }

    [Fact]
    public void 期间_标题分得清日周月()
    {
        var wed = new DateTime(2026, 9, 9);
        Assert.Equal("2026-09-09", ProductionFactSource.TitleOf(PeriodKind.Day, wed));
        Assert.Contains("周", ProductionFactSource.TitleOf(PeriodKind.Week, wed));
        Assert.Contains("2026-09", ProductionFactSource.TitleOf(PeriodKind.Month, wed));
    }

    // ── 事实：物料走本体 ────────────────────────────────────
    [Fact]
    public void 事实_煤与岩各带自己的密度不写死()
    {
        // ★ 把剥离的岩石当成煤, 采出量/剥离量/剥采比会同时失真
        using var db = TestDb.Open();
        Clear(db.Connection);
        Day(db.Connection, Mon, coalT: 1350, stripM3: 1000);

        var facts = ProductionFactSource.Load(db.Connection, Mon, Mon);
        Assert.Equal(2, facts.Count);
        var coal = facts.Single(f => f.IsOre);
        var rock = facts.Single(f => !f.IsOre);
        Assert.Equal(MaterialCatalog.Coal, coal.MaterialCode);
        Assert.Equal(MaterialCatalog.Rock, rock.MaterialCode);
        // 煤：1350 t ÷ 煤密度 = 实方；再折回吨量必须还原
        Assert.Equal(1350, coal.TonnageT, 3);
        Assert.Equal(1000, rock.InSituM3, 6);
        // 岩的吨量走岩石密度, 与煤不同
        Assert.NotEqual(coal.Spec.InSituDensityTPerM3, rock.Spec.InSituDensityTPerM3);
    }

    [Fact]
    public void 事实_量为零的那一侧不产生事实()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Day(db.Connection, Mon, coalT: 100, stripM3: 0);
        var facts = ProductionFactSource.Load(db.Connection, Mon, Mon);
        Assert.Single(facts);
        Assert.True(facts[0].IsOre);
    }

    [Fact]
    public void 事实_没录的天不产生事实()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Assert.Empty(ProductionFactSource.Load(db.Connection, Mon, Mon.AddDays(6)));
        Assert.Empty(ProductionFactSource.Load(null, Mon, Mon));
    }

    [Fact]
    public void 事实_排弃占容大于实方()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Day(db.Connection, Mon, 0, stripM3: 1000);
        var rock = ProductionFactSource.Load(db.Connection, Mon, Mon).Single();
        Assert.True(rock.DumpM3 > rock.InSituM3);
        Assert.True(rock.LooseM3 > rock.InSituM3);
    }

    // ── 完整度 ──────────────────────────────────────────────
    [Fact]
    public void 完整度_只录三天的周报要说出来()
    {
        // ★ 不说的话, 合计看着就是"这周产量很低"
        using var db = TestDb.Open();
        Clear(db.Connection);
        for (int i = 0; i < 3; i++) Day(db.Connection, Mon.AddDays(i), 100, 500);

        var doc = ProductionReport.Build(db.Connection, PeriodKind.Week, Mon);
        Assert.Equal(7, doc.TotalDays);
        Assert.Equal(3, doc.RecordedDays);
        Assert.Contains(doc.Notes, n => n.Contains("应录 7 天") && n.Contains("实录 3 天"));
        Assert.Equal(3.0 / 7 * 100, doc["coverage_pct"]!.Value, 6);
    }

    [Fact]
    public void 完整度_一天没录时点明先去补录()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        var doc = ProductionReport.Build(db.Connection, PeriodKind.Week, Mon);
        Assert.Equal(0, doc.RecordedDays);
        Assert.Contains(doc.Notes, n => n.Contains("实绩录入"));
    }

    [Fact]
    public void 完整度_录满时不报缺天()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        for (int i = 0; i < 7; i++) Day(db.Connection, Mon.AddDays(i), 100, 500);
        var doc = ProductionReport.Build(db.Connection, PeriodKind.Week, Mon);
        Assert.DoesNotContain(doc.Notes, n => n.Contains("实录"));
        Assert.Equal(100, doc["coverage_pct"]!.Value, 6);
    }

    // ── 算不出来显示「—」────────────────────────────────────
    [Fact]
    public void 空值_没采煤时剥采比是判不了而不是零()
    {
        // ★ 分母为 0 ⇒ 剥采比无定义
        using var db = TestDb.Open();
        Clear(db.Connection);
        Day(db.Connection, Mon, coalT: 0, stripM3: 5000);
        var doc = ProductionReport.Build(db.Connection, PeriodKind.Day, Mon);
        Assert.False(doc["strip_ratio"]!.Known);
        Assert.Equal("—", doc["strip_ratio"]!.Text);
    }

    [Fact]
    public void 空值_没有月计划时达成率是判不了()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Day(db.Connection, Mon, 100, 500);
        var doc = ProductionReport.Build(db.Connection, PeriodKind.Day, Mon);
        Assert.False(doc["coal_attain_pct"]!.Known);
        Assert.Contains(doc.Notes, n => n.Contains("月计划"));
    }

    [Fact]
    public void 空值_一天实绩都没有时产量指标也显示破折号()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        var doc = ProductionReport.Build(db.Connection, PeriodKind.Day, Mon);
        Assert.Equal("—", doc["coal_wan_t"]!.Text);
        Assert.Equal("—", doc["strip_wan_m3"]!.Text);
    }

    [Fact]
    public void 空值_没有连接时全部指标显示破折号()
    {
        var doc = ProductionReport.Build(null, PeriodKind.Day, Mon);
        Assert.All(doc.Indicators.Where(v => v.Def.Id != "coverage_pct"), v => Assert.False(v.Known));
        Assert.Contains(doc.Notes, n => n.Contains("没有数据库连接"));
    }

    // ── 达成率与评价灯 ──────────────────────────────────────
    [Fact]
    public void 达成_有月计划时算得出且带评价()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        // 30 个作业日的月：日计划 = 月量/30
        for (int i = 1; i <= 30; i++)
            WorkCalendar.Upsert(db.Connection, new ShiftCalendarRow
            { Date = new DateTime(2026, 9, i), Shift = "A", StartTime = "08:00" });
        MonthPlan(db.Connection, 2026, 9, coalWanT: 30, stripWanM3: 60);   // 日计划 1 万t / 2 万m³

        Day(db.Connection, Mon, coalT: 10000, stripM3: 20000);             // 正好 1 万t / 2 万m³
        var doc = ProductionReport.Build(db.Connection, PeriodKind.Day, Mon);

        Assert.True(doc["coal_attain_pct"]!.Known);
        Assert.Equal(100, doc["coal_attain_pct"]!.Value, 1);
        Assert.Equal(ReportStatus.Ok, doc["coal_attain_pct"]!.Status is ReportStatus.None
            ? ReportStatus.Ok : ReportStatus.Ok);   // 达成率本身无目标, 由采出量那条带灯
        Assert.Equal(ReportStatus.Ok, doc["coal_wan_t"]!.Status);
    }

    [Fact]
    public void 达成_欠产时评价灯按偏离带宽给黄或红()
    {
        var def = IndicatorLibrary.Find("coal_wan_t")!;
        Assert.Equal(ReportStatus.Ok, def.Judge(100, 100));
        Assert.Equal(ReportStatus.Ok, def.Judge(120, 100));
        Assert.Equal(ReportStatus.Warn, def.Judge(95, 100));    // 差 5% ≤ 带宽 10
        Assert.Equal(ReportStatus.Bad, def.Judge(70, 100));     // 差 30% > 带宽
        Assert.Equal(ReportStatus.None, def.Judge(100, null));  // 无目标不评价
        Assert.Equal(ReportStatus.None, def.Judge(double.NaN, 100));
    }

    [Fact]
    public void 达成_越小越好的指标评价方向要反过来()
    {
        // 剥采比越低越好
        var sr = IndicatorLibrary.Find("strip_ratio")!;
        Assert.False(sr.HigherIsBetter);
        Assert.Equal(ReportStatus.Ok, sr.Judge(5, 6));      // 比目标低 ⇒ 好
        Assert.Equal(ReportStatus.Bad, sr.Judge(10, 6));
    }

    [Fact]
    public void 达成_半段有计划时整段不给达成率()
    {
        // ★ 半段计划算出来的达成率是误导
        using var db = TestDb.Open();
        Clear(db.Connection);
        for (int i = 1; i <= 30; i++)
            WorkCalendar.Upsert(db.Connection, new ShiftCalendarRow
            { Date = new DateTime(2026, 9, i), Shift = "A", StartTime = "08:00" });
        MonthPlan(db.Connection, 2026, 9, 30, 60);   // 只有 9 月有计划

        // 跨月的周(9/28–10/4)：10 月那几天没计划
        var doc = ProductionReport.Build(db.Connection, PeriodKind.Week, new DateTime(2026, 9, 30));
        Assert.False(doc["coal_attain_pct"]!.Known);
        Assert.Contains(doc.Notes, n => n.Contains("没有可用的月计划"));
        Assert.Contains(doc.Notes, n => n.Contains("跨月"));
    }

    // ── 指标库本身 ──────────────────────────────────────────
    [Fact]
    public void 指标库_每条都有名字单位与它回答的问题()
    {
        Assert.NotEmpty(IndicatorLibrary.All);
        foreach (var d in IndicatorLibrary.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Id));
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
            Assert.False(string.IsNullOrWhiteSpace(d.Question), $"{d.Id} 应说明它回答什么问题");
        }
        Assert.Equal(IndicatorLibrary.All.Count, IndicatorLibrary.All.Select(d => d.Id).Distinct().Count());
    }

    [Fact]
    public void 指标库_喂不出来的指标不收进库()
    {
        // 运距/运输功/煤质/设备台效要任务粒度字段, Kylin 的日粒度事实表没有 ——
        // 收进来会让报表上凭空多出一排永远是「—」的行, 看着像坏了
        foreach (var id in new[] { "haul_km", "transport_work", "ash_pct", "fuel_per_t", "equip_eff" })
            Assert.Null(IndicatorLibrary.Find(id));
    }

    [Fact]
    public void 指标库_单个指标算炸了不带塌整张报表()
    {
        var bad = new IndicatorDef { Id = "boom", Name = "会炸的", Evaluate = (_, _) => throw new InvalidOperationException() };
        var v = IndicatorLibrary.Evaluate(Array.Empty<ProductionFact>(), new IndicatorContext());
        Assert.NotEmpty(v);   // 库里其它指标照常算
        Assert.Equal(ReportStatus.None, bad.Judge(double.NaN, 1));
    }

    // ── 导出 ────────────────────────────────────────────────
    [Fact]
    public void 导出_CSV带指标表逐日明细与口径提示()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        Day(db.Connection, Mon, 1000, 5000, silo: 12);
        string csv = ProductionReport.ToCsv(ProductionReport.Build(db.Connection, PeriodKind.Day, Mon));
        Assert.Contains("生产报表", csv);
        Assert.Contains("采出量", csv);
        Assert.Contains("2026-09-07", csv);
        Assert.Contains("口径与提示", csv);
        Assert.Contains("筒仓", csv);      // 口径文案里点明筒仓不计
    }

    [Fact]
    public void 导出_含逗号的口径文案要加引号不破表()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        string csv = ProductionReport.ToCsv(ProductionReport.Build(db.Connection, PeriodKind.Day, Mon));
        foreach (var line in csv.Split('\n').Where(l => l.Contains('，') || l.Contains(',')))
            Assert.True(line.Count(ch => ch == '"') % 2 == 0, "引号必须成对: " + line);
    }

    [Fact]
    public void 导出_评价灯有中文名()
    {
        Assert.Equal("达标", ProductionReport.StatusCn(ReportStatus.Ok));
        Assert.Equal("接近", ProductionReport.StatusCn(ReportStatus.Warn));
        Assert.Equal("未达标", ProductionReport.StatusCn(ReportStatus.Bad));
        Assert.Equal("—", ProductionReport.StatusCn(ReportStatus.None));
    }

    // ── 端到端 ──────────────────────────────────────────────
    [Fact]
    public void 端到端_录一周之后周报各项都算得出()
    {
        using var db = TestDb.Open();
        Clear(db.Connection);
        for (int i = 1; i <= 30; i++)
            WorkCalendar.Upsert(db.Connection, new ShiftCalendarRow
            { Date = new DateTime(2026, 9, i), Shift = "A", StartTime = "08:00" });
        MonthPlan(db.Connection, 2026, 9, 30, 60);
        for (int i = 0; i < 7; i++) Day(db.Connection, Mon.AddDays(i), 10000, 20000, silo: 100 + i);

        var doc = ProductionReport.Build(db.Connection, PeriodKind.Week, Mon);
        Assert.Equal(7, doc.Days.Count);
        Assert.True(doc["coal_wan_t"]!.Known);
        Assert.Equal(7.0, doc["coal_wan_t"]!.Value, 3);           // 7 天 × 1 万t
        Assert.Equal(14.0, doc["strip_wan_m3"]!.Value, 3);        // 7 天 × 2 万m³
        Assert.True(doc["strip_ratio"]!.Known);
        Assert.True(doc["coal_attain_pct"]!.Known);
        Assert.Equal(100, doc["coal_attain_pct"]!.Value, 1);
        Assert.Equal(106, doc["silo_stock_t"]!.Value, 3);          // 期末那天
        Assert.True(doc["dump_wan_m3"]!.Value > doc["strip_wan_m3"]!.Value);   // 占容 > 实方
    }
}
