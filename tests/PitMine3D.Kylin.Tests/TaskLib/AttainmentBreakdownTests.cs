// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/AttainmentBreakdownTests.cs（逐行对应；仅命名空间/依赖适配）
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
/// 达成缺口归因的判据。
///
/// <para>
/// 这一层最容易出的错不是算错，而是<b>把凑不上的量摊进最近的一个原因里</b> ——
/// 结论看着精确（"运力不足占 40%"），实则是编的。所以本组花最大力气钉两件事：
/// ① 恒等式真的守恒（工时缺口 + 效率缺口 ≡ 总缺口）；
/// ② 归不上的必须留在「未解释」里，不许被摊掉。
/// </para>
/// </summary>
public class AttainmentBreakdownTests
{
    /// <summary>计划 8h × 100 m³/h = 800 m³ 的采装任务；配车按需给足。</summary>
    private static ProductionTask Task(double plan = 800, double actual = 800,
                                       double planH = 8, double actH = 8,
                                       int trucks = 4, int recommend = 4,
                                       params IncompleteReason[] reasons) => new()
    {
        Id = "D0811-WK10-中", Process = ProcessType.Load, Shift = "中班",
        WorkZone = "主采面·东", StartHour = 8, EndHour = 16,
        TargetVolumeM3 = plan, PlannedHours = planH,
        ActualVolumeM3 = actual, ActualHours = actH,
        Group = new EquipmentGroup
        {
            MainEquipment = "WK-10",
            Trucks = Enumerable.Range(1, trucks).Select(i => $"T-{i:00}").ToList(),
            RecommendedTrucks = recommend,
        },
        Reasons = reasons.ToList(),
    };

    // ── A1：恒等式守恒 —— 工时缺口 + 效率缺口 ≡ 总缺口 ──────────────────────────
    [Theory]
    [InlineData(800, 600, 8, 8)]     // 只慢不停
    [InlineData(800, 600, 8, 6)]     // 只停不慢
    [InlineData(800, 450, 8, 6.5)]   // 又停又慢
    [InlineData(800, 950, 8, 8)]     // 超额
    public void A1_两个分量之和恒等于缺口(double plan, double act, double hPlan, double hAct)
    {
        var b = AttainmentAnalyzer.Of(Task(plan, act, hPlan, hAct));
        Assert.Equal(b.GapM3, b.HourGapM3 + b.RateGapM3, 3);
    }

    // ── A2：突发停机按「故障 h × 计划班产」折成方量 ────────────────────────────
    [Fact]
    public void A2_突发停机折成方量()
    {
        // 计划 800（8h×100），实际只干了 6h 出 600 ⇒ 工时缺口 200，其中故障 2h
        var b = AttainmentAnalyzer.Of(Task(800, 600, 8, 6), faultHours: 2);

        var fault = b.Items.Single(x => x.Cause.Contains("突发停机"));
        Assert.Equal(200, fault.VolumeM3, 0);          // 2h × 100 m³/h
        Assert.Contains("100", fault.Basis);           // 算式要写在脸上
        Assert.Equal(0, b.UnexplainedM3, 0);           // 这一条全解释完了
        Assert.Equal(100, b.ExplainedPct, 0);
    }

    // ── A3：配车不足归到「设备布置」，且按缺车比例折算 ──────────────────────────
    [Fact]
    public void A3_配车不足是设备布置问题()
    {
        // 工时干满 8h，但只出 600（效率缺口 200）；配 2 车 < 荐 4 车 ⇒ 缺 50%
        var b = AttainmentAnalyzer.Of(Task(800, 600, 8, 8, trucks: 2, recommend: 4));

        var layout = b.Items.Single(x => x.Cause.Contains("设备布置"));
        Assert.Equal(100, layout.VolumeM3, 0);         // 200 × 50%
        Assert.Contains("配 2 车 < 荐 4 车", layout.Basis);

        // 剩下那一半归执行效率 —— 不许把整个效率缺口都推给配车
        var exec = b.Items.Single(x => x.Cause == "执行效率");
        Assert.Equal(100, exec.VolumeM3, 0);
        Assert.Equal(0, b.UnexplainedM3, 0);
    }

