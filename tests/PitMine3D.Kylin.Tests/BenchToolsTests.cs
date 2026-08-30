using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>分帮扩帮（批量偏移）回归。</summary>
public class BenchToolsTests
{
    [Fact]
    public void Line_batch_offset_makes_parallel_family()
    {
        var line = new LineEntity { X0 = 0, Y0 = 0, X1 = 10, Y1 = 0 };
        var fam = BenchTools.BatchOffset(line, 5, 3, 3);   // 上方 3, 3 条 → y=3,6,9
        Assert.Equal(3, fam.Count);
        Assert.Equal(3, ((LineEntity)fam[0]).Y0, 4);
        Assert.Equal(6, ((LineEntity)fam[1]).Y0, 4);
        Assert.Equal(9, ((LineEntity)fam[2]).Y0, 4);
    }

    [Fact]
    public void Unsupported_entity_returns_empty()
    {
        var pt = new PointEntity { X = 0, Y = 0 };
        Assert.Empty(BenchTools.BatchOffset(pt, 1, 1, 3));
    }
}
