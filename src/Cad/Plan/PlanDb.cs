using System;
using System.Collections.Generic;
using System.Data.Common;

namespace PitMine3D.Kylin.Cad.Plan;

/// <summary>规划模块直接读库的几条小查询（原走 GeoDataBase 服务；这里按同一表同一语义）。</summary>
public static class PlanDb
{
    /// <summary>
    /// 边坡设计（slope_design）：帮别 / 帮型 / β(最终帮坡角，缺则工作帮坡角) / 安全系数 / 内摩擦角 / 生效日期。
    /// 只取现行（effective_to 为空或未到期），与原 <c>ISlopeDesignService.CurrentDesigns</c> 同口径。
    /// </summary>
    public static List<(string side, string type, double beta, double? f, double? phi, string from)> LoadSlopeDesigns(DbConnection conn)
    {
        var rows = new List<(string, string, double, double?, double?, string)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COALESCE(side_name,''), COALESCE(side_type,''), final_slope_angle_deg, working_slope_angle_deg,
                                   safety_factor, friction_angle_deg, COALESCE(effective_from,'')
                            FROM slope_design
                            WHERE effective_to IS NULL OR effective_to = '' OR effective_to >= CAST(CURRENT_DATE AS TEXT)
                            ORDER BY side_name";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            double? fin = rd.IsDBNull(2) ? null : Convert.ToDouble(rd.GetValue(2));
            double? wk = rd.IsDBNull(3) ? null : Convert.ToDouble(rd.GetValue(3));
            double? f = rd.IsDBNull(4) ? null : Convert.ToDouble(rd.GetValue(4));
            double? phi = rd.IsDBNull(5) ? null : Convert.ToDouble(rd.GetValue(5));
            double beta = fin ?? wk ?? 0;
            rows.Add((rd.GetString(0), rd.GetString(1), beta, f, phi, rd.GetValue(6)?.ToString() ?? ""));
        }
        return rows;
    }
}

/// <summary>
/// 可采区域边界表 <c>mineable_region</c> 的读写（原 <c>IMineableRegionService</c> 的 All/Insert/Update/Delete 四件；
/// 「采场/排土场圈定」窗口专用）。列：id / name / category / points_json / visible / note / color（created/updated 交给库默认与触发器）。
/// 没接库时 All 返回空表、写入返回错误文本，不抛。
/// </summary>
public static class MineableRegionRepo
{
    private static string L(string? s) => s == null ? "NULL" : "'" + s.Replace("'", "''") + "'";

    public static List<Data.Entities.MineableRegion> All(DbConnection? conn)
    {
        var list = new List<Data.Entities.MineableRegion>();
        if (conn == null) return list;
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, name, category, points_json, visible, note, color FROM mineable_region ORDER BY id";
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                list.Add(new Data.Entities.MineableRegion
                {
                    Id = Convert.ToInt64(rd.GetValue(0)),
                    Name = rd.IsDBNull(1) ? "" : rd.GetValue(1)?.ToString() ?? "",
                    Category = rd.IsDBNull(2) || string.IsNullOrWhiteSpace(rd.GetValue(2)?.ToString()) ? Data.Entities.MineableRegion.CatMineable : rd.GetValue(2)!.ToString()!.Trim(),
                    PointsJson = rd.IsDBNull(3) ? "[]" : rd.GetValue(3)?.ToString() ?? "[]",
                    Visible = rd.IsDBNull(4) ? 1 : Convert.ToInt64(rd.GetValue(4)),
                    Note = rd.IsDBNull(5) ? null : rd.GetValue(5)?.ToString(),
                    Color = rd.IsDBNull(6) ? null : rd.GetValue(6)?.ToString(),
                });
        }
        catch { }
        return list;
    }

    /// <summary>新建；返回新 id（失败 0，err 带原因）。</summary>
    public static long Insert(DbConnection? conn, Data.Entities.MineableRegion e, out string err)
    {
        err = "";
        if (conn == null) { err = "没有数据库连接"; return 0; }
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO mineable_region (name, category, points_json, visible, note, color) VALUES ("
                    + L(e.Name) + ", " + L(e.Category) + ", " + L(e.PointsJson) + ", " + e.Visible + ", " + L(e.Note) + ", " + L(string.IsNullOrWhiteSpace(e.Color) ? null : e.Color!.Trim().TrimStart('#')) + ")";
                cmd.ExecuteNonQuery();
            }
            using var q = conn.CreateCommand();
            q.CommandText = "SELECT MAX(id) FROM mineable_region";
            object? v = q.ExecuteScalar();
            e.Id = v == null || v is DBNull ? 0 : Convert.ToInt64(v);
            return e.Id;
        }
        catch (Exception ex) { err = ex.Message; return 0; }
    }

    public static string Update(DbConnection? conn, Data.Entities.MineableRegion e)
    {
        if (conn == null) return "没有数据库连接";
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE mineable_region SET name = " + L(e.Name) + ", category = " + L(e.Category) + ", points_json = " + L(e.PointsJson)
                + ", visible = " + e.Visible + ", note = " + L(e.Note) + ", color = " + L(string.IsNullOrWhiteSpace(e.Color) ? null : e.Color!.Trim().TrimStart('#'))
                + " WHERE id = " + e.Id;
            cmd.ExecuteNonQuery();
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string Delete(DbConnection? conn, long id)
    {
        if (conn == null) return "没有数据库连接";
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM mineable_region WHERE id = " + id;
            cmd.ExecuteNonQuery();
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    }
}
