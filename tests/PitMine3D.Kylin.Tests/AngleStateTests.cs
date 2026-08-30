using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三点测角回归。</summary>
public class AngleStateTests
{
    [Fact]
    public void Right_angle_is_90()
    {
        // 顶点(0,0)，射线 →(1,0) 与 →(0,1)
        double d = AngleMath.AngleDeg(0, 0, 1, 0, 0, 1);
        Assert.Equal(90, d, 4);
    }

    [Fact]
    public void Straight_angle_is_180()
    {
        double d = AngleMath.AngleDeg(0, 0, 1, 0, -1, 0);
        Assert.Equal(180, d, 4);
    }

    [Fact]
    public void Reflex_folds_to_under_180()
    {
        // 270° 的几何等价 90°
        double d = AngleMath.AngleDeg(0, 0, 1, 0, 0, -1);
        Assert.Equal(90, d, 4);
    }

    [Fact]
    public void State_machine_returns_on_third_point_then_resets()
    {
        var s = new AngleState();
        Assert.Null(s.AddPoint(0, 0));   // 顶点
        Assert.True(s.HasVertex);
        Assert.Null(s.AddPoint(1, 0));   // 第一边
        Assert.True(s.HasFirstRay);
        double? d = s.AddPoint(0, 1);    // 第二边 → 出角
        Assert.NotNull(d);
        Assert.Equal(90, d!.Value, 4);
        Assert.False(s.HasVertex);       // 已复位
    }
}
