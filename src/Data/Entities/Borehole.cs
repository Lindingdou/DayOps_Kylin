// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/Borehole.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>钻孔基础信息 / 附表1 钻孔施工情况一览表。</summary>
[Table("borehole")]
[ColumnDescription("钻孔基础信息")]
public class Borehole
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("hole_id")]
    [ColumnDescription("孔号 (业务唯一键)")]
    public string HoleId { get; set; } = "";

    [Column("x")]
    [ColumnDescription("经距 (CGCS2000, m, 已去 37 带号; V014 起 x=经距)")]
    public double X { get; set; }

    [Column("y")]
    [ColumnDescription("纬距 (CGCS2000, m; V014 起 y=纬距)")]
    public double Y { get; set; }

    [Column("z_collar")]
    [ColumnDescription("孔口高程 (m, 黄海)")]
    public double? ZCollar { get; set; }

    [Column("depth_total")]
    [ColumnDescription("总孔深 (m)")]
    public double? DepthTotal { get; set; }

    [Column("terminate_horizon")]
    [ColumnDescription("终孔层位 (Ct3/Cb/O)")]
    public string? TerminateHorizon { get; set; }

    [Column("drill_date")]
    [ColumnDescription("施工时间 (原格式)")]
    public string? DrillDate { get; set; }

    [Column("drill_unit")]
    [ColumnDescription("施工单位")]
    public string? DrillUnit { get; set; }

    [Column("drill_rating")]
    [ColumnDescription("钻探评级 甲/乙/丙")]
    public string? DrillRating { get; set; }

    [Column("log_rating")]
    [ColumnDescription("测井评级")]
    public string? LogRating { get; set; }

    [Column("overall_rating")]
    [ColumnDescription("综合评级")]
    public string? OverallRating { get; set; }

    [Column("category")]
    [ColumnDescription("钻孔类别 (2007核实/2014补勘/生产)")]
    public string? Category { get; set; }

    [Column("coord_filled")]
    [ColumnDescription("坐标补充标记 (原始/经距补/高程补/+)")]
    public string CoordFilled { get; set; } = "原始";

    [Column("coord_fill_basis")]
    [ColumnDescription("IDW 邻孔补充依据")]
    public string? CoordFillBasis { get; set; }

    [Column("source_page")]
    [ColumnDescription("源 PDF 页号")]
    public int? SourcePage { get; set; }

    [Column("remark")]
    [ColumnDescription("备注 (\"无芯、电测孔\" 等)")]
    public string? Remark { get; set; }

    [Column("created_at")]
    [ColumnDescription("创建时间")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    [ColumnDescription("更新时间")]
    public DateTime? UpdatedAt { get; set; }
}
