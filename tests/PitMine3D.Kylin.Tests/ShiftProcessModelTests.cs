using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 班内工艺·工序推演（§三六〇）—— 任务（日/班）尺度那一层。
///
/// 三条头号判据：
///   ① <b>在途车数 n = λ×W 不许取整</b>。n &lt; 1 时"每隔几分钟才有一台车在途"是真实状态，
///      取整会把它夸大成 1/n 倍，看着像车队一直满负荷。
///   ② <b>推荐车数缺失时不猜是哪一侧短板</b>：班产已经是 min(铲装, 运力)，但没说哪边小；
///      没有荐车数就判不出来，返回「无明显瓶颈」并说明<b>为什么判不了</b>。
///   ③ <b>排土承接不下是系统级卡点</b>，逐线判不出来 —— 单独一条系统级提示。
/// </summary>
public class ShiftProcessModelTests
{
    private static ProcessLine L(int trucks = 3, int rec = 3, double start = 0, double end = 8,
                                 double cap = 300, double perTruck = 40, double cycleH = 0.4,
                                 string zone = "北一采")
    {
        var l = new ProcessLine
        {
            Zone = zone, Shovel = "E1", RecommendedTrucks = rec, Destination = "北排土场",
            StartHour = start, EndHour = end, TargetM3 = 2400,
            GroupCapacityM3PerH = cap, PerTruckM3 = perTruck, CycleH = cycleH,
        };
        for (int i = 0; i < trucks; i++) l.Trucks.Add("T" + i);
        return l;
    }

    // ── 车流：Little 定律 ───────────────────────────────────
    [Fact]
    public void 车流_出车率与在途车数按Little定律且不取整()
    {
        // λ = 300 ÷ 40 = 7.5 车/h；n = 7.5 × 0.4 = 3 台在途
        var l = L(cap: 300, perTruck: 40, cycleH: 0.4);
        Assert.Equal(7.5, l.TripsPerHour!.Value, 6);
        Assert.Equal(3.0, l.TrucksInTransit!.Value, 6);
    }

    [Fact]
    public void 车流_在途不足一台时保留小数不夸大成一台()
    {
        // ★ 取整会把"每隔几分钟才有一台车在途"夸大成 1/n 倍
        var l = L(cap: 30, perTruck: 40, cycleH: 0.4);   // λ=0.75, n=0.3
        Assert.Equal(0.3, l.TrucksInTransit!.Value, 6);
        Assert.True(l.TrucksInTransit!.Value < 1);
    }

    [Fact]
    public void 车流_没解出单车载重或循环时间时判不了不是零()
    {
        Assert.Null(L(perTruck: 0).TripsPerHour);
        Assert.Null(L(perTruck: 0).TrucksInTransit);
        Assert.Null(L(cycleH: 0).TrucksInTransit);
    }

    // ── 逐线瓶颈 ────────────────────────────────────────────
    [Fact]
    public void 瓶颈_配车少于荐车是铲等车()
    {
        var l = L(trucks: 2, rec: 5);
        Assert.Equal(Bottleneck.ShovelWaitsTruck, l.BottleneckAt(4));
        Assert.Contains("缺 3 台", l.BottleneckWhy);
        Assert.Contains("铲将待车", l.BottleneckWhy);
    }

    [Fact]
    public void 瓶颈_配车多于荐车是车等铲()
    {
        var l = L(trucks: 6, rec: 4);
        Assert.Equal(Bottleneck.TruckWaitsShovel, l.BottleneckAt(4));
        Assert.Contains("多 2 台", l.BottleneckWhy);
    }

    [Fact]
    public void 瓶颈_配车等于荐车时供需匹配()
    {
        var l = L(trucks: 4, rec: 4);
        Assert.Equal(Bottleneck.None, l.BottleneckAt(4));
        Assert.Contains("供需匹配", l.BottleneckWhy);
    }

    [Fact]
    public void 瓶颈_没有荐车数时不猜并说明为什么判不了()
    {
        // ★ 班产已经是 min(铲装, 运力)，但没说哪边小 —— 没有荐车数就判不出来
        var l = L(trucks: 3, rec: 0);
        Assert.Equal(Bottleneck.None, l.BottleneckAt(4));
        Assert.Contains("判不出", l.BottleneckWhy);
        Assert.Contains("不猜", l.BottleneckWhy);
    }

    [Fact]
    public void 瓶颈_不在时段内的线不算瓶颈()
        => Assert.Equal(Bottleneck.None, L(trucks: 1, rec: 5, start: 0, end: 4).BottleneckAt(6));

