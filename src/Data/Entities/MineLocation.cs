// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/MineLocation.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>采区位置 / 平盘。</summary>
[Table("mine_location")]
[ColumnDescription("采区位置 / 平盘")]
public class MineLocation
{
    [Column("location_code"), PrimaryKey]
    [ColumnDescription("平盘编码")]
    public string LocationCode { get; set; } = "";

    [Column("name")]
    [ColumnDescription("平盘名称")]
    public string? Name { get; set; }

    [Column("elevation_m")]
    [ColumnDescription("标高(米)")]
    public double? ElevationM { get; set; }

    [Column("team")]
    [ColumnDescription("所属队组")]
    public string? Team { get; set; }

    [Column("is_active")]
    [ColumnDescription("是否启用")]
    public bool IsActive { get; set; } = true;
}
