using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;
using B = PitMine3D.Kylin.Cad.BlockModel.Block;

namespace PitMine3D.Kylin.Tests;

/// <summary>分标高(台阶)资源量回归（忠实原「整体+分台阶报量」）。核心不变量: 各带矿量之和 == 整体矿量(守恒)。</summary>
public class ResourceByElevationTests
{
    // 3 个标高层: z=0(两 ore 品位5 + 一 waste 品位1), z=10(一 ore 品位8), z=20(一 waste 品位0.5)
    private static List<B> Model() => new()
    {
        new B { X = 0, Y = 0, Z = 0, Size = 1, Grade = 5 },
        new B { X = 1, Y = 0, Z = 0, Size = 1, Grade = 5 },
        new B { X = 2, Y = 0, Z = 0, Size = 1, Grade = 1 },
        new B { X = 0, Y = 0, Z = 10, Size = 1, Grade = 8 },
        new B { X = 0, Y = 0, Z = 20, Size = 1, Grade = 0.5 },
    };

    const double Cut = 3, Density = 2.7;

    [Fact]
    public void Bench_ore_and_waste_sum_to_total()
    {
        var blocks = Model();
        var (ore, waste, _, _, metal, tonnage) = BlockModel.Resource(blocks, Cut, Density);
        var benches = BlockModel.ResourceByElevation(blocks, Cut, Density, benchHeight: 10);

        Assert.Equal(3, benches.Count);                                   // 3 台阶带各有块
        Assert.Equal(ore, benches.Sum(b => b.OreVol), 6);                 // 矿量守恒
        Assert.Equal(waste, benches.Sum(b => b.WasteVol), 6);             // 废石守恒
        Assert.Equal(metal, benches.Sum(b => b.Metal), 6);               // 金属守恒
        Assert.Equal(tonnage, benches.Sum(b => b.Tonnage), 6);           // 吨位守恒
    }

    [Fact]
    public void Bench_buckets_are_correct()
    {
        var benches = BlockModel.ResourceByElevation(Model(), Cut, Density, benchHeight: 10);
        // 底带 z=0: 2 ore(品位5, 体积2) + 1 waste(体积1); 平均品位 5
        Assert.Equal(2, benches[0].OreVol, 6);
        Assert.Equal(1, benches[0].WasteVol, 6);
        Assert.Equal(5, benches[0].AvgGrade, 6);
        // 中带 z=10: 1 ore(品位8), 0 waste
        Assert.Equal(1, benches[1].OreVol, 6);
        Assert.Equal(0, benches[1].WasteVol, 6);
        Assert.Equal(8, benches[1].AvgGrade, 6);
        // 顶带 z=20: 0 ore, 1 waste
        Assert.Equal(0, benches[2].OreVol, 6);
        Assert.Equal(1, benches[2].WasteVol, 6);
    }

    [Fact]
    public void Benches_ordered_low_to_high_and_contiguous()
    {
        var benches = BlockModel.ResourceByElevation(Model(), Cut, Density, benchHeight: 10);
        for (int i = 1; i < benches.Count; i++)
        {
            Assert.True(benches[i].ZLow >= benches[i - 1].ZLow);          // 低→高
            Assert.Equal(benches[i - 1].ZHigh, benches[i].ZLow, 6);       // 相连
        }
    }

    [Fact]
    public void Auto_bench_height_when_zero()
    {
        // benchHeight=0 → 自动分 ~10 带; 不崩, 守恒仍成立
        var blocks = Model();
        var (ore, _, _, _, _, _) = BlockModel.Resource(blocks, Cut, Density);
        var benches = BlockModel.ResourceByElevation(blocks, Cut, Density, benchHeight: 0);
        Assert.True(benches.Count >= 1);
        Assert.Equal(ore, benches.Sum(b => b.OreVol), 6);
    }

    [Fact]
    public void Csv_header_and_rows()
    {
        var benches = BlockModel.ResourceByElevation(Model(), Cut, Density, 10);
        var csv = BlockModel.ResourceByElevationCsv(benches);
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal("z_low,z_high,ore_vol,waste_vol,strip_ratio,avg_grade,metal,tonnage", lines[0]);
        Assert.Equal(benches.Count + 1, lines.Length);
    }

    [Fact]
    public void Empty_returns_empty()
    {
        Assert.Empty(BlockModel.ResourceByElevation(new List<B>(), Cut, Density, 10));
    }
}
