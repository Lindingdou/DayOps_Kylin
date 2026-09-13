// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/BoreholeSeamResult.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>钻孔煤层成果 / 附表2 可采煤层成果表（一孔一层一条）。</summary>
[Table("borehole_seam_result")]
[ColumnDescription("钻孔煤层成果")]
public class BoreholeSeamResult
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("borehole_id")]
    [ColumnDescription("FK borehole.id")]
    public long BoreholeId { get; set; }

    [Column("seam_code")]
    [ColumnDescription("煤层编号")]
    public string SeamCode { get; set; } = "";

    // ─── 钻探成果 ───
    [Column("drill_end_depth")]
    [ColumnDescription("钻探-止煤深度 (m)")]
    public double? DrillEndDepth { get; set; }

    [Column("drill_seam_thickness")]
    [ColumnDescription("钻探-煤层厚度 (m)")]
    public double? DrillSeamThickness { get; set; }

    [Column("drill_structure")]
    [ColumnDescription("钻探-煤层结构 (原文)")]
    public string? DrillStructure { get; set; }

    [Column("drill_recovery_rate")]
    [ColumnDescription("钻探-采取率 %")]
    public double? DrillRecoveryRate { get; set; }

    [Column("drill_quality")]
    [ColumnDescription("钻探-质量评价 甲/乙/丙")]
    public string? DrillQuality { get; set; }

    // ─── 测井成果 ───
    [Column("log_end_depth")]
    [ColumnDescription("测井-止煤深度 (m)")]
    public double? LogEndDepth { get; set; }

    [Column("log_seam_thickness")]
    [ColumnDescription("测井-煤层厚度 (m)")]
    public double? LogSeamThickness { get; set; }

    [Column("log_structure")]
    [ColumnDescription("测井-煤层结构 (原文)")]
    public string? LogStructure { get; set; }

    [Column("log_quality")]
    [ColumnDescription("测井-质量评价")]
    public string? LogQuality { get; set; }

    // ─── 综合采用 ───
    [Column("overall_thickness")]
    [ColumnDescription("综合-煤层厚度 (m)")]
    public double? OverallThickness { get; set; }

    [Column("adopted_thickness")]
    [ColumnDescription("综合-采用厚度 (合计/估算 m)")]
    public double? AdoptedThickness { get; set; }

    [Column("weathered_coal_thickness")]
    [ColumnDescription("风氧化煤厚 (m)")]
    public double? WeatheredCoalThickness { get; set; }

    [Column("parting_thickness")]
    [ColumnDescription("夹石厚度 (m)")]
    public double? PartingThickness { get; set; }

    // ─── 顶底板 ───
    [Column("roof_lithology")]
    [ColumnDescription("顶板岩性 (Type B 才有)")]
    public string? RoofLithology { get; set; }

    [Column("floor_lithology")]
    [ColumnDescription("底板岩性 (Type B 才有)")]
    public string? FloorLithology { get; set; }

    [Column("floor_elevation")]
    [ColumnDescription("底板标高 (m)")]
    public double? FloorElevation { get; set; }

    [Column("overall_rating")]
    [ColumnDescription("综合级别 (钻甲/测乙/合甲)")]
    public string? OverallRating { get; set; }

    [Column("status")]
    [ColumnDescription("状态 (正常/未达/尖灭/全风化/...)")]
    public string Status { get; set; } = "正常";

    [Column("source_page")]
    [ColumnDescription("源 PDF 页")]
    public int? SourcePage { get; set; }

    [Column("source_layout")]
    [ColumnDescription("源布局 A20/A19/B17/D6")]
    public string? SourceLayout { get; set; }

    [Column("remark")]
    [ColumnDescription("备注")]
    public string? Remark { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
