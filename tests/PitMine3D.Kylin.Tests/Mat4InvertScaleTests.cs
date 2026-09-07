using System;
using PitMine3D.Kylin.Controls;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 大场景 2D 正交投影的矩阵求逆：行列式量级 ~1e-14 但完全可逆，旧的固定阈值 1e-12 会误判奇异，
/// 导致 ScreenToWorld 返回 null → 2D 下点选/框选/捕捉/坐标读数全部失效（.3dm 等大坐标模型必中）。
/// </summary>
public class Mat4InvertScaleTests
{
    [Fact]
    public void Invert_LargeOrthoScene_NotTreatedAsSingular()
    {
        var cam = new Camera();
        cam.SetMode(true);
        cam.FitBounds(615500, 4374400, 622500, 4381600);   // 真实 4-2底面.3dm 的 XY 范围(跨度 7000)
        double vw = 1783, vh = 993;
        var m = cam.ViewProj((float)(vw / vh));
        Assert.NotNull(Mat4.Invert(m));
        // 屏幕 ↔ 世界往返可用
        foreach (var (sx, sy) in new[] { (100.0, 80.0), (890.0, 500.0), (1700.0, 950.0) })
        {
            var w = cam.ScreenToWorldOnZPlane(sx, sy, vw, vh);
            Assert.True(w != null, $"2D ScreenToWorld 在大场景应可用（({sx},{sy}) 返回 null）");
            var s = cam.WorldToScreen(w!.Value.x, w.Value.y, 0, vw, vh);
            Assert.NotNull(s);
            Assert.True(Math.Abs(s!.Value.sx - sx) < 1.0 && Math.Abs(s.Value.sy - sy) < 1.0, $"往返 ({sx},{sy}) → ({s.Value.sx:0.#},{s.Value.sy:0.#})");
        }
    }

    [Fact]
    public void Invert_TrulySingular_StillReturnsNull()
    {
        var zero = new float[16];
        Assert.Null(Mat4.Invert(zero));
        var dup = Mat4.Identity(); dup[5] = 0;   // 一行全零 → 奇异
        Assert.Null(Mat4.Invert(dup));
        var proj = Mat4.Identity(); proj[10] = 0; proj[11] = 0; proj[14] = 0;   // 退化投影
        Assert.Null(Mat4.Invert(proj));
        Assert.NotNull(Mat4.Invert(Mat4.Identity()));
    }
}
