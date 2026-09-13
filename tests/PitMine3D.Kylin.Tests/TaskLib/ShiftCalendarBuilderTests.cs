// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/ShiftCalendarBuilderTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「生成整月班次日历」的判据（CB 组）。
///
/// <para><b>背景</b>：`shift_calendar` 此前<b>整张表一行都没有</b>（迁移里没有种子），
/// 而录入窗口<b>一次只存一天</b> —— 排满一个月要翻 26 次日期。于是没人排，
/// 于是逐日逐班永远切不出来（`ShiftPlanAssembler` 拒绝拿自然日顺延顶替，那条拒绝是对的）。</para>
///
/// <para><b>这一组守的三条</b>：作业日数是<b>数据不是规则</b>（来自逐月配置表）·
/// 已排过的日子<b>不覆盖</b> · 爆破班<b>一律留空</b>。</para>
/// </summary>
public class ShiftCalendarBuilderTests
{
    // ══════════════════════════════════════════════════════════════
    //  CB1 三班倒的形状
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// CB1 三班倒 = A/B/C 整天均分，且<b>只能是这三个名字</b>。
    /// <para>`shift_calendar` 上有 CHECK 约束 <c>shift IN ('A','B','C')</c> ——
    /// 写「早」会直接抛 SQLite Error 19，而那是运行时才炸。</para>
    /// </summary>
    [Fact]
    public void CB1_三班倒是ABC整天均分()
    {
        var r = ShiftCalendarBuilder.Build(2026, 8, shiftsPerDay: 3, RestRule.Continuous);

        Assert.Equal(31 * 3, r.Rows.Count);
        Assert.Equal(new[] { "A", "B", "C" },
                     r.Rows.Select(x => x.Shift).Distinct().OrderBy(x => x).ToArray());

        var day1 = r.Rows.Where(x => x.Date.Day == 1).OrderBy(x => x.Shift).ToList();
        Assert.Equal(new[] { "00:00", "08:00", "16:00" }, day1.Select(x => x.StartTime).ToArray());
    }

    /// <summary>CB1b 两班就是 00:00 / 12:00 —— 起始时刻是<b>算出来的</b>，不是写死的三档。</summary>
    [Fact]
    public void CB1b_两班是整天均分的两档()
    {
        Assert.Equal("00:00", ShiftCalendarBuilder.StartOf(0, 2));
        Assert.Equal("12:00", ShiftCalendarBuilder.StartOf(1, 2));
        Assert.Equal("08:00", ShiftCalendarBuilder.StartOf(1, 3));
    }

    /// <summary>CB1c 超过三班要夹住并说明 —— 库里只有三档，第四班写不进去。</summary>
    [Fact]
    public void CB1c_超过三班夹住并说明()
    {
        var r = ShiftCalendarBuilder.Build(2026, 8, shiftsPerDay: 4, RestRule.Continuous);
        Assert.Equal(3, r.Rows.Select(x => x.Shift).Distinct().Count());
        Assert.Contains(r.Notes, n => n.Contains("CHECK") || n.Contains("排不下第四班"));
    }

    // ══════════════════════════════════════════════════════════════
    //  CB2 作业日数是数据，不是规则
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// CB2 给了作业日数（逐月配置表）就<b>按它来</b>，休息日均匀摊。
    /// <para>2026-08 有 31 天、配置表给 25 天 ⇒ 排 25 天、休 6 天。</para>
    /// </summary>
    [Fact]
    public void CB2_作业日数取自配置表()
    {
        var r = ShiftCalendarBuilder.Build(2026, 8, 3, RestRule.Even, targetWorkdays: 25);

        int days = r.Rows.Select(x => x.Date.Date).Distinct().Count();
        Assert.Equal(25, days);
        Assert.Equal(6, r.RestDays.Count);
        Assert.Contains(r.Notes, n => n.Contains("逐月配置表") && n.Contains("不是这里编的"));
    }

    /// <summary>
    /// CB2b 休息日<b>均匀摊开，不在月底扎堆</b>。
    /// <para>简单写法（末尾连着休 6 天）在算术上一样是 25 个作业日 ——
    /// 而现场是最后一周全停。这条钉住分布，不只钉个数。</para>
    /// </summary>
    [Fact]
    public void CB2b_休息日不扎堆在月底()
    {
        var r = ShiftCalendarBuilder.Build(2026, 8, 3, RestRule.Even, targetWorkdays: 25);
        var rest = r.RestDays.Select(d => d.Day).OrderBy(x => x).ToList();

        // 6 个休息日铺在 31 天里 ⇒ 任意相邻两个之间不该超过 ~10 天，且不能全在下旬
        Assert.True(rest.First() <= 10, "第一个休息日太靠后：" + string.Join(",", rest));
        Assert.True(rest.Last() >= 22, "最后一个休息日太靠前：" + string.Join(",", rest));
        Assert.True(rest.Count(d => d > 25) <= 2, "休息日在月底扎堆：" + string.Join(",", rest));
    }

