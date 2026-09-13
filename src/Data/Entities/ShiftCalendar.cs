// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Entities/ShiftCalendar.cs（逐行对应；仅命名空间适配）
using System;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Entities;

/// <summary>班次日历(日 × 班)。</summary>
[Table("shift_calendar")]
[ColumnDescription("班次日历 / 每日 A B C 三班配置")]
public class ShiftCalendar
{
    [Column("date"), PrimaryKey]
    [ColumnDescription("日期")]
    public DateTime Date { get; set; }

    [Column("shift"), PrimaryKey]
    [ColumnDescription("班次 A/B/C")]
    public string Shift { get; set; } = "";

    [Column("start_time")]
    [ColumnDescription("开班时间")]
    public string? StartTime { get; set; }

    [Column("leader_name")]
    [ColumnDescription("班长姓名")]
    public string? LeaderName { get; set; }

    [Column("is_blast_shift")]
    [ColumnDescription("是否爆破班")]
    public bool IsBlastShift { get; set; }

    [Column("weather")]
    [ColumnDescription("天气状况")]
    public string? Weather { get; set; }

    [Column("notes")]
    [ColumnDescription("备注")]
    public string? Notes { get; set; }
}
