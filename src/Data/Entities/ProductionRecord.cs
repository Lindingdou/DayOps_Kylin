// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/ProductionRecord.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>班次生产记录(设备 × 日 × 班)。</summary>
[Table("production_record")]
[ColumnDescription("班次生产记录")]
public class ProductionRecord
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("equipment_id")]
    [ColumnDescription("设备编号")]
    public string EquipmentId { get; set; } = "";

    [Column("date")]
    [ColumnDescription("日期")]
    public DateTime Date { get; set; }

    [Column("shift")]
    [ColumnDescription("班次")]
    public string Shift { get; set; } = "";

    [Column("output_m3")]
    [ColumnDescription("班产(立方米)")]
    public double OutputM3 { get; set; }

    [Column("work_hours")]
    [ColumnDescription("工作小时")]
    public double WorkHours { get; set; }

    [Column("fault_hours")]
    [ColumnDescription("故障小时")]
    public double FaultHours { get; set; }

    [Column("fault_reason")]
    [ColumnDescription("故障原因摘要")]
    public string? FaultReason { get; set; }

    [Column("created_at")]
    [ColumnDescription("创建时间")]
    public DateTime? CreatedAt { get; set; }
}
