// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/HaulRouteStageCheck.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  HaulRouteStage 的离线判据（不需要三维内核、不需要工程库）。
//
//  写法上的两条纪律（照 SimOverlayComposerCheck 同一套）：
//   1) **每条判据都能证伪**。凡是只判「成功」的判据都会空过，所以：
//      · 会用到的每一个未命中桶，都设计一个必然触发它的算例并断言它 >0
//        （一个从来不触发的计数器，等于没有这条判据）；
//      · 「降级不画」这类判据都带**反例对照组** —— 同一笔只把可信度改成 Real，
//        必须画得出来。对照组也画不出来，说明判据抓的不是可信度而是别的东西。
//   2) **不吞降级**。跳过/舍弃/共用色都要在 Notes 里出现，判据 J5/J6 查的就是这个。
//
//  ★ 副作用（跑之前要知道）：本判据会往进程级单例 SimRoadGraph 上**外挂一张台架图**
//    （AttachForTest，Label 会带「【外挂图】」自报家门），跑完在 finally 里 Invalidate 还原，
//    生产图会在下一次问路时重新读库。overlay 通道**不受影响** —— 判据用的是自己 new 的
//    SimOverlayComposer + RecordingOverlaySink，绝不去动全局那个。
//
//  跑法：<c>HaulRouteStageCheck.Run()</c> 返回一行行的报告；<c>RunAll().AllPassed</c> 是布尔结论。
// ─────────────────────────────────────────────────────────────────────────────
public static class HaulRouteStageCheck
{
    // ── 合成路网 ────────────────────────────────────────────────────────────
    //  主干 L1—J2—J3—D4；ISO 是孤立节点（永远不可达）。
    //  整体挪离原点：(0,0) 在口径上表示「没有坐标」，节点不能压在那上面。
    private const string L1 = "L1", J2 = "J2", J3 = "J3", D4 = "D4", ISO = "ISO", K5 = "K5";

    /// <summary>双向图：回程与去程完全同路（空驶差异段应为 0）。</summary>
    private static RoadGraph GraphBidirectional()
    {
        var g = NodesOnly();
        g.AddEdge(new RoadEdge("E12", L1, J2, new[]
        {
            new Point3d(1000, 1000, 100), new Point3d(1100, 1040, 100), new Point3d(1200, 1000, 100),
        }));
        g.AddEdge(new RoadEdge("E23", J2, J3, new[]
        {
            new Point3d(1200, 1000, 100), new Point3d(1400, 1000, 100),
        }));
        g.AddEdge(new RoadEdge("E34", J3, D4));          // 无中线 → 走两端节点兜底
        return g;
    }

    /// <summary>单向去程 + 另一条单向回程：空驶必然有 2 段是重载不经过的。</summary>
    private static RoadGraph GraphOneWayReturn()
    {
        var g = NodesOnly();
        g.AddNode(K5, RoadNodeType.Junction, new Point3d(1100, 900, 100));
        g.AddEdge(new RoadEdge("E12", L1, J2, new[]
        {
            new Point3d(1000, 1000, 100), new Point3d(1100, 1040, 100), new Point3d(1200, 1000, 100),
        })
        { OneWay = true });
        g.AddEdge(new RoadEdge("E23", J2, J3, new[]
        {
            new Point3d(1200, 1000, 100), new Point3d(1400, 1000, 100),
        }));
        g.AddEdge(new RoadEdge("E34", J3, D4));
        g.AddEdge(new RoadEdge("EK1", J2, K5, new[]
        {
            new Point3d(1200, 1000, 100), new Point3d(1100, 900, 100),
        })
        { OneWay = true });
        g.AddEdge(new RoadEdge("EK2", K5, L1, new[]
        {
            new Point3d(1100, 900, 100), new Point3d(1000, 1000, 100),
        })
        { OneWay = true });
        return g;
    }

