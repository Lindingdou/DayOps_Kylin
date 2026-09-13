// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/DayCapacityBudgetTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 当日能力预算 + 编制配置新增锚点（面日产能工时 / 交接班损失）的判据。
///
/// <para><b>这组要挡的三类事</b>：</para>
/// <list type="number">
/// <item><b>预算变成第二套口径</b>：<see cref="DayCapacityBudget"/> 与
/// <see cref="TaskExploder"/> 各算各的，界面说"排得下"、装箱报欠产。B1 把两侧钉在
/// 同一个写死的期望值上（见 [[mirror-impl-cross-check]]：两边各测各的证明不了一致）。</item>
/// <item><b>逐面账被塌成总账</b>：A 面闲 3000、B 面欠 3000，总账"刚好排满"，
/// 实际一台铲晒太阳、另一台干不完。B2 造的就是这个盘子。</item>
/// <item><b>加了锚点但没接上</b>：字段加进去容易，引擎忘了读就是白加，而界面照样有模有样。
/// 每条都成对写 —— 开闸要变、关闸必须真的变回去（[[incline-shape-guards]] 的 F0）。</item>
/// </list>
/// </summary>
public class DayCapacityBudgetTests
{
    // ── 夹具 ─────────────────────────────────────────────────────────────────
    //
    //  三班 00–08 / 08–16 / 16–24，无检修无爆破。交接班损失缺省 0.5h/班、只扣非首班，
    //  故每台设备当日有效工时 = 8 + 7.5 + 7.5 = 23h。下面所有期望值都由这个 23 推出来，
    //  写成算式而不是光秃秃的数字：口径改了要一眼看得出改的是哪一项。

    private const double DayHours = 23.0;   // = 8 + (8−0.5) + (8−0.5)

    private static ExploderConfig Plate(params (string Zone, double CapH, double Target)[] faces)
    {
        var cfg = new ExploderConfig
        {
            DateLabel = "2026-08-20 周四",
            IdPrefix = "T0820",
            EnforceMassBalance = false,   // 本组只判能力预算，不牵采排守恒
            Shifts =
            {
                new ShiftWindow("早班", 0, 8),
                new ShiftWindow("中班", 8, 16),
                new ShiftWindow("夜班", 16, 24),
            },
        };
        foreach (var (zone, capH, target) in faces)
            cfg.Faces.Add(new FaceInput
            {
                Zone = zone,
                Process = ProcessType.Load,
                Material = "岩",
                DayTargetM3 = target,
                Group = new EquipmentGroup { MainEquipment = "WK-" + zone, GroupCapacityM3PerH = capH },
            });
        return cfg;
    }

    private static double ShortfallReportedByExploder(ExploderResult r)
        => r.Violations.Where(v => v.Code == ViolationCodes.DayShortfall)
                       .Sum(v => ParseM3(v.Message));

    /// <summary>从「…欠 1234 m³ 需回摊次日」里取那个数。取不到返回 0（B1 会因此红，正是要的）。</summary>
    private static double ParseM3(string msg)
    {
        int i = msg.IndexOf('欠');
        if (i < 0) return 0;
        var digits = new string(msg[(i + 1)..].TakeWhile(c => char.IsDigit(c) || c == ' ').ToArray()).Trim();
        return double.TryParse(digits, out double d) ? d : 0;
    }

    // ── B1：预算欠产 == 装箱欠产（同一笔账，不是另算一遍）────────────────────
    //
    //  期望值写死在两边：能力 = 100 × 23h = 2300；目标 3000 ⇒ 欠 700。
    //  任一侧（预算的 WorkWindowCalc 口径 / 装箱的 slot 口径）改了，这条立刻红。
    [Fact]
    public void B1_预算欠产与装箱欠产是同一笔账()
    {
        var cfg = Plate(("东", 100, 3000));

        var (load, _) = DayCapacityBudget.Of(cfg);

        Assert.Equal(100 * DayHours, load.CapacityM3, 1);        // 2300
        Assert.Equal(3000 - 100 * DayHours, load.ShortM3, 1);    // 700 ← 写死的期望值

        // 装箱那一侧独立再报一次，两个数必须相等
        double byExploder = ShortfallReportedByExploder(TaskExploder.Explode(Plate(("东", 100, 3000))));
        Assert.True(byExploder > 0, "装箱本该报「当日欠产」，一条都没有 ⇒ 判据取数的路子断了，不是真的没欠");
        Assert.Equal(load.ShortM3, byExploder, 0);

        // 关闸：目标缩到能力以内，两侧都必须归零（否则上面那条恒真）
        var fit = Plate(("东", 100, 2000));
        Assert.Equal(0, DayCapacityBudget.Of(fit).Load.ShortM3, 1);
        Assert.Equal(0, ShortfallReportedByExploder(TaskExploder.Explode(Plate(("东", 100, 2000)))), 0);
    }

