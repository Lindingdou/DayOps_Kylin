// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/SinkStocktake.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 库容盘点流水(V035)。「已填」是实绩逐日累计出来的,不允许在普通台账编辑里随手改 ——
/// 改了账就对不上。确需修正(实测扫描、历史补录、口径纠偏)时走盘点:留改前/改后/差额/原因,
/// 只增不改,谁改的、改了多少、为什么改都留痕。
///
/// 单位一律【排弃占容方 m³】,与调度侧 SinkNode.FilledM3 同口径(不是 dump_site 的万 m³)。
/// </summary>
[Table("sink_stocktake")]
[ColumnDescription("库容盘点流水")]
public class SinkStocktake
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("sink_id")]
    [ColumnDescription("去向编号")]
    public string SinkId { get; set; } = "";

    [Column("sink_name")]
    [ColumnDescription("去向名称(冗余,供流水直读)")]
    public string SinkName { get; set; } = "";

    [Column("before_filled_m3")]
    [ColumnDescription("盘点前已填(占容方 m³)")]
    public double BeforeFilledM3 { get; set; }

    [Column("after_filled_m3")]
    [ColumnDescription("盘点后已填(占容方 m³)")]
    public double AfterFilledM3 { get; set; }

    [Column("delta_m3")]
    [ColumnDescription("差额(占容方 m³;正=补记,负=冲回)")]
    public double DeltaM3 { get; set; }

    [Column("reason")]
    [ColumnDescription("修正原因(必填)")]
    public string Reason { get; set; } = "";

    [Column("operator")]
    [ColumnDescription("盘点人")]
    public string Operator { get; set; } = "";

    // created_at 不映射:交给 DB DEFAULT CURRENT_TIMESTAMP。
}
