// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/SlopeDesign.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>边坡设计参数。按帮别(东/西/南/北/工作帮/最终帮)分别记录。</summary>
[Table("slope_design")]
[ColumnDescription("边坡设计")]
public class SlopeDesign
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("side_name")]
    [ColumnDescription("帮别 东帮/西帮/南帮/北帮/工作帮/最终帮")]
    public string SideName { get; set; } = "";

    [Column("side_type")]
    [ColumnDescription("帮型 working/final/transition")]
    public string SideType { get; set; } = "working";

    [Column("working_slope_angle_deg")]
    [ColumnDescription("工作帮坡角(°)")]
    public double? WorkingSlopeAngleDeg { get; set; }

    [Column("final_slope_angle_deg")]
    [ColumnDescription("最终帮坡角(°)")]
    public double? FinalSlopeAngleDeg { get; set; }

    [Column("max_depth_m")]
    [ColumnDescription("最大开采深度(m)")]
    public double? MaxDepthM { get; set; }

    [Column("safety_factor")]
    [ColumnDescription("安全系数 F(≥ 1.30 安全)")]
    public double? SafetyFactor { get; set; }

    [Column("cohesion_kpa")]
    [ColumnDescription("内聚力 c(kPa)")]
    public double? CohesionKpa { get; set; }

    [Column("friction_angle_deg")]
    [ColumnDescription("内摩擦角 φ(°)")]
    public double? FrictionAngleDeg { get; set; }

    [Column("rock_type")]
    [ColumnDescription("岩性")]
    public string? RockType { get; set; }

    [Column("groundwater_level_m")]
    [ColumnDescription("地下水位(m)")]
    public double? GroundwaterLevelM { get; set; }

    [Column("effective_from")]
    [ColumnDescription("生效起始日")]
    public DateTime EffectiveFrom { get; set; }

    [Column("effective_to")]
    public DateTime? EffectiveTo { get; set; }

    [Column("design_version")]
    [ColumnDescription("设计版本号")]
    public string? DesignVersion { get; set; }

    [Column("designed_by")]
    [ColumnDescription("设计单位/人")]
    public string? DesignedBy { get; set; }

    [Column("notes")]
    public string? Notes { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
