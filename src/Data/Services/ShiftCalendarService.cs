// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/ShiftCalendarService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class ShiftCalendarService : IShiftCalendarService
{
    private readonly ISqlService _sql;
    private readonly IRepository<ShiftCalendar> _repo;

    public ShiftCalendarService(ISqlService sql)
    {
        _sql = sql;
        _repo = sql.Repository<ShiftCalendar>();
    }

    public ShiftCalendar? Get(DateTime date, string shift)
        // 主键也是业务日期列 ⇒ 同样得按 yyyy-MM-dd 传（见 BusinessDate）
        => _repo.GetByKey(new { Date = BusinessDate.P(date), Shift = shift });

    public IReadOnlyList<ShiftCalendar> ByDate(DateTime date)
        => _repo.Where("date = @d ORDER BY shift", new { d = BusinessDate.P(date) });

    public IReadOnlyList<ShiftCalendar> InRange(DateTime startDate, DateTime endDate)
        => _repo.Where("date BETWEEN @s AND @e ORDER BY date, shift",
            new { s = BusinessDate.P(startDate), e = BusinessDate.P(endDate) });

    public IReadOnlyList<ShiftCalendar> BlastShifts(DateTime startDate, DateTime endDate)
        => _repo.Where("date BETWEEN @s AND @e AND is_blast_shift = 1 ORDER BY date, shift",
            new { s = BusinessDate.P(startDate), e = BusinessDate.P(endDate) });

    /// <summary>
    /// 写一条班次。<b>日期必须按 <c>yyyy-MM-dd</c> 落库</b>，不能让 Dapper 直接绑 DateTime。
    ///
    /// <para><b>实测事故</b>（2026-08-19）：`_repo.Upsert(entity)` 让 Microsoft.Data.Sqlite
    /// 把 <c>Date</c> 绑成 <c>2026-08-19 00:00:00</c>，而<b>读方全都按 <c>2026-08-19</c> 比</b>
    /// （<see cref="BusinessDate"/>）。于是同一张表、同一批数据：</para>
    /// <list type="bullet">
    /// <item><c>InRange</c> 用 <c>BETWEEN</c> —— 文本比较照样落在区间内 ⇒ <b>90 条查得到</b>；</item>
    /// <item><c>ByDate</c> 用 <c>date = @d</c> 精确相等 ⇒ <b>0 条</b>。</item>
    /// </list>
    /// <para>界面上的样子：顶部汇总写着「30 个作业日 · 90 条班次记录」，
    /// 下面的表格却是「样例班次（未入库）」—— <b>同一个窗口自己前后矛盾</b>，而没有一处报错。</para>
    ///
    /// <para><b>`BusinessDate` 原来只管读、不管写</b>，这是它留下的半边口径。
    /// 写方不归一化，读方再统一也没用。</para>
    /// </summary>
    public void Upsert(ShiftCalendar entity)
    {
        if (entity == null) return;
        _sql.Execute(
            @"INSERT INTO shift_calendar (date, shift, start_time, leader_name, is_blast_shift, weather, notes)
              VALUES (@Date, @Shift, @StartTime, @LeaderName, @IsBlastShift, @Weather, @Notes)
              ON CONFLICT(date, shift) DO UPDATE SET
                  start_time     = excluded.start_time,
                  leader_name    = excluded.leader_name,
                  is_blast_shift = excluded.is_blast_shift,
                  weather        = excluded.weather,
                  notes          = excluded.notes",
            new
            {
                Date = BusinessDate.P(entity.Date),      // ★ 这一个转换就是全部
                entity.Shift,
                entity.StartTime,
                entity.LeaderName,
                IsBlastShift = entity.IsBlastShift ? 1 : 0,
                entity.Weather,
                entity.Notes,
            });
    }
}

/// <summary>
/// 检修档期(V042)。日期列存的是 <c>yyyy-MM-dd</c> 文本（与 <c>plan_date TEXT</c> 对齐），
/// 故这里直接按字符串比，不走 <c>BusinessDate.P</c>——那是给 DateTime 列用的。
/// </summary>
internal sealed class MaintenanceWindowService : IMaintenanceWindowService
{
    private readonly IRepository<MaintenanceWindowPlan> _repo;
    public MaintenanceWindowService(ISqlService sql) => _repo = sql.Repository<MaintenanceWindowPlan>();

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd");

    public IReadOnlyList<MaintenanceWindowPlan> ByDate(DateTime date)
        => _repo.Where("plan_date = @d ORDER BY equipment_id, start_time", new { d = Key(date) });

    public IReadOnlyList<MaintenanceWindowPlan> InRange(DateTime startDate, DateTime endDate)
        => _repo.Where("plan_date BETWEEN @s AND @e ORDER BY plan_date, equipment_id, start_time",
            new { s = Key(startDate), e = Key(endDate) });

    public void Upsert(MaintenanceWindowPlan entity) => _repo.Upsert(entity);

    public void Delete(string equipmentId, DateTime date, string startTime)
        => _repo.DeleteWhere("equipment_id = @id AND plan_date = @d AND start_time = @t",
            new { id = equipmentId, d = Key(date), t = startTime });
}

/// <summary>
/// 穿孔作业计划(V044)。与 <see cref="MaintenanceWindowService"/> 同构：日期列是
/// <c>yyyy-MM-dd</c> 文本，按字符串比，不走 <c>BusinessDate.P</c>（那是给 DateTime 列用的）。
/// </summary>
internal sealed class DrillPlanService : IDrillPlanService
{
    private readonly IRepository<DrillPlan> _repo;
    public DrillPlanService(ISqlService sql) => _repo = sql.Repository<DrillPlan>();

    private static string Key(DateTime d) => d.ToString("yyyy-MM-dd");

    public IReadOnlyList<DrillPlan> ByDate(DateTime date)
        => _repo.Where("plan_date = @d ORDER BY equipment_id, start_time", new { d = Key(date) });

    public IReadOnlyList<DrillPlan> InRange(DateTime startDate, DateTime endDate)
        => _repo.Where("plan_date BETWEEN @s AND @e ORDER BY plan_date, equipment_id, start_time",
            new { s = Key(startDate), e = Key(endDate) });

    public IReadOnlyList<DrillPlan> ByZone(string zone)
        => _repo.Where("zone = @z ORDER BY plan_date, start_time", new { z = zone ?? "" });

    public void Upsert(DrillPlan entity) => _repo.Upsert(entity);

    public void Delete(string equipmentId, DateTime date, string startTime)
        => _repo.DeleteWhere("equipment_id = @id AND plan_date = @d AND start_time = @t",
            new { id = equipmentId, d = Key(date), t = startTime });
}

internal sealed class MineLocationService : IMineLocationService
{
    private readonly IRepository<MineLocation> _repo;
    public MineLocationService(ISqlService sql) => _repo = sql.Repository<MineLocation>();

    public MineLocation? Get(string locationCode) => _repo.GetByKey(locationCode);
    public IReadOnlyList<MineLocation> All(bool activeOnly = true)
        => activeOnly
            ? _repo.Where("is_active = 1 ORDER BY elevation_m")
            : _repo.All();
    public void Upsert(MineLocation entity) => _repo.Upsert(entity);
    public void Delete(string locationCode) => _repo.Delete(locationCode);
}
