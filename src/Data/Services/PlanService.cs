// 忠实移植自原 PitMine3D Modules/GeoDataBase/Domain/Services/PlanService.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Data.Services;
using PitMine3D.Kylin.Data.Sql;

namespace PitMine3D.Kylin.Data.Services;

internal sealed class PlanService : IPlanService
{
    private readonly ISqlService _sql;
    private readonly IRepository<MonthlyPlan> _planRepo;
    private readonly IRepository<MonthlyPlanShovel> _shovelRepo;
    private readonly IRepository<WeekPlanTarget> _weekRepo;

    public PlanService(ISqlService sql)
    {
        _sql = sql;
        _planRepo = sql.Repository<MonthlyPlan>();
        _shovelRepo = sql.Repository<MonthlyPlanShovel>();
        _weekRepo = sql.Repository<WeekPlanTarget>();
    }

    public MonthlyPlan? Get(int year, int month) => _planRepo.GetByKey(new { Year = year, Month = month });
    public IReadOnlyList<MonthlyPlan> ByYear(int year) => _planRepo.Where("year = @y ORDER BY month", new { y = year });
    public IReadOnlyList<MonthlyPlan> All() => _planRepo.All();
    public void Upsert(MonthlyPlan entity) => _planRepo.Upsert(entity);

    /// <summary>
    /// 撤掉某个月的月度计划行（<b>连同该月的电铲分配</b>）。
    /// <para><b>为什么要有它</b>：删掉一期的采掘单元台账时，这张表里那一行不撤的话，
    /// 三维模拟读不到期次就会退回这张表 —— 拿<b>已经删掉的那一期</b>的采出/剥离凑出一帧，
    /// 面板上看着完全正常（实测：采出 0、剥离 381 万m³、采排不守恒 −27.5%），
    /// 人会以为是算法坏了。删就要整条链一起删。</para>
    /// </summary>
    public void Delete(int year, int month)
    {
        using var tx = _sql.BeginTransaction();
        _sql.Execute("DELETE FROM monthly_plan_shovel WHERE year = @y AND month = @m", new { y = year, m = month });
        _sql.Execute("DELETE FROM monthly_plan WHERE year = @y AND month = @m", new { y = year, m = month });
        tx.Commit();
    }

    public IReadOnlyList<MonthlyPlanShovel> GetShovelAssignments(int year, int month)
        => _shovelRepo.Where("year = @y AND month = @m", new { y = year, m = month });

    public void ReplaceShovelAssignments(int year, int month, IEnumerable<MonthlyPlanShovel> assignments)
    {
        using var tx = _sql.BeginTransaction();
        _sql.Execute("DELETE FROM monthly_plan_shovel WHERE year = @y AND month = @m",
            new { y = year, m = month });
        foreach (var a in assignments)
        {
            a.Year = year;
            a.Month = month;
            _shovelRepo.Insert(a);
        }
        tx.Commit();
    }

    // ── 周计划目标（V049）─────────────────────────────────────────────────

    public WeekPlanTarget? GetWeek(string monday)
        // 单主键传标量（复合主键才传匿名对象）—— 传匿名对象时 Dapper 会把它当参数值本身，直接抛
        => string.IsNullOrWhiteSpace(monday) ? null : _weekRepo.GetByKey(monday.Trim());

    /// <summary>
    /// 区间查。日期是 <c>yyyy-MM-dd</c> 定长文本，字典序即时间序，所以 BETWEEN 是安全的。
    /// <para>⚠ 写方必须保证存进去的就是 <c>yyyy-MM-dd</c> —— <c>shift_calendar</c> 上栽过一次：
    /// 存量 93 行写成了 <c>'2026-08-19 00:00:00'</c>，按 <c>=</c> 精确比查 0 条、按 BETWEEN 照样 93 条，
    /// 同一张表两个读法一个有一个没有。</para>
    /// </summary>
    public IReadOnlyList<WeekPlanTarget> WeeksInRange(string from, string to)
        => _weekRepo.Where("monday BETWEEN @a AND @b ORDER BY monday",
                           new { a = (from ?? "").Trim(), b = (to ?? "").Trim() });

    public void UpsertWeek(WeekPlanTarget entity)
    {
        if (entity == null || string.IsNullOrWhiteSpace(entity.Monday)) return;
        entity.Monday = entity.Monday.Trim();
        _weekRepo.Upsert(entity);
    }

    public void DeleteWeek(string monday)
    {
        if (string.IsNullOrWhiteSpace(monday)) return;
        _sql.Execute("DELETE FROM week_plan_target WHERE monday = @d", new { d = monday.Trim() });
    }
}
