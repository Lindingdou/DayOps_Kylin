using PitMine3D.Kylin.Cad.Tasks;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>TaskLib 汇节点回归（忠实移植 SinkNode：库容/按量推进/接纳/登记簿）。</summary>
public class SinkNodeTests
{
    [Fact]
    public void Capacity_remaining_and_fill_rate()
    {
        var s = new SinkNode { DesignCapacityM3 = 100000, FilledM3 = 40000 };
        Assert.True(s.IsCapacityLimited);
        Assert.Equal(60000, s.RemainingM3, 3);
        Assert.Equal(0.4, s.FillRate, 6);
        Assert.Equal(35000, s.RemainingAfter(25000), 3);
    }

    [Fact]
    public void Unlimited_when_zero_capacity()
    {
        var s = new SinkNode { DesignCapacityM3 = 0 };   // 破碎站/煤仓通过型
        Assert.False(s.IsCapacityLimited);
        Assert.Equal(double.PositiveInfinity, s.RemainingM3);
        Assert.Equal(0, s.FillRate, 6);
    }

    [Fact]
    public void Advance_distance_from_dump_volume()
    {
        // d = V容 / (工作线长 × 台阶高) = 60000 / (300 × 20) = 10 m
        var s = new SinkNode { WorkLineLengthM = 300, BenchHeightM = 20 };
        Assert.Equal(10.0, s.AdvanceMetersFor(60000), 6);
        Assert.Equal(0, new SinkNode().AdvanceMetersFor(1000), 6);   // 无工作线长 → 0
    }

    [Fact]
    public void Accepts_by_material_allowed_sinks()
    {
        var coal = MaterialCatalog.Resolve(MaterialCatalog.Coal);   // 允许 破碎站/仓/堆场
        var rock = MaterialCatalog.Resolve(MaterialCatalog.Rock);   // 允许 内外排
        var crusher = new SinkNode { Kind = SinkKind.Crusher };
        var dump = new SinkNode { Kind = SinkKind.ExternalDump };
        Assert.True(crusher.Accepts(coal));
        Assert.False(crusher.Accepts(rock));     // 岩不进破碎站
        Assert.True(dump.Accepts(rock));
    }

    [Fact]
    public void Registry_put_find_addfilled_candidates()
    {
        var reg = new SinkRegistry();
        reg.Put(new SinkNode { Id = "D1", Kind = SinkKind.ExternalDump, DesignCapacityM3 = 100000 });
        reg.Put(new SinkNode { Id = "C1", Kind = SinkKind.Crusher });
        Assert.Equal("D1", reg.Find("D1")!.Id);
        Assert.Equal(0.3, reg.AddFilled("D1", 30000), 6);          // 填 30% 返回填充率
        var rock = MaterialCatalog.Resolve(MaterialCatalog.Rock);
        Assert.Contains(reg.CandidatesFor(rock), s => s.Id == "D1"); // 岩可去外排
    }
}
