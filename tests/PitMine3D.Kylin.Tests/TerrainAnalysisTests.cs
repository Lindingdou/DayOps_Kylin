using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>地形分析（坡度着色）回归。</summary>
public class TerrainAnalysisTests
{
    [Fact]
    public void Flat_triangle_zero_slope()
    {
        double s = TerrainAnalysis.SlopeDegrees((0, 0, 0), (1, 0, 0), (0, 1, 0));
        Assert.Equal(0, s, 3);
    }

    [Fact]
    public void Vertical_triangle_ninety_slope()
    {
        double s = TerrainAnalysis.SlopeDegrees((0, 0, 0), (1, 0, 0), (0, 0, 1));   // XZ 平面
        Assert.Equal(90, s, 3);
    }

    [Fact]
    public void Forty_five_degree_slope()
    {
        double s = TerrainAnalysis.SlopeDegrees((0, 0, 0), (1, 0, 0), (0, 1, 1));   // 沿 Y 抬升
        Assert.Equal(45, s, 2);
    }

    [Fact]
    public void Slope_color_green_flat_red_steep()
    {
        var flat = TerrainAnalysis.SlopeColor(0);
        var steep = TerrainAnalysis.SlopeColor(60);
        Assert.True(flat.g > flat.r);      // 平=绿主导
        Assert.True(steep.r > steep.g);    // 陡=红主导
    }

