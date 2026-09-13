using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Views;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>命令行精确坐标解析（绝对/相对/极坐标）。</summary>
public class CoordInputTests
{
    [Fact]
    public void Absolute_xy()
    {
        var p = MainWindow.ParseCoord("10,20", null);
        Assert.NotNull(p);
        Assert.Equal(10, p!.Value.x, 6);
        Assert.Equal(20, p!.Value.y, 6);
    }

    [Fact]
    public void Relative_xy_adds_to_last()
    {
        var p = MainWindow.ParseCoord("@5,3", (10, 20));
        Assert.Equal(15, p!.Value.x, 6);
        Assert.Equal(23, p!.Value.y, 6);
    }

    [Fact]
    public void Relative_without_last_is_null()
    {
        Assert.Null(MainWindow.ParseCoord("@5,3", null));
    }

    [Fact]
    public void Polar_absolute_from_origin()
    {
        var p = MainWindow.ParseCoord("10<90", null);   // 90° → +Y
        Assert.Equal(0, p!.Value.x, 4);
        Assert.Equal(10, p!.Value.y, 4);
    }

    [Fact]
    public void Polar_relative_from_last()
    {
        var p = MainWindow.ParseCoord("@10<0", (5, 5));  // 0° → +X
        Assert.Equal(15, p!.Value.x, 4);
        Assert.Equal(5, p!.Value.y, 4);
    }

    [Fact]
    public void Negative_and_decimal()
    {
        var p = MainWindow.ParseCoord("-2.5,3.5", null);
        Assert.Equal(-2.5, p!.Value.x, 6);
        Assert.Equal(3.5, p!.Value.y, 6);
    }

    [Theory]
    [InlineData("LINE")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("10")]
    public void Non_coordinate_is_null(string s)
    {
        Assert.Null(MainWindow.ParseCoord(s, (0, 0)));
    }
}

/// <summary>移动/复制的命令行位移输入：位移(D) 关键字、位移向量按原点解析、直接距离输入。</summary>
public class MoveDisplacementInputTests
{
    [Theory]
    [InlineData("D")]
    [InlineData("d")]
    [InlineData(" d ")]
    [InlineData("位移")]
    [InlineData("displacement")]
    [InlineData("Displacement")]
    public void Displacement_keyword_matches(string s) => Assert.True(MainWindow.IsDisplacementKeyword(s));

    [Theory]
    [InlineData("DD")]
    [InlineData("")]
    [InlineData("10,20")]
    [InlineData("DIST")]
    public void Displacement_keyword_rejects(string s) => Assert.False(MainWindow.IsDisplacementKeyword(s));

    [Fact]
    public void Displacement_vector_parses_relative_and_absolute_alike()
    {
        // 位移模式下坐标相对原点解析：@dx,dy 与 dx,dy 同义
        var a = MainWindow.ParseCoord("100,50", (0, 0));
        var b = MainWindow.ParseCoord("@100,50", (0, 0));
        Assert.Equal(a!.Value.x, b!.Value.x, 9);
        Assert.Equal(a!.Value.y, b!.Value.y, 9);
        Assert.Equal(100, a!.Value.x, 9);
        Assert.Equal(50, a!.Value.y, 9);
    }

    [Fact]
    public void Direct_distance_along_cursor_direction()
    {
        // 基点(0,0) 光标(3,4) 距离 10 → (6,8)
        var t = MainWindow.DirectDistanceTarget((0, 0), (3, 4), 10);
        Assert.NotNull(t);
        Assert.Equal(6, t!.Value.x, 9);
        Assert.Equal(8, t!.Value.y, 9);
    }

    [Fact]
    public void Direct_distance_negative_goes_opposite()
    {
        var t = MainWindow.DirectDistanceTarget((10, 10), (20, 10), -5);
        Assert.Equal(5, t!.Value.x, 9);
        Assert.Equal(10, t!.Value.y, 9);
    }

    [Fact]
    public void Direct_distance_without_direction_is_null()
    {
        Assert.Null(MainWindow.DirectDistanceTarget((0, 0), null, 10));
        Assert.Null(MainWindow.DirectDistanceTarget((5, 5), (5, 5), 10));   // 光标压在基点上：方向不明
    }
}

/// <summary>移动/复制的三分量输入：x,y,z / @dx,dy,dz 解析，捕捉候选带顶点 z，捕捉索引把 z 一起给。</summary>
public class MoveZInputTests
{
    [Fact]
    public void Absolute_xyz_and_xy()
    {
        var p = MainWindow.ParseCoord3("10,20,30", null, 0);
        Assert.Equal((10.0, 20.0), (p!.Value.x, p.Value.y));
        Assert.Equal(30.0, p.Value.z);
        var q = MainWindow.ParseCoord3("10,20", null, 0);
        Assert.Null(q!.Value.z);   // 没给 z → 调用方按"纯 XY"处理
    }

    [Fact]
    public void Relative_dz_adds_to_lastZ()
    {
        var p = MainWindow.ParseCoord3("@5,3,-2", (10, 20), 100);
        Assert.Equal(15.0, p!.Value.x, 9); Assert.Equal(23.0, p.Value.y, 9);
        Assert.Equal(98.0, p.Value.z!.Value, 9);
        var q = MainWindow.ParseCoord3("@5,3", (10, 20), 100);
        Assert.Null(q!.Value.z);
    }

    [Fact]
    public void Polar_has_no_z_and_bad_input_is_null()
    {
        var p = MainWindow.ParseCoord3("@10<90", (0, 0), 5);
        Assert.Equal(10.0, p!.Value.y, 6); Assert.Null(p.Value.z);
        Assert.Null(MainWindow.ParseCoord3("1,2,3,4", null, 0));
        Assert.Null(MainWindow.ParseCoord3("a,b,c", null, 0));
        Assert.Null(MainWindow.ParseCoord3("@1,2,3", null, 0));   // 相对坐标没有上一点
    }

    [Fact]
    public void Snap_candidates_carry_vertex_z()
    {
        var scene = new Scene();
        scene.Add(new PolylineEntity { Points = { (0, 0), (10, 0) }, Elevation = 100, Zs = new List<double> { -1, 1 } });   // 三维线 99 / 101
        scene.Add(new LineEntity { X0 = 50, Y0 = 0, X1 = 60, Y1 = 0, Elevation = 7 });
        var v = scene.SnapCandidates();
        var idx = new SnapPoints.Index(v);
        Assert.Equal(99.0, idx.FindNearest3(0, 0, 0.5)!.Value.z, 6);
        Assert.Equal(101.0, idx.FindNearest3(10, 0, 0.5)!.Value.z, 6);
        Assert.Equal(100.0, idx.FindNearest3(5, 0, 0.5)!.Value.z, 6);    // 段中点 = 两端均值
        Assert.Equal(7.0, idx.FindNearest3(55, 0, 0.5)!.Value.z, 6);     // 平面直线中点 = 标高
        Assert.Equal((0.0, 0.0), idx.FindNearest(0, 0, 0.5)!.Value);      // 二维口径不变
    }
}
