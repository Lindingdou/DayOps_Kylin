using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 创建工作线（§三六四）。原版口径：<b>工作线只表征推进方向，不设任何驱动距离</b>；
/// 由「基线 + 结束位置形态线」构成，扇形再加回转中心；方向 / 回转角与旋向都由两条线反算。
/// </summary>
public class WorkLineModelTests
{
    [Fact]
    public void 直线_推进方向是基线左法向且结束线平推()
    {
        var g = WorkLineModel.FromTwoPoints((0, 0), (100, 0), WorkLineMode.Straight, 1200)!;
        Assert.Equal(WorkLineMode.Straight, g.Mode);
        Assert.Equal((0, 1), (Math.Round(g.DirX, 9), Math.Round(g.DirY, 9)));       // 起点→终点 +x，左法向 +y
        Assert.Equal(2, g.EndLine.Count);
        Assert.All(g.EndLine, p => Assert.Equal(50, p.y, 6));                       // 默认推进距 = 基线长 × 0.5
        Assert.Null(g.Pivot);
        Assert.Equal(1200, g.LevelZ);
    }

    [Fact]
    public void 直线_右侧推进时方向取反()
    {
        var g = WorkLineModel.FromTwoPoints((0, 0), (100, 0), WorkLineMode.Straight, 0, dirSide: -1)!;
        Assert.Equal(-1, g.DirY, 9);
        Assert.All(g.EndLine, p => Assert.Equal(-50, p.y, 6));
    }

    [Fact]
    public void 直线_箭头从中点指向推进方向()
    {
        var g = WorkLineModel.FromTwoPoints((0, 0), (100, 0), WorkLineMode.Straight, 0)!;
        Assert.Equal((50, 0), (Math.Round(g.Arrow[0].x, 6), Math.Round(g.Arrow[0].y, 6)));
        Assert.True(g.Arrow[1].y > g.Arrow[0].y);
        Assert.Equal(25, g.Arrow[1].y - g.Arrow[0].y, 6);                            // 箭头长 = 基线长 × 0.25
    }

    [Fact]
    public void 扇形_有回转中心且结束线是基线绕中心转出来的()
    {
        var g = WorkLineModel.FromTwoPoints((0, 0), (100, 0), WorkLineMode.Fan, 0)!;
        Assert.NotNull(g.Pivot);
        Assert.Equal(2, g.EndLine.Count);
        // 转过之后两端到回转中心的距离不变
        var pv = g.Pivot!.Value;
        Assert.Equal(Dist(g.Base[0], pv), Dist(g.EndLine[0], pv), 6);
        Assert.Equal(Dist(g.Base[1], pv), Dist(g.EndLine[1], pv), 6);
        Assert.Equal(WorkLineModel.DefaultSweepDeg, Math.Abs(g.SweepDeg), 6);
    }

    [Fact]
    public void 扇形_旋向与推进方向一致()
    {
        // 推进往 +y（左），基线绕 a 端外侧的中心转 ⇒ 结束线中点应在基线中点的 +y 侧
        var g = WorkLineModel.FromTwoPoints((0, 0), (100, 0), WorkLineMode.Fan, 0)!;
        double emY = (g.EndLine[0].y + g.EndLine[1].y) * 0.5;
        Assert.True(emY > 0, $"结束线中点 y={emY} 应在推进侧");
    }

    [Fact]
    public void 两点重合_定不出方向返回空()
        => Assert.Null(WorkLineModel.FromTwoPoints((5, 5), (5, 5), WorkLineMode.Straight, 0));

    [Fact]
    public void 反算_直线由两条线中点差得推进方向()
    {
        var (dx, dy, sweep) = WorkLineModel.Infer(
            new List<(double x, double y)> { (0, 0), (100, 0) },
            new List<(double x, double y)> { (0, 30), (100, 30) }, null);
        Assert.Equal((0, 1), (Math.Round(dx, 9), Math.Round(dy, 9)));
        Assert.Equal(0, sweep, 9);
    }

    [Fact]
    public void 反算_扇形由两条线绕中心的角差得回转角()
    {
        var g = WorkLineModel.FromTwoPoints((0, 0), (100, 0), WorkLineMode.Fan, 0)!;
        var (_, _, sweep) = WorkLineModel.Infer(g.Base, g.EndLine, g.Pivot);
        Assert.Equal(g.SweepDeg, sweep, 6);
    }

    [Fact]
    public void 编码_与原版内核对齐直线零扇形二()
    {
        Assert.Equal(0, (byte)WorkLineMode.Straight);
        Assert.Equal(2, (byte)WorkLineMode.Fan);
    }

    private static double Dist((double x, double y) a, (double x, double y) b) => Math.Sqrt((a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y));
}
