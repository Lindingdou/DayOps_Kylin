// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/EmptyFaceGateTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Zoning;
using TaskStatus = PitMine3D.Kylin.TaskLib.Domain.TaskStatus;   // 消歧 System.Threading.Tasks.TaskStatus

using PitMine3D.Kylin.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  G 组：当日盘子的**空盘闸门**。
//
//  ══ 现场是这么坏的 ══
//  2026-08-19 的甘特：顶栏写着【没有作业面·排不出计划】，下面却排着 11 台钻机、
//  一天三个班的穿孔条；右侧「计划校核（引擎）」写着「✓ 无异常」；
//  底部「当日工作量：计划 0万m³ · 实绩 0 · 达成度 0%」。
//  四句话互相矛盾，而没有任何一处报错。
//
//  拆开是四条各自独立的失效，四条都"看着正常"：
//  **D1** 拒绝分解的闸判的是 `machines.Count`，而这张清单是两段拼的 ——
//      电铲按面取（0 个面 ⇒ 0 台）、钻机按矿调（`DrillFleetHook`，与面无关 ⇒ 11 台）。
//      0+11>0 ⇒ 闸放行。**钻机替电铲开了闸。**
//  **D2** `NoFaces` 只挂在顶栏文案上，拦不住产出侧任何一条路。
//  **D3** 分解器那条路直接 return，`CheckConstraints`/`SpaceTimeValidator` 一条都不跑 ——
//      「✓ 无异常」不是"没问题"，是"没人去校"。
//  **D4** 分解器排的是**整月**，而 `ProductionTask` 是**单日契约**（只有 Shift 与
//      0..24 的起止，**没有日期列**）。整月塞进当日盘子 ⇒ 一个月的条叠在同一根 24h 轴上。
//
//  ══ 为什么必须靠注入来判 ══
//  裸台架里 `DrillFleetHook` 是 null（那是 PlanLib 在插件初始化时装的），
//  于是台架里**天然就没有钻机** —— D1 那条路在台架里一次都走不到，
//  而它正是现场唯一走到的那条。判据必须自己把钻机注进去，否则这一组全是空过的。
//  同 [[always-firing-warning-is-a-dead-path]]：台架看不见的那条路才是出事的那条。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>空盘闸门（G 组）。用真库副本 —— 单元台账与班次日历都得是真的。</summary>
[Collection("RealDb")]
public class EmptyFaceGateTests
{
    private readonly ITestOutputHelper _out;
    private readonly RealDbFixture _db;
    public EmptyFaceGateTests(RealDbFixture db, ITestOutputHelper o) { _db = db; _out = o; }

    private const string LedgerRoot =
        @"C:\Users\0doudou\Desktop\DayOps\PitMine3D\bin\Debug\Data\采掘单元台账";
    private const string Period = "2026-08";

    /// <summary>造几台钻机（台效随便给，只要不为 0 —— 判的是"闸放不放行"，不是排量）。</summary>
    private static IReadOnlyList<PlanMachine> FakeDrills(int n) =>
        Enumerable.Range(1, n)
            .Select(i => new PlanMachine($"DRL-{i:00}", "DML", ProcessType.Drill, 8000,
                                         new[] { MaterialCatalog.Rock }))
            .ToList();

