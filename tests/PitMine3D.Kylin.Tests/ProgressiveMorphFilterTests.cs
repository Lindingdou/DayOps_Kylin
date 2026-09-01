using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>渐进形态学滤波(PMF)回归 —— 高出裸地面判非地面; 孤立物体(无地面在下)被开运算削去; 缓坡地形保留。</summary>
public class ProgressiveMorphFilterTests
{
    // 15×15 地面格 (x,y ∈ 0,2,…,28), z = slope·x; skip 里的格不放地面点
    static List<(double x, double y, double z)> Ground(double slope, HashSet<(int, int)> skip = null)
    {
        var pts = new List<(double, double, double)>();
        for (int i = 0; i <= 28; i += 2)
            for (int j = 0; j <= 28; j += 2)
            {
                if (skip != null && skip.Contains((i, j))) continue;
                pts.Add((i, j, slope * i));
            }
        return pts;
    }

    [Fact]
    public void Elevated_points_above_ground_are_non_ground()
    {
        // 平地面(225点) + 4×4 建筑点 z=6(叠在地面上) → 建筑非地面, 地面地面
        var pts = Ground(0);
        int building = 0;
        for (int i = 12; i <= 18; i += 2)
            for (int j = 12; j <= 18; j += 2) { pts.Add((i, j, 6)); building++; }
        var r = ProgressiveMorphFilter.Filter(pts, cell: 2, dhMax: 3);
        Assert.Equal(building, r.NonGroundCount);      // 16 建筑点
        Assert.Equal(225, r.GroundCount);              // 全部地面点
    }

    [Fact]
    public void Isolated_object_with_no_ground_beneath_is_opened_away()
    {
        // 3×3 格物体(z=5, 其下无地面点) 嵌在平地面里 → 形态学开运算(窗达标)把它削到裸地面 → 非地面
        var skip = new HashSet<(int, int)>();
        for (int i = 12; i <= 16; i += 2) for (int j = 12; j <= 16; j += 2) skip.Add((i, j));   // 9 格无地面
        var pts = Ground(0, skip);                     // 225-9=216 地面点
        foreach (var (i, j) in skip) pts.Add((i, j, 5));   // 9 物体点(仅此高度)
        var r = ProgressiveMorphFilter.Filter(pts, cell: 2, dhMax: 3, maxWindowM: 20);
        Assert.Equal(9, r.NonGroundCount);             // 物体被开运算削去 → 非地面
        Assert.Equal(216, r.GroundCount);
    }

    [Fact]
    public void Gentle_slope_terrain_is_preserved_as_ground()
    {
        // 缓坡 slope=0.15 < 坡度容差 0.3 → 不被当物体, 全判地面
        var pts = Ground(0.15);
        var r = ProgressiveMorphFilter.Filter(pts, cell: 2, slope: 0.3, dhMax: 3);
        Assert.Equal(0, r.NonGroundCount);
        Assert.Equal(225, r.GroundCount);
    }

    [Fact]
    public void Empty_input_yields_empty_result()
    {
        var r = ProgressiveMorphFilter.Filter(new List<(double, double, double)>(), cell: 2);
        Assert.Equal(0, r.GroundCount);
        Assert.Equal(0, r.NonGroundCount);
    }
}
