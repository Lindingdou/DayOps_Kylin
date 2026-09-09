using System.Collections.Generic;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 导入路径改成"批量预取"之后的行为守卫。
///
/// 背景: 各 ImportX 原本逐行查库(查外键、查码表、查是否已存在), 每行 2~4 次往返。
/// 库在进程内时无所谓, 一旦数据库挪到远程, 每次往返都是毫秒级网络开销。
/// 现在改成进循环前一次性拉进内存、循环里查字典, 每行只剩写入那一次往返。
///
/// 这个改法有一处真实风险: 预取集合是循环开始前的快照, 如果同一份 CSV 里出现重复键,
/// 后一行看不到前一行刚写进去的数据 —— 而原来"每行现查数据库"是看得到的。
/// 所以每个改过的导入都必须在写入成功后把键补进集合。漏补就会重复插入。
/// 下面这些用例就是打这一点的; 另外覆盖外键降级和父行缺失两条原有语义。
/// </summary>
public class ImportBatchPrefetchTests
{
    private static IReadOnlyList<IReadOnlyDictionary<string, string>> Rows(
        params (string k, string v)[][] rows)
    {
        var list = new List<IReadOnlyDictionary<string, string>>();
        foreach (var kv in rows)
        {
            var d = new Dictionary<string, string>();
            foreach (var (k, v) in kv) d[k] = v;
            list.Add(d);
        }
        return list;
    }

    private static GeoDatabase Seeded()
    {
        var db = TestDb.Open();
        // 与 ImportSchemaConsistencyTests 同法: 关 FK 以隔离本测关注点(键去重), 免父行缺失干扰。
        using var p = db.Connection.CreateCommand();
        p.CommandText = "PRAGMA foreign_keys=OFF";
        p.ExecuteNonQuery();
        return db;
    }

    [Fact]
    public void 同份CSV内重复键_第二行按已存在处理而非重复插入()
    {
        using var db = Seeded();
        var two = Rows(
            new[] { ("equipment_id", "PF-DUP"), ("date", "2099-12-31"), ("shift", "A"), ("output_m3", "100") },
            new[] { ("equipment_id", "PF-DUP"), ("date", "2099-12-31"), ("shift", "A"), ("output_m3", "200") });

        var o = GeoDataQueries.ImportProductionRecords(db.Connection, two, overwrite: false);

        Assert.Equal(1, o.Inserted);          // 只插一条
        Assert.Equal(1, o.Skipped);           // 第二条认出重复被跳过
        Assert.Equal(0, o.Errors);
        Assert.Equal(1, db.ScalarLong(
            "SELECT COUNT(*) FROM production_record WHERE equipment_id='PF-DUP' AND date='2099-12-31' AND shift='A'"));
    }

    [Fact]
    public void 同份CSV内重复键_overwrite时第二行改为更新()
    {
        using var db = Seeded();
        var two = Rows(
            new[] { ("year", "2099"), ("month", "7"), ("plan_coal_wan_t", "10") },
            new[] { ("year", "2099"), ("month", "7"), ("plan_coal_wan_t", "20") });

        var o = GeoDataQueries.ImportMonthlyPlans(db.Connection, two, overwrite: true);

        Assert.Equal(1, o.Inserted);
        Assert.Equal(1, o.Updated);           // 第二条走更新而不是再插一条
        Assert.Equal(1, db.ScalarLong("SELECT COUNT(*) FROM monthly_plan WHERE year=2099 AND month=7"));
    }

    [Fact]
    public void 月度KPI同份CSV重复键_不重复插入()
    {
        using var db = Seeded();
        var two = Rows(
            new[] { ("equipment_id", "KPI-DUP"), ("year", "2099"), ("month", "3"), ("work_hours", "100") },
            new[] { ("equipment_id", "KPI-DUP"), ("year", "2099"), ("month", "3"), ("work_hours", "200") });

        var o = GeoDataQueries.ImportKpiMonthly(db.Connection, two, overwrite: false);

        Assert.Equal(1, o.Inserted);
        Assert.Equal(1, o.Skipped);
        Assert.Equal(1, db.ScalarLong(
            "SELECT COUNT(*) FROM equipment_kpi_monthly WHERE equipment_id='KPI-DUP' AND year=2099 AND month=3"));
    }

    [Fact]
    public void 设备台账同份CSV重复键_不重复插入()
    {
        using var db = Seeded();
        var two = Rows(
            new[] { ("equipment_id", "EQ-DUP"), ("category", "电铲") },
            new[] { ("equipment_id", "EQ-DUP"), ("category", "卡车") });

        var o = GeoDataQueries.ImportEquipmentLedger(db.Connection, two, overwrite: false);

        Assert.Equal(1, o.Inserted);
        Assert.Equal(1, o.Skipped);
        Assert.Equal(1, db.ScalarLong("SELECT COUNT(*) FROM equipment WHERE equipment_id='EQ-DUP'"));
    }

    [Fact]
    public void 运输道路同份CSV重复键_不重复插入()
    {
        using var db = Seeded();
        var two = Rows(
            new[] { ("road_id", "RD-DUP"), ("name", "甲"), ("road_type", "main"), ("length_m", "100") },
            new[] { ("road_id", "RD-DUP"), ("name", "乙"), ("road_type", "main"), ("length_m", "200") });

        var o = GeoDataQueries.ImportHaulRoads(db.Connection, two, overwrite: false);

        Assert.Equal(1, o.Inserted);
        Assert.Equal(1, o.Skipped);
        Assert.Equal(1, db.ScalarLong("SELECT COUNT(*) FROM haul_road WHERE road_id='RD-DUP'"));
    }

    // ── 预取不得改变原有的两条降级语义 ────────────────────────────────────────

    [Fact]
    public void 煤质化验_未知煤种码降级为NULL而不是整行失败()
    {
        using var db = Seeded();
        using (var bh = db.Connection.CreateCommand())
        {
            bh.CommandText = "INSERT INTO borehole (hole_id, x, y) VALUES ('ZK-PF-1', 1000, 2000)";
            bh.ExecuteNonQuery();
        }

        var o = GeoDataQueries.ImportCoalSamples(db.Connection, Rows(
            new[] { ("hole_id", "ZK-PF-1"), ("seam_code", "4-1"), ("depth_from", "120.5"),
                    ("coal_type", "这不是有效煤种码") }), overwrite: false);

        Assert.Equal(1, o.Inserted);
        Assert.Equal(0, o.Errors);            // 无损降级: 不因未知码丢整条化验
        Assert.Equal(1, db.ScalarLong(
            "SELECT COUNT(*) FROM coal_sample WHERE seam_code='4-1' AND depth_from=120.5 AND coal_type IS NULL"));
    }

    [Fact]
    public void 煤质化验_孔号在库中不存在时计入错误()
    {
        using var db = Seeded();

        var o = GeoDataQueries.ImportCoalSamples(db.Connection, Rows(
            new[] { ("hole_id", "根本没有这个孔"), ("seam_code", "4-1"), ("depth_from", "10") }), overwrite: false);

        Assert.Equal(0, o.Inserted);
        Assert.Equal(1, o.Errors);
    }

    [Fact]
    public void 见煤成果_孔号不存在时计入错误()
    {
        using var db = Seeded();

        var o = GeoDataQueries.ImportSeamResults(db.Connection, Rows(
            new[] { ("hole_id", "根本没有这个孔"), ("seam_code", "4-1") }), overwrite: false);

        Assert.Equal(0, o.Inserted);
        Assert.Equal(1, o.Errors);
    }
}
