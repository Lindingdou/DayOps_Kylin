// 忠实移植自原 PitMine3D Tests/Tests.RoadLib/CenterlineInventoryTests.cs（仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PitMine3D.Kylin.Tests.Road;

/// <summary>
/// 「中心线管理」清单量的口径。清单是用来<b>决定删哪几条</b>的，判错就是删错，
/// 所以每条判据都配一个对照组（放开那一条闸门必须真的翻过来），免得判据空过：
/// 悬空端点、连通片、立交不并、最大纵坡各一组。
/// </summary>
public class CenterlineInventoryTests
{
    private static double[] L(params double[] xyz) => xyz;

    private static CenterlineRow RowOf(CenterlineInventoryResult r, int index)
        => r.Rows.First(x => x.Index == index);

    // ── 逐条自身量 ─────────────────────────────────────────────────────────

    [Fact]
    public void BasicMetrics_LengthVertexZAndGrade()
    {
        // 水平 100m、抬升 10m → 纵坡 10%；再水平 100m 平段 → 0%
        var r = CenterlineInventory.Build(new List<double[]>
        {
            L(0, 0, 100,   100, 0, 110,   200, 0, 110),
        });

        var row = RowOf(r, 0);
        Assert.Equal(3, row.VertexCount);
        Assert.Equal(100.0, row.MinZ, 6);
        Assert.Equal(110.0, row.MaxZ, 6);
        Assert.Equal(10.0, row.MaxGradePct, 6);          // 取逐段最大，不是端到端平均
        Assert.Equal(Math.Sqrt(100 * 100 + 10 * 10) + 100, row.LengthM, 6);
        Assert.Equal(0.0, row.StartX, 6);
        Assert.Equal(200.0, row.EndX, 6);
    }

    [Fact]
    public void VerticalSegment_IsFlaggedNotDividedByZero()
    {
        // 水平投影为 0 的段：真实数据里就是画废了的线，必须能一眼挑出来而不是 NaN/∞
        var r = CenterlineInventory.Build(new List<double[]> { L(0, 0, 100, 0, 0, 130) });
        Assert.Equal(999.0, RowOf(r, 0).MaxGradePct, 6);
    }

    [Fact]
    public void DegenerateLines_AreDroppedFromRows_ButIndexesStillPointAtTheInput()
    {
        // 第 0 条退化（只有一个顶点）→ 不进清单；第 1 条的 Index 必须仍是 1，
        // 否则窗口按 Index 回指原表就会删错线。
        var r = CenterlineInventory.Build(new List<double[]>
        {
            L(1, 2, 3),
            L(0, 0, 10, 100, 0, 10),
        });

        Assert.Single(r.Rows);
        Assert.Equal(1, r.Rows[0].Index);
    }

    // ── 悬空端点：贴上了 vs 没贴上，各一组 ──────────────────────────────────

    [Fact]
    public void IsolatedLine_HasTwoLooseEnds()
    {
        var r = CenterlineInventory.Build(new List<double[]> { L(0, 0, 10, 100, 0, 10) });
        Assert.Equal(2, RowOf(r, 0).LooseEnds);
        Assert.Equal(2, r.LooseEndCount);
    }

    [Fact]
    public void TeeContact_ClearsTheTouchingEnd_AndJoinsOneComponent()
    {
        // 支线端点落在主线中段（T 形，建网会在这儿打断成节点）→ 那一端不算悬空，两条同片
        var main = L(0, 0, 10, 200, 0, 10);
        var spur = L(100, 0, 10, 100, 80, 10);
        var r = CenterlineInventory.Build(new List<double[]> { main, spur });

        Assert.Equal(1, RowOf(r, 1).LooseEnds);      // 只剩远离主线那一端悬空
        Assert.Equal(1, r.ComponentCount);
        Assert.Equal(2, RowOf(r, 0).ComponentLineCount);
    }

