// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/DayHandoffTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 一天任务衔接的判据（H 组）。
///
/// <para>日班档最初是把月度那套按班压缩演一遍 —— 期长换成 8 小时，别的没变。
/// 但一天的调度关心的不是量，是<b>活跟活怎么接上</b>：炮放完了采装才能进场、
/// 一台铲干完这个面挪到哪个面、交班时哪个面上还剩什么要交代。这一组判的就是"接"。</para>
/// </summary>
public sealed class DayHandoffTests
{
    private readonly ITestOutputHelper _out;
    public DayHandoffTests(ITestOutputHelper o) => _out = o;

    private static ProductionTask T(string id, ProcessType p, string zone, double s, double e,
                                    string shift = "早班", string equip = "")
    {
        var t = new ProductionTask
        {
            Id = id, Process = p, WorkZone = zone,
            StartHour = s, EndHour = e, Shift = shift,
        };
        if (equip.Length > 0) t.Group.MainEquipment = equip;
        return t;
    }

    // ── H1 工序链 ────────────────────────────────────────────────────────────

    [Fact]
    public void H1_同面工序按穿孔爆破采装排土连起来()
    {
        var plan = DayHandoffPlan.Build(new[]
        {
            T("d", ProcessType.Drill, "主采面·东", 0, 4),
            T("b", ProcessType.Blast, "主采面·东", 5, 6),
            T("l", ProcessType.Load,  "主采面·东", 7, 15, "中班"),
        });

        var proc = plan.Links.Where(l => l.Kind == HandoffKind.Process).ToList();
        foreach (var l in proc) _out.WriteLine($"  {l.FromProcess.Label()}→{l.ToProcess.Label()}　间隔 {l.GapH:0.##} h");
        Assert.Equal(2, proc.Count);
        Assert.Equal(ProcessType.Drill, proc[0].FromProcess);
        Assert.Equal(ProcessType.Blast, proc[0].ToProcess);
        Assert.Equal(ProcessType.Blast, proc[1].FromProcess);
        Assert.Equal(ProcessType.Load, proc[1].ToProcess);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void H2_炮没放就装必须判成接不上()
    {
        var plan = DayHandoffPlan.Build(new[]
        {
            T("b", ProcessType.Blast, "主采面·东", 5, 8),
            T("l", ProcessType.Load,  "主采面·东", 6, 15),   // 早于爆破完工 2 h
        });
        var bad = plan.Conflicts.ToList();
        foreach (var l in bad) _out.WriteLine($"  接不上：{l.Label}　提前 {-l.GapH:0.##} h");
        Assert.Single(bad);
        Assert.Equal(HandoffKind.Process, bad[0].Kind);
        Assert.Equal(-2.0, bad[0].GapH, 6);
    }

    /// <summary>不同面之间不连工序链 —— 这个面的爆破管不着那个面的采装。</summary>
    [Fact]
    public void H3_跨面不连工序链()
    {
        var plan = DayHandoffPlan.Build(new[]
        {
            T("b", ProcessType.Blast, "主采面·东", 5, 8),
            T("l", ProcessType.Load,  "辅采面·南", 6, 15),
        });
        _out.WriteLine($"  工序链 {plan.Links.Count(l => l.Kind == HandoffKind.Process)} 条");
        Assert.Empty(plan.Links.Where(l => l.Kind == HandoffKind.Process));
        Assert.Empty(plan.Conflicts);
    }

    /// <summary>检修/空闲不推进任何东西，不进工序链；但要进同设备链。</summary>
    [Fact]
    public void H4_检修不进工序链但进设备链()
    {
        var plan = DayHandoffPlan.Build(new[]
        {
            T("i", ProcessType.Idle, "主采面·东", 0, 4, "早班", "WK-10"),
            T("l", ProcessType.Load, "主采面·东", 4, 8, "早班", "WK-10"),
        });
        Assert.Empty(plan.Links.Where(l => l.Kind == HandoffKind.Process));
        var eq = plan.Links.Where(l => l.Kind == HandoffKind.Equipment).ToList();
        _out.WriteLine($"  设备链 {eq.Count} 条");
        Assert.Single(eq);
        Assert.Equal("WK-10", eq[0].Equipment);
    }

    // ── H2 同设备接续 ────────────────────────────────────────────────────────

    [Fact]
    public void H5_一台设备同时在两处必须判成接不上()
    {
        var plan = DayHandoffPlan.Build(new[]
        {
            T("a", ProcessType.Load, "主采面·东", 0, 6, "早班", "WK-10"),
            T("b", ProcessType.Load, "辅采面·南", 4, 8, "早班", "WK-10"),   // 重叠 2 h
        });
        var bad = plan.Conflicts.Where(l => l.Kind == HandoffKind.Equipment).ToList();
        foreach (var l in bad) _out.WriteLine($"  {l.Equipment}：{l.FromZone} 到 {l.ToZone} 重叠 {-l.GapH:0.##} h");
        Assert.Single(bad);
        Assert.Equal(-2.0, bad[0].GapH, 6);
    }

    [Fact]
    public void H6_同设备换面算转场()
    {
        var plan = DayHandoffPlan.Build(new[]
        {
            T("a", ProcessType.Load, "主采面·东", 0, 4, "早班", "WK-10"),
            T("b", ProcessType.Load, "辅采面·南", 5, 8, "早班", "WK-10"),
        });
        var mv = plan.Moves.ToList();
        foreach (var l in mv) _out.WriteLine($"  转场 {l.Equipment}：{l.FromZone} ⇒ {l.ToZone}　留 {l.GapH:0.#} h");
        Assert.Single(mv);
        Assert.True(mv[0].MovesFace);
        Assert.Equal(1.0, mv[0].GapH, 6);
    }

    /// <summary>
    /// 面名为空不算转场 —— 截图逼出来的：信息栏打出「WK-10 ⇒ 主采面·东（采煤）」，
    /// 箭头前面是空的。那是「检修/空闲·未记位置」那条任务（WorkZone 为空）让它的设备
    /// 凭空多出一次转场。没记面不等于换了面，那是**缺数据**。
    /// </summary>
    [Fact]
    public void H6b_面名为空不算转场()
    {
        var plan = DayHandoffPlan.Build(new[]
        {
            T("a", ProcessType.Idle, "",            0, 4, "早班", "WK-10"),   // 未记位置
            T("b", ProcessType.Load, "主采面·东", 5, 8, "早班", "WK-10"),
        });
        foreach (var l in plan.Links.Where(l => l.Kind == HandoffKind.Equipment))
            _out.WriteLine($"  「{l.FromZone}」⇒「{l.ToZone}」　MovesFace={l.MovesFace}");
        Assert.Empty(plan.Moves);

        // 反过来：两端都有面名就必须判成转场（否则这条判据是空的）
        var real = DayHandoffPlan.Build(new[]
        {
            T("a", ProcessType.Load, "辅采面·南", 0, 4, "早班", "WK-10"),
            T("b", ProcessType.Load, "主采面·东", 5, 8, "早班", "WK-10"),
        });
        Assert.Single(real.Moves);
    }

    /// <summary>同一台设备在**同一个面**上接着干，不算转场 —— 不然每条设备链都被当成换面。</summary>
    [Fact]
    public void H7_同面接着干不算转场()
    {
        var plan = DayHandoffPlan.Build(new[]
        {
            T("a", ProcessType.Load, "主采面·东", 0, 4, "早班", "WK-10"),
            T("b", ProcessType.Load, "主采面·东", 4, 8, "中班", "WK-10"),
        });
        Assert.Empty(plan.Moves);
        Assert.Single(plan.Links.Where(l => l.Kind == HandoffKind.Equipment));
    }

    // ── 跨班交接 ─────────────────────────────────────────────────────────────

    [Fact]
    public void H8_跨班的那几条要能单独挑出来()
    {
        var plan = DayHandoffPlan.Build(new[]
        {
            T("a", ProcessType.Load, "主采面·东", 0, 8, "早班", "WK-10"),
            T("b", ProcessType.Load, "主采面·东", 8, 16, "中班", "WK-10"),
            T("c", ProcessType.Load, "辅采面·南", 1, 5, "早班", "WK-14"),
        });
        var cross = plan.CrossShift.ToList();
        foreach (var l in cross) _out.WriteLine($"  {l.FromShift}→{l.ToShift}　{l.Kind}　{l.FromZone}");
        Assert.NotEmpty(cross);
        Assert.All(cross, l => Assert.NotEqual(l.FromShift, l.ToShift));
        // 同一个班内的那条不许混进来
        Assert.DoesNotContain(cross, l => l.FromTaskId == "c" || l.ToTaskId == "c");
    }

    // ── 同面链 ───────────────────────────────────────────────────────────────

    /// <summary>一个面上两台设备并排干**不是**冲突 —— 只有工序链和设备链才判先后。</summary>
    [Fact]
    public void H9_同面并排作业不算冲突()
    {
        var plan = DayHandoffPlan.Build(new[]
        {
            T("a", ProcessType.Load, "主采面·东", 0, 8, "早班", "WK-10"),
            T("b", ProcessType.Load, "主采面·东", 0, 8, "早班", "WK-14"),
        });
        foreach (var l in plan.Links) _out.WriteLine($"  {l.Kind}　间隔 {l.GapH:0.##}");
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void H10_面链上的空档要算出来()
    {
        var plan = DayHandoffPlan.Build(new[]
        {
            T("a", ProcessType.Load, "主采面·东", 0, 3, "早班", "WK-10"),
            T("b", ProcessType.Load, "主采面·东", 5, 8, "早班", "WK-10"),   // 空 2 h
        });
        var ch = plan.Faces.Single();
        _out.WriteLine($"  {ch.Zone}　空档 {ch.IdleGapH:0.##} h　跨 {ch.ShiftCount} 班");
        Assert.Equal(2.0, ch.IdleGapH, 6);
        Assert.Equal(1, ch.ShiftCount);
    }

    // ── 空过防护 ─────────────────────────────────────────────────────────────

    [Fact]
    public void H11_空盘子不炸且要说明白()
    {
        var plan = DayHandoffPlan.Build(null);
        Assert.Empty(plan.Links);
        Assert.NotEmpty(plan.Notes);
        _out.WriteLine("  " + plan.Notes[0]);

        var p2 = DayHandoffPlan.Build(Array.Empty<ProductionTask>());
        Assert.Empty(p2.Links);
    }

    /// <summary>有采装没爆破要报出来 —— 可能吃的是存量爆堆，也可能是排产缺口，这一层分不出，但必须说。</summary>
    [Fact]
    public void H12_有采装没前序爆破要出提示()
    {
        var plan = DayHandoffPlan.Build(new[] { T("l", ProcessType.Load, "主采面·东", 0, 8) });
        _out.WriteLine("  " + string.Join(" │ ", plan.Notes));
        Assert.Contains(plan.Notes, n => n.Contains("没有前序爆破"));

        // 有爆破就不该再提
        var ok = DayHandoffPlan.Build(new[]
        {
            T("b", ProcessType.Blast, "主采面·东", 0, 1),
            T("l", ProcessType.Load, "主采面·东", 2, 8),
        });
        Assert.DoesNotContain(ok.Notes, n => n.Contains("没有前序爆破"));
    }

    /// <summary>
    /// H0 自检：把冲突判据关掉（间隔全部改成正的）必须让 H2/H5 真的变绿 ——
    /// 反过来说，上面那两条判据不是恒真的。
    /// </summary>
    [Fact]
    public void H0_自检_把时间改顺了冲突必须消失()
    {
        var bad = DayHandoffPlan.Build(new[]
        {
            T("b", ProcessType.Blast, "主采面·东", 5, 8),
            T("l", ProcessType.Load,  "主采面·东", 6, 15),
        });
        Assert.NotEmpty(bad.Conflicts);            // 前提：这组确实红

        var good = DayHandoffPlan.Build(new[]
        {
            T("b", ProcessType.Blast, "主采面·东", 5, 8),
            T("l", ProcessType.Load,  "主采面·东", 8, 15),
        });
        _out.WriteLine($"  改顺之后冲突 {good.Conflicts.Count()} 条");
        Assert.Empty(good.Conflicts);              // 改顺就必须绿 ⇒ 判据不是恒真
    }
}
