// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IShiftCalendarService.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

public interface IShiftCalendarService
{
    ShiftCalendar? Get(DateTime date, string shift);
    IReadOnlyList<ShiftCalendar> ByDate(DateTime date);
    IReadOnlyList<ShiftCalendar> InRange(DateTime startDate, DateTime endDate);
    IReadOnlyList<ShiftCalendar> BlastShifts(DateTime startDate, DateTime endDate);
    void Upsert(ShiftCalendar entity);
}

/// <summary>
/// 检修档期(V042)。装箱的有效时窗要扣它——三个减项（检修 / 爆破清场 / 交接班）里，
/// 此前只有它没有表，于是台账模式下 <c>ExploderConfig.Maintenance</c> 恒为空。
/// </summary>
public interface IMaintenanceWindowService
{
    /// <summary>某日全部档期（按设备、起始时刻排）。</summary>
    IReadOnlyList<MaintenanceWindowPlan> ByDate(DateTime date);

    /// <summary>日期区间内的档期（周计划/月度检修视图用）。</summary>
    IReadOnlyList<MaintenanceWindowPlan> InRange(DateTime startDate, DateTime endDate);

    /// <summary>新增或更新（主键 = 设备 × 日期 × 起始时刻）。</summary>
    void Upsert(MaintenanceWindowPlan entity);

    /// <summary>删除一条档期。</summary>
    void Delete(string equipmentId, DateTime date, string startTime);
}

/// <summary>
/// 穿孔作业计划(V044)。工序链「穿孔 → 爆破 → 采装」里，穿孔此前**没有表**：
/// 装配层明写"穿孔计划暂无台账，本日不排"，于是真库上钻机一条任务都排不出来。
/// </summary>
public interface IDrillPlanService
{
    /// <summary>某日全部穿孔计划（按设备、起始时刻排）。</summary>
    IReadOnlyList<DrillPlan> ByDate(DateTime date);

    /// <summary>日期区间内的穿孔计划（周计划 / 采准接续视图用）。</summary>
    IReadOnlyList<DrillPlan> InRange(DateTime startDate, DateTime endDate);

    /// <summary>某个待爆区的穿孔计划（钻爆衔接：这个区的孔谁在打、打完没有）。</summary>
    IReadOnlyList<DrillPlan> ByZone(string zone);

    /// <summary>新增或更新（主键 = 钻机 × 日期 × 起始时刻）。</summary>
    void Upsert(DrillPlan entity);

    /// <summary>删除一条穿孔计划。</summary>
    void Delete(string equipmentId, DateTime date, string startTime);
}

public interface IMineLocationService
{
    MineLocation? Get(string locationCode);
    IReadOnlyList<MineLocation> All(bool activeOnly = true);
    void Upsert(MineLocation entity);
    void Delete(string locationCode);
}
