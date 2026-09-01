using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>PMB 导出（写→读往返）回归 —— 读写对 PmbExportService/PmbImportService 互逆。</summary>
public class PmbExportServiceTests
{
    [Fact]
    public void ToBytes_roundtrips_grid_and_attrs_through_importer()
    {
        // 2×2×2 网格, origin(10,20,30), 尺寸 5; grade 与 dens 两属性(x-fastest 值)
        var g = new PmbExportService.Grid(10, 20, 30, 5, 5, 5, 2, 2, 2);
        // idx = k*4 + j*2 + i
        var grade = new double[] { 0, 1, 2, 3, 4, 5, 6, 7 };
        var dens = new double[] { 10, 11, 12, 13, 14, 15, 16, 17 };
        var bytes = PmbExportService.ToBytes(g, new List<(string, double[])> { ("grade", grade), ("dens", dens) }, "m1");

        var r = PmbImportService.Parse(bytes);
        Assert.True(r.Success, r.Error);
        Assert.Equal(2, r.Nx); Assert.Equal(2, r.Ny); Assert.Equal(2, r.Nz);
        Assert.Equal(8, r.Blocks.Count);
        Assert.Contains("grade", r.AttrNames); Assert.Contains("dens", r.AttrNames);
        // 首属性 grade 作品位; cell 中心 = origin + (i+0.5)*size
        Assert.Equal(12.5, r.Blocks[0].X, 6);   // 10 + 0.5*5
        Assert.Equal(22.5, r.Blocks[0].Y, 6);
        Assert.Equal(32.5, r.Blocks[0].Z, 6);
        Assert.Equal(0.0, r.Blocks[0].Grade, 6);
        Assert.Equal(7.0, r.Blocks[7].Grade, 6);   // idx7 = 末块
        // 全属性数组精确还原
        Assert.Equal(grade, r.AllAttrs["grade"]);
        Assert.Equal(dens, r.AllAttrs["dens"]);
    }

    [Fact]
    public void FromBlocks_reconstructs_grid_independent_of_input_order()
    {
        // 乱序块体(2×1×2 网格, 尺寸 4, origin 角(0,0,0)→中心(2,2,2)…)
        var blocks = new List<BlockModel.Block>
        {
            new() { X = 6, Y = 2, Z = 6, Size = 4, Grade = 3 },   // i1 j0 k1
            new() { X = 2, Y = 2, Z = 2, Size = 4, Grade = 0 },   // i0 j0 k0
            new() { X = 6, Y = 2, Z = 2, Size = 4, Grade = 1 },   // i1 j0 k0
            new() { X = 2, Y = 2, Z = 6, Size = 4, Grade = 2 },   // i0 j0 k1
        };
        var (g, attrs) = PmbExportService.FromBlocks(blocks);
        Assert.Equal(2, g.Nx); Assert.Equal(1, g.Ny); Assert.Equal(2, g.Nz);
        Assert.Equal(0.0, g.Ox, 6); Assert.Equal(0.0, g.Oz, 6);   // 2 - 4/2
        var grade = attrs.Single(a => a.name == "grade").values;
        // x-fastest: idx0=(0,0,0)=0, idx1=(1,0,0)=1, idx2=(0,0,1)=2, idx3=(1,0,1)=3
        Assert.Equal(new double[] { 0, 1, 2, 3 }, grade);

        // 往返: FromBlocks → ToBytes → Parse 还原品位序
        var bytes = PmbExportService.ToBytes(g, attrs, "m");
        var r = PmbImportService.Parse(bytes);
        Assert.True(r.Success, r.Error);
        Assert.Equal(4, r.Blocks.Count);
        Assert.Equal(new double[] { 0, 1, 2, 3 }, r.Blocks.Select(b => b.Grade).ToArray());
    }

    [Fact]
    public void ToBytes_geometry_only_when_no_attrs_still_parses()
    {
        var g = new PmbExportService.Grid(0, 0, 0, 1, 1, 1, 3, 1, 1);
        var bytes = PmbExportService.ToBytes(g, null, "geo");
        var r = PmbImportService.Parse(bytes);
        Assert.True(r.Success, r.Error);
        Assert.Equal(3, r.Blocks.Count);
        Assert.All(r.Blocks, b => Assert.Equal(0.0, b.Grade, 6));   // 无属性 → 品位 0
    }

    [Fact]
    public void ToBytes_rejects_attr_length_mismatch()
    {
        var g = new PmbExportService.Grid(0, 0, 0, 1, 1, 1, 2, 2, 2);   // 8 块
        Assert.Throws<ArgumentException>(() =>
            PmbExportService.ToBytes(g, new List<(string, double[])> { ("bad", new double[] { 1, 2, 3 }) }));
    }
}
