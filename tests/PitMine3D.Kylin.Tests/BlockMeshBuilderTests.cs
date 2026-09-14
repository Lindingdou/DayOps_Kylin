using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;
using C = PitMine3D.Kylin.Cad.BlockMeshBuilder.Cell;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 块体 → 六面体网格（体素）。此前每块只出一张平面矩形足印，三维里是一摞平板；
/// 这里验真立方体 + 整块剔除（口径照原版 SurfaceInstanceBuilder：剔块不剔面）。
/// </summary>
[Collection("MeshRenderMode")]
public class BlockMeshBuilderTests
{
    private const int VertsPerCell = 24;   // 6 面 × 4 顶点
    private const int TrisPerCell = 12;

    private static C Cube(double x, double y, double z, double s = 1, float r = 1, float g = 0, float b = 0)
        => new(x, y, z, s / 2, s / 2, s / 2, r, g, b);

    [Fact]
    public void SingleCell_isASolidBox_notAFlatFootprint()
    {
        var res = BlockMeshBuilder.Build(new[] { Cube(10, 20, 30, 2) });
        Assert.Equal(1, res.DrawnCells);
        Assert.Equal(0, res.CulledCells);
        Assert.Equal(VertsPerCell, res.Verts.Count);
        Assert.Equal(TrisPerCell, res.Tris.Count);
        // 三个轴都有厚度，各 2（此前 Z 方向为零 → 平板）
        Assert.Equal(9, res.Verts.Min(v => v.x), 9); Assert.Equal(11, res.Verts.Max(v => v.x), 9);
        Assert.Equal(19, res.Verts.Min(v => v.y), 9); Assert.Equal(21, res.Verts.Max(v => v.y), 9);
        Assert.Equal(29, res.Verts.Min(v => v.z), 9); Assert.Equal(31, res.Verts.Max(v => v.z), 9);
    }

    [Fact]
    public void OutwardWinding_everyFaceNormalPointsAwayFromCenter()
    {
        var res = BlockMeshBuilder.Build(new[] { Cube(0, 0, 0, 2) });
        foreach (var (a, b, c) in res.Tris)
        {
            var p = res.Verts[a]; var q = res.Verts[b]; var r = res.Verts[c];
            double ux = q.x - p.x, uy = q.y - p.y, uz = q.z - p.z;
            double vx = r.x - p.x, vy = r.y - p.y, vz = r.z - p.z;
            var n = (x: uy * vz - uz * vy, y: uz * vx - ux * vz, z: ux * vy - uy * vx);
            // 面心相对块心的方向，与法线同向 → 法线朝外
            var m = (x: (p.x + q.x + r.x) / 3, y: (p.y + q.y + r.y) / 3, z: (p.z + q.z + r.z) / 3);
            Assert.True(n.x * m.x + n.y * m.y + n.z * m.z > 0, "法线应朝外");
        }
    }

    [Fact]
    public void BuriedCell_isCulled_shellIsKept()
    {
        // 3×3×3 实心：只有正中那块六面都被占 → 26 块壳层 + 1 块剔除
        var cells = new List<C>();
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                for (int k = 0; k < 3; k++)
                    cells.Add(Cube(i, j, k));
        var res = BlockMeshBuilder.Build(cells);
        Assert.Equal(26, res.DrawnCells);
        Assert.Equal(1, res.CulledCells);
        // 发出的正好是立方体的外表面：6 个方向 × 3×3 = 54 张面（被邻居挡住的面不发，见类注释）
        Assert.Equal(54, res.FaceCount);
        Assert.Equal(54 * 4, res.Verts.Count);
    }

    /// <summary>
    /// 贴面的两块：公共面两边都被挡住 → 都不发；剩下 10 张面照发。
    /// 托管端两面受光且不做背面剔除，若把这对严丝合缝的重合面都发出去，深度完全相等、谁先画谁赢，
    /// 逐块次序一变侧面就糊成白噪（模型侧壁一摞 0.5 m 薄片时尤其明显）。挡住的面本就看不见。
    /// </summary>
    [Fact]
    public void CoveredFacesBetweenTouchingCells_areNotEmitted()
    {
        var res = BlockMeshBuilder.Build(new[] { Cube(0, 0, 0), Cube(1, 0, 0) });
        Assert.Equal(2, res.DrawnCells);
        Assert.Equal(0, res.CulledCells);                      // 谁都没被六面包围，两块都要画
        Assert.Equal(10, res.FaceCount);                       // 12 张面减掉重合的那一对
        Assert.Equal(10 * 2, res.Tris.Count);
    }

