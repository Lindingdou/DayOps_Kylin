using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>采场/排土场自动识别（栅格极性分类，忠实原 LandformClassifier）回归。</summary>
public class LandformClassifierTests
{
    [Fact]
    public void Components_finds_separate_blobs_above_min()
    {
        // 5×5 网格: 左上 2×2 块 + 右下单点; minCells=2 → 只留 2×2 块
        int nx = 5, ny = 5; var m = new bool[nx * ny];
        m[0] = m[1] = m[nx] = m[nx + 1] = true;   // (0,0)(1,0)(0,1)(1,1)
        m[nx * 4 + 4] = true;                      // (4,4) 单点
        var comps = LandformClassifier.Components(m, nx, ny, 2);
        Assert.Single(comps);
        Assert.Equal(4, comps[0].Count);
    }

    [Fact]
    public void FillHoles_fills_interior_hole()
    {
        // 5×5 实心环(边全真, 中心(2,2)假) → 填洞后中心变真
        int nx = 5, ny = 5; var m = new bool[nx * ny];
        for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++) if (!(x == 2 && y == 2)) m[y * nx + x] = true;
        Assert.False(m[2 * nx + 2]);
        LandformClassifier.FillHoles(m, nx, ny);
        Assert.True(m[2 * nx + 2]);   // 洞已填
    }

    [Fact]
    public void Simplify_collinear_reduces_to_endpoints()
    {
        var pts = new List<(int, int)> { (0, 0), (1, 0), (2, 0), (3, 0), (4, 0) };
        var s = LandformClassifier.Simplify(pts, 0.5);
        Assert.Equal(2, s.Count);     // 共线 → 首末
    }

    [Fact]
    public void Empty_input_returns_not_ok_no_throw()
    {
        var r = LandformClassifier.Classify(new List<double[]>());
        Assert.False(r.Ok);
        Assert.Contains("未提供", r.Message);
    }

    [Fact]
    public void Bowl_terrain_detects_a_pit()
    {
        // 台阶线覆盖 [0,400]² 扫描线, Z = 碗(中心低 60、边缘 100) → 中心残差负 = 采场
        var lines = new List<double[]>();
        for (int row = 0; row <= 400; row += 20)
        {
            var seg = new List<double>();
            for (int x = 0; x <= 400; x += 20)
            {
                double dx = x - 200, dy = row - 200, dist = Math.Sqrt(dx * dx + dy * dy);
                double z = 100 - Math.Max(0, 200 - dist) * 0.2;   // 碗: 中心 60, 边 100
                seg.Add(x); seg.Add(row); seg.Add(z);
            }
            lines.Add(seg.ToArray());
        }
        var r = LandformClassifier.Classify(lines, cellSize: 10, minAreaHa: 1, requirePairs: false);
        Assert.True(r.Ok, r.Message);
        Assert.Contains(r.Regions, z => z.Category == "pit");   // 中心碗 = 采场
    }
}
