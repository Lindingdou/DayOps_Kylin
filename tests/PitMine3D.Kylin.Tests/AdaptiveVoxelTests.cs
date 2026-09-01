using System;
using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;

/// <summary>
/// 自适应体素细分（八叉细分 + N³ 占比 → 百分比块 → 边界无偏体积）回归 —— 忠实移植原 VoxelVolumeBuilder。
/// 用解析谓词（半空间/球）合成已知值：占比、细分体积、球体积收敛(自适应比均匀更准)。
/// </summary>
public class AdaptiveVoxelTests
{
    [Fact]
    public void SamplePercent_half_space_is_half()
    {
        // 单元 cell 中心(0,0,0) 尺寸 2；inside = x<0（半空间）→ N=4 子采样占比 = 0.5。
        Func<double, double, double, bool> inside = (x, y, z) => x < 0;
        double p = AdaptiveVoxel.SamplePercent(inside, 0, 0, 0, 2, 2, 2, 4);
        Assert.Equal(0.5, p, 6);
    }

    [Fact]
    public void SamplePercent_full_and_empty()
    {
        Assert.Equal(1.0, AdaptiveVoxel.SamplePercent((x, y, z) => true, 0, 0, 0, 2, 2, 2, 4), 6);
        Assert.Equal(0.0, AdaptiveVoxel.SamplePercent((x, y, z) => false, 0, 0, 0, 2, 2, 2, 4), 6);
        // N=1 退化为单中心二值
        Assert.Equal(1.0, AdaptiveVoxel.SamplePercent((x, y, z) => x < 0, -0.5, 0, 0, 2, 2, 2, 1), 6);
        Assert.Equal(0.0, AdaptiveVoxel.SamplePercent((x, y, z) => x < 0, 0.5, 0, 0, 2, 2, 2, 1), 6);
    }

    [Fact]
    public void RefineCell_solid_emits_one_full_leaf()
    {
        var outp = new List<VoxelSubCell>();
        AdaptiveVoxel.RefineCell((x, y, z) => true, outp, 0, 0, 0, 2, 2, 2, 2, 4);
        var leaf = Assert.Single(outp);
        Assert.Equal(1.0, leaf.Percent, 6);
        Assert.Equal(2, leaf.Sx, 6);          // 8 子中心皆内 → 免细分, 出满尺寸块
    }

    [Fact]
    public void RefineCell_empty_emits_nothing()
    {
        var outp = new List<VoxelSubCell>();
        AdaptiveVoxel.RefineCell((x, y, z) => false, outp, 0, 0, 0, 2, 2, 2, 2, 4);
        Assert.Empty(outp);
    }

    [Fact]
    public void RefineCell_half_boundary_sums_to_half_volume()
    {
        // cell 中心(0,0,0) 尺寸 2（体积 8）；inside=x<0 → 细分后子块占比加权总体积 ≈ 半 = 4。
        var outp = new List<VoxelSubCell>();
        AdaptiveVoxel.RefineCell((x, y, z) => x < 0, outp, 0, 0, 0, 2, 2, 2, 2, 4);
        double vol = 0;
        foreach (var s in outp) vol += s.Sx * s.Sy * s.Sz * s.Percent;
        Assert.InRange(vol, 3.8, 4.2);
    }

    [Fact]
    public void Voxelize_depth0_equals_uniform_center_count()
    {
        // depth=0 = 均匀中心法：应等于 中心落球内的 cell 数 × cellVol。
        Func<double, double, double, bool> ball = (x, y, z) => x * x + y * y + z * z < 25;   // r=5
        var uni = AdaptiveVoxel.Voxelize(ball, -6, -6, -6, 6, 6, 6, 2, 2, 2, 0, 1);
        Assert.Equal(uni.SolidCells * 8.0, uni.Volume, 6);   // cellVol=8
        Assert.True(uni.SolidCells > 0);
    }

    [Fact]
    public void Voxelize_sphere_adaptive_more_accurate_than_uniform()
    {
        Func<double, double, double, bool> ball = (x, y, z) => x * x + y * y + z * z < 25;   // r=5
        double truth = 4.0 / 3.0 * Math.PI * 125.0;   // ≈523.6

        var uni = AdaptiveVoxel.Voxelize(ball, -6, -6, -6, 6, 6, 6, 2, 2, 2, 0, 1);      // 均匀
        var ada = AdaptiveVoxel.Voxelize(ball, -6, -6, -6, 6, 6, 6, 2, 2, 2, 2, 4);      // 自适应 depth2/N4

        Assert.InRange(ada.Volume, 490.0, 555.0);                                   // 接近解析球体积
        Assert.True(ada.SubCells.Count > 0);                                        // 出了边界百分比块
        Assert.True(Math.Abs(ada.Volume - truth) <= Math.Abs(uni.Volume - truth));  // 自适应不劣于均匀
    }
}
