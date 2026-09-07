using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Controls;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>3D 视图框选：世界→屏幕投影与反投影互逆；屏幕空间窗口/交叉判定选中三角网与抬高的实体。</summary>
[Collection("MeshRenderMode")]
public class BoxSelect3DTests
{
    [Fact]
    public void WorldToScreen_InvertsScreenToWorld_On3DAndOrtho()
    {
        var cam = new Camera();
        cam.SetOrientation(0.7, 0.6);
        cam.FitBounds(0, 0, 100, 100, 0);
        double vw = 800, vh = 600;
        foreach (var (sx, sy) in new[] { (400.0, 300.0), (120.0, 500.0), (700.0, 100.0) })
        {
            var w = cam.ScreenToWorldOnZPlane(sx, sy, vw, vh);
            Assert.NotNull(w);
            var s = cam.WorldToScreen(w!.Value.x, w.Value.y, 0, vw, vh);
            Assert.NotNull(s);
            Assert.True(Math.Abs(sx - s!.Value.sx) < 0.5 && Math.Abs(sy - s.Value.sy) < 0.5, $"往返误差 ({sx},{sy}) → ({s.Value.sx:0.###},{s.Value.sy:0.###})");   // float 矩阵精度
        }
        var e = cam.Eye();
        // 相机正后方的点不可投影
        Assert.Null(cam.WorldToScreen(e[0] * 2 - cam.Target[0], e[1] * 2 - cam.Target[1], e[2] * 2 - cam.Target[2], vw, vh));
    }

    [Fact]
    public void MatchScreen_SelectsMeshAndRaisedPolyline_InPerspective()
    {
        var old = MeshEntity.RenderMode;
        try
        {
            MeshEntity.RenderMode = MeshEntity.DisplayMode.Shaded;
            var cam = new Camera();
            cam.SetOrientation(0.7, 0.6);
            cam.FitBounds(0, 0, 100, 100, 50);
            double vw = 800, vh = 600;
            var proj = cam.MakeProjector(vw, vh);
            var mesh = new MeshEntity("网", new List<(double x, double y, double z)> { (40, 40, 50), (60, 40, 52), (60, 60, 55), (40, 60, 51) },
                new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) });
            var pl = new PolylineEntity { Elevation = 50 }; pl.Points.Add((45, 45)); pl.Points.Add((55, 55));
            // 用投影后的顶点范围围一个框：窗口选两者都全含
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var v in mesh.Verts) { var s = proj(v.x, v.y, v.z)!.Value; minX = Math.Min(minX, s.sx); maxX = Math.Max(maxX, s.sx); minY = Math.Min(minY, s.sy); maxY = Math.Max(maxY, s.sy); }
            Assert.True(SelectionBox.MatchScreen(mesh, minX - 2, minY - 2, maxX + 2, maxY + 2, false, proj));
            Assert.True(SelectionBox.MatchScreen(pl, minX - 2, minY - 2, maxX + 2, maxY + 2, false, proj));
            // 半个框：窗口选不中，交叉选选中
            Assert.False(SelectionBox.MatchScreen(mesh, minX - 2, minY - 2, (minX + maxX) / 2, maxY + 2, false, proj));
            Assert.True(SelectionBox.MatchScreen(mesh, minX - 2, minY - 2, (minX + maxX) / 2, maxY + 2, true, proj));
            // 远离的框
            Assert.False(SelectionBox.MatchScreen(mesh, 0, 0, 5, 5, true, proj));
        }
        finally { MeshEntity.RenderMode = old; }
    }
}
