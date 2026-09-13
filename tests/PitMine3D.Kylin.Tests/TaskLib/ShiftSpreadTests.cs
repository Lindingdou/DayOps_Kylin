// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/ShiftSpreadTests.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 月计划 → 日 → <b>班</b> 的摊布判据。
///
/// <para>
/// 这一层要答的是"当天这点量怎么摆到三个班上"，依据只有三样：<b>物料量 × 设备能力 × 可用作业时间</b>。
/// </para>
/// <para>
/// 原实现是<b>按班次顺序贪心填满</b>：<c>vol = min(班产×可用工时, 剩余量)</c>，剩余清零后
/// 后面的班一律挂空闲。只要一个面的日目标装得下一个班，这个面当天就只在早班有活 ——
/// 实测样例盘子：早班挖 7746 m³、中班 1354、夜班 <b>0</b>，七台设备全空。
/// 三班连续作业的矿不会这么排，这样的计划发下去中班夜班的人来了没活干。
/// </para>
/// <para>
/// 更隐蔽的是<b>归因</b>：那些空闲挂的是 <see cref="IncompleteReason.OreShortage"/>（欠料·采空），
/// 而事实是"前面的班替它干完了"，备采根本没用尽 —— 动态调整会据此路由到「换面」策略，
/// 为一个不存在的问题换面。
/// </para>
/// </summary>
public class ShiftSpreadTests
{
    /// <summary>三班各 8h、一个采装面、班产 100 m³/h ⇒ 全天能力 ≈ 2400 m³（扣交接后略少）。</summary>
    private static ExploderConfig Plate(double dayTarget, double capPerH = 100, double handoverH = 0)
    {
        var cfg = new ExploderConfig
        {
            DateLabel = "2026-08-11 周二",
            IdPrefix = "T0811",
            EnforceMassBalance = false,
            HandoverRampH = handoverH,
            Shifts =
            {
                new ShiftWindow("早班", 0, 8),
                new ShiftWindow("中班", 8, 16),
                new ShiftWindow("夜班", 16, 24),
            },
        };
        cfg.Faces.Add(new FaceInput
        {
            Zone = "主采面·东",
            Process = ProcessType.Load,
            Material = "煤",
            DayTargetM3 = dayTarget,
            Group = new EquipmentGroup { MainEquipment = "WK-01", GroupCapacityM3PerH = capPerH },
        });
        return cfg;
    }

    private static ProductionTask[] Loads(ExploderResult r)
        => r.Tasks.Where(t => t.Process == ProcessType.Load).OrderBy(t => t.StartHour).ToArray();

    // ── N1：日目标远小于全天能力 ⇒ 三个班**都有活**（开闸；贪心实现在这里必红）──────
    [Fact]
    public void N1_量小也三班都有活()
    {
        var r = TaskExploder.Explode(Plate(dayTarget: 600));   // 全天能力 2400，只排 600
        var load = Loads(r);

        Assert.Equal(3, load.Length);
        Assert.Equal(new[] { "早班", "中班", "夜班" }, load.Select(t => t.Shift));

        // 三班能力相同 ⇒ 各摊三分之一（200 m³、2 h）
        foreach (var t in load)
        {
            Assert.InRange(t.TargetVolumeM3, 195, 205);
            Assert.InRange(t.PlannedHours, 1.8, 2.2);
        }

        // ★ 关闸：夜班必须真的有量。贪心实现下它是 0（整天的活都在早班）
        var night = load.Single(t => t.Shift == "夜班");
        Assert.True(night.TargetVolumeM3 > 1, "夜班没量 —— 又变回「早班填满、后两班挂空闲」了");

        // 量摊开了就不该再有"空闲"条：那个原因码的本义是备采用尽，不是被前面的班干完了
        Assert.DoesNotContain(r.Tasks, t => t.Process == ProcessType.Idle);
    }

    // ── N2：摊完的总量必须**严格等于**日目标（不许摊丢方量）────────────────────
    [Theory]
    [InlineData(600)]
    [InlineData(1000)]
    [InlineData(2399)]
    [InlineData(777.7)]
    public void N2_摊完不丢方量(double target)
    {
        var r = TaskExploder.Explode(Plate(target));
        double sum = Loads(r).Sum(t => t.TargetVolumeM3);
        Assert.InRange(sum, target - 2, target + 2);   // 逐条 Math.Round 的整数化误差
    }

    // ── N3：日目标 ≥ 全天能力 ⇒ 逐班填满 + 报当日欠产（原行为不许被摊布改掉）────────
    [Fact]
    public void N3_量大照旧填满并报欠产()
    {
        var r = TaskExploder.Explode(Plate(dayTarget: 5000));   // 远超 2400
        var load = Loads(r);

        Assert.Equal(3, load.Length);
        foreach (var t in load)
            Assert.InRange(t.PlannedHours, 7.5, 8.05);          // 每班都排满

        Assert.Contains(r.Violations, v => v.Code == "当日欠产");
    }

    // ── N4：某班时窗被检修吃光 ⇒ 该班不参与摊，量落到其余班（不许摊丢）──────────────
    [Fact]
    public void N4_检修班不参与摊布()
    {
        var cfg = Plate(dayTarget: 600);
        cfg.Maintenance.Add(new MaintenanceWindow { EquipId = "WK-01", Start = 8, End = 16, Label = "检修" });

        var r = TaskExploder.Explode(cfg);
        var load = Loads(r);

        Assert.Equal(2, load.Length);                                  // 中班没了
        Assert.DoesNotContain(load, t => t.Shift == "中班");
        Assert.InRange(load.Sum(t => t.TargetVolumeM3), 598, 602);     // 600 全落在早/夜两班
        foreach (var t in load) Assert.InRange(t.TargetVolumeM3, 295, 305);
    }

    // ── N5：这个面今天真没量 ⇒ 三班都空闲，原因码才是「欠料·采空」──────────────────
    [Fact]
    public void N5_真没量才叫空闲()
    {
        var r = TaskExploder.Explode(Plate(dayTarget: 0));

        Assert.Empty(Loads(r));
        var idle = r.Tasks.Where(t => t.Process == ProcessType.Idle).ToArray();
        Assert.Equal(3, idle.Length);
        Assert.All(idle, t => Assert.Contains(IncompleteReason.OreShortage, t.Reasons));
    }

    // ── N6：交接损失照旧扣在后两班（摊布用的是**扣完之后**的可用能力）─────────────────
    [Fact]
    public void N6_按扣完交接的可用能力摊()
    {
        var r = TaskExploder.Explode(Plate(dayTarget: 600, handoverH: 0.5));
        var load = Loads(r);

        Assert.Equal(3, load.Length);
        var morning = load.Single(t => t.Shift == "早班");
        var noon = load.Single(t => t.Shift == "中班");

        // 早班 8h 可用、中/夜各 7.5h ⇒ 早班该多摊一点，不是三等分
        Assert.True(morning.TargetVolumeM3 > noon.TargetVolumeM3 + 1,
            $"早班 {morning.TargetVolumeM3:0} 应多于中班 {noon.TargetVolumeM3:0}（早班没有交接损失）");
        Assert.Equal(0.5, noon.StartHour - 8, 2);   // 中班起点被交接推后
    }
}
