using System;
using PitMine3D.Kylin.Controls;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// <see cref="Camera.WorldPerPixelAt"/> 回归：夹点方块按"该点所在深度"折算像素→世界长度。
/// 起因: 旧法拿屏幕中心在 Z=0 平面的反投影当比例, 图元有标高 + 视角一斜, 方块比该有的大好几倍(节点连成粗蓝带)。
/// </summary>
public class CameraWorldPerPixelTests
{
    const double W = 1600, H = 900;

    [Fact]
    public void In2D_matches_screen_to_world_difference()
    {
        var cam = new Camera();
        cam.SetMode(true);
        cam.FitBounds(new double[] { 0, 0, 100, 40 });
        var a = cam.ScreenToWorldOnZPlane(W / 2, H / 2, W, H)!.Value;
        var b = cam.ScreenToWorldOnZPlane(W / 2 + 1, H / 2, W, H)!.Value;
        double expected = Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));
        // 2D 下与旧换算完全一致 —— 平面视图里夹点尺寸不变
        Assert.Equal(expected, cam.WorldPerPixelAt(30, 10, 0, H), 6);   // 矩阵链是 float32, 只比到 1e-6
        Assert.Equal(expected, cam.WorldPerPixelAt(-500, 900, 1200, H), 6);   // 2D 正交: 与位置/高程无关
    }

    [Fact]
    public void In3D_one_unit_projects_to_one_pixel_at_that_depth()
    {
        var cam = new Camera();
        cam.SetMode(false);
        cam.FitBounds(0, 0, 400, 300, zCenter: 1200);   // 模型抬到 1200 m 高程(等高线/台阶线常态)
        cam.Orbit(0.3, -0.2);
        var proj = cam.MakeProjector(W, H);
        var (rx, ry, rz, _, _, _) = cam.ViewAxes();
        foreach (var p in new[] { (200.0, 150.0, 1200.0), (0.0, 0.0, 1180.0), (400.0, 300.0, 1250.0) })
        {
            double upp = cam.WorldPerPixelAt(p.Item1, p.Item2, p.Item3, H);
            var s0 = proj(p.Item1, p.Item2, p.Item3)!.Value;
            var s1 = proj(p.Item1 + upp * rx, p.Item2 + upp * ry, p.Item3 + upp * rz)!.Value;   // 沿相机右向量挪 1 像素的世界长度
            double px = Math.Sqrt((s1.sx - s0.sx) * (s1.sx - s0.sx) + (s1.sy - s0.sy) * (s1.sy - s0.sy));
            Assert.InRange(px, 0.98, 1.02);
        }
    }

    [Fact]
    public void In3D_near_points_get_smaller_world_size_than_far()
    {
        var cam = new Camera();
        cam.SetMode(false);
        cam.FitBounds(0, 0, 400, 300, zCenter: 1200);
        float[] eye = cam.Eye();
        double dx = cam.Target[0] - eye[0], dy = cam.Target[1] - eye[1], dz = cam.Target[2] - eye[2];
        double near = cam.WorldPerPixelAt(eye[0] + dx * 0.5, eye[1] + dy * 0.5, eye[2] + dz * 0.5, H);   // 视线中途
        double at = cam.WorldPerPixelAt(cam.Target[0], cam.Target[1], cam.Target[2], H);
        double far = cam.WorldPerPixelAt(eye[0] + dx * 2.0, eye[1] + dy * 2.0, eye[2] + dz * 2.0, H);      // 注视点之后
        Assert.Equal(at * 0.5, near, 6);   // Eye() 是 float32, 比到 1e-6
        Assert.Equal(at * 2.0, far, 6);
        Assert.Equal(2.0 * cam.Dist * Math.Tan(Math.PI / 8) / H, at, 6);   // 注视点深度 = Dist
    }

    [Fact]
    public void Old_z0_plane_estimate_overshoots_for_elevated_model()
    {
        // 复现用户看到的"节点太大": 模型在 1200 m 高程, 相机斜视 —— 屏幕中心射线与 Z=0 的交点跑到远处,
        // 按那儿折算出的每像素世界长度比模型所在深度的真值大好几倍。
        var cam = new Camera();
        cam.SetMode(false);
        cam.FitBounds(0, 0, 400, 300, zCenter: 1200);
        var a = cam.ScreenToWorldOnZPlane(W / 2, H / 2, W, H)!.Value;
        var b = cam.ScreenToWorldOnZPlane(W / 2 + 1, H / 2, W, H)!.Value;
        double oldUpp = Math.Sqrt((b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y));
        double newUpp = cam.WorldPerPixelAt(cam.Target[0], cam.Target[1], cam.Target[2], H);
        Assert.True(oldUpp > newUpp * 2, $"old={oldUpp} new={newUpp}");
    }
}
