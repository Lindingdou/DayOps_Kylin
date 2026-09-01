using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>设备效能 What-if 提升路径模拟（忠实原 EquipmentForecastWindow 四杠杆模型）回归。</summary>
public class EfficiencyWhatIfTests
{
    [Fact]
    public void Simulate_four_levers_with_faultshare_haul_and_synergy_decay()
    {
        // base=100万m³, faultShare=0.2; l1=0.5(故障降50%) l2=0.1 l3=0.1 l4=0.2(运距)
        // c1=0.5*0.2=0.10 · c2=0.10 · c3=0.10 · c4=0.2*0.6=0.12 → raw=0.42
        // actualGain=0.42*0.85=0.357 → sim=100*1.357=135.7 · delta=35.7
        var r = EfficiencyWhatIf.Simulate(100, 0.2, 0.5, 0.1, 0.1, 0.2);
        Assert.Equal(0.10, r.C1, 9);
        Assert.Equal(0.10, r.C2, 9);
        Assert.Equal(0.10, r.C3, 9);
        Assert.Equal(0.12, r.C4, 9);
        Assert.Equal(0.42, r.RawGain, 9);
        Assert.Equal(0.357, r.ActualGain, 9);
        Assert.Equal(135.7, r.Simulated, 6);
        Assert.Equal(35.7, r.Delta, 6);
    }

    [Fact]
    public void Simulate_zero_levers_keeps_baseline()
    {
        var r = EfficiencyWhatIf.Simulate(80, 0.3, 0, 0, 0, 0);
        Assert.Equal(0, r.ActualGain, 9);
        Assert.Equal(80, r.Simulated, 9);
        Assert.Equal(0, r.Delta, 9);
    }

    [Fact]
    public void Simulate_faultshare_clamped_to_unit_range()
    {
        // faultShare 传 2 → 夹到 1; l1=0.5 → c1=0.5*1=0.5
        var r = EfficiencyWhatIf.Simulate(100, 2.0, 0.5, 0, 0, 0);
        Assert.Equal(0.5, r.C1, 9);
    }
}
