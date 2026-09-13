// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/PreparedReserveTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 备采家底（采准三量）与采掘单元身份的判据。
///
/// <para><b>这组要挡的是"拿 0 当采空"</b>：备采储量这一列刚加上，绝大多数面还没录。
/// 如果判据写成 <c>reserve &lt; dayTarget ⇒ 采空</c>，那么每一个没录的面（reserve=0）
/// 天天都会报"今天就采空"——整屏假告警，真正快断档的那个面反而淹了。
/// 所以 P1 与 P4 是一对：**未录必须如实说"判不了"，录了才判**。</para>
///
/// <para><b>UnitId 那两条（P5/P6）挡的是另一类空过</b>：字段加在 FaceInput 上很容易，
/// 忘了在装箱时抄进 ProductionTask 就等于白加——界面上作业面台账显示得好好的，
/// 任务书/派车单/图上定位却全是空。P5 连 Idle 任务一起判：空闲条也要能定位到体，
/// 否则"这台铲今天在哪待着"在图上就是个谜。</para>
/// </summary>
public class PreparedReserveTests
{
    /// <summary>一个采装面的最小盘子。reserve≤0 表示"未录"。</summary>
    private static ExploderConfig Plate(double dayTarget, double reserve, double minPreparedDays = 0, string unitId = "")
    {
        var cfg = new ExploderConfig
        {
            DateLabel = "2026-08-11 周二",
            IdPrefix = "T0811",
            EnforceMassBalance = false,
            MinPreparedDays = minPreparedDays,
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
            AvailableReserveM3 = reserve,
            UnitId = unitId,
            Group = new EquipmentGroup { MainEquipment = "WK-01", GroupCapacityM3PerH = 100 },
        });
        return cfg;
    }

    private static PlanViolation[] Reserve(ExploderResult r)
        => r.Violations.Where(v => v.Code == ViolationCodes.PreparedReserve).ToArray();

    // ── P1：没录备采 → 只说"判不了"（Info），绝不报采空 ──────────────────────
    [Fact]
    public void P1_未录备采只报判不了()
    {
        var v = Reserve(TaskExploder.Explode(Plate(dayTarget: 2000, reserve: 0)));

        Assert.Single(v);
        Assert.Equal(ViolationSeverity.Info, v[0].Severity);
        Assert.Contains("未录备采储量", v[0].Message);
    }

    // ── P2：备采 < 当日目标 → Error（计划本身不成立，不是预警）────────────────
    [Fact]
    public void P2_备采不够当天就报错()
    {
        var v = Reserve(TaskExploder.Explode(Plate(dayTarget: 2000, reserve: 1500)));

        Assert.Single(v);
        Assert.Equal(ViolationSeverity.Error, v[0].Severity);
        Assert.Contains("今天就会采空", v[0].Message);
    }

    // ── P3：够采但低于保有下限 → Warn；下限关掉必须真的不报（成对判）──────────
    [Fact]
    public void P3_低于保有下限报警而关闸不报()
    {
        // 备采 6000 / 日目标 2000 = 3 天 < 下限 5 天
        var warn = Reserve(TaskExploder.Explode(Plate(2000, 6000, minPreparedDays: 5)));
        Assert.Single(warn);
        Assert.Equal(ViolationSeverity.Warn, warn[0].Severity);
        Assert.Contains("保有下限", warn[0].Message);

        // 关闸：下限 = 0 即不校核，同一份数据必须一条都不报
        var off = Reserve(TaskExploder.Explode(Plate(2000, 6000, minPreparedDays: 0)));
        Assert.Empty(off);
    }

    // ── P4：备采充足 → 一条都不报（否则 P3 恒真，等于没判）────────────────────
    [Fact]
    public void P4_备采充足不报()
        => Assert.Empty(Reserve(TaskExploder.Explode(Plate(2000, 60_000, minPreparedDays: 5))));

    // ── P5：单元号随任务下沉，空闲任务也要带 ────────────────────────────────
    [Fact]
    public void P5_单元号进任务且空闲条也带()
    {
        // 有量的面：单元号要进每一条采装任务。
        // ★ 前提改过（2026-08-11）：原先写的是"日目标小到一个班就干完 → 后两班会出空闲条"，
        //   那是**贪心按班填满**时代的行为。日目标现在按各班可用能力占比摊到三个班，
        //   量小只是各班都干得少，不会再有"前面替后面干完了"的空闲条（见 ShiftSpreadTests）。
        var work = TaskExploder.Explode(Plate(dayTarget: 400, reserve: 50_000, unitId: "3煤-B12-P03"));
        var load = work.Tasks.Where(t => t.Process == ProcessType.Load).ToList();
        Assert.NotEmpty(load);
        Assert.All(load, t => Assert.Equal("3煤-B12-P03", t.UnitId));

        // 空闲条只在**这个面今天真没量**时出现（备采用尽 / 今天不采）—— 那才是"空闲"的本义。
        var none = TaskExploder.Explode(Plate(dayTarget: 0, reserve: 50_000, unitId: "3煤-B12-P03"));
        var idle = none.Tasks.Where(t => t.Process == ProcessType.Idle).ToList();

        Assert.NotEmpty(idle);   // 没有空闲条就说明这条判据没真跑到 Idle 分支
        Assert.All(idle, t => Assert.Equal("3煤-B12-P03", t.UnitId));
    }

    // ── P6：可采天数 —— 未录一律 null，不许退化成 0 ─────────────────────────
    [Theory]
    [InlineData(2000, 0, null)]        // 未录备采
    [InlineData(0, 6000, null)]        // 今天不采这个面
    [InlineData(2000, 6000, 3.0)]      // 正常
    public void P6_可采天数未录时为空(double dayTarget, double reserve, double? want)
    {
        var f = new FaceInput { DayTargetM3 = dayTarget, AvailableReserveM3 = reserve };

        if (want == null) Assert.Null(f.PreparedDays);
        else Assert.Equal(want.Value, f.PreparedDays!.Value, 3);
    }

    // ── P7：绑没绑单元是个明确状态，空白不算"绑了" ──────────────────────────
    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("3煤-B12-P03", true)]
    public void P7_绑单元的判定(string unitId, bool bound)
        => Assert.Equal(bound, new FaceInput { UnitId = unitId }.HasUnit);
}
