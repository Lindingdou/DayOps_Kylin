using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Tasks;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>TaskLib 煤质/配煤回归（忠实移植 CoalQuality：达标判定 + 按吨量加权混合）。</summary>
public class CoalQualityTests
{
    [Fact]
    public void MeetsTarget_within_tolerance()
    {
        var target = new CoalQuality { AshPct = 25, CalorificMJkg = 20, SulfurPct = 0.8 };
        // 灰 25.5≤26 · 热 19.5≥19 · 硫 0.85≤0.9 → 达标(默认容差 1/1/0.1)
        var ok = new CoalQuality { AshPct = 25.5, CalorificMJkg = 19.5, SulfurPct = 0.85 };
        Assert.True(ok.MeetsTarget(target));
        // 灰超限
        var badAsh = new CoalQuality { AshPct = 27, CalorificMJkg = 20, SulfurPct = 0.8 };
        Assert.False(badAsh.MeetsTarget(target));
        // 热值不足
        var badCv = new CoalQuality { AshPct = 25, CalorificMJkg = 18.5, SulfurPct = 0.8 };
        Assert.False(badCv.MeetsTarget(target));
    }

    [Fact]
    public void Blend_weighted_by_tonnage()
    {
        var blended = CoalQuality.Blend(new List<(double, CoalQuality)>
        {
            (100, new CoalQuality { AshPct = 20, CalorificMJkg = 22, SulfurPct = 0.5 }),
            (300, new CoalQuality { AshPct = 30, CalorificMJkg = 18, SulfurPct = 1.0 }),
        });
        Assert.Equal(27.5, blended.AshPct, 4);        // (100×20+300×30)/400
        Assert.Equal(19.0, blended.CalorificMJkg, 4); // (100×22+300×18)/400
        Assert.Equal(0.875, blended.SulfurPct, 4);
    }

    [Fact]
    public void Blend_empty_or_zero_tonnage()
    {
        Assert.Equal(0, CoalQuality.Blend(new List<(double, CoalQuality)>()).AshPct, 6);
        Assert.Equal(0, CoalQuality.Blend(new List<(double, CoalQuality)> { (0, new CoalQuality { AshPct = 20 }) }).AshPct, 6);
    }
}
