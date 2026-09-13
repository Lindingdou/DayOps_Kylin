// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/ProcessSystem.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>工艺系统(穿爆/采装/运输/排土/边坡/经济/安全/辅助)。</summary>
[Table("process_system")]
[ColumnDescription("工艺系统")]
public class ProcessSystem
{
    [Column("system_id"), PrimaryKey, AutoIncrement]
    public long SystemId { get; set; }

    [Column("code")]
    [ColumnDescription("系统编码")]
    public string Code { get; set; } = "";

    [Column("name")]
    [ColumnDescription("系统名称")]
    public string Name { get; set; } = "";

    [Column("category")]
    [ColumnDescription("分类")]
    public string? Category { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("display_order")]
    [ColumnDescription("UI 排序")]
    public int DisplayOrder { get; set; }

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