    [Fact]
    public void 进度_按时段线性且钳在零到一()
    {
        var l = L(start: 0, end: 8);
        Assert.Equal(0.5, l.ProgressAt(4), 6);
        Assert.Equal(0, l.ProgressAt(-1), 6);
        Assert.Equal(1, l.ProgressAt(99), 6);
    }

    // ── 系统级瓶颈 ──────────────────────────────────────────
    private static ShiftProcessSystem Sys(params ProcessLine[] lines)
    {
        var s = new ShiftProcessSystem { ShiftName = "早班", StartHour = 0, EndHour = 8 };
        foreach (var l in lines) s.Lines.Add(l);
        return s;
    }

    [Fact]
    public void 系统_取占比最大的那一类瓶颈()
    {
        var s = Sys(L(trucks: 1, rec: 5, zone: "A"), L(trucks: 1, rec: 5, zone: "B"), L(trucks: 9, rec: 5, zone: "C"));
        Assert.Equal(Bottleneck.ShovelWaitsTruck, s.BottleneckAt(4));
    }

    [Fact]
    public void 系统_没有采装线但有工序在干时不算无面()
    {
        var s = Sys();
        s.Steps.Add(new ProcessStep { Stage = ChainStage.Drill, StartHour = 0, EndHour = 8 });
        Assert.Equal(Bottleneck.None, s.BottleneckAt(4));
    }

    [Fact]
    public void 系统_没有采装线也没有工序在干时是无可装作业面()
    {
        var s = Sys();
        Assert.Equal(Bottleneck.NoFace, s.BottleneckAt(4));
        Assert.Contains("无可装作业面", s.BottleneckCaption(4));
    }

    [Fact]
    public void 系统_空闲工序不算在干()
    {
        var s = Sys();
        s.Steps.Add(new ProcessStep { Stage = ChainStage.Load, StartHour = 0, EndHour = 8, IsIdle = true });
        Assert.Equal(Bottleneck.NoFace, s.BottleneckAt(4));
    }

    [Fact]
    public void 系统_结论句逐线摊开为什么()
    {
        var s = Sys(L(trucks: 1, rec: 5, zone: "北一采"));
        string cap = s.BottleneckCaption(4);
        Assert.Contains("铲等车", cap);
        Assert.Contains("北一采", cap);
        Assert.Contains("缺 4 台", cap);
    }

    [Fact]
    public void 系统_班内时钟按相位换成绝对时刻()
    {
        var s = new ShiftProcessSystem { ShiftName = "中班", StartHour = 8, EndHour = 16 };
        Assert.Equal(8, s.ClockAt(0), 6);
        Assert.Equal(12, s.ClockAt(0.5), 6);
        Assert.Equal(16, s.ClockAt(1), 6);
        Assert.Equal(16, s.ClockAt(9), 6);              // 越界钳住
    }

    // ── 由盘子建一天 ────────────────────────────────────────
    private static (ExploderConfig Cfg, ExploderResult Plan) Day(double target = 2400, int trucks = 3, int rec = 3)
    {
        var cfg = new ExploderConfig
        {
            IdPrefix = "D",
            Shifts = { new ShiftWindow("早班", 0, 8), new ShiftWindow("中班", 8, 16) },
        };
        var g = new EquipmentGroup
        {
            MainEquipment = "E1", RecommendedTrucks = rec, GroupCapacityM3PerH = 300,
            LoadTaktMin = 3, CycleTimeMin = 24, MatchFactor = 1, TruckPayloadT = 100,
        };
        for (int i = 0; i < trucks; i++) g.Trucks.Add("T" + i);
        cfg.Faces.Add(new FaceInput
        {
            Zone = "北一采", Process = ProcessType.Load, DayTargetM3 = target, Material = "rock",
            DestinationId = "D1", DestinationName = "北排土场", EquivHaulKm = 2.5, Group = g,
        });
        return (cfg, TaskExploder.Explode(cfg));
    }

    [Fact]
    public void 建一天_采装笔成线穿孔爆破成工序位()
    {
        var (cfg, plan) = Day();
        cfg.Drills.Add(new DrillInput { EquipId = "DR-1", Zone = "北一采", Start = 0, End = 6 });
        plan = TaskExploder.Explode(cfg);

        var day = DayProcessPlan.Build(cfg, plan, new DateTime(2026, 9, 11));
        var zao = day.Shifts.Single(s => s.ShiftName == "早班");
        Assert.NotEmpty(zao.Lines);
        Assert.Contains(zao.Steps, s => s.Stage == ChainStage.Drill);
    }

