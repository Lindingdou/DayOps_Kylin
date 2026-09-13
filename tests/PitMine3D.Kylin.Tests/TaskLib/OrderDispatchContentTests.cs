// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/OrderDispatchContentTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// OD 组：**任务编制结果 → 任务书 / 任务下达 / 派车派工** 的内容完整性。
///
/// <para>被判的那批死路（都在 2026-08-20 的五工序闭环落地之后暴露，且**一处都不报错**）：</para>
/// <list type="number">
/// <item><b>四本方量账只认一本</b> —— 三个窗都写死 <c>TargetVolumeM3</c>，
///   于是穿孔（控制方量）、运输（承运吨）、排土（分解器路的排弃占容）在正式单据上全是「—」；
///   而标量装箱路的排土把占容方写在 <c>TargetVolumeM3</c> 里，任务书又拿它再乘一次 Kr。
///   同一列上一半是空的、另一半是放大的。</item>
/// <item><b>穿孔的量在映射时整段丢弃</b> —— 逐班分解器明明算出了控制方量，
///   <c>ShiftPlanAssembler.ToTasks</c> 只接采装那一笔，其余不接也不记账。</item>
/// <item><b>爆破笔把整盘下达拦死</b> —— 按已定口径爆破不指人，而「缺主设备」那条闸不分工序，
///   于是只要本班有一炮，一条任务都签发不出去，理由却是"未指定主设备"。</item>
/// <item><b>缺去向的闸漏掉全部运输笔</b> —— 判据写的是 <c>TargetVolumeM3 &gt; 1</c>，
///   而运输笔的量在 <c>HaulTonnageT</c>，那一列恒为 0 ⇒ 整类静默跳过。</item>
/// <item><b>混采面两笔运输撞稳定键</b> —— 日期/班次/车队/工序/作业面五样全同，
///   单据按键存字典，后写的盖掉先写的。</item>
/// </list>
///
/// <para>每条都按 F0 成对写：**开闸要变、关闸必须真的变回去**（还原实现的那一半用注释点名）。</para>
/// </summary>
public class OrderDispatchContentTests
{
    private const string Date = "2026-08-20 周四";
    private const string Shift = "早班";

    // ── 造件 ─────────────────────────────────────────────────────────────────

    private static EquipmentGroup G(string main, params string[] trucks)
        => new() { MainEquipment = main, Trucks = trucks.ToList() };

    private static ProductionTask Load(string equip, string zone, double m3, string material = "岩")
        => new()
        {
            Id = $"L-{equip}-{zone}", Process = ProcessType.Load, Shift = Shift, WorkZone = zone,
            TargetVolumeM3 = m3, MaterialCode = MaterialCatalog.CodeFromText(material), Material = material,
            Group = G(equip, "T-01", "T-02"),
            DestinationName = "内排土场1", DestinationKind = SinkKind.InternalDump, HaulDistanceKm = 1.4,
            Status = TaskStatus.Planned,
        };

    private static ProductionTask Haul(ProductionTask src, double tonnage, int trips, string material = "岩")
        => new()
        {
            Id = $"H-{src.Id}-{material}", Process = ProcessType.Haul, Shift = Shift, WorkZone = src.WorkZone,
            HaulTonnageT = tonnage, TripCount = trips,
            MaterialCode = MaterialCatalog.CodeFromText(material), Material = material,
            Group = G(src.Group.MainEquipment + " 车队"),
            DestinationName = src.DestinationName, DestinationKind = src.DestinationKind,
            HaulDistanceKm = src.HaulDistanceKm,
            SourceTaskId = src.Id, Status = TaskStatus.Planned,
        };

    private static ProductionTask Drill(string equip, string zone, double controlM3,
                                        int? holes = null, double? meters = null)
        => new()
        {
            Id = $"D-{equip}-{zone}", Process = ProcessType.Drill, Shift = Shift, WorkZone = zone,
            ControlVolumeM3 = controlM3, Group = G(equip), Status = TaskStatus.Planned,
            Drill = holes is > 0 || meters is > 0 ? new DrillQuantity { PlanHoles = holes, PlanMeters = meters } : null,
        };

