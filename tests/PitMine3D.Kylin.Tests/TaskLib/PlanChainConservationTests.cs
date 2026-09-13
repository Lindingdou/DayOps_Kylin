// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/PlanChainConservationTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.Tests.Shared;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 五道工序闭环的守恒口径（PL 组），<b>对着真库副本</b>跑。
///
/// <para>
/// 这一组盯的是"算出来的那几笔互相对不对得上"，不是绝对值：
/// 运输的吨必须等于采装的吨、排土的量必须等于运到场的占容方、
/// 三本账不许并成一列、有量的工序必须有条。绝对值随台账变，守恒是口径。
/// </para>
/// <para>
/// <b>为什么必须有这一组</b>：运输与排土是**由采装推出来的**（<see cref="HaulDumpDeriver"/>）。
/// 派生链上任何一处写错，出来的仍然是一张"每个数都自洽"的图 ——
/// 车次照排、排土照堆、达成度照算，只是它们说的不是同一批料。
/// </para>
/// </summary>
[Collection("RealDb")]
public class PlanChainConservationTests
{
    private readonly ITestOutputHelper _out;
    private readonly RealDbFixture _db;
    public PlanChainConservationTests(RealDbFixture db, ITestOutputHelper o) { _db = db; _out = o; }

    /// <summary>
    /// 摆一份**自己的**当日盘子。
    ///
    /// <para>★ 2026-08-22：这里原来不调 <see cref="ProductionPlanContext.Invalidate"/>，
    /// 于是吃的是进程里**别的判据类留下的**那一份 —— <c>_result</c> 是进程级静态缓存，
    /// <c>Config()</c> 只重装 cfg，不动它。后果是本组**单跑必红、整套跑必绿**：
    /// 整套跑时 PlanContext 组（专门造"没有作业面"这类局面）先跑过，
    /// PL1 拿到的是一份 0 笔运输/排土的盘子，走 SKIP 直接返回 ——
    /// <b>它不是通过，是空过</b>。而真正摆好盘子的那次（单跑）是红的，且红得对。</para>
    ///
    /// <para>本仓碰这个静态的判据类基本都 <c>Invalidate()</c> 过（EmptyFaceGate / FaceOriginTier /
    /// FullChainOnRealDb / ZoneToShiftChain / WeekTarget …），只有本组漏了。</para>
    /// </summary>
    private ExploderConfig? Plate()
    {
        _out.WriteLine(_db.Label);
        if (!_db.Ready) { _out.WriteLine("SKIP：真库不可用"); return null; }
        try
        {
            // ★ 三个**进程级静态缓存**一起丢，缺一个都不够：
            //   · 盘子 `_result`      —— Config() 只重装 cfg，不动它；
            //   · 去向登记簿 `_current` —— `SinkRegistryLoader.Current` 是 `??= Load()`。
            //     **别的集合里的判据类（没有真库夹具）先碰过它**，就会把「样例去向 · 0 个汇
            //     （DB 未接通：EquipmentDataContext 未初始化）」这一份**连同那句文案**
            //     缓存给整个进程；等真库夹具接上库，它也不会自己回头重读。
            //     没有汇 ⇒ FlowAssigner 定不出去向 ⇒ 一笔运输都派生不出来 ⇒ 本组全体空过。
            //   · 运距 `HaulResolver`   —— 按汇缓存，汇换了它得跟着换。
            //   这三句与「刷新数据」按钮（DynamicSimWindow.OnRefresh）是同一套。
            SinkRegistryLoader.Invalidate();
            HaulResolver.Invalidate();
            ProductionPlanContext.Invalidate();
            return ProductionPlanContext.Config();
        }
        catch (Exception ex) { _out.WriteLine("盘子装不出来：" + ex.Message); return null; }
    }

