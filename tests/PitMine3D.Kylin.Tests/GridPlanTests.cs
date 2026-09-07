using System;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>自适应格网(AutoCAD 式)：1-2-5 换挡、主线对齐世界原点、铺满可见区、线数有上限。</summary>
public class GridPlanTests
{
    [Fact]
    public void Nice125_SnapsToDecadeSteps()
    {
        Assert.Equal(1, GridPlanner.Nice125(0.7));
        Assert.Equal(2, GridPlanner.Nice125(1.3));
        Assert.Equal(5, GridPlanner.Nice125(4.2));
        Assert.Equal(10, GridPlanner.Nice125(6));
        Assert.Equal(0.5, GridPlanner.Nice125(0.31), 9);
        Assert.Equal(200, GridPlanner.Nice125(120));
        Assert.Equal(1, GridPlanner.Nice125(0));       // 退化输入不崩
    }

    [Fact]
    public void MinorSpacing_KeepsScreenGapAboveMinimum_AcrossZoomDecades()
    {
        foreach (double wpp in new[] { 0.001, 0.01, 0.5, 3.7, 42, 1500 })
        {
            var p = GridPlanner.For(wpp, wpp * 1800, wpp * 1000, 0, 0, minPx: 10);
            double gapPx = p.Minor / wpp;
            Assert.True(gapPx >= 10, $"wpp={wpp} 细线屏幕间距 {gapPx:0.#}px 应 ≥10");
            Assert.True(gapPx < 100, $"wpp={wpp} 细线屏幕间距 {gapPx:0.#}px 不应过疏");
            Assert.Equal(p.Minor * 5, p.Major, 9);
        }
    }

    [Fact]
    public void Coverage_AlignsToMajor_AndContainsView()
    {
        double wpp = 1;   // 1 世界单位/像素
        var p = GridPlanner.For(wpp, 1800, 1000, 12345.6, -987.4, minPx: 10);
        // 主线对齐世界原点：边界是 major 的整数倍
        Assert.Equal(0, p.X0 % p.Major, 6);
        Assert.Equal(0, p.Y1 % p.Major, 6);
        // 罩住可见区
        Assert.True(GridPlanner.Covers(p, 12345.6, -987.4, 1800, 1000));
        Assert.True(p.X0 <= 12345.6 - 900 && p.X1 >= 12345.6 + 900);
        // 平移出范围后需要重建
        Assert.False(GridPlanner.Covers(p, 12345.6 + 3000, -987.4, 1800, 1000));
    }

    [Fact]
    public void LineCount_Capped_ByPromotingSpacing()
    {
        // 极端: 视野很大而像素尺度很小 → 若不升挡会有几十万条线
        var p = GridPlanner.For(0.001, 1_000_000, 1_000_000, 0, 0, minPx: 10, maxLines: 4000);
        Assert.True(p.TotalLines <= 4200, $"线数 {p.TotalLines} 应受上限约束");
        Assert.True(p.Minor > 0.01);
        // 3D 用 coverage 放大覆盖范围, 线数仍受控
        var p3 = GridPlanner.For(1, 1800, 1000, 0, 0, coverage: 3, maxLines: 4000);
        Assert.True(p3.X1 - p3.X0 >= 1800 * 3);
        Assert.True(p3.TotalLines <= 4200);
    }

    [Fact]
    public void IsMajor_MarksEveryNthLine()
    {
        var p = GridPlanner.For(1, 1800, 1000, 0, 0, minPx: 10, majorEvery: 5);
        // 起点已对齐主线 → 第 0、5、10… 条是主线, 其余是细线
        for (int i = 0; i < p.VerticalLines; i++)
        {
            double x = p.X0 + i * p.Minor;
            Assert.Equal(i % 5 == 0, GridPlanner.IsMajor(x, p.Major));
        }
        Assert.True(p.VerticalLines > 20 && p.HorizontalLines > 10);
    }
}
