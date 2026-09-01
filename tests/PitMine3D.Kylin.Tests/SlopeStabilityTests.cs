using System;
using PitMine3D.Kylin.Cad;
using Xunit;
using FoS = PitMine3D.Kylin.Cad.BenchParameterVerifier;

namespace PitMine3D.Kylin.Tests;

/// <summary>无黏聚力边坡安全系数 F=tanφ/tanβ 直测（既有 BenchParameterVerifier.CohesionlessFactorOfSafety, 忠实原 BenchTemplateResolver）。</summary>
public class SlopeStabilityTests
{
    [Fact]
    public void FoS_equals_tan_phi_over_tan_beta()
    {
        Assert.Equal(1.0, FoS.CohesionlessFactorOfSafety(35, 35), 6);   // φ=β → 临界 F=1
        Assert.Equal(1.0, FoS.CohesionlessFactorOfSafety(45, 45), 6);
        // β=45°(tan=1), φ=30° → F=tan30≈0.5774(<1 不稳)。
        Assert.Equal(Math.Tan(30 * Math.PI / 180), FoS.CohesionlessFactorOfSafety(45, 30), 6);
        Assert.True(FoS.CohesionlessFactorOfSafety(45, 30) < 1);
    }

    [Fact]
    public void Steeper_slope_lowers_fos_and_higher_friction_raises_it()
    {
        Assert.True(FoS.CohesionlessFactorOfSafety(50, 35) < FoS.CohesionlessFactorOfSafety(30, 35));   // β 越大 F 越小
        Assert.True(FoS.CohesionlessFactorOfSafety(40, 30) < FoS.CohesionlessFactorOfSafety(40, 38));   // φ 越大 F 越大
    }

    [Fact]
    public void Flat_slope_is_infinitely_stable()
    {
        Assert.True(double.IsPositiveInfinity(FoS.CohesionlessFactorOfSafety(0, 35)));   // 平坡 β→0
    }

    [Fact]
    public void Safe_threshold_1_30()
    {
        Assert.True(FoS.CohesionlessFactorOfSafety(20, 35) > 1.30);    // β20/φ35 → F≈1.92 安全
        Assert.True(FoS.CohesionlessFactorOfSafety(40, 35) < 1.30);    // β40/φ35 → F≈0.83 不安全
    }
}
