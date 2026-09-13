// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/ShiftDutyWindowTests.cs（逐行对应；仅命名空间适配）
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
/// 「班组作业推演」窗口的纯核判据（V 组）。
///
/// <para><b>这一组防的是"算对了但显示成另一回事"</b>：`ShiftInference` 那 20 条判据
/// 管的是算得对不对，这一组管的是<b>算完之后有没有被摆成一个假象</b> ——
/// 四种量口径并成一列、削峰欠量混进已排量、NaN 显示成 0。
/// 三者都不会让任何数字算错，只会让读的人得出相反的结论。</para>
///
/// <para><b>最容易空过的写法</b>：算例里只放一种量口径。那样"并成一列"和"分列"
/// 出来的表长得一模一样，判据无论实现走哪条路都绿。下面凡是判口径的，
/// 算例里<b>一定放两种以上</b>。</para>
/// </summary>
public class ShiftDutyWindowTests
{
    private static ShiftDuty D(string machine, double vol, string basis,
                              double cap = double.NaN, double capped = 0,
                              MachineRole role = MachineRole.Excavate,
                              string date = "2026-08-01", string shift = "早",
                              bool blast = false)
        => new()
        {
            Date = date, Shift = shift, StartTime = "08:00", EndTime = "16:00",
            EffectiveHours = 8, IsBlastShift = blast,
            MachineId = machine, Role = role, UnitId = "U-1",
            VolumeM3 = vol, Basis = basis, CapM3 = cap, CappedM3 = capped,
        };

    // ══════════════════════════════════════════════════════════════
    //  V1 能力核对：NaN ≠ 0
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// V1 <b>「排产没给台效」与「上限是 0」必须是两句话</b>。
    /// <para>两者在结果上都是不削峰，只有 NaN 分得开。把 NaN 显示成 0，
    /// 每一班都会被读成"超能力作业"。</para>
    /// </summary>
    [Fact]
    public void V1_没给台效与上限为零说成两句话()
    {
        string noRate = ShiftDutyWindow.CapText(D("WK-1", 1000, "原位实方"));            // CapM3 = NaN
        string zero = ShiftDutyWindow.CapText(D("WK-2", 0, "原位实方", cap: 0));

        Assert.Contains("没给台效", noRate);
        Assert.DoesNotContain("0", noRate);          // 不许把"没给"写成一个数
        Assert.NotEqual(noRate, zero);
        Assert.Contains("上限", zero);
    }

    /// <summary>V1b 削过峰的那一班要把削掉多少说出来 —— 只说"已削峰"没法对账。</summary>
    [Fact]
    public void V1b_削峰要报出削掉多少()
    {
        string t = ShiftDutyWindow.CapText(D("WK-1", 800, "原位实方", cap: 800, capped: 200));
        Assert.Contains("削峰", t);
        Assert.Contains("200", t);
        Assert.Contains("800", t);
    }

    // ══════════════════════════════════════════════════════════════
    //  V2 量口径不许并列
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// V2 <b>四种口径各自成行，不出现合计</b>。
    /// <para>算例里放三种口径 —— 只放一种的话"分列"和"并列"出来的表一模一样，
    /// 这条判据就空过了。</para>
    /// </summary>
    [Fact]
    public void V2_不同量口径各自成行且不出现合计()
    {
        var res = new ShiftInferenceResult
        {
            Duties =
            {
                D("WK-1", 1000, "原位实方"),
                D("ZJ-1", 3000, "控制方量", role: MachineRole.Drill),
                D("TL-1", 500, "排弃占容", role: MachineRole.Dump),
            },
        };

        var byBasis = res.ByBasis.ToList();
        Assert.Equal(3, byBasis.Count);
        Assert.Equal(1000, byBasis.Single(x => x.Basis == "原位实方").M3);
        Assert.Equal(3000, byBasis.Single(x => x.Basis == "控制方量").M3);

        // CSV 里也不许出现一个把三者加起来的数
        string csv = ShiftDutyWindow.ToCsv(res);
        Assert.DoesNotContain("4500", csv);
        Assert.DoesNotContain("合计", csv);
    }

    // ══════════════════════════════════════════════════════════════
    //  V3 削峰欠量单列
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// V3 <b>排不下的量单独成段，不混进明细</b>。
    /// <para>混进明细里会被"对这一列求和"吃掉 —— 于是削峰等于悄悄少摊，
    /// 而每一行看着都对。</para>
    /// </summary>
    [Fact]
    public void V3_削峰欠量单独成段不混进明细()
    {
        var res = new ShiftInferenceResult
        {
            Duties = { D("WK-1", 800, "原位实方", cap: 800, capped: 200) },
            Shortfalls = { ("原位实方", 200, "日台效不够") },
        };

        string csv = ShiftDutyWindow.ToCsv(res);
        int head = csv.IndexOf("排不下", StringComparison.Ordinal);
        Assert.True(head > 0, "欠量段没出现");

        // 明细段（欠量段之前）里只有一条数据行 —— 欠量没被当成第二条作业塞进去
        string detail = csv[..head];
        int dataRows = detail.Split('\n').Count(l => l.StartsWith("2026-08-01", StringComparison.Ordinal));
        Assert.Equal(1, dataRows);

        Assert.Equal(200, res.ShortfallOf("原位实方"));
        Assert.Equal(0, res.ShortfallOf("控制方量"));      // 不许串口径
    }