    private static RoadGraph NodesOnly()
    {
        var g = new RoadGraph();
        g.AddNode(L1, RoadNodeType.Loading, new Point3d(1000, 1000, 100));
        g.AddNode(J2, RoadNodeType.Junction, new Point3d(1200, 1000, 100));
        g.AddNode(J3, RoadNodeType.Junction, new Point3d(1400, 1000, 100));
        g.AddNode(D4, RoadNodeType.Unloading, new Point3d(1400, 1300, 120));
        g.AddNode(ISO, RoadNodeType.Junction, new Point3d(5000, 5000, 100));   // 无边，永远不可达
        return g;
    }

    /// <summary>L1 → D4 这条路在合成图上应有的段数（判据里到处要用，只写一处）。</summary>
    private const int SegsL1ToD4 = 4;

    // ── O-D 造型 ────────────────────────────────────────────────────────────

    private static SimHaulOd Od(string unit, double sx, double sy, double dx, double dy,
                                SimHaulTrust trust = SimHaulTrust.Real,
                                bool coal = false, double tons = 1000,
                                string destCode = "DUMP-A", string material = "rock")
        => new()
        {
            UnitId = unit,
            IsCoal = coal,
            Sx = sx, Sy = sy, Sz = 100,
            DestinationCode = destCode, DestinationName = destCode,
            Dx = dx, Dy = dy, Dz = 120,
            DestTrust = trust,
            TonnageT = tons, InSituM3 = tons / 2.4,
            MaterialCode = material, MaterialName = material,
        };

    private static SimHaulOd Good(string unit, double tons = 1000, bool coal = false, string material = "rock")
        => Od(unit, 1000, 1000, 1400, 1300, SimHaulTrust.Real, coal, tons, "DUMP-A", material);

    private static (HaulRouteStage Stage, SimOverlayComposer Composer, RecordingOverlaySink Sink) NewStage()
    {
        var sink = new RecordingOverlaySink();
        var comp = new SimOverlayComposer(sink);
        return (new HaulRouteStage(comp), comp, sink);
    }

    // ── 跑 ──────────────────────────────────────────────────────────────────

    /// <summary>跑全部判据，返回报告文本。</summary>
    public static string Run()
    {
        var rep = RunAll();
        var sb = new StringBuilder();
        sb.AppendLine($"HaulRouteStage 判据：{rep.Items.Count(i => i.Passed)}/{rep.Items.Count} 通过");
        foreach (var i in rep.Items) sb.AppendLine("  " + i);
        return sb.ToString();
    }

    /// <summary>跑全部判据。</summary>
    public static SimCheckReport RunAll()
    {
        var rep = new SimCheckReport();
        try
        {
            rep.Items.Add(J1_条数守恒且每个未命中桶都真的会触发());
            rep.Items.Add(J2_解不出逐笔退回且不画假线());
            rep.Items.Add(J3_命中率说得出且三种情形文案互不相同());
            rep.Items.Add(J4_降级去向不画_带反例对照());
            rep.Items.Add(J5_未声明可信度按不可信处理());
            rep.Items.Add(J6_段级去重且吨量累加());
            rep.Items.Add(J7_不擦别人的键_带反例对照());
            rep.Items.Add(J8_空驶只画重载不经过的段_带反例对照());
            rep.Items.Add(J9_超上限不静默());
            rep.Items.Add(J10_煤色不会被载荷分档压成不可见());
        }
        catch (Exception ex)
        {
            rep.Items.Add(new SimCheckItem("判据自身异常", false, $"{ex.GetType().Name}: {ex.Message}"));
        }
        finally
        {
            try { SimRoadGraph.Invalidate(); } catch { }
        }
        return rep;
    }

    // ── J1 分类账 ───────────────────────────────────────────────────────────