    // ── B2：能力不在面之间流动 —— 总账平不等于排得下 ──────────────────────────
    [Fact]
    public void B2_一面闲一面欠时总账会骗人()
    {
        // A：能力 2300、目标 300 ⇒ 闲 2000。B：能力 2300、目标 4300 ⇒ 欠 2000。
        // 全矿总能力 4600 = 全矿总目标 4600，"总账刚好排满"——而实际两台铲一个闲一个干不完。
        var cfg = Plate(("A", 100, 300), ("B", 100, 4300));

        var (load, _) = DayCapacityBudget.Of(cfg);

        Assert.Equal(load.TargetM3, load.CapacityM3, 1);   // 总账确实是平的
        Assert.Equal(2000, load.ShortM3, 1);               // 逐面账不平
        Assert.Equal(2000, load.SlackM3, 1);
        Assert.Equal(1, load.ShortFaces);

        // 抬头必须把这件事说出来 —— 数对了但话说反了，人照样会照总账做决定
        string head = DayCapacityBudget.Headline(load, reshuffled: false);
        Assert.Contains("排不下", head);
        Assert.Contains("闲", head);
    }

    // ── B3：采装与排土分列，永不相加（实方 vs 占容方）────────────────────────
    [Fact]
    public void B3_采装与排土的方量不混线()
    {
        var cfg = Plate(("采装东", 100, 1000));
        cfg.Faces.Add(new FaceInput
        {
            Zone = "内排场",
            Process = ProcessType.Dump,
            Material = "岩",
            DayTargetM3 = 800,
            Group = new EquipmentGroup { MainEquipment = "TY-01", GroupCapacityM3PerH = 60 },
        });

        var (load, dump) = DayCapacityBudget.Of(cfg);

        Assert.Equal(1, load.FaceCount);
        Assert.Equal(1, dump.FaceCount);
        Assert.Equal(1000, load.TargetM3, 1);              // 排土那 800 没混进来
        Assert.Equal(800, dump.TargetM3, 1);
        Assert.Equal(60 * DayHours, dump.CapacityM3, 1);

        // 穿孔/检修不经装箱切分，一条都不许进预算
        cfg.Drills.Add(new DrillInput { EquipId = "KQ-01", Zone = "采装东", Start = 0, End = 8 });
        var (load2, dump2) = DayCapacityBudget.Of(cfg);
        Assert.Equal(load.FaceCount, load2.FaceCount);
        Assert.Equal(dump.FaceCount, dump2.FaceCount);
    }

    // ── B4：交接班损失是锚点，且真的落在时窗上 ────────────────────────────────
    [Fact]
    public void B4_交接班损失喂进能力且可锚定为零()
    {
        // 缺省 0.5h/班、只扣非首班 ⇒ 23h；锚成 0 ⇒ 24h。差值 = 100 × 1h = 100 m³
        var withRamp = Plate(("东", 100, 9999));
        Assert.Equal(100 * 23.0, DayCapacityBudget.Of(withRamp).Load.CapacityM3, 1);

        try
        {
            var cfg = Plate(("东", 100, 9999));
            CompileOverrides.SetForTest(new CompileAnchors { HandoverRampH = 0 });
            string label = CompileOverrides.ApplyTo(cfg);

            Assert.Equal(0, cfg.HandoverRampH, 3);
            Assert.Equal(100 * 24.0, DayCapacityBudget.Of(cfg).Load.CapacityM3, 1);
            Assert.Contains("交接", label);   // 生效了就必须写进来源文案

            // 代价那一项要与差值对得上（100 m³ = 缺省口径下交接吃掉的量）
            Assert.Equal(100.0, DayCapacityBudget.Of(withRamp).Load.HandoverCostM3, 1);
            Assert.Equal(0, DayCapacityBudget.Of(cfg).Load.HandoverCostM3, 1);
        }
        finally { CompileOverrides.SetForTest(null); }
    }

