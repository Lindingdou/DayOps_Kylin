// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SeamQualitySamplerTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;
using Hole = PitMine3D.Kylin.TaskLib.Engine.SeamQualitySampler.HoleSample;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「按位置取煤质」的判据（Q 组）。
///
/// <para><b>这一环之前只有一条来源</b>：人在「作业面台账」上手填四项。没人填 ⇒ 该面无煤质目标
/// ⇒ <b>配煤约束整条不跑</b>，而计划照出、报表照有 ——「按综合灰分重分配采出量」那一步
/// 静默变成空操作。钻孔就在图上，面也在图上，中间缺的只是一次按距离的取值。</para>
///
/// <para>这一组判的全是<b>该不该取</b>与<b>取的是哪一份</b>，不判插值精度：
/// 取错层、取到三公里外、取了精煤而不是原煤 —— 回来的都是四个看着完全正常的数。</para>
/// </summary>
public class SeamQualitySamplerTests
{
    private static Hole H(string id, double x, double y,
                          double? ash = 12.0, double? cv = 22.0, double? s = 0.6, double? m = 8.0)
        => new(id, x, y, ash, cv, s, m);

    // ══════════════════════════════════════════════════════════════
    //  Q2 四项齐全才交给引擎
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Q2 <b>四项齐全才交给引擎</b>；一个齐全的都没有时取不到，并说清楚为什么。
    /// <para>半份硬凑时没填的项变成 0 —— 配煤校核里「灰分 ≤ 0+容差」几乎恒假、
    /// 「热值 ≥ 0−容差」恒真，<b>一个半真半假的约束比没有约束更难查</b>。</para>
    /// </summary>
    [Fact]
    public void Q2_四项不齐的样不参与()
    {
        var pick = SeamQualitySampler.PickFrom(0, 0, new[]
        {
            H("ZK1", 10, 0, ash: 12, cv: null, s: 0.6, m: 8),      // 缺热值
            H("ZK2", 20, 0, ash: null, cv: 22, s: 0.6, m: 8),      // 缺灰分
        });

        Assert.False(pick.Ok);
        Assert.Null(pick.Quality);
        Assert.Contains("没有一个四项齐全", pick.Why);
    }

    /// <summary>Q2b 齐全的与不齐的混在一起时，<b>只用齐全的</b>，并报出丢了几个。</summary>
    [Fact]
    public void Q2b_只用四项齐全的并报出丢了几个()
    {
        var pick = SeamQualitySampler.PickFrom(0, 0, new[]
        {
            H("ZK1", 10, 0, ash: 10),
            H("ZK2", 12, 0, ash: null),                             // 半份，不参与
        });

        Assert.True(pick.Ok);
        Assert.Equal(1, pick.HoleCount);
        Assert.Equal(10, pick.Quality!.AshPct, 6);                  // 只被 ZK1 决定
        Assert.Contains("四项不全", pick.Provenance);
    }

    // ══════════════════════════════════════════════════════════════
    //  Q3 距离上限
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Q3 最近的孔超出取样半径时<b>取不到</b>，不取一个远处的值。
    /// <para>三公里外那个孔的煤质不代表这个面 —— 而取回来的四个数一样正常，没人看得出来。</para>
    /// </summary>
    [Fact]
    public void Q3_超出半径取不到而不是取远处的值()
    {
        var pick = SeamQualitySampler.PickFrom(0, 0, new[] { H("ZK1", 3000, 0) }, radiusM: 1500);

        Assert.False(pick.Ok);
        Assert.Contains("3,000", pick.Why.Replace(",", ",")); // 报出实际距离
        Assert.Contains("取不到", pick.Why);
        Assert.Equal(3000, pick.NearestM, 3);                       // 距离仍如实带回来
    }

    /// <summary>Q3b 半径之内的取得到；<b>「取不到」与「没有孔」要分得开</b>（都不 Ok，但补法不同）。</summary>
    [Fact]
    public void Q3b_没有孔与太远是两回事()
    {
        var none = SeamQualitySampler.PickFrom(0, 0, Array.Empty<Hole>());
        Assert.False(none.Ok);
        Assert.Contains("一个煤质样都没有", none.Why);
        Assert.True(double.IsNaN(none.NearestM));

        var far = SeamQualitySampler.PickFrom(0, 0, new[] { H("ZK1", 9999, 0) });
        Assert.False(far.Ok);
        Assert.Contains("取样半径", far.Why);
        Assert.False(double.IsNaN(far.NearestM));                   // 有孔，只是太远
    }

