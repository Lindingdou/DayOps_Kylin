// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/PlanDriftTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「计划被改过没有」的判据（N 组）。
///
/// <para><b>这一组防的是一个会自己消失的数</b>：动态调整里的欠量 = 计划 − 实绩。
/// 实绩是死的，计划量却每次都重新装配 —— 早上排 3000 干了 2000 欠 1000；
/// 中午有人把单元量改成 2000，下午再看**欠量成了 0**，那 1000 方谁也没干。</para>
///
/// <para><b>最容易空过的写法</b>：只断言"有基准时能查出改量"。
/// 那样"没有基准"这条路会返回 HasDrift=false，被界面当成「没改过」——
/// 而它其实是「判不了」。N1 专钉这一条。</para>
/// </summary>
public class PlanDriftTests
{
    private static ProductionTask T(string id, string zone, double target,
                                    ProcessType proc = ProcessType.Load, string shift = "早")
        => new()
        {
            Id = id, WorkZone = zone, Shift = shift, Process = proc,
            TargetVolumeM3 = target, Group = new EquipmentGroup { MainEquipment = "WK-1" },
        };

    private static PlanBaselineResult Base(params ProductionTask[] ts)
        => new() { Source = BaselineSource.Snapshot, Tasks = ts.ToList(), Label = "基准：当日快照" };

    // ══════════════════════════════════════════════════════════════
    //  N1 「判不了」不是「没改过」
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// N1 <b>没有基准时不能说"没变"</b>。
    /// <para>没快照 ⇒ Comparable=false、HasDrift=false —— 但界面据此说「计划没被改过」就是撒谎。
    /// 所以文案里**不许**出现"一致/没变/未改动"，且必须说出补法。</para>
    /// </summary>
    [Fact]
    public void N1_没有基准时说判不了而不是没改过()
    {
        var r = PlanDrift.Compare(new PlanBaselineResult { Source = BaselineSource.None },
                                  new[] { T("a", "采场1", 3000) });

        Assert.False(r.Comparable);
        Assert.False(r.HasDrift);                       // 没有漂移条目 —— 但那不是"没改过"
        foreach (var word in new[] { "一致", "没变", "未改动", "没有改" })
            Assert.DoesNotContain(word, r.Headline);
        Assert.Contains("没有计划快照", r.Headline);
        Assert.Contains("保存本日计划", r.Headline);
    }

    /// <summary>
    /// N1b 现算的计划（Live）同样<b>不是基准</b> —— 它就是那个会漂的数本身，拿它对账等于自己对自己。
    /// </summary>
    [Fact]
    public void N1b_现算的计划不算基准()
    {
        var live = new PlanBaselineResult { Source = BaselineSource.Live, Tasks = { T("a", "采场1", 3000) } };
        Assert.False(PlanDrift.Compare(live, new[] { T("a", "采场1", 2000) }).Comparable);
    }

    // ══════════════════════════════════════════════════════════════
    //  N2 改量
    // ══════════════════════════════════════════════════════════════

    /// <summary>N2 量被改小 ⇒ 抓得到，且两边的数都留着（只说"变了"没法判该信哪个）。</summary>
    [Fact]
    public void N2_量被改过时抓得到并留下两边的数()
    {
        var r = PlanDrift.Compare(Base(T("a", "采场1", 3000)), new[] { T("a", "采场1", 2000) });

        Assert.True(r.Comparable);
        Assert.True(r.HasDrift);
        var row = Assert.Single(r.Changed);
        Assert.Equal(3000, row.BaseM3);
        Assert.Equal(2000, row.NowM3);
        Assert.Equal(-1000, row.DeltaM3);
        Assert.Contains("采场1", row.Label);
    }

    /// <summary>N2b 浮点噪声不算改动 —— 否则每次打开都是"计划已变"，这个提示就废了。</summary>
    [Fact]
    public void N2b_容差内不算改动()
    {
        var r = PlanDrift.Compare(Base(T("a", "采场1", 3000)), new[] { T("a", "采场1", 3000.4) });
        Assert.False(r.HasDrift);
        Assert.Contains("一致", r.Headline);            // 这条路上才允许说"一致"
    }