    [Fact]
    public void 建一天_线上带得出车流所需的载重与循环()
    {
        var (cfg, plan) = Day();
        var zao = DayProcessPlan.Build(cfg, plan, DateTime.Today).Shifts.Single(s => s.ShiftName == "早班");
        var l = zao.Lines.First();
        Assert.True(l.PerTruckM3 > 0, "单车装载量应由载重按物料密度折出来");
        Assert.Equal(24 / 60.0, l.CycleH, 6);
        Assert.NotNull(l.TrucksInTransit);
    }

    [Fact]
    public void 建一天_排土承接明显不足时给系统级提示()
    {
        // ★ 逐线判不出来 —— 它是系统级的卡点
        var (cfg, _) = Day(target: 4000);
        cfg.Faces.Add(new FaceInput
        {
            Zone = "北排", Process = ProcessType.Dump, DayTargetM3 = 200,
            Group = new EquipmentGroup { MainEquipment = "E2", RecommendedTrucks = 2, GroupCapacityM3PerH = 200, Trucks = { "T9", "T8" } },
        });
        var plan = TaskExploder.Explode(cfg);
        var day = DayProcessPlan.Build(cfg, plan, DateTime.Today);
        var zao = day.Shifts.Single(s => s.ShiftName == "早班");
        Assert.Contains("排土只承接", string.Join("|", zao.Notes));
        Assert.Contains("堆在采场边", string.Join("|", zao.Notes));
    }

    [Fact]
    public void 建一天_一条任务都没排的班要说出来()
    {
        // 一个面都没有 ⇒ 装箱连空闲笔都出不了（空闲笔是"某个面这个班没活干"，没有面就没有它）
        var cfg = new ExploderConfig { IdPrefix = "D", Shifts = { new ShiftWindow("早班", 0, 8) } };
        var day = DayProcessPlan.Build(cfg, TaskExploder.Explode(cfg), DateTime.Today);
        Assert.Contains(day.Shifts, s => s.Notes.Any(n => n.Contains("一条任务都没排")));
    }

    [Fact]
    public void 建一天_目标为零的面出的是空闲笔不是没排()
    {
        // 空闲笔本身是信息：这个面这个班没活干（备采用尽 / 目标为 0），与"一条任务都没排"不是一回事
        var (cfg, plan) = Day(target: 0);
        var zao = DayProcessPlan.Build(cfg, plan, DateTime.Today).Shifts.Single(s => s.ShiftName == "早班");
        Assert.Empty(zao.Lines);
        Assert.Contains(zao.Steps, s => s.IsIdle);
        Assert.DoesNotContain(zao.Notes, n => n.Contains("一条任务都没排"));
    }

    [Fact]
    public void 建一天_没有盘子时说清楚不抛()
    {
        var day = DayProcessPlan.Build(null, null, DateTime.Today);
        Assert.Empty(day.Shifts);
        Assert.Contains("推不出来", string.Join("|", day.Notes));
    }

    [Fact]
    public void 建一天_按时刻找班找不到就是找不到()
    {
        var (cfg, plan) = Day();
        var day = DayProcessPlan.Build(cfg, plan, DateTime.Today);
        Assert.Equal("早班", day.ShiftAt(4)!.ShiftName);
        Assert.Equal("中班", day.ShiftAt(12)!.ShiftName);
        Assert.Null(day.ShiftAt(20));                   // 班次之间/之后的空档是真的存在
    }

    // ── 标签 ────────────────────────────────────────────────
    [Fact]
    public void 标签_五道工序与四种瓶颈都有中文名()
    {
        foreach (ChainStage s in Enum.GetValues<ChainStage>())
            Assert.False(string.IsNullOrWhiteSpace(s.Label()));
        foreach (Bottleneck b in Enum.GetValues<Bottleneck>())
            Assert.False(string.IsNullOrWhiteSpace(b.Label()));
        Assert.Equal("铲等车（运力不足）", Bottleneck.ShovelWaitsTruck.Label());
        Assert.Equal("无明显瓶颈", Bottleneck.None.Label());
    }

    [Fact]
    public void 标签_工艺顺序就是枚举顺序()
        => Assert.Equal(new[] { ChainStage.Drill, ChainStage.Blast, ChainStage.Load, ChainStage.Haul, ChainStage.Dump },
                        Enum.GetValues<ChainStage>().OrderBy(x => (int)x));
}
