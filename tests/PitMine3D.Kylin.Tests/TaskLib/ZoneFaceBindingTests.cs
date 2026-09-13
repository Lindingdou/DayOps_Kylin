// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/ZoneFaceBindingTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Zoning;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「区域 ↔ 作业面按位置绑」的判据（B 组）。
///
/// <para><b>改这一版之前是纯字符串</b>：`SimPlanScene.NameHit` 一个坐标都不用，
/// 于是界面上常年是「名字「采场1」对不上盘子里任何作业面」——
/// 而两边都有坐标。靠名字对号的代价是：谁改了个名字这块地就整期不动，
/// 画面上只显示「本期无采掘」，不报错。</para>
///
/// <para><b>这一组最容易空过的地方</b>：算例里让位置和名字**同时**对上 ——
/// 那样无论实现走哪条路都绿，「位置优先」什么也证不了。
/// 所以下面每条判位置的算例，名字都<b>故意不一样</b>。</para>
/// </summary>
public class ZoneFaceBindingTests
{
    /// <summary>0..100 见方的区域环。</summary>
    private static List<ZonePoint> Square(double x0 = 0, double y0 = 0, double s = 100, double z = 1200)
        => new()
        {
            new(x0, y0, z), new(x0 + s, y0, z), new(x0 + s, y0 + s, z), new(x0, y0 + s, z),
        };

    private static FaceInput Face(string zone, double x = double.NaN, double y = double.NaN)
    {
        var f = new FaceInput { Zone = zone, Process = global::PitMine3D.Kylin.TaskLib.Domain.ProcessType.Load };
        if (!double.IsNaN(x)) { f.SourceX = x; f.SourceY = y; }
        return f;
    }

    // ══════════════════════════════════════════════════════════════
    //  B1 源点落在环内
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// B1 铲位落在区内就配上 —— <b>名字完全不一样也配得上</b>。
    /// <para>算例里名字故意毫不相干（「东采区」vs「采场1」）：名字也对得上的话，
    /// 这条判据无论实现走哪条路都绿，等于什么也没证。</para>
    /// </summary>
    [Fact]
    public void B1_铲位在区内时名字不同也配得上()
    {
        var r = ZoneFaceBinding.Match(Square(), "采场1", new[] { Face("东采区", 50, 50) });

        var hit = Assert.Single(r.Hits);
        Assert.Equal(BindRoute.Inside, hit.Route);
        Assert.Equal("东采区", hit.Face.Zone);
        Assert.Equal(0, hit.DistanceM, 6);
    }

    /// <summary>B1b 铲位在区外且超容差 ⇒ 位置不认（名字也不同 ⇒ 整体配不上）。</summary>
    [Fact]
    public void B1b_铲位远在区外时不认()
    {
        var r = ZoneFaceBinding.Match(Square(), "采场1", new[] { Face("东采区", 5000, 5000) });

        Assert.False(r.Ok);
        Assert.Contains(r.Notes, n => n.Contains("位置与名字都对不上"));
    }

    // ══════════════════════════════════════════════════════════════
    //  B2 容差
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// B2 铲位在区外但在容差内也算 —— 铲位常常标在坡顶线外侧几十米。
    /// </summary>
    [Fact]
    public void B2_容差内的铲位也算()
    {
        // 区是 0..100；铲位在 (50, 180) ⇒ 离边 80m，缺省容差 150m
        var r = ZoneFaceBinding.Match(Square(), "采场1", new[] { Face("东采区", 50, 180) });

        var hit = Assert.Single(r.Hits);
        Assert.Equal(BindRoute.Near, hit.Route);
        Assert.InRange(hit.DistanceM, 79, 81);
    }

    /// <summary>
    /// B2b <b>容差必须有上限</b> —— 不设上限时"最近的那个面"无论多远都会被配上。
    /// <para>这条钉的是"取最近"与"取最近且在容差内"的区别：前者永远配得上一个。</para>
    /// </summary>
    [Fact]
    public void B2b_容差必须有上限()
    {
        var faces = new[] { Face("东采区", 50, 1000) };            // 离边 900m
        Assert.False(ZoneFaceBinding.Match(Square(), "采场1", faces).Ok);
        // 把容差放大到 1000 就该配上 —— 证明上面那条红是被容差挡的，不是别的原因
        Assert.True(ZoneFaceBinding.Match(Square(), "采场1", faces, toleranceM: 1000).Ok);
    }