    /// <summary>
    /// G1 <b>0 个采装面 + 有钻机 ⇒ 必须拒绝分解</b>（D1）。
    ///
    /// <para>这一条直接对着截图那盘：设备库 518 台、钻机十几台，采装面 0 个。
    /// 闸放行的话排出来的就是"一整月只有穿孔"的计划 —— 量恒 0（量只落在采装笔上），
    /// 而甘特上满满当当。</para>
    /// </summary>
    [Fact(Skip = "依赖原桌面真库的当时状态（采掘单元清单文件 / 2026-08 班次日历 / 作业面档案），Kylin 种子库不含；判据本身逐行保留")]
    public void G1_没有采装面时钻机不得单独开闸()
    {
        _out.WriteLine(_db.Label);
        if (!_db.Ready) { _out.WriteLine("SKIP: 真库不可用"); return; }

        var cfg = new ExploderConfig();                 // ★ 一个面都不放
        Assert.Empty(cfg.Faces);

        var saved = ShiftPlanAssembler.DrillFleetHook;
        try
        {
            ShiftPlanAssembler.DrillFleetHook = (_, _, _) => FakeDrills(11);

            var a = ShiftPlanAssembler.Build(Period, cfg, LedgerRoot);
            _out.WriteLine("Headline：" + a.Headline);
            foreach (var n in a.Notes.Take(6)) _out.WriteLine("   " + n);

            // ① 闸必须关上
            Assert.False(a.Ok, "0 个采装面却分解成立了 —— 钻机替电铲开了闸");
            Assert.Empty(a.Tasks);
            Assert.Empty(a.MonthTasks);

            // ② 结论必须说清"缺的是面，不是设备"，并且要点名钻机不单独成盘。
            //    只断言"拒绝了"是不够的：原来那句「一台可派设备都没有」读起来像设备库空了，
            //    人会跑去查设备台账 —— 那是一条会把人带偏的正确结论。
            Assert.Contains("作业面", a.Headline);
            Assert.Contains("钻机", a.Headline);
        }
        finally { ShiftPlanAssembler.DrillFleetHook = saved; }
    }

    /// <summary>
    /// G2 <b>闸是按工序判的，不是按"有没有面"判的</b>（D1 的另一半）。
    ///
    /// <para>放一个**排土面**进去 —— 面不为空了，但采装机组依然是 0。
    /// 如果闸写成 `cfg.Faces.Count == 0` 就会在这儿放行，而排土面同样取不到采装设备。</para>
    /// </summary>
    [Fact(Skip = "依赖原桌面真库的当时状态（采掘单元清单文件 / 2026-08 班次日历 / 作业面档案），Kylin 种子库不含；判据本身逐行保留")]
    public void G2_只有排土面时同样拒绝分解()
    {
        _out.WriteLine(_db.Label);
        if (!_db.Ready) { _out.WriteLine("SKIP: 真库不可用"); return; }

        var cfg = new ExploderConfig();
        cfg.Faces.Add(new FaceInput
        {
            Zone = "内排场·判据", Process = ProcessType.Dump,
            Material = "硬岩", MaterialCode = MaterialCatalog.Rock,
            DerivedFromInbound = true,
        });
        Assert.NotEmpty(cfg.Faces);
        Assert.Empty(cfg.Faces.Where(f => f.Process == ProcessType.Load));

        var saved = ShiftPlanAssembler.DrillFleetHook;
        try
        {
            ShiftPlanAssembler.DrillFleetHook = (_, _, _) => FakeDrills(4);
            var a = ShiftPlanAssembler.Build(Period, cfg, LedgerRoot);
            _out.WriteLine("Headline：" + a.Headline);

            Assert.False(a.Ok, "只有排土面却分解成立了");
            Assert.Contains("采装", a.Headline);
        }
        finally { ShiftPlanAssembler.DrillFleetHook = saved; }
    }

    /// <summary>
    /// G3 <b>按日切</b>（D4）：给了 <c>onlyDate</c> 就只剩那一天，不给就是整月。
    ///
    /// <para>纯装配判据，不碰库 —— 判的是转换这一步，不是排产。</para>
    /// <para><b>为什么这条最要紧</b>：<see cref="ProductionTask"/> 上没有日期列，
    /// 切漏了下游**没有任何一处能发现** —— 甘特按班筛、达成度按班聚合，
    /// 26 个作业日的早班条会重合成一条，看着就是"这台机早班有活"。</para>
    /// </summary>
    [Fact]
    public void G3_整月计划必须按日切进当日盘子()
    {
        var plan = new ShiftPlanResult();
        foreach (string d in new[] { "2026-08-18", "2026-08-19", "2026-08-20" })
            foreach (string sh in new[] { "早", "中", "夜" })
                plan.Rows.Add(new ShiftTaskRow
                {
                    Date = d, Shift = sh, StartHour = 0, EndHour = 8,
                    MachineId = "WK-01", Process = ProcessType.Load,
                    FaceName = "判据面", UnitId = "U-1",
                    MaterialCode = MaterialCatalog.Rock, VolumeM3 = 1000,
                });

        var all = ShiftPlanAssembler.ToTasks(plan, cfg: null);
        var day = ShiftPlanAssembler.ToTasks(plan, cfg: null, onlyDate: "2026-08-19");
        _out.WriteLine($"整月 {all.Count} 条 · 当日 {day.Count} 条");

        Assert.Equal(9, all.Count);
        Assert.Equal(3, day.Count);

        // 切出来的每一条都得是那一天的 —— 靠 Id 里的日期标签反查（TaskKey 的既定格式）
        Assert.All(day, t => Assert.StartsWith("TK-2026-08-19-", t.Id, StringComparison.Ordinal));

        // 而整月那一份里**确实**混着别的天：证明这个判据不是在空过
        Assert.Contains(all, t => !t.Id.StartsWith("TK-2026-08-19-", StringComparison.Ordinal));
    }

