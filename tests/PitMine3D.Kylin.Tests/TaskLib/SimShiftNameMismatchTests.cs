// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SimShiftNameMismatchTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「编制结果 → 日班推演」这一跳的判据：<b>班次名对不上时不许静默演出一片空白</b>。
///
/// <para><b>病灶</b>：<see cref="SimBuilder"/> 建日班时，班次名取自<b>现装的盘子</b>
/// （<c>Config()</c>，每次现装），任务取自<b>缓存的计划</b>（<c>Day()</c>，可能是上一轮编的、
/// 也可能是从盘里回读的快照）。两边的班次名一旦不同，逐班筛任务用的是严格相等，
/// 每一帧都被跳过 —— 结果是 <b>0 帧、且一条提示都没有</b>，时间轴看着就像"今天没排活"。</para>
///
/// <para><b>这不是假想</b>：班次日历接通时班名是「早班/中班/夜班」，读不到时退回缺省班制
/// 「早/中/夜」。于是"编制时日历接通、演的时候日历读不到"就会静默演出空白。
/// 2026-08-20 用离线探针跑通「编制 → 推演」时抓到的，当时的现象正是 0 帧。</para>
/// </summary>
[Collection("PlanContext")]
public sealed class SimShiftNameMismatchTests
{
    /// <summary>造一份班次名为 <paramref name="shiftName"/> 前缀的当日计划快照（不碰台账）。</summary>
    private static ExploderResult PlanWithShiftNames(string suffix)
    {
        var cfg = new ExploderConfig
        {
            DateLabel = "2026-08-20 周四", IdPrefix = "SM", EnforceMassBalance = false,
            Shifts =
            {
                new ShiftWindow("早" + suffix, 0, 8),
                new ShiftWindow("中" + suffix, 8, 16),
                new ShiftWindow("夜" + suffix, 16, 24),
            },
        };
        cfg.Sinks.Put(new SinkNode { Id = "DP-1", Name = "内排场", Kind = SinkKind.InternalDump, FallbackHaulKm = 1.2 });
        cfg.Faces.Add(new FaceInput
        {
            Zone = "北帮剥离", Process = ProcessType.Load, Material = "岩", DayTargetM3 = 6000,
            DestinationId = "DP-1", DestinationName = "内排场", DestinationKind = SinkKind.InternalDump,
            HaulDistanceKm = 1.2,
            Group = new EquipmentGroup { MainEquipment = "WK-01", GroupCapacityM3PerH = 300 },
        });
        return TaskExploder.Explode(cfg);
    }

    // ── M1：盘子与任务的班次名不一致时，帧不许为空，且必须报出不一致 ────────────
    [Fact]
    public void M1_班次名对不上时按任务演并报账()
    {
        try
        {
            // 先让期次落定并注册装配钩子；此后注入的快照才不会被 ProjectScope.Changed 清掉
            try { SampleTaskBoard.Config(); } catch { }

            // 裸台架的盘子（台账不通）用的是缺省班制「早/中/夜」；
            // 而这份计划是按「早班/中班/夜班」编的 —— 正是要判的那种不一致。
            ProductionPlanContext.SetSnapshot(PlanWithShiftNames("班"));

            var tl = SimBuilder.Build(SimGranularity.Shift, SimTrack.Plan);

            Assert.True(tl.Frames.Count > 0,
                "班次名对不上就演出 0 帧 —— 这正是那条静默死路：时间轴看着像「今天没排活」，"
              + "而计划里明明有任务，且从头到尾不报错。");
            Assert.Equal(3, tl.Frames.Count);
            Assert.Contains(tl.Notes, n => n.Contains("班次名对不上"));

            // 演出来的量必须是计划里的量，不是零壳
            double waste = tl.Frames.Sum(f => f.Balance.Flows.Where(x => !x.IsOre).Sum(x => x.InSituM3));
            Assert.True(waste > 1, $"三帧的剥离量合计只有 {waste:0} m³ —— 帧建出来了但没带上量");
        }
        finally { ProductionPlanContext.Invalidate(); }
    }

    // ── M2：关闸 —— 名字对得上时不许再报这条（否则它恒真，等于没判）──────────────
    [Fact]
    public void M2_名字对得上时不报不一致()
    {
        try
        {
            try { SampleTaskBoard.Config(); } catch { }

            // 与裸台架缺省班制同名（早/中/夜）⇒ 本来就对得上
            ProductionPlanContext.SetSnapshot(PlanWithShiftNames(""));

            var tl = SimBuilder.Build(SimGranularity.Shift, SimTrack.Plan);

            Assert.True(tl.Frames.Count > 0, "名字对得上却演不出帧，那是另一个毛病");
            Assert.DoesNotContain(tl.Notes, n => n.Contains("班次名对不上"));
        }
        finally { ProductionPlanContext.Invalidate(); }
    }
}
