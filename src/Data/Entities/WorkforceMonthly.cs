// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/WorkforceMonthly.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>工种月度效率。</summary>
[Table("workforce_monthly")]
[ColumnDescription("工种月度效率")]
public class WorkforceMonthly
{
    [Column("year"), PrimaryKey]
    [ColumnDescription("年份")]
    public int Year { get; set; }

    [Column("month"), PrimaryKey]
    [ColumnDescription("月份 0=年汇总")]
    public int Month { get; set; }

    [Column("headcount")]
    [ColumnDescription("全矿在册人数")]
    public double Headcount { get; set; }

    [Column("attendance_workdays")]
    [ColumnDescription("全矿出勤工日")]
    public double AttendanceWorkdays { get; set; }

    [Column("coal_headcount")]
    [ColumnDescription("采煤系统在册")]
    public double CoalHeadcount { get; set; }

    [Column("coal_workdays")]
    [ColumnDescription("采煤系统出勤工日")]
    public double CoalWorkdays { get; set; }

    [Column("coal_output_t")]
    [ColumnDescription("月煤产量(吨)")]
    public double CoalOutputT { get; set; }

    [Column("overall_eff_t_per_workday")]
    [ColumnDescription("全矿工日效(吨/工日)")]
    public double OverallEffTPerWorkday { get; set; }

    [Column("coal_eff_t_per_workday")]
    [ColumnDescription("采煤工日效(吨/工日)")]
    public double CoalEffTPerWorkday { get; set; }

    [Column("overall_eff_t_per_person")]
    [ColumnDescription("全矿人效(吨/人)")]
    public double OverallEffTPerPerson { get; set; }

    [Column("coal_eff_t_per_person")]
    [ColumnDescription("采煤人效(吨/人)")]
    public double CoalEffTPerPerson { get; set; }
}
