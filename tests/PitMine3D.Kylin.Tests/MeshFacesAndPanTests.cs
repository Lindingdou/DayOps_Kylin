using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Controls;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三角网着色面镶嵌 + 显示模式 + 三维视平面平移。</summary>
[Collection("MeshRenderMode")]
public class MeshFacesAndPanTests
{
    private static MeshEntity Quad()
    {
        var v = new List<(double x, double y, double z)> { (0, 0, 10), (1, 0, 10), (1, 1, 20), (0, 1, 20) };
        var t = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        return new MeshEntity("m", v, t) { Cr = 1f, Cg = 0.5f, Cb = 0.25f };
    }

    [Fact]
    public void Faces_ThreeVerticesPerTriangle_ShadedFromBaseColor()
    {
        var saved = (MeshEntity.RenderMode, MeshEntity.ColorByElevation);
        try
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded; MeshEntity.ColorByElevation = false;
            var m = Quad();
            var o = new List<float>(); m.TessellateFaces(o);
            Assert.Equal(2 * 3 * 6, o.Count);
            // 颜色 = 基色 × k, k∈[0.42,1]；分量比例保持
            for (int i = 0; i < o.Count; i += 6)
            {
                float k = o[i + 3];
                Assert.InRange(k, 0.42f, 1f);
                Assert.Equal(0.5f * k, o[i + 4], 4); Assert.Equal(0.25f * k, o[i + 5], 4);
            }
            // 纯着色模式不出边线, 但 TessellateEdges 仍出 5 条
            var e = new List<float>(); m.Tessellate(e); Assert.Empty(e);
            m.TessellateEdges(e); Assert.Equal(5 * 12, e.Count);
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Wireframe;
            o.Clear(); m.TessellateFaces(o); Assert.Empty(o);
            e.Clear(); m.Tessellate(e); Assert.Equal(5 * 12, e.Count);
        }
        finally { (MeshEntity.RenderMode, MeshEntity.ColorByElevation) = saved; }
    }

    [Fact]
    public void Faces_ElevationRamp_LowGreenHighWhite()
    {
        var saved = (MeshEntity.RenderMode, MeshEntity.ColorByElevation);
        try
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded; MeshEntity.ColorByElevation = true;
            var m = Quad();
            var o = new List<float>(); m.TessellateFaces(o);
            var lo = MeshEntity.TerrainRamp(0); var hi = MeshEntity.TerrainRamp(1);
            Assert.True(lo.g > lo.r && lo.g > lo.b);          // 低=绿
            Assert.True(hi.r > 0.9 && hi.g > 0.9 && hi.b > 0.9); // 高=白
            var scene = new Scene(); scene.Add(m);
            Assert.Equal(o.Count, scene.BuildFaces().Length);
        }
        finally { (MeshEntity.RenderMode, MeshEntity.ColorByElevation) = saved; }
    }

    [Fact]
    public void Pan3D_MovesTargetByPixelsInViewPlane_NoBlowup()
    {
        var cam = new Camera();
        cam.SetOrientation(0.9, 0.55);           // 3D 轨道
        cam.FitBounds(0, 0, 100, 100, 50);
        Assert.Equal(50f, cam.Target[2]);
        var t0 = (cam.Target[0], cam.Target[1], cam.Target[2]);
        double vw = 800, vh = 600;
        cam.PanScreen(400, 300, 480, 300, vw, vh);   // 向右拖 80px
        double unitsPerPx = 2.0 * cam.Dist * Math.Tan(Math.PI / 8) / vh;
        double moved = Math.Sqrt(Math.Pow(cam.Target[0] - t0.Item1, 2) + Math.Pow(cam.Target[1] - t0.Item2, 2) + Math.Pow(cam.Target[2] - t0.Item3, 2));
        Assert.Equal(80 * unitsPerPx, moved, 3);     // 位移 = 像素 × 注视距离处每像素世界量, 与 Z=0 平面无关
        // 近地平线视角也不会爆炸(旧实现对 Z=0 平面反投影会甩飞)
        cam.SetOrientation(0.9, 0.02);
        var t1 = (cam.Target[0], cam.Target[1], cam.Target[2]);
        cam.PanScreen(400, 300, 400, 320, vw, vh);
        double moved2 = Math.Sqrt(Math.Pow(cam.Target[0] - t1.Item1, 2) + Math.Pow(cam.Target[1] - t1.Item2, 2) + Math.Pow(cam.Target[2] - t1.Item3, 2));
        Assert.Equal(20 * unitsPerPx, moved2, 3);
        // 2D 仍按 Z=0 平面反投影(光标抓点跟随)
        cam.SetMode(true);
        var w0 = cam.ScreenToWorldOnZPlane(400, 300, vw, vh)!.Value;
        cam.PanScreen(400, 300, 500, 300, vw, vh);
        var w1 = cam.ScreenToWorldOnZPlane(500, 300, vw, vh)!.Value;
        Assert.Equal(w0.x, w1.x, 3); Assert.Equal(w0.y, w1.y, 3);
    }
}
