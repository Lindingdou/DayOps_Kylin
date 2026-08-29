using PitMine3D.Kylin.Controls;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 相机范围缩放（ZOOMEXTENTS）回归：对准包围盒中心并纳入其范围。
/// Camera 是 internal，靠 InternalsVisibleTo 可见。
/// </summary>
public class CameraTests
{
    [Fact]
    public void FitBounds_centers_target_and_fits_distance()
    {
        var cam = new Camera();
        cam.FitBounds(new double[] { 0, 0, 100, 40 });   // span = 100

        Assert.Equal(50.0, cam.Target[0], 3);   // 中心 x
        Assert.Equal(20.0, cam.Target[1], 3);   // 中心 y
        Assert.Equal(0.0, cam.Target[2], 3);
        Assert.Equal(140.0, cam.Dist, 3);       // span × 1.4
    }

    [Fact]
    public void FitBounds_ignores_null_or_short()
    {
        var cam = new Camera();
        double before = cam.Dist;
        cam.FitBounds(null);
        cam.FitBounds(new double[] { 1, 2 });
        Assert.Equal(before, cam.Dist, 6);       // 未变
    }
}