    /// <summary>
    /// ① 每笔 O-D 落进且只落进一个桶（Balanced）；
    /// ② 问路笔数 = 命中 + 未命中；
    /// ③ ★ 会用到的每个未命中桶**真的会被触发** —— 一个从来不触发的计数器等于没有这条判据。
    /// </summary>
    private static SimCheckItem J1_条数守恒且每个未命中桶都真的会触发()
    {
        SimRoadGraph.AttachForTest(GraphBidirectional());
        var (stage, _, _) = NewStage();

        var ods = new List<SimHaulOd>
        {
            Good("U-hit"),                                                             // 命中
            Od("U-unreach", 1000, 1000, 5000, 5010),                                   // 汇吸到孤立点 → 不连通
            Od("U-srcfar", 9000, 9000, 1400, 1300),                                    // 源 200m 内无节点 → 源未吸附
            Od("U-dstfar", 1000, 1000, 9000, 9000),                                    // 汇 200m 内无节点 → 汇未吸附
            Od("U-same", 1000, 1000, 1010, 1000),                                      // 源汇同吸一个节点
            Od("U-approx", 1000, 1000, 1400, 1300, SimHaulTrust.Approximate),          // 降级去向 → 不问路
            Od("U-unknown", 1000, 1000, 1400, 1300, SimHaulTrust.Unknown),             // 未声明 → 不问路
            Od("U-nopos", 0, 0, 1400, 1300),                                           // 源无坐标 → 不问路
            null!,                                                                     // 空条目
        };

        var r = stage.Apply("2027-01", ods);

        var bad = new List<string>();
        if (r.OdTotal != ods.Count) bad.Add($"OdTotal={r.OdTotal} 应为 {ods.Count}");
        if (!r.Balanced) bad.Add("分类账不自洽（有分支漏计）");
        if (r.OdRouted != 5) bad.Add($"问路笔数={r.OdRouted} 应为 5（9 笔里有 4 笔压根不该问路）");
        if (r.OdHit != 1) bad.Add($"命中={r.OdHit} 应为 1");

        // ③ 每个设计要触发的桶都必须 >0
        foreach (var m in new[] { SimRouteMiss.Unreachable, SimRouteMiss.SourceUnsnapped,
                                  SimRouteMiss.SinkUnsnapped, SimRouteMiss.SameNode })
            if (r.Miss(m) != 1) bad.Add($"{HaulRouteStage.MissLabel(m)} 桶 = {r.Miss(m)}，应为 1（桶不触发 = 判据空过）");

        if (r.OdSkippedApprox != 1) bad.Add($"降级去向桶={r.OdSkippedApprox} 应为 1");
        if (r.OdSkippedUnknownTrust != 1) bad.Add($"未声明桶={r.OdSkippedUnknownTrust} 应为 1");
        if (r.OdSkippedNoSourcePos != 1) bad.Add($"无坐标桶={r.OdSkippedNoSourcePos} 应为 1");
        if (r.OdInvalid != 1) bad.Add($"空条目桶={r.OdInvalid} 应为 1");

        return new SimCheckItem("J1 条数守恒 + 各未命中桶均可触发", bad.Count == 0,
            bad.Count == 0
                ? $"9 笔 → 问路 5（命中 1 · 未命中 4，四类各 1）+ 未问路 4（降级/未声明/无坐标/空各 1）；Balanced=真"
                : string.Join("；", bad));
    }

    // ── J2 解不出就退回，不画假线 ───────────────────────────────────────────

    private static SimCheckItem J2_解不出逐笔退回且不画假线()
    {
        SimRoadGraph.AttachForTest(GraphBidirectional());
        var (stage, _, sink) = NewStage();

        var r = stage.Apply("2027-02", new List<SimHaulOd>
        {
            Good("U-hit"),
            Od("U-unreach", 1000, 1000, 5000, 5010),
        });

        var bad = new List<string>();
        if (r.Miss(SimRouteMiss.Unreachable) != 1) bad.Add("不连通没计数");
        if (r.DrawnSegments != SegsL1ToD4)
            bad.Add($"画出 {r.DrawnSegments} 段，应恰为命中那一笔的 {SegsL1ToD4} 段"
                  + "（多出来就说明给解不出的那笔补了直线兜底 = 假线）");
        if (sink.Channel.Count != SegsL1ToD4) bad.Add($"通道里有 {sink.Channel.Count} 条环，应为 {SegsL1ToD4}");
        // 每条环必须恰好 2 点：≥3 点会被内核填成半透明面，那就不是线了（xllAcEd.cpp:7791）
        if (sink.Channel.Any(c => c.Ring.Length != 6)) bad.Add("有环不是 2 点 —— 内核会把它填成面");

        return new SimCheckItem("J2 解不出逐笔退回 · 不补假线", bad.Count == 0,
            bad.Count == 0 ? $"2 笔 → 命中 1 画 {SegsL1ToD4} 段，不连通 1 计数且 0 段；环全是 2 点（纯线段）"
                           : string.Join("；", bad));
    }

