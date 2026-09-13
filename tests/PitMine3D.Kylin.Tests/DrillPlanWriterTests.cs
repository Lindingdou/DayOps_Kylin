// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/DrillPlanWriterTests.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Views.Plan;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Data.Entities;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 「排产结果 + 工序作业区 → 穿孔作业计划」的判据（D 组）。
///
/// <para><b>这一环之前没有生产者</b>：`drill_plan`（V044）建了表，可只有人手工往里录。
/// 台账模式下钻机一条任务都排不出来 —— 甘特里没有穿孔条、工序进度的穿孔一栏恒 0%、
/// 「钻爆计划衔接」拿不到穿孔窗口。而 `EquipmentAssigner` 早就把
/// 「哪台钻机、第几个工日、打哪个单元、多少控制方量」算出来了，只是没人把它落到表上。</para>
///
/// <para><b>喂的是合成算例</b>（走 <c>BuildRows</c>，不碰数据库）：
/// `Write` 的每一步输入都来自台账，裸台架里那些路径一律静默走兜底。</para>
/// </summary>
public class DrillPlanWriterTests
{
    private const string M = "2026-08";

    private static MachineAssignment Drill(string machine, string unit, int d0, int d1, double m3 = 5000)
        => new()
        {
            MachineId = machine, UnitId = unit, Role = MachineRole.Drill,
            StartDay = d0, EndDay = d1, AssignedM3 = m3,
        };

    private static List<DateTime> Days(params int[] dayOfMonth)
        => dayOfMonth.Select(d => new DateTime(2026, 8, d)).ToList();

    /// <summary>三班制的一天：00:00 / 08:00 / 16:00 —— 班长推出来是 8h，末班收班跨零点。</summary>
    private static (string, string, string) ThreeShift(DateTime _) => ("00:00", "23:59", "末班跨零点");

    private static (string, string, string) DayShift(DateTime _) => ("08:00", "16:00", "");

    private static Dictionary<string, (string? Zone, double? BenchZ)> Zones(
        params (string Unit, string? Zone, double? Z)[] items)
        => items.ToDictionary(x => x.Unit, x => (x.Zone, x.Z), StringComparer.OrdinalIgnoreCase);

