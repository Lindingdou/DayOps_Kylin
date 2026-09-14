using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 降效与备采保有下限接进装箱（§三五三）。此前 <c>LinkDerate</c> 是本仓已有的纯函数、
/// <b>一个调用方都没有</b>；<c>ExploderConfig</c> 里也没有降效这一维，
/// 于是"天气恶劣 → 全盘降效回摊"这条设计口径落不下去。
///
/// 头号判据：**只设全盘降效时，结果必须与没有分环节那一版逐位相同**（早退分支）——
/// 这条兼容性靠肉眼看公式看不出来，只能钉在判据上。
/// </summary>
public class ExploderDerateTests
{
    private static ShiftWindow Zao => new("早", 0, 8);

    private static ExploderConfig OneFace(double target = 9999, double capPerH = 100,
                                          ProcessType process = ProcessType.Load)
        => new()
        {
            IdPrefix = "D", Shifts = { Zao },
            Faces = { new FaceInput { Zone = "A", Process = process, DayTargetM3 = target,
                Group = new EquipmentGroup { MainEquipment = "E1", GroupCapacityM3PerH = capPerH,
                    RecommendedTrucks = 0, Trucks = new List<string> { "T1" } } } },
        };

    private static ShiftTask Load(ExploderResult r) => r.Tasks.Single(t => t.Process != ProcessType.Idle);

    // ── 降效落在能力上 ─────────────────────────────────────
    [Fact]
    public void 降效_打的是每小时干得少而不是少上班()
    {
        // ★ 混在时窗上，「今天雨大」会被记成「今天少上了两小时班」，甘特上条形位置全错
        var cfg = OneFace(target: 400, capPerH: 100);
        cfg.WeatherDeratePct = 20;
        var t = Load(TaskExploder.Explode(cfg));
        Assert.Equal(0.0, t.StartHour, 6);                 // 班首没动
        Assert.Equal(5.0, t.PlannedHours, 1);              // 400 ÷ (100×0.8) = 5h（不降效是 4h）
        Assert.Equal(400, t.TargetVolumeM3, 6);
    }

    [Fact]
    public void 降效_当日干不完照常报欠产()
    {
        var cfg = OneFace(target: 800, capPerH: 100);      // 不降效正好 8h 干完
        cfg.WeatherDeratePct = 50;
        var r = TaskExploder.Explode(cfg);
        Assert.Equal(400, Load(r).TargetVolumeM3, 6);
        Assert.Contains(r.Violations, v => v.Code == "当日欠产");
    }

    [Fact]
    public void 降效_截到95不许降成零()
    {
        var cfg = OneFace();
        cfg.WeatherDeratePct = 300;
        Assert.Equal(0.05, cfg.WeatherFactor, 6);          // 全停产是不排班，不是降效 100%
    }

    [Fact]
    public void 降效_不设时系数为一()
        => Assert.Equal(1.0, OneFace().WeatherFactor, 9);

    // ── 分环节 ─────────────────────────────────────────────
    [Fact]
    public void 分环节_三项都留空时与只设全盘逐位相同()
    {
        var a = OneFace(target: 700); a.WeatherDeratePct = 30;
        var b = OneFace(target: 700); b.WeatherDeratePct = 30;
        b.LoadDeratePct = b.HaulDeratePct = b.DumpDeratePct = 30;   // 显式写成与全盘同值
        Assert.Equal(Load(TaskExploder.Explode(a)).TargetVolumeM3,
                     Load(TaskExploder.Explode(b)).TargetVolumeM3, 9);
        Assert.False(a.HasLinkDerate);
        Assert.False(b.HasLinkDerate);
    }

    [Fact]
    public void 分环节_给了不同值才算分环节()
    {
        var cfg = OneFace();
        cfg.WeatherDeratePct = 10;
        cfg.HaulDeratePct = 30;
        Assert.True(cfg.HasLinkDerate);
    }

    [Fact]
    public void 分环节_排土面直吃排土降效()
    {
        var cfg = OneFace(process: ProcessType.Dump);
        cfg.LoadDeratePct = 50; cfg.HaulDeratePct = 40; cfg.DumpDeratePct = 10;
        Assert.Equal(0.9, cfg.WeatherFactorFor(cfg.Faces[0]), 6);
    }

    [Fact]
    public void 分环节_编组没经周期求解时退回全盘不凭空劈()
    {
        // ★ 编个份额出来分，比不分更坏 —— 那是给一个不存在的精度
        var cfg = OneFace();
        cfg.LoadDeratePct = 10; cfg.HaulDeratePct = 40;
        Assert.False(cfg.Faces[0].Group.HasCycleBreakdown);
        Assert.Equal(0.9, cfg.WeatherFactorFor(cfg.Faces[0]), 6);   // 退采装侧
    }

    [Fact]
    public void 分环节_采装瓶颈的面运输降效不生效()
    {
        // MF>1 ⇒ 车队还有富余，路上慢一点压不到班产
        var cfg = OneFace();
        var g = cfg.Faces[0].Group;
        g.LoadTaktMin = 3; g.CycleTimeMin = 24; g.MatchFactor = 1.6;
        cfg.LoadDeratePct = 0; cfg.HaulDeratePct = 40;
        Assert.Equal(1.0, cfg.WeatherFactorFor(cfg.Faces[0]), 6);
    }