    // ── J3 命中率必须说得出 ────────────────────────────────────────────────

    /// <summary>
    /// 「接了路网且都中」「接了但一笔没中」「压根没路网」三种情形，**画出来的图一模一样**（都可能是空的），
    /// 所以文案必须互不相同、且都报得出命中率。三条文案两两相同就算不过。
    /// </summary>
    private static SimCheckItem J3_命中率说得出且三种情形文案互不相同()
    {
        SimRoadGraph.AttachForTest(GraphBidirectional());
        var (s1, _, _) = NewStage();
        var allHit = s1.Apply("A", new List<SimHaulOd> { Good("U1") });

        SimRoadGraph.AttachForTest(GraphBidirectional());
        var (s2, _, _) = NewStage();
        var noneHit = s2.Apply("B", new List<SimHaulOd> { Od("U1", 1000, 1000, 5000, 5010) });

        SimRoadGraph.AttachForTest(null);              // 空图 = 压根没路网
        var (s3, _, _) = NewStage();
        var noGraph = s3.Apply("C", new List<SimHaulOd> { Good("U1") });

        var bad = new List<string>();
        if (Math.Abs(allHit.HitRate - 1.0) > 1e-9) bad.Add($"全中的命中率={allHit.HitRate:P1}");
        if (noneHit.HitRate != 0) bad.Add($"一笔没中的命中率={noneHit.HitRate:P1}");
        if (noGraph.Miss(SimRouteMiss.NoGraph) != 1) bad.Add("无路网没计进 NoGraph 桶");
        if (noneHit.DrawnSegments != 0 || noGraph.DrawnSegments != 0) bad.Add("没命中却画出了线");

        string t1 = allHit.Summary + "|" + allHit.HitText;
        string t2 = noneHit.Summary + "|" + noneHit.HitText;
        string t3 = noGraph.Summary + "|" + noGraph.HitText;
        if (t1 == t2 || t2 == t3 || t1 == t3) bad.Add("三种情形的文案有重复 —— 看文案分不出是哪一种");
        if (!t2.Contains("命中") || !t3.Contains("命中")) bad.Add("没命中的两种情形没把命中率写出来");
        if (!noneHit.Summary.StartsWith("◆", StringComparison.Ordinal)) bad.Add("一条线都没画时 Summary 没有醒目前缀");

        return new SimCheckItem("J3 命中率说得出 · 三情形文案不同", bad.Count == 0,
            bad.Count == 0 ? "全中 100% / 一笔没中 0%（不连通）/ 无路网 0%（NoGraph），三条文案两两不同"
                           : string.Join("；", bad));
    }

    // ── J4 降级去向不画（带反例对照）───────────────────────────────────────

