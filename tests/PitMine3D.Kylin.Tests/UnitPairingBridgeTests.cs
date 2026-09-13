// 忠实移植自原 PitMine3D Tests/Tests.PitMineApp/UnitPairingBridgeTests.cs（逐行对应；仅命名空间/路径适配 —— P1 源码判据改读 Kylin 的 Views/Plan/DumpPairingWindow.cs）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Cad.Units;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// <b>P 组 · 采排配对的职责边界</b>（`docs/短期生产计划_按钮职责切分.md` §〇 主干决定）。
///
/// <para>配对只在<b>单元链</b>一处解，采排配对窗口是它的<b>视图</b>。
/// 这一组钉两件事：① 那个窗口不许再有自己的分配算法；② 视图与算法之间那座桥不许丢量、不许静默。</para>
///
/// <para><b>为什么 ① 必须是源码级判据</b>：旧文档写着「不能有写回按钮」并附了一句
/// "已查证：全文无 <c>Insert</c>/<c>Upsert</c>" —— 而实际的第二/第三套实现写的是<b>内存对象</b>，
/// 那句查证<b>从来没可能红过</b>。所以判据要判<b>这件事本身</b>（"窗口里不许出现那个分配器"），
/// 不判它常见的某一种实现形式。</para>
/// </summary>
public class UnitPairingBridgeTests
{
    private readonly ITestOutputHelper _out;
    public UnitPairingBridgeTests(ITestOutputHelper o) => _out = o;