    // ══════════════════════════════════════════════════════════════
    //  N3 消失 —— 最要命的一类
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// N3 基准里有、现盘里没有 ⇒ 单列为<b>消失</b>，不能混进"改量"。
    /// <para>现盘里它不存在，于是任何"遍历当前任务算欠量"的统计都碰不到它，
    /// 它的欠量恒为 0 —— 而它明明是当天排过的活。</para>
    /// </summary>
    [Fact]
    public void N3_消失的任务单列并点名要命()
    {
        var r = PlanDrift.Compare(Base(T("a", "采场1", 3000), T("b", "采场2", 1500)),
                                  new[] { T("a", "采场1", 3000) });

        Assert.Empty(r.Changed);
        Assert.Equal("b", Assert.Single(r.Vanished).Id);
        Assert.Contains("消失", r.Headline);
        Assert.Contains("欠量恒为 0", r.Headline);
    }

    /// <summary>N3b 现盘多出来的单列为<b>新增</b>（不是"改量"，基准里根本没有它）。</summary>
    [Fact]
    public void N3b_新增的任务单列()
    {
        var r = PlanDrift.Compare(Base(T("a", "采场1", 3000)),
                                  new[] { T("a", "采场1", 3000), T("c", "采场3", 800) });

        Assert.Empty(r.Changed);
        Assert.Empty(r.Vanished);
        Assert.Equal("c", Assert.Single(r.Added).Id);
    }

    /// <summary>N3c 没有 Id 的任务对不上号 ⇒ 算新增，<b>不能悄悄丢掉</b>。</summary>
    [Fact]
    public void N3c_没有Id的任务算新增不丢掉()
    {
        var r = PlanDrift.Compare(Base(), new[] { T("", "采场1", 500) });
        Assert.Single(r.Added);
    }

    // ══════════════════════════════════════════════════════════════
    //  N4 净差额抵消
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// N4 <b>净差额 ≈ 0 不等于没改</b>。
    /// <para>一个面加 1000、另一个面减 1000 ⇒ 合计一分不差，而两个面的活全变了。
    /// 判"有没有动过"必须看条数。这条钉死"按合计判"那种写法。</para>
    /// </summary>
    [Fact]
    public void N4_一加一减抵消时仍然算改过()
    {
        var r = PlanDrift.Compare(
            Base(T("a", "采场1", 3000), T("b", "采场2", 2000)),
            new[] { T("a", "采场1", 4000), T("b", "采场2", 1000) });

        Assert.True(r.HasDrift);
        Assert.Equal(2, r.Changed.Count);
        Assert.Equal(0, r.NetDeltaM3, 6);
        Assert.Contains("抵掉了", r.Headline);          // 必须点破，否则读的人会当成"没改"
    }

    // ══════════════════════════════════════════════════════════════
    //  N5 口径
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// N5 非量型工序不进对账 —— 穿孔/爆破没有 m³，拿它们比量是无中生有。
    /// </summary>
    [Fact]
    public void N5_非量型工序不参与量对账()
    {
        var r = PlanDrift.Compare(
            Base(T("d", "采场1", 0, ProcessType.Drill)),
            new[] { T("d", "采场1", 9999, ProcessType.Drill) });

        Assert.False(r.HasDrift);
        Assert.Equal(0, r.BaseTotalM3);
        Assert.Equal(0, r.NowTotalM3);
    }

    /// <summary>
    /// N5b <b>按 Id 对号，不按下标</b>。
    /// <para>现盘中间插了一台新铲时，按下标对会整体错位 —— 而每一行看着都对。</para>
    /// </summary>
    [Fact]
    public void N5b_按Id对号而不是按下标()
    {
        var r = PlanDrift.Compare(
            Base(T("a", "采场1", 3000), T("b", "采场2", 2000)),
            new[] { T("x", "新面", 100), T("a", "采场1", 3000), T("b", "采场2", 2000) });

        Assert.Empty(r.Changed);                        // 按下标对的话这里会是 2 条"改量"
        Assert.Empty(r.Vanished);
        Assert.Equal("x", Assert.Single(r.Added).Id);
    }

    /// <summary>N6 空盘/null 不炸，且照样有话说。</summary>
    [Fact]
    public void N6_空盘不炸且有话说()
    {
        var r = PlanDrift.Compare(null, null);
        Assert.False(r.Comparable);
        Assert.False(string.IsNullOrWhiteSpace(r.Headline));

        var r2 = PlanDrift.Compare(Base(), Array.Empty<ProductionTask>());
        Assert.True(r2.Comparable);
        Assert.False(r2.HasDrift);
    }
}
