using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>两点测距状态机回归。</summary>
public class MeasureStateTests
{
    [Fact]
    public void First_point_returns_null_second_returns_distance()
    {
        var m = new MeasureState();
        Assert.Null(m.AddPoint(0, 0));      // 第一点
        Assert.True(m.HasFirst);
        double? d = m.AddPoint(3, 4);       // 第二点 → 3-4-5
        Assert.NotNull(d);
        Assert.Equal(5.0, d!.Value, 6);
        Assert.False(m.HasFirst);           // 复位，可继续下一次
    }

    [Fact]
    public void Reset_clears_first()
    {
        var m = new MeasureState();
        m.AddPoint(1, 1);
        m.Reset();
        Assert.False(m.HasFirst);
        Assert.Null(m.AddPoint(2, 2));      // 复位后又从第一点开始
    }
}