    /// <summary>仓库根：按<b>本文件路径</b>往上找，不按运行目录 —— 判据常用 /p:OutputPath 指到临时目录。</summary>
    private static string RepoRoot([CallerFilePath] string self = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", ".."));

    // ════════════════════════════════════════════════════════════════════
    //  ① 窗口里不许有第二套分配算法
    // ════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "P1 采排配对窗口不许引用分配器（配对只有单元链一处产出）")]
    public void P1_PairingWindowHasNoAllocator()
    {
        string path = Path.Combine(RepoRoot(), "src", "Views", "Plan", "DumpPairingWindow.cs");
        Assert.True(File.Exists(path), $"判据读的文件不在：{path}（窗口改名/挪位置了？那这条判据本身要跟着改）");
        string src = File.ReadAllText(path);

        // 注释里提它是可以的（说明"为什么删掉"），但**代码里不许调用**。
        var calls = src.Split('\n')
                       .Select((line, i) => (line: line.Trim(), no: i + 1))
                       .Where(x => x.line.Contains("PlanFlowAllocator", StringComparison.Ordinal)
                                && !x.line.StartsWith("//", StringComparison.Ordinal)
                                && !x.line.StartsWith("///", StringComparison.Ordinal)
                                && !x.line.StartsWith("*", StringComparison.Ordinal))
                       .ToList();
        Assert.True(calls.Count == 0,
            "采排配对窗口又出现了自己的分配算法（那是全仓第三套配对实现）：\n　"
            + string.Join("\n　", calls.Select(c => $"第{c.no}行：{c.line}")));

        // 反向自检：这条判据只有在【真的读到了那个文件】时才有意义 —— 空文件/读错文件也会"绿"
        Assert.Contains("UnitPlanStore", src);   // 现在的唯一流来源
        _out.WriteLine($"已核 {path}（{src.Length / 1024.0:0.0} KB）：无分配器调用，且确实读到了新的流来源。");
    }

    // ════════════════════════════════════════════════════════════════════
    //  ② 桥：不丢量、不静默
    // ════════════════════════════════════════════════════════════════════

    private static UnitPlanResult MakeResult(string destName, double rockM3, double coalM3, double unplacedM3 = 0)
    {
        var r = new UnitPlanResult { Success = true };
        var rock = new UnitAssignment
        {
            UnitId = "岩1010-B2-P3", Kind = UnitKind.Rock, SeamCode = "岩1010", BandId = 2, PanelIndex = 3,
            Seq = 1, InSituM3 = rockM3 + unplacedM3, Fraction = 1, DoneAfter = 1, TonnageT = (rockM3 + unplacedM3) * 2.5,
            UnplacedM3 = unplacedM3,
        };
        rock.Flows.Add(new UnitFlow
        {
            DestinationCode = destName + "-L0-100", DestinationName = destName,
            InSituM3 = rockM3, DumpM3 = rockM3 * 1.15, TonnageT = rockM3 * 2.5, HaulKm = 2.0,
            MaterialCode = "rock", Density = 2.5, Kr = 1.15,
        });
        r.Assignments.Add(rock);

        var coal = new UnitAssignment
        {
            UnitId = "煤2-B1-P1", Kind = UnitKind.Coal, SeamCode = "煤2", BandId = 1, PanelIndex = 1,
            Seq = 2, InSituM3 = coalM3, Fraction = 1, DoneAfter = 1, TonnageT = coalM3 * 1.35,
        };
        coal.Flows.Add(new UnitFlow
        {
            DestinationCode = "CRUSH1", DestinationName = "破碎站1", IsCoalSink = true,
            InSituM3 = coalM3, TonnageT = coalM3 * 1.35, HaulKm = 3.5, MaterialCode = "coal", Density = 1.35,
        });
        r.Assignments.Add(coal);

        r.CoalT = coal.TonnageT;
        r.StripM3 = rock.InSituM3;
        r.UnplacedM3 = unplacedM3;
        return r;
    }

    private static List<PlanDestination> Catalog(params (string Name, PlanSinkKind Kind)[] items)
        => items.Select((x, i) => new PlanDestination
        { Id = $"D{i + 1}", Name = x.Name, Kind = x.Kind, FallbackHaulKm = 9.9 }).ToList();

    [Fact(DisplayName = "P2 归并前后实方守恒，且运距用引擎算的不是台账兜底的")]
    public void P2_BridgeConservesVolume()
    {
        var r = MakeResult("内排土场1", rockM3: 60e4, coalM3: 20e4);
        var cat = Catalog(("内排土场1", PlanSinkKind.InternalDump), ("破碎站1", PlanSinkKind.Crusher));

        var res = UnitFlowBridge.ToPlanFlows(r, cat);
        _out.WriteLine(res.Caption);
        foreach (var n in res.Notes) _out.WriteLine("　" + n);

        Assert.Equal(2, res.Mapped);
        Assert.Equal(2, res.Total);
        Assert.Equal(res.SourceInSituM3, res.FlowInSituM3, 3);
        Assert.Equal(80e4 / 1e4, res.Flows.Sum(f => f.InSituWanM3), 4);

        // 运距必须是引擎那份（2.0 / 3.5），不能被台账的 FallbackHaulKm=9.9 顶掉 ——
        // 顶掉的话"接了路网/按真质心算"的结果会被悄悄换成一个静态数，而每一格看上去都正常
        Assert.Equal(2.0, res.Flows.Single(f => f.MaterialCode == "rock").HaulKm, 3);
        Assert.Equal(3.5, res.Flows.Single(f => f.MaterialCode == "coal").HaulKm, 3);

        // 作业面粒度 = 层-B带（幅号归并掉）
        Assert.Contains(res.Flows, f => f.SourceName == "岩1010-B2");
        Assert.Contains(res.Flows, f => f.SourceName == "煤2-B1");
    }

    [Fact(DisplayName = "P3 去向映射不上要落「未分配」并点名，不许静默丢量")]
    public void P3_UnmappedDestinationIsReported()
    {
        var r = MakeResult("内排土场1", rockM3: 60e4, coalM3: 20e4);
        var cat = Catalog(("外排土场东", PlanSinkKind.ExternalDump));   // 故意没有「内排土场1」与「破碎站1」

        var res = UnitFlowBridge.ToPlanFlows(r, cat);
        _out.WriteLine(res.Caption);
        foreach (var n in res.Notes) _out.WriteLine("　" + n);

        Assert.Equal(0, res.Mapped);
        Assert.Equal(2, res.Total);
        Assert.Equal(res.SourceInSituM3, res.FlowInSituM3, 3);              // ★ 一方都不许丢
        Assert.All(res.Flows, f => Assert.False(f.HasDestination));         // 全落「未分配」
        Assert.Contains(res.Notes, n => n.Contains("找不到同名项"));
        Assert.Contains(res.Notes, n => n.Contains("内排土场1"));           // 点名，不是只报个数
    }

    [Fact(DisplayName = "P4 排不下的量要出现在矩阵里，否则「排不下」在这张表上等于不存在")]
    public void P4_UnplacedShowsUp()
    {
        var r = MakeResult("内排土场1", rockM3: 40e4, coalM3: 20e4, unplacedM3: 20e4);
        var cat = Catalog(("内排土场1", PlanSinkKind.InternalDump), ("破碎站1", PlanSinkKind.Crusher));

        var res = UnitFlowBridge.ToPlanFlows(r, cat);
        Assert.Equal(res.SourceInSituM3, res.FlowInSituM3, 3);
        double noDest = res.Flows.Where(f => !f.HasDestination).Sum(f => f.InSituWanM3);
        Assert.Equal(20e4 / 1e4, noDest, 4);
        _out.WriteLine($"排不下 {noDest:0.0} 万m³ 落在「未分配」列 —— 矩阵上看得见");
    }

    [Fact(DisplayName = "P5 没有上游结果时不自建流，并说清缺什么")]
    public void P5_NoUpstreamNoFabrication()
    {
        var res = UnitFlowBridge.ToPlanFlows(null, Catalog(("内排土场1", PlanSinkKind.InternalDump)));
        Assert.Empty(res.Flows);
        Assert.Contains(res.Notes, n => n.Contains("先在「采掘单元清单」按目标排一次"));

        // 失败的结果同样不许变成流（Put 那侧也挡，这里是第二道）
        var bad = new UnitPlanResult { Success = false, Error = "没有采掘单元" };
        Assert.Empty(UnitFlowBridge.ToPlanFlows(bad, null).Flows);
    }

    [Fact(DisplayName = "P6 交接件：失败不覆盖好结果，且「上一次没成」要说出来")]
    public void P6_StoreKeepsGoodResultAndReportsFailure()
    {
        UnitPlanStore.Clear();
        Assert.Null(UnitPlanStore.Last);
        Assert.Contains("还没排过产", UnitPlanStore.Caption);

        var good = MakeResult("内排土场1", 60e4, 20e4);
        UnitPlanStore.Put(good, "2026-08", "目标 煤 20万t");
        Assert.Same(good, UnitPlanStore.Last);
        Assert.Equal("2026-08", UnitPlanStore.Period);
        Assert.DoesNotContain("没成", UnitPlanStore.Caption);

        UnitPlanStore.Put(null, "2026-09", "自洽校核没过");
        Assert.Same(good, UnitPlanStore.Last);                      // ★ 好结果没被空的盖掉
        Assert.True(UnitPlanStore.LastAttemptFailed);
        Assert.Contains("更早", UnitPlanStore.Caption);              // ★ 但必须说"你看到的是更早那一次"
        _out.WriteLine(UnitPlanStore.Caption);

        UnitPlanStore.Clear();
    }
}