    // ── B5：面日产能工时是锚点，且**不再挂在配煤标准上** ──────────────────────
    //
    //  它先前是 BlendStandard.EffHoursPerDay，于是"只想调有效工时"必须先造一份配煤标准，
    //  而 Blend==null 的语义是「不管配煤，纯量矿」—— 一造就把配煤约束整个打开了。
    //  这一条钉的就是那个副作用不许回来。
    [Fact]
    public void B5_有效工时锚点不会顺手打开配煤约束()
    {
        try
        {
            var cfg = Plate(("东", 100, 1000));
            Assert.Null(cfg.Blend);                     // 纯量矿：本来就不管配煤

            CompileOverrides.SetForTest(new CompileAnchors { EffHoursPerDay = 16 });
            CompileOverrides.ApplyTo(cfg);

            Assert.Equal(16, cfg.EffHoursPerDay, 3);
            Assert.Null(cfg.Blend);                     // ★ 配煤约束仍然是关着的
        }
        finally { CompileOverrides.SetForTest(null); }
    }

    // ── B6：面日产能工时真的当闸用（作业组织重分配） ──────────────────────────
    [Fact]
    public void B6_有效工时是面间重分配的天花板()
    {
        // 两个面各 100 m³/h，池子 4000。集中强采会按产能从大到小灌满，
        // 每面上限 = 100 × EffHoursPerDay。工时 =10 ⇒ 单面封顶 1000，两面都得开；
        // 工时 =40 ⇒ 单面封顶 4000，第一个面就吃满，第二个面归零。
        double FirstFaceGot(double effHours)
        {
            var cfg = Plate(("甲", 100, 2000), ("乙", 100, 2000));
            cfg.Organization = WorkOrganization.Concentrated;
            cfg.EffHoursPerDay = effHours;
            TaskExploder.Explode(cfg);
            return cfg.Faces.First(f => f.Zone == "甲").DayTargetM3;
        }

        Assert.Equal(1000, FirstFaceGot(10), 0);
        Assert.Equal(4000, FirstFaceGot(40), 0);
    }

    // ── B7：新锚点进 Clone / IsEmpty —— 漏一个就"重置了还显示有锚点" ──────────
    [Fact]
    public void B7_新锚点进入克隆与空判()
    {
        Assert.True(new CompileAnchors().IsEmpty);
        Assert.False(new CompileAnchors { EffHoursPerDay = 18 }.IsEmpty);
        Assert.False(new CompileAnchors { HandoverRampH = 0 }.IsEmpty);   // 0 ≠ 没设过

        var src = new CompileAnchors { EffHoursPerDay = 18, HandoverRampH = 0.25, MinPreparedDays = 3 };
        var cp = src.Clone();
        Assert.Equal(18, cp.EffHoursPerDay);
        Assert.Equal(0.25, cp.HandoverRampH);
        Assert.Equal(3, cp.MinPreparedDays);
    }

    // ── B8：重排必须整份带走盘子级口径 ────────────────────────────────────────
    //
    //  CloneConfig 原先漏拷 WeatherDeratePct / MinPreparedDays / Organization，
    //  于是"动态重排"会把天气降效和作业组织策略静默丢掉：重排出来的计划按满能力算，
    //  比原计划还乐观，而且一条校核都不报。
    [Fact]
    public void B8_重排不许丢掉降效与组织策略()
    {
        var baseCfg = Plate(("东", 100, 2400));
        baseCfg.WeatherDeratePct = 50;     // 能力腰斩：100 → 50 m³/h

        var r = TaskRescheduler.Reschedule(baseCfg, new List<ProductionTask>(), 0,
                                           new Dictionary<string, AdjustStrategy>());

        double planned = r.Plan.Tasks.Where(t => t.Process == ProcessType.Load).Sum(t => t.TargetVolumeM3);

        // 降效带过去了 ⇒ 当日最多排 50 × 23 = 1150；丢了 ⇒ 会排到 2300 上下
        Assert.True(planned <= 50 * DayHours + 1,
            $"重排排了 {planned:0} m³，超过降效后的当日能力 {50 * DayHours:0} —— 天气降效多半没跟着盘子过去");
    }
}
