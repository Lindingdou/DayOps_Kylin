// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/Equipment.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>设备台账 - 在籍设备主清单。</summary>
[Table("equipment")]
[ColumnDescription("设备台账 / 在籍设备主清单")]
public class Equipment
{
    [Column("equipment_id"), PrimaryKey]
    [ColumnDescription("设备编号")]
    public string EquipmentId { get; set; } = "";

    [Column("category")]
    [ColumnDescription("设备类别")]
    public string Category { get; set; } = "";    // 存 EquipmentCategory.ToString()

    [Column("model")]
    [ColumnDescription("型号")]
    public string? Model { get; set; }

    [Column("manufacturer")]
    [ColumnDescription("制造商")]
    public string? Manufacturer { get; set; }

    [Column("origin")]
    [ColumnDescription("产地")]
    public string? Origin { get; set; }

    [Column("serial_number")]
    [ColumnDescription("出厂序列号")]
    public string? SerialNumber { get; set; }

    [Column("asset_code")]
    [ColumnDescription("资产编码")]
    public string? AssetCode { get; set; }

    [Column("status")]
    [ColumnDescription("状态")]
    public string? Status { get; set; }

    [Column("acquisition_date")]
    [ColumnDescription("入厂日期")]
    public DateTime? AcquisitionDate { get; set; }

    [Column("commission_year")]
    [ColumnDescription("投产年份")]
    public int? CommissionYear { get; set; }

    [Column("cumulative_hours")]
    [ColumnDescription("累计工时")]
    public double? CumulativeHours { get; set; }

    [Column("last_overhaul_date")]
    [ColumnDescription("上次大修日期")]
    public DateTime? LastOverhaulDate { get; set; }

    [Column("operating_area")]
    [ColumnDescription("当前作业区(平盘编码)")]
    public string? OperatingArea { get; set; }

    [Column("notes")]
    [ColumnDescription("备注")]
    public string? Notes { get; set; }

    [Column("created_at")]
    [ColumnDescription("创建时间")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    [ColumnDescription("更新时间")]
    public DateTime? UpdatedAt { get; set; }
}
