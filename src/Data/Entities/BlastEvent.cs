// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/BlastEvent.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>爆破事件 / 单次炮。</summary>
[Table("blast_event")]
[ColumnDescription("爆破事件 / 单次炮")]
public class BlastEvent
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("blast_date")]
    [ColumnDescription("爆破日期")]
    public DateTime BlastDate { get; set; }

    [Column("blast_time")]
    [ColumnDescription("爆破时刻")]
    public string? BlastTime { get; set; }

    [Column("blast_seq")]
    [ColumnDescription("当年炮次")]
    public int? BlastSeq { get; set; }

    [Column("drill_id")]
    [ColumnDescription("钻机编号")]
    public string? DrillId { get; set; }

    [Column("location_code")]
    [ColumnDescription("平盘编码")]
    public string? LocationCode { get; set; }

    [Column("material")]
    [ColumnDescription("物料类型(c4/rh 等)")]
    public string? Material { get; set; }

    [Column("diameter_mm")]
    [ColumnDescription("孔径(毫米)")]
    public double? DiameterMm { get; set; }

    [Column("hole_count")]
    [ColumnDescription("孔数")]
    public int? HoleCount { get; set; }

    [Column("total_hole_length_m")]
    [ColumnDescription("总延米")]
    public double? TotalHoleLengthM { get; set; }

    [Column("explosive_kg")]
    [ColumnDescription("装药量(千克)")]
    public double? ExplosiveKg { get; set; }

    [Column("blast_volume_m3")]
    [ColumnDescription("爆破方量(立方米)")]
    public double? BlastVolumeM3 { get; set; }

    [Column("unit_consumption_kg_m3")]
    [ColumnDescription("单耗(千克/立方米)")]
    public double? UnitConsumptionKgM3 { get; set; }
}
