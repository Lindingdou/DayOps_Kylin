using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 作业区域裁剪 RegionClip 已知值回归 —— 忠实原 PointCloudLib.RoadCenterline.RegionClip（需求07：
/// 道路中心线只在作业区域内生成）。点在多边形(射线,凹) / 并集 / 外扩包围盒 / 台阶线预筛 /
/// 折线裁到区域并集(边界 Z 线性插值 + 短段丢弃)。
/// </summary>
public class RegionClipTests
{
    // 单位方 [0,10]×[0,10]（扁平 [x,y,...]）。
    static readonly double[] Square = { 0, 0, 10, 0, 10, 10, 0, 10 };

    [Fact]
    public void PointInPolygon_ray_method_inside_outside()
    {
        Assert.True(RegionClip.PointInPolygon(5, 5, Square));
        Assert.False(RegionClip.PointInPolygon(15, 5, Square));
        Assert.False(RegionClip.PointInPolygon(5, 15, Square));
        Assert.False(RegionClip.PointInPolygon(-1, 5, Square));
    }

    [Fact]
    public void PointInPolygon_supports_concave()
    {
        // L 形凹多边形：(0,0)(10,0)(10,4)(4,4)(4,10)(0,10)。
        double[] ell = { 0, 0, 10, 0, 10, 4, 4, 4, 4, 10, 0, 10 };
        Assert.True(RegionClip.PointInPolygon(2, 8, ell));    // 竖臂内
        Assert.True(RegionClip.PointInPolygon(8, 2, ell));    // 横臂内
        Assert.False(RegionClip.PointInPolygon(8, 8, ell));   // 凹口(缺角)外
    }

    [Fact]
    public void PointInAny_is_union()
    {
        var rings = new List<double[]> { Square, new double[] { 20, 20, 30, 20, 30, 30, 20, 30 } };
        Assert.True(RegionClip.PointInAny(5, 5, rings));      // 在方1
        Assert.True(RegionClip.PointInAny(25, 25, rings));    // 在方2
        Assert.False(RegionClip.PointInAny(15, 15, rings));   // 两者皆不在
    }

    [Fact]
    public void ExpandedBBoxes_buffers_each_ring()
    {
        var boxes = RegionClip.ExpandedBBoxes(new List<double[]> { Square }, 5);
        Assert.Single(boxes);
        Assert.Equal((-5.0, -5.0, 15.0, 15.0), boxes[0]);
    }

    [Fact]
    public void FilterBenchLinesByBBox_keeps_lines_touching_box()
    {
        var boxes = RegionClip.ExpandedBBoxes(new List<double[]> { Square }, 5);   // (-5,-5,15,15)
        var inside = new double[] { 5, 5, 100, 8, 8, 100 };      // 顶点在框内
        var outside = new double[] { 100, 100, 0, 120, 120, 0 }; // 全在框外
        var kept = RegionClip.FilterBenchLinesByBBox(new List<double[]> { inside, outside }, boxes);
        Assert.Single(kept);
        Assert.Same(inside, kept[0]);
    }

    [Fact]
    public void ClipPolyline_clips_to_region_with_linear_Z_at_boundary()
    {
        // 水平线 y=5, 从 (-5,5,z=0) 到 (15,5,z=200), 裁到单位方 → 子段 (0,5)→(10,5)。
        // 左界 x=0 在 t=0.25 → z=50; 右界 x=10 在 t=0.75 → z=150。
        var flat = new double[] { -5, 5, 0, 15, 5, 200 };
        var res = RegionClip.ClipPolyline(flat, new List<double[]> { Square }, minKeepLen: 1);
        Assert.Single(res);
        var seg = res[0];
        Assert.Equal(6, seg.Length);
        Assert.Equal(0, seg[0], 6); Assert.Equal(5, seg[1], 6); Assert.Equal(50, seg[2], 6);    // 入界点 Z=50
        Assert.Equal(10, seg[3], 6); Assert.Equal(5, seg[4], 6); Assert.Equal(150, seg[5], 6);  // 出界点 Z=150
    }

    [Fact]
    public void ClipPolyline_drops_segments_shorter_than_minKeepLen()
    {
        // 同上子段长仅 10 < minKeepLen 20 → 丢弃, 空结果。
        var flat = new double[] { -5, 5, 0, 15, 5, 200 };
        var res = RegionClip.ClipPolyline(flat, new List<double[]> { Square }, minKeepLen: 20);
        Assert.Empty(res);
    }

    [Fact]
    public void ClipPolyline_whole_line_inside_kept_and_fully_outside_empty()
    {
        var inside = new double[] { 2, 2, 10, 8, 8, 20 };     // 全在方内
        var keptRes = RegionClip.ClipPolyline(inside, new List<double[]> { Square }, minKeepLen: 1);
        Assert.Single(keptRes);
        Assert.Equal(new double[] { 2, 2, 10, 8, 8, 20 }, keptRes[0]);

        var outside = new double[] { 50, 50, 0, 60, 60, 0 };  // 全在方外
        Assert.Empty(RegionClip.ClipPolyline(outside, new List<double[]> { Square }, minKeepLen: 1));
    }
}
