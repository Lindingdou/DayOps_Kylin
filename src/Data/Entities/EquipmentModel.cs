// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/EquipmentModel.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>设备型号字典。</summary>
[Table("equipment_model")]
[ColumnDescription("设备型号字典")]
public class EquipmentModel
{
    [Column("model"), PrimaryKey]
    [ColumnDescription("型号")]
    public string Model { get; set; } = "";

    [Column("category")]
    [ColumnDescription("适用类别")]
    public string Category { get; set; } = "";

    [Column("working_weight_t")]
    [ColumnDescription("工作重量(吨)")]
    public double? WorkingWeightT { get; set; }

    [Column("power_kw")]
    [ColumnDescription("功率(千瓦)")]
    public double? PowerKw { get; set; }

    [Column("bucket_m3")]
    [ColumnDescription("铲斗容积(立方米)")]
    public double? BucketM3 { get; set; }

    [Column("load_t")]
    [ColumnDescription("载重(吨)")]
    public double? LoadT { get; set; }

    [Column("dimensions_lwh")]
    [ColumnDescription("外形尺寸长宽高")]
    public string? DimensionsLwh { get; set; }

    [Column("drill_diameter_mm")]
    [ColumnDescription("钻孔直径(毫米)")]
    public double? DrillDiameterMm { get; set; }

    [Column("tire_spec")]
    [ColumnDescription("轮胎规格")]
    public string? TireSpec { get; set; }

    [Column("std_daily_cap_wan_m3")]
    [ColumnDescription("标准日产能(万立方米)")]
    public double? StdDailyCapWanM3 { get; set; }
}
