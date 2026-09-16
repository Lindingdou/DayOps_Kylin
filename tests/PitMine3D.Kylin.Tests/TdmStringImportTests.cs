using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>3DMine String File(.3ds 文本折线)导入 —— 忠实移植原 TdmStringReader 回归。</summary>
public class TdmStringImportTests
{
    // 依原 TdmStringReader 文档格式构造合成 .3ds：首两行 header, "0,…RGB" 折线头, "code,X,Y,Z" 顶点行。
    private static readonly string[] Sample =
    {
        @"C:\demo.3ds, 3DMine String File",
        "file_version=3DMine_2009",
        "1,7,0,1.00,x,x,Continuous,1",                        // 全局样式行(非顶点→边界, 跳过)
        "0,0,0,0,1,Continuous,0,0,1.00,0.00,0.00",            // 折线头: 末3浮点 RGB=(1,0,0)=红
        "1,100.0,200.0,10.0,",                                 // 顶点(尾随空字段)
        "2,110.0,200.0,10.0,",
        "3,110.0,210.0,10.0,",
        "0,0,0,0,2,Continuous,0,0,0.00,1.00,0.00",            // 下条折线头: RGB=(0,1,0)=绿
        "1,0.0,0.0,0.0,",
        "2,50.0,0.0,0.0,",
        "DbSText,注记文字,5,0,0",                               // 文字注记→跳过计数
    };

    [Fact]
    public void Parse_strings_yields_colored_polylines()
    {
        var res = new DxfImportService.EntityImportResult();
        TdmImportService.ParseStrings(Sample, res);
        Assert.Null(res.Error);

        var polys = res.Entities.OfType<PolylineEntity>().ToList();
        Assert.Equal(2, polys.Count);                          // 两条折线

        var red = polys[0];
        Assert.Equal(3, red.Points.Count);
        Assert.Equal(100.0, red.Points[0].x, 6); Assert.Equal(200.0, red.Points[0].y, 6);
        Assert.Equal(1f, red.Cr, 3); Assert.Equal(0f, red.Cg, 3); Assert.Equal(0f, red.Cb, 3);   // 红

        var green = polys[1];
        Assert.Equal(2, green.Points.Count);
        Assert.Equal(0f, green.Cr, 3); Assert.Equal(1f, green.Cg, 3); Assert.Equal(0f, green.Cb, 3); // 绿

        Assert.Contains(res.Warnings, w => w.Contains("DbSText"));   // 跳过 1 个文字注记
        Assert.Equal("3DMine", red.LayerName);
    }

    [Fact]
    public void Non_string_file_is_rejected()
    {
        var res = new DxfImportService.EntityImportResult();
        Assert.Throws<System.IO.InvalidDataException>(() =>
            TdmImportService.ParseStrings(new[] { "random", "data" }, res));
    }

    [Fact]
    public void Closed_polyline_detected_when_first_equals_last()
    {
        var lines = new[]
        {
            "x, 3DMine String File", "file_version=3DMine_2009",
            "0,0,0,0,1,Continuous,0,0,-1,-1,-1",   // 默认色(负→无色)
            "1,0,0,0,", "2,10,0,0,", "3,10,10,0,", "4,0,0,0,",   // 首尾重合→闭合
        };
        var res = new DxfImportService.EntityImportResult();
        TdmImportService.ParseStrings(lines, res);
        var pl = res.Entities.OfType<PolylineEntity>().Single();
        Assert.True(pl.Closed);
    }

    [Fact]
    public void Parse_strings_preserves_z_swaps_survey_axis_and_populates_extent()
    {
        var lines = new[]
        {
            "x, 3DMine String File", "file_version=3DMine_2009",
            "0,0,0,0,-1,-1,-1",
            "1,4380000,494000,1000,", "2,4380100,494200,1100,",
            "0,0,0,0,-1,-1,-1",
            "1,4380200,494100,1200,", "2,4380300,494300,1300,",
        };
        var res = new DxfImportService.EntityImportResult();

        TdmImportService.ParseStrings(lines, res);

        var polys = res.Entities.OfType<PolylineEntity>().ToList();
        Assert.Equal(2, polys.Count);
        Assert.Equal((494000d, 4380000d), polys[0].Points[0]);
        Assert.True(polys[0].Has3D);
        Assert.Equal(new[] { 1000d, 1100d }, polys[0].Zs);
        Assert.Equal(1f, polys[0].Cr, 3);
        Assert.Equal(1f, polys[0].Cg, 3);
        Assert.Equal(1f, polys[0].Cb, 3);
        Assert.All(polys, p => Assert.Equal("3DMine", p.LayerName));

        Assert.Equal(494000d, res.Bounds[0], 6);
        Assert.Equal(4380000d, res.Bounds[1], 6);
        Assert.Equal(494300d, res.Bounds[2], 6);
        Assert.Equal(4380300d, res.Bounds[3], 6);
        Assert.Equal(new[] { (494100d, 4380050d), (494200d, 4380250d) }, res.Centers);
        Assert.Contains(res.Warnings, w => w.Contains("换轴"));
    }

    [Fact]
    public void Closed_polyline_requires_matching_z_as_in_pitmine3d()
    {
        var lines = new[]
        {
            "x, 3DMine String File", "file_version=3DMine_2011",
            "0,0,0,0,-1,-1,-1",
            "1,0,0,10,", "2,10,0,10,", "3,0,0,11,",
        };
        var res = new DxfImportService.EntityImportResult();

        TdmImportService.ParseStrings(lines, res);

        Assert.False(res.Entities.OfType<PolylineEntity>().Single().Closed);
    }

    [Theory]
    [InlineData("file_version=3DMine_2009")]
    [InlineData("file_version=3DMine_2011")]
    [InlineData("file_version=3DMine_2025")]
    public void String_versions_with_the_same_marker_and_record_layout_are_accepted(string versionLine)
    {
        var lines = new[]
        {
            "x, 3DMine String File", versionLine,
            "0,0,0,0,-1,-1,-1",
            "1,494000,4380000,1000,", "2,494200,4380100,1100,",
        };
        var res = new DxfImportService.EntityImportResult();

        TdmImportService.ParseStrings(lines, res, "版本兼容样本");

        Assert.True(res.Success, res.Error);
        var poly = res.Entities.OfType<PolylineEntity>().Single();
        Assert.Equal("版本兼容样本", poly.LayerName);
        Assert.Equal((494000d, 4380000d), poly.Points[0]);
        Assert.Equal(new[] { 1000d, 1100d }, poly.Zs);
        Assert.Equal(new[] { 494000d, 4380000d, 494200d, 4380100d }, res.Bounds);
        Assert.DoesNotContain(res.Warnings, w => w.Contains("换轴"));
    }
}
