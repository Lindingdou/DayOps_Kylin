using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>煤/岩判别器（类别码集 + 容差, 忠实原 CoalRockClassifier）回归。</summary>
public class CoalRockClassifierTests
{
    [Fact]
    public void CoalCodes_within_tolerance_are_coal()
    {
        var c = new CoalRockClassifier { CoalCodes = new double[] { 1, 3, 5 }, Tol = 0.5 };
        Assert.True(c.IsCoal(1));
        Assert.True(c.IsCoal(3.4));    // 3±0.5
        Assert.False(c.IsCoal(2));     // 2 距 1/3 均 >0.5
        Assert.True(c.IsCoal(5.5));    // 5+0.5 边界
    }

    [Fact]
    public void Empty_rock_set_means_non_coal_is_rock()
    {
        var c = new CoalRockClassifier { CoalCodes = new double[] { 1 }, RockCodes = System.Array.Empty<double>(), Tol = 0.5 };
        Assert.True(c.IsRock(9));      // 非煤 → 岩
        Assert.False(c.IsRock(1));     // 煤 → 非岩
    }

    [Fact]
    public void Explicit_rock_set_makes_neither_ignored()
    {
        // 煤码{1}, 岩码{2}, 容差0.5 → 值5 既非煤又非岩 = 忽略(IsCoal 与 IsRock 均 false)
        var c = new CoalRockClassifier { CoalCodes = new double[] { 1 }, RockCodes = new double[] { 2 }, Tol = 0.5 };
        Assert.True(c.IsCoal(1)); Assert.False(c.IsRock(1));
        Assert.True(c.IsRock(2)); Assert.False(c.IsCoal(2));
        Assert.False(c.IsCoal(5)); Assert.False(c.IsRock(5));   // 忽略
    }

    [Fact]
    public void Tolerance_widens_match()
    {
        var c = new CoalRockClassifier { CoalCodes = new double[] { 10 }, Tol = 2 };
        Assert.True(c.IsCoal(11.9));   // 10±2
        Assert.False(c.IsCoal(12.5));
    }
}
