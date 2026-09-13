// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/TruckFlowSimTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 车流离散事件仿真的口径（DE 组）。**纯算例，不碰库**。
///
/// <para>
/// 仿真存在的理由只有一个：解析解算不出**排队**。稳态公式（τ_L / T_c / MF / Erlang-C）
/// 的前提是"这条线独立、到达平稳"，而现场是多条线抢同一个卸点 ——
/// 卸点不够时队会**反压**回铲上，铲装完一车没车可装只能等。
/// </para>
/// <para>
/// 所以这一组断的是：<b>不该排队时不许排队</b>（否则仿真在凭空造损失）、
/// <b>该排队时必须排出来</b>（否则它白跑一趟）、以及<b>同一份输入两次跑必须一模一样</b>
/// （掷随机数的仿真钉不住判据，也没人能复算）。
/// </para>
/// </summary>
public sealed class TruckFlowSimTests
{
    private readonly ITestOutputHelper _out;
    public TruckFlowSimTests(ITestOutputHelper o) => _out = o;

    private const double Payload = 100;      // W_t
    private const double Takt = 4;           // τ_L 装车节拍 min
    private const double Cycle = 24;         // T_c min（单程行驶 = (24−4−1.5−1)/2 = 8.75）

    /// <summary>一条线：一个面 + 一台铲 + n 台车 + 一个去向。</summary>
    private static (ExploderConfig Cfg, List<ProductionTask> Tasks) Case(
        int trucks, double tonnage, double acceptTph = 0, int lanes = 1, string sinkName = "内排场")
    {
        var cfg = new ExploderConfig
        {
            Shifts = { new ShiftWindow("早", 0, 8) },
        };
        var tasks = new List<ProductionTask>();

        for (int i = 0; i < lanes; i++)
        {
            string zone = $"面{i + 1}";
            var face = new FaceInput
            {
                Zone = zone,
                Process = ProcessType.Load,
                MaterialCode = MaterialCatalog.Rock,
                DestinationName = sinkName,
            };
            face.Group.MainEquipment = $"S-{i + 1}";
            face.Group.TruckPayloadT = Payload;
            face.Group.LoadTaktMin = Takt;
            face.Group.CycleTimeMin = Cycle;
            for (int k = 0; k < trucks; k++) face.Group.Trucks.Add($"T-{i + 1}-{k + 1}");
            cfg.Faces.Add(face);

            tasks.Add(new ProductionTask
            {
                Id = $"L-{i + 1}",
                Process = ProcessType.Haul,
                WorkZone = zone,
                Shift = "早",
                StartHour = 0,
                EndHour = 8,
                DestinationName = sinkName,
                HaulTonnageT = tonnage,
                SourceTaskId = $"src-{i + 1}",
            });
        }

        var sink = new SinkNode { Id = "SK-1", Name = sinkName, AcceptTph = acceptTph };
        cfg.Sinks.Put(sink);
        return (cfg, tasks);
    }

    /// <summary>
    /// DE1 <b>配到最优车数、卸点不限时，铲基本不等车、也不该有卸点排队</b>。
    /// <para>n* = T_c/τ_L = 6：这时供需刚好匹配，排队是资源争用造成的，而这里没有争用。</para>
    /// </summary>
    [Fact]
    public void DE1_BalancedFleet_NoQueueNoStarvation()
    {
        var (cfg, tasks) = Case(trucks: 6, tonnage: 100_000);
        var r = TruckFlowSimulator.Run(tasks, cfg);
        var lane = Assert.Single(r.Lanes);

        _out.WriteLine($"车次 {lane.TripsDone} · 铲等车 {lane.ShovelIdleMin:0.#} min · 卸点排队 {lane.QueueAtSinkMin:0.#} min");
        Assert.Equal(0, lane.QueueAtSinkMin, 1);                 // 只有一条线，卸点不会有争用
        Assert.True(lane.ShovelIdleMin <= Cycle, $"铲等车 {lane.ShovelIdleMin:0.#} min —— 配到 n* 还等车就是模型错了");
        Assert.True(lane.TripsDone > 0);
    }

    /// <summary>
    /// DE2 <b>车少于最优配车 ⇒ 铲等车</b>（运力不足的直接证据）。
    /// </summary>
    [Fact]
    public void DE2_TooFewTrucks_ShovelStarves()
    {
        var (cfg, tasks) = Case(trucks: 2, tonnage: 100_000);
        var r = TruckFlowSimulator.Run(tasks, cfg);
        var lane = Assert.Single(r.Lanes);

        _out.WriteLine($"2 台车：车次 {lane.TripsDone} · 铲等车 {lane.ShovelIdleMin:0.#} min · MF {lane.MatchFactorSim}");
        Assert.True(lane.ShovelIdleMin > 60, "车明显不够，铲却没等车 —— 模型没算供给不足");
        Assert.True(lane.MatchFactorSim < 1, "匹配系数应当 <1（运力不足）");
        Assert.Equal(0, lane.QueueAtSinkMin, 1);
    }

