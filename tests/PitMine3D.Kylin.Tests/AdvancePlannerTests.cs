using System;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>工作线推进几何回归（平行/定点回转/动点回转）。</summary>
public class AdvancePlannerTests
{
    // 沿 X 轴的拉沟线 (0,0)-(10,0)
    private static double[] Boxcut() => new double[] { 0, 0, 10, 0 };

    [Fact]
    public void Parallel_translates_by_step_along_azimuth()
    {
        // 方位 90°(+Y), 采宽 5, 3 步 → 每步整体 +Y 平移
        var lines = AdvancePlanner.GenerateWorkingLines(Boxcut(), AdvanceMode.Parallel, 90, 0, 0, 5, 3);
        Assert.Equal(4, lines.Count);   // 含拉沟
        Assert.Equal(0.0, lines[0][1], 6);
        Assert.Equal(5.0, lines[1][1], 6);   // 第1步 y=5
        Assert.Equal(15.0, lines[3][1], 6);  // 第3步 y=15
        Assert.Equal(0.0, lines[3][0], 6);   // x 不变
    }

    [Fact]
    public void MovingPivot_offsets_straight_line_by_step()
    {
        // 直线动点回转 = 沿法向平移采宽; 方位 90° → +Y
        var lines = AdvancePlanner.GenerateWorkingLines(Boxcut(), AdvanceMode.MovingPivot, 90, 0, 0, 5, 2);
        Assert.Equal(3, lines.Count);
        // 直线各顶点沿 +Y 移 5
        Assert.Equal(5.0, lines[1][1], 6);
        Assert.Equal(5.0, lines[1][3], 6);
        Assert.Equal(10.0, lines[2][1], 6);   // 第2步累计 +10
    }

    [Fact]
    public void FixedPivot_rotates_about_pivot_preserving_radius()
    {
        // 绕原点回转 → 各点到原点距离守恒
        var lines = AdvancePlanner.GenerateWorkingLines(Boxcut(), AdvanceMode.FixedPivot, 0, 0, 0, 2, 3);
        Assert.Equal(4, lines.Count);
        foreach (var ln in lines)
        {
            // 端点 (10,0) 初始半径 10 → 旋转后仍 10
            double r = Math.Sqrt(ln[2] * ln[2] + ln[3] * ln[3]);
            Assert.Equal(10.0, r, 6);
        }
    }

    [Fact]
    public void Invalid_inputs_return_empty_or_boxcut_only()
    {
        Assert.Empty(AdvancePlanner.GenerateWorkingLines(null!, AdvanceMode.Parallel, 0, 0, 0, 5, 3));
        Assert.Empty(AdvancePlanner.GenerateWorkingLines(new double[] { 0, 0 }, AdvanceMode.Parallel, 0, 0, 0, 5, 3));   // <4
        Assert.Empty(AdvancePlanner.GenerateWorkingLines(Boxcut(), AdvanceMode.Parallel, 0, 0, 0, 0, 3));   // stepB=0
        var one = AdvancePlanner.GenerateWorkingLines(Boxcut(), AdvanceMode.Parallel, 0, 0, 0, 5, 0);       // maxSteps=0
        Assert.Empty(one);
    }

    [Fact]
    public void Parallel_preserves_shape_length()
    {
        var lines = AdvancePlanner.GenerateWorkingLines(Boxcut(), AdvanceMode.Parallel, 90, 0, 0, 5, 1);
        // 平移不改长度：仍是 10 长的段
        double len = Math.Sqrt(Math.Pow(lines[1][2] - lines[1][0], 2) + Math.Pow(lines[1][3] - lines[1][1], 2));
        Assert.Equal(10.0, len, 6);
    }
}