    [Fact]
    public void 分环节_运力瓶颈的面运输降效全额生效()
    {
        var cfg = OneFace();
        var g = cfg.Faces[0].Group;
        g.LoadTaktMin = 3; g.CycleTimeMin = 24; g.MatchFactor = 0.6;   // 车不够，B 侧是瓶颈
        cfg.LoadDeratePct = 0; cfg.HaulDeratePct = 40;
        double f = cfg.WeatherFactorFor(cfg.Faces[0]);
        Assert.True(f < 0.85, $"运力瓶颈面应显著降效，实得 {f:0.###}");
    }

    [Fact]
    public void 分环节_没有面时用全盘值()
    {
        var cfg = OneFace();
        cfg.WeatherDeratePct = 25;
        Assert.Equal(0.75, cfg.WeatherFactorFor(null), 6);
    }

    // ── 配煤那一步用同一个闸 ─────────────────────────────────
    [Fact]
    public void 配煤_面日产能上限跟着降效一起缩()
    {
        // ★ 两处取值不同就会出现「配煤说移得动、装箱那边排不下」这种自相矛盾，而且谁都不报错
        var cfg = new ExploderConfig
        {
            Shifts = { Zao }, Blend = new BlendStandard { MaxAshPct = 12 }, EffHoursPerDay = 20,
            WeatherDeratePct = 50,
            Faces =
            {
                new FaceInput { Zone = "高灰", Process = ProcessType.Load, DayTargetM3 = 4000,
                    Quality = new CoalQuality { AshPct = 20 },
                    Group = new EquipmentGroup { MainEquipment = "E1", GroupCapacityM3PerH = 100, Trucks = { "T1" } } },
                new FaceInput { Zone = "低灰", Process = ProcessType.Load, DayTargetM3 = 1000,
                    Quality = new CoalQuality { AshPct = 8 },
                    Group = new EquipmentGroup { MainEquipment = "E2", GroupCapacityM3PerH = 100, Trucks = { "T2" } } },
            },
        };
        TaskExploder.Explode(cfg);
        // 低灰面产能上限 = 100 × 20 × 0.5 = 1000 = 它本来的目标 ⇒ 一点都移不进来
        Assert.Equal(1000, cfg.Faces[1].DayTargetM3, 6);
    }

    // ── 备采保有下限 ───────────────────────────────────────
    [Fact]
    public void 备采_不足下限时预警且说明为什么()
    {
        var cfg = OneFace(target: 1000);
        cfg.MinPreparedDays = 5;
        cfg.Faces[0].AvailableReserveM3 = 3000;            // 只够 3 天
        var v = Assert.Single(TaskExploder.Explode(cfg).Violations.Where(x => x.Code == "备采保有"));
        Assert.Contains("仅够 3.0 天", v.Message);
        Assert.Contains("采准", v.Message);
    }

    [Fact]
    public void 备采_够用就不报()
    {
        var cfg = OneFace(target: 1000);
        cfg.MinPreparedDays = 5;
        cfg.Faces[0].AvailableReserveM3 = 9000;
        Assert.DoesNotContain(TaskExploder.Explode(cfg).Violations, x => x.Code == "备采保有");
    }

    [Fact]
    public void 备采_没录储量的面不判不是采空()
    {
        // ★ 0 = 未录，不是"采空" —— 拿 0 当采空会让每个没录的面都报警，报到没人看
        var cfg = OneFace(target: 1000);
        cfg.MinPreparedDays = 5;
        cfg.Faces[0].AvailableReserveM3 = 0;
        Assert.DoesNotContain(TaskExploder.Explode(cfg).Violations, x => x.Code == "备采保有");
    }

    [Fact]
    public void 备采_下限为零时整条校核关掉()
    {
        var cfg = OneFace(target: 1000);
        cfg.MinPreparedDays = 0;
        cfg.Faces[0].AvailableReserveM3 = 1;
        Assert.DoesNotContain(TaskExploder.Explode(cfg).Violations, x => x.Code == "备采保有");
    }

    [Fact]
    public void 备采_只判采装面()
    {
        var cfg = OneFace(target: 1000, process: ProcessType.Dump);
        cfg.MinPreparedDays = 5;
        cfg.Faces[0].AvailableReserveM3 = 100;
        Assert.DoesNotContain(TaskExploder.Explode(cfg).Violations, x => x.Code == "备采保有");
    }

    // ── 重排要把锚点带过去 ───────────────────────────────────
    [Fact]
    public void 重排_降效与备采下限不许悄悄回缺省()
    {
        // ★ 不克隆的话重排出来的盘子算得出来、也不报错 —— 只是和原始计划不是一套口径
        var cfg = OneFace(target: 800, capPerH: 100);
        cfg.WeatherDeratePct = 50; cfg.MinPreparedDays = 7; cfg.EffHoursPerDay = 18;
        cfg.LoadDeratePct = 10; cfg.HaulDeratePct = 20; cfg.DumpDeratePct = 30;
        cfg.Faces[0].AvailableReserveM3 = 1234;
        cfg.Faces[0].Group.LoadTaktMin = 3; cfg.Faces[0].Group.CycleTimeMin = 24; cfg.Faces[0].Group.MatchFactor = 0.9;

        var r = TaskRescheduler.Reschedule(cfg, System.Array.Empty<ShiftTask>(), 0,
                                           new Dictionary<string, AdjustStrategy>());
        // 降效带过去了 ⇒ 800 m³ 在 8h × 100 × 0.5 的能力下排不满，仍报欠产
        Assert.Contains(r.Plan.Violations, v => v.Code == "当日欠产");
    }
}