    /// <summary>
    /// B2c 距离量的是<b>到边</b>，不是到顶点。
    /// <para>细长区域上，点可能离每个顶点都很远却贴着边 —— 按顶点算会把它判成"远"。</para>
    /// </summary>
    [Fact]
    public void B2c_距离量到边不是到顶点()
    {
        // 一条 0..1000 × 0..10 的细长地；点在 (500, 30)：离边 20m，离最近顶点 ~500m
        var ring = new List<ZonePoint>
        { new(0, 0, 0), new(1000, 0, 0), new(1000, 10, 0), new(0, 10, 0) };
        var poly = ring.Select(p => (p.X, p.Y)).ToList();

        Assert.InRange(ZoneFaceBinding.DistanceToRing(poly, 500, 30), 19, 21);
    }

    // ══════════════════════════════════════════════════════════════
    //  B3 名字只是退路
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// B3 位置对不上、名字对得上 ⇒ 走名字，<b>并且明说"位置对不上"</b>。
    /// <para>这种绑定谁改个名字就断，看的人有权知道自己靠的是哪一条。</para>
    /// </summary>
    [Fact]
    public void B3_位置不成立时退名字并说清楚()
    {
        var r = ZoneFaceBinding.Match(Square(), "采场1", new[] { Face("采场1", 9999, 9999) });

        var hit = Assert.Single(r.Hits);
        Assert.Equal(BindRoute.Name, hit.Route);
        Assert.True(r.NameOnly);
        Assert.Contains(r.Notes, n => n.Contains("靠名字") && n.Contains("改个名字就断"));
    }

    /// <summary>
    /// B3b <b>位置优先于名字</b>：位置指向 A、名字指向 B 时，配上的是 <b>A</b>。
    /// <para>这是整条改动的核心。实现里把两条路的先后写反，这一条必红。</para>
    /// </summary>
    [Fact]
    public void B3b_位置指A名字指B时取A()
    {
        var r = ZoneFaceBinding.Match(Square(), "采场1", new[]
        {
            Face("东采区", 50, 50),      // 位置在区内，名字不相干
            Face("采场1", 9999, 9999),   // 名字一模一样，位置在天边
        });

        var hit = Assert.Single(r.Hits);
        Assert.Equal("东采区", hit.Face.Zone);
        Assert.Equal(BindRoute.Inside, hit.Route);
    }

    /// <summary>B3c 没有源端坐标的面只能走名字，而且要点名说有几个。</summary>
    [Fact]
    public void B3c_没有坐标的面只能走名字并点名()
    {
        var r = ZoneFaceBinding.Match(Square(), "采场1", new[] { Face("采场1") });

        Assert.Equal(BindRoute.Name, Assert.Single(r.Hits).Route);
        Assert.Contains(r.Notes, n => n.Contains("没有源端坐标"));
    }

    // ══════════════════════════════════════════════════════════════
    //  B5 多个不挑一个
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// B5 一块地上落了两个面的铲位 ⇒ <b>全都保留</b>。
    /// <para>随便挑一个的话，另一个面的量整期不进这块地，而两边各自都看着对。</para>
    /// </summary>
    [Fact]
    public void B5_区内多个面全保留不挑一个()
    {
        var r = ZoneFaceBinding.Match(Square(), "采场1", new[]
        {
            Face("上台阶", 30, 30),
            Face("下台阶", 70, 70),
        });

        Assert.Equal(2, r.Hits.Count);
        Assert.All(r.Hits, h => Assert.Equal(BindRoute.Inside, h.Route));
        Assert.Contains(r.Notes, n => n.Contains("全都保留，不挑一个"));
    }

    // ══════════════════════════════════════════════════════════════
    //  边界
    // ══════════════════════════════════════════════════════════════

    /// <summary>B6 环围不成面（&lt;3 点）时位置走不了，退名字并说明。</summary>
    [Fact]
    public void B6_环不成面时退名字并说明()
    {
        var r = ZoneFaceBinding.Match(
            new List<ZonePoint> { new(0, 0, 0), new(10, 0, 0) }, "采场1",
            new[] { Face("采场1", 5, 0) });

        Assert.Equal(BindRoute.Name, Assert.Single(r.Hits).Route);
        Assert.Contains(r.Notes, n => n.Contains("不足 3 个点"));
    }

    /// <summary>B7 盘子里一个面都没有时不炸，且有话说。</summary>
    [Fact]
    public void B7_没有面时不炸且有话说()
    {
        var r = ZoneFaceBinding.Match(Square(), "采场1", Array.Empty<FaceInput>());
        Assert.False(r.Ok);
        Assert.NotEmpty(r.Notes);
    }

    /// <summary>B8 四档路线各有各的说法，没有一档落空（认不出会让界面显示空白）。</summary>
    [Fact]
    public void B8_四档路线都有说法()
    {
        foreach (var route in Enum.GetValues<BindRoute>())
        {
            var h = new ZoneFaceHit { Route = route, DistanceM = 50 };
            Assert.False(string.IsNullOrWhiteSpace(h.RouteZh), $"{route} 没有说法");
        }
    }
}
