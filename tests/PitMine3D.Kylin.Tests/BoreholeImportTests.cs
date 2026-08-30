using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>钻孔 CSV 解析回归（GeoDataBase 托管切片）。</summary>
public class BoreholeImportTests
{
    [Fact]
    public void Groups_intervals_by_hole()
    {
        const string csv =
            "孔号,X,Y,高程,自,至,岩性\n" +   // 表头(X列非数值)跳过
            "ZK01,100,200,50,0,5,粘土\n" +
            "ZK01,100,200,50,5,12,砂岩\n" +
            "ZK02,300,400,48,0,8,煤\n";
        var r = BoreholeImportService.Parse(csv);
        Assert.True(r.Success, r.Error);
        Assert.Equal(2, r.Boreholes.Count);
        var zk1 = r.Boreholes[0];
        Assert.Equal("ZK01", zk1.Name);
        Assert.Equal(100, zk1.X, 4); Assert.Equal(50, zk1.Z, 4);
        Assert.Equal(2, zk1.Intervals.Count);
        Assert.Equal(12, zk1.TotalDepth, 4);         // 最大 至深
        Assert.Equal("砂岩", zk1.Intervals[1].Rock);
    }

    [Fact]
    public void Collar_only_without_intervals()
    {
        const string csv = "ZK9,10,20,5\n";          // 只孔口, 无分层
        var r = BoreholeImportService.Parse(csv);
        Assert.True(r.Success, r.Error);
        Assert.Single(r.Boreholes);
        Assert.Empty(r.Boreholes[0].Intervals);
        Assert.Equal(0, r.Boreholes[0].TotalDepth, 4);
    }

    [Fact]
    public void Bounds_cover_collars()
    {
        const string csv = "A,0,0,1,0,3,x\nB,10,20,1,0,3,y\n";
        var r = BoreholeImportService.Parse(csv);
        Assert.Equal(0, r.Bounds[0], 4); Assert.Equal(0, r.Bounds[1], 4);
        Assert.Equal(10, r.Bounds[2], 4); Assert.Equal(20, r.Bounds[3], 4);
    }

    [Fact]
    public void No_valid_rows_fails()
    {
        var r = BoreholeImportService.Parse("孔号,X,Y\n说明行,甲,乙\n");
        Assert.False(r.Success);
        Assert.NotNull(r.Error);
    }
}
