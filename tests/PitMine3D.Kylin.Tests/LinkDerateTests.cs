using System;
using PitMine3D.Kylin.Cad.Tasks;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>按环节降效回归（忠实 WeatherFactorFor：排土直降/同降退全盘/差降按两侧物理）。</summary>
public class LinkDerateTests
{
    [Fact]
    public void Dump_face_takes_dump_derate()
    {
        Assert.Equal(0.85, LinkDerate.Factor(ProcessType.Dump, 0, 0, 0, dLoadPct: 10, dHaulPct: 20, dDumpPct: 15), 6);
    }

    [Fact]
    public void Same_derate_returns_load_factor()
    {
        // 采=运=20% → 系数 = 1-0.2 = 0.8（与全盘老口径一致）
        Assert.Equal(0.8, LinkDerate.Factor(ProcessType.Load, 3, 10, 1.0, 20, 20, 5), 6);
    }

    [Fact]
    public void Balanced_face_haul_derate_bites()
    {
        // MF=1 均衡, 采10%运20%: tcD=3/0.9+7/0.8=12.083, capD=min(0.9,1·10/12.083)=0.8276
        double f = LinkDerate.Factor(ProcessType.Load, 3, 10, 1.0, 10, 20, 0);
        Assert.Equal(0.8276, f, 3);
        Assert.True(f < 0.9);   // 运输降效在均衡面生效
    }

    [Fact]
    public void Shovel_bottleneck_face_ignores_haul_derate()
    {
        // MF=1.5 铲瓶颈, 采0%运30%: 车队侧有富余 → 运输降效不减产能, 系数≈1
        double f = LinkDerate.Factor(ProcessType.Load, 3, 10, 1.5, 0, 30, 0);
        Assert.Equal(1.0, f, 3);
    }

    [Fact]
    public void No_cycle_breakdown_falls_back_to_load_factor()
    {
        // 无周期分解(takt/tc/mf=0) → 退采装侧, 不凭空劈
        Assert.Equal(0.9, LinkDerate.Factor(ProcessType.Load, 0, 0, 0, 10, 20, 0), 6);
    }
}
