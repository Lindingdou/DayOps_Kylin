// 忠实移植自原 PitMine3D Tests/Tests.PitMineApp/ConfirmToLedgerTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
using System;
using Xunit;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Tests.Synth;
using BlockModel = PitMine3D.Kylin.Tests.Synth.BlockModel;
using WorkLineGeometry = PitMine3D.Kylin.Cad.WorkLineSamples;
using MonthPeriod = PitMine3D.Kylin.Cad.Plan.MonthPeriod;
using ShortTermPlan = PitMine3D.Kylin.Cad.Plan.ShortTermPlan;
using DumpMode = PitMine3D.Kylin.Cad.DumpMode;
using DumpStripStore = PitMine3D.Kylin.UnitLedger.DumpStripStore;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 「确定入库」写月度计划台账时的年月换算判据。
///
/// <para><b>为什么这条最容易错</b>：<see cref="MonthPeriod.Month"/> 存的是<b>月序</b>不是月份 ——
/// <c>MinePlanImporter.cs:157</c> 填的是 <c>startMonth + em.Month - 1</c>，
/// 6 月起排 12 个月就得到 6…17，标签是 <c>M06</c>…<c>M17</c>。</para>
///
/// <para>两种错法都<b>不会抛任何异常</b>：
/// ① 当月份用 ⇒ M13 之后整段被丢掉（台账少了 5 个月，而界面说"已入库"）；
/// ② 对 12 取模不滚年 ⇒ 明年 1 月的量盖到今年 1 月上（两条记录都"存在"、数都"正常"）。
/// L2 和 L3 分别钉这两条。</para>
/// </summary>
public class ConfirmToLedgerTests
{
    // ═══ L1：普通月序 ═══
    [Theory]
    [InlineData(2026, 1, 2026, 1)]
    [InlineData(2026, 8, 2026, 8)]
    [InlineData(2026, 12, 2026, 12)]
    public void L1_月序在年内时年月不变(int baseY, int ord, int wantY, int wantM)
    {
        Assert.True(MonthlyStripSession.TrySplitYearMonth(baseY, ord, "", out int y, out int m));
        Assert.Equal(wantY, y);
        Assert.Equal(wantM, m);
    }

    // ═══ L2：跨年月序必须滚年，不能被丢掉 ═══
    [Theory]
    [InlineData(2026, 13, 2027, 1)]
    [InlineData(2026, 17, 2027, 5)]
    [InlineData(2026, 24, 2027, 12)]
    [InlineData(2026, 25, 2028, 1)]
    public void L2_跨年月序滚年而不是被丢掉(int baseY, int ord, int wantY, int wantM)
    {
        // 当月份用的那一版会在这里返回 false（13 > 12）⇒ 那几个月一个都写不进台账
        Assert.True(MonthlyStripSession.TrySplitYearMonth(baseY, ord, "", out int y, out int m));
        Assert.Equal(wantY, y);
        Assert.Equal(wantM, m);
    }

    // ═══ L3：滚年之后不能和年内那个月撞成同一条记录 ═══
    [Fact]
    public void L3_明年一月不会盖到今年一月()
    {
        Assert.True(MonthlyStripSession.TrySplitYearMonth(2026, 1, "", out int y1, out int m1));
        Assert.True(MonthlyStripSession.TrySplitYearMonth(2026, 13, "", out int y2, out int m2));

        Assert.Equal(m1, m2);            // 月份同为 1
        Assert.NotEqual(y1, y2);         // 但年份必须不同 —— 对 12 取模不滚年时这条红
        Assert.Equal(2027, y2);
    }

    // ═══ L4：月序没填时退到标签里的真年月 ═══
    [Theory]
    [InlineData("2027-01", 2027, 1)]
    [InlineData("2027年3月", 2027, 3)]
    [InlineData("2026/12", 2026, 12)]
    public void L4_月序缺失时认标签里的真年月(string label, int wantY, int wantM)
    {
        Assert.True(MonthlyStripSession.TrySplitYearMonth(2026, 0, label, out int y, out int m));
        Assert.Equal(wantY, y);
        Assert.Equal(wantM, m);
    }

    // ═══ L5：只有 M08 这种月序标签时也要认得（并滚年）═══
    [Theory]
    [InlineData("M08", 2026, 8)]
    [InlineData("M14", 2027, 2)]
    public void L5_月序标签也认得(string label, int wantY, int wantM)
    {
        Assert.True(MonthlyStripSession.TrySplitYearMonth(2026, 0, label, out int y, out int m));
        Assert.Equal(wantY, y);
        Assert.Equal(wantM, m);
    }

    // ═══ L6：定不出来就返回 false，不许猜 ═══
    [Theory]
    [InlineData(2026, 0, "")]
    [InlineData(2026, 0, "第三期")]
    [InlineData(2026, 0, "2027-13")]     // 13 月不是月份
    [InlineData(0, 5, "2027-01")]        // 没有基准年
    [InlineData(1899, 5, "")]            // 基准年不成立
    public void L6_定不出年月就拒绝(int baseY, int ord, string label)
    {
        Assert.False(MonthlyStripSession.TrySplitYearMonth(baseY, ord, label, out int y, out int m));
        Assert.Equal(0, y);
        Assert.Equal(0, m);   // 拒绝时不留半个值，免得调用方误用
    }
}
