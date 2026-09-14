using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 处理尖灭（§三六三）。
///   ① 截断的是"从起点数过去第一处"，不是"厚度最小处" —— 两头都出露、中间尖灭时取最小处会把线断成两段。
///   ② 顶/底板没盖到的站<b>不算尖灭</b>（那是面没盖到，不是煤没了）。
///   ③ 不做"强制贯通"：贯通出来那段图上像台阶，地质上没有煤。
/// </summary>
public class BenchPinchTests
{
    private static List<(double x, double y)> L(params (double x, double y)[] p) => p.ToList();

    /// <summary>顶板 z = 100，底板 z = 100 − thick(x)：x&lt;50 厚 5m，x∈[50,80] 线性收到 0，x&gt;80 为 0。</summary>
    private static (IRoadZSampler top, IRoadZSampler bot) Seam()
    {
        var topV = new double[] { -10, -10, 100, 200, -10, 100, 200, 10, 100, -10, 10, 100 };
        var top = MeshZSampler.Build(topV, new[] { 0, 1, 2, 0, 2, 3 })!;
        var v = new List<double>(); var t = new List<int>();
        double[] xs = { -10, 50, 80, 200 }; double[] th = { 5, 5, 0, 0 };
        for (int i = 0; i < xs.Length; i++) { v.AddRange(new[] { xs[i], -10.0, 100 - th[i] }); v.AddRange(new[] { xs[i], 10.0, 100 - th[i] }); }
        for (int i = 0; i + 1 < xs.Length; i++) { int a = i * 2, b = i * 2 + 1, c = (i + 1) * 2, d = (i + 1) * 2 + 1; t.AddRange(new[] { a, c, b, b, c, d }); }
        return (top, MeshZSampler.Build(v.ToArray(), t.ToArray())!);
    }

    // ── 手动 ────────────────────────────────────────────────
    [Fact]
    public void 手动_在最近处截断保留起点侧()
    {
        var r = BenchPinch.TruncateAtPoint(L((0, 0), (100, 0), (200, 0)), 130, 5);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(130, r.PinchX, 6);
        Assert.Equal(0, r.PinchY, 6);
        Assert.Equal(130, r.KeptLengthM, 6);
        Assert.Equal(70, r.CutLengthM, 6);
        Assert.Equal(3, r.Line.Count);                              // (0,0) (100,0) (130,0)
    }

    [Fact]
    public void 手动_点在顶点上不重复加点()
    {
        var r = BenchPinch.TruncateAtPoint(L((0, 0), (100, 0), (200, 0)), 100, 0);
        Assert.True(r.Ok);
        Assert.Equal(2, r.Line.Count);
        Assert.Equal(100, r.KeptLengthM, 6);
    }

    [Fact]
    public void 手动_尖灭点落在起点时报错并提示反向()
    {
        var r = BenchPinch.TruncateAtPoint(L((0, 0), (100, 0)), -5, 0);
        Assert.False(r.Ok);
        Assert.Contains("反过来", r.Error);
    }

    [Fact]
    public void 手动_点超过终点时截在终点截掉零()
    {
        var r = BenchPinch.TruncateAtPoint(L((0, 0), (100, 0)), 150, 0);
        Assert.True(r.Ok);
        Assert.Equal(0, r.CutLengthM, 6);
    }

    // ── 煤层 ────────────────────────────────────────────────
    [Fact]
    public void 煤层_从起点数过去第一处厚度不足即尖灭()
    {
        var (top, bot) = Seam();
        var r = BenchPinch.TruncateWhereThin(L((0, 0), (150, 0)), top, bot, minThick: 0.3);
        Assert.True(r.Ok, r.Error);
        // 厚度 5 → 0 在 x∈[50,80] 线性：厚 0.3 处 x = 50 + 30·(1 − 0.3/5) = 78.2；站距 2 ⇒ 第一处 <0.3 的站在 78~80
        Assert.InRange(r.PinchX, 78, 80.01);
        Assert.True(r.CutLengthM > 0);
        Assert.Contains("尖灭于点", r.Note);
    }

    [Fact]
    public void 煤层_全线厚度够时不截并如实说()
    {
        var (top, bot) = Seam();
        var r = BenchPinch.TruncateWhereThin(L((0, 0), (40, 0)), top, bot);
        Assert.True(r.Ok);
        Assert.Equal(0, r.CutLengthM, 6);
        Assert.Contains("没有尖灭", r.Note);
    }

    [Fact]
    public void 煤层_起点就没煤时报错这条线不在煤里()
    {
        var (top, bot) = Seam();
        var r = BenchPinch.TruncateWhereThin(L((100, 0), (150, 0)), top, bot);
        Assert.False(r.Ok);
        Assert.Contains("不在煤里", r.Error);
    }

    [Fact]
    public void 煤层_面没盖到的站不算尖灭只计数()
    {
        // ★ 那是面没盖到，不是煤没了
        var (top, bot) = Seam();
        var r = BenchPinch.TruncateWhereThin(L((0, 0), (20, 0), (20, 50), (40, 50), (40, 0)), top, bot);   // 中段 y=50 在面外
        Assert.True(r.Ok, r.Error);
        Assert.Equal(0, r.CutLengthM, 6);
        Assert.Contains("没盖到", r.Note);
    }

    [Fact]
    public void 煤层_全线都采不到时报错()
    {
        var (top, bot) = Seam();
        var r = BenchPinch.TruncateWhereThin(L((0, 500), (100, 500)), top, bot);
        Assert.False(r.Ok);
        Assert.Contains("不在两张面的范围内", r.Error);
    }

    [Fact]
    public void 煤层_没有面或线太短时报错()
    {
        Assert.Contains("未指定面", BenchPinch.TruncateWhereThin(L((0, 0), (1, 0)), null, null).Error);
        Assert.Contains("至少 2 点", BenchPinch.TruncateWhereThin(L((0, 0)), Seam().top, Seam().bot).Error);
    }
}
