// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/ProcessPhase.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>工艺环节(钻孔/装药/起爆/剥离/采煤/装车/卸车/...)。</summary>
[Table("process_phase")]
[ColumnDescription("工艺环节")]
public class ProcessPhase
{
    [Column("phase_id"), PrimaryKey, AutoIncrement]
    public long PhaseId { get; set; }

    [Column("system_id")]
    [ColumnDescription("所属工艺系统")]
    public long SystemId { get; set; }

    [Column("code")]
    [ColumnDescription("环节编码")]
    public string Code { get; set; } = "";

    [Column("name")]
    [ColumnDescription("环节名称")]
    public string Name { get; set; } = "";

    [Column("sequence_order")]
    [ColumnDescription("环节先后顺序")]
    public int SequenceOrder { get; set; }

    [Column("typical_equipment_category")]
    [ColumnDescription("典型适用设备类别")]
    public string? TypicalEquipmentCategory { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
