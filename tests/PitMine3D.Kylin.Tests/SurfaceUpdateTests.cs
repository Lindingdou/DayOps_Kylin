using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>更新煤层面/现状面回归 —— 观测点影响半径内 smoothstep 羽化 + IDW 估值; 区外不动; 影响片区。</summary>
public class SurfaceUpdateTests
{
    // 3×3 平面网格(z=0), x,y ∈ {0,50,100}, 8 三角
    static (double[] v, int[] t) FlatGrid()
    {
        var v = new double[] {
            0,0,0, 50,0,0, 100,0,0, 0,50,0, 50,50,0, 100,50,0, 0,100,0, 50,100,0, 100,100,0 };
        var t = new int[] { 0,1,4, 0,4,3, 1,2,5, 1,5,4, 3,4,7, 3,7,6, 4,5,8, 4,8,7 };
        return (v, t);
    }

    [Fact]
    public void Single_observation_feathers_by_smoothstep_of_distance()
    {
        var (v, t) = FlatGrid();
        var obs = new List<(double, double, double)> { (50, 50, 5) };   // 中心抬到 5
        var r = SurfaceUpdate.Evaluate(v, t, obs, new SurfaceUpdate.Options { InfluenceRadius = 60 });
        // 中心顶点(idx4) d=0 → w=1 → newZ=5
        Assert.True(r.Affected[4]);
        Assert.Equal(5.0, r.NewZ[4], 6);
        Assert.Equal(5.0, r.Disp[4], 6);
        // 边中点(idx1=(50,0)) d=50, R=60 → t=1/6, w=smoothstep=0.074074, newZ=0.37037
        Assert.True(r.Affected[1]);
        double tt = 1 - 50.0 / 60, w = tt * tt * (3 - 2 * tt);
        Assert.Equal(w * 5, r.NewZ[1], 6);
        // 角点(idx0=(0,0)) d=70.7 > 60 → 不动
        Assert.False(r.Affected[0]);
        Assert.Equal(0.0, r.NewZ[0], 9);
        // 中心+4边中点 = 5 受影响; 最大抬升 5; 净体积>0(抬升)
        Assert.Equal(5, r.AffectedVertices);
        Assert.Equal(5.0, r.MaxDisp, 6);
        Assert.Equal(0.0, r.MinDisp, 6);      // 无下沉
        Assert.True(r.NetVolume > 0);
    }

    [Fact]
    public void No_observation_within_radius_leaves_surface_unchanged()
    {
        var (v, t) = FlatGrid();
        var obs = new List<(double, double, double)> { (500, 500, 5) };   // 远在半径外
        var r = SurfaceUpdate.Evaluate(v, t, obs, new SurfaceUpdate.Options { InfluenceRadius = 60 });
        Assert.False(r.Any);
        Assert.Equal(0, r.AffectedVertices);
        for (int i = 0; i < 9; i++) Assert.Equal(0.0, r.NewZ[i], 9);
    }

    [Fact]
    public void Vertex_exactly_on_observation_takes_its_value()
    {
        var (v, t) = FlatGrid();
        var obs = new List<(double, double, double)> { (50, 50, 7) };
        var r = SurfaceUpdate.Evaluate(v, t, obs, new SurfaceUpdate.Options { InfluenceRadius = 30 });
        Assert.Equal(7.0, r.NewZ[4], 6);      // 落在观测点上 → 取其 z
    }

    [Fact]
    public void Separated_observations_form_two_clusters()
    {
        var (v, t) = FlatGrid();
        // 两观测点相距 √(80²+80²)=113 > 2R(=50) → 影响圈不搭接 → 2 片区
        var obs = new List<(double, double, double)> { (10, 10, 3), (90, 90, 3) };
        var r = SurfaceUpdate.Evaluate(v, t, obs, new SurfaceUpdate.Options { InfluenceRadius = 25 });
        Assert.Equal(2, r.Clusters.Count);
    }

    [Fact]
    public void Overlapping_observations_merge_into_one_cluster()
    {
        var (v, t) = FlatGrid();
        // 两观测点相距 20 < 2R(=100) → 影响圈搭接 → 并为 1 片区
        var obs = new List<(double, double, double)> { (45, 50, 3), (65, 50, 3) };
        var r = SurfaceUpdate.Evaluate(v, t, obs, new SurfaceUpdate.Options { InfluenceRadius = 50 });
        Assert.Single(r.Clusters);
        Assert.Equal(2, r.Clusters[0].ObsCount);
    }

    [Fact]
    public void Lowering_observation_gives_negative_net_volume()
    {
        var (v, t) = FlatGrid();
        var obs = new List<(double, double, double)> { (50, 50, -4) };   // 中心下沉
        var r = SurfaceUpdate.Evaluate(v, t, obs, new SurfaceUpdate.Options { InfluenceRadius = 60 });
        Assert.Equal(-4.0, r.NewZ[4], 6);
        Assert.True(r.MinDisp < 0);
        Assert.True(r.NetVolume < 0);        // 净下沉
    }
}
