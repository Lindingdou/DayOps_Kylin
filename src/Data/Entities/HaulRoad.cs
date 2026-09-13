// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/HaulRoad.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>运输道路网络(单段)。</summary>
[Table("haul_road")]
[ColumnDescription("运输道路")]
public class HaulRoad
{
    [Column("road_id"), PrimaryKey]
    [ColumnDescription("路段编号")]
    public string RoadId { get; set; } = "";

    [Column("name")]
    [ColumnDescription("路段名称")]
    public string Name { get; set; } = "";

    [Column("road_type")]
    [ColumnDescription("道路类型 main/branch/dump/temp")]
    public string RoadType { get; set; } = "main";

    [Column("start_location")]
    [ColumnDescription("起点(平盘/采区)")]
    public string? StartLocation { get; set; }

    [Column("end_location")]
    [ColumnDescription("终点(排土场/破碎站)")]
    public string? EndLocation { get; set; }

    [Column("length_m")]
    [ColumnDescription("长度(m)")]
    public double LengthM { get; set; }

    [Column("max_slope_pct")]
    [ColumnDescription("最大坡度(%)")]
    public double? MaxSlopePct { get; set; }

    [Column("avg_slope_pct")]
    [ColumnDescription("平均坡度(%)")]
    public double? AvgSlopePct { get; set; }

    [Column("road_width_m")]
    [ColumnDescription("路宽(m)")]
    public double? RoadWidthM { get; set; }

    [Column("turning_radius_m")]
    [ColumnDescription("转弯半径(m)")]
    public double? TurningRadiusM { get; set; }

    [Column("pavement_type")]
    [ColumnDescription("路面材质 gravel/compacted/paved")]
    public string? PavementType { get; set; }

    [Column("max_load_t")]
    [ColumnDescription("最大允许载重(t)")]
    public double? MaxLoadT { get; set; }

    [Column("primary_truck_model")]
    [ColumnDescription("主要服务车型")]
    public string? PrimaryTruckModel { get; set; }

    [Column("maintenance_team")]
    [ColumnDescription("维护队组")]
    public string? MaintenanceTeam { get; set; }

    [Column("last_maintenance_date")]
    [ColumnDescription("最后维护日期")]
    public DateTime? LastMaintenanceDate { get; set; }

    [Column("condition")]
    [ColumnDescription("路况 good/fair/poor/closed")]
    public string Condition { get; set; } = "good";

    [Column("notes")]
    public string? Notes { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
