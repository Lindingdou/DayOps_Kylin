using System;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>无黏聚力边坡安全系数回归（F=tanφ/tanβ, 忠实原 CohesionlessFactorOfSafety）。</summary>
public class SlopeStabilityTests
{
    [Fact]
    public void FoS_equals_tan_phi_over_tan_beta()
    {
        // φ=β → F=1(临界)。
        Assert.Equal(1.0, SlopeStability.CohesionlessFoS(35, 35), 6);
        // φ=45°,β=45° → tan45/tan45=1。
        Assert.Equal(1.0, SlopeStability.CohesionlessFoS(45, 45), 6);
        // 已知值: β=45°(tan=1), φ=30°(tan≈0.5774) → F≈0.5774(<1 不稳)。
        Assert.Equal(Math.Tan(30 * Math.PI / 180), SlopeStability.CohesionlessFoS(45, 30), 6);
        Assert.True(SlopeStability.CohesionlessFoS(45, 30) < 1);
    }

    [Fact]
    public void Steeper_slope_lowers_fos_and_higher_friction_raises_it()
    {
        // 同 φ=35°: 帮坡越陡(β 越大)F 越小。
        Assert.True(SlopeStability.CohesionlessFoS(50, 35) < SlopeStability.CohesionlessFoS(30, 35));
        // 同 β=40°: 内摩擦角越大 F 越大。
        Assert.True(SlopeStability.CohesionlessFoS(40, 30) < SlopeStability.CohesionlessFoS(40, 38));
    }

    [Fact]
    public void Flat_slope_is_infinitely_stable()
    {
        Assert.True(double.IsPositiveInfinity(SlopeStability.CohesionlessFoS(0, 35)));   // 平坡 β→0
    }

    [Fact]
    public void Safe_threshold_1_30()
    {
        // β=20°(tan≈0.364), φ=35°(tan≈0.700) → F≈1.92 ≥1.30 安全。
        double f = SlopeStability.CohesionlessFoS(20, 35);
        Assert.True(f > 1.30);
        Assert.True(SlopeStability.IsSafe(f));
        // β=40°, φ=35° → F≈0.83 <1.30 不安全。
        Assert.False(SlopeStability.IsSafe(SlopeStability.CohesionlessFoS(40, 35)));
        Assert.Equal(1.30, SlopeStability.SafeThreshold, 6);
    }
}