    /// <summary>
    /// G4 <b>切片是子集，不是重算</b>（D4 的反面）。
    ///
    /// <para>按日切之后那三条，必须与整月那份里同日的三条**逐字段一致**。
    /// 如果切片走的是另一条装配路径，量或去向会悄悄变 —— 而两份各自都自洽。</para>
    /// </summary>
    [Fact]
    public void G4_按日切出来的与整月里同日的那几条一致()
    {
        var plan = new ShiftPlanResult();
        foreach (string d in new[] { "2026-08-19", "2026-08-20" })
            plan.Rows.Add(new ShiftTaskRow
            {
                Date = d, Shift = "早", StartHour = 0, EndHour = 8,
                MachineId = "WK-02", Process = ProcessType.Load,
                FaceName = "判据面", UnitId = "U-9",
                MaterialCode = MaterialCatalog.Rock, VolumeM3 = 4321,
            });

        var all = ShiftPlanAssembler.ToTasks(plan, cfg: null);
        var day = ShiftPlanAssembler.ToTasks(plan, cfg: null, onlyDate: "2026-08-19");

        var mine = all.Where(t => t.Id.StartsWith("TK-2026-08-19-", StringComparison.Ordinal)).ToList();
        Assert.Single(day);
        Assert.Single(mine);
        Assert.Equal(mine[0].Id, day[0].Id);
        Assert.Equal(mine[0].TargetVolumeM3, day[0].TargetVolumeM3);
        Assert.Equal(mine[0].WorkZone, day[0].WorkZone);
        Assert.Equal(mine[0].UnitId, day[0].UnitId);
    }

    /// <summary>
    /// G5 <b>只跑校核的入口真的会报</b>（D3）。
    ///
    /// <para>造一盘必然违规的任务（同一台设备同一时段两条），
    /// <see cref="TaskExploder.CheckOnly"/> 必须报出「设备双占」。</para>
    ///
    /// <para><b>为什么要专门判这一条</b>：分解器那条路原来一条判据都不跑，
    /// 而界面照旧显示「✓ 无异常」。**空过的判据比没有判据更坏** ——
    /// 没有判据时人还知道自己没查过，空过时人拿到的是"已经查过了"这个结论。
    /// 所以这里不能只断言"CheckOnly 不抛"，必须断言它**真的报得出来**。</para>
    /// </summary>
    [Fact]
    public void G5_只校核入口对设备双占必须报出来()
    {
        var cfg = new ExploderConfig();
        var res = new ExploderResult();
        foreach (int i in new[] { 1, 2 })
            res.Tasks.Add(new ProductionTask
            {
                Id = $"T-{i}", Process = ProcessType.Load,
                Group = new EquipmentGroup { MainEquipment = "WK-07" },
                WorkZone = $"面{i}", Shift = "早", StartHour = 0, EndHour = 8,
                Status = TaskStatus.Planned,
            });

        Assert.Empty(res.Violations);
        TaskExploder.CheckOnly(cfg, res);

        foreach (var v in res.Violations.Take(8))
            _out.WriteLine($"   [{v.Severity}] {v.Code} {v.Message}");

        Assert.Contains(res.Violations,
            v => v.Code == ViolationCodes.EquipDoubleBooked || v.Code == "设备双占");
    }

