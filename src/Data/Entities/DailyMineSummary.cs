// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/DailyMineSummary.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>矿山日汇总。</summary>
[Table("daily_mine_summary")]
[ColumnDescription("矿山日汇总 / 皮带+筒仓日报")]
public class DailyMineSummary
{
    [Column("date"), PrimaryKey]
    [ColumnDescription("日期")]
    public DateTime Date { get; set; }

    [Column("big_belt_coal_t")]
    [ColumnDescription("大皮带煤量(吨)")]
    public double BigBeltCoalT { get; set; }

    [Column("small_belt_coal_t")]
    [ColumnDescription("小皮带煤量(吨)")]
    public double SmallBeltCoalT { get; set; }

    [Column("longhua_coal_t")]
    [ColumnDescription("龙华煤量(吨)")]
    public double LonghuaCoalT { get; set; }

    [Column("truck_coal_export_t")]
    [ColumnDescription("卡车外运煤量(吨)")]
    public double TruckCoalExportT { get; set; }

    [Column("winnowed_coal_t")]
    [ColumnDescription("风选煤量(吨)")]
    public double WinnowedCoalT { get; set; }

    [Column("big_truck_pile_coal_t")]
    [ColumnDescription("大卡车堆煤(吨)")]
    public double BigTruckPileCoalT { get; set; }

    [Column("stripping_total_m3")]
    [ColumnDescription("日剥离总量(立方米)")]
    public double StrippingTotalM3 { get; set; }

    [Column("silo_1_t")]
    [ColumnDescription("1 号筒仓库存(吨)")]
    public double Silo1T { get; set; }

    [Column("silo_2_t")]
    [ColumnDescription("2 号筒仓库存(吨)")]
    public double Silo2T { get; set; }

    [Column("silo_3_t")]
    [ColumnDescription("3 号筒仓库存(吨)")]
    public double Silo3T { get; set; }
}
