using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>矿床识别纯核回归（移植自 BlockModelLib.DepositAutoDetector）。</summary>
public class DepositAutoDetectorTests
{
    [Fact]
    public void Horizontal_layer_dip_near_zero()
    {
        var cells = new List<(double X, double Y, double Z)>();
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                cells.Add((i, j, 0));                  // 全在 z=0 水平面
        var s = DepositAutoDetector.Detect(cells, 1.0);
        Assert.NotNull(s);
        Assert.Equal(0, s!.Value.DipDeg, 1);           // 水平 → 倾角 0
    }

    [Fact]
    public void Dipping_plane_45deg()
    {
        var cells = new List<(double X, double Y, double Z)>();
        for (int i = 0; i < 5; i++)
            for (int j = 0; j < 5; j++)
                cells.Add((i, j, i));                  // z=x → 45° 倾斜面
        var s = DepositAutoDetector.Detect(cells, 1.0);
        Assert.NotNull(s);
        Assert.Equal(45, s!.Value.DipDeg, 1);
        Assert.Equal(90, s!.Value.StrikeAzimuthDeg, 1);   // 走向沿 y 轴 → 90°
    }

    [Fact]
    public void Two_z_bands_give_two_seams()
    {
        var cells = new List<(double X, double Y, double Z)>();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            { cells.Add((i, j, 0)); cells.Add((i, j, 50)); }   // z=0 与 z=50 两带
        var s = DepositAutoDetector.Detect(cells, 10.0);
        Assert.NotNull(s);
        Assert.Equal(2, s!.Value.SeamCount);
    }

    [Fact]
    public void Too_few_cells_returns_null()
    {
        var cells = new List<(double X, double Y, double Z)>();
        for (int i = 0; i < 7; i++) cells.Add((i, 0, 0));
        Assert.Null(DepositAutoDetector.Detect(cells, 1.0));
    }

    [Fact]
    public void JacobiEigen3_recovers_eigenvalues()
    {
        // [[2,1,0],[1,2,0],[0,0,5]] → 特征值 {1,3,5}
        var a = new double[3, 3] { { 2, 1, 0 }, { 1, 2, 0 }, { 0, 0, 5 } };
        DepositAutoDetector.JacobiEigen3(a, out var eval, out _);
        var sorted = eval.OrderBy(v => v).ToArray();
        Assert.Equal(1, sorted[0], 6);
        Assert.Equal(3, sorted[1], 6);
        Assert.Equal(5, sorted[2], 6);
    }
}