    [Fact]
    public void GapBeyondSnapTol_StaysTwoComponents_AndTighteningTolIsWhatDecides()
    {
        var a = L(0, 0, 10, 100, 0, 10);
        var b = L(120, 0, 10, 220, 0, 10);            // 缺口 20m > 默认 5m 容差

        var loose = CenterlineInventory.Build(new List<double[]> { a, b });
        Assert.Equal(2, loose.ComponentCount);

        // 对照组：把容差放到 25m，同一份输入就该并成一片 —— 证明上面的 2 是容差挡的
        var tight = CenterlineInventory.Build(new List<double[]> { a, b }, snapTolM: 25.0);
        Assert.Equal(1, tight.ComponentCount);
    }

    // ── 立交：XY 贴上但差着标高，不许并片 ──────────────────────────────────

    [Fact]
    public void GradeSeparated_DoesNotConnect_ButSameLevelDoes()
    {
        var lower = L(0, 0, 10, 200, 0, 10);
        var upperCross = L(100, -50, 30, 100, 50, 30);        // 正交跨过，高差 20m
        var overpass = CenterlineInventory.Build(new List<double[]> { lower, upperCross });
        Assert.Equal(2, overpass.ComponentCount);

        // 对照组：同一处但同标高 → X 形平交，必须并成一片
        var levelCross = L(100, -50, 10, 100, 50, 10);
        var atGrade = CenterlineInventory.Build(new List<double[]> { lower, levelCross });
        Assert.Equal(1, atGrade.ComponentCount);
    }

    // ── 连通片编号：0 号必须是总长最大的那片 ────────────────────────────────

    [Fact]
    public void ComponentZero_IsTheLongestOne()
    {
        var trunkA = L(0, 0, 10, 500, 0, 10);
        var trunkB = L(500, 0, 10, 900, 0, 10);               // 与 A 端点相接 → 同片，共 900m
        var scrap = L(0, 900, 10, 12, 900, 10);               // 孤立碎线 12m

        var r = CenterlineInventory.Build(new List<double[]> { scrap, trunkA, trunkB });

        Assert.Equal(2, r.ComponentCount);
        Assert.Equal(0, RowOf(r, 1).ComponentId);            // 主干
        Assert.Equal(0, RowOf(r, 2).ComponentId);
        Assert.Equal(1, RowOf(r, 0).ComponentId);            // 碎线排后面
        Assert.Equal(1, RowOf(r, 0).ComponentLineCount);     // 孤立线：本片只有它自己
    }

    [Fact]
    public void VerticalLine_IsNotAClosedLoop_BothEndsStillLoose()
    {
        // 首尾同 XY、差着 32m 标高：只按 XY 判闭环会把它当成"没有端点"，
        // 于是这条画废了的线一处悬空都不报 —— 而它恰恰是最该被挑出来删的那种。
        // （2026-08-11 离线出图时抓到的：清单里它的「悬空」一栏是空的。）
        var r = CenterlineInventory.Build(new List<double[]> { L(622800, 4380900, 1050, 622800, 4380900, 1082) });

        Assert.False(RowOf(r, 0).IsClosed);
        Assert.Equal(2, RowOf(r, 0).LooseEnds);
    }

    [Fact]
    public void ClosedLoop_HasNoEndpoints_SoNoLooseEnds()
    {
        var ring = L(0, 0, 10, 100, 0, 10, 100, 100, 10, 0, 100, 10, 0, 0, 10);
        var r = CenterlineInventory.Build(new List<double[]> { ring });

        Assert.True(RowOf(r, 0).IsClosed);
        Assert.Equal(0, RowOf(r, 0).LooseEnds);
    }

    [Fact]
    public void TotalLength_IsTheSumOfRows()
    {
        var lines = new List<double[]>
        {
            L(0, 0, 10, 100, 0, 10),
            L(0, 50, 10, 100, 50, 10),
        };
        var r = CenterlineInventory.Build(lines);
        Assert.Equal(200.0, r.TotalLengthM, 6);
        Assert.Equal(r.Rows.Sum(x => x.LengthM), r.TotalLengthM, 6);
    }

    [Fact]
    public void Empty_ReturnsEmptyResult_NotNull()
    {
        var r = CenterlineInventory.Build(null);
        Assert.Empty(r.Rows);
        Assert.Equal(0, r.ComponentCount);
    }
}
