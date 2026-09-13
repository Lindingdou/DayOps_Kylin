// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/CapacityMonthly.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>月度产能(设备 × 年月)。</summary>
[Table("capacity_monthly")]
[ColumnDescription("月度产能")]
public class CapacityMonthly
{
    [Column("equipment_id"), PrimaryKey]
    [ColumnDescription("设备编号")]
    public string EquipmentId { get; set; } = "";

    [Column("year"), PrimaryKey]
    [ColumnDescription("年份")]
    public int Year { get; set; }

    [Column("month"), PrimaryKey]
    [ColumnDescription("月份")]
    public int Month { get; set; }

    [Column("output_m3")]
    [ColumnDescription("月产量(立方米)")]
    public double OutputM3 { get; set; }
}
