// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/LinkDerateTests.cs（逐行对应；仅命名空间/依赖适配）
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
/// 按环节降效（采装 / 运输 / 排土）的判据。
///
/// <para><b>口径</b>：班产是 <c>min(铲装能力 A, 车队运力 B)</c> 压出来的一个标量，单看它分不了环节。
/// 用编组带下来的周期分解还原两侧再分别降：
/// <code>
///   A ∝ 1/τ_L        A' = A·(1−d采装)
///   B ∝ n/T_c        T_c' = τ_L/(1−d采装) + (T_c−τ_L)/(1−d运输)，B' = B·T_c/T_c'
/// </code>
/// 于是 <b>运输降效对采装瓶颈的面不生效、对运力瓶颈的面全额生效</b> —— D2 钉的就是这条，
/// 它是"分环节"到底有没有物理含义的唯一凭据。若哪天有人改成"按运距加权"，D2 会红。</para>
///
/// <para><b>D1 是这一组里最重要的一条</b>：只设全盘值时必须与老口径**逐位相同**。
/// 新增一个维度最常见的坏法不是算错，而是"顺手把没用这个维度的人也一起改了"。</para>
/// </summary>
public class LinkDerateTests
{
    private const double DayHours = 23.0;   // 8 + 7.5 + 7.5（交接班缺省 0.5h、只扣非首班）

    private static ExploderConfig Plate()
        => new()
        {
            DateLabel = "2026-08-20 周四", IdPrefix = "T", EnforceMassBalance = false,
            Shifts =
            {
                new ShiftWindow("早班", 0, 8), new ShiftWindow("中班", 8, 16), new ShiftWindow("夜班", 16, 24),
            },
        };

    /// <summary>一个解过编组的采装面：τ_L=2min、T_c=20min，MF 由入参给（&lt;1 运力瓶颈，&gt;1 采装瓶颈）。</summary>
    private static FaceInput Solved(string zone, double mf, double capH = 100, double target = 9999)
        => new()
        {
            Zone = zone, Process = ProcessType.Load, Material = "岩", DayTargetM3 = target,
            Group = new EquipmentGroup
            {
                MainEquipment = "WK-" + zone, GroupCapacityM3PerH = capH,
                LoadTaktMin = 2, CycleTimeMin = 20, MatchFactor = mf,
            },
        };

    /// <summary>没解过编组的面（τ_L/T_c 为 0）—— 兜底盘子、历史快照都长这样。</summary>
    private static FaceInput Unsolved(string zone, double capH = 100, double target = 9999)
        => new()
        {
            Zone = zone, Process = ProcessType.Load, Material = "岩", DayTargetM3 = target,
            Group = new EquipmentGroup { MainEquipment = "WK-" + zone, GroupCapacityM3PerH = capH },
        };

