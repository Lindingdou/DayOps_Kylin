using PitMine3D.Kylin.Tests;
using System.Collections.Generic;
using PitMine3D.Kylin.Data;
using Xunit;

/// <summary>
/// 全部 ImportX 写入 对真实迁移+种子库 schema 一致性守卫 —— 各导入以最小有效行(唯一键避种子撞) 写入真实库,
/// 验 INSERT/UPDATE 列名与迁移一致(列不符→Import 内 try/catch 吞异常→Errors&gt;0→断言失败, 抓写路径迁移漂移)。
/// 各导入自有测用手写内存 schema, 测不到迁移一致性; 此测覆盖 11 导入(含 CoalSamples/SeamResults, 先插 borehole 父行)
/// + blast/equipment(GeoQueryAggregationTests 守卫) = 全 13 导入写路径迁移-schema 一致性守卫。
/// </summary>
public class ImportSchemaConsistencyTests
{
    private static IReadOnlyList<IReadOnlyDictionary<string, string>> Row(params (string k, string v)[] kv)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in kv) d[k] = v;
        return new List<IReadOnlyDictionary<string, string>> { d };
    }

    // 写路径成功(schema一致): 无异常(Errors=0) 且有写入(Inserted 或 Updated ≥ 1)。
    private static void AssertWritten(GeoDataQueries.ImportOutcome o)
    {
        Assert.Equal(0, o.Errors);
        Assert.True(o.Inserted + o.Updated >= 1, $"应有写入, 实 Ins{o.Inserted}/Upd{o.Updated}/Skip{o.Skipped}");
    }

    [Fact]
    public void All_imports_write_into_real_migrated_schema()
    {
        using var db = TestDb.Open();
        var c = db.Connection;
        // 本测只验"INSERT 列名与迁移一致"(列不符→no such column→Errors); FK 完整性是正交关注(生产层管),
        // 故关 FK 强制以隔离列检查(否则合规列的 INSERT 会因 FK 父行缺失误报)。
        using (var pragma = c.CreateCommand()) { pragma.CommandText = "PRAGMA foreign_keys=OFF"; pragma.ExecuteNonQuery(); }
        // coal_sample/seam_result 按 hole_id 查 borehole_id(NOT NULL), 故先插一个 borehole 父行。
        using (var bh = c.CreateCommand()) { bh.CommandText = "INSERT INTO borehole (hole_id, x, y) VALUES ('ZK-GUARD-B', 1000, 2000)"; bh.ExecuteNonQuery(); }
        // 唯一键(未来年月/GUARD 前缀 id)避免与种子撞导致 duplicate-skip, 确保真正 INSERT 触发 schema 校验。
        AssertWritten(GeoDataQueries.ImportProductionRecords(c,
            Row(("equipment_id", "1"), ("date", "2099-12-31"), ("shift", "zzz"), ("output_m3", "100")), false));
        AssertWritten(GeoDataQueries.ImportCapacityMonthly(c,
            Row(("equipment_id", "1"), ("year", "2099"), ("month", "12"), ("output_m3", "1000")), false));
        AssertWritten(GeoDataQueries.ImportFaultEvents(c,
            Row(("equipment_id", "1"), ("date", "2099-12-31"), ("fault_type", "zzz-guard"))));
        AssertWritten(GeoDataQueries.ImportKpiMonthly(c,
            Row(("equipment_id", "1"), ("year", "2099"), ("month", "12"), ("plan_hours", "300"), ("work_hours", "250"),
                ("fault_hours", "20"), ("availability", "0.9"), ("actual_run_rate", "0.8"), ("utilization_rate", "0.85")), false));
        AssertWritten(GeoDataQueries.ImportEquipmentLedger(c,
            Row(("equipment_id", "ZZ-GUARD-1"), ("category", "truck")), false));
        AssertWritten(GeoDataQueries.ImportObservationPoints(c,
            Row(("point_id", "OP-GUARD-1"), ("seam_code", "MG1"), ("x", "99999"), ("y", "99999")), false));
        AssertWritten(GeoDataQueries.ImportMonthlyPlans(c,
            Row(("year", "2099"), ("month", "12")), false));
        AssertWritten(GeoDataQueries.ImportHaulRoads(c,
            Row(("road_id", "R-GUARD-1"), ("name", "guard"), ("road_type", "main"), ("length_m", "500")), false));
        AssertWritten(GeoDataQueries.ImportSlopeDesigns(c,
            Row(("side_name", "GUARD-N"), ("side_type", "working"))));
        // 需 borehole 父(上面已插 ZK-GUARD-B) → 现可验其列-schema。
        AssertWritten(GeoDataQueries.ImportCoalSamples(c,
            Row(("hole_id", "ZK-GUARD-B"), ("seam_code", "MG1"), ("depth_from", "999.0")), false));
        AssertWritten(GeoDataQueries.ImportSeamResults(c,
            Row(("hole_id", "ZK-GUARD-B"), ("seam_code", "MG2")), false));
    }
}
