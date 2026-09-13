// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/CoalObservationPoint.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>见煤点（附表3，不挂钻孔，自带 X/Y）。</summary>
[Table("coal_observation_point")]
[ColumnDescription("见煤点（非钻孔的煤层观察点）")]
public class CoalObservationPoint
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("point_id")]
    [ColumnDescription("见煤点编号 (1/33/ATB15-08/604-64)")]
    public string PointId { get; set; } = "";

    [Column("seam_code")]
    [ColumnDescription("煤层编号")]
    public string SeamCode { get; set; } = "";

    [Column("x")]
    [ColumnDescription("经距 (m, 已去 37 带号; V014 起 x=经距)")]
    public double X { get; set; }

    [Column("y")]
    [ColumnDescription("纬距 (m; V014 起 y=纬距)")]
    public double Y { get; set; }

    [Column("original_y_format")]
    [ColumnDescription("源经距格式(历史): 8=含带号；6=无带号(已补 +37000000)")]
    public int OriginalYFormat { get; set; } = 8;

    [Column("seam_thickness")]
    [ColumnDescription("煤层厚度 (m)")]
    public double? SeamThickness { get; set; }

    [Column("coal_structure")]
    [ColumnDescription("煤层结构 (原文)")]
    public string? CoalStructure { get; set; }

    [Column("estimated_thickness")]
    [ColumnDescription("估算厚度 (m)")]
    public double? EstimatedThickness { get; set; }

    [Column("floor_elevation")]
    [ColumnDescription("底板标高 (m)")]
    public double? FloorElevation { get; set; }

    [Column("annual_report")]
    [ColumnDescription("年报来源 (2014年年报/2023年年报)")]
    public string? AnnualReport { get; set; }

    [Column("source_page")]
    [ColumnDescription("源 PDF 页")]
    public int? SourcePage { get; set; }

    [Column("remark")]
    [ColumnDescription("备注")]
    public string? Remark { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