    /// <summary>
    /// G6 <b>关掉闸必须真的红</b>（F0 同款自检）。
    ///
    /// <para>把钻机注入器摘掉之后 G1 那一盘依然拒绝分解 —— 这说明 G1 的绿
    /// **不是靠"台架里本来就没钻机"混过去的**。没有这一条，G1 在裸台架上恒绿，
    /// 而它要判的那条路一次都没走到。同 [[script-edit-silently-noop]]。</para>
    /// </summary>
    [Fact(Skip = "依赖原桌面真库的当时状态（采掘单元清单文件 / 2026-08 班次日历 / 作业面档案），Kylin 种子库不含；判据本身逐行保留")]
    public void G6_摘掉钻机注入器后G1的结论不变()
    {
        _out.WriteLine(_db.Label);
        if (!_db.Ready) { _out.WriteLine("SKIP: 真库不可用"); return; }

        var saved = ShiftPlanAssembler.DrillFleetHook;
        try
        {
            ShiftPlanAssembler.DrillFleetHook = null;
            var a = ShiftPlanAssembler.Build(Period, new ExploderConfig(), LedgerRoot);
            _out.WriteLine("无钻机 Headline：" + a.Headline);
            Assert.False(a.Ok);
            Assert.Contains("作业面", a.Headline);
            // 这一版**不该**提钻机（一台都没有，提了就是无中生有）
            Assert.DoesNotContain("钻机有", a.Headline);
        }
        finally { ShiftPlanAssembler.DrillFleetHook = saved; }
    }

    /// <summary>
    /// G7 <b>「没有作业面」必须进 Violations，不能只进 Notes</b>（D2 + D3）。
    ///
    /// <para>截图上那句「计划校核（引擎）：✓ 无异常」就是这么来的 ——
    /// 校核面板只读 <c>Violations</c>，而"没有作业面"当时只是一条 <c>Note</c>。
    /// 于是一盘 0 个采装面、只有穿孔的计划，被判成了「无异常」。</para>
    ///
    /// <para><b>两个分支都断言，不留空过口</b>：库里没有面时判"空盘 + 报错"，
    /// 有面时判"顶栏不再挂【没有作业面】"。写成 <c>if (有面) return;</c> 的话，
    /// 等哪天工序区入了库，这条判据就永远从第一行绿到最后一行 ——
    /// 而它要判的东西一次都没被碰过。同 [[absence-cannot-prove-absence]]。</para>
    /// </summary>
    [Fact]
    public void G7_没有作业面时必须报错而不是报无异常()
    {
        _out.WriteLine(_db.Label);
        if (!_db.Ready) { _out.WriteLine("SKIP: 真库不可用"); return; }

        ProductionPlanContext.Invalidate();
        var cfg = ProductionPlanContext.Config();
        var res = ProductionPlanContext.Result();
        bool none = ProductionPlanContext.NoFaces;

        _out.WriteLine($"NoFaces={none}　面 {cfg.Faces?.Count ?? 0} 个"
                     + $"　任务 {res.Tasks.Count} 条　校核 {res.Violations.Count} 条");
        foreach (var v in res.Violations.Take(5))
            _out.WriteLine($"   [{v.Severity}] {v.Code} {v.Message.Split('。')[0]}");

        if (none)
        {
            // ① 一条任务都不许排（此前分解器会绕过去，排出一整月的穿孔）
            Assert.Empty(res.Tasks);
            // ② 而且必须报出来 —— 面板只读 Violations
            Assert.Contains(res.Violations,
                v => v.Code == ViolationCodes.NoWorkFace && v.Severity == ViolationSeverity.Error);
            _out.WriteLine("→ 走的是【没有作业面】分支：空盘 + 报错（判据通过）");
        }
        else
        {
            // 面有了 ⇒ 顶栏那个记号必须撤掉，否则文案与事实反着说
            Assert.DoesNotContain("没有作业面", ProductionPlanContext.SourceLabel);
            Assert.DoesNotContain(res.Violations, v => v.Code == ViolationCodes.NoWorkFace);
            _out.WriteLine("→ 走的是【有面】分支：记号已撤（判据通过）");
        }
    }

