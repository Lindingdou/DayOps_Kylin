// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/DrillPlan.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 穿孔作业计划(V044)：某台钻机某天在某个待爆区的一段作业时窗。
///
/// <para>
/// <b>与 <c>blast_event</c> 的分工</b>：blast_event 是<b>已发生的事实</b>（炮次/装药/方量/单耗，事后填）；
/// 本表是<b>计划</b>（明天谁去哪打孔）。两者按 待爆区 + 日期 相互对照——
/// 有穿孔计划却迟迟没有对应炮次，就是采准脱节，这正是「钻爆计划衔接」要看的东西。
/// </para>
/// <para>
/// 时刻用 <c>HH:mm</c> 文本，与 <c>shift_calendar.start_time</c>、<c>blast_event.blast_time</c>、
/// <c>maintenance_window.start_time</c> 同一口径。<b>跨零点请拆两条</b>。
/// </para>
/// <para>
/// <see cref="BenchElevationM"/> / <see cref="HoleCount"/> / <see cref="HoleLengthM"/> 均可空：
/// <b>0 是合法值</b>（标高 0、孔数 0 都可能被人真填进来），用 null 表达"没录"。
/// </para>
/// </summary>
[Table("drill_plan")]
[ColumnDescription("穿孔作业计划 / 钻机当日作业时窗")]
public class DrillPlan
{
    [Column("equipment_id"), PrimaryKey]
    [ColumnDescription("钻机编号(= equipment.equipment_id)")]
    public string EquipmentId { get; set; } = "";

    [Column("plan_date"), PrimaryKey]
    [ColumnDescription("作业日期 yyyy-MM-dd")]
    public string PlanDate { get; set; } = "";

    [Column("start_time"), PrimaryKey]
    [ColumnDescription("起 HH:mm")]
    public string StartTime { get; set; } = "";

    [Column("end_time")]
    [ColumnDescription("止 HH:mm(同日内;跨零点拆两条)")]
    public string EndTime { get; set; } = "";

    [Column("zone")]
    [ColumnDescription("待爆区/平盘(对 blast_event.location_code 或作业面名)")]
    public string Zone { get; set; } = "";

    [Column("bench_elevation_m")]
    [ColumnDescription("台阶标高 m(NULL=未录)")]
    public double? BenchElevationM { get; set; }

    [Column("hole_count")]
    [ColumnDescription("计划孔数(NULL=未录)")]
    public int? HoleCount { get; set; }

    [Column("hole_length_m")]
    [ColumnDescription("计划延米 m(NULL=未录)")]
    public double? HoleLengthM { get; set; }

    [Column("status")]
    [ColumnDescription("计划 / 进行中 / 完成 / 取消")]
    public string Status { get; set; } = "计划";

    [Column("note")]
    [ColumnDescription("备注")]
    public string? Note { get; set; }

    // created_at / updated_at 不映射:交给 DB DEFAULT + AFTER UPDATE 触发器。
}
