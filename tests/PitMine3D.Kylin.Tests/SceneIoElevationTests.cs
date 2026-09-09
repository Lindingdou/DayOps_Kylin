using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 场景存档 / 撤销的标高保真回归。
///
/// 踩过的坑：SceneIO 存了色/层/线型/线宽/透明/隐藏，唯独漏了 <c>Elevation</c>。
/// 而撤销、重做、保存、打开走的都是同一套快照 —— 结果是"清空视图后 Ctrl+Z 找回来的线全塌到 0 层高"，
/// 三维线连基准一起丢（ZAt = Zs[i] + Elevation）。
/// </summary>
public class SceneIoElevationTests
{
    private static Scene RoundTrip(Scene src) => SceneIO.Load(SceneIO.Save(src));

    [Fact]
    public void 平面多段线的标高存得住()
    {
        var s = new Scene();
        var pl = new PolylineEntity { Elevation = 1234.5, Closed = true };
        pl.Points.AddRange(new[] { (0.0, 0.0), (10.0, 0.0), (10.0, 10.0) });
        s.Add(pl);

        var back = RoundTrip(s).Entities.OfType<PolylineEntity>().Single();
        Assert.Equal(1234.5, back.Elevation, 9);
        Assert.True(back.Closed);
        for (int i = 0; i < back.Points.Count; i++) Assert.Equal(1234.5, back.ZAt(i), 9);
    }

    [Fact]
    public void 三维多段线的标高与逐点高程都存得住()
    {
        var s = new Scene();
        var pl = new PolylineEntity { Elevation = 50, Zs = new List<double> { 0, 5, 10 } };
        pl.Points.AddRange(new[] { (0.0, 0.0), (10.0, 0.0), (20.0, 0.0) });
        s.Add(pl);
        var absBefore = Enumerable.Range(0, pl.Points.Count).Select(pl.ZAt).ToList();
        Assert.Equal(new double[] { 50, 55, 60 }, absBefore);

        var back = RoundTrip(s).Entities.OfType<PolylineEntity>().Single();
        Assert.Equal(50.0, back.Elevation, 9);
        Assert.Equal(absBefore, Enumerable.Range(0, back.Points.Count).Select(back.ZAt).ToList());
    }

    [Fact]
    public void 点与三角网的标高也存得住()
    {
        var s = new Scene();
        s.Add(new PointEntity { X = 1, Y = 2, Elevation = 77.25, Size = 3, Style = 3 });
        s.Add(new MeshEntity("网",
            new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0), (0, 1, 0) },
            new List<(int a, int b, int c)> { (0, 1, 2) }) { Elevation = 8.5 });

        var back = RoundTrip(s);
        var p = back.Entities.OfType<PointEntity>().Single();
        Assert.Equal(77.25, p.Elevation, 9);
        Assert.Equal(3, p.Style);
        var m = back.Entities.OfType<MeshEntity>().Single();
        Assert.Equal(8.5, m.Elevation, 9);
        Assert.Equal("网", m.Name);
    }

    [Fact]
    public void 标高为零时不写字段_老档案照样能读()
    {
        var s = new Scene();
        var pl = new PolylineEntity();
        pl.Points.AddRange(new[] { (0.0, 0.0), (1.0, 1.0) });
        s.Add(pl);
        string json = SceneIO.Save(s);
        Assert.DoesNotContain("\"E\"", json);              // 0 标高不占字节

        var dst = SceneIO.Load(json);
        Assert.Equal(0.0, dst.Entities.OfType<PolylineEntity>().Single().Elevation, 9);
    }

    [Fact]
    public void 其余属性一并保真()
    {
        var s = new Scene();
        var pl = new PolylineEntity
        {
            Elevation = 12, LayerName = "台阶线", Cr = 0.95f, Cg = 0.2f, Cb = 0.2f,
            LineWeight = 50, Transparency = 30, Dash = new double[] { 4, 2 }, Visible = false,
        };
        pl.Points.AddRange(new[] { (0.0, 0.0), (5.0, 0.0) });
        s.Add(pl);

        var back = RoundTrip(s).Entities.OfType<PolylineEntity>().Single();
        Assert.Equal(12.0, back.Elevation, 9);
        Assert.Equal("台阶线", back.LayerName);
        Assert.Equal(50, back.LineWeight);
        Assert.Equal(30, back.Transparency);
        Assert.Equal(new double[] { 4, 2 }, back.Dash);
        Assert.False(back.Visible);
    }
}
