// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/GanttShiftFilterTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Gantt;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 日甘特班次口径的判据（G 组）。
///
/// <para><b>这一组防的是"少了几根条，没有任何东西报错"</b>：
/// 盘子里总有一批任务的 <c>Shift</c> 是空的（排产没落班、跨班连续作业、临时补的活）。
/// 按 <c>t.Shift == 选中班</c> 直筛，它们<b>哪个班都进不去</b> ——
/// 切到早班没有、中班没有、夜班还是没有，只有「全天」才看得见。</para>
///
/// <para><b>最容易空过的写法</b>：算例里每条任务都带班次。
/// 那样"直筛"和"直筛 + 点名报空班次"出来的结果一模一样，判据无论实现走哪条路都绿。
/// 下面 G3 专门放一条没有班次的进去。</para>
/// </summary>
public class GanttShiftFilterTests
{
    private static ProductionTask T(string id, string shift, double vol = 1000)
        => new()
        {
            Id = id, Shift = shift, WorkZone = "采场1",
            Process = ProcessType.Load, TargetVolumeM3 = vol,
        };

    // ══════════════════════════════════════════════════════════════
    //  G1 全天是默认口径
    // ══════════════════════════════════════════════════════════════

    /// <summary>G1 空串 / 「全部」都表示全天，一条都不筛。</summary>
    [Fact]
    public void G1_全天不筛()
    {
        var all = new[] { T("a", "早"), T("b", "中"), T("c", "") };

        foreach (var word in new[] { "", "   ", ShiftScope.All })
        {
            var r = GanttShiftFilter.Apply(all, word);
            Assert.False(r.Filtered);
            Assert.Equal(3, r.Tasks.Count);
            Assert.Equal(0, r.DroppedNoShift);
            Assert.Contains("全天", r.Caption);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  G2 筛得对
    // ══════════════════════════════════════════════════════════════

    /// <summary>G2 选中某班只留该班，其余班的条数报出来。</summary>
    [Fact]
    public void G2_只留选中班并报出隐藏了多少()
    {
        var r = GanttShiftFilter.Apply(new[] { T("a", "早"), T("b", "中"), T("c", "夜") }, "早");

        Assert.True(r.Filtered);
        Assert.Equal("a", Assert.Single(r.Tasks).Id);
        Assert.Equal(2, r.DroppedOtherShift);
        Assert.Equal(0, r.DroppedNoShift);
        Assert.Contains("其余班 2 项已隐藏", r.Caption);
    }

    // ══════════════════════════════════════════════════════════════
    //  G3 没有班次归属的任务 —— 这个类存在的理由
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// G3 <b>Shift 为空的任务哪个班都进不去，必须单独点名</b>。
    /// <para>把它们并进「其余班已隐藏」里，读的人会以为切到别的班就能看到 —— 切过去还是没有。</para>
    /// </summary>
    [Fact]
    public void G3_没有班次归属的任务单独点名()
    {
        var all = new[] { T("a", "早"), T("b", ""), T("c", "   "), T("d", "中") };
        var r = GanttShiftFilter.Apply(all, "早");

        Assert.Equal(2, r.DroppedNoShift);              // b、c
        Assert.Equal(1, r.DroppedOtherShift);           // d
        Assert.Contains("没有班次归属", r.Caption);
        Assert.Contains("哪个班都进不去", r.Caption);
    }

    /// <summary>
    /// G3b <b>换个班也一样看不到</b> —— 这条才真正证明"哪个班都进不去"。
    /// <para>只在一个班上断言的话，"它恰好不属于这个班"和"它属于任何班都不算"分不开。</para>
    /// </summary>
    [Fact]
    public void G3b_切到任何一个班都看不到无班次的任务()
    {
        var all = new[] { T("a", "早"), T("b", "中"), T("x", "") };

        foreach (var shift in new[] { "早", "中", "夜" })
        {
            var r = GanttShiftFilter.Apply(all, shift);
            Assert.DoesNotContain(r.Tasks, t => t.Id == "x");
            Assert.Equal(1, r.DroppedNoShift);
        }

        // 只有全天看得见 —— 这半句让上面三次断言不至于是"它根本不在盘子里"
        Assert.Contains(GanttShiftFilter.Apply(all, "").Tasks, t => t.Id == "x");
    }

    // ══════════════════════════════════════════════════════════════
    //  G4 筛出来的空 ≠ 今天没活
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// G4 <b>这一班一条都没有时必须说出来</b>。
    /// <para>空甘特看着就是「今天没排活」。而实际上全天有 3 项，只是都不在这一班。</para>
    /// </summary>
    [Fact]
    public void G4_筛出空班时说清楚不是今天没活()
    {
        var r = GanttShiftFilter.Apply(new[] { T("a", "早"), T("b", "早"), T("c", "中") }, "夜");

        Assert.Empty(r.Tasks);
        Assert.Contains("一条任务都没有", r.Caption);
        Assert.Contains("全天共 3 项", r.Caption);
        Assert.Contains("不是「今天没排活」", r.Caption);
    }

    // ══════════════════════════════════════════════════════════════
    //  G5 口径要挂出来
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// G5 筛过之后要说明表头统计也按同一把尺子重算。
    /// <para>「计划 3.2 万m³」是全天还是一个班，光看这个数分不出来 —— 必须写在旁边。</para>
    /// </summary>
    [Fact]
    public void G5_筛过之后要挂出统计口径()
    {
        var r = GanttShiftFilter.Apply(new[] { T("a", "早") }, "早");
        Assert.Contains("表头", r.Caption);
        Assert.Contains("同一把尺子", r.Caption);
    }

    /// <summary>G6 null / 空盘 / null 元素都不炸。</summary>
    [Fact]
    public void G6_空盘不炸()
    {
        Assert.Empty(GanttShiftFilter.Apply(null, "早").Tasks);
        Assert.Empty(GanttShiftFilter.Apply(Array.Empty<ProductionTask>(), "").Tasks);
        Assert.Single(GanttShiftFilter.Apply(new ProductionTask?[] { null, T("a", "早") }!, "早").Tasks);
    }

    /// <summary>
    /// G7 筛完的量之和 = 该班任务的量之和 —— <b>表头统计与条形同源</b>的算术侧。
    /// <para>结构上是靠"先筛再造模型"保证的；这里把它钉住，免得哪天有人在造模型之后再筛一次。</para>
    /// </summary>
    [Fact]
    public void G7_筛完的量之和只含该班()
    {
        var r = GanttShiftFilter.Apply(
            new[] { T("a", "早", 1000), T("b", "中", 5000), T("c", "", 9000) }, "早");

        Assert.Equal(1000, r.Tasks.Sum(t => t.TargetVolumeM3));
    }
}
