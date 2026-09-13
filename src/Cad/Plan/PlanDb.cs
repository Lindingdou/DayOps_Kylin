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
