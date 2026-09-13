using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 八叉树叶块（忠实原 OctreeLeafBuilder）+「实体转块体 / 体素格网体积 生成块体」走均匀占位→叶块这条路的回归：
/// 叶块铺满占位且不重叠、实心内部并大块、薄体（煤层）不再整列丢失。
/// </summary>
public class OctreeLeafBuilderTests
{
    private static bool[] Keep(int nx, int ny, int nz, Func<int, int, int, bool> occ)
    {
        var k = new bool[(long)nx * ny * nz];
        for (int z = 0; z < nz; z++) for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++)
            k[x + y * (long)nx + z * (long)nx * ny] = occ(x, y, z);
        return k;
    }

    /// <summary>叶块铺回细格：每个占位细格恰被一个叶块盖住，非占位细格不被盖。</summary>
    private static void AssertTiles(int nx, int ny, int nz, bool[] keep, List<OctreeLeaf> leaves)
    {
        var cover = new int[keep.Length];
        foreach (var lf in leaves)
        {
            int s = lf.Size;
            for (int z = lf.Z; z < lf.Z + s; z++) for (int y = lf.Y; y < lf.Y + s; y++) for (int x = lf.X; x < lf.X + s; x++)
            {
                Assert.True(x < nx && y < ny && z < nz, $"叶块 ({lf.X},{lf.Y},{lf.Z}) s={s} 越出域");
                cover[x + y * (long)nx + z * (long)nx * ny]++;
            }
        }
        for (long i = 0; i < keep.Length; i++) Assert.Equal(keep[i] ? 1 : 0, cover[i]);
    }

    [Fact]
    public void Full_pow2_domain_collapses_to_single_root_leaf()
    {
        var keep = Keep(4, 4, 4, (_, _, _) => true);
        var leaves = OctreeLeafBuilder.BuildFromOccupancy(4, 4, 4, keep);
        var lf = Assert.Single(leaves);
        Assert.Equal((0, 0, 0, (byte)2), (lf.X, lf.Y, lf.Z, lf.Shift));
        Assert.Equal(64, lf.VolumeCells);
    }

    [Fact]
    public void Empty_occupancy_gives_no_leaves()
    {
        Assert.Empty(OctreeLeafBuilder.BuildFromOccupancy(3, 3, 3, new bool[27]));
        Assert.Empty(OctreeLeafBuilder.BuildFromOccupancy(0, 3, 3, Array.Empty<bool>()));
    }

    [Fact]
    public void Non_pow2_solid_domain_tiles_exactly_with_merged_interior()
    {
        // 6×5×3 全占位：根 8³，叶块须铺满且不出域；实心处并成 2³ 大块，块数远少于 90 格
        var keep = Keep(6, 5, 3, (_, _, _) => true);
        var leaves = OctreeLeafBuilder.BuildFromOccupancy(6, 5, 3, keep);
        AssertTiles(6, 5, 3, keep, leaves);
        Assert.Contains(leaves, l => l.Shift == 1);
        Assert.True(leaves.Count < 90);
        Assert.Equal(90, leaves.Sum(l => l.VolumeCells));
    }

    [Fact]
    public void Checkerboard_stays_at_finest_level()
    {
        var keep = Keep(4, 4, 4, (x, y, z) => ((x + y + z) & 1) == 0);
        var leaves = OctreeLeafBuilder.BuildFromOccupancy(4, 4, 4, keep);
        Assert.Equal(32, leaves.Count);
        Assert.All(leaves, l => Assert.Equal(0, l.Shift));
        AssertTiles(4, 4, 4, keep, leaves);
    }

    [Fact]
    public void Union_of_two_boxes_tiles_and_merges()
    {
        // 两个重叠立方体 [0,8)³ ∪ [4,12)³ 的占位（细格 1）
        var keep = Keep(12, 12, 12, (x, y, z) => (x < 8 && y < 8 && z < 8) || (x >= 4 && y >= 4 && z >= 4));
        var leaves = OctreeLeafBuilder.BuildFromOccupancy(12, 12, 12, keep);
        AssertTiles(12, 12, 12, keep, leaves);
        Assert.Contains(leaves, l => l.Shift == 2);   // 各自的内部并成 4³
        Assert.Equal(8 * 8 * 8 * 2 - 4 * 4 * 4, leaves.Sum(l => l.VolumeCells));
    }

    // ── 均匀占位 → 叶块 → 块体模型（实体转块体那条路）──

    private static (List<(double x, double y, double z)> v, List<(int a, int b, int c)> t) Surf(double x0, double y0, double w, double h, int n, Func<double, double, double> zf)
    {
        var v = new List<(double, double, double)>(); var t = new List<(int, int, int)>();
        for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++) { double x = x0 + w * i / n, y = y0 + h * j / n; v.Add((x, y, zf(x - x0, y - y0))); }
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) { int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1; t.Add((a, b, d)); t.Add((a, d, c)); }
        return (v, t);
    }

    /// <summary>大坐标下 15 m 厚的倾斜"煤层"闭合体（顶/底板放样成体）。</summary>
    private static BlockVoxelBuilder.MeshInput ThinSeam()
    {
        double X0 = 4_412_300.37, Y0 = 4_366_512.61;
        var top = Surf(X0, Y0, 800, 600, 20, (x, y) => 1300 + 0.08 * x + 6 * Math.Sin(y / 60));
        var bot = Surf(X0, Y0, 800, 600, 20, (x, y) => 1285 + 0.08 * x + 6 * Math.Sin(y / 60) - 4 * Math.Cos(x / 90));
        var solid = LayerSolid.FromSurfaces(top.v, top.t, bot.v, bot.t)!.Value;
        var input = BlockVoxelBuilder.FromMesh("seam", solid.verts, solid.tris);
        Assert.True(input.Closed);
        return input;
    }

    [Fact]
    public void Octree_model_keeps_every_occupied_cell_and_marks_coarse_leaves()
    {
        var (v, t) = PrimitiveBodies.Box(5, 5, 5, 10, 10, 10);
        var box = BlockVoxelBuilder.FromMesh("box", v, t);
        var r = BlockVoxelBuilder.Build(new[] { box }, 2.5, 2.5, 2.5, out _)!;   // 4×4×4 全在内
        Assert.Equal(64, r.KeepCount);
        var leaves = BlockVoxelBuilder.ToLeaves(r);
        Assert.Single(leaves);
        var meta = BlockVoxelBuilder.ToBlockModel(r, leaves, "box");
        var blk = Assert.Single(meta.Blocks);
        Assert.Equal((5, 5, 5), (blk.X, blk.Y, blk.Z));
        Assert.Equal(10, blk.Size, 9);                 // 边长 = 4 格 × 2.5
        Assert.Equal(1, meta.SubCellCount);            // 粗叶块计入变尺寸单元
        Assert.Equal(2.5, meta.Sx, 9);
        Assert.False(meta.IsRegular);
        Assert.Equal(BlockStorageMode.Sparse, meta.StorageMode);
    }

    [Fact]
    public void Thin_seam_octree_model_matches_uniform_volume_and_is_close_to_exact()
    {
        var seam = ThinSeam();
        double exact = seam.ExactVolume;
        var r = BlockVoxelBuilder.Build(new[] { seam }, 5, 5, 5, out _)!;   // 原版默认块尺寸 5
        var leaves = BlockVoxelBuilder.ToLeaves(r);
        var meta = BlockVoxelBuilder.ToBlockModel(r, leaves, "seam");
        Assert.Equal(leaves.Count, meta.Blocks.Count);
        // 叶块体积之和 = 占位 cell 数（一格不丢、不重）
        double cellsFromBlocks = meta.Blocks.Sum(b => Math.Pow(b.Size / meta.Sx, 3));
        Assert.Equal(r.KeepCount, cellsFromBlocks, 6);
        Assert.Equal(leaves.Count(l => l.Shift > 0), meta.SubCellCount);
        Assert.InRange(r.TotalVolume / exact, 0.97, 1.03);
        // 每个叶块中心都真在体内（独立用缠绕数复核）
        var gwn = new WindingNumberTester(seam.Verts, seam.Tris);
        Assert.All(meta.Blocks, b => Assert.True(gwn.IsInsideClosed(b.X, b.Y, b.Z)));
    }

    [Fact]
    public void Thin_seam_adaptive_percent_path_drops_columns_but_uniform_octree_does_not()
    {
        // 记录此前的失真根因：块 25 m > 层厚 15 m 时，母块间"两头中心都在外"的整列在自适应细分里被判成非边界而丢掉，
        // 体积少两成；均匀占位(块体生成的正路)没有这个洞。
        var seam = ThinSeam();
        double exact = seam.ExactVolume;
        var uni = BlockVoxelBuilder.Build(new[] { seam }, 25, 25, 25, out _)!;
        var ada = BlockVoxelBuilder.Build(new[] { seam }, 25, 25, 25, out _, depth: 2)!;
        Assert.True(ada.TotalVolume < uni.TotalVolume * 0.9, $"adaptive {ada.TotalVolume:0} vs uniform {uni.TotalVolume:0}");
        Assert.InRange(uni.TotalVolume / exact, 0.85, 1.15);
        var leaves = BlockVoxelBuilder.ToLeaves(uni);
        Assert.Equal(uni.KeepCount, leaves.Sum(l => l.VolumeCells));
    }
}
