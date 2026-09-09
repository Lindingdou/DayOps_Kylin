using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 派生新多段线时的高程保真回归（裁剪 / 加密 / 抽稀 / 连接共用这套）。
///
/// 踩过的坑：PolylineEntity 的实际高程是 <c>Zs[i] + Elevation</c>，
/// 派生新线时把**绝对** Z 直接塞进 Zs、同时又从源拷了 Elevation，标高就被加了两遍 ——
/// 标高 50 的三维线裁完变成 100+，用户看到的就是"命令把图元属性改了"。
/// 所以重采出来的一律是绝对 Z，写回实体前必须过 <see cref="PolylineEdit.RebaseZ"/>。
/// </summary>
public class PolylineZPreserveTests
{
    // 源线：(0,0,10) → (10,0,20) → (20,0,30)，绝对高程
    private static readonly List<(double x, double y)> SrcPts = new() { (0, 0), (10, 0), (20, 0) };
    private static readonly List<double> SrcZ = new() { 10, 20, 30 };

    [Fact]
    public void 重采_落在原顶点上取到原值()
    {
        var z = PolylineEdit.SampleZAlong(SrcPts, SrcZ, SrcPts);
        Assert.Equal(new double[] { 10, 20, 30 }, z);
    }

    [Fact]
    public void 重采_段内点线性插值()
    {
        var dst = new List<(double x, double y)> { (5, 0), (15, 0) };
        var z = PolylineEdit.SampleZAlong(SrcPts, SrcZ, dst);
        Assert.Equal(15.0, z[0], 9);
        Assert.Equal(25.0, z[1], 9);
    }

    [Fact]
    public void 重采_加密后的点全在原线上_插值精确()
    {
        var dense = PolylineEdit.Densify(SrcPts, closed: false, maxStep: 2.5);
        var z = PolylineEdit.SampleZAlong(SrcPts, SrcZ, dense);
        Assert.Equal(dense.Count, z.Count);
        for (int i = 0; i < dense.Count; i++)
            Assert.Equal(10 + dense[i].x, z[i], 9);   // 该线上 z = 10 + x
    }

    [Fact]
    public void 重采_抽稀后的点是原顶点子集_取到原值()
    {
        var simp = PolylineSimplify.DouglasPeucker(
            new List<(double x, double y)> { (0, 0), (10, 0.001), (20, 0) }, 1.0);
        var z = PolylineEdit.SampleZAlong(SrcPts, SrcZ, simp);
        Assert.Equal(10.0, z[0], 6);
        Assert.Equal(30.0, z[^1], 6);
    }

    [Fact]
    public void 回基_减掉标高后再加回来等于原绝对高程()
    {
        const double elevation = 50;
        var abs = new List<double> { 50, 55, 60 };
        var zs = PolylineEdit.RebaseZ(abs, elevation);
        Assert.Equal(new double[] { 0, 5, 10 }, zs);
        for (int i = 0; i < zs.Count; i++)
            Assert.Equal(abs[i], zs[i] + elevation, 9);   // ZAt 复原
    }

    [Fact]
    public void 回基_标高为零时原样透传()
    {
        var abs = new List<double> { 1, 2, 3 };
        Assert.Equal(abs, PolylineEdit.RebaseZ(abs, 0));
    }

    [Fact]
    public void 不回基就会把标高加两遍_这正是要防的()
    {
        const double elevation = 50;
        var abs = new List<double> { 50, 55, 60 };
        // 错误做法：绝对 Z 直接当 Zs
        var wrong = abs.Select(z => z + elevation).ToList();
        Assert.Equal(100.0, wrong[0], 9);
        // 正确做法
        var right = PolylineEdit.RebaseZ(abs, elevation).Select(z => z + elevation).ToList();
        Assert.Equal(abs, right);
    }

    [Fact]
    public void 重采_平面源线退化输入不炸()
    {
        Assert.Empty(PolylineEdit.SampleZAlong(SrcPts, SrcZ, new List<(double x, double y)>()));
        var z = PolylineEdit.SampleZAlong(new List<(double x, double y)>(), new List<double>(),
            new List<(double x, double y)> { (0, 0), (1, 1) });
        Assert.Equal(new double[] { 0, 0 }, z);
        var one = PolylineEdit.SampleZAlong(new List<(double x, double y)> { (0, 0) }, new List<double> { 7 },
            new List<(double x, double y)> { (5, 5) });
        Assert.Equal(new double[] { 7 }, one);
    }
}
