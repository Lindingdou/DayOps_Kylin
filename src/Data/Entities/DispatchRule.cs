// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/DispatchRule.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>编组规则(配置表)。</summary>
[Table("dispatch_rule")]
[ColumnDescription("编组规则")]
public class DispatchRule
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("shovel_model")]
    [ColumnDescription("电铲型号")]
    public string ShovelModel { get; set; } = "";

    [Column("truck_model")]
    [ColumnDescription("卡车型号")]
    public string TruckModel { get; set; } = "";

    [Column("bucket_loads_per_truck")]
    [ColumnDescription("装满铲数")]
    public double BucketLoadsPerTruck { get; set; }

    [Column("recommended_truck_count")]
    [ColumnDescription("推荐卡车数")]
    public int RecommendedTruckCount { get; set; }

    [Column("cycle_time_min")]
    [ColumnDescription("单循环分钟")]
    public double CycleTimeMin { get; set; }

    [Column("efficiency_score")]
    [ColumnDescription("效率评分 1-100")]
    public int EfficiencyScore { get; set; }

    [Column("effective_from")]
    [ColumnDescription("生效起始日")]
    public DateTime? EffectiveFrom { get; set; }

    [Column("is_active")]
    [ColumnDescription("是否启用")]
    public bool IsActive { get; set; } = true;
}
