using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Controls;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>3D 屏幕空间点选：点在三角网面内命中(取最前)、贴近线命中优先、视平面落点与投影互逆。</summary>
[Collection("MeshRenderMode")]
public class PickScreen3DTests
{
    private static Camera Cam()
    {
        var cam = new Camera();
        cam.SetOrientation(0.7, 0.6);
        cam.FitBounds(0, 0, 100, 100, 1000);
        return cam;
    }

    [Fact]
    public void ClickInsideFace_PicksMesh_FrontMostWins_ShadedMode()
    {
        var old = MeshEntity.RenderMode;
        try
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            var cam = Cam(); double vw = 800, vh = 600;
            var proj = cam.MakeProjectorDepth(vw, vh);
            MeshEntity Quad(string n, double z) => new(n, new List<(double x, double y, double z)> { (0, 0, z), (100, 0, z), (100, 100, z), (0, 100, z) }, new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) });
            var low = Quad("低", 1000); var high = Quad("高", 1030);
            var s = proj(80, 80, 1030)!.Value;   // 高网上一点(透视下其视线继续落到低网 z=1000 处仍在低网内)
            var hit = SelectionBox.PickScreen(new SceneEntity[] { low, high }, s.sx, s.sy, 12, proj);
            Assert.Same(high, hit);                // 两网重叠, 取深度最前(上方)者
            // 点到网外
            var far = proj(-150, -150, 1000)!.Value;   // 网外且在相机前方(相机在 +x+y 侧)
            Assert.Null(SelectionBox.PickScreen(new SceneEntity[] { low, high }, far.sx, far.sy, 12, proj));
            // 面上有一条抬高的线：贴线点击优先选线
            var ln = new LineEntity { X0 = 40, Y0 = 50, X1 = 60, Y1 = 50, Elevation = 1031 };
            var sl = proj(50, 50, 1031)!.Value;
            Assert.Same(ln, SelectionBox.PickScreen(new SceneEntity[] { low, high, ln }, sl.sx + 2, sl.sy + 2, 12, proj));
            // 隐藏/不可选层不命中
            high.Visible = false;
            Assert.Same(low, SelectionBox.PickScreen(new SceneEntity[] { low, high }, s.sx, s.sy, 12, proj));
        }
        finally { MeshEntity.RenderMode = old; }
    }

    [Fact]
    public void ScreenToViewPlane_RoundTripsThroughProjection()
    {
        var cam = Cam(); double vw = 800, vh = 600;
        foreach (var (sx, sy) in new[] { (400.0, 300.0), (100.0, 80.0), (760.0, 560.0) })
        {
            var p = cam.ScreenToViewPlane(sx, sy, vw, vh);
            Assert.NotNull(p);
            var s = cam.WorldToScreen(p!.Value.x, p.Value.y, p.Value.z, vw, vh);
            Assert.NotNull(s);
            Assert.True(Math.Abs(s!.Value.sx - sx) < 0.5 && Math.Abs(s.Value.sy - sy) < 0.5, $"({sx},{sy}) → ({s.Value.sx:0.##},{s.Value.sy:0.##})");
        }
        // 视平面中心 = 注视点
        var c = cam.ScreenToViewPlane(vw / 2, vh / 2, vw, vh)!.Value;
        Assert.Equal(cam.Target[0], c.x, 3); Assert.Equal(cam.Target[1], c.y, 3); Assert.Equal(cam.Target[2], c.z, 3);
    }
}
