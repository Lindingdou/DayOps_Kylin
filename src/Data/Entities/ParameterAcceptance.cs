// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/ParameterAcceptance.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>现场验收 — 单参数实测记录。</summary>
[Table("parameter_acceptance")]
[ColumnDescription("现场参数验收")]
public class ParameterAcceptance
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("param_id")]
    public long ParamId { get; set; }

    [Column("location_code")]
    public string LocationCode { get; set; } = "";

    [Column("phase_id")]
    public long PhaseId { get; set; }

    [Column("measure_date")]
    [ColumnDescription("测量日期")]
    public DateTime MeasureDate { get; set; }

    [Column("measured_value")]
    [ColumnDescription("数值型实测")]
    public double? MeasuredValue { get; set; }

    [Column("measured_text")]
    [ColumnDescription("文本型实测")]
    public string? MeasuredText { get; set; }

    [Column("template_value")]
    [ColumnDescription("验收时模板值快照")]
    public double? TemplateValue { get; set; }

    [Column("deviation_pct")]
    [ColumnDescription("偏差百分比")]
    public double? DeviationPct { get; set; }

    [Column("status")]
    [ColumnDescription("状态 pass/warning/fail/pending")]
    public string Status { get; set; } = "pending";

    [Column("equipment_id")]
    [ColumnDescription("关联设备")]
    public string? EquipmentId { get; set; }

    [Column("accepted_by")]
    [ColumnDescription("验收人")]
    public string? AcceptedBy { get; set; }

    [Column("acceptance_date")]
    public DateTime? AcceptanceDate { get; set; }

    [Column("conclusion")]
    [ColumnDescription("验收结论")]
    public string? Conclusion { get; set; }

    [Column("scope_code")]
    [ColumnDescription("炮区/工作面编号")]
    public string? ScopeCode { get; set; }

    [Column("notes")]
    public string? Notes { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
