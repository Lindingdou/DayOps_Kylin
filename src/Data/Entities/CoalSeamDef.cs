// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/CoalSeamDef.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>煤层定义字典（4-1/4-2/7-1/9/11 等）。</summary>
[Table("coal_seam_def")]
[ColumnDescription("煤层定义字典")]
public class CoalSeamDef
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("code")]
    [ColumnDescription("煤层编号 (4-1/9/11/...)")]
    public string Code { get; set; } = "";

    [Column("name")]
    [ColumnDescription("显示名 (\"4-1 号煤层\")")]
    public string Name { get; set; } = "";

    [Column("sort_order")]
    [ColumnDescription("排序号 (浅 → 深)")]
    public int SortOrder { get; set; }

    [Column("avg_thickness")]
    [ColumnDescription("矿区平均厚度 (m, 柱状图比例尺)")]
    public double? AvgThickness { get; set; }

    [Column("color_hex")]
    [ColumnDescription("显示颜色 #RRGGBB")]
    public string? ColorHex { get; set; }

    [Column("description")]
    [ColumnDescription("说明")]
    public string? Description { get; set; }
}