    /// <summary>
    /// G8 <b>整链：工序区入库 → 起面 → 当日盘子只切出那一天</b>（D4 在真数据上的落点）。
    ///
    /// <para>ZS1 判的是"切不切得出来"，这一条判的是"切出来的是**一天**还是**一个月**" ——
    /// 后者在界面上分不出：<see cref="ProductionTask"/> 没有日期列，
    /// 26 个作业日的早班条会在甘特上重合成一条，看着就是"这台机早班有活"。</para>
    ///
    /// <para><b>钻机得自己注进去</b>：裸台架里 <c>DrillFleetHook</c> 是 null，
    /// 而穿爆超前（M3）是硬约束 —— 不注钻机的话需爆破的单元一个都排不出采装，
    /// 这条判据会在"0 条任务"上空过。</para>
    /// </summary>
    [Fact]
    public void G8_入库后当日盘子只含当日那一天()
    {
        _out.WriteLine(_db.Label);
        if (!_db.Ready) { _out.WriteLine("SKIP: 真库不可用"); return; }

        // ── ① 生成 + 入库（写副本；每组只入最大的一块，与界面默认勾选同一条）──
        var plan = ProcessZonePlanner.Plan(Period, ledgerRoot: LedgerRoot);
        if (!plan.Ok) { _out.WriteLine("SKIP: 生成不出候选 —— " + plan.Header); return; }
        var chosen = plan.Proposals
            .GroupBy(p => (p.Process, p.GroupKey))
            .Select(g => g.OrderByDescending(x => x.AreaM2).First())
            .ToList();
        string msg = ProcessZoneStore.Apply(Period, chosen, out int ins, out int upd, out _);
        _out.WriteLine($"① 入库：{msg}");
        Assert.True(ins + upd > 0);

        // ── ② 起面 ──
        ProductionPlanContext.Invalidate();
        var cfg = ProductionPlanContext.Config();
        int loadFaces = (cfg.Faces ?? new List<FaceInput>()).Count(f => f?.Process == ProcessType.Load);
        _out.WriteLine($"② 起面：{cfg.Faces?.Count ?? 0} 个（采装 {loadFaces} 个）　来源 {ProductionPlanContext.FaceOrigin}");
        Assert.True(loadFaces > 0, "入库之后仍然一个采装面都没有");

        // ── ③ 当日切片 ──
        var saved = ShiftPlanAssembler.DrillFleetHook;
        try
        {
            ShiftPlanAssembler.DrillFleetHook = (_, _, _) => FakeDrills(20);
            var day = new DateTime(2026, 8, 19);
            var a = ShiftPlanAssembler.Build(Period, cfg, LedgerRoot, onlyDate: day);
            _out.WriteLine($"③ {a.Headline}");
            if (!a.Ok) { _out.WriteLine("   ◆ 分解不成立：" + a.Headline); Assert.True(a.Ok, a.Headline); }

            string tag = "TK-2026-08-19-";
            _out.WriteLine($"   整月 {a.MonthTasks.Count} 条 · 当日 {a.Tasks.Count} 条");
            foreach (var g in a.Tasks.GroupBy(t => t.Process))
                _out.WriteLine($"     {g.Key} {g.Count()} 条");

            // ① 当日那份**一条都不许**是别的天的
            Assert.All(a.Tasks, t => Assert.StartsWith(tag, t.Id, StringComparison.Ordinal));

            // ② 而整月那份里**确实**有别的天 —— 否则这条判据是在一份本来就只有一天的
            //    数据上空过（那样它永远绿，且什么都没判）
            Assert.Contains(a.MonthTasks, t => !t.Id.StartsWith(tag, StringComparison.Ordinal));
            Assert.True(a.MonthTasks.Count > a.Tasks.Count,
                $"整月 {a.MonthTasks.Count} 条 ≤ 当日 {a.Tasks.Count} 条 —— 切片没起作用，或数据只有一天");

            // ③ 当日必须真有活（0 条会让 ① 恒真）
            Assert.NotEmpty(a.Tasks);
        }
        finally { ShiftPlanAssembler.DrillFleetHook = saved; }
    }
}
