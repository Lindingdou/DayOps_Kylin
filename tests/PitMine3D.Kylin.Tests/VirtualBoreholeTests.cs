using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>虚拟钻孔(VirtualBorehole)回归 —— 忠实原 VirtualDrillEngine.Drill(竖直求交煤层顶底板 TIN)。</summary>
public class VirtualBoreholeTests
{
    // [0,100]² 上 z 恒定的平面(4 角), TinSampler 对平面精确
    private static List<(double x, double y, double z)> Flat(double z) => new()
    {
        (0, 0, z), (100, 0, z), (100, 100, z), (0, 100, z),
    };

    // 煤层 A(顶10底8) 上, B(顶5底3) 下
    private static List<VirtualBorehole.Seam> TwoSeams() => new()
    {
        new VirtualBorehole.Seam("B", Flat(5), Flat(3)),   // 乱序放
        new VirtualBorehole.Seam("A", Flat(10), Flat(8)),
    };

    [Fact]
    public void Drill_hits_both_seams_with_exact_elevations()
    {
        var hits = VirtualBorehole.Drill(50, 50, TwoSeams());
        Assert.Equal(2, hits.Count);
        // 自顶向下: A 先(顶10), B 后(顶5)
        Assert.Equal("A", hits[0].SeamCode);
        Assert.Equal(10, hits[0].RoofZ, 6);
        Assert.Equal(8, hits[0].FloorZ, 6);
        Assert.Equal(2, hits[0].Thickness, 6);
        Assert.Equal("B", hits[1].SeamCode);
        Assert.Equal(5, hits[1].RoofZ, 6);
        Assert.Equal(2, hits[1].Thickness, 6);
    }

    [Fact]
    public void Point_outside_seam_extent_not_in_log()
    {
        // A 只覆盖 [0,100]²; 在 (500,500) 钻 → A 落网外, 但 B 覆盖全域? 都是同域 → 都落网外
        var hits = VirtualBorehole.Drill(500, 500, TwoSeams());
        Assert.Empty(hits);
    }

    [Fact]
    public void Seam_present_only_where_both_roof_and_floor_defined()
    {
        // A 顶板覆盖大域, 底板只覆盖小域 → A 在大域外(仅顶无底)不见煤
        var seams = new List<VirtualBorehole.Seam>
        {
            new("A",
                new List<(double, double, double)> { (0, 0, 10), (100, 0, 10), (100, 100, 10), (0, 100, 10) },     // 顶板 [0,100]²
                new List<(double, double, double)> { (0, 0, 8), (10, 0, 8), (10, 10, 8), (0, 10, 8) }),            // 底板仅 [0,10]²
        };
        Assert.Single(VirtualBorehole.Drill(5, 5, seams));      // 小域内: 顶底都命中
        Assert.Empty(VirtualBorehole.Drill(50, 50, seams));     // 大域但底板外: 不见煤
    }

    [Fact]
    public void SeamsFromHorizonPoints_pairs_roof_and_floor()
    {
        var pts = new List<(string, bool, double, double, double)>
        {
            ("A", true, 0, 0, 10), ("A", true, 100, 0, 10), ("A", true, 50, 100, 10),
            ("A", false, 0, 0, 8), ("A", false, 100, 0, 8), ("A", false, 50, 100, 8),
            ("C", true, 0, 0, 3),   // C 只有顶板 → 无法配对, 丢弃
        };
        var seams = VirtualBorehole.SeamsFromHorizonPoints(pts);
        Assert.Single(seams);                        // 仅 A 顶底配对成功
        Assert.Equal("A", seams[0].Code);
        var hits = VirtualBorehole.Drill(30, 20, seams);
        Assert.Single(hits);
        Assert.Equal(2, hits[0].Thickness, 6);       // 10-8
    }

    [Fact]
    public void Csv_header_and_rows()
    {
        var csv = VirtualBorehole.ToCsv(VirtualBorehole.Drill(50, 50, TwoSeams()));
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal("seam,roof_z,floor_z,thickness", lines[0]);
        Assert.Equal(3, lines.Length);               // 表头 + 2 层
    }

    [Fact]
    public void Empty_seams_safe()
    {
        Assert.Empty(VirtualBorehole.Drill(0, 0, new List<VirtualBorehole.Seam>()));
        Assert.Empty(VirtualBorehole.Drill(0, 0, null!));
    }

    // ── 2D 柱状预览(BuildVirtualColumn) ──────────────────
    [Fact]
    public void VirtualColumn_maps_seam_elevations_to_depth_rects()
    {
        // 顶板最高者(z=100)作孔口(深度0)。煤3 顶100/底98 → 深度[0,2]; 煤5 顶90/底87 → 深度[10,13]。
        var hits = new List<VirtualBorehole.SeamHit>
        {
            new("3", 100, 98, 2),
            new("5", 90, 87, 3),
        };
        var ents = BoreholeRender.BuildVirtualColumn(hits, 0, 0, depthScale: 1, width: 10, labelH: 1);
        var rects = ents.OfType<RectEntity>().ToList();
        Assert.Equal(2, rects.Count);
        // 煤3: (0,0)-(10,-2)
        var r3 = rects[0];
        Assert.Equal(0, r3.Y0, 6); Assert.Equal(-2, r3.Y1, 6); Assert.Equal(10, r3.X1, 6);
        // 煤5: (0,-10)-(10,-13)
        var r5 = rects[1];
        Assert.Equal(-10, r5.Y0, 6); Assert.Equal(-13, r5.Y1, 6);
        // 有岩柱中轴(竖线)
        Assert.Contains(ents.OfType<LineEntity>(), l => System.Math.Abs(l.X0 - l.X1) < 1e-9 && System.Math.Abs(l.Y1 - (-13)) < 1e-6);
    }

    [Fact]
    public void VirtualColumn_empty_safe()
    {
        Assert.Empty(BoreholeRender.BuildVirtualColumn(new List<VirtualBorehole.SeamHit>(), 0, 0, 1, 10));
    }
}
