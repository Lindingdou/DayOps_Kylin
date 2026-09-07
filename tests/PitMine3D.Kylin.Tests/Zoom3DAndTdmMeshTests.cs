using System;
using System.IO;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Controls;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三维滚轮缩放锚定光标(视平面口径) + .3dm 网格级导入(面模型用)。</summary>
public class Zoom3DAndTdmMeshTests
{
    [Fact]
    public void Zoom3D_KeepsCursorPointAtTargetDepth_NoTargetJump()
    {
        var cam = new Camera();
        cam.SetOrientation(0.9, 0.55);
        cam.FitBounds(0, 0, 100, 100, 50);
        double vw = 800, vh = 600;
        // 光标在屏幕中心：缩放不动注视点
        var t0 = (cam.Target[0], cam.Target[1], cam.Target[2]);
        cam.ZoomAtScreen(400, 300, vw, vh, 0.9);
        Assert.Equal(t0.Item1, cam.Target[0], 5); Assert.Equal(t0.Item2, cam.Target[1], 5); Assert.Equal(t0.Item3, cam.Target[2], 5);
        // 光标偏右 100px：注视点朝光标方向移动 100px×每像素量×(1-0.9)，量级远小于模型跨度
        double d0 = cam.Dist;
        double upp = 2.0 * d0 * Math.Tan(Math.PI / 8) / vh;
        cam.ZoomAtScreen(500, 300, vw, vh, 0.9);
        double moved = Math.Sqrt(Math.Pow(cam.Target[0] - t0.Item1, 2) + Math.Pow(cam.Target[1] - t0.Item2, 2) + Math.Pow(cam.Target[2] - t0.Item3, 2));
        Assert.Equal(100 * upp * 0.1, moved, 4);
        Assert.Equal(d0 * 0.9, cam.Dist, 6);
        // 近地平线连续放大 20 次也不会把注视点甩远(旧实现对 Z=0 平面反投影会发散)
        cam.SetOrientation(0.9, 0.02);
        var t1 = (cam.Target[0], cam.Target[1], cam.Target[2]);
        for (int i = 0; i < 20; i++) cam.ZoomAtScreen(600, 500, vw, vh, 0.9);
        double drift = Math.Sqrt(Math.Pow(cam.Target[0] - t1.Item1, 2) + Math.Pow(cam.Target[1] - t1.Item2, 2) + Math.Pow(cam.Target[2] - t1.Item3, 2));
        Assert.True(drift < 100, $"注视点漂移 {drift:0.#} 应小于模型跨度");
    }

    private static string? Fixture()
    {
        foreach (var p in new[] { @"C:\Users\cFore\Desktop\2026年6月测试文件\平朔数据\4-2底面.3dm" })
            if (File.Exists(p)) return p;
        return null;
    }

    [Fact]
    public void Tdm_LoadMeshes_MatchesLineWireframeTriangleCount()
    {
        var fx = Fixture(); if (fx == null) return;   // 无样本机器跳过
        var r = TdmImportService.LoadMeshes(fx);
        Assert.True(r.Success, r.Error);
        Assert.NotEmpty(r.Meshes);
        int tris = r.Meshes.Sum(m => m.Indices.Length / 3);
        var old = TdmImportService.Load(fx);
        Assert.Equal(old.EntityCount, tris);          // 与旧线框通道的三角数一致
        var m0 = r.Meshes[0];
        Assert.True(m0.Vx.Length > 100 && m0.Vx.Length == m0.Vy.Length && m0.Vy.Length == m0.Vz.Length);
        Assert.All(m0.Indices, i => Assert.InRange(i, 0, m0.Vx.Length - 1));
        Assert.False(string.IsNullOrWhiteSpace(m0.Name));
    }
}
