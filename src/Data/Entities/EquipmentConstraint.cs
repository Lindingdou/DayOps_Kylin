// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/EquipmentConstraint.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>设备约束(参数 → 设备能力)。</summary>
[Table("equipment_constraint")]
[ColumnDescription("设备约束")]
public class EquipmentConstraint
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("param_id")]
    [ColumnDescription("关联参数")]
    public long ParamId { get; set; }

    [Column("equipment_model")]
    [ColumnDescription("设备型号")]
    public string EquipmentModel { get; set; } = "";

    [Column("constraint_type")]
    [ColumnDescription("约束类型 min/max/range/equals/contains")]
    public string ConstraintType { get; set; } = "max";

    [Column("limit_value")]
    [ColumnDescription("单值约束")]
    public double? LimitValue { get; set; }

    [Column("limit_min")]
    public double? LimitMin { get; set; }

    [Column("limit_max")]
    public double? LimitMax { get; set; }

    [Column("text_value")]
    [ColumnDescription("文本/枚举约束")]
    public string? TextValue { get; set; }

    [Column("consequence")]
    [ColumnDescription("违反后果 hard/soft/informational")]
    public string Consequence { get; set; } = "hard";

    [Column("priority")]
    [ColumnDescription("优先级 1-5")]
    public int Priority { get; set; } = 3;

    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }
}
