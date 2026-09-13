// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/LongTermMetric.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>长周期指标(EAV,合并 production_history + pit_history + energy_consumption)。</summary>
[Table("long_term_metric")]
[ColumnDescription("长周期指标(EAV)")]
public class LongTermMetric
{
    [Column("id"), PrimaryKey, AutoIncrement]
    public long Id { get; set; }

    [Column("source")]
    [ColumnDescription("数据源(production/pit/energy)")]
    public string Source { get; set; } = "";

    [Column("item")]
    [ColumnDescription("指标名称")]
    public string Item { get; set; } = "";

    [Column("unit")]
    [ColumnDescription("单位")]
    public string? Unit { get; set; }

    [Column("year")]
    [ColumnDescription("年份")]
    public int Year { get; set; }

    [Column("month")]
    [ColumnDescription("月份(NULL=年度 / 1-12=月度)")]
    public int? Month { get; set; }

    [Column("value")]
    [ColumnDescription("数值")]
    public double Value { get; set; }
}
