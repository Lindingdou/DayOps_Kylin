// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/WeekPlanTarget.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 周计划目标（V049）——「这一周要干多少」，人定的量。
///
/// <para>
/// 口径与 <see cref="MonthlyPlan"/> 完全一致（采出万t / 剥离万m³），四周之和因此能与月计划对账。
/// <b>只有量，没有面与工序</b>：面与工序的归属是空间维（工序作业区定），本表只管时间维。
/// </para>
/// <para>
/// 键是<b>该周周一的 yyyy-MM-dd</b>，不是"第几周" —— ISO 周号在跨年那两周有两套定义，
/// 存进去以后没人能确定当初指的是哪一周。
/// </para>
/// </summary>
[Table("week_plan_target")]
[ColumnDescription("周计划目标")]
public class WeekPlanTarget
{
    [Column("monday"), PrimaryKey]
    [ColumnDescription("该周周一(yyyy-MM-dd)")]
    public string Monday { get; set; } = "";

    [Column("target_coal_wan_t")]
    [ColumnDescription("本周采出目标(万吨)")]
    public double TargetCoalWanT { get; set; }

    [Column("target_strip_wan_m3")]
    [ColumnDescription("本周剥离目标(万立方米)")]
    public double TargetStripWanM3 { get; set; }

    [Column("source")]
    [ColumnDescription("来源(人工下达/按月摊算后确认)")]
    public string Source { get; set; } = "人工下达";

    [Column("note")]
    [ColumnDescription("备注")]
    public string? Note { get; set; }

    [Column("created_at")]
    [ColumnDescription("创建时间")]
    public string? CreatedAt { get; set; }

    [Column("updated_at")]
    [ColumnDescription("更新时间")]
    public string? UpdatedAt { get; set; }

    /// <summary>两条腿都是 0 = 这一周没下过目标（不是"这周不干活"）。</summary>
    public bool HasTarget => TargetCoalWanT > 1e-9 || TargetStripWanM3 > 1e-9;
}
