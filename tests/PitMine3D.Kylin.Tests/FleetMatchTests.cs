using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>车铲匹配回归（Erlang-C 对标 + 匹配系数）。</summary>
public class FleetMatchTests
{
    [Fact]
    public void ErlangC_c1_reduces_to_rho()
    {
        // 单服务台 M/M/1：P(等待)=ρ
        Assert.Equal(0.5, FleetMatch.ErlangC(1, 0.5), 6);
        Assert.Equal(0.2, FleetMatch.ErlangC(1, 0.2), 6);
    }

    [Fact]
    public void ErlangC_c2_known_value()
    {
        // c=2, ρ=0.5 (a=1) → 1/3
        Assert.Equal(1.0 / 3.0, FleetMatch.ErlangC(2, 0.5), 6);
    }

    [Fact]
    public void ErlangC_saturated_is_one()
    {
        Assert.Equal(1.0, FleetMatch.ErlangC(3, 1.0), 6);
        Assert.Equal(1.0, FleetMatch.ErlangC(0, 0.5), 6);
    }

    [Fact]
    public void MatchFactor_formula_and_verdict()
    {
        Assert.Equal(1.0, FleetMatch.MatchFactor(4, 0.5, 2.0), 6);   // 4×0.5/2=1 均衡
        Assert.Equal("基本均衡", FleetMatch.Verdict(1.0));
        Assert.Equal("铲待车（配车偏少）", FleetMatch.Verdict(0.6));
        Assert.Equal("车排队（配车偏多）", FleetMatch.Verdict(1.4));
    }
}
