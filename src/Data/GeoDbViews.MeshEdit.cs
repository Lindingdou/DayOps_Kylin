using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 「三维地质建模 → 网格编辑」组用到的库查询：创建剖面·钻孔投影柱状(原 SectionCutWindow.CollectBoreholes 经
/// GeoDataBase BoreholeService.InBounds / GetSegments 读库) —— 剖面线两侧带宽内的钻孔 → 投影里程/偏距 + 岩性分层段。
/// </summary>
public static partial class GeoDbViews
{
    public sealed record SectionBoreRow(long Id, string HoleId, double X, double Y, double? ZCollar, double? DepthTotal);

    /// <summary>包围盒内有平面坐标的钻孔(原 BoreholeService.InBounds)。</summary>
    public static List<SectionBoreRow> SectionBoreholesInBounds(SqliteConnection conn, double xMin, double xMax, double yMin, double yMax)
    {
        const string sql = @"SELECT id AS Id, hole_id AS HoleId, x AS X, y AS Y, z_collar AS ZCollar, depth_total AS DepthTotal
                             FROM borehole WHERE x IS NOT NULL AND y IS NOT NULL
                               AND x BETWEEN @xMin AND @xMax AND y BETWEEN @yMin AND @yMax ORDER BY hole_id";
        return conn.Query<SectionBoreRow>(sql, new { xMin, xMax, yMin, yMax }).ToList();
    }

    /// <summary>钻孔岩性分层段(原 BoreholeService.GetSegments): 深度 从/到、层名、配色、类型(coal=煤层)。</summary>
    public static List<SectionBuilder.BoreSeg> SectionBoreholeSegments(SqliteConnection conn, long boreholeId)
    {
        const string sql = @"SELECT depth_from, depth_to, COALESCE(lithology_name, lithology_code, ''), color_hex, lithology_code
                             FROM borehole_lithology_segment WHERE borehole_id = @id ORDER BY depth_from, sort_order";
        var list = new List<SectionBuilder.BoreSeg>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", boreholeId);
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            string code = rd.IsDBNull(4) ? "" : rd.GetString(4);
            list.Add(new SectionBuilder.BoreSeg(rd.GetDouble(0), rd.GetDouble(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetString(3),
                code == "coal" ? "煤层" : code));
        }
        return list;
    }

    /// <summary>剖面线两侧带宽内的钻孔 → 投影(全局里程+偏距) + 分层段(忠实原 CollectBoreholes)。库未就绪/为空自动跳过。</summary>
    public static List<SectionBuilder.BoreProj> CollectSectionBoreholes(SqliteConnection? conn, double[] section, double band, out string warn)
    {
        warn = "";
        var list = new List<SectionBuilder.BoreProj>();
        if (conn == null || band <= 0) return list;
        var segs = SectionEngine.BuildSegmentTable(section, out _);
        if (segs.Count == 0) return list;
        try
        {
            double xMin = double.MaxValue, xMax = double.MinValue, yMin = double.MaxValue, yMax = double.MinValue;
            for (int i = 0; i + 2 < section.Length; i += 3)
            {
                xMin = Math.Min(xMin, section[i]); xMax = Math.Max(xMax, section[i]);
                yMin = Math.Min(yMin, section[i + 1]); yMax = Math.Max(yMax, section[i + 1]);
            }
            int noCollar = 0;
            foreach (var h in SectionBoreholesInBounds(conn, xMin - band, xMax + band, yMin - band, yMax + band))
            {
                if (h.ZCollar == null) { noCollar++; continue; }
                var proj = SectionBuilder.Project(segs, h.X, h.Y, band);
                if (proj == null) continue;
                var segList = SectionBoreholeSegments(conn, h.Id).Where(s => s.To > s.From).OrderBy(s => s.From).ToList();
                double depth = Math.Max(h.DepthTotal ?? 0, segList.Count > 0 ? segList.Max(s => s.To) : 0);
                if (depth <= 0) continue;
                list.Add(new SectionBuilder.BoreProj(h.HoleId, proj.Value.s, proj.Value.offset, h.ZCollar.Value, depth, segList));
            }
            if (noCollar > 0) warn += $"{noCollar} 个钻孔缺孔口高程被跳过；";
        }
        catch (Exception ex)
        {
            warn += $"钻孔库未就绪，跳过钻孔投影（{ex.Message}）；";
        }
        return list;
    }
}
