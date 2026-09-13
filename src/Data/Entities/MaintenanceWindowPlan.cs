// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/MaintenanceWindowPlan.cs（逐行对应；仅命名空间适配）
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>
/// 检修档期(V042)：某台设备某天的一段**有起止时刻**的检修时窗。
///
/// <para>
/// <b>与 <c>equipment.status</c> 的分工</b>：status='Maintenance' 是「这台现在处于检修状态」，
/// 一个当前态，答不了"几点到几点"；本表是有起止的档期，能直接进装箱的有效时窗计算
/// （班起 − 检修 − 爆破清场 − 交接班损失）。两者都要：状态决定<b>该不该给它排活</b>，
/// 档期决定<b>哪几个小时排不了</b>。
/// </para>
/// <para>
/// 时刻用 <c>HH:mm</c> 文本，与 <c>shift_calendar.start_time</c>、<c>blast_event.blast_time</c>
/// 同一口径。<b>跨零点的档期请拆成两条</b>（当日 22:00–24:00 + 次日 00:00–02:00）——
/// 装箱的时窗是同一天内的 [start,end)，绕回 0 点会把次日的活算进今天。
/// </para>
/// </summary>
[Table("maintenance_window")]
[ColumnDescription("检修档期 / 设备当日检修时窗")]
public class MaintenanceWindowPlan
{
    [Column("equipment_id"), PrimaryKey]
    [ColumnDescription("设备编号(= equipment.equipment_id)")]
    public string EquipmentId { get; set; } = "";

    [Column("plan_date"), PrimaryKey]
    [ColumnDescription("检修日期 yyyy-MM-dd")]
    public string PlanDate { get; set; } = "";

    [Column("start_time"), PrimaryKey]
    [ColumnDescription("起 HH:mm")]
    public string StartTime { get; set; } = "";

    [Column("end_time")]
    [ColumnDescription("止 HH:mm(同日内;跨零点拆两条)")]
    public string EndTime { get; set; } = "";

    [Column("kind")]
    [ColumnDescription("定修 / 保养 / 临修 / 年检")]
    public string Kind { get; set; } = "定修";

    [Column("note")]
    [ColumnDescription("备注(检修内容、承修班组)")]
    public string? Note { get; set; }

    // created_at / updated_at 不映射:交给 DB DEFAULT + AFTER UPDATE 触发器。
}
