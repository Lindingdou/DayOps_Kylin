// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/FaultEvent.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>故障事件。</summary>
[Table("fault_event")]
[ColumnDescription("故障事件")]
public class FaultEvent
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
    public string? Shift { get; set; }

    [Column("fault_type")]
    [ColumnDescription("故障类型")]
    public string FaultType { get; set; } = "";

    [Column("duration_hours")]
    [ColumnDescription("持续小时")]
    public double DurationHours { get; set; }

    [Column("description")]
    [ColumnDescription("故障描述")]
    public string? Description { get; set; }

    [Column("is_resolved")]
    [ColumnDescription("是否修复")]
    public bool IsResolved { get; set; }

    [Column("repair_team")]
    [ColumnDescription("维修队组")]
    public string? RepairTeam { get; set; }

    [Column("created_at")]
    [ColumnDescription("创建时间")]
    public DateTime? CreatedAt { get; set; }
}
