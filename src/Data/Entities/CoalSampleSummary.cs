// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/CoalSampleSummary.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>每孔每层煤质平均（衍生表，可重算）。</summary>
[Table("coal_sample_summary")]
[ColumnDescription("每孔每层煤质平均")]
public class CoalSampleSummary
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("borehole_id")]
    [ColumnDescription("FK borehole.id")]
    public long BoreholeId { get; set; }

    [Column("seam_code")]
    [ColumnDescription("煤层编号")]
    public string SeamCode { get; set; } = "";

    [Column("sample_count")]
    [ColumnDescription("该层化验段数")]
    public int SampleCount { get; set; }

    [Column("avg_thickness")]
    [ColumnDescription("平均采样厚度 (m)")]
    public double? AvgThickness { get; set; }

    [Column("avg_mad_raw")]
    [ColumnDescription("原煤 Mad 平均 (%)")]
    public double? AvgMadRaw { get; set; }

    [Column("avg_mad_clean")]
    [ColumnDescription("浮煤 Mad 平均 (%)")]
    public double? AvgMadClean { get; set; }

    [Column("avg_ad_raw")]
    [ColumnDescription("原煤 Ad 平均 (%)")]
    public double? AvgAdRaw { get; set; }

    [Column("avg_ad_clean")]
    [ColumnDescription("浮煤 Ad 平均 (%)")]
    public double? AvgAdClean { get; set; }

    [Column("avg_vdaf_raw")]
    [ColumnDescription("原煤 Vdaf 平均 (%)")]
    public double? AvgVdafRaw { get; set; }

    [Column("avg_vdaf_clean")]
    [ColumnDescription("浮煤 Vdaf 平均 (%)")]
    public double? AvgVdafClean { get; set; }

    [Column("avg_fcd_raw")]
    [ColumnDescription("原煤 FCd 平均 (%)")]
    public double? AvgFcdRaw { get; set; }

    [Column("avg_fcd_clean")]
    [ColumnDescription("浮煤 FCd 平均 (%)")]
    public double? AvgFcdClean { get; set; }

    [Column("avg_std_raw")]
    [ColumnDescription("原煤 S_t,d 平均 (%)")]
    public double? AvgStdRaw { get; set; }

    [Column("avg_std_clean")]
    [ColumnDescription("浮煤 S_t,d 平均 (%)")]
    public double? AvgStdClean { get; set; }

    [Column("avg_qgr_d")]
    [ColumnDescription("Q_gr,v,d 平均 (MJ/kg)")]
    public double? AvgQgrD { get; set; }

    [Column("avg_qnet_ad")]
    [ColumnDescription("Q_net,v,ad 平均 (MJ/kg)")]
    public double? AvgQnetAd { get; set; }

    [Column("avg_caking_g")]
    [ColumnDescription("粘结指数 G 平均")]
    public double? AvgCakingG { get; set; }

    [Column("avg_plastic_y")]
    [ColumnDescription("胶质层 Y 平均 (mm)")]
    public double? AvgPlasticY { get; set; }

    [Column("avg_clean_yield")]
    [ColumnDescription("浮煤回收率平均 (%)")]
    public double? AvgCleanYield { get; set; }

    [Column("dominant_coal_type")]
    [ColumnDescription("该层最常见煤类")]
    public string? DominantCoalType { get; set; }

    [Column("is_from_source")]
    [ColumnDescription("1=PDF 原始平均行，0=程序聚合")]
    public int IsFromSource { get; set; }

    [Column("last_built_at")]
    [ColumnDescription("上次重建时间")]
    public DateTime? LastBuiltAt { get; set; }
}
