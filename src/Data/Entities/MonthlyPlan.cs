// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/MonthlyPlan.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>月度计划。</summary>
[Table("monthly_plan")]
[ColumnDescription("月度计划")]
public class MonthlyPlan
{
    [Column("year"), PrimaryKey]
    [ColumnDescription("年份")]
    public int Year { get; set; }

    [Column("month"), PrimaryKey]
    [ColumnDescription("月份")]
    public int Month { get; set; }

    [Column("plan_strip_wan_m3")]
    [ColumnDescription("计划剥离(万立方米)")]
    public double PlanStripWanM3 { get; set; }

    [Column("plan_coal_wan_t")]
    [ColumnDescription("计划采煤(万吨)")]
    public double PlanCoalWanT { get; set; }

    [Column("plan_outsource_strip_wan_m3")]
    [ColumnDescription("计划外委剥离(万立方米)")]
    public double PlanOutsourceStripWanM3 { get; set; }

    [Column("ratio_strip_coal")]
    [ColumnDescription("剥采比")]
    public double RatioStripCoal { get; set; }

    [Column("avg_distance_km")]
    [ColumnDescription("平均运距(千米)")]
    public double AvgDistanceKm { get; set; }

    [Column("avg_height_m")]
    [ColumnDescription("平均段高(米)")]
    public double AvgHeightM { get; set; }

    [Column("team1_distance_km")]
    [ColumnDescription("一队运距(千米)")]
    public double Team1DistanceKm { get; set; }

    [Column("team2_distance_km")]
    [ColumnDescription("二队运距(千米)")]
    public double Team2DistanceKm { get; set; }
}

/// <summary>月度铲位安排(子表)。</summary>
[Table("monthly_plan_shovel")]
[ColumnDescription("月度铲位安排")]
public class MonthlyPlanShovel
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("year")]
    [ColumnDescription("年份")]
    public int Year { get; set; }

    [Column("month")]
    [ColumnDescription("月份")]
    public int Month { get; set; }

    [Column("equipment_id")]
    [ColumnDescription("设备编号")]
    public string EquipmentId { get; set; } = "";

    [Column("location_code")]
    [ColumnDescription("平盘编码")]
    public string LocationCode { get; set; } = "";
}
