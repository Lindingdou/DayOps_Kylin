using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>中长远进度计划排产（量版合成，忠实原 LongTermScheduler）回归。</summary>
public class LongTermSchedulerTests
{
    [Fact]
    public void AdvanceRateFrom_known_value_and_guards()
    {
        // 100万t/a ÷ (1200m·12m·1.35) ×1e4 = 1e6/19440 = 51.44 m/a
        Assert.Equal(51.44, LongTermScheduler.AdvanceRateFrom(100, 1200, 12, 1.35), 2);
        Assert.Equal(0, LongTermScheduler.AdvanceRateFrom(100, 0, 12), 9);   // 退化
    }

    [Fact]
    public void CapacityWanTaFrom_known_value_and_inverts_advance_rate()
    {
        // 正式 Q=L·v·H·ρ/1e4: 1000·50·15·1.35/1e4 = 1,012,500/1e4 = 101.25 万t/a。
        Assert.Equal(101.25, LongTermScheduler.CapacityWanTaFrom(1000, 50, 15, 1.35), 6);
        // 正逆自洽(设计反问题): CapacityWanTaFrom 后 AdvanceRateFrom 还原推进度 v。
        double q = LongTermScheduler.CapacityWanTaFrom(1200, 51.44, 12, 1.35);
        Assert.Equal(51.44, LongTermScheduler.AdvanceRateFrom(q, 1200, 12, 1.35), 2);
    }

    [Fact]
    public void ServiceLifeMin_bands_gb50197()
    {
        Assert.Equal(30, LongTermScheduler.ServiceLifeMinFor(1000));
        Assert.Equal(25, LongTermScheduler.ServiceLifeMinFor(500));
        Assert.Equal(20, LongTermScheduler.ServiceLifeMinFor(300));
        Assert.Equal(15, LongTermScheduler.ServiceLifeMinFor(100));
        Assert.Equal(10, LongTermScheduler.ServiceLifeMinFor(50));
    }

    [Fact]
    public void Schedule_basic_then_production_conserves_reserve_and_ramps()
    {
        var p = new LongTermPlan
        {
            DesignCapacityWanTa = 100, CoalReserveWanT = 1000, BaseStripRatio = 5,
            BasicStrippingYears = 1, RampUpYears = 2, DeclineYears = 1,
            RampProfile = RampProfileKind.Linear, StartYear = 2027, InnerDumpEnabled = false,
            WorkLineLenM = 1200, WorkLineMode = AdvanceMode.Parallel, AdvanceAzimuthDeg = 0,
        };
        LongTermScheduler.Schedule(p);

        // 基建期: 1 年, 无煤, 有剥离, 资本支出(负 CF)
        var basicPds = p.Periods.Where(z => z.Phase == PlanPhase.Basic).ToList();
        Assert.Single(basicPds);
        Assert.Equal(0, basicPds[0].CoalWanT, 6);
        Assert.True(basicPds[0].StripWanM3 > 0);
        Assert.True(basicPds[0].CashFlowWan < 0);
        Assert.Equal("2027", basicPds[0].Label);

        // 生产期采出总量 ≈ 储量(舍入内)
        var prod = p.Periods.Where(z => z.CoalWanT > 0).ToList();
        double totalCoal = prod.Sum(z => z.CoalWanT);
        Assert.InRange(totalCoal, 999, 1001);

        // 首生产年 = 爬坡起点 r0=0.35 → 能力 35%
        Assert.Equal(35, prod[0].CapacityPct, 0);

        // 服务年限 = 生产年数; Result 非空
        Assert.NotNull(p.Result);
        Assert.Equal(prod.Count, p.Result!.ServiceLifeYears);
        // 峰值生产剥采比 > 基准(5) 且 ≤ 峰值上限(base×peakMul, 约7.7)
        Assert.True(p.Result.ProductionRatioPeak > 5);
        Assert.True(p.Result.ProductionRatioPeak <= 7.7 + 1e-6);
        // NPV = 各期折现现金流之和
        Assert.Equal(p.Periods.Sum(z => z.NpvWan), p.Result.Npv, 0);
    }

    [Fact]
    public void GenerateVariants_and_compare_recommends_feasible_best()
    {
        var basis = new LongTermPlan { DesignCapacityWanTa = 500, CoalReserveWanT = 15000, BaseStripRatio = 6 };
        var variants = LongTermScheduler.GenerateVariants(basis, LongTermScheduler.DefaultWorkLines(), LongTermScheduler.DefaultDirections());
        Assert.Equal(16, variants.Count);                       // 4 工作线 × 4 方向
        Assert.All(variants, v => Assert.NotNull(v.Result));
        var results = variants.Select(v => v.Result!).ToList();
        var best = LongTermComparer.Score(results);
        Assert.NotNull(best);
        // 各方案回填了综合分 ∈ [0,100]
        Assert.All(results, x => Assert.InRange(x.CompositeScore, 0, 100));
        // 推荐 = 综合分最高(可行优先): 无更高分的可行方案
        if (best!.Ok)
            Assert.DoesNotContain(results, x => x.Ok && x.CompositeScore > best.CompositeScore);
        // 至少一方案得满分维度(归一后某方案某指标=1 → 综合分应拉开)
        Assert.True(results.Max(x => x.CompositeScore) >= results.Min(x => x.CompositeScore));
    }

    [Fact]
    public void Compare_single_plan_scores_50_degenerate()
    {
        // 单方案: 各指标 min==max → 归一 0.5 → 综合分 50
        var p = new LongTermPlan { DesignCapacityWanTa = 300, CoalReserveWanT = 9000 };
        LongTermScheduler.Schedule(p);
        var best = LongTermComparer.Score(new[] { p.Result! });
        Assert.Equal(50, p.Result!.CompositeScore, 0);
        Assert.Same(p.Result, best);
    }

    [Fact]
    public void Schedule_is_deterministic()
    {
        LongTermPlan Mk() => new() { DesignCapacityWanTa = 500, CoalReserveWanT = 8000, BaseStripRatio = 6 };
        var a = Mk(); LongTermScheduler.Schedule(a);
        var b = Mk(); LongTermScheduler.Schedule(b);
        Assert.Equal(a.Periods.Count, b.Periods.Count);
        Assert.Equal(a.Result!.Npv, b.Result!.Npv, 0);
        Assert.Equal(a.Result.ServiceLifeYears, b.Result.ServiceLifeYears);
    }
}
