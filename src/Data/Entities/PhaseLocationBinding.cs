// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/PhaseLocationBinding.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>工艺环节 ↔ 平盘 ↔ 模板 三方绑定。</summary>
[Table("phase_location_binding")]
[ColumnDescription("环节-平盘-模板绑定")]
public class PhaseLocationBinding
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("phase_id")]
    [ColumnDescription("工艺环节")]
    public long PhaseId { get; set; }

    [Column("location_code")]
    [ColumnDescription("平盘编码")]
    public string LocationCode { get; set; } = "";

    [Column("bound_template_id")]
    [ColumnDescription("套用模板(NULL=用参数默认值)")]
    public long? BoundTemplateId { get; set; }

    [Column("started_at")]
    public DateTime StartedAt { get; set; }

    [Column("ended_at")]
    public DateTime? EndedAt { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("notes")]
    public string? Notes { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
