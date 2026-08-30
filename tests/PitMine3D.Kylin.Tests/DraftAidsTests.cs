using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>制图辅助（正交/栅格捕捉）回归。</summary>
public class DraftAidsTests
{
    [Fact]
    public void Ortho_locks_to_horizontal_when_dx_larger()
    {
        var (x, y) = DraftAids.Ortho(0, 0, 10, 3);
        Assert.Equal(10, x, 6); Assert.Equal(0, y, 6);   // |dx|>|dy| → 水平, y 对齐基点
    }

    [Fact]
    public void Ortho_locks_to_vertical_when_dy_larger()
    {
        var (x, y) = DraftAids.Ortho(0, 0, 3, 10);
        Assert.Equal(0, x, 6); Assert.Equal(10, y, 6);   // |dy|>|dx| → 垂直, x 对齐基点
    }

    [Fact]
    public void Ortho_from_nonzero_base()
    {
        var (x, y) = DraftAids.Ortho(5, 5, 5.2, 12);     // dy 大 → 垂直
        Assert.Equal(5, x, 6); Assert.Equal(12, y, 6);
    }

    [Fact]
    public void Snap_rounds_to_grid()
    {
        Assert.Equal((0.0, 1.0), DraftAids.Snap(0.4, 0.6, 1));
        Assert.Equal((3.0, 3.0), DraftAids.Snap(2.6, 2.6, 1));
        Assert.Equal((10.0, 20.0), DraftAids.Snap(12, 18, 10));
    }

    [Fact]
    public void Snap_zero_step_is_identity()
    {
        Assert.Equal((1.23, 4.56), DraftAids.Snap(1.23, 4.56, 0));
    }
}