    /// <summary>爆破笔：按已定口径**主设备为空、量为 0**——这正是它被两条闸误伤的原因。</summary>
    private static ProductionTask Blast(string zone)
        => new()
        {
            Id = $"B-{zone}", Process = ProcessType.Blast, Shift = Shift, WorkZone = zone,
            Group = new EquipmentGroup { MainEquipment = "" }, Status = TaskStatus.Planned,
        };

    private static ProductionTask Dump(string equip, string zone, double dumpM3)
        => new()
        {
            Id = $"P-{equip}-{zone}", Process = ProcessType.Dump, Shift = Shift, WorkZone = zone,
            DumpVolumeM3 = dumpM3, Group = G(equip), DestinationName = "内排土场1",
            DestinationKind = SinkKind.InternalDump, Status = TaskStatus.Planned,
        };

    // ═════════════════════════════════════════════════════════════════════════
    //  OD1–OD3　四本账：各取各的，缺的写「—」，合计不合并
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// OD1 四道有量的工序**各自取到自己那本账**。
    /// <para>F0 还原：把 <c>TaskQuantity.Of</c> 一律改成读 <c>TargetVolumeM3</c>，
    /// 除采装外三条断言全红（那正是补这一组之前的状态）。</para>
    /// </summary>
    [Fact]
    public void OD1_四本方量账各取各的()
    {
        var load = Load("WK-10", "采场1·岩1308", 12000);
        var haul = Haul(load, tonnage: 30000, trips: 300);
        var drill = Drill("KJ-2", "采场1·岩1308", controlM3: 55000, holes: 12, meters: 168);
        var dump = Dump("TY-1", "内排土场1·L76", dumpM3: 9000);

        var qL = TaskQuantity.Of(load);
        Assert.True(qL.HasValue);
        Assert.Equal("原位实方", qL.Basis);
        Assert.Equal(12000, qL.Value, 3);
        Assert.Equal("m³", qL.Unit);

        var qH = TaskQuantity.Of(haul);
        Assert.True(qH.HasValue);
        Assert.Equal("承运量", qH.Basis);
        Assert.Equal(30000, qH.Value, 3);
        Assert.Equal("t", qH.Unit);                       // 吨是三个体积口径之间唯一守恒的量
        Assert.Contains("300 车次", qH.Second);

        var qD = TaskQuantity.Of(drill);
        Assert.True(qD.HasValue);
        Assert.Equal("控制方量", qD.Basis);
        Assert.Equal(55000, qD.Value, 3);
        Assert.Contains("12 孔", qD.Second);
        Assert.Contains("168", qD.Second);

        var qP = TaskQuantity.Of(dump);
        Assert.True(qP.HasValue);
        Assert.Equal("排弃占容", qP.Basis);
        Assert.Equal(9000, qP.Value, 3);
    }

    /// <summary>
    /// OD2 <b>没有出处的量位写「—」，不写 0</b>。
    /// 爆破按口径就没有方量（不是缺数据），必须能与"该有却没录"分开。
    /// </summary>
    [Fact]
    public void OD2_爆破的量是无口径而不是零()
    {
        var q = TaskQuantity.Of(Blast("采场1·岩1308"));

        Assert.False(q.HasValue);
        Assert.Equal("—", q.Caption);                     // 不是 "0 m³"
        Assert.Equal("", q.Basis);
        Assert.Contains("爆破无方量口径", q.Why);           // 为什么没有，写出来

        // 对照：采装笔没录量时，理由是"未记采装量" —— 两种「—」不许混成一种，
        // 混了就没人去补后者（前者是口径，后者是漏录）。
        var empty = Load("WK-11", "采场2·煤0801", 0);
        Assert.False(TaskQuantity.Of(empty).HasValue);
        Assert.Contains("未记", TaskQuantity.Of(empty).Why);
    }