    /// <summary>孤立块没有邻居 → 六面全发（一个完整立方体）。</summary>
    [Fact]
    public void IsolatedCell_getsAllSixFaces()
    {
        var res = BlockMeshBuilder.Build(new[] { Cube(0, 0, 0) });
        Assert.Equal(6, res.FaceCount);
        Assert.Equal(VertsPerCell, res.Verts.Count);
        Assert.Equal(TrisPerCell, res.Tris.Count);
    }

    [Fact]
    public void SolidSlab_shrinksFromCubicToQuadraticFaceCount()
    {
        // 10×10×10 实心：只剩外壳 = 1000 - 8³ = 488 块
        var cells = new List<C>();
        for (int i = 0; i < 10; i++)
            for (int j = 0; j < 10; j++)
                for (int k = 0; k < 10; k++)
                    cells.Add(Cube(i, j, k));
        var res = BlockMeshBuilder.Build(cells);
        Assert.Equal(1000 - 8 * 8 * 8, res.DrawnCells);
        Assert.Equal(8 * 8 * 8, res.CulledCells);
        Assert.Equal(6 * 10 * 10, res.FaceCount);   // 只剩外表面 600 张，内部贴合面全省掉
    }

    [Fact]
    public void DifferentSizedNeighbour_isNotCounted_soNothingGoesMissing()
    {
        // 子块比父块小，面不重合 → 不算「占着」，两边都留（漏画比多画危险）
        var parent = Cube(0, 0, 0, 2);
        var small = new C(1.5, 0, 0, 0.5, 0.5, 0.5, 0, 1, 0);
        var res = BlockMeshBuilder.Build(new[] { parent, small });
        Assert.Equal(2, res.DrawnCells);
        Assert.Equal(0, res.CulledCells);
    }

