using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>KDF 导出 round-trip: 导出字节 → KdfImportService 回读, 实体/图层/色/文字保真。</summary>
public class KdfExportTests
{
    [Fact]
    public void Export_polyline_and_text_roundtrips_through_import()
    {
        var scene = new Scene();
        scene.Add(new LineEntity { X0 = 1, Y0 = 2, X1 = 11, Y1 = 2, LayerName = "开采境界", Cr = 1f, Cg = 0f, Cb = 0f });
        var pl = new PolylineEntity { Closed = true, LayerName = "台阶" };
        pl.Points.Add((0, 0)); pl.Points.Add((10, 0)); pl.Points.Add((10, 10));
        scene.Add(pl);
        scene.Add(new TextEntity { X = 3, Y = 4, Height = 2.5, Text = "标高", Rotation = 0, LayerName = "注记" });

        var (bytes, n) = KdfExportService.BuildBytes(scene);
        Assert.Equal(3, n);

        var res = KdfImportService.LoadBytes(bytes);
        Assert.Null(res.Error);

        var polys = res.Entities.OfType<PolylineEntity>().ToList();
        var texts = res.Entities.OfType<TextEntity>().ToList();
        Assert.True(polys.Count >= 2);                          // 直线(2点) + 闭合多段线(4点)
        Assert.Single(texts);

        // 直线 → 2 点折线, 端点保真
        var line = polys.First(p => p.Points.Count == 2);
        Assert.Equal(1, line.Points[0].x, 6); Assert.Equal(2, line.Points[0].y, 6);
        Assert.Equal(11, line.Points[1].x, 6);

        // 闭合多段线 → 4 点(首点回写)
        var closed = polys.First(p => p.Points.Count == 4);
        Assert.Equal(closed.Points[0].x, closed.Points[3].x, 6);
        Assert.Equal(closed.Points[0].y, closed.Points[3].y, 6);

        // 文字 内容/位置/高度 保真(GBK)
        Assert.Equal("标高", texts[0].Text);
        Assert.Equal(3, texts[0].X, 6); Assert.Equal(4, texts[0].Y, 6);
        Assert.Equal(2.5, texts[0].Height, 6);

        // 图层随实体回读
        Assert.Contains(res.LayerOrder, l => l == "开采境界" || l == "台阶" || l == "注记");
    }

    [Fact]
    public void Export_multiline_text_roundtrips_as_mtext()
    {
        var scene = new Scene();
        scene.Add(new TextEntity { X = 1, Y = 2, Height = 3, Text = "甲\n乙\n丙" });   // 多行 → AcDbMText
        var (bytes, n) = KdfExportService.BuildBytes(scene);
        Assert.Equal(1, n);
        var res = KdfImportService.LoadBytes(bytes);
        var txt = res.Entities.OfType<TextEntity>().Single();
        Assert.Equal("甲\n乙\n丙", txt.Text);   // 多行合成单一实体, GBK 往返
    }

    [Fact]
    public void Export_circle_becomes_polyline_with_correct_extent()
    {
        var scene = new Scene();
        scene.Add(new CircleEntity { Cx = 5, Cy = 5, Radius = 3 });
        var (bytes, n) = KdfExportService.BuildBytes(scene);
        Assert.Equal(1, n);
        var res = KdfImportService.LoadBytes(bytes);
        var poly = res.Entities.OfType<PolylineEntity>().Single();
        Assert.True(poly.Points.Count >= 72);                  // 圆 → 72 段折线
        // 顶点都在半径 3 的圆上
        foreach (var (x, y) in poly.Points)
            Assert.Equal(3.0, System.Math.Sqrt((x - 5) * (x - 5) + (y - 5) * (y - 5)), 3);
    }
}