    /// <summary>
    /// OD3 合计**分账列示**，四本账不并成一个总量；运输功逐笔算。
    /// <para>F0 还原：把 <c>Sum</c> 改成"把四个量加进同一个 double"，
    /// 下面每一条独立断言都会指向同一个数而全红。</para>
    /// </summary>
    [Fact]
    public void OD3_合计分账不并成一个总量()
    {
        var load = Load("WK-10", "采场1·岩1308", 12000);
        var haul = Haul(load, tonnage: 30000, trips: 300);      // 运距 1.4 km
        var drill = Drill("KJ-2", "采场1·岩1308", 55000, 12, 168);
        var dump = Dump("TY-1", "内排土场1·L76", 9000);
        var blast = Blast("采场2·岩1296");

        var s = TaskQuantity.Sum(new[] { load, haul, drill, dump, blast });

        Assert.Equal(12000, s.LoadInSituM3, 3);
        Assert.Equal(30000, s.HaulTonnageT, 3);
        Assert.Equal(55000, s.DrillControlM3, 3);
        Assert.Equal(9000, s.DumpM3, 3);
        Assert.Equal(1, s.BlastTasks);
        Assert.Equal(300, s.Trips);

        // 运输功 = 承运吨 × 本笔运距（不是拿采装笔的物料流反推）
        Assert.Equal(30000 * 1.4, s.WorkTKm, 3);
        Assert.Equal(1.4, s.AvgHaulKm, 3);

        // 抬头文案里四本账各占一段，且**没有**"合计 N 万m³"这种把它们加起来的说法
        string cap = s.Caption;
        Assert.Contains("控制方量", cap);
        Assert.Contains("m³实方", cap);
        Assert.Contains("承运", cap);
        Assert.Contains("排弃占容", cap);
        Assert.Contains("不派设备", cap);      // 爆破那一段写明它没有量、也不指人
    }

