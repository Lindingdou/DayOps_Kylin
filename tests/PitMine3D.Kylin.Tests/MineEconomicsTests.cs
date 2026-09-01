using System;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>矿山经济/时序基元回归（泰勒服务年限 + 等额现金流 NPV, 忠实原 PitEvaluator）。</summary>
public class MineEconomicsTests
{
    [Fact]
    public void Taylor_mine_life_known_values()
    {
        Assert.Equal(6.5, MineEconomics.TaylorMineLifeYears(1), 6);      // R=1Mt → 6.5·1
        Assert.Equal(13.0, MineEconomics.TaylorMineLifeYears(16), 6);    // 16^0.25=2 → 6.5·2=13
        Assert.Equal(0, MineEconomics.TaylorMineLifeYears(0), 6);        // 无储量 → 0
        Assert.True(MineEconomics.TaylorMineLifeYears(100) > MineEconomics.TaylorMineLifeYears(10));  // 储量↑寿命↑
    }

    [Fact]
    public void Annuity_pv_factor_and_zero_rate_degenerates_to_years()
    {
        // r=10%, T=10 → (1−1.1⁻¹⁰)/0.1 = 6.1446。
        Assert.Equal((1 - Math.Pow(1.1, -10)) / 0.1, MineEconomics.AnnuityPvFactor(0.10, 10), 6);
        Assert.Equal(10.0, MineEconomics.AnnuityPvFactor(0.0, 10), 6);   // r→0 退化为 T
        Assert.Equal(0, MineEconomics.AnnuityPvFactor(0.1, 0), 6);
    }

    [Fact]
    public void Npv_levelized_discounts_below_undiscounted_net()
    {
        // 总净值 1000, 年限 10, 折现 10% → (1000/10)·6.1446=614.46 < 1000(折现后小于名义)。
        double npv = MineEconomics.NpvLevelized(1000, 10, 0.10);
        Assert.Equal(100 * MineEconomics.AnnuityPvFactor(0.10, 10), npv, 4);
        Assert.True(npv < 1000);
        // 折现率 0 → NPV = 总净值(无折现)。
        Assert.Equal(1000, MineEconomics.NpvLevelized(1000, 10, 0.0), 4);
    }
}
