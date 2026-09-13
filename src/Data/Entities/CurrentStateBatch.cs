// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/CurrentStateBatch.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 现状写实批次(系统唯一时间标签)。一批多点;<see cref="Label"/> 唯一=系统唯一,<see cref="Name"/> 可改名。
/// 删批次连带删该批全部现状点(服务层保证)。created_at/updated_at 由 DB 默认值 + 触发器维护,故不映射。
/// </summary>
[Table("current_state_batch")]
[ColumnDescription("现状写实批次")]
public class CurrentStateBatch
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("label")]
    [ColumnDescription("系统唯一时间标签 CSR-yyyyMMdd-HHmmss")]
    public string Label { get; set; } = "";

    [Column("name")]
    [ColumnDescription("友好名(可改)")]
    public string Name { get; set; } = "";

    [Column("source")]
    [ColumnDescription("来源(手工录入/Excel导入)")]
    public string? Source { get; set; }

    [Column("remark")]
    [ColumnDescription("备注")]
    public string? Remark { get; set; }

    /// <summary>该批次点数(派生,不落库)。由服务 <c>AllBatches</c> 填充。</summary>
    [NotMapped]
    public int PointCount { get; set; }
}