    /// <summary>
    /// V3b <b>某个口径一条都没排上、却有欠量时不能整段消失</b>。
    /// <para>按已排口径循环着摆欠量的话，「一方都没排上」这种最该看见的情况反而不显示。</para>
    /// </summary>
    [Fact]
    public void V3b_一条都没排上的口径其欠量仍要出现()
    {
        var res = new ShiftInferenceResult
        {
            Duties = { D("WK-1", 1000, "原位实方") },
            Shortfalls = { ("控制方量", 5000, "本期没有钻机可用") },
        };

        Assert.DoesNotContain(res.ByBasis, x => x.Basis == "控制方量");   // 已排里确实没有它
        string csv = ShiftDutyWindow.ToCsv(res);
        Assert.Contains("控制方量", csv);
        Assert.Contains("5000", csv);
        Assert.Contains("本期没有钻机可用", csv);
    }

    // ══════════════════════════════════════════════════════════════
    //  V4 行的内容
    // ══════════════════════════════════════════════════════════════

    /// <summary>V4 爆破班要标出来并把扣掉的清场工时说清楚 —— 不然那一班的工时看着无缘无故短一截。</summary>
    [Fact]
    public void V4_爆破班标出来并说明扣了清场()
    {
        var rows = ShiftDutyWindow.BuildRows(new ShiftInferenceResult
        {
            Duties = { D("WK-1", 700, "原位实方", blast: true) },
        });

        var r = Assert.Single(rows);
        Assert.Contains("爆破班", r.班次);
        Assert.Contains("清场", r.工时);
    }

    /// <summary>
    /// V4b 角色名走 <see cref="EquipmentAssigner.RoleName"/> 那一份，<b>窗口里不另写一份 switch</b>。
    /// <para>两份各自正确也证明不了一致 —— 加一个角色时只会改到其中一份，
    /// 而漏掉的那一份会安静地显示「其他」。这里把同一个期望值钉在调用侧。</para>
    /// </summary>
    [Fact]
    public void V4b_角色名与排产同一份()
    {
        foreach (var role in Enum.GetValues<MachineRole>())
        {
            var rows = ShiftDutyWindow.BuildRows(new ShiftInferenceResult
            { Duties = { D("M-1", 1, "原位实方", role: role) } });
            Assert.Equal(EquipmentAssignResult.RoleName(role), rows[0].角色);
        }
    }

    /// <summary>V4c 行按 日期 → 时段 → 设备 排，不按插入序（插入序是排产内部的顺序，人读不了）。</summary>
    [Fact]
    public void V4c_按日期时段设备排序()
    {
        var rows = ShiftDutyWindow.BuildRows(new ShiftInferenceResult
        {
            Duties =
            {
                D("WK-2", 1, "原位实方", date: "2026-08-03"),
                D("WK-1", 1, "原位实方", date: "2026-08-01"),
            },
        });
        Assert.Equal("2026-08-01", rows[0].日期);
    }

    // ══════════════════════════════════════════════════════════════
    //  V5 边界
    // ══════════════════════════════════════════════════════════════

    /// <summary>V5 空结果 / null 不炸，且 CSV 至少留下表头（空文件会被当成"导出失败"）。</summary>
    [Fact]
    public void V5_空结果不炸且留表头()
    {
        Assert.Empty(ShiftDutyWindow.BuildRows(null));
        Assert.Empty(ShiftDutyWindow.BuildRows(new ShiftInferenceResult()));

        string csv = ShiftDutyWindow.ToCsv(null);
        Assert.Contains("日期", csv);
        Assert.Contains("量口径", csv);
    }

    /// <summary>
    /// V5b CSV 表头写明<b>「推演」</b>—— 这份表不是实绩，不得回写台账。
    /// <para>一张没有标注的 CSV 转手几次就会被当成实测数据填进 production_record。</para>
    /// </summary>
    [Fact]
    public void V5b_表头标明是推演不是实绩()
    {
        Assert.Contains("推演", ShiftDutyWindow.ToCsv(new ShiftInferenceResult
        { Duties = { D("WK-1", 100, "原位实方") } }));
    }

    /// <summary>V5c 含逗号/引号的字段要转义，否则一个带逗号的去向码会把整行列错位。</summary>
    [Fact]
    public void V5c_逗号与引号要转义()
    {
        var d = D("WK-1", 100, "原位实方");
        d.UnitId = "南排, 二平盘";
        string csv = ShiftDutyWindow.ToCsv(new ShiftInferenceResult { Duties = { d } });
        Assert.Contains("\"南排, 二平盘\"", csv);
    }
}
