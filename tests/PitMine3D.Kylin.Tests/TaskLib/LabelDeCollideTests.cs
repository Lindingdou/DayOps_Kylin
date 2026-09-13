// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/LabelDeCollideTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;
using PitMine3D.Kylin.TaskLib.Simulation;

/// <summary>
/// 作业铭牌避让的判据（D 组）。
///
/// <para>同样是截图逼出来的：中班/夜班整班都是检修，七台设备的铭牌落在采场同一小片上，
/// 图上是一坨读不出来的字。三维那一层要答的就是「谁在哪个面上」，字叠了等于没答。</para>
/// </summary>
public sealed class LabelDeCollideTests
{
    private readonly ITestOutputHelper _out;
    public LabelDeCollideTests(ITestOutputHelper o) => _out = o;

    private const double H = 30;           // 世界米字高
    private static double Row => H * 1.45;
    private static double Wide => H * 7.0;

    private static (List<double> xyz, List<float> h) Make(params (double x, double y)[] pts)
        => (pts.SelectMany(p => new[] { p.x, p.y, 0.0 }).ToList(),
            Enumerable.Repeat((float)H, pts.Length).ToList());

    /// <summary>避让后任意两块牌子不得再落在同一个碰撞盒里。</summary>
    private void AssertNoOverlap(List<double> xyz)
    {
        int n = xyz.Count / 3;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double dx = Math.Abs(xyz[i * 3] - xyz[j * 3]);
                double dy = Math.Abs(xyz[i * 3 + 1] - xyz[j * 3 + 1]);
                Assert.False(dx < Wide - 1e-9 && dy < Row - 1e-9,
                    $"第 {i} 与第 {j} 块牌子仍然重叠：dx={dx:0.#} dy={dy:0.#}（需 dx≥{Wide:0} 或 dy≥{Row:0}）");
            }
    }

    [Fact]
    public void D1_七块牌子叠在一起要全部分开()
    {
        // 实测算例：中班七台设备落在同一小片（相互几十米）
        var (xyz, h) = Make((620000, 4380000), (620020, 4380010), (620010, 4379990),
                            (620030, 4380005), (619990, 4380015), (620005, 4379995),
                            (620015, 4380020));
        LabelDeCollide.DeCollideLabels(xyz, h);
        for (int i = 0; i < 7; i++) _out.WriteLine($"  #{i} ({xyz[i * 3]:0}, {xyz[i * 3 + 1]:0})");
        AssertNoOverlap(xyz);
    }

    [Fact]
    public void D2_X一律不动_只沿Y让()
    {
        var (xyz, h) = Make((620000, 4380000), (620020, 4380010), (620010, 4379990));
        var x0 = Enumerable.Range(0, 3).Select(i => xyz[i * 3]).ToList();
        LabelDeCollide.DeCollideLabels(xyz, h);
        for (int i = 0; i < 3; i++)
            Assert.Equal(x0[i], xyz[i * 3]);
    }

    /// <summary>本来就分得开的，一毫米都不许动 —— 避让不能反过来把好好的图搅乱。</summary>
    [Fact]
    public void D3_本来就不挤的不许动()
    {
        var (xyz, h) = Make((620000, 4380000), (621000, 4380000), (622000, 4380000),
                            (620000, 4381000));
        var before = xyz.ToList();
        LabelDeCollide.DeCollideLabels(xyz, h);
        Assert.Equal(before, xyz);
    }

    /// <summary>只往下让，不往上 —— 上下顺序必须与原来一致，否则读不出哪个牌子对哪个点。</summary>
    [Fact]
    public void D4_只往下让且顺序不变()
    {
        var (xyz, h) = Make((620000, 4380000), (620010, 4380030), (620005, 4379970));
        var before = Enumerable.Range(0, 3).Select(i => xyz[i * 3 + 1]).ToList();
        LabelDeCollide.DeCollideLabels(xyz, h);
        var after = Enumerable.Range(0, 3).Select(i => xyz[i * 3 + 1]).ToList();

        for (int i = 0; i < 3; i++)
        {
            _out.WriteLine($"  #{i} Y {before[i]:0} → {after[i]:0}");
            Assert.True(after[i] <= before[i] + 1e-9, $"第 {i} 块被往上推了");
        }
        var byBefore = Enumerable.Range(0, 3).OrderByDescending(i => before[i]).ToList();
        var byAfter = Enumerable.Range(0, 3).OrderByDescending(i => after[i]).ToList();
        Assert.Equal(byBefore, byAfter);
    }

    /// <summary>横向拉开就不该再往下让 —— 否则一排横着的牌子会被推成阶梯。</summary>
    [Fact]
    public void D5_横向拉开的同一行不许被推成阶梯()
    {
        var (xyz, h) = Make((620000, 4380000), (620000 + Wide + 1, 4380000),
                            (620000 + 2 * (Wide + 1), 4380000));
        var before = xyz.ToList();
        LabelDeCollide.DeCollideLabels(xyz, h);
        Assert.Equal(before, xyz);
    }

    [Fact]
    public void D6_零个或一个牌子不会出事()
    {
        var e = new List<double>(); var eh = new List<float>();
        LabelDeCollide.DeCollideLabels(e, eh);
        Assert.Empty(e);

        var (xyz, h) = Make((620000, 4380000));
        var before = xyz.ToList();
        LabelDeCollide.DeCollideLabels(xyz, h);
        Assert.Equal(before, xyz);
    }

    /// <summary>这条防"闸门关掉也全绿"：把碰撞盒当成 0 时判据必须真的红。</summary>
    [Fact]
    public void D0_自检_不做避让必须判红()
    {
        var (xyz, _) = Make((620000, 4380000), (620020, 4380010), (620010, 4379990));
        var ex = Record.Exception(() => AssertNoOverlap(xyz));
        _out.WriteLine(ex?.Message ?? "（没红 —— 这条判据是空的！）");
        Assert.NotNull(ex);
    }
}
