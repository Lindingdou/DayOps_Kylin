// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/WorkingFace.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>工作面 / 台阶几何参数。一个工作面就是"在某平盘上由某电铲负责的一个连续作业区域"。</summary>
[Table("working_face")]
[ColumnDescription("工作面 / 台阶几何")]
public class WorkingFace
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("face_code")]
    [ColumnDescription("工作面编号(如 WF-1195-A)")]
    public string FaceCode { get; set; } = "";

    [Column("location_code")]
    [ColumnDescription("平盘编码")]
    public string? LocationCode { get; set; }

    [Column("equipment_id")]
    [ColumnDescription("主电铲编号")]
    public string? EquipmentId { get; set; }

    [Column("bench_height_m")]
    [ColumnDescription("台阶高度(m)")]
    public double BenchHeightM { get; set; }

    [Column("bench_slope_angle_deg")]
    [ColumnDescription("台阶坡面角(°)")]
    public double? BenchSlopeAngleDeg { get; set; }

    [Column("working_platform_width_m")]
    [ColumnDescription("工作平台宽度(m)")]
    public double? WorkingPlatformWidthM { get; set; }

    [Column("safety_platform_width_m")]
    [ColumnDescription("安全平台宽度(m)")]
    public double? SafetyPlatformWidthM { get; set; }

    [Column("mining_width_m")]
    [ColumnDescription("采宽(m)")]
    public double? MiningWidthM { get; set; }

    [Column("face_length_m")]
    [ColumnDescription("工作面长度(m)")]
    public double? FaceLengthM { get; set; }

    [Column("advance_rate_m_per_month")]
    [ColumnDescription("月推进度(m/月)")]
    public double? AdvanceRateMPerMonth { get; set; }

    [Column("material")]
    [ColumnDescription("物料类型 c4/c9/rh/coal")]
    public string? Material { get; set; }

    [Column("rock_hardness")]
    [ColumnDescription("岩石硬度 hard/medium/soft")]
    public string? RockHardness { get; set; }

    [Column("effective_from")]
    [ColumnDescription("生效起始日")]
    public DateTime EffectiveFrom { get; set; }

    [Column("effective_to")]
    [ColumnDescription("失效日(NULL=现行)")]
    public DateTime? EffectiveTo { get; set; }

    [Column("status")]
    [ColumnDescription("状态 active/closed/planning")]
    public string Status { get; set; } = "active";

    [Column("notes")]
    public string? Notes { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
