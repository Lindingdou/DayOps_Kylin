using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>§四/§八 查询分析回归（对 SQLite 种子库）。</summary>
public class GeoDataQueriesTests
{
    [Fact]
    public void Equipment_roster_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var r = GeoDataQueries.GetEquipmentRoster(db.Connection);
        Assert.True(r.Total >= 5, $"设备总数 {r.Total}");
        Assert.NotEmpty(r.ByCategory);
        Assert.Equal(r.Total, Sum(r));               // 分类计数之和 = 总数
    }

    private static int Sum(GeoDataQueries.EquipmentRoster r)
    {
        int s = 0; foreach (var c in r.ByCategory) s += c.Count; return s;
    }

    [Fact]
    public void Production_stats_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var s = GeoDataQueries.GetProductionStats(db.Connection);
        Assert.True(s.Records > 0, "生产记录数");
        Assert.True(s.OutputM3 > 0, "总产量");
        Assert.InRange(s.UtilizationPct, 0, 100);    // 作业率百分比合理
    }

    [Fact]
    public void Capacity_ranking_sorted_desc()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = GeoDataQueries.GetCapacityRanking(db.Connection, 5);
        Assert.NotEmpty(rows);
        for (int i = 1; i < rows.Count; i++)
            Assert.True(rows[i - 1].TotalOutputM3 >= rows[i].TotalOutputM3, "按产量降序");
    }

    [Fact]
    public void Fault_stats_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var f = GeoDataQueries.GetFaultStats(db.Connection);
        Assert.True(f.Events > 0, "故障事件数");
        Assert.True(f.DowntimeHours >= 0);
        Assert.True(f.Unresolved <= f.Events);           // 未修复 ≤ 总数
    }

    [Fact]
    public void Kpi_stats_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var k = GeoDataQueries.GetKpiStats(db.Connection);
        Assert.True(k.Records > 0, "KPI 记录数");
        Assert.InRange(k.AvgAvailabilityPct, 0, 100);
        Assert.InRange(k.AvgUtilizationPct, 0, 100);
    }

    [Fact]
    public void Borehole_stats_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var b = GeoDataQueries.GetBoreholeStats(db.Connection);
        Assert.True(b.Holes > 100, $"钻孔数 {b.Holes}");     // 种子 241 孔
        Assert.True(b.TotalDepthM > 0);
        Assert.True(b.SeamResults > 0, "见煤结果");
        Assert.NotEmpty(b.ByCategory);
    }

    [Fact]
    public void Coal_quality_stats_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var q = GeoDataQueries.GetCoalQualityStats(db.Connection);
        Assert.True(q.Samples > 100, $"煤样数 {q.Samples}");   // 种子 257 样
        Assert.True(q.Seams > 0, "煤层数");
        Assert.InRange(q.AvgAshPct, 0, 100);                   // 灰分百分比合理
        Assert.True(q.AvgCalorificMJ > 0, "发热量");
    }

    [Fact]
    public void Coal_seams_from_seed()
    {
        using var db = GeoDatabase.OpenSeeded();
        var seams = GeoDataQueries.GetCoalSeams(db.Connection);
        Assert.NotEmpty(seams);                                // 种子 7 煤层
    }
}
