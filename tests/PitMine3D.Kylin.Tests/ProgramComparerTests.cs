using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>开采程序方案比选回归（移植自 PlanLib.ProgramComparer）。</summary>
public class ProgramComparerTests
{
    // 各准则：peak/basic/ttc 低优；inner/life/bal/npv 高优
    private static ProgramComparer.Plan P(string n, double peak, double basic, double inner,
        double ttc, double life, double bal, double npv, bool ok = true)
        => new(n, peak, basic, inner, ttc, life, bal, npv, ok);

    [Fact]
    public void All_best_plan_scores_100_and_is_recommended()
    {
        var plans = new List<ProgramComparer.Plan>
        {
            P("A", 1, 1, 100, 1, 50, 1.0, 100),   // 每项都最优
            P("B", 10, 10, 10, 10, 10, 0.1, 10),  // 每项都最差
        };
        var (scored, best) = ProgramComparer.Score(plans);
        Assert.Equal("A", best);
        Assert.Equal(100, scored.First(s => s.Name == "A").CompositeScore, 0);
        Assert.Equal(0, scored.First(s => s.Name == "B").CompositeScore, 0);
    }

    [Fact]
    public void Low_is_better_for_peak_stripping()
    {
        // 仅峰值剥采比不同(A 低=优)，其余全相等 → A 胜
        var plans = new List<ProgramComparer.Plan>
        {
            P("A", 1, 5, 5, 5, 5, 5, 5),
            P("B", 9, 5, 5, 5, 5, 5, 5),
        };
        var (_, best) = ProgramComparer.Score(plans);
        Assert.Equal("A", best);
    }

    [Fact]
    public void Single_plan_scores_neutral_50()
    {
        var (scored, best) = ProgramComparer.Score(new List<ProgramComparer.Plan> { P("Solo", 3, 3, 3, 3, 3, 3, 3) });
        Assert.Single(scored);
        Assert.Equal("Solo", best);
        Assert.Equal(50, scored[0].CompositeScore, 0);   // 单方案各项归一=0.5 → 50 分
    }

    [Fact]
    public void Infeasible_top_score_yields_to_feasible()
    {
        var plans = new List<ProgramComparer.Plan>
        {
            P("A", 1, 1, 100, 1, 50, 1.0, 100, ok: false),  // 分最高但不可行
            P("B", 10, 10, 10, 10, 10, 0.1, 10, ok: true),  // 分最低但可行
        };
        var (_, best) = ProgramComparer.Score(plans);
        Assert.Equal("B", best);                            // 推荐最高分【且可行】者
    }
}
