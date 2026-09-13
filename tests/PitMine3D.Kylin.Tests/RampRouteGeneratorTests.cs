using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Transport;
using PitMine3D.Kylin.Cad.RoadLayout;
using Xunit;

namespace PitMine3D.Kylin.Tests.RoadLayout;

/// <summary>选线器「每级多候选 + 沿帮中心线」纯逻辑测试(不碰几何核,确定性)。</summary>
public class RampRouteGeneratorTests
{
    /// <summary>
    /// 造 3 级方环台阶线:边长 200m 的正方形 4 顶点。
    /// 开口折线长 600m ≥ 需展线长(ΔH15 ÷ 8% = 187.5m) → 三级都判斜坡道;闭合周长 800m 供起坡点均分。
    /// </summary>
    private static RoadLayoutInput MakeBenchInput()
    {
        var benches = new List<BenchLine>();
        foreach (double lv in new[] { 100.0, 85.0, 70.0 })
            benches.Add(new BenchLine
            {
                Level = lv,
                Crest = new List<(double, double, double)>
                {
                    (0, 0, lv), (200, 0, lv), (200, 200, lv), (0, 200, lv)
                },
                BermWidth = 40,        // ≥ 回头直径(2×转弯半径 10),不致因平盘窄改判
                IsWorkingWall = false, // 固定坑线只布非工作帮
            });
        return new RoadLayoutInput
        {
            Sources = new List<LoadingPoint> { new() { Id = "S1", OreTons = 1000 } },
            Sinks = new List<UnloadingPoint> { new() { Id = "U1", Kind = UnloadKind.Crusher } },
            Constraints = new TransportConstraintSettings(),
            Benches = benches,
        };
    }

    [Fact]
    public void SameLevelPair_YieldsMultipleCandidates_WithDistinctPortals()
    {
        var cands = new RampRouteGenerator().Generate(MakeBenchInput());
        var groups = RampRouteGenerator.GroupByLevelPair(cands);

        // 3 级台阶 → 2 对相邻水平;每对按弧长均分出 CandidatesPerLevel 个候选。
        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Equal(RampRouteGenerator.CandidatesPerLevel, g.Count));

        foreach (var g in groups)
        {
            // 同一组的上/下标高一致(确实是"同一级的多候选",不是串到别级去了)。
            Assert.Single(g.Select(c => (c.FromLevel, c.ToLevel)).Distinct());
            // 起坡点沿环错开 → 平面位置两两不同,才谈得上"择路"。
            var portals = g.Select(c => (c.PortalStart.X, c.PortalStart.Y)).ToList();
            Assert.Equal(portals.Count, portals.Distinct().Count());
            // 展线长/形式相同(现口径:多候选只差位置),首条起坡点仍落在坡顶线首点,与首版一致。
            Assert.Single(g.Select(c => c.RequiredLengthM).Distinct());
            Assert.Equal(0.0, g[0].PortalStart.X, 6);
            Assert.Equal(0.0, g[0].PortalStart.Y, 6);
        }
    }

    [Fact]
    public void EveryCandidate_HasNonEmptyCenterline_DescendingFromUpperToLowerLevel()
    {
        var cands = new RampRouteGenerator().Generate(MakeBenchInput());
        Assert.NotEmpty(cands);

        foreach (var c in cands)
        {
            // 中心线不再是"只有起坡一点"的占位:至少两点才构得成线。
            Assert.True(c.Centerline.Count >= 2, $"中心线点数 {c.Centerline.Count} 过少");
            var head = c.Centerline[0];
            var tail = c.Centerline[c.Centerline.Count - 1];
            // 首点 = 起坡点(平面)、Z 起于上水平;末点 Z 落到下水平。
            Assert.Equal(c.PortalStart.X, head.X, 6);
            Assert.Equal(c.PortalStart.Y, head.Y, 6);
            Assert.Equal(c.FromLevel, head.Z, 6);
            Assert.Equal(c.ToLevel, tail.Z, 6);
            // Z 单调不升(按已走弧长线插,不得中途回弹)。
            for (int i = 1; i < c.Centerline.Count; i++)
                Assert.True(c.Centerline[i].Z <= c.Centerline[i - 1].Z + 1e-9, "中心线 Z 出现回弹");
        }
    }
}
