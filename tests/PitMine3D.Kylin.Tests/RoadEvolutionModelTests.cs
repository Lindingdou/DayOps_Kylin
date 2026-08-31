using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 演化对比数据模型派生逻辑回归 —— Pt3 距离、SegFrac 除零守卫、ClassText 分支映射、
/// 里程账残差/配平、计数(Keep/Shift 仅本期侧)。补 RoadEvolutionModel 直接测试覆盖。
/// </summary>
public class RoadEvolutionModelTests
{
    [Fact]
    public void Pt3_distance_3d_vs_horizontal()
    {
        var a = new Pt3(0, 0, 0); var b = new Pt3(3, 4, 12);
        Assert.Equal(13, a.DistanceTo(b), 6);           // √(9+16+144)=13
        Assert.Equal(5, a.HorizontalDistanceTo(b), 6);  // √(9+16)=5，忽略 Z
    }

    [Fact]
    public void SegFrac_guards_zero_edge_length()
    {
        Assert.Equal(0.5, new RouteEvolution { SegLenM = 5, EdgeLenM = 10 }.SegFrac, 6);
        Assert.Equal(0, new RouteEvolution { SegLenM = 5, EdgeLenM = 0 }.SegFrac, 6);   // 除零守卫
    }

    [Fact]
    public void ClassText_branches_on_flags()
    {
        Assert.Equal("新建入网", new RouteEvolution { Class = RoadEvolutionClass.Extend, IsNewRoad = true }.ClassText);
        Assert.Equal("延拓", new RouteEvolution { Class = RoadEvolutionClass.Extend, IsNewRoad = false, AtEdgeEnd = true }.ClassText);
        Assert.Equal("改线(新)", new RouteEvolution { Class = RoadEvolutionClass.Extend, IsNewRoad = false, AtEdgeEnd = false }.ClassText);
        Assert.Equal("截短", new RouteEvolution { Class = RoadEvolutionClass.Shorten, AtEdgeEnd = true }.ClassText);
        Assert.Equal("改线(旧)", new RouteEvolution { Class = RoadEvolutionClass.Shorten, AtEdgeEnd = false }.ClassText);
        Assert.Equal("已废除", new RouteEvolution { Class = RoadEvolutionClass.Abolish }.ClassText);
        Assert.Equal("移位", new RouteEvolution { Class = RoadEvolutionClass.Shift }.ClassText);
        Assert.Equal("保持", new RouteEvolution { Class = RoadEvolutionClass.Keep }.ClassText);
    }

    [Fact]
    public void Ledger_derived_values_and_balance()
    {
        var l = new EvolutionLedger { PrevTotalM = 1000, CurrTotalM = 1200, NewM = 300, GoneM = 100, SharedCurrM = 900, SharedPrevM = 900 };
        Assert.Equal(200, l.NetM, 6);                   // 1200-1000
        Assert.Equal(0, l.SharedDriftM, 6);             // 900-900
        Assert.Equal(0, l.CurrResidualM, 6);            // 1200-(900+300)
        Assert.Equal(0, l.PrevResidualM, 6);            // 1000-(900+100)
        Assert.True(l.IsBalanced);
        l.NewM = 250;                                   // 破坏配平
        Assert.Equal(50, l.CurrResidualM, 6);           // 1200-(900+250)
        Assert.False(l.IsBalanced);
    }

    [Fact]
    public void Counts_keep_shift_only_current_side()
    {
        var res = new RoadEvolutionResult();
        res.Routes.Add(new RouteEvolution { Class = RoadEvolutionClass.Keep, Side = EvolutionSide.Curr });
        res.Routes.Add(new RouteEvolution { Class = RoadEvolutionClass.Keep, Side = EvolutionSide.Prev });   // 上期侧不计
        res.Routes.Add(new RouteEvolution { Class = RoadEvolutionClass.Abolish, Side = EvolutionSide.Prev }); // 废除本在上期
        res.Routes.Add(new RouteEvolution { Class = RoadEvolutionClass.Extend, Side = EvolutionSide.Curr, IsNewRoad = true });
        res.Routes.Add(new RouteEvolution { Class = RoadEvolutionClass.Extend, Side = EvolutionSide.Curr });
        Assert.Equal(1, res.KeepCount);                 // Keep 仅本期侧计
        Assert.Equal(1, res.AbolishCount);              // Abolish 不限侧
        Assert.Equal(2, res.ExtendCount);
        Assert.Equal(1, res.NewRoadCount);
    }

    [Fact]
    public void LenOf_sums_class_segment_lengths()
    {
        var res = new RoadEvolutionResult();
        res.Routes.Add(new RouteEvolution { Class = RoadEvolutionClass.Extend, Side = EvolutionSide.Curr, SegLenM = 30 });
        res.Routes.Add(new RouteEvolution { Class = RoadEvolutionClass.Extend, Side = EvolutionSide.Curr, SegLenM = 20 });
        Assert.Equal(50, res.LenOf(RoadEvolutionClass.Extend), 6);
    }
}
