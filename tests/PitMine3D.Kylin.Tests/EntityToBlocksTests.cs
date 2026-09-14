using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>实体转块体回归（GWN 体素化 → 块体 → 配色方块）。</summary>
public class EntityToBlocksTests
{
    private static (double[] v, int[] t) BoxFlat(double s)
    {
        var (vv, tt) = PrimitiveBodies.Box(0, 0, 0, s, s, s);
        var v = new double[vv.Count * 3];
        for (int i = 0; i < vv.Count; i++) { v[i * 3] = vv[i].x; v[i * 3 + 1] = vv[i].y; v[i * 3 + 2] = vv[i].z; }
        var t = new int[tt.Count * 3];
        for (int i = 0; i < tt.Count; i++) { t[i * 3] = tt[i].a; t[i * 3 + 1] = tt[i].b; t[i * 3 + 2] = tt[i].c; }
        return (v, t);
    }

    private static List<BlockModel.Block> Voxelize(WindingNumberTester wn, double cell)
    {
        var blocks = new List<BlockModel.Block>();
        for (double z = wn.MinZ + cell * 0.5; z <= wn.MaxZ; z += cell)
            for (double y = wn.MinY + cell * 0.5; y <= wn.MaxY; y += cell)
                for (double x = wn.MinX + cell * 0.5; x <= wn.MaxX; x += cell)
                    if (wn.IsInsideClosed(x, y, z)) blocks.Add(new BlockModel.Block { X = x, Y = y, Z = z, Size = cell, Grade = 0 });
        return blocks;
    }

    [Fact]
    public void Box_voxelizes_to_full_grid()
    {
        var (v, t) = BoxFlat(10);
        var wn = new WindingNumberTester(v, t);
        var blocks = Voxelize(wn, 2.0);   // 10/2 = 5³ = 125 格全在内
        Assert.Equal(125, blocks.Count);
    }

    [Fact]
    public void Blocks_build_grade_colored_cells()
    {
        var (v, t) = BoxFlat(10);
        var wn = new WindingNumberTester(v, t);
        var blocks = Voxelize(wn, 5.0);   // 2³ = 8
        var cells = BlockModel.BuildCells(blocks, 0, 0);
        // 2×2×2 体素全是壳层块(没有块被六面包围) → 8 块都画；
        // 但块与块之间贴合的面互相挡住、不发，最后发的正是外表面 6×2×2 = 24 张
        var mesh = Assert.IsType<MeshEntity>(Assert.Single(cells));
        Assert.Equal(8, blocks.Count);
        Assert.Equal(6 * 2 * 2 * 4, mesh.Verts.Count);
    }

    [Fact]
    public void Voxel_block_volume_approaches_true()
    {
        var (v, t) = BoxFlat(10);
        var wn = new WindingNumberTester(v, t);
        double cell = 1.0;
        var blocks = Voxelize(wn, cell);
        double vol = blocks.Count * cell * cell * cell;
        Assert.InRange(vol, 900, 1000);
    }
}
