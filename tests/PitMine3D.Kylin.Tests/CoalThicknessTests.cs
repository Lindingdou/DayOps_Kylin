using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>煤厚分析回归（岩性含「煤」的分层厚度累计）。</summary>
public class CoalThicknessTests
{
    [Fact]
    public void Sums_only_coal_intervals()
    {
        var iv = new List<(double from, double to, string rock)>
        {
            (0, 5, "表土"), (5, 8, "煤"), (8, 12, "泥岩"), (12, 14.5, "2煤"), (14.5, 20, "砂岩")
        };
        // 煤层: 5-8 (3m) + 12-14.5 (2.5m) = 5.5m
        Assert.Equal(5.5, CoalThicknessAnalyzer.CoalThickness(iv), 6);
    }

    [Fact]
    public void Zero_when_no_coal()
    {
        var iv = new List<(double from, double to, string rock)> { (0, 10, "砂岩"), (10, 20, "泥岩") };
        Assert.Equal(0.0, CoalThicknessAnalyzer.CoalThickness(iv), 6);
    }

    [Fact]
    public void Handles_reversed_and_empty()
    {
        var iv = new List<(double from, double to, string rock)> { (8, 5, "煤") };   // 自>至 → 取绝对
        Assert.Equal(3.0, CoalThicknessAnalyzer.CoalThickness(iv), 6);
        Assert.Equal(0.0, CoalThicknessAnalyzer.CoalThickness(new List<(double, double, string)>()), 6);
        Assert.Equal(0.0, CoalThicknessAnalyzer.CoalThickness(null!), 6);
    }
}