    /// <summary>
    /// CB2c 规则算出来的天数与配置表<b>对不上就说出来</b>，不偷偷凑。
    /// <para>月计划的日均量是按配置表那个数除出来的 —— 两个数不一致，日均量就不对。</para>
    /// </summary>
    [Fact]
    public void CB2c_规则与配置表对不上要说出来()
    {
        // 周日休 ⇒ 2026-08 有 5 个周日 ⇒ 26 个作业日，而配置表给 25
        var r = ShiftCalendarBuilder.Build(2026, 8, 3, RestRule.Sunday, targetWorkdays: 25);
        Assert.Equal(26, r.Rows.Select(x => x.Date.Date).Distinct().Count());
        Assert.Contains(r.Notes, n => n.Contains("配置表给的是 25") && n.Contains("以规则为准"));
    }

    /// <summary>CB2d 配置表给的天数多于本月天数 ⇒ 点名，并按全月排。</summary>
    [Fact]
    public void CB2d_作业日多于本月天数要点名()
    {
        var r = ShiftCalendarBuilder.Build(2026, 2, 3, RestRule.Even, targetWorkdays: 40);
        Assert.Equal(28, r.Rows.Select(x => x.Date.Date).Distinct().Count());
        Assert.Contains(r.Notes, n => n.Contains("多于本月天数"));
    }

    /// <summary>CB2e 均匀摊却没给作业日数 ⇒ 说明缺口并按全月排，<b>不猜一个天数</b>。</summary>
    [Fact]
    public void CB2e_没给作业日数时不猜()
    {
        var r = ShiftCalendarBuilder.Build(2026, 8, 3, RestRule.Even);
        Assert.Equal(31, r.Rows.Select(x => x.Date.Date).Distinct().Count());
        Assert.Contains(r.Notes, n => n.Contains("没给作业日数") && n.Contains("逐月配置表"));
    }

    // ══════════════════════════════════════════════════════════════
    //  CB3 不覆盖人排的班
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// CB3 库里已有的日子<b>原样保留</b>，只补缺，并报出保留了几天。
    /// <para>覆盖掉的话，人逐日改过的爆破班/天气/带班人会被一次点击抹平 ——
    /// 而生成出来的那份看着完全正常。</para>
    /// </summary>
    [Fact]
    public void CB3_已排过的日子不覆盖只补缺()
    {
        var had = new[] { new DateTime(2026, 8, 1), new DateTime(2026, 8, 2) };
        var r = ShiftCalendarBuilder.Build(2026, 8, 3, RestRule.Continuous, 0, had);

        Assert.Equal(2, r.KeptDays.Count);
        Assert.DoesNotContain(r.Rows, x => x.Date.Day <= 2);
        Assert.Equal(29 * 3, r.Rows.Count);
        Assert.Contains("已排过", r.Headline);
    }

    /// <summary>CB3b 整月都排过 ⇒ 一条都不生成，并明说"不用补"（不是失败）。</summary>
    [Fact]
    public void CB3b_整月排满时说不用补()
    {
        var had = Enumerable.Range(1, 31).Select(d => new DateTime(2026, 8, d)).ToList();
        var r = ShiftCalendarBuilder.Build(2026, 8, 3, RestRule.Continuous, 0, had);

        Assert.Empty(r.Rows);
        Assert.Contains("已经排满", r.Headline);
    }

    // ══════════════════════════════════════════════════════════════
    //  CB4 爆破班不许猜
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// CB4 <b>爆破班一律留空</b>。
    /// <para>哪一班放炮是逐日定的。猜一个出来会让那一班凭空少 1.5 小时清场工时，
    /// 而班表、达成度、工序量全都跟着偏 —— 每个数看着都正常。</para>
    /// </summary>
    [Fact]
    public void CB4_爆破班一律留空并说明()
    {
        var r = ShiftCalendarBuilder.Build(2026, 8, 3, RestRule.Continuous);
        Assert.All(r.Rows, x => Assert.False(x.IsBlastShift));
        Assert.Contains("爆破班", r.Headline);
        Assert.Contains("1.5", r.Headline);
    }

    // ══════════════════════════════════════════════════════════════
    //  边界
    // ══════════════════════════════════════════════════════════════

    /// <summary>CB5 年月不成立时不炸，且有话说。</summary>
    [Fact]
    public void CB5_年月不成立时不炸()
    {
        foreach (var (y, m) in new[] { (2026, 0), (2026, 13), (0, 8) })
        {
            var r = ShiftCalendarBuilder.Build(y, m, 3, RestRule.Continuous);
            Assert.False(r.Ok);
            Assert.False(string.IsNullOrWhiteSpace(r.Headline));
        }
    }

    /// <summary>CB5b 闰年二月按 29 天（`DaysInMonth` 不许自己算）。</summary>
    [Fact]
    public void CB5b_闰年二月是29天()
        => Assert.Equal(29, ShiftCalendarBuilder
                            .Build(2028, 2, 3, RestRule.Continuous)
                            .Rows.Select(x => x.Date.Date).Distinct().Count());
}
