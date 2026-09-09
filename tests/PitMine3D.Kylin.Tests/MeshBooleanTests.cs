using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 三角网布尔运算（原「布尔-并/交/差/补」）回归：用两个相互重叠的轴对齐立方体，
/// 四种运算的体积都有解析解，逐个对上；再验非闭合输入必须明确失败而不是给个错网格。
/// </summary>
public class MeshBooleanTests
{
    // 轴对齐长方体（外法线），12 三角
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Box(
        double x0, double y0, double z0, double x1, double y1, double z1)
    {
        var v = new List<(double, double, double)>
        {
            (x0,y0,z0),(x1,y0,z0),(x1,y1,z0),(x0,y1,z0),
            (x0,y0,z1),(x1,y0,z1),(x1,y1,z1),(x0,y1,z1),
        };
        var t = new List<(int, int, int)>
        {
            (0,2,1),(0,3,2),   // 底 (法线 -Z)
            (4,5,6),(4,6,7),   // 顶 (+Z)
            (0,1,5),(0,5,4),   // 前 (-Y)
            (2,3,7),(2,7,6),   // 后 (+Y)
            (3,0,4),(3,4,7),   // 左 (-X)
            (1,2,6),(1,6,5),   // 右 (+X)
        };
        return (v, t);
    }

    private static double Volume(MeshBoolean.Result r) => MeshMetrics.RobustVolume(r.Verts, r.Tris);

    // A = [0,10]³ (1000)；B = [5,15]³ (1000)；交 = [5,10]³ (125)
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) A() => Box(0, 0, 0, 10, 10, 10);
    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) B() => Box(5, 5, 5, 15, 15, 15);

    [Fact]
    public void 交集_两立方体重叠区体积正确()
    {
        var a = A(); var b = B();
        var r = MeshBoolean.Compute(a.v, a.t, b.v, b.t, MeshBoolean.Op.Intersection);
        Assert.True(r.Success, r.Error);
        Assert.Equal(125.0, Volume(r), 3);
        Assert.True(MeshDiagnose.Analyze(r.Verts, r.Tris).IsClosed, "交集结果应是闭合体");
    }

    [Fact]
    public void 并集_体积等于两体之和减去交集()
    {
        var a = A(); var b = B();
        var r = MeshBoolean.Compute(a.v, a.t, b.v, b.t, MeshBoolean.Op.Union);
        Assert.True(r.Success, r.Error);
        Assert.Equal(1000 + 1000 - 125.0, Volume(r), 3);
        Assert.True(MeshDiagnose.Analyze(r.Verts, r.Tris).IsClosed, "并集结果应是闭合体");
    }

    [Fact]
    public void 差集_A减B等于A体积减交集()
    {
        var a = A(); var b = B();
        var r = MeshBoolean.Compute(a.v, a.t, b.v, b.t, MeshBoolean.Op.Difference);
        Assert.True(r.Success, r.Error);
        Assert.Equal(1000 - 125.0, Volume(r), 3);
        Assert.True(MeshDiagnose.Analyze(r.Verts, r.Tris).IsClosed, "差集结果应是闭合体");
    }

    [Fact]
    public void 补集_B减A等于B体积减交集()
    {
        var a = A(); var b = B();
        var r = MeshBoolean.Compute(a.v, a.t, b.v, b.t, MeshBoolean.Op.Complement);
        Assert.True(r.Success, r.Error);
        Assert.Equal(1000 - 125.0, Volume(r), 3);
    }

    [Fact]
    public void 不相交的两体_并集是两块_交集为空()
    {
        var a = Box(0, 0, 0, 1, 1, 1);
        var b = Box(5, 5, 5, 6, 6, 6);
        var u = MeshBoolean.Compute(a.v, a.t, b.v, b.t, MeshBoolean.Op.Union);
        Assert.True(u.Success, u.Error);
        Assert.Equal(2.0, Volume(u), 6);

        var i = MeshBoolean.Compute(a.v, a.t, b.v, b.t, MeshBoolean.Op.Intersection);
        Assert.False(i.Success);
        Assert.Contains("不相交", i.Error);
    }

    [Fact]
    public void 一体完全包住另一体_交集等于小体()
    {
        var outer = Box(0, 0, 0, 10, 10, 10);
        var inner = Box(3, 3, 3, 5, 5, 5);   // 8
        var r = MeshBoolean.Compute(outer.v, outer.t, inner.v, inner.t, MeshBoolean.Op.Intersection);
        Assert.True(r.Success, r.Error);
        Assert.Equal(8.0, Volume(r), 4);
    }

    [Fact]
    public void 开放面输入_明确失败并说明原因()
    {
        var a = A();
        var open = A();
        open.t.RemoveRange(0, 2);            // 掀掉底面 → 不闭合
        var r = MeshBoolean.Compute(a.v, a.t, open.v, open.t, MeshBoolean.Op.Union);
        Assert.False(r.Success);
        Assert.Contains("闭合", r.Error);
        Assert.Empty(r.Tris);                // 不能给出一个"看着像成功"的结果
    }

    [Fact]
    public void 刀切实体_保留下方并封盖成水密体()
    {
        var solid = Box(0, 0, 0, 10, 10, 10);
        // 刀：z = 4 的水平开放面（两三角，覆盖整个 XY 范围且外扩，保证切透）
        var kv = new List<(double x, double y, double z)> { (-5, -5, 4), (15, -5, 4), (15, 15, 4), (-5, 15, 4) };
        var kt = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };

        var r = MeshBoolean.CutByKnife(solid.v, solid.t, kv, kt, keepBelow: true);
        Assert.True(r.Success, r.Error);
        Assert.Equal(400.0, Volume(r), 2);   // 10×10×4
        Assert.True(MeshDiagnose.Analyze(r.Verts, r.Tris).IsClosed, "刀切结果应水密");
    }

    [Fact]
    public void 刀切实体_保留上方体积互补()
    {
        var solid = Box(0, 0, 0, 10, 10, 10);
        var kv = new List<(double x, double y, double z)> { (-5, -5, 4), (15, -5, 4), (15, 15, 4), (-5, 15, 4) };
        var kt = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };

        var r = MeshBoolean.CutByKnife(solid.v, solid.t, kv, kt, keepBelow: false);
        Assert.True(r.Success, r.Error);
        Assert.Equal(600.0, Volume(r), 2);   // 10×10×6
    }
}
