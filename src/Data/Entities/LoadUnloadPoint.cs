// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/LoadUnloadPoint.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 装卸点 — 开拓运输系统的源(采剥点)/汇(卸载点)。
/// 项目级持久化(随工程 .db),取代早先的 user 设置 blob;RoadLib「装卸点设置」手动插入、受管,
/// 跨模块经 <c>ILoadUnloadPointService</c> 读。
/// </summary>
[Table("load_unload_point")]
[ColumnDescription("装卸点(运输源/汇)")]
public class LoadUnloadPoint
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("name")]
    [ColumnDescription("名称(指定名)")]
    public string Name { get; set; } = "";

    [Column("kind")]
    [ColumnDescription("类别 loading(采剥点/源) | unloading(卸载点/汇)")]
    public string Kind { get; set; } = "loading";

    [Column("unload_sub")]
    [ColumnDescription("卸载子类 crusher/dump/stockpile(仅卸载点)")]
    public string? UnloadSub { get; set; }

    [Column("x")] public double X { get; set; }
    [Column("y")] public double Y { get; set; }
    [Column("z")] public double Z { get; set; }

    [Column("throughput_tph")]
    [ColumnDescription("吞吐能力 t/h")]
    public double ThroughputTph { get; set; }

    [Column("ref_region_id")]
    [ColumnDescription("关联区域 mineable_region.id(0=未关联)")]
    public long RefRegionId { get; set; }

    [Column("visible")]
    [ColumnDescription("是否在视口显示(0/1)")]
    public long Visible { get; set; } = 1;

    [Column("note")]
    public string? Note { get; set; }

    // created_at / updated_at 不映射:交给 DB DEFAULT CURRENT_TIMESTAMP + AFTER UPDATE 触发器(同 MineableRegion)。
}
