// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/EquipmentKpiMonthly.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>设备月度指标(设备 × 年月)。</summary>
[Table("equipment_kpi_monthly")]
[ColumnDescription("设备月度指标")]
public class EquipmentKpiMonthly
{
    [Column("equipment_id"), PrimaryKey]
    [ColumnDescription("设备编号")]
    public string EquipmentId { get; set; } = "";

    [Column("year"), PrimaryKey]
    [ColumnDescription("年份")]
    public int Year { get; set; }

    [Column("month"), PrimaryKey]
    [ColumnDescription("月份 0=年汇总 1-12=月度")]
    public int Month { get; set; }

    [Column("plan_hours")]
    [ColumnDescription("计划工时")]
    public double PlanHours { get; set; }

    [Column("work_hours")]
    [ColumnDescription("实际工时")]
    public double WorkHours { get; set; }

    [Column("fault_hours")]
    [ColumnDescription("故障工时")]
    public double FaultHours { get; set; }

    [Column("idle_hours")]
    [ColumnDescription("待工工时")]
    public double IdleHours { get; set; }

    [Column("delay_hours")]
    [ColumnDescription("延误工时")]
    public double DelayHours { get; set; }

    [Column("availability")]
    [ColumnDescription("可用率(0-1)")]
    public double Availability { get; set; }

    [Column("actual_run_rate")]
    [ColumnDescription("实动率(0-1)")]
    public double ActualRunRate { get; set; }

    [Column("utilization_rate")]
    [ColumnDescription("利用率(0-1)")]
    public double UtilizationRate { get; set; }

    [Column("internal_fault_rate_pct")]
    [ColumnDescription("内因故障率(%)")]
    public double InternalFaultRatePct { get; set; }

    [Column("external_fault_rate_pct")]
    [ColumnDescription("外因故障率(%)")]
    public double ExternalFaultRatePct { get; set; }

    [NotMapped]
    public double Oee => Availability * ActualRunRate * UtilizationRate;
}
