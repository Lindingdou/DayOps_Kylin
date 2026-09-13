// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/StageSimPlayerTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Adjust;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  单环节三维演示（StageSimPlayer）的判据
//
//  不需要三维内核：仓库里本来就有 RecordingDynamicOverlay（记录端），
//  把每次推送深拷贝存下来供逐位比较。**深拷贝这一点是关键** ——
//  动画侧刻意复用同一个大数组逐帧改写，存引用的话所有帧都会指向同一份内容，
//  判据会全绿而实际在动（见 SimDynamicOverlay.cs 头注）。
//
//  ── 判据清单（每条都能证伪）──
//   S0 非退化   ：算例真的推出了图元（组数 / 段数非零），否则 S1~S6 全部空过。
//   S1 组齐全   ：区域/期初/当前/期末/推进带/设备/铭牌，各占各的组，组名互不相同。
//   S2 会动     ：progress 0 → 当前环 == 期初环；progress 1 → 当前环 == 期末环；
//                 中间值两头都不等。**这条直接回答「推进带到底动了没有」**。
//   S3 推进带   ：t=0 时顶点没动 ⇒ 一根横档都不画（退化线段不许推给内核）；t=1 时画满。
//   S4 帧末一次 ：每调一次 Play，RequestRender 恰好 +1（不是每组 render 一次）。
//   S5 Stop     ：抹掉全部组，且幂等。
//   S6 推进距离 ：v = V ÷ (L×H)，与 SimMiningParams 对得上，不是随手编的数。
//   S7 对不上不画：区域名对不上时**不画推进带**，但必须给出人读的原因（不静默吞掉）。
//   S8 采排反向 ：采场内缩（面积变小）、排土外扩（面积变大）—— 反了就是把挖当成了堆。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class StageSimPlayerTests : IDisposable
{
    private const string Pit = "试验采场";
    private const string Dmp = "试验排土场";

    // 组名是 StageSimPlayer 的对外契约（内核侧还会再加 "X:" 前缀）。写死在这里就是钉住它。
    private const string GRegions = "ADJ:regions";
    private const string GBefore = "ADJ:before";
    private const string GCurrent = "ADJ:current";
    private const string GTarget = "ADJ:target";
    private const string GBand = "ADJ:band";
    private const string GEquip = "ADJ:equip";
    private const string GLabel = "ADJ:label";

    private readonly RecordingDynamicOverlay _rec = new();

    public StageSimPlayerTests() => SimDynamicOverlay.Bind(_rec);
    public void Dispose() => SimDynamicOverlay.Bind(null);   // 恢复自动探测，别污染别的测试类

    // ── 算例 ─────────────────────────────────────────────────────────────────

    /// <summary>1000×600 的矩形采场 + 800×500 的矩形排土场，都摆在 z=100。</summary>
    private static StageSimPlayer NewPlayer(RecordingDynamicOverlay rec)
    {
        var p = new StageSimPlayer(rec);
        p.Reload();                                     // 装 SimMiningParams（H/L 有缺省值）
        p.Regions.Regions.Clear();
        p.Regions.Regions.Add(new SimRegion
        {
            Name = Pit, Category = "pit", Z = 100,
            Ring = Rect(0, 0, 1000, 600),
        });
        p.Regions.Regions.Add(new SimRegion
        {
            Name = Dmp, Category = "external_dump", Z = 100,
            Ring = Rect(3000, 0, 800, 500),
        });
        return p;
    }

    private static List<SimPoint> Rect(double x, double y, double w, double h)
        => new() { new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h) };

    /// <summary>一条 5 天的时间轴：某区域某工序每天 <paramref name="perDay"/> m³。</summary>
    private static DayStageTimeline Timeline(string region, ProcessType proc, double perDay)
    {
        var tl = new DayStageTimeline { ActualDayIndex = 0 };
        for (int i = 0; i < 5; i++)
        {
            var d = new DateTime(2026, 8, 1).AddDays(i);
            tl.Days.Add(new DayPlan
            {
                Date = d, IsWorkday = true,
                Stages =
                {
                    new DayStage
                    {
                        Date = d, RegionName = region, Process = proc,
                        TargetVolumeM3 = perDay, EquipCount = 4, Projected = true,
                    },
                },
            });
        }
        return tl;
    }

    private static StageGanttCell CellAt(DayStageTimeline tl, string region, ProcessType proc, int dayIdx)
    {
        var m = StageGanttModel.From(tl);
        return m.Groups.SelectMany(g => g.Lanes).SelectMany(l => l.Cells)
                .Single(c => c.DayIndex == dayIdx
                          && string.Equals(c.Region, region, StringComparison.OrdinalIgnoreCase)
                          && c.Process == proc);
    }

    /// <summary>某组最近一次推送的 xyz（没推过返回空）。</summary>
    private double[] Xyz(string group) => _rec.StateOf(group)?.Xyz ?? Array.Empty<double>();
    private int Count(string group) => _rec.StateOf(group)?.Count ?? 0;

    /// <summary>把一组线段的端点还原成环（每段 6 个 double，取每段起点）。</summary>
    private static List<(double X, double Y)> RingOf(double[] xyz)
    {
        var pts = new List<(double, double)>();
        for (int i = 0; i + 5 < xyz.Length; i += 6) pts.Add((xyz[i], xyz[i + 1]));
        return pts;
    }

    private static double Area(List<(double X, double Y)> r)
    {
        if (r.Count < 3) return 0;
        double a = 0;
        for (int i = 0; i < r.Count; i++)
        {
            var p = r[i]; var q = r[(i + 1) % r.Count];
            a += p.X * q.Y - q.X * p.Y;
        }
        return Math.Abs(a / 2);
    }

    // ═══════════════════════ 判据 ═══════════════════════

    /// <summary>S0 非退化 —— 没有这条，后面几条会在「什么都没推」时全绿。</summary>
    [Fact]
    public void S0_Play_ActuallyPushesGeometry()
    {
        var p = NewPlayer(_rec);
        var tl = Timeline(Pit, ProcessType.Load, 120_000);
        var info = p.Play(CellAt(tl, Pit, ProcessType.Load, 2), tl, 1.0);

        Assert.True(info.Ok, "演示没跑成：" + info.Headline);
        Assert.True(info.RegionMatched);
        Assert.True(Count(GCurrent) >= 3, "当前环一段都没推 ⇒ 后面的判据会空过");
        Assert.True(_rec.Renders > 0);
    }

    /// <summary>S1 组齐全且互不相同（组名撞了就会互相擦掉）。</summary>
    [Fact]
    public void S1_AllGroups_ArePushedUnderDistinctNames()
    {
        var p = NewPlayer(_rec);
        var tl = Timeline(Pit, ProcessType.Load, 120_000);
        p.Play(CellAt(tl, Pit, ProcessType.Load, 2), tl, 1.0);

        foreach (var g in new[] { GRegions, GBefore, GCurrent, GTarget, GBand, GEquip, GLabel })
            Assert.True(_rec.StateOf(g) != null, $"组 {g} 一次都没推过");

        var names = _rec.Pushes.Select(x => x.Group).Distinct().ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.All(names, n => Assert.StartsWith("ADJ:", n));   // 前缀是与别的舞台隔离的凭据
    }

    /// <summary>
    /// S2 <b>推进带到底动了没有</b>：t=0 时当前环 == 期初环，t=1 时 == 期末环，中间值两头都不等。
    /// 这一条是整个演示功能的核心 —— 画得出来但不会动，截图上完全看不出来。
    /// </summary>
    [Fact]
    public void S2_CurrentRing_MovesWithProgress()
    {
        var p = NewPlayer(_rec);
        var tl = Timeline(Pit, ProcessType.Load, 120_000);
        var cell = CellAt(tl, Pit, ProcessType.Load, 2);

        p.Play(cell, tl, 0.0);
        var at0 = RingOf(Xyz(GCurrent));
        var before = RingOf(Xyz(GBefore));

        p.Play(cell, tl, 1.0);
        var at1 = RingOf(Xyz(GCurrent));
        var target = RingOf(Xyz(GTarget));

        p.Play(cell, tl, 0.5);
        var atHalf = RingOf(Xyz(GCurrent));

        Assert.NotEmpty(at0);
        Assert.Equal(Area(before), Area(at0), 3);       // 期初形态
        Assert.Equal(Area(target), Area(at1), 3);       // 期末形态
        Assert.NotEqual(Area(at0), Area(at1), 3);       // 真的推进了

        double a0 = Area(at0), a1 = Area(at1), ah = Area(atHalf);
        Assert.True(ah < a0 - 1e-6 && ah > a1 + 1e-6,
            $"中间帧面积 {ah:N0} 没落在期初 {a0:N0} 与期末 {a1:N0} 之间 —— 进度条没起作用");
    }

    /// <summary>S3 推进带：t=0 顶点没动 ⇒ 一根横档都不画；t=1 画满（每个顶点一根）。</summary>
    [Fact]
    public void S3_Band_IsEmptyAtStart_AndFullAtEnd()
    {
        var p = NewPlayer(_rec);
        var tl = Timeline(Pit, ProcessType.Load, 120_000);
        var cell = CellAt(tl, Pit, ProcessType.Load, 2);

        p.Play(cell, tl, 0.0);
        Assert.Equal(0, Count(GBand));                  // 退化线段不许推给内核

        p.Play(cell, tl, 1.0);
        Assert.True(Count(GBand) >= 4, $"期末推进带只有 {Count(GBand)} 根横档");
    }

    /// <summary>S4 帧末一次 render —— 不是每推一组 render 一次（那是纯浪费帧预算）。</summary>
    [Fact]
    public void S4_OneRenderPerFrame()
    {
        var p = NewPlayer(_rec);
        var tl = Timeline(Pit, ProcessType.Load, 120_000);
        var cell = CellAt(tl, Pit, ProcessType.Load, 2);

        int before = _rec.Renders;
        p.Play(cell, tl, 0.3);
        Assert.Equal(before + 1, _rec.Renders);

        p.Play(cell, tl, 0.6);
        Assert.Equal(before + 2, _rec.Renders);
    }

    /// <summary>S5 Stop 抹掉全部组，且幂等。</summary>
    [Fact]
    public void S5_Stop_ClearsEveryGroup_AndIsIdempotent()
    {
        var p = NewPlayer(_rec);
        var tl = Timeline(Pit, ProcessType.Load, 120_000);
        p.Play(CellAt(tl, Pit, ProcessType.Load, 2), tl, 1.0);

        p.Stop();
        foreach (var g in new[] { GRegions, GBefore, GCurrent, GTarget, GBand, GEquip, GLabel })
            Assert.Equal(0, Count(g));

        int calls = _rec.Calls;
        p.Stop();                                        // 再来一次不该抛，也不该留下东西
        Assert.True(_rec.Calls > calls);
        foreach (var g in new[] { GCurrent, GBand })
            Assert.Equal(0, Count(g));
    }

    /// <summary>
    /// S6 推进距离对得上 v = V ÷ (L×H)。
    /// 这条钉住的是「距离是算出来的，不是编出来的」——
    /// 顺手也验了累计推进只数**同一区域同一工序**的前几天。
    /// </summary>
    [Fact]
    public void S6_Advance_MatchesVolumeOverLengthTimesHeight()
    {
        var prm = SimMiningParams.Load();
        Assert.True(prm.Usable, "H/L 解不出 ⇒ 本条空过");
        double perDay = 120_000;
        double expect = prm.AdvanceMetersFor(perDay);

        var p = NewPlayer(_rec);
        var tl = Timeline(Pit, ProcessType.Load, perDay);
        var info = p.Play(CellAt(tl, Pit, ProcessType.Load, 2), tl, 1.0);

        Assert.Equal(expect, info.AdvanceM, 6);
        Assert.Equal(expect * 2, info.CumBeforeM, 6);    // 第 3 天 ⇒ 前面累计了 2 天
    }

    /// <summary>S7 区域名对不上：不画推进带，但必须说出原因（不静默）。</summary>
    [Fact]
    public void S7_UnmatchedRegion_DrawsNothing_ButExplainsWhy()
    {
        var p = NewPlayer(_rec);
        var tl = Timeline("这个名字台账里没有", ProcessType.Load, 120_000);
        var info = p.Play(CellAt(tl, "这个名字台账里没有", ProcessType.Load, 2), tl, 1.0);

        Assert.False(info.RegionMatched);
        Assert.Equal(0, Count(GCurrent));
        Assert.Equal(0, Count(GBand));
        Assert.NotEmpty(info.Headline);
        Assert.NotEmpty(info.Notes);
        // 把已装区域点名列出来，人才知道该改哪个名字
        Assert.Contains(info.Notes, n => n.Contains(Pit));
    }

    /// <summary>S8 采场内缩、排土外扩 —— 反了就是把挖当成了堆。</summary>
    [Fact]
    public void S8_PitShrinks_DumpGrows()
    {
        var p = NewPlayer(_rec);

        var pitTl = Timeline(Pit, ProcessType.Load, 120_000);
        p.Play(CellAt(pitTl, Pit, ProcessType.Load, 0), pitTl, 0.0);
        double pit0 = Area(RingOf(Xyz(GCurrent)));
        p.Play(CellAt(pitTl, Pit, ProcessType.Load, 0), pitTl, 1.0);
        double pit1 = Area(RingOf(Xyz(GCurrent)));
        Assert.True(pit1 < pit0, $"采场推进后面积反而变大了（{pit0:N0} → {pit1:N0}）");

        var dumpTl = Timeline(Dmp, ProcessType.Dump, 120_000);
        p.Play(CellAt(dumpTl, Dmp, ProcessType.Dump, 0), dumpTl, 0.0);
        double d0 = Area(RingOf(Xyz(GCurrent)));
        p.Play(CellAt(dumpTl, Dmp, ProcessType.Dump, 0), dumpTl, 1.0);
        double d1 = Area(RingOf(Xyz(GCurrent)));
        Assert.True(d1 > d0, $"排土推进后面积反而变小了（{d0:N0} → {d1:N0}）");
    }

    /// <summary>
    /// S9 设备符号数 = **当前在班**那一段的编组规模（不是全天之和）。
    /// <para>演示按班走之后，班间空档现场是没人的 —— 那时候还画着设备就是假的。</para>
    /// </summary>
    [Fact]
    public void S9_EquipMarkers_MatchTheShiftOnDuty()
    {
        var p = NewPlayer(_rec);
        var tl = ThreeShiftTimeline();
        var cell = CellAt(tl, Pit, ProcessType.Load, 1);

        // 早班中段（2:00）：只画早班的 3 台
        var i0 = p.Play(cell, tl, HourToT(cell, 2));
        Assert.Equal("早班", i0.ActiveShift);
        Assert.Equal(3, Count(GEquip));

        // 夜班中段（20:00）：只画夜班的 7 台
        var i2 = p.Play(cell, tl, HourToT(cell, 20));
        Assert.Equal("夜班", i2.ActiveShift);
        Assert.Equal(7, Count(GEquip));

        Assert.NotEqual(cell.Stages.Sum(s => s.EquipCount), Count(GEquip));   // 不是全天之和
    }

    // ═══════════════ 一天之内的三班（逐班演示）═══════════════

    /// <summary>
    /// 三班算例：早 0–8（3 台 / 4 万方）、**中班 8–16 不排**、夜 16–24（7 台 / 6 万方）。
    /// 刻意留一段空档 —— 那是「进度条走的是时钟不是完成度」唯一能证伪的地方。
    /// </summary>
    private static DayStageTimeline ThreeShiftTimeline()
    {
        var tl = new DayStageTimeline { ActualDayIndex = 1 };
        for (int i = 0; i < 3; i++)
        {
            var d = new DateTime(2026, 8, 1).AddDays(i);
            tl.Days.Add(new DayPlan
            {
                Date = d, IsWorkday = true,
                Stages =
                {
                    new DayStage { Date = d, RegionName = Pit, Process = ProcessType.Load,
                                   Shift = "早班", StartHour = 0, EndHour = 8,
                                   TargetVolumeM3 = 40_000, EquipCount = 3, Projected = true },
                    new DayStage { Date = d, RegionName = Pit, Process = ProcessType.Load,
                                   Shift = "夜班", StartHour = 16, EndHour = 24,
                                   TargetVolumeM3 = 60_000, EquipCount = 7, Projected = true },
                },
            });
        }
        return tl;
    }

    /// <summary>把当日时刻换成进度条的 t（时钟从最早开班到最晚收班线性铺开）。</summary>
    private static double HourToT(StageGanttCell cell, double hour)
    {
        double lo = cell.Stages.Min(s => s.StartHour), hi = cell.Stages.Max(s => s.EndHour);
        return Math.Clamp((hour - lo) / (hi - lo), 0, 1);
    }

    /// <summary>S10 非退化：算例真的拆出了两班，且中间留着空档。</summary>
    [Fact]
    public void S10_ShiftSlices_AreSplitAndOrdered()
    {
        var p = NewPlayer(_rec);
        var tl = ThreeShiftTimeline();
        var info = p.Play(CellAt(tl, Pit, ProcessType.Load, 1), tl, 1.0);

        Assert.Equal(2, info.Shifts.Count);
        Assert.Equal("早班", info.Shifts[0].Shift);
        Assert.Equal("夜班", info.Shifts[1].Shift);
        Assert.True(info.Shifts[0].EndHour < info.Shifts[1].StartHour, "算例没有空档 ⇒ S11/S12 会空过");
    }

    /// <summary>
    /// S11 <b>进度条走的是时钟，不是完成度</b>。
    /// <para>t=0.5 是 12:00（空档里）：早班 4 万方已完成、夜班还没开工 ⇒ 完成度 40%，不是 50%。
    /// 若实现把 t 直接当完成度，这里会读到 50%，本条立刻红。</para>
    /// </summary>
    [Fact]
    public void S11_ProgressIsWallClock_NotCompletion()
    {
        var p = NewPlayer(_rec);
        var tl = ThreeShiftTimeline();
        var cell = CellAt(tl, Pit, ProcessType.Load, 1);

        var mid = p.Play(cell, tl, 0.5);
        Assert.Equal(12.0, mid.ClockHour, 3);
        Assert.Equal("", mid.ActiveShift);                       // 空档里没有班
        Assert.Equal(40_000, mid.DoneTodayM3, 0);                // 只完成了早班那一份
        Assert.Equal(0, Count(GEquip));                          // 空档不画设备
    }

    /// <summary>S12 空档里轮廓不动：12:00 与 16:00 开工前的形态一致（那几个小时确实没推进）。</summary>
    [Fact]
    public void S12_ContourFrozenDuringTheGap()
    {
        var p = NewPlayer(_rec);
        var tl = ThreeShiftTimeline();
        var cell = CellAt(tl, Pit, ProcessType.Load, 1);

        p.Play(cell, tl, HourToT(cell, 9));      // 早班刚收
        double a9 = Area(RingOf(Xyz(GCurrent)));
        p.Play(cell, tl, HourToT(cell, 15.9));   // 夜班开工前
        double a16 = Area(RingOf(Xyz(GCurrent)));
        Assert.Equal(a9, a16, 3);

        p.Play(cell, tl, HourToT(cell, 20));     // 夜班干起来了
        double a20 = Area(RingOf(Xyz(GCurrent)));
        Assert.NotEqual(a16, a20, 3);
    }

    /// <summary>S13 收班瞬间（t=1）仍算最后一个班在班 —— 否则默认视图一眼看到的是空场。</summary>
    [Fact]
    public void S13_AtEndOfDay_LastShiftStaysOnDuty()
    {
        var p = NewPlayer(_rec);
        var tl = ThreeShiftTimeline();
        var info = p.Play(CellAt(tl, Pit, ProcessType.Load, 1), tl, 1.0);

        Assert.Equal(24.0, info.ClockHour, 3);
        Assert.Equal("夜班", info.ActiveShift);
        Assert.Equal(7, Count(GEquip));
        Assert.Equal(100_000, info.DoneTodayM3, 0);              // 全天量做完
    }
}
