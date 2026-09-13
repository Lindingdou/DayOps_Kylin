// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/ParameterDefinition.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>参数定义(孔径/孔深/单耗/台阶高度/...)。</summary>
[Table("parameter_definition")]
[ColumnDescription("参数定义")]
public class ParameterDefinition
{
    [Column("param_id"), PrimaryKey, AutoIncrement]
    public long ParamId { get; set; }

    [Column("phase_id")]
    [ColumnDescription("所属工艺环节")]
    public long PhaseId { get; set; }

    [Column("code")]
    [ColumnDescription("参数编码(全表唯一)")]
    public string Code { get; set; } = "";

    [Column("name")]
    [ColumnDescription("参数名称")]
    public string Name { get; set; } = "";

    [Column("unit")]
    [ColumnDescription("单位")]
    public string? Unit { get; set; }

    [Column("value_type")]
    [ColumnDescription("值类型 numeric/text/boolean/enum")]
    public string ValueType { get; set; } = "numeric";

    [Column("standard_min")]
    [ColumnDescription("标准范围下限")]
    public double? StandardMin { get; set; }

    [Column("standard_max")]
    [ColumnDescription("标准范围上限")]
    public double? StandardMax { get; set; }

    [Column("standard_default")]
    [ColumnDescription("默认推荐值")]
    public double? StandardDefault { get; set; }

    [Column("alarm_low")]
    [ColumnDescription("报警下限")]
    public double? AlarmLow { get; set; }

    [Column("alarm_high")]
    [ColumnDescription("报警上限")]
    public double? AlarmHigh { get; set; }

    [Column("is_required")]
    [ColumnDescription("是否必填")]
    public bool IsRequired { get; set; }

    [Column("calc_formula")]
    [ColumnDescription("派生公式(可空)")]
    public string? CalcFormula { get; set; }

    [Column("source_table")]
    [ColumnDescription("可派生自的事实表")]
    public string? SourceTable { get; set; }

    [Column("source_column")]
    [ColumnDescription("派生字段")]
    public string? SourceColumn { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("display_order")]
    public int DisplayOrder { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
