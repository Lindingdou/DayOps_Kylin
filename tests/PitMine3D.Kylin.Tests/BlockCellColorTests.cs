using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;
using B = PitMine3D.Kylin.Cad.BlockModel.Block;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 块体分类离散着色(BuildCellsColored)回归 —— 按属性值分类, 同值同色/异值异色。
/// 块体现在出的是六面体网格(一张三角网, 逐顶点色), 不再是每块一张平面 RectEntity。
/// </summary>
public class BlockCellColorTests
{
    [Fact]
    public void Colored_cells_use_supplied_color_per_block()
    {
        var blocks = new List<B>
        {
            new() { X = 0, Y = 0, Z = 0, Size = 2, Grade = 5 },
            new() { X = 10, Y = 0, Z = 0, Size = 2, Grade = 10 },
            new() { X = 20, Y = 0, Z = 0, Size = 2, Grade = 5 },
        };
        // 分类: 不同品位值 → 不同色 id
        var catId = new Dictionary<double, int>();
        foreach (var b in blocks) if (!catId.ContainsKey(b.Grade)) catId[b.Grade] = catId.Count;
        Assert.Equal(2, catId.Count);   // 2 类别(5, 10)

        var cells = BlockModel.BuildCellsColored(blocks, b => (catId[b.Grade] * 0.1f, 0.5f, 0.2f));
        var mesh = Assert.IsType<MeshEntity>(Assert.Single(cells));
        Assert.Equal(3 * VertsPerCell, mesh.Verts.Count);          // 3 块各一个立方体(彼此不相邻, 无剔除)
        // 块0,块2 同品位(5) → 同色; 块1(10) 异色
        Assert.Equal(CellColor(mesh, 0).r, CellColor(mesh, 2).r, 6);
        Assert.NotEqual(CellColor(mesh, 0).r, CellColor(mesh, 1).r);
        // 立方体几何 = 中心 ± size/2，**三个轴都有**(此前只有 XY 足印, Z 被丢掉)
        var b0 = mesh.Bounds;
        Assert.Equal(-1, b0.minX, 6);
        Assert.Equal(-1, b0.minY, 6); Assert.Equal(1, b0.maxY, 6);
        Assert.Equal(-1, b0.minZ, 6); Assert.Equal(1, b0.maxZ, 6);
    }

    [Fact]
    public void Continuous_grade_color_still_works()
    {
        var blocks = new List<B> { new() { X = 0, Y = 0, Z = 0, Size = 2, Grade = 5 } };
        var cells = BlockModel.BuildCells(blocks, 0, 10);   // 连续品位色(蓝→红)
        var mesh = Assert.IsType<MeshEntity>(Assert.Single(cells));
        Assert.Equal(VertsPerCell, mesh.Verts.Count);
        Assert.Equal(12, mesh.Tris.Count);                  // 六面体 = 6 面 × 2 三角
    }

    // ── 分级区间(graduated)配色: 上界升序, [b_{i-1},b_i) → 类 i ──
    [Fact]
    public void ClassOf_upper_exclusive_bounds()
    {
        var breaks = new List<double> { 1, 3, 5 };          // 4 类: <1 / [1,3) / [3,5) / ≥5
        Assert.Equal(0, BlockModel.ClassOf(0.5, breaks));
        Assert.Equal(1, BlockModel.ClassOf(1.0, breaks));   // 1 不 <1 → 落 [1,3)
        Assert.Equal(1, BlockModel.ClassOf(2.9, breaks));
        Assert.Equal(2, BlockModel.ClassOf(3.0, breaks));
        Assert.Equal(3, BlockModel.ClassOf(5.0, breaks));   // 末类含上界之上
        Assert.Equal(3, BlockModel.ClassOf(99, breaks));
    }

    [Fact]
    public void Classed_cells_take_interval_color()
    {
        var blocks = new List<B>
        {
            new() { X = 0, Y = 0, Z = 0, Size = 2, Grade = 0.5 },   // 类0
            new() { X = 5, Y = 0, Z = 0, Size = 2, Grade = 2 },     // 类1
            new() { X = 10, Y = 0, Z = 0, Size = 2, Grade = 6 },    // 类3
        };
        var breaks = new List<double> { 1, 3, 5 };
        var colors = new List<(float, float, float)> { (0, 0, 0), (0.3f, 0, 0), (0.6f, 0, 0), (0.9f, 0, 0) };
        var cells = BlockModel.BuildCellsClassed(blocks, breaks, colors);
        var mesh = Assert.IsType<MeshEntity>(Assert.Single(cells));
        Assert.Equal(0.0f, CellColor(mesh, 0).r, 6);   // 类0
        Assert.Equal(0.3f, CellColor(mesh, 1).r, 6);   // 类1
        Assert.Equal(0.9f, CellColor(mesh, 2).r, 6);   // 类3(跳过类2, 因值6≥5)
    }

    /// <summary>一个未被剔除的块 = 6 面 × 4 顶点。</summary>
    private const int VertsPerCell = 24;

    /// <summary>第 i 个画出来的块的颜色(逐顶点色, 每块 24 个顶点同色)。</summary>
    private static (float r, float g, float b) CellColor(MeshEntity m, int i) => m.VertColors![i * VertsPerCell];
}