    [Fact]
    public void SlopeMap_three_edges_per_triangle()
    {
        var pts = new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0), (0, 1, 0) };
        var tris = new List<(int a, int b, int c)> { (0, 1, 2) };
        Assert.Equal(3, TerrainAnalysis.BuildSlopeMap(pts, tris).Count);
    }

    [Fact]
    public void Flat_triangle_has_no_aspect()
    {
        Assert.Equal(-1, TerrainAnalysis.AspectDegrees((0, 0, 0), (1, 0, 0), (0, 1, 0)), 3);
    }

    [Fact]
    public void Sloped_triangle_aspect_in_range()
    {
        double asp = TerrainAnalysis.AspectDegrees((0, 0, 0), (1, 0, 0), (0, 1, 1));   // 有坡
        Assert.InRange(asp, 0, 360);
    }

    [Fact]
    public void Hsv_primaries()
    {
        var red = TerrainAnalysis.HsvToRgb(0, 1, 1);
        Assert.True(red.r > 0.9f && red.g < 0.1f && red.b < 0.1f);
        var green = TerrainAnalysis.HsvToRgb(120, 1, 1);
        Assert.True(green.g > 0.9f && green.r < 0.1f);
        var blue = TerrainAnalysis.HsvToRgb(240, 1, 1);
        Assert.True(blue.b > 0.9f && blue.g < 0.1f);
    }

    [Fact]
    public void Flat_surface_volume_equals_height_times_area()
    {
        // 单位正方形高 10, 基准 0 → 上方体积 = 10*1 = 10
        var pts = new List<(double x, double y, double z)>
        {
            (0, 0, 10), (1, 0, 10), (1, 1, 10), (0, 1, 10)
        };
        var tris = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        var (above, below, net) = TerrainAnalysis.Volume(pts, tris, 0);
        Assert.Equal(10, above, 4);
        Assert.Equal(0, below, 4);
        Assert.Equal(10, net, 4);
    }

    [Fact]
    public void Below_base_counts_as_fill()
    {
        var pts = new List<(double x, double y, double z)>
        {
            (0, 0, -2), (1, 0, -2), (1, 1, -2), (0, 1, -2)
        };
        var tris = new List<(int a, int b, int c)> { (0, 1, 2), (0, 2, 3) };
        var (above, below, net) = TerrainAnalysis.Volume(pts, tris, 0);
        Assert.Equal(0, above, 4);
        Assert.Equal(2, below, 4);
        Assert.Equal(-2, net, 4);
    }

    [Fact]
    public void ElevationColor_low_green_high_brown()
    {
        var low = TerrainAnalysis.ElevationColor(0, 0, 100);     // 最低
        var high = TerrainAnalysis.ElevationColor(100, 0, 100);  // 最高
        Assert.True(low.g > low.r);      // 低=绿主导
        Assert.True(high.r > high.g);    // 高=棕(红主导)
    }

    [Fact]
    public void PointInPolygon_square()
    {
        var poly = new List<(double x, double y)> { (0, 0), (4, 0), (4, 4), (0, 4) };
        Assert.True(PitMine3D.Kylin.Cad.Draw.LineMath.PointInPolygon(2, 2, poly));
        Assert.False(PitMine3D.Kylin.Cad.Draw.LineMath.PointInPolygon(5, 5, poly));
    }

    [Fact]
    public void VolumeWithinBoundary_excludes_outside_triangles()
    {
        // 两三角: 一个质心在边界内、一个在外 → 只算内的
        var pts = new List<(double x, double y, double z)>
        {
            (0, 0, 10), (1, 0, 10), (1, 1, 10),   // 三角0 质心≈(0.67,0.33) 在内
            (10, 10, 10), (11, 10, 10), (11, 11, 10) // 三角1 质心≈(10.67,10.33) 在外
        };
        var tris = new List<(int a, int b, int c)> { (0, 1, 2), (3, 4, 5) };
        var boundary = new List<(double x, double y)> { (-1, -1), (2, -1), (2, 2), (-1, 2) };
        var (above, _, _) = TerrainAnalysis.VolumeWithinBoundary(pts, tris, 0, boundary);
        Assert.Equal(0.5 * 10, above, 3);   // 只 1 个三角(面积0.5, 高10)
    }

    [Fact]
    public void TwoEpoch_uniform_rise_is_fill()
    {
        // 第一期 z=0, 第二期 z=5, 同单位正方形 → 填方=5*面积(1), 挖方=0
        var e1 = new List<(double x, double y, double z)> { (0, 0, 0), (1, 0, 0), (1, 1, 0), (0, 1, 0) };
        var e2 = new List<(double x, double y, double z)> { (0, 0, 5), (1, 0, 5), (1, 1, 5), (0, 1, 5) };
        var (cut, fill, net) = TerrainAnalysis.TwoEpochVolume(e1, e2, 16);
        Assert.Equal(0, cut, 3);
        Assert.Equal(5, fill, 3);
        Assert.Equal(5, net, 3);
    }

    // ── 两期算量分标高带(原 VolumeReportGenerator「按标高带」) ──
    [Fact]
    public void TwoEpoch_by_elevation_conserves_total()
    {
        // 混合升降: 左半升(填) 右半降(挖), 分带后各带挖/填之和须==整体挖/填(守恒)
        var e1 = new List<(double x, double y, double z)> { (0, 0, 10), (10, 0, 10), (10, 10, 10), (0, 10, 10), (5, 5, 10) };
        var e2 = new List<(double x, double y, double z)>
        {
            (0, 0, 16), (10, 0, 4), (10, 10, 4), (0, 10, 16), (5, 5, 10),   // 左 +6, 右 −6
        };
        int n = 24;
        var (cut, fill, _) = TerrainAnalysis.TwoEpochVolume(e1, e2, n);
        var bands = TerrainAnalysis.TwoEpochVolumeByElevation(e1, e2, n, bandHeight: 1);
        Assert.NotEmpty(bands);
        double bcut = 0, bfill = 0;
        foreach (var b in bands) { bcut += b.Cut; bfill += b.Fill; Assert.True(b.ZHigh >= b.ZLow); }
        Assert.Equal(cut, bcut, 2);      // 分带挖和 == 整体挖(守恒)
        Assert.Equal(fill, bfill, 2);    // 分带填和 == 整体填
    }

    [Fact]
    public void TwoEpoch_by_elevation_uniform_rise_bands_span_change_range()
    {
        // z=0 → z=5 均匀升: 全填, 变化柱 [0,5], bandHeight=1 → 5 带全在 [0,5], 挖=0
        var e1 = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0) };
        var e2 = new List<(double x, double y, double z)> { (0, 0, 5), (10, 0, 5), (10, 10, 5), (0, 10, 5) };
        int n = 20;
        var (_, fillTot, _) = TerrainAnalysis.TwoEpochVolume(e1, e2, n);
        var bands = TerrainAnalysis.TwoEpochVolumeByElevation(e1, e2, n, bandHeight: 1);
        double bfill = 0, bcut = 0;
        foreach (var b in bands)
        {
            bfill += b.Fill; bcut += b.Cut;
            Assert.InRange(b.ZLow, -1e-6, 5 + 1e-6);       // 所有带落在变化区间 [0,5]
            Assert.InRange(b.ZHigh, -1e-6, 5 + 1e-6);
        }
        Assert.Equal(0, bcut, 3);                          // 纯填无挖
        Assert.Equal(fillTot, bfill, 2);                   // 守恒
    }

    [Fact]
    public void TwoEpoch_by_elevation_csv_header_and_conservation()
    {
        var e1 = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0) };
        var e2 = new List<(double x, double y, double z)> { (0, 0, 4), (10, 0, 4), (10, 10, 4), (0, 10, 4) };
        var bands = TerrainAnalysis.TwoEpochVolumeByElevation(e1, e2, 16, bandHeight: 2);
        var csv = TerrainAnalysis.TwoEpochByElevationCsv(bands);
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal("z_low,z_high,cut,fill,net", lines[0]);
        Assert.Equal(bands.Count + 1, lines.Length);
    }

    [Fact]
    public void TwoEpoch_by_elevation_empty_safe()
    {
        var empty = new List<(double x, double y, double z)>();
        Assert.Empty(TerrainAnalysis.TwoEpochVolumeByElevation(empty, empty, 8, 1));
        Assert.Equal("z_low,z_high,cut,fill,net\n", TerrainAnalysis.TwoEpochByElevationCsv(new List<TerrainAnalysis.CutFillBand>()));
    }

    // ── 两期算量按连通块(原 VolumeReportGenerator「按连通块」) ──
    [Fact]
    public void TwoEpoch_by_part_uniform_rise_is_one_fill_zone()
    {
        // 全域均匀升 → 恰 1 个填块, 无挖块; 该块体积==整体填(守恒)
        var e1 = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0) };
        var e2 = new List<(double x, double y, double z)> { (0, 0, 5), (10, 0, 5), (10, 10, 5), (0, 10, 5) };
        int n = 20;
        var (_, fillTot, _) = TerrainAnalysis.TwoEpochVolume(e1, e2, n);
        var parts = TerrainAnalysis.TwoEpochVolumeByPart(e1, e2, n);
        Assert.Single(parts);
        Assert.False(parts[0].IsCut);
        Assert.Equal(fillTot, parts[0].Volume, 2);
        Assert.Equal(0, parts[0].Id);                          // 最大块 id=0
    }

    [Fact]
    public void TwoEpoch_by_part_conserves_by_kind()
    {
        // 左升右降 → 挖块与填块分离; 各类体积和==整体对应量(守恒)
        var e1 = new List<(double x, double y, double z)> { (0, 0, 10), (10, 0, 10), (10, 10, 10), (0, 10, 10), (5, 5, 10) };
        var e2 = new List<(double x, double y, double z)> { (0, 0, 16), (10, 0, 4), (10, 10, 4), (0, 10, 16), (5, 5, 10) };
        int n = 24;
        var (cut, fill, _) = TerrainAnalysis.TwoEpochVolume(e1, e2, n);
        var parts = TerrainAnalysis.TwoEpochVolumeByPart(e1, e2, n);
        Assert.NotEmpty(parts);
        double pcut = 0, pfill = 0;
        foreach (var p in parts) { if (p.IsCut) pcut += p.Volume; else pfill += p.Volume; }
        Assert.Equal(cut, pcut, 2);
        Assert.Equal(fill, pfill, 2);
        // 体积降序
        for (int i = 1; i < parts.Count; i++) Assert.True(parts[i - 1].Volume >= parts[i].Volume);
    }

    [Fact]
    public void TwoEpoch_by_part_csv_and_empty_safe()
    {
        var e1 = new List<(double x, double y, double z)> { (0, 0, 0), (10, 0, 0), (10, 10, 0), (0, 10, 0) };
        var e2 = new List<(double x, double y, double z)> { (0, 0, 3), (10, 0, 3), (10, 10, 3), (0, 10, 3) };
        var csv = TerrainAnalysis.TwoEpochByPartCsv(TerrainAnalysis.TwoEpochVolumeByPart(e1, e2, 12));
        Assert.StartsWith("id,kind,volume,cells\n", csv);
        Assert.Contains(",fill,", csv);
        var empty = new List<(double x, double y, double z)>();
        Assert.Empty(TerrainAnalysis.TwoEpochVolumeByPart(empty, empty, 8));
        Assert.Equal("id,kind,volume,cells\n", TerrainAnalysis.TwoEpochByPartCsv(new List<TerrainAnalysis.CutFillPart>()));
    }

    [Fact]
    public void PolygonAreaXY_shoelace()
    {
        // 10×10 方形 → 100; 三角(0,0)-(4,0)-(0,3) → 6; <3 点 → 0。
        Assert.Equal(100, TerrainAnalysis.PolygonAreaXY(new List<(double x, double y)> { (0, 0), (10, 0), (10, 10), (0, 10) }), 6);
        Assert.Equal(6, TerrainAnalysis.PolygonAreaXY(new List<(double x, double y)> { (0, 0), (4, 0), (0, 3) }), 6);
        Assert.Equal(0, TerrainAnalysis.PolygonAreaXY(new List<(double x, double y)> { (0, 0), (1, 1) }), 6);
    }
}
