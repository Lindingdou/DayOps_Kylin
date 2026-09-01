using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>短期(月度)生产计划排产（忠实原 ShortTermScheduler）回归。</summary>
public class ShortTermSchedulerTests
{
    [Fact]
    public void WorkdaysFor_winter_maintenance_scenario_factors()
    {
        var p = new ShortTermPlan { StandardWorkdays = 25, WinterMonthsCsv = "12,1,2", WinterDeratePct = 20, MaintenanceMonth = 7, MaintenanceDeratePct = 30 };
        p.Calendar = CalendarScenario.Standard;
        Assert.Equal(25, p.WorkdaysFor(3), 1);      // 常规月
        Assert.Equal(20, p.WorkdaysFor(1), 1);      // 冬季 25×0.8
        Assert.Equal(17.5, p.WorkdaysFor(7), 1);    // 检修 25×0.7
        p.Calendar = CalendarScenario.Push;
        Assert.Equal(27.5, p.WorkdaysFor(3), 1);    // 抢产 25×1.10
        p.Calendar = CalendarScenario.Conservative;
        Assert.Equal(23, p.WorkdaysFor(3), 1);      // 保守 25×0.92
    }

    [Fact]
    public void Schedule_conserves_annual_target_and_completes()
    {
        var p = new ShortTermPlan { AnnualCoalTargetWanT = 1000, MonthCount = 12, BaseRatio = 6.5, Dispatch = DispatchStrategy.Balanced };
        ShortTermScheduler.Schedule(p);
        Assert.Equal(12, p.Months.Count);
        double tot = p.Months.Sum(m => m.CoalWanT);
        Assert.InRange(tot, 999, 1001);                    // 年目标守恒
        Assert.NotNull(p.Result);
        Assert.InRange(p.Result!.CompletionRatePct, 99, 101);
        Assert.True(p.Result.Ok);                          // 默认参数可行(完成/上限/剥采比)
        Assert.Equal(100.0, p.Months[^1].CompletionPct, 0); // 末月累计完成 100%
        // 各月剥采比 ≤ 上限 12
        Assert.All(p.Months, m => Assert.True(m.Ratio <= 12 + 1e-6));
    }

    [Fact]
    public void Ceiling_clips_peak_and_redistributes_conserving_total()
    {
        // 集中强采(峰值高) + 紧月上限 85 → 削峰回摊, 无月超 85; 年总近守恒(原回摊限 6 迭代=近似, 紧上限略损)
        var p = new ShortTermPlan { AnnualCoalTargetWanT = 1000, MonthCount = 12, MonthlyCoalCeilingWanT = 85, Dispatch = DispatchStrategy.Concentrated };
        ShortTermScheduler.Schedule(p);
        double tot = p.Months.Sum(m => m.CoalWanT);
        Assert.InRange(tot, 990, 1001);            // 近守恒(6 迭代回摊, 紧上限损 <1%, 忠实原)
        Assert.All(p.Months, m => Assert.True(m.CoalWanT <= 85 + 0.5));   // 无月超上限(严格保证)
    }

    [Fact]
    public void GenerateVariants_nine_and_compare_recommends()
    {
        var basis = new ShortTermPlan { AnnualCoalTargetWanT = 1000 };
        var variants = ShortTermScheduler.GenerateVariants(basis, ShortTermScheduler.DefaultDispatches(), ShortTermScheduler.DefaultCalendars());
        Assert.Equal(9, variants.Count);        // 3 组织 × 3 工作历
        var results = variants.Select(v => v.Result!).ToList();
        var best = ShortTermComparer.Score(results);
        Assert.NotNull(best);
        Assert.All(results, x => Assert.InRange(x.CompositeScore, 0, 100));
        if (best!.Ok) Assert.DoesNotContain(results, x => x.Ok && x.CompositeScore > best.CompositeScore);
    }

    [Fact]
    public void Concentrated_dispatch_has_higher_output_cv_than_balanced()
    {
        var bal = new ShortTermPlan { Dispatch = DispatchStrategy.Balanced, MonthlyCoalCeilingWanT = 0 };
        var con = new ShortTermPlan { Dispatch = DispatchStrategy.Concentrated, MonthlyCoalCeilingWanT = 0 };
        ShortTermScheduler.Schedule(bal); ShortTermScheduler.Schedule(con);
        Assert.True(con.Result!.OutputCv > bal.Result!.OutputCv);   // 集中强采月间波动更大
    }
}
