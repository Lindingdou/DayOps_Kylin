// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/CoalSample.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>煤芯煤样化验明细 / 附表4（一段一条，含 3D 坐标）。</summary>
[Table("coal_sample")]
[ColumnDescription("煤芯煤样化验明细段")]
public class CoalSample
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("borehole_id")]
    [ColumnDescription("FK borehole.id")]
    public long BoreholeId { get; set; }

    [Column("seam_code")]
    [ColumnDescription("煤层编号")]
    public string SeamCode { get; set; } = "";

    // ─── 采样位置 ───
    [Column("depth_from")]
    [ColumnDescription("采样起深 (m)")]
    public double? DepthFrom { get; set; }

    [Column("depth_to")]
    [ColumnDescription("采样止深 (m)")]
    public double? DepthTo { get; set; }

    [Column("sample_thickness")]
    [ColumnDescription("采样厚度 (m)")]
    public double? SampleThickness { get; set; }

    [Column("z_sample")]
    [ColumnDescription("样品中心高程 (m, 由 z_collar - (from+to)/2 算)")]
    public double? ZSample { get; set; }

    // ─── 密度 ───
    [Column("apparent_density")]
    [ColumnDescription("视密度 t/m³")]
    public double? ApparentDensity { get; set; }

    [Column("true_density")]
    [ColumnDescription("真密度 t/m³")]
    public double? TrueDensity { get; set; }

    // ─── 工业分析 原煤 ───
    [Column("mad_raw")]
    [ColumnDescription("原煤空气干燥基水分 Mad (%)")]
    public double? MadRaw { get; set; }

    [Column("ad_raw")]
    [ColumnDescription("原煤干基灰分 Ad (%)")]
    public double? AdRaw { get; set; }

    [Column("vdaf_raw")]
    [ColumnDescription("原煤干燥无灰基挥发分 Vdaf (%)")]
    public double? VdafRaw { get; set; }

    [Column("fcd_raw")]
    [ColumnDescription("原煤干基固定碳 FCd (%)")]
    public double? FcdRaw { get; set; }

    // ─── 工业分析 浮煤 ───
    [Column("mad_clean")]
    [ColumnDescription("浮煤空气干燥基水分 Mad (%)")]
    public double? MadClean { get; set; }

    [Column("ad_clean")]
    [ColumnDescription("浮煤干基灰分 Ad (%)")]
    public double? AdClean { get; set; }

    [Column("vdaf_clean")]
    [ColumnDescription("浮煤干燥无灰基挥发分 Vdaf (%)")]
    public double? VdafClean { get; set; }

    [Column("fcd_clean")]
    [ColumnDescription("浮煤干基固定碳 FCd (%)")]
    public double? FcdClean { get; set; }

    // ─── 全硫 ───
    [Column("std_raw")]
    [ColumnDescription("原煤干基全硫 S_t,d (%)")]
    public double? StdRaw { get; set; }

    [Column("std_clean")]
    [ColumnDescription("浮煤干基全硫 S_t,d (%)")]
    public double? StdClean { get; set; }

    // ─── 发热量 ───
    [Column("qgr_d")]
    [ColumnDescription("干基弹筒发热量 Q_gr,v,d (MJ/kg)")]
    public double? QgrD { get; set; }

    [Column("qnet_ad")]
    [ColumnDescription("空气干燥基低位发热量 Q_net,v,ad (MJ/kg)")]
    public double? QnetAd { get; set; }

    // ─── 胶质层 ───
    [Column("plastic_x_mm")]
    [ColumnDescription("胶质层最大厚度 X (mm)")]
    public double? PlasticXMm { get; set; }

    [Column("plastic_y_mm")]
    [ColumnDescription("胶质层最终收缩度 Y (mm)")]
    public double? PlasticYMm { get; set; }

    [Column("plastometric_curve")]
    [ColumnDescription("胶质层曲线形状 / 熔合状况")]
    public string? PlastometricCurve { get; set; }

    // ─── 粘结 + 焦渣 ───
    [Column("caking_g")]
    [ColumnDescription("粘结指数 G")]
    public double? CakingG { get; set; }

    [Column("char_residue_raw")]
    [ColumnDescription("原煤焦渣特征 1-8")]
    public int? CharResidueRaw { get; set; }

    [Column("char_residue_clean")]
    [ColumnDescription("浮煤焦渣特征 1-8")]
    public int? CharResidueClean { get; set; }

    // ─── 洗选 + 煤类 ───
    [Column("clean_coal_yield")]
    [ColumnDescription("浮煤回收率 (%)")]
    public double? CleanCoalYield { get; set; }

    [Column("coal_type")]
    [ColumnDescription("煤类 (GB/T 5751 代号)")]
    public string? CoalType { get; set; }

    [Column("source_page")]
    [ColumnDescription("源 PDF 页")]
    public int? SourcePage { get; set; }

    [Column("remark")]
    [ColumnDescription("备注")]
    public string? Remark { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }
}
