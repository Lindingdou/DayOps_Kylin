using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// §四/§八 数据查询/分析（读 SQLite 数据基座）。忠实原 GeoDataBase 各 Service 的读侧口径,
/// 以只读聚合报表形式呈现（不做 CRUD 对话框）。纯查询、可对种子库单测。
/// </summary>
public static class GeoDataQueries
{
    public sealed record CategoryCount(string Category, int Count);
    public sealed record EquipmentRoster(int Total, IReadOnlyList<CategoryCount> ByCategory, int InService);

    /// <summary>设备台账概览：总数 / 分类计数 / 在役数。</summary>
    public static EquipmentRoster GetEquipmentRoster(SqliteConnection conn)
    {
        var byCat = new List<CategoryCount>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT category, COUNT(*) FROM equipment GROUP BY category ORDER BY COUNT(*) DESC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) byCat.Add(new CategoryCount(rd.IsDBNull(0) ? "(未分类)" : rd.GetString(0), rd.GetInt32(1)));
        }
        int total = (int)Scalar(conn, "SELECT COUNT(*) FROM equipment");
        int inSvc = (int)Scalar(conn, "SELECT COUNT(*) FROM equipment WHERE status IN ('在役','运行','正常','服役') OR status IS NULL");
        return new EquipmentRoster(total, byCat, inSvc);
    }

    public sealed record ProductionStats(int Records, double OutputM3, double WorkHours, double FaultHours, double UtilizationPct);

    /// <summary>生产数据统计：记录数 / 总产量 / 工时 / 故障工时 / 作业率(工时/(工时+故障))。</summary>
    public static ProductionStats GetProductionStats(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*), COALESCE(SUM(output_m3),0), COALESCE(SUM(work_hours),0), COALESCE(SUM(fault_hours),0)
                            FROM production_record";
        using var rd = cmd.ExecuteReader();
        rd.Read();
        int n = rd.GetInt32(0);
        double outp = rd.GetDouble(1), wh = rd.GetDouble(2), fh = rd.GetDouble(3);
        double util = (wh + fh) > 1e-9 ? wh / (wh + fh) * 100.0 : 0;
        return new ProductionStats(n, outp, wh, fh, util);
    }

    public sealed record CapacityRow(string EquipmentId, string Model, double TotalOutputM3);

    /// <summary>产能排名：按设备累计产量降序(join 型号)。取前 topN。</summary>
    public static List<CapacityRow> GetCapacityRanking(SqliteConnection conn, int topN = 10)
    {
        var rows = new List<CapacityRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.equipment_id, COALESCE(e.model,''), SUM(c.output_m3) AS tot
                            FROM capacity_monthly c LEFT JOIN equipment e ON e.equipment_id = c.equipment_id
                            GROUP BY c.equipment_id ORDER BY tot DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@n", topN);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new CapacityRow(rd.GetString(0), rd.GetString(1), rd.GetDouble(2)));
        return rows;
    }

    public sealed record FaultStats(int Events, double DowntimeHours, int Unresolved, string TopType, int TopTypeCount);

    /// <summary>故障分析（设备状态·故障报修）：事件数 / 累计停机时 / 未修复数 / 最多故障类型。</summary>
    public static FaultStats GetFaultStats(SqliteConnection conn)
    {
        int events = (int)Scalar(conn, "SELECT COUNT(*) FROM fault_event");
        double downtime = ScalarDouble(conn, "SELECT COALESCE(SUM(duration_hours),0) FROM fault_event");
        int unresolved = (int)Scalar(conn, "SELECT COUNT(*) FROM fault_event WHERE is_resolved = 0");
        string topType = "(无)"; int topCount = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(fault_type,'(未分类)'), COUNT(*) c FROM fault_event GROUP BY fault_type ORDER BY c DESC LIMIT 1";
            using var rd = cmd.ExecuteReader();
            if (rd.Read()) { topType = rd.GetString(0); topCount = rd.GetInt32(1); }
        }
        return new FaultStats(events, downtime, unresolved, topType, topCount);
    }

    public sealed record KpiStats(int Records, double AvgAvailabilityPct, double AvgUtilizationPct, int LatestYear, int LatestMonth);

    /// <summary>KPI 分析：equipment_kpi_monthly 平均可用率/利用率 + 最新期。</summary>
    public static KpiStats GetKpiStats(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*), COALESCE(AVG(availability),0), COALESCE(AVG(utilization_rate),0),
                            COALESCE(MAX(year),0), COALESCE(MAX(month),0) FROM equipment_kpi_monthly";
        using var rd = cmd.ExecuteReader();
        rd.Read();
        int n = rd.GetInt32(0);
        double av = rd.GetDouble(1), ut = rd.GetDouble(2);
        // availability/utilization 可能存为 0..1 或 0..100，统一按 <=1 视为比率×100。
        double avPct = av <= 1.0 ? av * 100 : av;
        double utPct = ut <= 1.0 ? ut * 100 : ut;
        return new KpiStats(n, avPct, utPct, rd.GetInt32(3), rd.GetInt32(4));
    }

    public sealed record BoreholeStats(int Holes, double TotalDepthM, double AvgDepthM, int SeamResults, IReadOnlyList<CategoryCount> ByCategory);

    /// <summary>钻孔管理概览：孔数 / 总孔深 / 均深 / 见煤结果数 / 按类别。</summary>
    public static BoreholeStats GetBoreholeStats(SqliteConnection conn)
    {
        int holes = (int)Scalar(conn, "SELECT COUNT(*) FROM borehole");
        double totDepth = ScalarDouble(conn, "SELECT COALESCE(SUM(depth_total),0) FROM borehole");
        int seamRes = (int)Scalar(conn, "SELECT COUNT(*) FROM borehole_seam_result");
        var byCat = new List<CategoryCount>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(category,'(未分类)'), COUNT(*) c FROM borehole GROUP BY category ORDER BY c DESC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) byCat.Add(new CategoryCount(rd.GetString(0), rd.GetInt32(1)));
        }
        return new BoreholeStats(holes, totDepth, holes > 0 ? totDepth / holes : 0, seamRes, byCat);
    }

    public sealed record CoalQualityStats(int Samples, int Seams, double AvgAshPct, double AvgVolatilePct, double AvgCalorificMJ, double AvgSulfurPct);

    /// <summary>煤质统计：样本数 / 煤层数 / 平均 灰分Ad / 挥发分Vdaf / 发热量Qnet / 全硫St。</summary>
    public static CoalQualityStats GetCoalQualityStats(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*), COUNT(DISTINCT seam_code),
                            COALESCE(AVG(ad_raw),0), COALESCE(AVG(vdaf_raw),0),
                            COALESCE(AVG(qnet_ad),0), COALESCE(AVG(std_raw),0)
                            FROM coal_sample WHERE ad_raw IS NOT NULL";
        using var rd = cmd.ExecuteReader();
        rd.Read();
        return new CoalQualityStats(rd.GetInt32(0), rd.GetInt32(1), rd.GetDouble(2), rd.GetDouble(3), rd.GetDouble(4), rd.GetDouble(5));
    }

    public sealed record SeamRow(string SeamCode, string Name, int SampleCount);

    /// <summary>煤层管理：各煤层定义 + 煤样计数。</summary>
    public static List<SeamRow> GetCoalSeams(SqliteConnection conn)
    {
        var rows = new List<SeamRow>();
        using var cmd = conn.CreateCommand();
        // coal_seam_def.code ↔ coal_sample.seam_code；名称在 coal_seam_def.name。
        cmd.CommandText = @"SELECT d.code, d.name,
                            (SELECT COUNT(*) FROM coal_sample s WHERE s.seam_code = d.code)
                            FROM coal_seam_def d ORDER BY d.sort_order, d.code";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new SeamRow(rd.GetString(0), rd.IsDBNull(1) ? "" : rd.GetString(1), rd.GetInt32(2)));
        return rows;
    }

    private static double ScalarDouble(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == null || v is System.DBNull ? 0 : System.Convert.ToDouble(v);
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == null || v is System.DBNull ? 0 : System.Convert.ToInt64(v);
    }
}