    // ── D1：只设全盘值 ⇒ 与老口径逐位相同（三格留空、或三格填成同一个数）────────
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(50)]
    public void D1_只设全盘时与老口径逐位相同(double pct)
    {
        var cfg = Plate();
        cfg.WeatherDeratePct = pct;
        var fleetBound = Solved("甲", 0.5);
        var shovelBound = Solved("乙", 2.0);
        var dump = new FaceInput { Zone = "内排", Process = ProcessType.Dump, Group = new EquipmentGroup { MainEquipment = "TY" } };

        Assert.False(cfg.HasLinkDerate);
        foreach (var f in new[] { fleetBound, shovelBound, dump })
            Assert.Equal(cfg.WeatherFactor, cfg.WeatherFactorFor(f), 9);

        // 三格显式填成同一个数，也必须与全盘一模一样（人真会这么填）
        cfg.LoadDeratePct = cfg.HaulDeratePct = cfg.DumpDeratePct = pct;
        Assert.False(cfg.HasLinkDerate);
        foreach (var f in new[] { fleetBound, shovelBound, dump })
            Assert.Equal(cfg.WeatherFactor, cfg.WeatherFactorFor(f), 9);
    }

    // ── D2：运输降效只打在运力瓶颈的面上 ──────────────────────────────────────
    //
    //  τ_L=2、T_c=20、运输降 20% ⇒ T_c' = 2 + 18/0.8 = 24.5
    //    · MF=0.5（运力瓶颈）：系数 = T_c/T_c' = 20/24.5 = 0.8163  ← 全额吃到
    //    · MF=2.0（采装瓶颈）：车队侧降到 2×0.8163=1.63 仍 > 铲装侧 1 ⇒ 系数 = 1  ← 一点不受影响
    [Fact]
    public void D2_运输降效对采装瓶颈的面不生效()
    {
        var cfg = Plate();
        cfg.HaulDeratePct = 20;      // 只降运输，采装/排土跟随全盘 0
        Assert.True(cfg.HasLinkDerate);

        Assert.Equal(20.0 / 24.5, cfg.WeatherFactorFor(Solved("运力瓶颈", 0.5)), 4);
        Assert.Equal(1.0, cfg.WeatherFactorFor(Solved("采装瓶颈", 2.0)), 4);

        // 反向：只降采装 20%，采装瓶颈的面全额吃（0.8），运力瓶颈的面几乎不受影响
        //   T_c' = 2/0.8 + 18 = 20.5；车队侧 0.5×20/20.5 = 0.4878，铲装侧 0.8 ⇒ 取 0.4878
        //   系数 = 0.4878/0.5 = 0.9756
        var c2 = Plate();
        c2.LoadDeratePct = 20;
        Assert.Equal(0.8, c2.WeatherFactorFor(Solved("采装瓶颈", 2.0)), 4);
        Assert.Equal(0.9756, c2.WeatherFactorFor(Solved("运力瓶颈", 0.5)), 3);
    }

    // ── D3：没有周期分解就不许凭空劈 —— 退回全盘值，并且必须报出来 ────────────
    [Fact]
    public void D3_缺周期分解时退回全盘并报账()
    {
        var cfg = Plate();
        cfg.WeatherDeratePct = 10;
        cfg.HaulDeratePct = 40;

        var solved = Solved("解过的", 0.5);
        var raw = Unsolved("没解过的");
        cfg.Faces.Add(solved);
        cfg.Faces.Add(raw);

        Assert.Equal(cfg.WeatherFactor, cfg.WeatherFactorFor(raw), 9);          // 退回全盘 10%
        Assert.NotEqual(cfg.WeatherFactor, cfg.WeatherFactorFor(solved), 3);    // 解过的那个真的分了

        var r = TaskExploder.Explode(cfg);
        Assert.Contains(r.Violations, v => v.Code == ViolationCodes.WeatherDerate
                                        && v.Message.Contains("分不了环节")
                                        && v.Message.Contains("没解过的"));

        // 关闸：面全都解过时不许再报这条（否则它恒真，等于没判）
        var clean = Plate();
        clean.HaulDeratePct = 40;
        clean.Faces.Add(Solved("解过的", 0.5));
        Assert.DoesNotContain(TaskExploder.Explode(clean).Violations,
                              v => v.Code == ViolationCodes.WeatherDerate && v.Message.Contains("分不了环节"));
    }

    // ── D4：排土面只吃排土降效，与采装/运输无关 ───────────────────────────────
    [Fact]
    public void D4_排土面只吃排土降效()
    {
        var cfg = Plate();
        cfg.LoadDeratePct = 50;
        cfg.HaulDeratePct = 50;
        cfg.DumpDeratePct = 10;

        var dump = new FaceInput
        {
            Zone = "内排", Process = ProcessType.Dump,
            Group = new EquipmentGroup { MainEquipment = "TY-01", GroupCapacityM3PerH = 60 },
        };
        Assert.Equal(0.9, cfg.WeatherFactorFor(dump), 6);

        // 排土降效跟随全盘时也要对：只设全盘 30 ⇒ 排土面 0.7
        var c2 = Plate();
        c2.WeatherDeratePct = 30;
        c2.HaulDeratePct = 5;                      // 只动运输，排土仍跟随全盘
        Assert.Equal(0.7, c2.WeatherFactorFor(dump), 6);
    }

    // ── D5：分环节真的改到了装箱出来的量（不是只改了一个只读属性）──────────────
    [Fact]
    public void D5_分环节真的改到装箱的量()
    {
        double Planned(double? haulD, double mf)
        {
            var cfg = Plate();
            cfg.HaulDeratePct = haulD;
            cfg.Faces.Add(Solved("面", mf, capH: 100, target: 9999));
            return TaskExploder.Explode(cfg).Tasks
                   .Where(t => t.Process == ProcessType.Load).Sum(t => t.TargetVolumeM3);
        }

        // 运力瓶颈的面：运输降 20% ⇒ 计划量按 20/24.5 缩
        double baseline = Planned(null, 0.5);
        double derated = Planned(20, 0.5);
        // 装箱逐班取整后求和，与解析值差个位数是正常的；容差给 2 m³，量级仍钉死
        Assert.Equal(100 * DayHours, baseline, 0);
        Assert.True(Math.Abs(derated - 100 * DayHours * (20.0 / 24.5)) <= 2,
            $"运输降 20% 后本该排 {100 * DayHours * (20.0 / 24.5):0} m³，实际 {derated:0}");

        // 采装瓶颈的面：同样降运输，量一分不少（这条是 D2 在装箱侧的回响）
        Assert.Equal(Planned(null, 2.0), Planned(20, 2.0), 0);
    }

    // ── D6：锚点进 ApplyTo / IsEmpty / Clone，且重排整份带走 ───────────────────
    [Fact]
    public void D6_锚点接线与重排带走()
    {
        Assert.False(new CompileAnchors { HaulDeratePct = 15 }.IsEmpty);
        Assert.Equal(15, new CompileAnchors { HaulDeratePct = 15 }.Clone().HaulDeratePct);

        try
        {
            var cfg = Plate();
            CompileOverrides.SetForTest(new CompileAnchors { LoadDeratePct = 5, HaulDeratePct = 25 });
            string label = CompileOverrides.ApplyTo(cfg);

            Assert.Equal(5, cfg.LoadDeratePct);
            Assert.Equal(25, cfg.HaulDeratePct);
            // 排土那一项没锚过 ⇒ 必须保持 null（跟随全盘），不许被顺手填成 0
            Assert.Null(cfg.DumpDeratePct);
            Assert.Contains("分环节降效", label);
        }
        finally { CompileOverrides.SetForTest(null); }

        // 重排：分环节降效必须跟着盘子过去，否则重排按另一套口径算而且不报错
        var b = Plate();
        b.HaulDeratePct = 60;
        b.Faces.Add(Solved("面", 0.5, capH: 100, target: 9999));
        var r = TaskRescheduler.Reschedule(b, new List<ProductionTask>(), 0, new Dictionary<string, AdjustStrategy>());
        double planned = r.Plan.Tasks.Where(t => t.Process == ProcessType.Load).Sum(t => t.TargetVolumeM3);

        // T_c' = 2 + 18/0.4 = 47 ⇒ 系数 20/47 = 0.4255；带丢了的话会排到 2300 上下
        Assert.True(planned <= 100 * DayHours * (20.0 / 47.0) + 1,
            $"重排排了 {planned:0} m³，超过分环节降效后的能力 —— 运输降效多半没跟着盘子过去");
    }
}
