using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 拉沟·推进候选推荐(PanelDelineator)+ StripRatioField 富化(埋深/煤厚/剥采比/列中心)已知值回归。
/// 忠实原 PlanLib.BoundaryOptimization.PanelDelineator —— 从剥采比场按六约束打分自动荐首采区拉沟位置+推进方位。
/// </summary>
public class PanelDelineatorTests
{
    // ── StripRatioField 富化(§FromBlocks 补 CoalThickM/DepthToCoalM/StripRatioAt/Center) ──
    [Fact]
    public void StripRatioField_enriched_fields_known_values()
    {
        // 单列 3 层: Z=0 煤 / Z=10 煤 / Z=20 岩 (cell 10, cutoff .5, ρ=1)。
        var blocks = new List<(double X, double Y, double Z, double Size, double Grade)>
        {
            (0, 0, 0, 10, 1.0), (0, 0, 10, 10, 1.0), (0, 0, 20, 10, 0.0),
        };
        var f = StripRatioField.FromBlocks(blocks, cutoff: 0.5, density: 1.0)!;
        Assert.Equal(1, f.Nx);
        Assert.Equal(1, f.Ny);
        Assert.Equal(0.5, f.StripRatioAt(0, 0), 6);      // 岩1000/(煤2000·1)=0.5
        Assert.Equal(20.0, f.CoalThickM[0], 6);          // 2 煤块×10
        Assert.Equal(10.0, f.DepthToCoalM[0], 6);        // 顶 Z=20 − 顶煤 Z=10
        Assert.Equal(25.0, f.TopZ, 6);                   // maxZ 20 + cell/2
        Assert.Equal(1, f.CoalColumns);
        var (cx, cy) = f.Center(0, 0);
        Assert.Equal(0.0, cx, 6);                        // Ox(-5)+0.5·10
        Assert.Equal(0.0, cy, 6);
    }

    [Fact]
    public void StripRatioField_no_coal_column_infinite_sr_full_depth()
    {
        // 单列纯岩 → SR=+∞, 埋深=满列高。
        var blocks = new List<(double X, double Y, double Z, double Size, double Grade)>
        {
            (0, 0, 0, 10, 0.0), (0, 0, 10, 10, 0.0),
        };
        var f = StripRatioField.FromBlocks(blocks, cutoff: 0.5, density: 1.0)!;
        Assert.True(double.IsPositiveInfinity(f.StripRatioAt(0, 0)));
        Assert.Equal(0, f.CoalColumns);
        Assert.Equal(20.0, f.DepthToCoalM[0], 6);        // (maxZ 10 − minZ 0) + cell 10
    }

    // 剥采比南低北高梯度场: 列(i,j) = Z=0 煤 + j 个岩块 → SR = j。
    private static StripRatioField GradientField()
    {
        var blocks = new List<(double X, double Y, double Z, double Size, double Grade)>();
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
            {
                blocks.Add((i * 10, j * 10, 0, 10, 1.0));           // 煤底
                for (int k = 0; k < j; k++)
                    blocks.Add((i * 10, j * 10, 10 + k * 10, 10, 0.0));  // j 个岩 → SR=j
            }
        return StripRatioField.FromBlocks(blocks, cutoff: 0.5, density: 1.0)!;
    }

    [Fact]
    public void Recommend_returns_five_options_with_one_recommended()
    {
        var f = GradientField();
        var opts = PanelDelineator.Recommend(f, minWorkingLineM: 40);
        Assert.Equal(5, opts.Count);                     // 4 边 + 1 梯度候选
        Assert.All(opts, o => Assert.InRange(o.TotalScore, 0, 100));
        Assert.Single(opts, o => o.Recommended);         // 恰一个推荐
        var rec = opts.First(o => o.Recommended);
        Assert.True(rec.Feasible);
        Assert.Equal(opts.Where(o => o.Feasible).Max(o => o.TotalScore), rec.TotalScore, 6);  // 推荐=最高分可行
    }

    [Fact]
    public void Recommend_low_strip_ratio_edge_scores_higher()
    {
        // 南带 SR=0(最低) → SrScore=1; 北带 SR=4(最高) → SrScore=0。
        var f = GradientField();
        var opts = PanelDelineator.Recommend(f, minWorkingLineM: 40);
        var south = opts.First(o => o.Name.Contains("南端"));
        var north = opts.First(o => o.Name.Contains("北端"));
        Assert.Equal(1.0, south.SrScore, 6);
        Assert.Equal(0.0, north.SrScore, 6);
        Assert.True(south.SrScore > north.SrScore);      // 低剥采比边评分更高
        Assert.Equal(0.0, south.AdvanceAzimuthDeg, 6);   // 南端拉沟·向北推 = 方位 0
    }

    [Fact]
    public void Recommend_all_infeasible_when_working_line_too_short()
    {
        // 最小工作线长 1000 > 场宽 50 → 全不可行, 无推荐。
        var f = GradientField();
        var opts = PanelDelineator.Recommend(f, minWorkingLineM: 1000);
        Assert.All(opts, o => Assert.False(o.Feasible));
        Assert.DoesNotContain(opts, o => o.Recommended);
    }

    [Fact]
    public void Recompute_weights_normalize_to_hundred()
    {
        // 全分=1 的候选按任意权重 → TotalScore=100(加权和/权重和×100)。
        var o = new BoxcutAdvanceOption
        {
            SrScore = 1, ShallowScore = 1, HaulScore = 1, InnerDumpScore = 1, WorkLineScore = 1, GeoScore = 1,
        };
        o.Recompute(BoxcutWeights.CreateDefault());
        Assert.Equal(100.0, o.TotalScore, 6);
    }
}
