// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/RoadCenterlineSet.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 中心线存档 — 某一版「道路中心线」的几何原样（V045）。
///
/// 与 <see cref="RoadNetwork"/> 的分工：本表存<b>进料</b>（图纸上那批中线折线），
/// RoadNetwork 存<b>成品</b>（吸附/打断/桥接之后的节点+边图）。建网是单向的，
/// 从成品反推不回中线，所以两张都要留：改口径重建网回到本表，寻径/运距只认成品。
///
/// 几何由 RoadLib 的 <c>CenterlineSetCodec</c> 打包成 GZip 二进制再 base64 存
/// <see cref="GeometryB64"/>（GeoDataBase 不解析）。line/vertex/length/包围盒是冗余列，
/// 供管理窗列表免解包直接显示。项目级持久化（随工程 .db）；RoadLib「中心线管理」存与载入。
/// </summary>
[Table("road_centerline_set")]
[ColumnDescription("中心线存档(某一版道路中心线几何)")]
public class RoadCenterlineSet
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("name")]
    [ColumnDescription("存档名称(可含时期,如 2026-06 提取+人工整理)")]
    public string Name { get; set; } = "";

    [Column("captured_at")]
    [ColumnDescription("存档时刻(什么时候的中线)")]
    public string CapturedAt { get; set; } = "";

    [Column("source")]
    [ColumnDescription("来路标注:提取/手动/管理整理(纯留痕)")]
    public string Source { get; set; } = "";

    [Column("geometry_b64")]
    [ColumnDescription("打包的中线几何(GZip+base64,RCL1),opaque")]
    public string GeometryB64 { get; set; } = "";

    [Column("line_count")] public long LineCount { get; set; }
    [Column("vertex_count")] public long VertexCount { get; set; }

    [Column("length_km")]
    [ColumnDescription("三维总长 km(冗余,列表显示)")]
    public double LengthKm { get; set; }

    [Column("min_x")] public double MinX { get; set; }
    [Column("min_y")] public double MinY { get; set; }
    [Column("min_z")] public double MinZ { get; set; }
    [Column("max_x")] public double MaxX { get; set; }
    [Column("max_y")] public double MaxY { get; set; }
    [Column("max_z")] public double MaxZ { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    // created_at / updated_at 不映射:交给 DB DEFAULT CURRENT_TIMESTAMP + AFTER UPDATE 触发器(同 RoadNetwork)。
}