    /// <summary>
    /// DE3 <b>多条线抢一个卸点、卸点能力又不够 ⇒ 排队排出来，而且会反压回铲上</b>。
    /// <para>这是仿真唯一不可替代的地方：解析解只能算出"超了多少"，算不出"排多久"。</para>
    /// </summary>
    [Fact]
    public void DE3_SharedTightSink_QueuesAndBackPressure()
    {
        // 四条线同抢一个卸点，通过能力压到 600 t/h（每车占 10 min）
        var (cfg, tasks) = Case(trucks: 6, tonnage: 100_000, acceptTph: 600, lanes: 4);
        var r = TruckFlowSimulator.Run(tasks, cfg);

        _out.WriteLine($"四条线：拉下 {r.TonnageT:N0} t · 卸点排队 {r.QueueAtSinkMin / 60:0.#} h · 铲等车 {r.ShovelIdleMin / 60:0.#} h");
        foreach (var n in r.Notes) _out.WriteLine("  " + n);

        Assert.Equal(4, r.Lanes.Count);
        Assert.True(r.QueueAtSinkMin > 60, "卸点被四条线抢，却一分钟队都没排 —— 反压没模拟出来");
        Assert.True(r.ShovelIdleMin > 0, "卸点堵住了，铲却一分钟没等 —— 反压没传回铲上");
        Assert.True(r.Attainment < 1.0, "卸点不够时仍然跑满了计划量 —— 那就没有瓶颈可言了");
    }

    /// <summary>
    /// DE4 <b>关闸自检</b>：把卸点能力放开（不限），同一组线的排队必须回到 0。
    /// <para>不做这一条，DE3 的"排队 &gt; 0"可能只是模型恒定在排队。</para>
    /// </summary>
    [Fact]
    public void DE4_UncappedSink_QueueGoesAway()
    {
        var tight = TruckFlowSimulator.Run(Case(6, 100_000, acceptTph: 600, lanes: 4).Tasks,
                                           Case(6, 100_000, acceptTph: 600, lanes: 4).Cfg);
        var (cfg, tasks) = Case(trucks: 6, tonnage: 100_000, acceptTph: 0, lanes: 4);   // 0 = 没录能力，按 t_卸 常数
        var loose = TruckFlowSimulator.Run(tasks, cfg);

        _out.WriteLine($"能力紧：排队 {tight.QueueAtSinkMin / 60:0.#} h · 能力松：排队 {loose.QueueAtSinkMin / 60:0.#} h");
        Assert.True(tight.QueueAtSinkMin > loose.QueueAtSinkMin,
            "放开卸点能力之后排队没减少 —— 这条闸没有真的在起作用");
        Assert.True(loose.TonnageT > tight.TonnageT, "放开能力之后拉的量没变多");
    }

    /// <summary>
    /// DE5 <b>确定性</b>：同一份输入跑两次，结果必须一模一样。
    /// <para>掷随机数的仿真钉不住判据，也没人能复算 —— 排队全部来自资源争用，不来自骰子。</para>
    /// </summary>
    [Fact]
    public void DE5_Deterministic()
    {
        var a = TruckFlowSimulator.Run(Case(5, 80_000, acceptTph: 900, lanes: 3).Tasks,
                                       Case(5, 80_000, acceptTph: 900, lanes: 3).Cfg);
        var b = TruckFlowSimulator.Run(Case(5, 80_000, acceptTph: 900, lanes: 3).Tasks,
                                       Case(5, 80_000, acceptTph: 900, lanes: 3).Cfg);

        Assert.Equal(a.TonnageT, b.TonnageT, 6);
        Assert.Equal(a.QueueAtSinkMin, b.QueueAtSinkMin, 6);
        Assert.Equal(a.ShovelIdleMin, b.ShovelIdleMin, 6);
    }

    /// <summary>
    /// DE6 <b>缺参数就不跑</b>：载重/节拍/循环时间/配车缺一样，这条线直接不参与。
    /// <para>猜出来的排队时间比没有更坏 —— 它会让人以为瓶颈在别处。</para>
    /// </summary>
    [Fact]
    public void DE6_MissingFleetSolution_LaneIsSkipped()
    {
        var (cfg, tasks) = Case(trucks: 6, tonnage: 100_000);
        cfg.Faces[0].Group.TruckPayloadT = 0;          // 载重没解出来

        var r = TruckFlowSimulator.Run(tasks, cfg);
        _out.WriteLine(string.Join(" | ", r.Notes));

        Assert.Empty(r.Lanes);
        Assert.Contains(r.Notes, x => x.Contains("缺一样就不跑"));
    }
}