    /// <summary>
    /// OD3b 标量装箱那条路把**排弃占容写在 TargetVolumeM3 里**（历史口径）——
    /// 这一格必须原样收下，<b>不许再乘一次膨胀系数</b>。
    /// <para>还原自检：改成 <c>t.TargetDumpM3</c>（= TargetVolumeM3 × Kr）就会大出一截。</para>
    /// </summary>
    [Fact]
    public void OD3b_装箱路的排土占容不再被乘第二次()
    {
        var boxed = new ProductionTask
        {
            Id = "P-boxed", Process = ProcessType.Dump, Shift = Shift, WorkZone = "内排土场1·L76",
            TargetVolumeM3 = 9000,          // 装箱路：这里放的已经是占容方
            MaterialCode = MaterialCatalog.CodeFromText("岩"), Material = "岩",
            Group = G("TY-1"),
        };

        var q = TaskQuantity.Of(boxed);
        Assert.True(q.HasValue);
        Assert.Equal(9000, q.Value, 3);
        Assert.True(boxed.TargetDumpM3 > 9000 + 1,
            "前提：TargetDumpM3 确实会再乘一次 Kr —— 若这条不成立，本判据就判不到那个 bug 了");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  OD4–OD5　下达闸门
    // ═════════════════════════════════════════════════════════════════════════

    private static ExploderResult Res(params ProductionTask[] tasks)
    {
        var r = new ExploderResult();
        r.Tasks.AddRange(tasks);
        return r;
    }

    /// <summary>
    /// OD4 <b>爆破笔不得因"未指定主设备"拦住下达</b>。
    /// <para>这条闸原来不分工序，于是只要本班有一炮，整盘一条都签发不出去 ——
    /// 而报的理由会让人跑去设备台账里找爆破队，那张表根本不存在。</para>
    /// <para>F0 关闸自检见 <see cref="OD4b_真的漏填主设备仍然拦住"/>：
    /// 换成采装笔缺主设备，必须照样拦。</para>
    /// </summary>
    [Fact]
    public void OD4_爆破笔不指人不拦下达()
    {
        var load = Load("WK-10", "采场1·岩1308", 12000);
        var chk = DispatchEngine.ValidateForIssue(Res(load, Blast("采场2·岩1296")), Shift);

        Assert.True(chk.CanIssue, "爆破按口径不指人，不该被「缺主设备」拦下：" + chk.BlockText);
        Assert.DoesNotContain("未指定主设备", chk.BlockText);
    }

    /// <summary>
    /// OD4c <b>两炮不许被判成「设备双占」</b>。
    ///
    /// <para>「缺主设备」那条闸的孪生兄弟：<c>CheckConstraints</c> 按 <c>MainEquipment</c> 分组判时段重叠，
    /// 而爆破笔的主设备恒为空 ⇒ 本班所有爆破笔归成同一台**"空设备"**，
    /// 两炮时窗一挨上就报 Error 级「时段重叠」，整盘不得下达 ——
    /// 而消息里点名的那台设备没有名字，照着它根本查不下去。
    /// <b>没有机器的活占不住任何一台机器。</b></para>
    ///
    /// <para>2026-08-20 的真盘子实测就是这样：18 条任务里 2 炮，硬生生多出 1 条阻止项。
    /// F0 还原：把 <c>CheckConstraints</c> 里的 <c>!IsNullOrWhiteSpace(MainEquipment)</c> 去掉，本条立刻红。</para>
    /// </summary>
    [Fact]
    public void OD4c_同班两炮不算设备双占()
    {
        var cfg = new ExploderConfig();
        var res = new ExploderResult();
        var b1 = Blast("采场1·岩1308"); b1.StartHour = 12; b1.EndHour = 12.67;
        var b2 = Blast("采场2·岩1296"); b2.StartHour = 12.2; b2.EndHour = 12.87;   // 故意重叠
        res.Tasks.Add(b1); res.Tasks.Add(b2);
        res.Tasks.Add(Load("WK-10", "采场1·岩1308", 12000));

        TaskExploder.CheckOnly(cfg, res);

        Assert.DoesNotContain(res.Violations, v => v.Code == "设备双占");

        // 关闸自检：**真设备**重叠仍然要判出来（否则这条断言证明不了它判的是"空设备"）
        var res2 = new ExploderResult();
        var a = Load("WK-10", "采场1·岩1308", 12000); a.StartHour = 8; a.EndHour = 16;
        var b = Load("WK-10", "采场2·岩1296", 9000); b.StartHour = 12; b.EndHour = 20;
        res2.Tasks.Add(a); res2.Tasks.Add(b);
        TaskExploder.CheckOnly(cfg, res2);
        Assert.Contains(res2.Violations, v => v.Code == "设备双占");
    }

    /// <summary>OD4b 关闸自检：**真的漏填**主设备的采装笔必须照样拦住。</summary>
    [Fact]
    public void OD4b_真的漏填主设备仍然拦住()
    {
        var bad = Load("", "采场3·岩1284", 8000);
        bad.Group = new EquipmentGroup { MainEquipment = "" };

        var chk = DispatchEngine.ValidateForIssue(Res(bad), Shift);

        Assert.False(chk.CanIssue);
        Assert.Contains("未指定主设备", chk.BlockText);
    }

    /// <summary>
    /// OD5 <b>缺去向的闸要判到运输笔</b>。
    /// <para>原判据写的是 <c>TargetVolumeM3 &gt; 1</c>，而运输笔的量在 <c>HaulTonnageT</c>，
    /// 那一列恒为 0 ⇒ 整类静默跳过。运输笔恰恰是"这车拉到哪"最直接的那一笔。</para>
    /// </summary>
    [Fact]
    public void OD5_运输笔缺去向照样拦住下达()
    {
        var load = Load("WK-10", "采场1·岩1308", 12000);
        var haul = Haul(load, tonnage: 30000, trips: 300);
        haul.DestinationId = ""; haul.DestinationName = "";     // 去向丢了

        var chk = DispatchEngine.ValidateForIssue(Res(load, haul), Shift);

        Assert.False(chk.CanIssue, "运输笔缺去向必须拦：任务书写不出「这车拉到哪」");
        Assert.Contains("未指定卸点", chk.BlockText);

        // 关闸自检：去向补回去就该放行（否则这条断言证明不了它判的是"缺去向"）
        haul.DestinationName = "内排土场1";
        Assert.True(DispatchEngine.ValidateForIssue(Res(load, haul), Shift).CanIssue);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  OD10　派车单展不开时，原因只许有一个出处
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// OD10 <b>展不开车次的原因由引擎结构化给出</b>，界面不许自己再判一遍。
    ///
    /// <para>踩过的实况：派车单空态面板自己按 <c>TruckPayloadT&lt;=0</c> 判成「解不出单车载重」，
    /// 而同一屏下方引擎逐条写的是「尚未配车」—— 两处说同一件事，说的不一样。
    /// 现在原因只在 <see cref="DispatchEngine"/> 里判一次，落到 <see cref="DispatchPlan.Skips"/> 上。</para>
    /// </summary>
    [Fact]
    public void OD10_展不开的原因由引擎分类给出()
    {
        // ① 缺去向
        var noDest = Load("WK-10", "采场1·岩1308", 12000);
        noDest.DestinationId = ""; noDest.DestinationName = "";

        // ② 有去向、有运距，但一台车都没配
        var noTrucks = Load("WK-11", "采场2·岩1296", 9000);
        noTrucks.Group = new EquipmentGroup { MainEquipment = "WK-11" };   // Trucks 空
        noTrucks.Group.RecommendedTrucks = 6;

        var plan = DispatchEngine.Expand(new[] { noDest, noTrucks }, Date, Shift, DispatchRuleKind.FixedAssignment);

        Assert.Empty(plan.Orders);
        Assert.Equal(2, plan.Skips.Count);

        var a = plan.Skips.Single(x => x.WorkZone == "采场1·岩1308");
        Assert.Equal(TripSkipKind.NoDestination, a.Kind);
        Assert.Contains("未指定卸点", a.Reason);          // 原话原样带出来，不改写

        var b = plan.Skips.Single(x => x.WorkZone == "采场2·岩1296");
        Assert.Equal(TripSkipKind.NoTrucks, b.Kind);      // ← 不是 NoFleetSolution
        Assert.Equal(6, b.RecommendedTrucks);

        // 每一条 Skip 都要能在人读说明里找到对应的那一行 —— 两个出口说的是同一件事
        foreach (var sk in plan.Skips)
            Assert.Contains(plan.Notes, n => n.Contains(sk.WorkZone, StringComparison.Ordinal));
    }

    /// <summary>OD10b 展得开的任务**不进** Skips —— 否则空态会把正常的班次也说成"展不开"。</summary>
    [Fact]
    public void OD10b_展得开的任务不进Skips()
    {
        var ok = Load("WK-10", "采场1·岩1308", 12000);
        ok.Group.Trucks.AddRange(new[] { "T-01", "T-02" });
        ok.Group.TruckPayloadT = 100;
        ok.Group.LoadTaktMin = 3.2; ok.Group.CycleTimeMin = 22.4; ok.Group.MatchFactor = 1.0;

        var plan = DispatchEngine.Expand(new[] { ok }, Date, Shift, DispatchRuleKind.FixedAssignment);

        Assert.DoesNotContain(plan.Skips, x => x.Kind == TripSkipKind.NoTrucks);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  OD6　运输随采装连带（口径：用户 2026-08-20 定）
    // ═════════════════════════════════════════════════════════════════════════

    private static TaskInstance Inst(ProductionTask t, bool issued, bool withdrawn = false)
    {
        var it = new TaskInstance { StableKey = TaskKey.Of(t, Date), TaskId = t.Id, PlanDate = Date, Shift = t.Shift };
        if (issued) { it.IssuedBy = "调度张"; it.IssuedAt = new DateTime(2026, 8, 20, 7, 30, 0); }
        if (withdrawn) it.Withdraw("调度李", new DateTime(2026, 8, 20, 9, 15, 0));
        return it;
    }

    /// <summary>
    /// OD6 运输笔的单据轴**跟随源采装笔**：源笔一签发，它就算下达了（不另建单据）。
    /// <para>不这样接的话，能造出「料挖了、但没人下达把它拉走」这种自相矛盾的状态，
    /// 而看板/工序进度/达成度没有任何一处判得出来。</para>
    /// </summary>
    [Fact]
    public void OD6_运输笔随源采装笔一起下达()
    {
        var load = Load("WK-10", "采场1·岩1308", 12000);
        var haul = Haul(load, 30000, 300);
        var tasks = new List<ProductionTask> { load, haul };

        var stat = DispatchStateLink.Apply(tasks, Date, new[] { Inst(load, issued: true) });

        Assert.Equal(DispatchState.Issued, load.Dispatch);
        Assert.Equal(DispatchState.Issued, haul.Dispatch);          // ← 连带
        Assert.Equal(TaskStatus.Dispatched, haul.Status);
        Assert.Equal(2, stat.Issued);
        Assert.False(DispatchStateLink.IsSelfIssuable(haul));       // 界面不给它勾选框
        Assert.True(DispatchStateLink.IsSelfIssuable(load));
    }

    /// <summary>OD6b 撤回也连带；而**排土笔独立**（有真设备真人，单独签发）。</summary>
    [Fact]
    public void OD6b_撤回连带而排土独立()
    {
        var load = Load("WK-10", "采场1·岩1308", 12000);
        var haul = Haul(load, 30000, 300);
        var dump = Dump("TY-1", "内排土场1·L76", 9000);
        var tasks = new List<ProductionTask> { load, haul, dump };

        DispatchStateLink.Apply(tasks, Date, new[] { Inst(load, issued: true, withdrawn: true) });

        Assert.Equal(DispatchState.Withdrawn, load.Dispatch);
        Assert.Equal(DispatchState.Withdrawn, haul.Dispatch);       // ← 连带撤回
        Assert.Equal(DispatchState.NotIssued, dump.Dispatch);       // ← 排土不跟着走
        Assert.True(DispatchStateLink.IsSelfIssuable(dump));
    }

    /// <summary>
    /// OD6c 挂不上源笔的运输笔按**未下达**处理，<b>绝不默认继承"已下达"</b>；
    /// 而且它<b>不看自己的单据</b> —— 单据轴只由源采装笔说了算。
    ///
    /// <para>这一条要判得到，就得给这笔孤儿运输**造一张自己的"已下达"单据**：
    /// 不造的话，"跟随源笔失败 ⇒ 未下达" 与 "根本没有单据 ⇒ 未下达" 长得一模一样，
    /// 把实现还原回去这条判据照样绿 —— 那就是一条空过的判据。</para>
    /// </summary>
    [Fact]
    public void OD6c_源笔不在盘子里时按未下达且不看自己的单据()
    {
        var load = Load("WK-10", "采场1·岩1308", 12000);
        var haul = Haul(load, 30000, 300);
        haul.SourceTaskId = "已经不在本盘的那一笔";

        // ★ 这笔运输自己有一张"已下达"的单据。跟随口径下它必须被**忽略**：
        //   两处都能说"下达没有"的那一刻起，两处就会对不上。
        var stat = DispatchStateLink.Apply(new List<ProductionTask> { load, haul }, Date,
                                          new[] { Inst(load, issued: true), Inst(haul, issued: true) });

        Assert.Equal(DispatchState.Issued, load.Dispatch);
        Assert.Equal(DispatchState.NotIssued, haul.Dispatch);
        Assert.Equal(1, stat.Issued);
        Assert.Equal(1, stat.NotIssued);
        Assert.Contains("随采装", DispatchStateLink.FollowerCaption(haul, new[] { load, haul }));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  OD7　稳定键
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// OD7 混采面派生出的**两笔运输不许撞稳定键**。
    /// <para>它俩的 日期/班次/车队/工序/作业面 五样全同 —— 键里不带物料码就是同一个键：
    /// 单据按键存字典，后写的盖掉先写的；实绩会把煤那趟的量记到岩那趟上；
    /// 「已下达 N 条」也会少一条。而每一条看着都正常。</para>
    /// <para>F0 还原：把 <c>TaskKey.ZoneKeyOf</c> 改回 <c>t.WorkZone</c>，本条立刻红。</para>
    /// </summary>
    [Fact]
    public void OD7_混采面的两笔运输稳定键不撞()
    {
        var load = Load("WK-10", "采场1·混采", 12000);
        var coal = Haul(load, 6000, 60, "煤");
        var rock = Haul(load, 24000, 240, "岩");

        Assert.NotEqual(TaskKey.Of(coal, Date), TaskKey.Of(rock, Date));

        // 而采装笔的键**不受影响**（它没有物料码这一维）—— 改键最怕的就是波及已落盘的单据
        var same = Load("WK-10", "采场1·混采", 12000);
        Assert.Equal(TaskKey.Of(load, Date), TaskKey.Of(same, Date));
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  OD8　派生笔不重复计量
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// OD8 运输/排土笔进了单据之后，**采装的实方合计不许跟着涨**。
    /// <para>三本账各记各的，任何一处把它们并起来，抬头那个"计划工作量"就不对应任何真实量
    /// —— 而它正是调度签发时看的那个数。</para>
    /// </summary>
    [Fact]
    public void OD8_加进派生笔不改变采装实方合计()
    {
        var load = Load("WK-10", "采场1·岩1308", 12000);
        double before = TaskQuantity.Sum(new[] { load }).LoadInSituM3;

        var withDerived = TaskQuantity.Sum(new[] { load, Haul(load, 30000, 300), Dump("TY-1", "内排土场1·L76", 9000) });

        Assert.Equal(before, withDerived.LoadInSituM3, 3);
        Assert.Equal(12000, withDerived.LoadInSituM3, 3);
        Assert.Equal(9000, withDerived.DumpM3, 3);       // 排土记在自己那本账上
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  OD9　分解器：穿孔量不再整段丢弃
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// OD9 逐班分解器算出的**控制方量**要落到穿孔任务上。
    /// <para>原实现只接 <c>Process == Load</c> 的那一笔，其余不接也不记账 ——
    /// 于是正式单据上穿孔那一行的量永远是「—」，而分解器那头算得清清楚楚。</para>
    /// <para>F0 还原：删掉 <c>ToTasks</c> 里的 <c>else if (Drill)</c> 分支，本条立刻红。</para>
    /// </summary>
    [Fact]
    public void OD9_分解器的控制方量落到穿孔任务上()
    {
        var plan = new ShiftPlanResult();
        plan.Rows.Add(new ShiftTaskRow
        {
            Date = "2026-08-20", Shift = Shift, StartHour = 8, EndHour = 16, EffectiveHours = 7.5,
            MachineId = "KJ-2", Process = ProcessType.Drill,
            FaceName = "采场1·岩1308", UnitId = "U-1308-07",
            MaterialCode = MaterialCatalog.CodeFromText("岩"),
            VolumeM3 = 55000, Basis = "控制方量",
        });
        plan.Rows.Add(new ShiftTaskRow
        {
            Date = "2026-08-20", Shift = Shift, StartHour = 8, EndHour = 16, EffectiveHours = 7.5,
            MachineId = "WK-10", Process = ProcessType.Load,
            FaceName = "采场1·岩1308", UnitId = "U-1308-07",
            MaterialCode = MaterialCatalog.CodeFromText("岩"),
            VolumeM3 = 12000, Basis = "原位实方",
        });

        var cfg = new ExploderConfig();
        cfg.Drills.Add(new DrillInput { EquipId = "KJ-2", Zone = "采场1·岩1308", HoleCount = 12, HoleLengthM = 168 });

        var tasks = ShiftPlanAssembler.ToTasks(plan, cfg, onlyDate: "2026-08-20");

        var drill = tasks.Single(t => t.Process == ProcessType.Drill);
        Assert.Equal(55000, drill.ControlVolumeM3, 3);
        Assert.Equal(0, drill.TargetVolumeM3, 3);              // 口径没混：实方那一列仍然是空的
        Assert.Equal(12, drill.Drill?.PlanHoles);
        Assert.Equal(168, drill.Drill?.PlanMeters ?? 0, 3);

        var load = tasks.Single(t => t.Process == ProcessType.Load);
        Assert.Equal(12000, load.TargetVolumeM3, 3);
        Assert.Equal(0, load.ControlVolumeM3, 3);              // 反向也不许串

        // 单据上这两行各写各的数，不再是一个「—」一个数
        Assert.Equal("控制方量", TaskQuantity.Of(drill).Basis);
        Assert.Equal("原位实方", TaskQuantity.Of(load).Basis);
    }

    /// <summary>
    /// OD9b <b>孔数/延米没录时留 null，不拿控制方量反推</b>。
    /// 反推要孔网参数，推出来的数在单据上看着完全正常 —— 那种数最难查。
    /// </summary>
    [Fact]
    public void OD9b_没有穿孔计划时孔数留空不反推()
    {
        var plan = new ShiftPlanResult();
        plan.Rows.Add(new ShiftTaskRow
        {
            Date = "2026-08-20", Shift = Shift, StartHour = 8, EndHour = 16,
            MachineId = "KJ-9", Process = ProcessType.Drill, FaceName = "采场9·岩1200",
            MaterialCode = MaterialCatalog.CodeFromText("岩"), VolumeM3 = 30000,
        });

        var drill = ShiftPlanAssembler.ToTasks(plan, new ExploderConfig(), onlyDate: "2026-08-20")
                                      .Single(t => t.Process == ProcessType.Drill);

        Assert.Equal(30000, drill.ControlVolumeM3, 3);
        Assert.Null(drill.Drill);                                  // 没有就是没有
        Assert.Equal("", TaskQuantity.Of(drill).Second);
    }
}
