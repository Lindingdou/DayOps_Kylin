// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IPlanService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;

namespace PitMine3D.Kylin.Data.Services;

public interface IPlanService
{
    MonthlyPlan? Get(int year, int month);
    IReadOnlyList<MonthlyPlan> ByYear(int year);
    IReadOnlyList<MonthlyPlan> All();
    void Upsert(MonthlyPlan entity);

    /// <summary>
    /// 撤掉某个月的月度计划行（连同该月电铲分配）。
    /// <para>删一期采掘单元台账时要一并调它 —— 不撤的话三维模拟读不到期次就退回这张表，
    /// 拿<b>已经删掉的那一期</b>的数凑出一帧，而面板上看着完全正常。</para>
    /// </summary>
    void Delete(int year, int month);

    IReadOnlyList<MonthlyPlanShovel> GetShovelAssignments(int year, int month);
    void ReplaceShovelAssignments(int year, int month, IEnumerable<MonthlyPlanShovel> assignments);

    // ── 周计划目标（V049）─────────────────────────────────────────────────
    //  月 → 周 → 日 这条链上"周"这一层。放在本服务里而不是另起一个：
    //  它与月计划是同一口径同一单位的量，四周之和要与月计划对账，分两个服务就会有两份取数口径。

    /// <summary>某周的目标；没下过返回 null（<b>不返回一个全 0 的对象</b>——"没下过"与"下的是 0"是两件事）。</summary>
    /// <param name="monday">该周周一 yyyy-MM-dd。</param>
    WeekPlanTarget? GetWeek(string monday);

    /// <summary>周一日期落在 [from, to] 内的周目标（含端点，按周一升序）。日期均为 yyyy-MM-dd。</summary>
    IReadOnlyList<WeekPlanTarget> WeeksInRange(string from, string to);

    /// <summary>写入/覆盖某周的目标。</summary>
    void UpsertWeek(WeekPlanTarget entity);

    /// <summary>撤掉某周的目标（读方随即退回「月量 ÷ 作业日」那条老路，并在来源文案里写明）。</summary>
    void DeleteWeek(string monday);
}