    // ══════════════════════════════════════════════════════════════
    //  Q6 反距离加权
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Q6 <b>近孔权重大</b> —— 不是等权平均。
    /// <para>ZK1 在 10m 处灰 8%，ZK2 在 1000m 处灰 20%：等权会给 14%，
    /// 反距离加权必须明显偏向 8%。</para>
    /// </summary>
    [Fact]
    public void Q6_近孔权重更大而不是等权()
    {
        var pick = SeamQualitySampler.PickFrom(0, 0, new[]
        {
            H("ZK1", 10, 0, ash: 8),
            H("ZK2", 1000, 0, ash: 20),
        });

        Assert.True(pick.Ok);
        Assert.True(pick.Quality!.AshPct < 9,
                    $"加权灰分 {pick.Quality.AshPct:0.##}% —— 没有明显偏向近孔（等权会是 14%）");
    }

    /// <summary>
    /// Q6b 面正好落在孔上（距离 0）时不炸、不出 NaN。
    /// <para>1/d 在 d=0 处是无穷 —— 不夹下限的话四项会一起变成 NaN，
    /// 而 NaN 会顺着配煤算下去，最后在一个毫不相干的地方冒出来。</para>
    /// </summary>
    [Fact]
    public void Q6b_面正好落在孔上时不出NaN()
    {
        var pick = SeamQualitySampler.PickFrom(0, 0, new[]
        {
            H("ZK1", 0, 0, ash: 10),
            H("ZK2", 500, 0, ash: 20),
        });

        Assert.True(pick.Ok);
        Assert.False(double.IsNaN(pick.Quality!.AshPct));
        Assert.False(double.IsNaN(pick.Quality.CalorificMJkg));
        Assert.InRange(pick.Quality.AshPct, 10, 10.1);              // 零距那个孔几乎吃满权重
    }

    /// <summary>Q6c 只用最近的 k 个孔，远处的不进来冲淡。</summary>
    [Fact]
    public void Q6c_只用最近的若干个孔()
    {
        var holes = Enumerable.Range(1, 10).Select(i => H($"ZK{i}", i * 100, 0)).ToList();
        var pick = SeamQualitySampler.PickFrom(0, 0, holes, maxHoles: 3);

        Assert.Equal(3, pick.HoleCount);
        Assert.Contains("3 个孔", pick.Provenance);
    }

    // ══════════════════════════════════════════════════════════════
    //  Q5 取样依据
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Q5 取样依据要说得出：几个孔、最近多远、半径多少、哪几个孔号、<b>原煤还是精煤</b>。
    /// <para>三个月后有人问「这个面的灰分哪来的」，答案得在库里。</para>
    /// </summary>
    [Fact]
    public void Q5_取样依据说得出来()
    {
        var pick = SeamQualitySampler.PickFrom(0, 0, new[] { H("ZK7", 120, 0), H("ZK8", 340, 0) });

        Assert.True(pick.Ok);
        Assert.Contains("2 个孔", pick.Provenance);
        Assert.Contains("反距离加权", pick.Provenance);
        Assert.Contains("ZK7", pick.Provenance);
        Assert.Contains("原煤", pick.Provenance);                   // Q4：口径写明白
        Assert.Contains("半径", pick.Provenance);
    }

    /// <summary>
    /// Q1 没有煤层号时<b>拒绝取样</b>（不跨层）。
    /// <para>另一层的煤质安到这一层上，四个数看着都正常，而两层的灰分可以差一倍。</para>
    /// </summary>
    [Fact]
    public void Q1_没有煤层号时拒绝取样()
    {
        var pick = SeamQualitySampler.Pick("", 0, 0);
        Assert.False(pick.Ok);
        Assert.Contains("不跨层取样", pick.Why);
    }

    /// <summary>Q7 四项都被加权，不是只算灰分。</summary>
    [Fact]
    public void Q7_四项都被加权()
    {
        var pick = SeamQualitySampler.PickFrom(0, 0, new[]
        {
            H("ZK1", 100, 0, ash: 10, cv: 20, s: 0.4, m: 6),
            H("ZK2", 100, 0, ash: 20, cv: 30, s: 0.8, m: 10),       // 同距 ⇒ 等权
        });

        Assert.True(pick.Ok);
        Assert.Equal(15, pick.Quality!.AshPct, 3);
        Assert.Equal(25, pick.Quality.CalorificMJkg, 3);
        Assert.Equal(0.6, pick.Quality.SulfurPct, 3);
        Assert.Equal(8, pick.Quality.MoisturePct, 3);
    }
}