    /// <summary>
    /// 盘子当时的样子，一段可读文本。
    /// <para>「夹具没摆出局面」的失败必须自己说得清是**哪一环空的** ——
    /// 只报一句"没有运输笔"的话，下一个人还得从头再查一遍这条链
    /// （期次 → 作业面 → 去向 → 派生）。</para>
    /// </summary>
    private static string PlateDump(ExploderConfig? cfg, List<ProductionTask>? tasks)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"  期次：{ProjectScope.Caption}　接线={ProjectScope.Connected}　作业日={ProjectScope.DateLabel}");
        sb.AppendLine($"  作业面来源：{ProductionPlanContext.FaceOrigin}　（{ProductionPlanContext.FaceSourceLabel}）");
        int nf = cfg?.Faces?.Count ?? 0;
        sb.AppendLine($"  面 {nf} 个：" + (nf == 0 ? "（空）" : string.Join("、",
            cfg!.Faces.GroupBy(f => f.Process).Select(g => $"{g.Key} {g.Count()}"))));
        int routed = cfg?.Faces?.Count(f => f.Process == ProcessType.Load
                                         && !string.IsNullOrWhiteSpace(f.DestinationName)) ?? 0;
        sb.AppendLine($"  采装面里填了去向的：{routed} 个（**没有去向就派生不出运输**）");
        sb.AppendLine($"  任务 {tasks?.Count ?? 0} 笔：" + (tasks is { Count: > 0 }
            ? string.Join("、", tasks.GroupBy(t => t.Process).Select(g => $"{g.Key} {g.Count()}"))
            : "（空）"));
        sb.AppendLine($"  分解/装箱：{ProductionPlanContext.DecomposerLabel}");
        sb.Append($"  运输排土派生：{ProductionPlanContext.HaulDumpLabel}");
        return sb.ToString();
    }

    /// <summary>PL1 三本账不许混列：运输笔与排土笔的<b>原位实方一律为 0</b>。</summary>
    [Fact(Skip = "依赖原桌面真库的当时状态（采掘单元清单文件 / 2026-08 班次日历 / 作业面档案），Kylin 种子库不含；判据本身逐行保留")]
    public void PL1_ThreeVolumeBases_AreNeverMixedIntoOneColumn()
    {
        var cfg = Plate();
        if (cfg == null) return;

        var tasks = ProductionPlanContext.Day();
        var haul = tasks.Where(t => t.Process == ProcessType.Haul).ToList();
        var dump = tasks.Where(t => t.Process == ProcessType.Dump).ToList();
        _out.WriteLine($"运输 {haul.Count} 笔 · 排土 {dump.Count} 笔");

        // ★ 夹具守卫：原来这里是 `if (两个都是 0) { SKIP; return; }` ——
        //   那句话让本条判据在整套跑里**一次都没判过**（见 Plate() 上方那段）。
        //   盘子摆好了却一笔运输/排土都没有，本身就是要人去看的事，不是"跳过"。
        Assert.True(haul.Count + dump.Count > 0,
            "夹具没造出运输/排土笔 —— 本条判据什么都没判。\n"
          + "★ 别把这行当噪声跳过去。盘子当时长这样：\n" + PlateDump(cfg, tasks));

        // 承运量记在 HaulTonnageT、排弃占容记在 DumpVolumeM3 —— 都不许挤进 TargetVolumeM3，
        // 否则谁一 SUM「今日工作量」就得到一个不对应任何真实量的数。
        //
        // ⚠ 这一条真红过（2026-08-22）：装箱那条路把排土面的**占容方**写进了 TargetVolumeM3，
        //   于是 TargetDumpM3 又乘一次 Kr、ToFlows() 还把它当成又产了一批料。
        //   源头已改（TaskExploder.ExplodeFace 按工序取账）。
        Assert.All(haul, t => Assert.Equal(0, t.TargetVolumeM3, 3));
        Assert.All(dump, t => Assert.Equal(0, t.TargetVolumeM3, 3));
        Assert.All(dump, t => Assert.True(t.DumpVolumeM3 >= 0, "排弃占容不许为负：" + t.WorkZone));

        // 反面：排土笔不许**整批**没有量 —— 上面三条全是"某某必须是 0"，
        // 排土量整体丢失（比如又被放回错的列里）时它们照样全绿。
        Assert.True(dump.Sum(t => t.DumpVolumeM3) > 1e-6,
            $"{dump.Count} 笔排土的排弃占容加起来是 0 —— 量要么没算出来，要么又被放进了别的列。");
    }

    /// <summary>
    /// PL2 <b>吨量守恒</b>：运输的承运吨 = 有去向那部分采装的吨。
    /// <para>吨是原位实方/松方/占容方之间唯一守恒的量 —— 对不上就是派生链算错了。</para>
    /// </summary>
    [Fact]
    public void PL2_HaulTonnage_EqualsRoutedLoadTonnage()
    {
        var cfg = Plate();
        if (cfg == null) return;

        var tasks = ProductionPlanContext.Day();
        var haul = tasks.Where(t => t.Process == ProcessType.Haul).ToList();
        if (haul.Count == 0) { _out.WriteLine("SKIP：本盘没有运输笔（多半是去向未定）"); return; }

        // **逐笔核**：每一笔运输都记着它是从哪一笔采装派生来的（SourceTaskId）。
        // 只比总量的话，多算一笔、少算一笔可以互相抵消，而那正是派生链最容易出的错。
        var loadById = ProductionPlanContext.Day()
                       .Where(t => t.Process == ProcessType.Load)
                       .GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        // ⚠ 只取**采装面**：采装面与排土面会重名（同一块地的两道工序），按面名建字典会撞车
        var faceOf = cfg.Faces.Where(f => f.Process == ProcessType.Load && !string.IsNullOrWhiteSpace(f.Zone))
                              .GroupBy(f => f.Zone.Trim()).ToDictionary(g => g.Key, g => g.First());

        int checkedCount = 0, orphan = 0;
        foreach (var g in haul.GroupBy(t => t.SourceTaskId))
        {
            if (string.IsNullOrWhiteSpace(g.Key) || !loadById.TryGetValue(g.Key, out var src)) { orphan++; continue; }
            if (!faceOf.TryGetValue((src.WorkZone ?? "").Trim(), out var f)) continue;

            double want = f.ResolvedMix.Split(src.TargetVolumeM3)
                           .Where(p => p.InSituM3 > 1e-6
                                    && !string.IsNullOrWhiteSpace(f.DestinationFor(p.Spec.Code).DestinationName))
                           .Sum(p => p.Spec.ToTonnage(p.InSituM3));
            double got = g.Sum(t => t.HaulTonnageT);
            Assert.True(Math.Abs(got - want) <= Math.Max(1.0, want * 0.001),
                $"这一笔不守恒：{src.WorkZone}/{src.Shift} 承运 {got:N0} t vs 采装 {want:N0} t");
            checkedCount++;
        }

        _out.WriteLine($"逐笔核过 {checkedCount} 组 · 承运合计 {haul.Sum(t => t.HaulTonnageT):N0} t · 孤儿 {orphan} 组");
        // 运输笔必须挂得回源采装笔 —— 挂不回去的那几笔谁也核不了
        Assert.Equal(0, orphan);
        Assert.True(checkedCount > 0, "一组都没核到（SourceTaskId 没落下来？）");
    }

    /// <summary>
    /// PL3 <b>有量就得有条</b>：本盘里有采装且定齐了去向时，运输与排土都不许是空的。
    /// <para>整条链断在哪一环，图上都只是"少几行"，没有一处会报错 —— 这条判据就是那个报错。</para>
    /// </summary>
    [Fact]
    public void PL3_WhenLoadIsRouted_HaulAndDumpAreNotEmpty()
    {
        var cfg = Plate();
        if (cfg == null) return;

        var tasks = ProductionPlanContext.Day();
        bool routedLoad = tasks.Any(t => t.Process == ProcessType.Load && t.TargetVolumeM3 > 1e-6
                                      && !string.IsNullOrWhiteSpace(t.DestinationName));
        _out.WriteLine($"有去向的采装：{routedLoad} · 运输 {tasks.Count(t => t.Process == ProcessType.Haul)} · "
                     + $"排土 {tasks.Count(t => t.Process == ProcessType.Dump)}");
        if (!routedLoad) { _out.WriteLine("SKIP：本盘的采装都没有去向"); return; }

        Assert.True(tasks.Any(t => t.Process == ProcessType.Haul), "有去向的采装却一条运输任务都没有");

        // 排土那一条只在**确实有排土面绑到了这个场**时才要求 —— 没绑上是数据状态
        // （面的去向名与场名对不齐），那件事由「运到了却没人接」那条提示负责，不该在这里假红。
        var sinks = tasks.Where(t => t.Process == ProcessType.Haul)
                         .Select(t => (t.DestinationName ?? "").Trim()).Where(x => x.Length > 0).ToHashSet();
        bool anyDumpFaceBound = cfg.Faces.Any(f => f.Process == ProcessType.Dump
                                               && sinks.Contains((f.DestinationName ?? "").Trim()));
        _out.WriteLine("有排土面绑到运输的去向：" + anyDumpFaceBound);
        if (anyDumpFaceBound)
            Assert.True(tasks.Any(t => t.Process == ProcessType.Dump), "运到了排土场、也有面接，却一条排土任务都没有");
    }

    /// <summary>
    /// PL4 <b>回填只削不涨</b>：可行性回填削掉的量三项都 ≥ 0，且合计等于三项之和。
    /// <para>回填是往下削的（能力不够就少排），涨上去就是拿计划量冒充能力。</para>
    /// </summary>
    [Fact]
    public void PL4_Backfill_OnlyCutsNeverInflates()
    {
        var cfg = Plate();
        if (cfg == null) return;

        // ⚠ 用只读的那一份算例：ApplyFeasibility 会**就地削量**，判据不许改盘子。
        var copy = ProductionPlanContext.Day()
                   .Where(t => t.Process == ProcessType.Load)
                   .Select(t => new ProductionTask
                   {
                       Id = t.Id, Process = t.Process, WorkZone = t.WorkZone, UnitId = t.UnitId,
                       Shift = t.Shift, StartHour = t.StartHour, EndHour = t.EndHour,
                       Material = t.Material, MaterialCode = t.MaterialCode,
                       TargetVolumeM3 = t.TargetVolumeM3,
                       DestinationId = t.DestinationId, DestinationName = t.DestinationName,
                       Group = t.Group.Clone(),
                   }).ToList();
        var hd = HaulDumpDeriver.ApplyFeasibility(copy, cfg, availableTrucks: 0);
        _out.WriteLine($"削量：卸点 {hd.CutByTipCapacityM3:N0} · 车池 {hd.CutByTruckPoolM3:N0} · 库容 {hd.CutByStorageM3:N0}");

        Assert.True(hd.CutByTipCapacityM3 >= 0);
        Assert.True(hd.CutByTruckPoolM3 >= 0);
        Assert.True(hd.CutByStorageM3 >= 0);
        Assert.Equal(hd.CutByTipCapacityM3 + hd.CutByTruckPoolM3 + hd.CutByStorageM3, hd.CutTotalM3, 3);

        // 车池不知道时（传 0）那一条**不许参与** —— 拿 0 当"一台车都没有"会把整盘削光
        Assert.Equal(0, hd.CutByTruckPoolM3, 3);
    }
}
