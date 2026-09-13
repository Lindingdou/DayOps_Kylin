using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 组合工作线（按最近端点串链 + 软连接段）与 连接台阶线（保图层/标高、只同图层、自动闭合）。
/// </summary>
public class WorkLineGroupTests
{
    [Fact]
    public void Chain_OrdersByNearestEndpoint_AndBridgesGaps()
    {
        // 三段乱序、其中一段反向：A(0,0→10,0)  C(22,0→30,0)  B(20,0←10,0)
        var m = new List<((double x, double y) a, (double x, double y) b)>
        {
            ((0, 0), (10, 0)),
            ((22, 0), (30, 0)),
            ((20, 0), (10, 0)),
        };
        var r = WorkLineGroup.Chain(m, 0.01);
        Assert.Equal(3, r.Order.Count);
        // 链从某一端起：A → B(反向) → C 或 C(反向) → B → A(反向)
        var idx = r.Order.Select(o => o.Index).ToArray();
        Assert.True(idx.SequenceEqual(new[] { 0, 2, 1 }) || idx.SequenceEqual(new[] { 1, 2, 0 }), string.Join(",", idx));
        Assert.Equal(1, r.HardSeams);           // A–B 端点重合
        Assert.Single(r.SoftLinks);            // B–C 缺口 2 m
        Assert.Equal(2.0, r.SoftLinks[0].LengthM, 6);
    }

    [Fact]
    public void Chain_SingleMember_NoLinks()
    {
        var r = WorkLineGroup.Chain(new List<((double x, double y), (double x, double y))> { ((0, 0), (5, 5)) }, 0.1);
        Assert.Single(r.Order);
        Assert.Empty(r.SoftLinks);
    }

    private static BenchLineJoin.Input In(int id, string layer, double z, bool closed, params (double x, double y)[] pts)
        => new() { Id = id, Layer = layer, Z = z, Closed = closed, Points = pts };

    [Fact]
    public void BenchJoin_SameLayerOnly_KeepsLayersApart_AndAutoCloses()
    {
        // 层 A：四段拼成一个方环；层 B：一段与 A 的端点重合但不同层 ⇒ 不并进去
        var lines = new List<BenchLineJoin.Input>
        {
            In(0, "台阶_坡脚线", 90, false, (0, 0), (10, 0)),
            In(1, "台阶_坡脚线", 90, false, (10, 0), (10, 10)),
            In(2, "台阶_坡脚线", 90, false, (10, 10), (0, 10)),
            In(3, "台阶_坡脚线", 90, false, (0, 10), (0, 0)),
            In(4, "排土场_坡脚线", 80, false, (0, 0), (-5, -5)),
            In(5, "台阶_坡脚线", 90, true, (50, 50), (60, 50), (60, 60)),   // 已闭合，不参与
        };
        var r = BenchLineJoin.Join(lines, 0.01, autoClose: true, sameLayerOnly: true);
        Assert.Single(r.Chains);
        var c = r.Chains[0];
        Assert.True(c.AutoClosed);
        Assert.Equal(4, c.MemberIds.Count);
        Assert.Equal(4, c.Points.Count);            // 闭合环去掉重复的收尾点
        Assert.Equal("台阶_坡脚线", c.Layer);
        Assert.Equal(90, c.Z);
        Assert.Equal(1, r.SkippedClosed);
        Assert.Equal(5, r.CandidateOpen);
        Assert.Equal(1, r.AutoClosedCount);

        // 跨图层连接：B 段也接进来 ⇒ 5 段一条链，首尾不再重合，不闭合
        var r2 = BenchLineJoin.Join(lines, 0.01, autoClose: true, sameLayerOnly: false);
        Assert.Single(r2.Chains);
        Assert.Equal(5, r2.Chains[0].MemberIds.Count);
        Assert.False(r2.Chains[0].AutoClosed);
    }

    [Fact]
    public void BenchJoin_NothingTouching_ReportsNothingJoined()
    {
        var lines = new List<BenchLineJoin.Input>
        {
            In(0, "台阶", 0, false, (0, 0), (1, 0)),
            In(1, "台阶", 0, false, (5, 0), (6, 0)),
        };
        var r = BenchLineJoin.Join(lines, 0.01, true, true);
        Assert.True(r.NothingJoined);
        Assert.Equal(2, r.CandidateOpen);
    }
}
