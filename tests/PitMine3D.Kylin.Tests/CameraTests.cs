using System;
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

    [Fact]
    public void Mode_3d_orbits_2d_pans()
    {
        var cam = new Camera();

        // 3D：orbit 改 yaw，不动 target
        double yaw0 = cam.Yaw;
        cam.Orbit(0.2, 0.1);
        Assert.NotEqual(yaw0, cam.Yaw, 6);

        // 切 2D：orbit 平移 target，不动 yaw
        cam.SetMode(true);
        Assert.True(cam.Is2D);
        double yawNow = cam.Yaw;
        float tx0 = cam.Target[0];
        cam.Orbit(0.2, 0.1);
        Assert.Equal(yawNow, cam.Yaw, 6);            // yaw 未变
        Assert.NotEqual(tx0, cam.Target[0], 4);      // target 平移了
    }

    [Fact]
    public void ViewProj_differs_between_2d_and_3d()
    {
        var cam = new Camera();
        float[] vp3d = cam.ViewProj(1.5f);
        cam.SetMode(true);
        float[] vp2d = cam.ViewProj(1.5f);

        bool anyDiff = false;
        for (int i = 0; i < 16; i++)
            if (Math.Abs(vp3d[i] - vp2d[i]) > 1e-4f) { anyDiff = true; break; }
        Assert.True(anyDiff, "2D 与 3D 的 ViewProj 应不同");
    }

    [Fact]
    public void Mat4_invert_times_self_is_identity()
    {
        float[] m = Mat4.Mul(Mat4.Translate(3, -2, 5), Mat4.RotateZ(0.7f));
        float[]? inv = Mat4.Invert(m);
        Assert.NotNull(inv);
        float[] id = Mat4.Mul(m, inv!);
        for (int c = 0; c < 4; c++)
        for (int r = 0; r < 4; r++)
        {
            float expected = c == r ? 1f : 0f;
            Assert.True(Math.Abs(id[c * 4 + r] - expected) < 1e-4f, $"[{c},{r}]={id[c * 4 + r]}");
        }
    }

    [Fact]
    public void ScreenToWorld_roundtrip_on_z0_plane()
    {
        var cam = new Camera();
        cam.SetMode(true);                  // 2D 正交俯视，Z=0 平面
        cam.FitBounds(0, 0, 100, 100);
        double vw = 800, vh = 600;

        // 正向：世界点 (30,70,0) → 屏幕像素
        float[] vp = cam.ViewProj((float)(vw / vh));
        double wx = 30, wy = 70;
        double cx = vp[0] * wx + vp[4] * wy + vp[12];
        double cy = vp[1] * wx + vp[5] * wy + vp[13];
        double cw = vp[3] * wx + vp[7] * wy + vp[15];
        double ndcx = cx / cw, ndcy = cy / cw;
        double sx = (ndcx + 1) * 0.5 * vw;
        double sy = (1 - ndcy) * 0.5 * vh;

        // 反向：屏幕 → 世界，应得回 (30,70)
        var got = cam.ScreenToWorldOnZPlane(sx, sy, vw, vh);
        Assert.NotNull(got);
        Assert.Equal(30, got!.Value.x, 1);
        Assert.Equal(70, got!.Value.y, 1);
    }
}
