using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>三角网 Laplacian 光顺回归（MeshSmooth：邻点质心趋近, 固定边界）。</summary>
public class MeshSmoothTests
{
    // 3×3 网格(9 顶点), 中心点(idx4)是唯一内点。z 全 0 但中心点抬高成"尖峰"。
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t, int center) Grid3x3(double centerZ)
    {
        var v = new List<(double x, double y, double z)>();
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
                v.Add((i * 10.0, j * 10.0, (i == 1 && j == 1) ? centerZ : 0));
        int Idx(int i, int j) => j * 3 + i;
        var t = new List<(int a, int b, int c)>();
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            { t.Add((Idx(i, j), Idx(i + 1, j), Idx(i + 1, j + 1))); t.Add((Idx(i, j), Idx(i + 1, j + 1), Idx(i, j + 1))); }
        return (v, t, Idx(1, 1));
    }

    [Fact]
    public void Bumped_center_smooths_toward_plane()
    {
        var (v, t, center) = Grid3x3(30);           // 中心抬高 30
        var sm = MeshSmooth.Laplacian(v, t, iterations: 1, lambda: 0.5);
        Assert.True(sm[center].z < 30 && sm[center].z >= 0, $"尖峰应被压低, 实 {sm[center].z}");
        // 邻点(边界)均 z=0, 质心 z=0, λ=0.5 → 30→15
        Assert.Equal(15.0, sm[center].z, 6);
    }

    [Fact]
    public void Flat_mesh_stays_flat()
    {
        var (v, t, _) = Grid3x3(0);                  // 全平
        var sm = MeshSmooth.Laplacian(v, t, iterations: 5, lambda: 0.5);
        Assert.All(sm, p => Assert.Equal(0.0, p.z, 9));   // 平面不变
    }

    [Fact]
    public void Boundary_vertices_fixed()
    {
        var (v, t, center) = Grid3x3(30);
        var sm = MeshSmooth.Laplacian(v, t, iterations: 3, lambda: 0.5, fixBoundary: true);
        // 边界点(除中心的 8 个)位置不动
        for (int i = 0; i < v.Count; i++)
            if (i != center) { Assert.Equal(v[i].x, sm[i].x, 9); Assert.Equal(v[i].y, sm[i].y, 9); Assert.Equal(v[i].z, sm[i].z, 9); }
    }
}
