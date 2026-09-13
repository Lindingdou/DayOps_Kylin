// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/CurrentStatePoint.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>现状高程点(现状地表/台阶等)。归属某现状写实批次;供建现状三维面。</summary>
[Table("current_state_point")]
[ColumnDescription("现状高程点")]
public class CurrentStatePoint
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("batch_id")]
    [ColumnDescription("FK current_state_batch.id")]
    public long BatchId { get; set; }

    [Column("x")]
    [ColumnDescription("经距 (m)")]
    public double X { get; set; }

    [Column("y")]
    [ColumnDescription("纬距 (m)")]
    public double Y { get; set; }

    [Column("z")]
    [ColumnDescription("现状高程 (m, 黄海)")]
    public double Z { get; set; }

    [Column("remark")]
    [ColumnDescription("备注")]
    public string? Remark { get; set; }

    [Column("seam_code")]
    [ColumnDescription("煤层编号 (见煤点所属煤层;V033 起)")]
    public string? SeamCode { get; set; }

    [Column("horizon")]
    [ColumnDescription("顶板 / 底板 (V033 起)")]
    public string? Horizon { get; set; }
}
