// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/SupplementaryBorehole.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 补勘钻孔基础信息(写实)。与原始 <see cref="Borehole"/> 隔离,
/// 供「补勘钻孔写实」录入与自动重建煤层三维面。
/// created_at/updated_at 由 DB 默认值 + 触发器维护,故本实体不映射这两列。
/// </summary>
[Table("supplementary_borehole")]
[ColumnDescription("补勘钻孔(写实)")]
public class SupplementaryBorehole
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("hole_id")]
    [ColumnDescription("孔号 (业务唯一键)")]
    public string HoleId { get; set; } = "";

    [Column("x")]
    [ColumnDescription("经距 (m, 与 borehole.x 同坐标系, 已去带号)")]
    public double X { get; set; }

    [Column("y")]
    [ColumnDescription("纬距 (m)")]
    public double Y { get; set; }

    [Column("z_collar")]
    [ColumnDescription("孔口高程 (m, 黄海)")]
    public double? ZCollar { get; set; }

    [Column("remark")]
    [ColumnDescription("备注")]
    public string? Remark { get; set; }

    [Column("batch_id")]
    [ColumnDescription("FK supplementary_batch.id(所属写实批次;V031 起)")]
    public long? BatchId { get; set; }
}