    // ══════════════════════════════════════════════════════════════
    //  D1 延米与孔数留 NULL，不写 0
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// D1 <b>孔数与延米一律 NULL，不是 0</b>。
    /// <para>孔网参数（孔距/排距/超深）在 PlanLib 的工艺链里，本模块引不到。
    /// <b>「免爆」与「算不出」的延米都是 0，报表上一模一样</b> —— 写 0 等于把缺口藏起来。
    /// V044 的这两列本就可空，NULL 才是「未录」的正确表达。</para>
    /// </summary>
    [Fact]
    public void D1_孔数与延米是NULL不是零()
    {
        var res = new DrillPlanWriteResult();
        var rows = DrillPlanWriter.BuildRows(
            new[] { Drill("ZJ-01", "4-B1-P1", 1, 2) }, Days(3, 4), DayShift,
            Zones(("4-B1-P1", "面A", 1180.0)), M, res);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Null(r.HoleCount);            // ★ 不是 0
            Assert.Null(r.HoleLengthM);
        });
        Assert.Equal(2, res.NoHoleParams);
    }

    /// <summary>D1b 「算不出」这件事要写进每一行的备注，别让人以为表里就是没有孔。</summary>
    [Fact]
    public void D1b_算不出要写进备注()
    {
        var res = new DrillPlanWriteResult();
        var rows = DrillPlanWriter.BuildRows(
            new[] { Drill("ZJ-01", "U1", 1, 1) }, Days(3), DayShift,
            Zones(("U1", "面A", 1180.0)), M, res);

        Assert.Contains("孔数与延米未录", rows[0].Note);
        Assert.Contains("控制方量", rows[0].Note);       // 量口径写明白，不与原位实方混
    }

    // ══════════════════════════════════════════════════════════════
    //  D2 工日 → 日期
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// D2 工日序号按<b>作业日清单</b>换成日期，<b>不是自然日顺延</b>。
    /// <para>作业日 3、4、7 号（5、6 号不上班）—— 第 3 个工日必须是 7 号，不是 5 号。
    /// 顺延出来的日期看着完全正常，而钻机会被排到本来不上班的那天。</para>
    /// </summary>
    [Fact]
    public void D2_工日按作业日清单换日期不是自然日顺延()
    {
        var res = new DrillPlanWriteResult();
        var rows = DrillPlanWriter.BuildRows(
            new[] { Drill("ZJ-01", "U1", 1, 3) }, Days(3, 4, 7), DayShift,
            Zones(("U1", "面A", 1180.0)), M, res);

        Assert.Equal(new[] { "2026-08-03", "2026-08-04", "2026-08-07" },
                     rows.Select(r => r.PlanDate).ToArray());
    }

    /// <summary>D2b 一笔跨几个工日 ⇒ 每个工日各一条（V044 主键是 设备 × 日期 × 起始时刻）。</summary>
    [Fact]
    public void D2b_跨工日的一笔拆成逐日多条()
    {
        var res = new DrillPlanWriteResult();
        var rows = DrillPlanWriter.BuildRows(
            new[] { Drill("ZJ-01", "U1", 2, 4) }, Days(1, 2, 3, 4, 5), DayShift,
            Zones(("U1", "面A", 1180.0)), M, res);

        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows.Select(r => r.PlanDate).Distinct().Count());
        Assert.All(rows, r => Assert.Equal("ZJ-01", r.EquipmentId));
    }

    /// <summary>D2c 工日序号越界时跳过并计数，不去 days[-1] 炸掉。</summary>
    [Fact]
    public void D2c_工日越界跳过而不炸()
    {
        var res = new DrillPlanWriteResult();
        var rows = DrillPlanWriter.BuildRows(
            new[] { Drill("ZJ-01", "U1", 0, 3) }, Days(1, 2), DayShift,
            Zones(("U1", "面A", 1180.0)), M, res);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2, res.Skipped);          // 第 0 天与第 3 天
    }

    // ══════════════════════════════════════════════════════════════
    //  D3 时窗
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// D3 <b>班长是推出来的</b>（班次日历只有开班时刻），三班 00/08/16 推出 8h，
    /// 末班收班跨零点 ⇒ 截到 23:59。
    /// </summary>
    [Theory]
    [InlineData(new[] { 0.0, 8.0, 16.0 }, 8.0)]      // 常规三班
    [InlineData(new[] { 8.0, 20.0 }, 12.0)]          // 两班倒
    [InlineData(new[] { 6.0, 14.0, 22.0 }, 8.0)]     // 早六点起
    public void D3_班长按开班间隔推(double[] starts, double want)
    {
        var gaps = Enumerable.Range(1, starts.Length - 1).Select(i => starts[i] - starts[i - 1]).ToList();
        Assert.Equal(want, DrillPlanWriter.Median(gaps), 6);
    }

    /// <summary>
    /// D3b 时刻解析：<b>解不出返回 -1，不返回 0</b> —— 0 点是合法开班时刻。
    /// <para>拿 0 当"解不出"的话，所有没填时刻的班都会变成"零点开班"，而每个数看上去都正常。</para>
    /// </summary>
    [Theory]
    [InlineData("00:00", 0.0)]
    [InlineData("08:30", 8.5)]
    [InlineData("23:59", 23.983333)]
    [InlineData("", -1.0)]
    [InlineData(null, -1.0)]
    [InlineData("乱写", -1.0)]
    [InlineData("25:00", -1.0)]
    public void D3b_时刻解不出返回负一不返回零(string? text, double want)
        => Assert.Equal(want, DrillPlanWriter.ParseHour(text), 4);

    /// <summary>D3c 小时数 → HH:mm 往返一致，且不会写出 24:00（V044 要同日内）。</summary>
    [Fact]
    public void D3c_时刻格式化不会写出二十四点()
    {
        Assert.Equal("00:00", DrillPlanWriter.Hhmm(0));
        Assert.Equal("08:30", DrillPlanWriter.Hhmm(8.5));
        Assert.Equal("23:59", DrillPlanWriter.Hhmm(23 + 59.0 / 60));
        Assert.Equal("23:59", DrillPlanWriter.Hhmm(24.5));      // 越界夹回，不写 24:xx
    }

    /// <summary>D3d 那一天排不出时窗（日历里没有开班时刻）⇒ 跳过并计数，不写一条起止都空的计划。</summary>
    [Fact]
    public void D3d_排不出时窗的那天跳过()
    {
        var res = new DrillPlanWriteResult();
        var rows = DrillPlanWriter.BuildRows(
            new[] { Drill("ZJ-01", "U1", 1, 2) }, Days(3, 4),
            d => d.Day == 4 ? ("", "", "") : ("08:00", "16:00", ""),
            Zones(("U1", "面A", 1180.0)), M, res);

        Assert.Single(rows);
        Assert.Equal(1, res.Skipped);
    }

    // ══════════════════════════════════════════════════════════════
    //  D4 在哪儿 —— 穿孔区，不是采装区
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// D4 待爆区取自<b>穿孔工序区</b>，台阶标高取那块地的环 Z。
    /// <para>穿孔区是采装区沿推进方向**前推超前期**的那一段地 ——
    /// 拿采装区的位置写钻机，钻机就被派到了电铲脚下，而报表上每个数都正常。</para>
    /// </summary>
    [Fact]
    public void D4_待爆区取自穿孔工序区()
    {
        var res = new DrillPlanWriteResult();
        var rows = DrillPlanWriter.BuildRows(
            new[] { Drill("ZJ-01", "U1", 1, 1) }, Days(3), DayShift,
            Zones(("U1", "面A·穿孔", 1225.5)), M, res);

        Assert.Equal("面A·穿孔", rows[0].Zone);
        Assert.Equal(1225.5, rows[0].BenchElevationM);
        Assert.Equal(0, res.NoZone);
    }

    /// <summary>
    /// D4b 配不上穿孔区时 zone 留空并<b>计数 + 写进备注</b>，不拿别的区顶上。
    /// <para>「钻爆计划衔接」按 待爆区 + 日期 对照穿孔与炮次，zone 空着就对不上 ——
    /// 那一段接续从此看不见，而计划本身看着是齐的。</para>
    /// </summary>
    [Fact]
    public void D4b_配不上穿孔区时留空并点名()
    {
        var res = new DrillPlanWriteResult();
        var rows = DrillPlanWriter.BuildRows(
            new[] { Drill("ZJ-01", "U9", 1, 1) }, Days(3), DayShift,
            Zones(("U1", "面A·穿孔", 1180.0)), M, res);

        Assert.Equal("", rows[0].Zone);
        Assert.Null(rows[0].BenchElevationM);          // ★ 不是 0（0 是合法标高）
        Assert.Equal(1, res.NoZone);
        Assert.Contains("没配上穿孔工序区", rows[0].Note);
    }

    /// <summary>
    /// D4c 环解不出 Z 时台阶标高是 <b>null 不是 0</b>。
    /// <para>0 是合法标高 —— 用它当"没录"，所有解不出的行都会变成"零米台阶"。</para>
    /// </summary>
    [Fact]
    public void D4c_解不出的标高是null不是零()
    {
        Assert.Null(DrillPlanWriter.ParseAvgZ(null));
        Assert.Null(DrillPlanWriter.ParseAvgZ("[]"));
        Assert.Null(DrillPlanWriter.ParseAvgZ("[0,0,1200]"));            // 不足 3 个点
        Assert.Equal(1200, DrillPlanWriter.ParseAvgZ("[0,0,1200,10,0,1200,10,10,1200]")!.Value, 6);
        // ★ 真的 0 米要如实返回 0，而不是被当成"解不出"
        Assert.Equal(0, DrillPlanWriter.ParseAvgZ("[0,0,0,10,0,0,10,10,0]")!.Value, 6);
    }

    // ══════════════════════════════════════════════════════════════
    //  D5 备注记账
    // ══════════════════════════════════════════════════════════════

    /// <summary>D5 每一行都记着期次 / 第几工日 / 单元号 —— 三个月后问「这条哪来的」答案得在库里。</summary>
    [Fact]
    public void D5_每行都记着期次工日与单元()
    {
        var res = new DrillPlanWriteResult();
        var rows = DrillPlanWriter.BuildRows(
            new[] { Drill("ZJ-01", "4-B1-P1", 2, 2) }, Days(3, 4, 5), DayShift,
            Zones(("4-B1-P1", "面A", 1180.0)), M, res);

        Assert.Contains("2026-08", rows[0].Note);
        Assert.Contains("第 2/3 工日", rows[0].Note);
        Assert.Contains("4-B1-P1", rows[0].Note);
    }

    /// <summary>D5b 状态一律「计划」——它是计划表，不是已发生的事实（那是 blast_event）。</summary>
    [Fact]
    public void D5b_状态是计划不是完成()
    {
        var res = new DrillPlanWriteResult();
        var rows = DrillPlanWriter.BuildRows(
            new[] { Drill("ZJ-01", "U1", 1, 1) }, Days(3), ThreeShift,
            Zones(("U1", "面A", 1180.0)), M, res);
        Assert.Equal("计划", rows[0].Status);
    }
}
