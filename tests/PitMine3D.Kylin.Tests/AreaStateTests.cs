using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>连续点取面积 jig 状态机回归。</summary>
public class AreaStateTests
{
    [Fact]
    public void Finish_returns_closed_area_and_perimeter_then_resets()
    {
        var state = new AreaState();
        state.AddPoint(0, 0);
        state.AddPoint(4, 0);
        state.AddPoint(4, 3);
        state.AddPoint(0, 3);

        var result = state.Finish();

        Assert.NotNull(result);
        Assert.Equal(12, result!.Value.Area, 6);
        Assert.Equal(14, result.Value.Perimeter, 6);
        Assert.Equal(4, result.Value.Points.Count);
        Assert.False(state.HasPoints);
    }

    [Fact]
    public void Finish_with_fewer_than_three_points_does_not_complete_or_clear()
    {
        var state = new AreaState();
        state.AddPoint(0, 0);
        state.AddPoint(2, 0);

        Assert.Null(state.Finish());
        Assert.True(state.HasPoints);
        Assert.Equal(2, state.Points.Count);
    }
}