    private static SimCheckItem J4_降级去向不画_带反例对照()
    {
        SimRoadGraph.AttachForTest(GraphBidirectional());

        var (sA, _, _) = NewStage();
        var approx = sA.Apply("A", new List<SimHaulOd>
        {
            Od("U-coal", 1000, 1000, 1400, 1300, SimHaulTrust.Approximate, coal: true,
               tons: 5000, destCode: "CR-DEFAULT", material: "coal"),
        });

        // ★ 反例对照组：同一笔，只把可信度改成 Real —— 必须画得出来。
        //   对照组也画不出来，说明判据抓的根本不是可信度。
        var (sB, _, _) = NewStage();
        var real = sB.Apply("B", new List<SimHaulOd>
        {
            Od("U-coal", 1000, 1000, 1400, 1300, SimHaulTrust.Real, coal: true,
               tons: 5000, destCode: "CR-DEFAULT", material: "coal"),
        });

        var bad = new List<string>();
        if (approx.DrawnSegments != 0) bad.Add($"降级去向画出了 {approx.DrawnSegments} 段 —— 这就是一条假线");
        if (approx.OdRouted != 0) bad.Add("降级去向还去问了路（会污染路网的进程级命中率计数）");
        if (approx.OdSkippedApprox != 1) bad.Add("降级去向没计数");
        if (!approx.Notes.Any(n => n.Contains("降级位置"))) bad.Add("降级没留条（吞了）");
        if (real.DrawnSegments != SegsL1ToD4)
            bad.Add($"反例对照组只画了 {real.DrawnSegments} 段（应 {SegsL1ToD4}）—— 判据抓的不是可信度");

        return new SimCheckItem("J4 降级去向不画（含反例对照）", bad.Count == 0,
            bad.Count == 0 ? $"Approximate → 0 段 + 计数 + 留条；同一笔改 Real → {SegsL1ToD4} 段"
                           : string.Join("；", bad));
    }

    // ── J5 未声明 = 不可信 ─────────────────────────────────────────────────

    private static SimCheckItem J5_未声明可信度按不可信处理()
    {
        SimRoadGraph.AttachForTest(GraphBidirectional());
        var (stage, _, _) = NewStage();

        // 刻意用「什么都不填」的默认对象：接线方忘了填 DestTrust 就是这个样子。
        var od = new SimHaulOd
        {
            UnitId = "U-forgot",
            Sx = 1000, Sy = 1000, Sz = 100,
            Dx = 1400, Dy = 1300, Dz = 120,
            DestinationCode = "DUMP-A", TonnageT = 1000,
        };
        var r = stage.Apply("A", new List<SimHaulOd> { od });

        var bad = new List<string>();
        if (od.DestTrust != SimHaulTrust.Unknown) bad.Add("SimHaulOd.DestTrust 的缺省值不是 Unknown（fail-open 了）");
        if (r.DrawnSegments != 0) bad.Add("没声明可信度却画了线");
        if (r.OdSkippedUnknownTrust != 1) bad.Add("没声明的笔数没单列计数");
        if (!r.Notes.Any(n => n.Contains("没有声明去向可信度"))) bad.Add("没留条说清是接线没填、不是路网问题");

        return new SimCheckItem("J5 未声明可信度 → 不画 + 单列 + 留条", bad.Count == 0,
            bad.Count == 0 ? "缺省 Unknown → 0 段，计数与留条都在（fail-closed）" : string.Join("；", bad));
    }

    // ── J6 段级去重 ────────────────────────────────────────────────────────

    private static SimCheckItem J6_段级去重且吨量累加()
    {
        SimRoadGraph.AttachForTest(GraphBidirectional());
        var (stage, _, sink) = NewStage();

        var r = stage.Apply("A", new List<SimHaulOd> { Good("U1", 1000), Good("U2", 3000) });

        var bad = new List<string>();
        if (r.RawSegments != SegsL1ToD4 * 2) bad.Add($"去重前段数={r.RawSegments}，应为 {SegsL1ToD4 * 2}");
        if (r.DistinctSegments != SegsL1ToD4) bad.Add($"去重后段数={r.DistinctSegments}，应为 {SegsL1ToD4}");
        if (sink.Channel.Count != SegsL1ToD4) bad.Add($"通道里 {sink.Channel.Count} 条环，去重没落到通道上");
        foreach (var s in r.Segments)
        {
            if (s.OdCount != 2) { bad.Add($"段的 OdCount={s.OdCount}，应为 2"); break; }
            if (Math.Abs(s.TonnageT - 4000) > 1e-6) { bad.Add($"段吨量={s.TonnageT}，应为 4000（两笔累加）"); break; }
        }

        return new SimCheckItem("J6 段级去重 · 吨量累加", bad.Count == 0,
            bad.Count == 0 ? $"两笔同 O-D：{SegsL1ToD4 * 2} 段 → 去重 {SegsL1ToD4} 段，每段 OdCount=2、吨量 4000"
                           : string.Join("；", bad));
    }

