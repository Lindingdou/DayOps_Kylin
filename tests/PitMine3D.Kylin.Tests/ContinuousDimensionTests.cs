using Xunit;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Views;

namespace PitMine3D.Kylin.Tests;

public sealed class ContinuousDimensionTests
{
    [Fact]
    public void 连续标注落位后回到拾取点_不串联上一个端点()
    {
        Assert.Equal(DimensionNextStep.PickPoints, DimensionContinuation.AfterPlacement(continuing: true));
    }

    [Fact]
    public void 单次对齐标注落位后结束命令()
    {
        Assert.Equal(DimensionNextStep.Finish, DimensionContinuation.AfterPlacement(continuing: false));
    }

    [Fact]
    public void 连续标注复用首条偏移高度_后续鼠标远近不再造成错落()
    {
        var segment = new DimensionSegment(0, 0, 100, 0);
        var first = DimensionContinuation.ResolveOffset(segment, 35, 24, lockedDistance: null);
        var next = DimensionContinuation.ResolveOffset(segment, 60, 200, first.Distance);

        Assert.Equal(50, first.X, 9);
        Assert.Equal(24, first.Y, 9);
        Assert.Equal(24, first.Distance, 9);
        Assert.Equal(50, next.X, 9);
        Assert.Equal(24, next.Y, 9);
    }

    [Fact]
    public void 连续标注复用高度时仍可由鼠标选择尺寸线所在一侧()
    {
        var segment = new DimensionSegment(0, 0, 100, 0);
        var offset = DimensionContinuation.ResolveOffset(segment, 60, -200, lockedDistance: 24);

        Assert.Equal(50, offset.X, 9);
        Assert.Equal(-24, offset.Y, 9);
        Assert.Equal(24, offset.Distance, 9);
    }

    [Fact]
    public void 点击直线直接取两端点()
    {
        var line = new LineEntity { X0 = 10, Y0 = 20, X1 = 30, Y1 = 40 };

        var target = Assert.IsType<DimensionSegment>(DimensionObjectTarget.Find(line, 22, 31));

        Assert.Equal(new DimensionSegment(10, 20, 30, 40), target);
    }

    [Fact]
    public void 点击多段线只取离鼠标最近的那一段()
    {
        var line = new PolylineEntity();
        line.Points.AddRange(new[] { (0d, 0d), (100d, 0d), (100d, 80d) });

        var target = Assert.IsType<DimensionSegment>(DimensionObjectTarget.Find(line, 98, 50));

        Assert.Equal(new DimensionSegment(100, 0, 100, 80), target);
    }

    [Fact]
    public void 闭合多段线的封口边也可标注()
    {
        var line = new PolylineEntity { Closed = true };
        line.Points.AddRange(new[] { (0d, 0d), (100d, 0d), (100d, 80d) });

        var target = Assert.IsType<DimensionSegment>(DimensionObjectTarget.Find(line, 20, 18));

        Assert.Equal(new DimensionSegment(100, 80, 0, 0), target);
    }

    [Fact]
    public void 圆弧不能被当成不可见弦做对齐标注()
    {
        var arc = new ArcEntity { X1 = 0, Y1 = 0, X2 = 50, Y2 = 50, X3 = 100, Y3 = 0 };

        Assert.Null(DimensionObjectTarget.Find(arc, 50, 50));
    }

    [Fact]
    public void 线性标注最后一次拾取点决定标注线落位()
    {
        var horizontal = DimensionPlacement.ResolveLinearAxis(0, 0, 100, 20, 80, 70);
        var vertical = DimensionPlacement.ResolveLinearAxis(0, 0, 100, 20, 160, 10);

        Assert.True(horizontal.Horizontal);
        Assert.Equal(70, horizontal.OffsetY, 9);
        Assert.Equal(50, horizontal.OffsetX, 9);
        Assert.False(vertical.Horizontal);
        Assert.Equal(160, vertical.OffsetX, 9);
        Assert.Equal(10, vertical.OffsetY, 9);
    }
}
