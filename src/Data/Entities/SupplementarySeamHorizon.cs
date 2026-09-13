// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/SupplementarySeamHorizon.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 补勘钻孔煤层顶/底板高程(写实, 一孔一层一条)。
/// 顶板、底板高程都直接存(写实);厚度 = 顶 - 底 由上层派生,不落库。
/// created_at/updated_at 由 DB 默认值 + 触发器维护,故本实体不映射这两列。
/// </summary>
[Table("supplementary_seam_horizon")]
[ColumnDescription("补勘煤层顶底板(写实)")]
public class SupplementarySeamHorizon
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("sup_borehole_id")]
    [ColumnDescription("FK supplementary_borehole.id")]
    public long SupBoreholeId { get; set; }

    [Column("seam_code")]
    [ColumnDescription("煤层编号 (4/7-1/9/11/...)")]
    public string SeamCode { get; set; } = "";

    [Column("roof_elevation")]
    [ColumnDescription("顶板高程 (m)")]
    public double? RoofElevation { get; set; }

    [Column("floor_elevation")]
    [ColumnDescription("底板高程 (m)")]
    public double? FloorElevation { get; set; }

    [Column("sort_order")]
    [ColumnDescription("层序 (浅→深, 决定上下关系)")]
    public int SortOrder { get; set; }

    [Column("remark")]
    [ColumnDescription("备注")]
    public string? Remark { get; set; }
}
