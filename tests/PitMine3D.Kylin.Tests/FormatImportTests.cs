using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>OFF 网格 / 点数据 文本格式解析回归（全托管、跨平台可验证）。</summary>
public class FormatImportTests
{
    [Fact]
    public void Off_parses_quad_to_four_edges()
    {
        const string off = "OFF\n4 1 0\n0 0 0\n1 0 0\n1 1 0\n0 1 0\n4 0 1 2 3\n";
        var r = OffImportService.Parse(off);
        Assert.True(r.Success, r.Error);
        Assert.Equal(4, r.SegmentCount);                 // 四边形 4 条边
        Assert.Equal(4 * 12, r.LineVertices.Length);
        Assert.Equal(0, r.Bounds[0], 6); Assert.Equal(1, r.Bounds[2], 6);
        Assert.True(r.LayerGeometry.ContainsKey("OFF网格"));
    }

    [Fact]
    public void Off_dedups_shared_edges_of_two_triangles()
    {
        // 两三角共享对角边 (0-2)：三角 0-1-2 与 0-2-3 → 5 条唯一边（不是 6）
        const string off = "OFF\n4 2 0\n0 0 0\n1 0 0\n1 1 0\n0 1 0\n3 0 1 2\n3 0 2 3\n";
        var r = OffImportService.Parse(off);
        Assert.True(r.Success, r.Error);
        Assert.Equal(5, r.SegmentCount);                 // 6 边 - 1 共享 = 5
    }

    [Fact]
    public void Off_handles_comments_and_header_variants()
    {
        const string off = "# a comment\nCOFF\n3 1 0\n0 0 0\n2 0 0\n1 1 0\n3 0 1 2\n";
        var r = OffImportService.Parse(off);
        Assert.True(r.Success, r.Error);
        Assert.Equal(3, r.SegmentCount);
    }

    [Fact]
    public void Points_parse_csv_with_header_and_comment()
    {
        const string csv = "# 测量点\nname,x,y,z\nP1,10,20,5\nP2,30,40,6\n";
        var r = PointDataImportService.Parse(csv);
        Assert.True(r.Success, r.Error);
        Assert.Equal(2, r.Points.Count);                 // 表头/注释跳过
        Assert.Equal(10, r.Points[0].x, 6);
        Assert.Equal(40, r.Points[1].y, 6);
        Assert.Equal(6, r.Points[1].z, 6);
    }

    [Fact]
    public void Points_parse_whitespace_and_two_columns()
    {
        const string txt = "0 0\n10.5\t20.5\n-3 -4 9\n";   // 空格/制表符, 第2行仅2列
        var r = PointDataImportService.Parse(txt);
        Assert.True(r.Success, r.Error);
        Assert.Equal(3, r.Points.Count);
        Assert.Equal(10.5, r.Points[1].x, 6);
        Assert.Equal(0, r.Points[1].z, 6);               // 无第三列 → z=0
        Assert.Equal(-4, r.Points[2].y, 6);
    }

    [Fact]
    public void Points_empty_when_no_numbers()
    {
        var r = PointDataImportService.Parse("name,code,desc\nfoo,bar,baz\n");
        Assert.False(r.Success);
        Assert.NotNull(r.Error);
    }
}
