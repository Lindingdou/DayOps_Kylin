// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/DayStageAdjustTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Adjust;
using PitMine3D.Kylin.TaskLib.Domain;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  「生产任务动态调整」逐日层的回归判据
//
//  覆盖两个**纯函数**层（不依赖数据库，跑得起来）：
//    · DayRollover        跨天顺延：欠量 → 后续作业日的余量能力
//    · StageGanttModel    时间轴 → 甘特模型的投影
//  DayStagePlanBuilder.Build() 要读班次日历/当日盘子/actuals，判据在这里够不着，
//  它的形态验收走离线出图台架（见 memory: wpf-dialog-offline-render-harness）。
//
//  ── 判据清单（每条都能证伪，不是「跑通就算过」）──
//   R0 算例非退化：欠量 / 顺延笔数 / 判不了量都必须非零 —— 否则 R1~R3 会**空过**。
//   R1 守恒      ：顺延 + 装不下 + 判不了 == 总欠量。少一分就是有量在中间蒸发。
//   R2 不超能力  ：任何一天追加的量 ≤ 该天该线的余量能力。超了 = 给出执行不了的计划。
//   R3 判不了≠装不下：产能未解出的线只能进 UnknownM3，不许并进 OverflowM3。
//   R4 只认真欠产：计划有量但实绩为 0 的环节**不算欠产**（那是还没干，不是没干成）。
//   R5 落账不丢  ：Apply 后 Σ RolledInM3 == AbsorbedM3。
//   R6 可重入    ：Clear 后全部归零；Clear→Plan→Apply 跑两遍结果一致（不累加）。
//   R7 无后续作业日：欠量全部计入「装不下」，且一笔顺延都不产生。
//   G1 不错位    ：每条环节线的格子数 == 天数（某天没作业也要占位）。
//   G2 组序      ：采场类在前、排土类在后（物料流方向）。
//   G3 条高依据  ：DisplayVolumeM3 == max(顺延后计划, 实绩) —— 超产/计划外/顺延都不许被削平。
//   G4 计划外成线：计划里没有、只有实绩的区域必须自成一组，不许被吞掉。
//   G5 非作业日  ：非作业日的格子 HasWork == false。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class DayStageAdjustTests
{
    private static readonly DateTime Day0 = new(2026, 8, 1);
    /// <summary>欠量发生的那天（下标 16 = 8/17）。</summary>
    private const int FromIdx = 16;

    private const string North = "采场面·北一";
    private const string South = "采场面·南二";
    private const string East = "采场面·东三";
    private const string Dump = "北排土场";

    // ── 合成算例 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 2026-08 整月。周日 + 8/15 为非作业日。
    /// <list type="bullet">
    /// <item><b>北一·采装</b>：计划 18500/天，产能 800 m³/h × 班窗 24h、已排 23.125h ⇒ 余量 700 m³/天。
    ///       8/17 实绩 12900 ⇒ 欠 5600，需要 8 天才摊得完（验逐日装填）。</item>
    /// <item><b>南二·采装</b>：**刻意不给产能** ⇒ 它的欠量只能落进「判不了」（验 R3）。</item>
    /// <item><b>排土</b>：8/17 计划 21400、实绩 0 ⇒ 按 R4 <b>不算欠产</b>。</item>
    /// <item><b>东三</b>：只有 8/7 一条纯实绩环节（计划 0）⇒ 验 G3/G4。</item>
    /// </list>
    /// </summary>
    private static DayStageTimeline Fixture()
    {
        var tl = new DayStageTimeline { ActualDayIndex = FromIdx };
        for (int i = 0; i < 31; i++)
        {
            var d = Day0.AddDays(i);
            bool off = d.DayOfWeek == DayOfWeek.Sunday || d.Day == 15;
            var day = new DayPlan { Date = d, IsWorkday = !off, IsActualDay = i == FromIdx };

            if (!off)
            {
                bool here = i == FromIdx;

                var n = Lane(d, North, ProcessType.Load, 18500, cap: 800, span: 24, planned: 18500 / 800.0);
                if (here) { n.ActualVolumeM3 = 12900; n.ActualFromLedger = true; n.Reasons.Add(IncompleteReason.TruckShortage); }
                day.Stages.Add(n);

                var s = Lane(d, South, ProcessType.Load, 11200, cap: 0, span: 0, planned: 0);   // 产能未解出
                if (here) { s.ActualVolumeM3 = 9000; s.ActualFromLedger = true; s.Reasons.Add(IncompleteReason.OreShortage); }
                day.Stages.Add(s);

                // 排土：8/17 计划有量但**实绩为 0** ⇒ R4 说它不算欠产
                day.Stages.Add(Lane(d, Dump, ProcessType.Dump, 21400, cap: 900, span: 24, planned: 21400 / 900.0));

                if (d.Day == 7)
                    day.Stages.Add(new DayStage
                    {
                        Date = d, RegionName = East, Process = ProcessType.Load,
                        TargetVolumeM3 = 0, ActualVolumeM3 = 8300,
                        Projected = true, ActualFromLedger = true, ActualOnly = true,
                    });
            }
            tl.Days.Add(day);
        }
        return tl;
    }

    private static DayStage Lane(DateTime d, string region, ProcessType p, double target,
                                 double cap, double span, double planned)
        => new()
        {
            Date = d, RegionName = region, Process = p, TargetVolumeM3 = target,
            CapacityM3PerH = cap, ShiftSpanH = span, PlannedHours = planned,
            Projected = true,
        };

    /// <summary>追加前各（天,区域,工序）的余量能力 —— Apply 之后会被 RolledInM3 吃掉，要先记。</summary>
    private static Dictionary<string, double> SpareSnapshot(DayStageTimeline tl)
    {
        var map = new Dictionary<string, double>();
        foreach (var d in tl.Days)
            foreach (var s in d.Stages)
            {
                string k = Key(d.Date, s.RegionName, s.Process);
                map[k] = map.GetValueOrDefault(k) + (s.CapacityResolved ? s.SpareCapacityM3 : 0);
            }
        return map;
    }

    private static string Key(DateTime d, string region, ProcessType p) => $"{d:yyyyMMdd}|{region}|{(int)p}";

    // ═══════════════════════ DayRollover ═══════════════════════

    /// <summary>R0 算例非退化 —— 没有这条，R1~R3 会在"什么都没发生"时全绿。</summary>
    [Fact]
    public void R0_Fixture_IsNotDegenerate()
    {
        var tl = Fixture();
        var res = DayRollover.Plan(tl, FromIdx);

        Assert.True(res.Ok);
        Assert.True(res.ShortfallM3 > 1e-6, "算例里没有欠量 ⇒ 后面的守恒/不超能力判据会空过");
        Assert.True(res.AbsorbedM3 > 1e-6, "算例里一笔都没顺延 ⇒ R2 会空过");
        Assert.True(res.Assignments.Count > 1, "只有一笔顺延 ⇒ 验不出逐日装填");
        Assert.True(res.UnknownM3 > 1e-6, "算例里没有产能未解出的线 ⇒ R3 会空过");
    }

    /// <summary>R1 守恒：顺延 + 装不下 + 判不了 == 总欠量。</summary>
    [Fact]
    public void R1_Conservation_AbsorbedPlusOverflowPlusUnknown_EqualsShortfall()
    {
        var res = DayRollover.Plan(Fixture(), FromIdx);
        Assert.Equal(res.ShortfallM3, res.AbsorbedM3 + res.OverflowM3 + res.UnknownM3, 3);
    }

    /// <summary>R1b 欠量本身对得上输入：北一欠 5600 + 南二欠 2200 = 7800。</summary>
    [Fact]
    public void R1b_Shortfall_MatchesTheInput()
    {
        var res = DayRollover.Plan(Fixture(), FromIdx);
        Assert.Equal(7800, res.ShortfallM3, 3);
        Assert.Equal(5600, res.Remainders.Single(r => r.Region == North).ShortfallM3, 3);
        Assert.Equal(2200, res.Remainders.Single(r => r.Region == South).ShortfallM3, 3);
    }

    /// <summary>R2 任何一天追加的量都不得超过该天该线的余量能力（超了 = 计划执行不了）。</summary>
    [Fact]
    public void R2_NoDay_ExceedsItsSpareCapacity()
    {
        var tl = Fixture();
        var spare = SpareSnapshot(tl);
        var res = DayRollover.Plan(tl, FromIdx);

        foreach (var g in res.Assignments.GroupBy(a => Key(a.ToDate, a.Region, a.Process)))
        {
            double added = g.Sum(a => a.AddedM3);
            double cap = spare.GetValueOrDefault(g.Key);
            Assert.True(added <= cap + 1e-6,
                $"{g.Key} 追加 {added:N0} 超过余量能力 {cap:N0} —— 这份计划执行不了");
        }
    }

    /// <summary>R2b 顺延只落在<b>起始日之后</b>的作业日上（不许往回补，也不许落到停产日）。</summary>
    [Fact]
    public void R2b_Assignments_LandOnLaterWorkdaysOnly()
    {
        var tl = Fixture();
        var res = DayRollover.Plan(tl, FromIdx);
        var fromDate = tl.Days[FromIdx].Date;

        Assert.NotEmpty(res.Assignments);
        foreach (var a in res.Assignments)
        {
            Assert.True(a.ToDate > fromDate, $"顺延落到了 {a.ToDate:MM-dd}，不晚于起始日 {fromDate:MM-dd}");
            var day = tl.Days.Single(d => d.Date == a.ToDate);
            Assert.True(day.IsWorkday, $"顺延落到了非作业日 {a.ToDate:MM-dd}");
        }
    }

    /// <summary>R3 产能未解出的线只能进「判不了」，不许并进「装不下」。</summary>
    [Fact]
    public void R3_Unknown_IsNeverCountedAsOverflow()
    {
        var res = DayRollover.Plan(Fixture(), FromIdx);

        var south = res.Remainders.Single(r => r.Region == South);
        Assert.True(south.CapacityUnknown, "南二没给产能，应判为「产能未解出」");
        Assert.Equal(0, south.AbsorbedM3, 3);
        Assert.Equal(2200, south.LeftM3, 3);

        Assert.Equal(2200, res.UnknownM3, 3);
        Assert.Equal(0, res.OverflowM3, 3);          // 北一装得下 ⇒ 真正的「装不下」为 0
    }

    /// <summary>
    /// R4 计划有量但实绩为 0 的环节不算欠产。
    /// 排土线 8/17 计划 21400、实绩 0 —— 若被当成欠 21400，全月缺口会凭空放大。
    /// </summary>
    [Fact]
    public void R4_PlannedButNotYetWorked_IsNotShortfall()
    {
        var res = DayRollover.Plan(Fixture(), FromIdx);
        Assert.DoesNotContain(res.Remainders, r => r.Process == ProcessType.Dump);
        Assert.DoesNotContain(res.Assignments, a => a.Process == ProcessType.Dump);
    }

    /// <summary>R5 Apply 之后，写进时间轴的顺延量合计 == 规划出来的 AbsorbedM3。</summary>
    [Fact]
    public void R5_Apply_WritesExactlyWhatWasAbsorbed()
    {
        var tl = Fixture();
        var res = DayRollover.Plan(tl, FromIdx);
        DayRollover.Apply(tl, res);

        double written = tl.AllStages.Sum(s => s.RolledInM3);
        Assert.Equal(res.AbsorbedM3, written, 3);
        Assert.True(written > 1e-6);
    }

    /// <summary>R6 可重入：Clear 归零；Clear→Plan→Apply 跑两遍结果一致，不累加。</summary>
    [Fact]
    public void R6_ClearThenReplan_IsIdempotent()
    {
        var tl = Fixture();

        var r1 = DayRollover.Plan(tl, FromIdx);
        DayRollover.Apply(tl, r1);
        double first = tl.AllStages.Sum(s => s.RolledInM3);

        DayRollover.Clear(tl);
        Assert.Equal(0, tl.AllStages.Sum(s => s.RolledInM3), 6);

        var r2 = DayRollover.Plan(tl, FromIdx);
        DayRollover.Apply(tl, r2);
        double second = tl.AllStages.Sum(s => s.RolledInM3);

        Assert.Equal(first, second, 3);
    }

    /// <summary>
    /// R6b 不 Clear 直接重跑会**吃掉余量**：第二遍能顺延的量必然更少。
    /// 这条钉住的是「Plan 读的是扣过 RolledInM3 之后的余量」——
    /// 若实现里忘了扣，两遍结果会相同，本条立刻变红。
    /// </summary>
    [Fact]
    public void R6b_ReplanWithoutClear_SeesLessSpare()
    {
        var tl = Fixture();
        var r1 = DayRollover.Plan(tl, FromIdx);
        DayRollover.Apply(tl, r1);

        var r2 = DayRollover.Plan(tl, FromIdx);       // 刻意不 Clear
        Assert.True(r2.AbsorbedM3 < r1.AbsorbedM3,
            $"没清顺延就重跑，第二遍仍能吸收 {r2.AbsorbedM3:N0}（首遍 {r1.AbsorbedM3:N0}）—— 余量没被扣");
    }

    /// <summary>R7 起始日之后没有作业日时：欠量全进「装不下」，且一笔顺延都不产生。</summary>
    [Fact]
    public void R7_NoLaterWorkday_AllShortfallOverflows()
    {
        var tl = Fixture();
        for (int i = FromIdx + 1; i < tl.Days.Count; i++) tl.Days[i].IsWorkday = false;

        var res = DayRollover.Plan(tl, FromIdx);

        Assert.Empty(res.Assignments);
        Assert.Equal(0, res.AbsorbedM3, 3);
        Assert.Equal(res.ShortfallM3, res.OverflowM3, 3);
        Assert.Contains(res.Notes, n => n.Contains("没有作业日"));
    }

    // ── 装填策略 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// R8 <b>策略只改分布，不改总账</b>：两种策略的「顺延 / 装不下 / 判不了」三个数必须一致。
    /// <para>装不装得下是产能决定的，跟先补哪天没关系。这条一旦红，说明某个策略在偷偷丢量或多算量。</para>
    /// </summary>
    [Fact]
    public void R8_Strategy_ChangesDistributionNotTotals()
    {
        var a = DayRollover.Plan(Fixture(), FromIdx, RolloverStrategy.EarliestFirst);
        var b = DayRollover.Plan(Fixture(), FromIdx, RolloverStrategy.Even);

        Assert.Equal(a.ShortfallM3, b.ShortfallM3, 3);
        Assert.Equal(a.AbsorbedM3, b.AbsorbedM3, 3);
        Assert.Equal(a.OverflowM3, b.OverflowM3, 3);
        Assert.Equal(a.UnknownM3, b.UnknownM3, 3);
        Assert.True(a.AbsorbedM3 > 1e-6);           // 空过防护
    }

    /// <summary>
    /// R9 均衡摊平**不把任何一天顶满**，而最早优先会把前几天顶满 —— 两者确实不同。
    /// 没有这条，「加了个策略但其实没生效」会一路绿着过去。
    /// </summary>
    [Fact]
    public void R9_Even_LeavesHeadroomOnEveryDay()
    {
        var tlA = Fixture();
        var early = DayRollover.Plan(tlA, FromIdx, RolloverStrategy.EarliestFirst);
        var tlB = Fixture();
        var even = DayRollover.Plan(tlB, FromIdx, RolloverStrategy.Even);

        // 北一那条线装得下（5600 < 12 天 × 700），均衡摊平才谈得上"留缓冲"
        var eN = early.Assignments.Where(x => x.Region == North).ToList();
        var vN = even.Assignments.Where(x => x.Region == North).ToList();
        Assert.NotEmpty(eN);
        Assert.NotEmpty(vN);

        Assert.Contains(eN, x => x.AddedM3 >= x.SpareBeforeM3 - 1e-6);       // 最早优先：有天被顶满
        Assert.All(vN, x => Assert.True(x.AddedM3 < x.SpareBeforeM3 - 1e-6,  // 均衡：没有一天顶满
            $"{x.ToDate:MM-dd} 追加 {x.AddedM3:N1} 已把余量 {x.SpareBeforeM3:N1} 占满"));

        Assert.True(vN.Count > eN.Count, "均衡摊平应当摊到更多天上");
    }

    /// <summary>R10 均衡摊平同样不许超能力（策略换了，硬约束不能松）。</summary>
    [Fact]
    public void R10_Even_StillRespectsCapacity()
    {
        var tl = Fixture();
        var spare = SpareSnapshot(tl);
        var res = DayRollover.Plan(tl, FromIdx, RolloverStrategy.Even);

        foreach (var g in res.Assignments.GroupBy(a => Key(a.ToDate, a.Region, a.Process)))
        {
            double added = g.Sum(a => a.AddedM3);
            double cap = spare.GetValueOrDefault(g.Key);
            Assert.True(added <= cap + 1e-6, $"{g.Key} 追加 {added:N1} 超过余量 {cap:N1}");
        }
        Assert.Equal(res.ShortfallM3, res.AbsorbedM3 + res.OverflowM3 + res.UnknownM3, 3);
    }

    /// <summary>R11 总余量不够时，均衡摊平自动等同于最早优先（装不下时每天本来就得顶满）。</summary>
    [Fact]
    public void R11_WhenCapacityIsTight_EvenDegradesToEarliest()
    {
        // 只留 2 个后续作业日 ⇒ 总余量 1400 < 欠量 5600
        var tl = Fixture();
        for (int i = FromIdx + 3; i < tl.Days.Count; i++) tl.Days[i].IsWorkday = false;

        var tl2 = Fixture();
        for (int i = FromIdx + 3; i < tl2.Days.Count; i++) tl2.Days[i].IsWorkday = false;

        var a = DayRollover.Plan(tl, FromIdx, RolloverStrategy.EarliestFirst);
        var b = DayRollover.Plan(tl2, FromIdx, RolloverStrategy.Even);

        Assert.True(a.OverflowM3 > 1e-6, "算例没造出「装不下」⇒ 本条空过");
        Assert.Equal(a.AbsorbedM3, b.AbsorbedM3, 3);
        Assert.Equal(a.Assignments.Count, b.Assignments.Count);
    }

    // ═══════════════════════ StageGanttModel ═══════════════════════

    /// <summary>G1 每条环节线的格子数 == 天数 —— 少一个就整条线右移，图上完全看不出来。</summary>
    [Fact]
    public void G1_EveryLane_HasOneCellPerDay()
    {
        var tl = Fixture();
        var m = StageGanttModel.From(tl);

        Assert.NotEmpty(m.Groups);
        foreach (var lane in m.Groups.SelectMany(g => g.Lanes))
        {
            Assert.Equal(tl.Days.Count, lane.Cells.Count);
            for (int i = 0; i < lane.Cells.Count; i++)
                Assert.Equal(tl.Days[i].Date, lane.Cells[i].Date);
        }
    }

    /// <summary>G2 组序：采场类全部排在排土类之前（物料流方向）。</summary>
    [Fact]
    public void G2_PitGroups_ComeBeforeDumpSites()
    {
        var m = StageGanttModel.From(Fixture());

        int lastPit = m.Groups.FindLastIndex(g => !g.IsDumpSite);
        int firstDump = m.Groups.FindIndex(g => g.IsDumpSite);
        Assert.True(firstDump >= 0, "算例里没有排土组 ⇒ 本条空过");
        Assert.True(lastPit >= 0, "算例里没有采场组 ⇒ 本条空过");
        Assert.True(lastPit < firstDump, "排土场排到了采场前面");
    }

    /// <summary>
    /// G3 条高依据 = max(顺延后计划, 实绩)。三类格子只有这么取才画得出来：
    /// 超产日、计划外作业、顺延补量日。
    /// </summary>
    [Fact]
    public void G3_DisplayVolume_IsMaxOfPlannedTotalAndActual()
    {
        var tl = Fixture();
        var res = DayRollover.Plan(tl, FromIdx);
        DayRollover.Apply(tl, res);
        var m = StageGanttModel.From(tl);

        var cells = m.Groups.SelectMany(g => g.Lanes).SelectMany(l => l.Cells).ToList();
        foreach (var c in cells)
            Assert.Equal(Math.Max(c.PlannedTotalM3, c.ActualVolumeM3), c.DisplayVolumeM3, 6);

        // 计划外那格：计划 0、实绩 8300 ⇒ 条高依据必须是 8300 而不是 0
        var eastCell = cells.Single(c => c.Region == East && c.ActualVolumeM3 > 1e-6);
        Assert.Equal(0, eastCell.TargetVolumeM3, 6);
        Assert.Equal(8300, eastCell.DisplayVolumeM3, 3);

        // 顺延补量那几格：条高依据必须大于原计划
        var rolled = cells.Where(c => c.RolledInM3 > 1e-6).ToList();
        Assert.NotEmpty(rolled);
        foreach (var c in rolled)
            Assert.True(c.DisplayVolumeM3 > c.TargetVolumeM3 + 1e-6,
                $"{c.Date:MM-dd} {c.Region} 顺延了 {c.RolledInM3:N0} 但条高依据没跟着长");
    }

    /// <summary>G4 计划里没有、只有实绩的区域必须自成一组（否则整段真实作业在图上消失）。</summary>
    [Fact]
    public void G4_ActualOnlyRegion_BecomesItsOwnGroup()
    {
        var m = StageGanttModel.From(Fixture());
        var g = m.Groups.SingleOrDefault(x => x.Region == East);

        Assert.NotNull(g);
        Assert.Equal(0, g!.TotalM3, 6);
        Assert.Equal(8300, g.ActualTotalM3, 3);
        Assert.Contains("计划外", g.TotalCaption);
    }

    /// <summary>G5 非作业日不许有环节（那天没人上班）。</summary>
    [Fact]
    public void G5_NonWorkdays_HaveNoWork()
    {
        var tl = Fixture();
        var m = StageGanttModel.From(tl);

        var offIdx = tl.Days.Select((d, i) => (d, i)).Where(x => !x.d.IsWorkday).Select(x => x.i).ToList();
        Assert.NotEmpty(offIdx);

        foreach (var lane in m.Groups.SelectMany(g => g.Lanes))
            foreach (int i in offIdx)
            {
                Assert.False(lane.Cells[i].IsWorkday);
                Assert.False(lane.Cells[i].HasWork, $"非作业日 {lane.Cells[i].Date:MM-dd} 上排了 {lane.Region}·{lane.Process.Label()}");
            }
    }

    /// <summary>
    /// G6 异常角标只挂在真有原因码的格子上。
    /// 推算天没有原因码 —— 若实现把原因码一起复制到每一天，全月都会插满红三角。
    /// </summary>
    [Fact]
    public void G6_AbnormalFlag_OnlyOnCellsWithReasons()
    {
        var m = StageGanttModel.From(Fixture());
        var cells = m.Groups.SelectMany(g => g.Lanes).SelectMany(l => l.Cells).ToList();

        var abnormal = cells.Where(c => c.Abnormal).ToList();
        Assert.NotEmpty(abnormal);                                   // 空过防护
        Assert.All(abnormal, c => Assert.Equal(Day0.AddDays(FromIdx), c.Date));
        Assert.Equal(2, abnormal.Count);                             // 北一(运力) + 南二(缺料)
    }
}