    // ── A3b：配车给足时不许出现「设备布置」这一项（关闸，否则 A3 可能恒真）─────────
    [Fact]
    public void A3b_配车够就不赖布置()
    {
        var b = AttainmentAnalyzer.Of(Task(800, 600, 8, 8, trucks: 4, recommend: 4));

        Assert.DoesNotContain(b.Items, x => x.Cause.Contains("设备布置"));
        Assert.Equal(200, b.Items.Single(x => x.Cause == "执行效率").VolumeM3, 0);
    }

    // ── A4：★ 归不上的必须留在「未解释」里，不许摊掉 ────────────────────────────
    [Fact]
    public void A4_未解释残差要露出来()
    {
        // 少干 3h（缺 300），但只报了 1h 故障、且没录任何等待原因码
        var b = AttainmentAnalyzer.Of(Task(800, 500, 8, 5), faultHours: 1);

        Assert.Equal(300, b.GapM3, 0);
        Assert.Equal(100, b.Items.Single(x => x.Cause.Contains("突发停机")).VolumeM3, 0);

        // ★ 剩下 200 没人认领 —— 必须留着，且解释率要如实降下来
        Assert.Equal(200, b.UnexplainedM3, 0);
        Assert.Equal(33, b.ExplainedPct, 0);
        Assert.Contains("未解释", AttainmentAnalyzer.Caption(b));
    }

    // ── A5：等待类原因码分掉剩余的少干工时 ──────────────────────────────────────
    [Fact]
    public void A5_等待原因码分掉剩余工时()
    {
        // 少干 4h（缺 400），故障 1h（100），剩 3h 由两项等待原因均摊
        var b = AttainmentAnalyzer.Of(Task(800, 400, 8, 4), faultHours: 1);
        Assert.Equal(300, b.UnexplainedM3, 0);          // 没录原因码 ⇒ 全部未解释

        var withReasons = AttainmentAnalyzer.Of(
            Task(800, 400, 8, 4, 4, 4, IncompleteReason.BlastWait, IncompleteReason.RoadCongestion),
            faultHours: 1);

        Assert.Equal(2, withReasons.Items.Count(x => x.Cause.StartsWith("等待", StringComparison.Ordinal)));
        Assert.Equal(0, withReasons.UnexplainedM3, 0);  // 这次凑满了
        foreach (var w in withReasons.Items.Where(x => x.Cause.StartsWith("等待", StringComparison.Ordinal)))
            Assert.Equal(150, w.VolumeM3, 0);           // 3h ÷ 2 × 100
    }

    // ── A6：超额不做归因（缺口为负时硬凑归因是荒唐的）────────────────────────────
    [Fact]
    public void A6_超额不归因()
    {
        var b = AttainmentAnalyzer.Of(Task(800, 950, 8, 8), faultHours: 2);

        Assert.True(b.IsOver);
        Assert.Empty(b.Items);
        Assert.Equal(0, b.UnexplainedM3, 0);
        Assert.Contains("超额", AttainmentAnalyzer.Caption(b));
    }

    // ── A7：归因量之和不许超过缺口（故障报得比缺口还大时要截断）───────────────────
    [Fact]
    public void A7_归因不许超过缺口()
    {
        // 缺口只有 100（少干 1h），却报了 5h 故障 —— 常见于故障时段与任务时段重叠算法出错
        var b = AttainmentAnalyzer.Of(Task(800, 700, 8, 7), faultHours: 5);

        Assert.Equal(100, b.GapM3, 0);
        Assert.True(b.Items.Sum(x => x.VolumeM3) <= b.GapM3 + 1,
            $"归因合计 {b.Items.Sum(x => x.VolumeM3):0} 超过了缺口 {b.GapM3:0} —— 分解不能凭空造量");
    }

    // ── A8：聚合按原因合并，且计划/实绩逐条相加 ──────────────────────────────────
    [Fact]
    public void A8_多条任务合并归因()
    {
        var a = AttainmentAnalyzer.Of(Task(800, 600, 8, 6), faultHours: 2);              // 突发停机 200
        var c = AttainmentAnalyzer.Of(Task(600, 450, 6, 4.5), faultHours: 1.5);          // 突发停机 150
        var all = AttainmentAnalyzer.Combine(new[] { a, c });

        Assert.Equal(1400, all.PlanM3, 0);
        Assert.Equal(1050, all.ActualM3, 0);
        Assert.Equal(350, all.GapM3, 0);
        Assert.Equal(350, all.Items.Single(x => x.Cause.Contains("突发停机")).VolumeM3, 0);
    }
}
