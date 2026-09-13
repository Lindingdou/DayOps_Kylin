// 忠实移植自原 PitMine3D Tests/Tests.RoadLib/CenterlineLayerDiffTests.cs（仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace PitMine3D.Kylin.Tests.Road;

/// <summary>
/// 中心线图层增量落地的口径。这层判据只有一条主线：<b>没变的线一根都不许动</b>
/// （动了就是整层删了重画，也就是"补一条线要等半天"的那个原因），
/// 同时 <b>变了的一条都不许漏</b>（漏写=图上留着旧几何，路网跟中线对不上）。
/// </summary>
public class CenterlineLayerDiffTests
{
    private static double[] L(params double[] xyz) => xyz;

    private static readonly double[] A = { 0, 0, 10, 100, 0, 12, 200, 0, 14 };
    private static readonly double[] B = { 0, 50, 10, 100, 50, 11 };
    private static readonly double[] C = { 0, 90, 10, 100, 90, 11 };

    private static List<(ulong Handle, double[] Xyz)> Before()
        => new() { (1UL, A), (2UL, B), (3UL, C) };

    [Fact]
    public void Unchanged_IsNoop_NothingDeletedNothingWritten()
    {
        var d = CenterlineLayerDiff.Compute(Before(), new List<double[]> { A, B, C });

        Assert.True(d.IsNoop);
        Assert.Empty(d.DeleteHandles);
        Assert.Empty(d.WriteLines);
        Assert.Equal(new ulong[] { 1, 2, 3 }, d.KeepHandles.OrderBy(h => h));
    }

    [Fact]
    public void OrderDoesNotMatter()
    {
        // 连通增强吐出来的顺序与图层里的顺序无关，不能因为换了个位置就判成"变了"
        var d = CenterlineLayerDiff.Compute(Before(), new List<double[]> { C, A, B });
        Assert.True(d.IsNoop);
    }

    [Fact]
    public void OneLineMoved_OnlyThatOneIsRewritten()
    {
        var movedB = L(0, 50, 10, 100, 50, 11.5);            // 端点被焊接挪了 0.5m
        var d = CenterlineLayerDiff.Compute(Before(), new List<double[]> { A, movedB, C });

        Assert.Equal(new ulong[] { 2 }, d.DeleteHandles);     // 只删被改的那条
        Assert.Single(d.WriteLines);
        Assert.Same(movedB, d.WriteLines[0]);
        Assert.Equal(new ulong[] { 1, 3 }, d.KeepHandles.OrderBy(h => h));
    }

    [Fact]
    public void NewLineAdded_ExistingOnesUntouched()
    {
        var drawn = L(0, 0, 10, 0, 50, 10);
        var d = CenterlineLayerDiff.Compute(Before(), new List<double[]> { A, B, C, drawn });

        Assert.Empty(d.DeleteHandles);                        // 画一条新线不该动到旧的三条
        Assert.Single(d.WriteLines);
        Assert.Same(drawn, d.WriteLines[0]);
        Assert.Equal(3, d.KeepHandles.Count);
        Assert.False(d.IsNoop);
    }

    [Fact]
    public void TeeSplit_OldLineReplacedByTwoHalves()
    {
        // 被 T 形打断：旧的 A 变成两截 → A 删掉，两截各写一条，B/C 不动
        var half1 = L(0, 0, 10, 100, 0, 12);
        var half2 = L(100, 0, 12, 200, 0, 14);
        var d = CenterlineLayerDiff.Compute(Before(), new List<double[]> { half1, half2, B, C });

        Assert.Equal(new ulong[] { 1 }, d.DeleteHandles);
        Assert.Equal(2, d.WriteLines.Count);
        Assert.Equal(new ulong[] { 2, 3 }, d.KeepHandles.OrderBy(h => h));
    }

    [Fact]
    public void DuplicateGeometry_EachOldLineClaimedAtMostOnce()
    {
        // 图上有两条一模一样的线（重复导入很常见），结果里只剩一条 → 必须恰好删掉一条
        var before = new List<(ulong, double[])> { (1UL, B), (2UL, (double[])B.Clone()) };
        var d = CenterlineLayerDiff.Compute(before, new List<double[]> { B });

        Assert.Single(d.KeepHandles);
        Assert.Single(d.DeleteHandles);
        Assert.Empty(d.WriteLines);
    }

    [Fact]
    public void EmptyAfter_ReportsFullDelete_ButDoesNotDecideForCaller()
    {
        // 退化输入把整层中线赔进去这件事由调用方挡（MergeManualRoutes 里那句 cr.Lines.Count == 0 守卫）；
        // 本类只如实报"全删"，不自作主张改成 noop —— 那样"载入空存档"就静默什么都不做了。
        var d = CenterlineLayerDiff.Compute(Before(), new List<double[]>());

        Assert.Equal(3, d.DeleteHandles.Count);
        Assert.Empty(d.WriteLines);
        Assert.Empty(d.KeepHandles);
    }

    [Fact]
    public void EmptyBefore_EverythingIsWritten()
    {
        var d = CenterlineLayerDiff.Compute(new List<(ulong, double[])>(), new List<double[]> { A, B });
        Assert.Equal(2, d.WriteLines.Count);
        Assert.Empty(d.DeleteHandles);
        Assert.Empty(d.KeepHandles);
    }

    [Fact]
    public void DegenerateOldEntry_IsDropped_NotMatched()
    {
        // 顶点 <2 的旧条目留在图上没有意义（也进不了 network），判删
        var before = new List<(ulong, double[])> { (1UL, A), (9UL, new double[] { 1, 2, 3 }) };
        var d = CenterlineLayerDiff.Compute(before, new List<double[]> { A });

        Assert.Equal(new ulong[] { 1 }, d.KeepHandles);
        Assert.Equal(new ulong[] { 9 }, d.DeleteHandles);
    }

    [Fact]
    public void SubMillimetreChange_CountsAsChanged()
    {
        // 判的是逐 bit 全等而不是容差：铺贴重采出来的 Z 差 1e-9 也是不同的几何，
        // 宁可多写一条，也不能让图上留着与算出来不一致的线。
        var nudged = L(0, 50, 10, 100, 50, 11 + 1e-9);
        var d = CenterlineLayerDiff.Compute(Before(), new List<double[]> { A, nudged, C });

        Assert.Equal(new ulong[] { 2 }, d.DeleteHandles);
        Assert.Single(d.WriteLines);
    }
}
