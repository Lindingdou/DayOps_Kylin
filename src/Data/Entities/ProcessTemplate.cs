// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/ProcessTemplate.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>参数模板:硬岩区标准 / 软岩区标准 / 煤层标准 等场景化预设。</summary>
[Table("process_template")]
[ColumnDescription("参数模板")]
public class ProcessTemplate
{
    [Column("template_id"), PrimaryKey, AutoIncrement]
    public long TemplateId { get; set; }

    [Column("code")]
    [ColumnDescription("模板编码")]
    public string Code { get; set; } = "";

    [Column("name")]
    [ColumnDescription("模板名称")]
    public string Name { get; set; } = "";

    [Column("description")]
    public string? Description { get; set; }

    [Column("applicable_material")]
    [ColumnDescription("适用物料 rh/c4/c9/coal")]
    public string? ApplicableMaterial { get; set; }

    [Column("applicable_hardness")]
    [ColumnDescription("适用硬度 hard/medium/soft")]
    public string? ApplicableHardness { get; set; }

    [Column("version")]
    public string Version { get; set; } = "v1.0";

    [Column("is_current")]
    [ColumnDescription("是否为现行版本")]
    public bool IsCurrent { get; set; } = true;

    [Column("status")]
    public string Status { get; set; } = "active";

    [Column("created_by")]
    public string? CreatedBy { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [Column("notes")]
    public string? Notes { get; set; }
}
