// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/RoadNetwork.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 路网存档 — 开拓运输系统某一时期的道路网络图。
/// 图(节点/边/中线/属性)由 RoadLib 序列化成 JSON 存 <see cref="GraphJson"/>(GeoDataBase 不解析)。
/// <see cref="CapturedAt"/> = "什么时候的路网"。冗余存 node/edge/length 供列表免反序列化显示。
/// 项目级持久化(随工程 .db);RoadLib「保存路网」存、「路网存档」管理。
/// </summary>
[Table("road_network")]
[ColumnDescription("路网存档(某时期道路网络图)")]
public class RoadNetwork
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("name")]
    [ColumnDescription("路网名称(可含时期,如 2026-06 现状)")]
    public string Name { get; set; } = "";

    [Column("captured_at")]
    [ColumnDescription("所属/采集时刻(什么时候的路网)")]
    public string CapturedAt { get; set; } = "";

    [Column("graph_json")]
    [ColumnDescription("RoadLib 序列化的路网图(节点+边),opaque")]
    public string GraphJson { get; set; } = "{}";

    [Column("node_count")] public long NodeCount { get; set; }
    [Column("edge_count")] public long EdgeCount { get; set; }

    [Column("length_km")]
    [ColumnDescription("总里程 km(冗余,列表显示)")]
    public double LengthKm { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    // created_at / updated_at 不映射:交给 DB DEFAULT CURRENT_TIMESTAMP + AFTER UPDATE 触发器(同 LoadUnloadPoint)。
}
