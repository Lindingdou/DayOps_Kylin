using System;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>分标高体素体积回归（忠实原「整体+分标高体积」）。用盒子谓词与网格解耦。</summary>
public class VoxelBandsTests
{
    // 盒 [0,10]×[0,10]×[0,20] 的 isInside
    private static bool InBox(double x, double y, double z)
        => x >= 0 && x <= 10 && y >= 0 && y <= 10 && z >= 0 && z <= 20;

    [Fact]
    public void Uniform_column_splits_into_equal_bands()
    {
        // 带高 5 → 4 带(0-5,5-10,10-15,15-20)；柱体 z 向均匀 → 各带体积应相等
        var bands = VoxelBands.ByElevation(0, 10, 0, 10, 0, 20, cell: 1.0, bandHeight: 5.0, InBox);
        Assert.Equal(4, bands.Count);
        double first = bands[0].Volume;
        Assert.True(first > 0);
        foreach (var b in bands)
            Assert.InRange(b.Volume, first * 0.9, first * 1.1);   // 各带互差 <10%(体素离散容差)
        // 带边界递增且相连
        for (int i = 1; i < bands.Count; i++)
            Assert.Equal(bands[i - 1].ZHigh, bands[i].ZLow, 6);
    }

    [Fact]
    public void Bands_sum_to_total_volume()
    {
        var bands = VoxelBands.ByElevation(0, 10, 0, 10, 0, 20, cell: 1.0, bandHeight: 5.0, InBox);
        double sum = bands.Sum(b => b.Volume);
        // 盒真体积 10×10×20=2000；cell=1 体素化应贴近(容差 15%)
        Assert.InRange(sum, 2000 * 0.85, 2000 * 1.15);
    }

    [Fact]
    public void Csv_has_header_and_row_per_band()
    {
        var bands = VoxelBands.ByElevation(0, 10, 0, 10, 0, 20, 1.0, 5.0, InBox);
        var csv = VoxelBands.ToCsv(bands);
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal("z_low,z_high,volume,cells", lines[0]);
        Assert.Equal(bands.Count + 1, lines.Length);   // 表头 + 每带一行
    }

    [Fact]
    public void Degenerate_inputs_return_empty()
    {
        Assert.Empty(VoxelBands.ByElevation(0, 10, 0, 10, 0, 20, cell: 0, bandHeight: 5, InBox));   // cell=0
        Assert.Empty(VoxelBands.ByElevation(0, 10, 0, 10, 5, 5, cell: 1, bandHeight: 5, InBox));    // 无高差
        Assert.Empty(VoxelBands.ByElevation(0, 10, 0, 10, 0, 20, cell: 1, bandHeight: 5, null!));   // 无谓词
    }

    [Fact]
    public void Half_filled_upper_bands_have_less_volume()
    {
        // 只在下半(z<10)填充 → 上带体积应显著小于下带
        Func<double, double, double, bool> lowerOnly = (x, y, z) => InBox(x, y, z) && z < 10;
        var bands = VoxelBands.ByElevation(0, 10, 0, 10, 0, 20, cell: 1.0, bandHeight: 10.0, lowerOnly);
        Assert.Equal(2, bands.Count);
        Assert.True(bands[0].Volume > bands[1].Volume * 5, "下带体积应远大于上带");
    }
}
