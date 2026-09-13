// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/DispatchGateTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「任务下达 / 执行调度」两组的三条硬伤的判据。三条都是**恒定值型**的死路——
/// 界面上看不出异样，数字却永远是同一个（0 / 放行 / 样例原因码），
/// 所以每一条都成对写：**开闸要变、关闸必须真的变回去**（[[incline-shape-guards]] 的 F0）。
///
/// <list type="bullet">
/// <item><b>D 组</b> 停机时长恒 0：报修与复机都读"计划时钟此刻"，两头相减必然是 0。
///   口径已挪进 <see cref="FaultEvent.Resume"/>，与任何时钟源无关，故这里测得到。</item>
/// <item><b>I 组</b> 签发口径打架：任务书按<b>逐物料</b>判缺去向、下达却只判主去向，
///   混采面"煤定了、岩没人管"任务书拦、下达放行——而放行的这侧才是真落盘的那一侧。</item>
/// <item><b>R 组</b> 原因码进不来：实绩录入原先是一列自由文本，
///   <see cref="IncompleteReason"/> 永远只能是盘子装配时带进来的那批。</item>
/// </list>
/// </summary>
public class DispatchGateTests
{
    // ═════════════════════════════════════════════════════════════════════════
    //  D 组：停机时长
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>报修于计划时刻 <paramref name="startHour"/>、真实挂钟在 <paramref name="agoH"/> 小时前。</summary>
    private static FaultEvent Reported(double startHour, double agoH, double est = 1.0) => new()
    {
        EquipId = "WK-10",
        PlanDate = "2026-08-11 周二",
        StartHour = startHour,
        EndHour = startHour,                 // 报修时两头相同，正是旧实现复机后仍然相同的那个值
        EstimatedHours = est,
        Status = FaultStatus.Reported,
        ReportedAt = DateTime.Now.AddHours(-agoH),
    };

    // ── D1：复机后停机时长 = 报修到复机的真实经过时长（开闸）────────────────────
    [Fact]
    public void D1_复机后停机时长按经过时长而不是零()
    {
        var f = Reported(startHour: 10.5, agoH: 2.5);
        var now = f.ReportedAt.AddHours(2.5);

        double dur = f.Resume(now);

        Assert.Equal(FaultStatus.Resumed, f.Status);
        Assert.Equal(2.5, dur, 2);
        Assert.Equal(2.5, f.DurationHours, 2);
        Assert.Equal(13.0, f.EndHour, 2);                 // StartHour 10.5 + 2.5

        // ★ 这一条是整组的命门：旧实现 EndHour = max(StartHour, 计划此刻)，
        //   而计划此刻在报修与复机之间根本不动 ⇒ EndHour == StartHour ⇒ 时长 0。
        Assert.True(f.DurationHours > 1e-6,
            "复机后停机时长为 0 —— 多半是又拿「计划时钟此刻 − StartHour」在算了");
        Assert.NotEqual(f.StartHour, f.EndHour);
    }

    // ── D1 关闸：真的零经过就该是零，不许兜一个非零出来 ─────────────────────────
    [Fact]
    public void D1b_报修后随即复机就是零时长()
    {
        var f = Reported(startHour: 10.5, agoH: 0);
        double dur = f.Resume(f.ReportedAt);              // 同一时刻复机

        Assert.Equal(0, dur, 3);
        Assert.Equal(f.StartHour, f.EndHour, 3);
    }

    // ── D2：补录/晚登记时，填写的实际停机压过经过时长 ─────────────────────────
    [Fact]
    public void D2_填了实际停机就以填的为准()
    {
        var f = Reported(startHour: 10.5, agoH: 6);       // 经过 6h
        double dur = f.Resume(DateTime.Now, overrideHours: 3.0);

        Assert.Equal(3.0, dur, 2);
        Assert.Equal(13.5, f.EndHour, 2);

        // 关闸：非正数的"填写"不算填写，仍走经过时长（否则填个 0 就把时长抹平了）
        var g = Reported(startHour: 10.5, agoH: 6);
        Assert.Equal(6.0, g.Resume(g.ReportedAt.AddHours(6), overrideHours: 0), 1);
    }

    // ── D3：跨零点不靠特例 —— 22:00 报修停 4h，EndHour 记 26 而不是回绕成 2 ──────
    [Fact]
    public void D3_跨零点的停机不回绕()
    {
        var f = Reported(startHour: 22, agoH: 4);
        f.Resume(f.ReportedAt.AddHours(4));

        Assert.Equal(26, f.EndHour, 2);
        Assert.Equal(4, f.DurationHours, 2);
        Assert.Equal("02:00+1", DispatchClock.Hm(f.EndHour));

        // 与任务时段求重叠：夜班 [16,24) 只该摊到 2h，另 2h 属于次日
        Assert.Equal(2, f.OverlapHours(16, 24), 2);
    }