    /// <summary>
    /// 变尺寸块(八叉树粗块)夹在实心里也要剔掉。
    ///
    /// 面表配对那套只认「同尺寸且正对」的邻居，粗块的面配不上 8 个小邻居的面 → 一块都剔不掉；
    /// .blk 的 311 万叶块因此全数超预算、只能抽稀画 30 万块，模型满屏窟窿。改按细格占用判后
    /// 同样的数据剔到 7.1 万块壳层。这里用 4×4×4 实心里嵌一个 2×2×2 粗块做最小复现。
    /// </summary>
    [Fact]
    public void OversizedInteriorCell_isCulled_byOccupancy()
    {
        var cells = new List<C>();
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < 4; j++)
                for (int k = 0; k < 4; k++)
                {
                    bool inner = i is 1 or 2 && j is 1 or 2 && k is 1 or 2;
                    if (!inner) cells.Add(Cube(i, j, k));      // 外壳 64-8 = 56 个单位块
                }
        cells.Add(new C(1.5, 1.5, 1.5, 1, 1, 1, 0, 1, 0));     // 中间那个 2×2×2 的粗块
        var res = BlockMeshBuilder.Build(cells);
        Assert.Equal(1, res.CulledCells);                       // 粗块六面全被壳层占住 → 剔掉
        Assert.Equal(56, res.DrawnCells);
    }

    /// <summary>非立方体单元：三轴半尺寸各自独立（.blk 的细格是 25×25×0.5 m 这种扁盒子）。</summary>
    [Fact]
    public void AnisotropicCell_keepsItsOwnThreeHalfExtents()
    {
        var res = BlockMeshBuilder.Build(new[] { new C(0, 0, 0, 12.5, 12.5, 0.25, 1, 1, 1) });
        Assert.Equal(1, res.DrawnCells);
        Assert.Equal(-12.5, res.Verts.Min(v => v.x), 9); Assert.Equal(12.5, res.Verts.Max(v => v.x), 9);
        Assert.Equal(-12.5, res.Verts.Min(v => v.y), 9); Assert.Equal(12.5, res.Verts.Max(v => v.y), 9);
        Assert.Equal(-0.25, res.Verts.Min(v => v.z), 9); Assert.Equal(0.25, res.Verts.Max(v => v.z), 9);
    }

    [Fact]
    public void PerCellColor_isCarriedOnEveryVertexOfThatCell()
    {
        var res = BlockMeshBuilder.Build(new[] { Cube(0, 0, 0, 1, 1, 0, 0), Cube(5, 0, 0, 1, 0, 0, 1) });
        Assert.Equal(res.Verts.Count, res.Colors.Count);
        for (int i = 0; i < VertsPerCell; i++) Assert.Equal((1f, 0f, 0f), res.Colors[i]);
        for (int i = VertsPerCell; i < 2 * VertsPerCell; i++) Assert.Equal((0f, 0f, 1f), res.Colors[i]);
    }

    [Fact]
    public void CellCap_truncatesInsteadOfBlowingUp()
    {
        var cells = new List<C>();
        for (int i = 0; i < 50; i++) cells.Add(Cube(i * 5, 0, 0));   // 彼此不相邻，全都暴露
        var res = BlockMeshBuilder.Build(cells, maxCells: 10);
        Assert.Equal(10, res.DrawnCells);
        Assert.Equal(40, res.TruncatedCells);
        Assert.Equal(10 * VertsPerCell, res.Verts.Count);   // 互不相邻 → 每块六面全发
    }

    [Fact]
    public void Empty_givesNoMesh()
    {
        var res = BlockMeshBuilder.Build(Array.Empty<C>());
        Assert.Equal(0, res.DrawnCells);
        Assert.Null(BlockMeshBuilder.ToMesh(res, "空", (1, 1, 1)));
    }

    [Fact]
    public void ToMesh_carriesPerVertexColoursAndBaseColour()
    {
        var res = BlockMeshBuilder.Build(new[] { Cube(0, 0, 0, 1, 0.25f, 0.5f, 0.75f) });
        var m = BlockMeshBuilder.ToMesh(res, "块体-测试", (0.1f, 0.2f, 0.3f))!;
        Assert.Equal("块体-测试", m.Name);
        Assert.Equal(0.1f, m.Cr); Assert.Equal(0.2f, m.Cg); Assert.Equal(0.3f, m.Cb);
        Assert.NotNull(m.VertColors);
        Assert.Equal(m.Verts.Count, m.VertColors!.Count);
        Assert.Equal((0.25f, 0.5f, 0.75f), m.VertColors[0]);
        // 逐顶点色进着色面缓冲（不是实体基色）
        var o = new List<float>();
        m.TessellateFaces(o);
        Assert.NotEmpty(o);
        bool sawCellColour = false;
        for (int i = 0; i + 5 < o.Count; i += 6)
        {
            // 面着色会乘光照系数 k∈(0,1]，比例仍应是 0.25:0.5:0.75
            float r = o[i + 3], g = o[i + 4], b = o[i + 5];
            if (r > 1e-4f) { Assert.Equal(2.0, g / r, 1); Assert.Equal(3.0, b / r, 1); sawCellColour = true; }
        }
        Assert.True(sawCellColour, "着色面应带逐顶点色");
    }

    [Fact]
    public void Edges_areCubeEdgesOnly_noTriangleDiagonals()
    {
        var res = BlockMeshBuilder.Build(new[] { Cube(0, 0, 0, 2) });
        // 一个立方体 12 条棱；按三角边推导会是 18 条(每面多一条对角线)
        Assert.Equal(12, res.Edges.Count);
        foreach (var (i, j) in res.Edges)
        {
            var a = res.Verts[i]; var b = res.Verts[j];
            // 棱只沿一个轴走，长度 = 边长；对角线会跨两个轴
            int moving = (Math.Abs(a.x - b.x) > 1e-9 ? 1 : 0) + (Math.Abs(a.y - b.y) > 1e-9 ? 1 : 0) + (Math.Abs(a.z - b.z) > 1e-9 ? 1 : 0);
            Assert.Equal(1, moving);
        }
        var m = BlockMeshBuilder.ToMesh(res, "块", (1, 1, 1))!;
        Assert.Equal(12, m.Edges.Count);   // 网格用显式棱, 不回落到三角边
    }

    [Fact]
    public void SharedEdges_betweenTouchingCubes_areNotDrawnTwice()
    {
        var res = BlockMeshBuilder.Build(new[] { Cube(0, 0, 0), Cube(1, 0, 0) });
        // 两块贴面：公共面那 4 条棱两块共用 → 12 + 12 - 4 = 20
        Assert.Equal(20, res.Edges.Count);
    }
}
