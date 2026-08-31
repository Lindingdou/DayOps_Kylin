using System.Collections.Generic;
using PitMine3D.Kylin.Cad.Tasks;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>TaskLib 物料流/采剥平衡回归（忠实移植 MaterialFlow：剥采比/内排率/运输功/加权运距）。</summary>
public class MaterialFlowTests
{
    private static PeriodBalance Sample()
    {
        return new PeriodBalance
        {
            Period = "2026-06",
            Flows =
            {
                new MaterialFlow { MaterialCode = MaterialCatalog.Coal, InSituM3 = 10000, SinkKind = SinkKind.Crusher, HaulKm = 3 },
                new MaterialFlow { MaterialCode = MaterialCatalog.Rock, InSituM3 = 30000, SinkKind = SinkKind.ExternalDump, HaulKm = 2 },
                new MaterialFlow { MaterialCode = MaterialCatalog.Rock, InSituM3 = 20000, SinkKind = SinkKind.InternalDump, HaulKm = 1 },
            }
        };
    }

    [Fact]
    public void Flow_derived_tonnage_and_work()
    {
        var f = new MaterialFlow { MaterialCode = MaterialCatalog.Coal, InSituM3 = 10000, HaulKm = 3 };
        Assert.Equal(13500, f.TonnageT, 3);        // 10000 × 1.35
        Assert.True(f.IsOre);
        Assert.Equal(13500 * 3, f.TransportWorkTKm, 3);
    }

    [Fact]
    public void Strip_ratio_ore_and_waste()
    {
        var b = Sample();
        Assert.Equal(1.35, b.OreWanT, 4);          // 煤 13500 t = 1.35 万t
        Assert.Equal(5.0, b.StripWanM3, 4);        // 岩 (30000+20000) = 5.0 万m³
        Assert.Equal(5.0 / 1.35, b.StripRatio, 3); // 剥采比 ≈ 3.70
    }

    [Fact]
    public void Dumped_and_internal_dump_rate()
    {
        var b = Sample();
        // 排弃占容 = 岩 (30000+20000)×1.15 = 57500 → 5.75 万m³
        Assert.Equal(5.75, b.DumpedWanM3, 4);
        // 内排率 = 内排(20000×1.15=23000) / 全排弃(57500) = 40%
        Assert.Equal(40.0, b.InternalDumpPct, 3);
    }

    [Fact]
    public void Transport_work_and_weighted_haul()
    {
        var b = Sample();
        // 运输功 = 13500×3 + 75000×2 + 50000×1 = 240500 t·km → 24.05 万t·km
        Assert.Equal(24.05, b.TransportWorkWanTKm, 4);
        // 加权运距 = 240500 / (13500+75000+50000) ≈ 1.736 km
        Assert.Equal(240500.0 / 138500.0, b.WeightedAvgHaulKm, 4);
    }

    [Fact]
    public void Empty_balance_zero()
    {
        var b = new PeriodBalance();
        Assert.Equal(0, b.StripRatio, 6);
        Assert.Equal(0, b.InternalDumpPct, 6);
        Assert.Equal(0, b.WeightedAvgHaulKm, 6);
    }
}