    // ── D4：未复机期间仍按预估时长（关闸：别把 Reported 分支顺手改坏了）──────────
    [Fact]
    public void D4_未复机时按预估时长()
    {
        var f = Reported(startHour: 10.5, agoH: 3, est: 1.8);

        Assert.True(f.IsOpen);
        Assert.Equal(1.8, f.DurationHours, 2);            // 不是 0，也不是已经过的 3h
        Assert.Equal(1.3, f.OverlapHours(11, 16), 2);     // [10.5,12.3) ∩ [11,16)
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  I 组：签发口径 —— 缺去向必须逐物料判
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 一条混采采装任务：煤 70% / 硬岩 30%。<paramref name="routeRock"/>=false 时只给煤定了破碎站，
    /// 岩没人管——主去向 <c>HasDestination</c> 照样为真，这正是旧判据放行的那种任务。
    /// </summary>
    private static ProductionTask MixedFace(bool routeRock)
    {
        var t = new ProductionTask
        {
            Id = "T0811-WK10-中班",
            Process = ProcessType.Load,
            Shift = "中班",
            WorkZone = "主采面·东",
            StartHour = 8, EndHour = 16,
            TargetVolumeM3 = 2450,
            Group = new EquipmentGroup { MainEquipment = "WK-10", Trucks = { "T-01", "T-02" } },
            Mix = new MaterialMix
            {
                Shares =
                {
                    new MaterialShare { MaterialCode = MaterialCatalog.Coal, Fraction = 0.7 },
                    new MaterialShare { MaterialCode = MaterialCatalog.Rock, Fraction = 0.3 },
                },
            },
            // 主去向 = 煤的破碎站 —— 注意 HasDestination 因此为真，旧判据看的正是这一个字段
            DestinationId = "CR-1", DestinationName = "1号破碎站",
            DestinationKind = SinkKind.Crusher, HaulDistanceKm = 2.6,
        };
        t.Splits.Add(new MaterialDestination
        {
            MaterialCode = MaterialCatalog.Coal, Fraction = 0.7,
            DestinationId = "CR-1", DestinationName = "1号破碎站",
            DestinationKind = SinkKind.Crusher, HaulKm = 2.6,
        });
        t.Splits.Add(routeRock
            ? new MaterialDestination
            {
                MaterialCode = MaterialCatalog.Rock, Fraction = 0.3,
                DestinationId = "D-IN1", DestinationName = "内排场",
                DestinationKind = SinkKind.InternalDump, HaulKm = 1.26,
            }
            // 分项在、去向空着 —— "煤定了破碎站、岩没人管"。
            // 有分项才谈得上缺：一个分项都不给时逐物料都回落主去向，两套判据本来就一致。
            : new MaterialDestination { MaterialCode = MaterialCatalog.Rock, Fraction = 0.3 });
        return t;
    }

    private static IssueCheck Check(params ProductionTask[] tasks)
        => DispatchEngine.ValidateForIssue(new ExploderResult { Tasks = tasks.ToList() });

    // ── I1：混采面缺次要物料去向 ⇒ 不得下达（开闸）+ 补齐后放行（关闸）──────────
    [Fact]
    public void I1_混采面缺岩的去向不得下达()
    {
        // 开闸：岩没定去向 —— 主去向是有的，旧判据（!HasDestination）会静默放行
        var bad = Check(MixedFace(routeRock: false));

        Assert.False(bad.CanIssue, "混采面的岩没定去向，下达闸门必须拦住——单据发下去现场会把岩拉进破碎站");
        var block = Assert.Single(bad.Blocks.Where(v => v.Code == ViolationCodes.NoDestination));
        Assert.Contains("硬岩", block.Message);            // 点名是哪种物料缺，不能只说"未指定卸点"

        // 关闸：把岩的去向补上，这条阻止项必须真的消失（否则它是恒真的，等于没判）
        var ok = Check(MixedFace(routeRock: true));
        Assert.DoesNotContain(ok.Blocks, v => v.Code == ViolationCodes.NoDestination);
    }

    // ── I2：单一物料、有主去向的常规任务不许被新判据误伤 ────────────────────────
    [Fact]
    public void I2_单一去向的任务照常放行()
    {
        var t = new ProductionTask
        {
            Id = "T0811-WK01-早班",
            Process = ProcessType.Load,
            Shift = "早班",
            WorkZone = "剥离面·北",
            TargetVolumeM3 = 3200,
            Group = new EquipmentGroup { MainEquipment = "WK-01", Trucks = { "T-05" } },
            MaterialCode = MaterialCatalog.Rock,
            DestinationId = "ND-1", DestinationName = "北排土场",
            DestinationKind = SinkKind.ExternalDump, HaulDistanceKm = 3.2,
        };

        Assert.DoesNotContain(Check(t).Blocks, v => v.Code == ViolationCodes.NoDestination);
    }

    // ── I3：不需要卸点的工序不许被要求卸点（NeedsDestination 口径没被顺手改宽）────
    [Fact]
    public void I3_穿孔任务不要求卸点()
    {
        var drill = new ProductionTask
        {
            Id = "T0811-KY01-早班",
            Process = ProcessType.Drill,
            Shift = "早班",
            WorkZone = "待爆区 B-3",
            TargetVolumeM3 = 0,
            Group = new EquipmentGroup { MainEquipment = "KY-01" },
        };

        Assert.DoesNotContain(Check(drill).Blocks, v => v.Code == ViolationCodes.NoDestination);
    }

    // ── I4：缺主设备照旧拦（同一个闸门里的另一条，别改一条崩一条）────────────────
    [Fact]
    public void I4_缺主设备仍然不得下达()
    {
        var t = MixedFace(routeRock: true);
        t.Group.MainEquipment = "";

        Assert.False(Check(t).CanIssue, "没有主设备，单据下达不到人头上");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  R 组：原因码
    // ═════════════════════════════════════════════════════════════════════════

    // ── R1：标签 ↔ 枚举双向对得上（每加一个原因码都必须有文案，否则这条会红）──────
    [Fact]
    public void R1_每个原因码都有文案且能解析回来()
    {
        var all = Enum.GetValues(typeof(IncompleteReason)).Cast<IncompleteReason>().ToList();

        Assert.Equal(all.Count, TaskEnumLabels.AllReasonLabels.Count);
        Assert.Equal(all.Count, TaskEnumLabels.AllReasonLabels.Distinct().Count());   // 文案不许撞车

        foreach (var r in all)
        {
            string label = r.Label();
            Assert.False(string.IsNullOrWhiteSpace(label), $"{r} 没有文案");
            Assert.True(TaskEnumLabels.TryParseReason(label, out var back), $"「{label}」解析不回枚举");
            Assert.Equal(r, back);
        }
    }

    // ── R2：整串解析 —— 顿号多选、去重、认枚举名；认不出的原样带回不静默丢 ────────
    [Fact]
    public void R2_整串原因解析()
    {
        var got = TaskEnumLabels.ParseReasons("设备故障、运力不足、设备故障", out var unknown);

        Assert.Equal(new[] { IncompleteReason.Fault, IncompleteReason.TruckShortage }, got);   // 去重且保序
        Assert.Empty(unknown);

        // 枚举名也认（供外部台账/导入灌入）
        Assert.Equal(new[] { IncompleteReason.Weather }, TaskEnumLabels.ParseReasons("Weather", out _));

        // ★ 认不出来必须点名带回。静默丢弃会让"我明明填了原因"变成无解的怪事
        var mixed = TaskEnumLabels.ParseReasons("设备故障、司机迟到", out var bad);
        Assert.Equal(new[] { IncompleteReason.Fault }, mixed);
        Assert.Equal(new[] { "司机迟到" }, bad);

        // 关闸：空串既不出原因码也不报错
        Assert.Empty(TaskEnumLabels.ParseReasons("", out var none));
        Assert.Empty(none);
    }

    // ── R3：录入 → 存盘 → 回读的往返（实绩录入界面走的就是这条链）────────────────
    [Fact]
    public void R3_原因码往返不丢()
    {
        var task = new ProductionTask { Id = "T0811-WK10-中班", TargetVolumeM3 = 2450, ActualVolumeM3 = 1900 };

        // ① 界面录入的那串字 → 枚举 → 写回任务本体（达成度归因与动态调整读的是它）
        var parsed = TaskEnumLabels.ParseReasons("设备故障、道路·卸点拥堵", out var unknown);
        Assert.Empty(unknown);
        task.Reasons.Clear();
        task.Reasons.AddRange(parsed);

        // ② 落盘
        var rec = new ActualRecord { TaskId = task.Id, Reasons = new List<IncompleteReason>(task.Reasons), Note = "东帮道路塌方" };

        // ③ 回读：原因码回任务、备注回行——备注是人写的字，不是任务上的派生量，不许被回刷覆盖
        Assert.Equal(new[] { IncompleteReason.Fault, IncompleteReason.RoadCongestion }, rec.Reasons);
        Assert.Equal("东帮道路塌方", rec.Note);

        // ④ 回刷到界面列：归一化成标准文案，且能再解析回同一组枚举
        string text = string.Join("、", rec.Reasons.Select(x => x.Label()));
        Assert.Equal("设备故障、道路·卸点拥堵", text);
        Assert.Equal(rec.Reasons, TaskEnumLabels.ParseReasons(text, out _));
    }
}