    // ── J7 不擦别人的键（带反例对照）───────────────────────────────────────

    private static SimCheckItem J7_不擦别人的键_带反例对照()
    {
        SimRoadGraph.AttachForTest(GraphBidirectional());
        var (stage, comp, sink) = NewStage();

        // 别人（frame 键）先画了一圈
        var foreign = new double[] { 0, 0, 0, 10, 0, 0, 10, 10, 0, 0, 10, 0 };
        comp.Set(SimOverlayLayers.Frame, new List<double[]> { foreign }, new uint[] { 0xD85A30 });
        comp.Flush();
        int pushBefore = comp.PushCount;

        // 本阶段在帧作用域里画自己那份
        using (comp.BeginFrame()) { stage.Apply("A", new List<SimHaulOd> { Good("U1") }); }

        var bad = new List<string>();
        bool foreignStillThere = sink.Channel.Any(c => c.Ring.Length == foreign.Length && c.Ring[3] == 10 && c.Ring[7] == 10);
        if (!foreignStillThere) bad.Add("别人的 frame 环被擦掉了");
        if (sink.Channel.Count != 1 + SegsL1ToD4) bad.Add($"通道里 {sink.Channel.Count} 条环，应为 1(frame)+{SegsL1ToD4}(haul)");
        if (comp.PushCount - pushBefore != 1) bad.Add($"帧作用域内推了 {comp.PushCount - pushBefore} 次，应恰为 1");

        // ★ 反例对照组：走老做法（直接调落地端 Show，整通道替换）—— 必须把 frame 擦掉。
        //   对照组要是也「没擦掉」，说明这条判据根本没抓到「整通道替换」这件事。
        sink.Show(new List<double[]> { new double[] { 0, 0, 0, 1, 0, 0 } }, new uint[] { 0 });
        bool foreignSurvivedDirect = sink.Channel.Any(c => c.Ring.Length == foreign.Length);
        if (foreignSurvivedDirect) bad.Add("反例对照组（直接调 Show）竟然没擦掉别人的环 —— 判据没抓到整通道替换");

        return new SimCheckItem("J7 各画各的键 · 每帧只推一次（含反例对照）", bad.Count == 0,
            bad.Count == 0 ? $"frame(1) 与 haul({SegsL1ToD4}) 共存，帧作用域内推 1 次；直接调 Show 的对照组把 frame 擦了"
                           : string.Join("；", bad));
    }

    // ── J8 空驶（带反例对照）───────────────────────────────────────────────

    private static SimCheckItem J8_空驶只画重载不经过的段_带反例对照()
    {
        // 反例对照组：双向图，回程与去程同路 ⇒ 空驶差异段必须是 0。
        SimRoadGraph.AttachForTest(GraphBidirectional());
        var (sA, _, _) = NewStage();
        sA.ShowEmptyRun = true;
        var same = sA.Apply("A", new List<SimHaulOd> { Good("U1") });

        // 正例：去程单向、回程绕 K5 ⇒ 空驶必然有 2 段是重载不经过的。
        SimRoadGraph.AttachForTest(GraphOneWayReturn());
        var (sB, _, sinkB) = NewStage();
        sB.ShowEmptyRun = true;
        var diff = sB.Apply("B", new List<SimHaulOd> { Good("U1") });

        var bad = new List<string>();
        if (same.EmptyRunSegments != 0)
            bad.Add($"双向图上空驶画了 {same.EmptyRunSegments} 段 —— 回程与去程同路，不该重复画");
        if (diff.EmptyRunSegments != 2)
            bad.Add($"单向回程图上空驶画了 {diff.EmptyRunSegments} 段，应为 2（J2→K5、K5→L1）");
        if (diff.DrawnSegments != SegsL1ToD4 + 2)
            bad.Add($"总段数={diff.DrawnSegments}，应为 {SegsL1ToD4}+2");
        if (sinkB.Channel.Count != SegsL1ToD4 + 2) bad.Add("空驶没落到通道上");

        return new SimCheckItem("J8 空驶只画重载不经过的段（含反例对照）", bad.Count == 0,
            bad.Count == 0 ? "双向图 → 空驶 0 段；单向回程图 → 空驶 2 段（差异段）"
                           : string.Join("；", bad));
    }

