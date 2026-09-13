// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/SupplementaryBatch.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 补勘写实批次(系统唯一时间标签)。一批多孔;<see cref="Label"/> 唯一=系统唯一,
/// <see cref="Name"/> 可改名。删批次连带删该批全部孔+层位(服务层保证)。
/// created_at/updated_at 由 DB 默认值 + 触发器维护,故本实体不映射这两列。
/// </summary>
[Table("supplementary_batch")]
[ColumnDescription("补勘写实批次")]
public class SupplementaryBatch
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("label")]
    [ColumnDescription("系统唯一时间标签 SBW-yyyyMMdd-HHmmss")]
    public string Label { get; set; } = "";

    [Column("name")]
    [ColumnDescription("友好名(可改)")]
    public string Name { get; set; } = "";

    [Column("source")]
    [ColumnDescription("来源(手工录入/Excel导入/迁移归集)")]
    public string? Source { get; set; }

    [Column("remark")]
    [ColumnDescription("备注")]
    public string? Remark { get; set; }

    /// <summary>该批次孔数(派生,不落库)。由服务 <c>AllBatches</c> 填充。</summary>
    [NotMapped]
    public int HoleCount { get; set; }
}
