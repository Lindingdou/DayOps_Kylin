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
        cam.SetMode(false);   // 相机默认 2D(俯视), 本例先验 3D 轨道行为, 显式切 3D

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
        cam.SetMode(false);   // 相机默认 2D, 显式切 3D 以对比两模式
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

    [Fact]
    public void Recolor_keeps_positions_replaces_color()
    {
        float[] src = { 1, 2, 3, 0.5f, 0.5f, 0.5f, 4, 5, 6, 0.1f, 0.2f, 0.3f };
        float[] dst = CadGlViewport.Recolor(src, 1f, 0.9f, 0.2f);

        Assert.Equal(1f, dst[0]); Assert.Equal(3f, dst[2]);          // 位置不变
        Assert.Equal(4f, dst[6]); Assert.Equal(6f, dst[8]);
        Assert.Equal(1f, dst[3]); Assert.Equal(0.9f, dst[4]); Assert.Equal(0.2f, dst[5]);   // 颜色替换
        Assert.Equal(0.2f, dst[11]);
        Assert.Equal(0.5f, src[3]);                                  // 原数组不被改（克隆）
    }

    [Fact]
    public void ZoomAtScreen_keeps_cursor_point_fixed()
    {
        var cam = new Camera();
        cam.SetMode(true);
        cam.FitBounds(0, 0, 100, 100);
        double vw = 800, vh = 600, sx = 220, sy = 160;
        var before = cam.ScreenToWorldOnZPlane(sx, sy, vw, vh);
        cam.ZoomAtScreen(sx, sy, vw, vh, 0.5);
        var after = cam.ScreenToWorldOnZPlane(sx, sy, vw, vh);
        Assert.NotNull(before); Assert.NotNull(after);
        Assert.Equal(before!.Value.x, after!.Value.x, 1);   // 光标下的世界点缩放后不动
        Assert.Equal(before!.Value.y, after!.Value.y, 1);
    }

    /// <summary>
    /// 空图纸画第一个图元时才落定渲染局部原点(局部 = 世界 − 原点)。
    /// CadGlViewport.ApplyOrigin 会把注视点同量反向平移, 视图才不会当场挪一下
    /// ——"刚画的圆/矩形不在落笔的位置"就是漏了这一步。
    /// </summary>
    [Fact]
    public void ShiftTarget_compensates_render_origin_change()
    {
        double vw = 800, vh = 600, ox = 3, oy = 2;   // 第一个圆的圆心 → 新原点

        var cam = new Camera();
        cam.SetMode(true);
        var before = cam.WorldToScreen(ox, oy, 0, vw, vh);       // 原点未定: 世界 == 局部

        cam.ShiftTarget(-ox, -oy);                               // ApplyOrigin 的补偿
        var after = cam.WorldToScreen(0, 0, 0, vw, vh);          // 同一世界点的新局部坐标

        Assert.NotNull(before); Assert.NotNull(after);
        Assert.Equal(before!.Value.sx, after!.Value.sx, 6);      // 屏幕位置纹丝不动
        Assert.Equal(before!.Value.sy, after!.Value.sy, 6);

        // 反证: 不补偿的话同一世界点在屏幕上真的挪了(且挪的量不小), 免得上面成了恒真断言
        var uncompensated = new Camera();
        uncompensated.SetMode(true);
        var moved = uncompensated.WorldToScreen(0, 0, 0, vw, vh);
        Assert.NotNull(moved);
        Assert.True(Math.Abs(moved!.Value.sx - before!.Value.sx) > 10, "未补偿时屏幕位置应明显偏移");
    }

    [Fact]
    public void PanScreen_moves_target()
    {
        var cam = new Camera();
        cam.SetMode(true);
        cam.FitBounds(0, 0, 100, 100);
        float tx0 = cam.Target[0];
        cam.PanScreen(400, 300, 500, 300, 800, 600);   // 向右拖 100px
        Assert.NotEqual(tx0, cam.Target[0]);
    }

    // ── 十字光标尺寸(选项·显示) ──────────────────────────────────────────────
    [Fact]
    public void CursorArm_full_size_spans_view_from_any_corner()
    {
        // 100% = 满屏十字: 臂长(NDC)≥2 才保证光标贴到任一角时, 横竖两线仍拉满整个视图
        var (ax, ay) = CadGlViewport.CursorArmNdc(100, 1600, 900);
        Assert.True(ax >= 2f); Assert.True(ay >= 2f);
    }

    [Fact]
    public void CursorArm_scales_with_percent()
    {
        var (ax, ay) = CadGlViewport.CursorArmNdc(50, 1000, 1000);   // 方视口 50% → 臂长半屏(NDC 1.0)
        Assert.Equal(1.0, ax, 3); Assert.Equal(1.0, ay, 3);
    }

    [Fact]
    public void CursorArm_is_square_in_pixels_on_wide_viewport()
    {
        // 宽视口上横竖两臂的【像素】长度要一样, 否则小尺寸时十字被宽高比拉扁
        double w = 1600, h = 800;
        var (ax, ay) = CadGlViewport.CursorArmNdc(10, w, h);
        Assert.Equal(ax * w / 2, ay * h / 2, 3);
    }

    [Fact]
    public void CursorArm_clamps_out_of_range_percent()
    {
        Assert.Equal(CadGlViewport.CursorArmNdc(2, 1000, 1000).ax, CadGlViewport.CursorArmNdc(-5, 1000, 1000).ax, 6);
        Assert.Equal(CadGlViewport.CursorArmNdc(100, 1000, 1000).ax, CadGlViewport.CursorArmNdc(999, 1000, 1000).ax, 6);
    }
}