    // ── J9 超上限不静默 ────────────────────────────────────────────────────

    private static SimCheckItem J9_超上限不静默()
    {
        SimRoadGraph.AttachForTest(GraphBidirectional());
        var (stage, _, sink) = NewStage();
        stage.MaxRings = 2;                       // 逼它截断

        var r = stage.Apply("A", new List<SimHaulOd> { Good("U1") });

        var bad = new List<string>();
        if (r.DrawnSegments != 2) bad.Add($"画了 {r.DrawnSegments} 段，上限是 2");
        if (sink.Channel.Count != 2) bad.Add("上限没落到通道上");
        if (r.DroppedByCap != SegsL1ToD4 - 2) bad.Add($"舍掉的段数记成了 {r.DroppedByCap}，应为 {SegsL1ToD4 - 2}");
        if (!r.Notes.Any(n => n.Contains("超过一帧上限"))) bad.Add("截断没留条（静默丢了两段）");
        // 留下来的必须是载荷最大的（这里只有一笔，段吨量相同 —— 至少要保证条数与记账对得上）
        if (r.DrawnSegments + r.DroppedByCap != r.DistinctSegments) bad.Add("画出 + 舍掉 ≠ 去重后段数");

        return new SimCheckItem("J9 超上限截断不静默", bad.Count == 0,
            bad.Count == 0 ? $"上限 2 → 画 2 段、舍 {SegsL1ToD4 - 2} 段并留条；画出+舍掉 = 去重后段数"
                           : string.Join("；", bad));
    }

    // ── J10 颜色 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 煤色 #2B3138 已经接近黑：载荷分档若按「乘亮度系数」做，末梢煤线会被压成看不见的纯黑。
    /// 本阶段用的是「向中性灰插值」，所以档位越低煤线越**亮**、岩线越淡 —— 这条判据就是钉住这个。
    /// </summary>
    private static SimCheckItem J10_煤色不会被载荷分档压成不可见()
    {
        static int Lum(uint c) => (int)((c >> 16 & 0xFF) * 30 + (c >> 8 & 0xFF) * 59 + (c & 0xFF) * 11) / 100;

        uint coal = SimMaterialColorProvider.RgbCoal;
        uint rock = SimMaterialColorProvider.RgbRock;
        uint coalTail = HaulRouteStage.TowardGray(coal, 0.68);
        uint rockTail = HaulRouteStage.TowardGray(rock, 0.68);

        var bad = new List<string>();
        if (Lum(coalTail) <= Lum(coal)) bad.Add("末梢煤色没有比本色更亮 —— 会被压进黑里看不见");
        if (Lum(rockTail) >= Lum(rock)) bad.Add("末梢岩色没有比本色更淡");
        if (HaulRouteStage.TowardGray(coal, 0) != coal) bad.Add("干线档不是本色（t=0 应恒等）");
        if (HaulRouteStage.TowardGray(coal, 1) != 0x808080u) bad.Add("t=1 应落到中性灰");
        // 煤与岩在同一档上仍要能分辨（色相是物料，不能被灰度吃掉）
        if (coalTail == rockTail) bad.Add("同一档上煤色与岩色变成了同一个值 —— 物料分不出来了");

        return new SimCheckItem("J10 载荷分档不吃掉物料色", bad.Count == 0,
            bad.Count == 0
                ? $"煤 #{coal:X6}→末梢 #{coalTail:X6}（变亮）· 岩 #{rock:X6}→末梢 #{rockTail:X6}（变淡）· 两者仍可分辨"
                : string.Join("；", bad));
    }
}
