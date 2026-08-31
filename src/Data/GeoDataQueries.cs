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

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == null || v is System.DBNull ? 0 : System.Convert.ToInt64(v);
    }
}
