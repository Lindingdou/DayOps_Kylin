using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>封闭三角网体素化 回归 —— 忠实原「离散化模型」: 闭合网内格心成块体(GWN 内外判)。</summary>
public class MeshVoxelizerTests
{
    // 边长 s 的闭合立方体(8 顶点 + 12 三角, 一致外法向)。
    static (double[] v, int[] t) Cube(double s)
    {
        var v = new double[]
        {
            0, 0, 0,  s, 0, 0,  s, s, 0,  0, s, 0,   // 底 0-3
            0, 0, s,  s, 0, s,  s, s, s,  0, s, s,   // 顶 4-7
        };
        var t = new int[]
        {
            0, 2, 1,  0, 3, 2,   // 底 -z
            4, 5, 6,  4, 6, 7,   // 顶 +z
            0, 1, 5,  0, 5, 4,   // 前 -y
            3, 7, 6,  3, 6, 2,   // 后 +y
            1, 2, 6,  1, 6, 5,   // 右 +x
            0, 4, 7,  0, 7, 3,   // 左 -x
        };
        return (v, t);
    }

    [Fact]
    public void Cube_voxelizes_to_expected_block_count()
    {
        var (v, t) = Cube(10);
        var r = MeshVoxelizer.Voxelize(v, t, cellSize: 2);   // 5×5×5 格心 (1,3,5,7,9)³ 全在内
        Assert.False(r.TooLarge);
        Assert.Equal(5, r.Nx); Assert.Equal(5, r.Ny); Assert.Equal(5, r.Nz);
        Assert.Equal(125, r.Centers.Count);
        Assert.All(r.Centers, c => Assert.True(c.x > 0 && c.x < 10 && c.y > 0 && c.y < 10 && c.z > 0 && c.z < 10));
    }

    [Fact]
    public void Coarser_cell_fewer_blocks()
    {
        var (v, t) = Cube(10);
        var r = MeshVoxelizer.Voxelize(v, t, cellSize: 5);   // 2×2×2 格心 (2.5,7.5)³ 全在内
        Assert.Equal(8, r.Centers.Count);
    }

    [Fact]
    public void Too_large_is_guarded()
    {
        var (v, t) = Cube(10);
        var r = MeshVoxelizer.Voxelize(v, t, cellSize: 0.001, maxCells: 100_000);   // 1e12 格 → 拒
        Assert.True(r.TooLarge);
        Assert.Empty(r.Centers);
    }

    [Fact]
    public void Degenerate_input_is_safe()
    {
        Assert.Empty(MeshVoxelizer.Voxelize(null!, null!, 1).Centers);
        Assert.Empty(MeshVoxelizer.Voxelize(new double[] { 0, 0, 0 }, new int[] { 0, 0, 0 }, 1).Centers);
        var (v, t) = Cube(10);
        Assert.Empty(MeshVoxelizer.Voxelize(v, t, 0).Centers);   // cellSize=0
    }
}
