using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data;

/// <summary>一次落库的账（忠实原 DumpStripSaveReport）：写进去几行、被唯一索引挡掉几行、第一条错误。</summary>
public readonly record struct DumpStripSaveReport(int Inserted, int Failed, string? FirstError)
{
    public bool AllOk => Failed == 0;
}

/// <summary>
/// dump_strip 表读写（忠实原 GeoDataBase.Domain.Services.DumpStripService，SqlLib 仓储改手写 SQL）。
/// 与一般 CRUD 的差别只有一条 —— <see cref="ReplaceForRegion"/>：切分参数一改，编号/幅数/带数全变，
/// 旧行留着就是两套网格叠在一张表里，所以按（排土场, 方案）先清后写；被唯一索引挡掉的行<b>记账，不静默吞</b>。
/// </summary>
public static class DumpStripRepo
{
    private const string Cols = "id, region_id, region_name, category, code, level_index, panel_index, panel_count, step_index, sub_index, sub_count, "
                              + "crest_z, toe_z, bench_height_m, strike_len_m, strip_width_m, capacity_m3, centroid_x, centroid_y, centroid_z, "
                              + "crest_json, toe_json, entity_handle, design_version, notes";

    public static List<DumpStrip> All(DbConnection? conn) => Query(conn, "1=1 ORDER BY region_id, level_index, panel_index, step_index, sub_index");

    public static List<DumpStrip> ByRegion(DbConnection? conn, long regionId, string? designVersion = null)
        => string.IsNullOrWhiteSpace(designVersion)
            ? Query(conn, $"region_id = {regionId} ORDER BY level_index, panel_index, step_index, sub_index")
            : Query(conn, $"region_id = {regionId} AND IFNULL(design_version,'') = {L(designVersion)} ORDER BY level_index, panel_index, step_index, sub_index");

    public static DumpStripSaveReport ReplaceForRegion(DbConnection? conn, long regionId, string? designVersion, IReadOnlyList<DumpStrip> rows)
    {
        if (conn == null) return new DumpStripSaveReport(0, rows?.Count ?? 0, "没有数据库连接");
        try
        {
            Exec(conn, string.IsNullOrWhiteSpace(designVersion)
                ? $"DELETE FROM dump_strip WHERE region_id = {regionId}"
                : $"DELETE FROM dump_strip WHERE region_id = {regionId} AND IFNULL(design_version,'') = {L(designVersion)}");
        }
        catch (Exception ex) { return new DumpStripSaveReport(0, rows?.Count ?? 0, "清旧行失败: " + ex.Message); }

        int n = 0, bad = 0; string? first = null;
        foreach (var r in rows ?? Array.Empty<DumpStrip>())
        {
            if (r == null) continue;
            r.RegionId = regionId; r.DesignVersion = designVersion;
            try { Exec(conn, Insert(r)); n++; }
            catch (Exception ex) { bad++; first ??= $"{r.Code}: {ex.Message}"; }
        }
        return new DumpStripSaveReport(n, bad, first);
    }

    private static string Insert(DumpStrip r)
        => "INSERT INTO dump_strip (region_id, region_name, category, code, level_index, panel_index, panel_count, step_index, sub_index, sub_count, "
         + "crest_z, toe_z, bench_height_m, strike_len_m, strip_width_m, capacity_m3, centroid_x, centroid_y, centroid_z, crest_json, toe_json, entity_handle, design_version, notes) VALUES ("
         + $"{r.RegionId}, {L(r.RegionName)}, {L(r.Category)}, {L(r.Code)}, {r.LevelIndex}, {r.PanelIndex}, {r.PanelCount}, {r.StepIndex}, {r.SubIndex}, {r.SubCount}, "
         + $"{D(r.CrestZ)}, {D(r.ToeZ)}, {D(r.BenchHeightM)}, {D(r.StrikeLenM)}, {D(r.StripWidthM)}, {D(r.CapacityM3)}, {D(r.CentroidX)}, {D(r.CentroidY)}, {D(r.CentroidZ)}, "
         + $"{L(r.CrestJson)}, {L(r.ToeJson)}, {r.EntityHandle}, {L(r.DesignVersion)}, {L(r.Notes)})";

    private static List<DumpStrip> Query(DbConnection? conn, string where)
    {
        var list = new List<DumpStrip>();
        if (conn == null) return list;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT {Cols} FROM dump_strip WHERE {where}";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                int i = 0;
                list.Add(new DumpStrip
                {
                    Id = I(rd, i++), RegionId = I(rd, i++), RegionName = S(rd, i++), Category = S(rd, i++), Code = S(rd, i++),
                    LevelIndex = I(rd, i++), PanelIndex = I(rd, i++), PanelCount = I(rd, i++), StepIndex = I(rd, i++), SubIndex = I(rd, i++), SubCount = I(rd, i++),
                    CrestZ = F(rd, i++), ToeZ = F(rd, i++), BenchHeightM = F(rd, i++), StrikeLenM = F(rd, i++), StripWidthM = F(rd, i++), CapacityM3 = F(rd, i++),
                    CentroidX = F(rd, i++), CentroidY = F(rd, i++), CentroidZ = F(rd, i++),
                    CrestJson = S(rd, i++), ToeJson = S(rd, i++), EntityHandle = I(rd, i++),
                    DesignVersion = rd.IsDBNull(i) ? null : rd.GetString(i), Notes = rd.IsDBNull(i + 1) ? null : rd.GetString(i + 1),
                });
            }
        }
        catch { /* 表还没建 / 连接断：按空清单处理，调用方按"没生成过"提示 */ }
        return list;
    }

    private static long I(DbDataReader rd, int i) => rd.IsDBNull(i) ? 0 : Convert.ToInt64(rd.GetValue(i), CultureInfo.InvariantCulture);
    private static double F(DbDataReader rd, int i) => rd.IsDBNull(i) ? 0 : Convert.ToDouble(rd.GetValue(i), CultureInfo.InvariantCulture);
    private static string S(DbDataReader rd, int i) => rd.IsDBNull(i) ? "" : Convert.ToString(rd.GetValue(i), CultureInfo.InvariantCulture) ?? "";
    private static string L(string? s) => s == null ? "NULL" : "'" + s.Replace("'", "''") + "'";
    private static string D(double v) => double.IsFinite(v) ? v.ToString("R", CultureInfo.InvariantCulture) : "0";
    private static void Exec(DbConnection conn, string sql) { using var cmd = conn.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
}
