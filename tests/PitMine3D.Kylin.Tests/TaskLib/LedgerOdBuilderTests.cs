// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/LedgerOdBuilderTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 台账 → 运输 O-D 的判据。
///
/// <para><b>H4 是这组的骨架</b>：每一笔流必须落进<b>恰好一个</b>桶（画出来，或某一种跳过）。
/// 少记一笔就意味着悄悄丢了一笔，而命中率看上去照样正常。
/// 而且四个跳过桶<b>各造一笔</b> —— 一个从来不触发的计数器等于没有这条判据。</para>
/// </summary>
public class LedgerOdBuilderTests
{
    private static MiningUnitLedger.Row Dump(string region, string seam, int band, int panel,
                                             double cx = 1000, double cy = 2000, double cz = 100)
        => new()
        {
            UnitId = $"{region}-{seam}-P{panel:00}", Kind = LedgerKind.Dump,
            Region = region, Seam = seam, Band = band, Panel = panel,
            Cx = cx, Cy = cy, Cz = cz,
        };

    private static MiningUnitLedger.Row Src(string id, LedgerKind kind, string dest,
                                            double cx = 500, double cy = 600, double m3 = 1000,
                                            string mat = "")
    {
        var r = new MiningUnitLedger.Row { UnitId = id, Kind = kind, Cx = cx, Cy = cy, Cz = 50 };
        r.Flows.Add(new MiningUnitLedger.Flow { Destination = dest, InSituM3 = m3, MaterialCode = mat });
        return r;
    }

    private static CoalSinkPoint Sink() => new()
    { X = 9000, Y = 9000, Z = 120, Name = "1号破碎站", PickedAt = new DateTime(2026, 8, 7) };

    // ═══ H1：去向码往返一致、不撞码 ═══
    [Fact]
    public void H1_去向码造出来能解回它自己()
    {
        var dumps = new List<MiningUnitLedger.Row>
        { Dump("内排土场1", "L3", 1, 1), Dump("内排土场1", "L2", 1, 2), Dump("内排土场1", "L1", 1, 3) };

        var idx = DumpSlotCode.BuildIndex(dumps, out int maxLv, out var collided);

        Assert.Equal(3, maxLv);
        Assert.Empty(collided);
        Assert.Equal(3, idx.Count);
        foreach (var d in dumps)
            Assert.Same(d, idx[DumpSlotCode.OfLedgerRow(d, maxLv)]);
    }

    // ═══ H2：极性真的翻了（反例对照）═══
    [Fact]
    public void H2_极性翻转最上一级码最大最下一级为L0()
    {
        var top = Dump("内排土场1", "L1", 1, 1);     // 台账 L1 = 最上一级
        var bottom = Dump("内排土场1", "L3", 1, 1);
        int maxLv = DumpSlotCode.MaxLevelOf(new[] { top, bottom });

        string ctop = DumpSlotCode.OfLedgerRow(top, maxLv);
        string cbot = DumpSlotCode.OfLedgerRow(bottom, maxLv);

        Assert.Contains("-L2-", ctop);      // 3−1
        Assert.Contains("-L0-", cbot);      // 3−3，最下一级 = 0
        Assert.NotEqual(ctop, cbot);
        // 不翻极性的话两者互换 —— 计划会从山顶往下排，而每项校核还都是 ✓
    }

    // ═══ H3：撞码整条剔除，指向它的流必须解不出（不是"后者胜"）═══
    [Fact]
    public void H3_撞码整条剔除而不是后者胜()
    {
        var a = Dump("内排土场1", "L1", 1, 1, cx: 1000, cy: 1000);
        var b = Dump("内排土场1", "L1", 1, 1, cx: 8888, cy: 8888);   // 同 (场,级,带,幅) ⇒ 同码
        var baseRows = new List<MiningUnitLedger.Row> { a, b };

        var idx = DumpSlotCode.BuildIndex(baseRows, out int maxLv, out var collided);
        Assert.Single(collided);
        Assert.Empty(idx);                       // 撞了就谁也别要

        string code = DumpSlotCode.OfLedgerRow(a, maxLv);
        var res = LedgerOdBuilder.Build(baseRows, new List<MiningUnitLedger.Row> { Src("岩1", LedgerKind.Rock, code) }, Sink());

        Assert.Empty(res.Ods);
        Assert.Equal(1, res.SkipDestUnresolved);
        Assert.Contains(res.Notes, n => n.StartsWith("◆") && n.Contains("同时占用"));
        // "后者胜"的话那笔流会被画到 (8888,8888) 上 —— 线长、颜色、命中率全都正常
    }

    // ═══ H4：分类账自洽，且四个跳过桶各自都真的会触发 ═══
    [Fact]
    public void H4_分类账自洽且每个跳过桶都触发()
    {
        var good = Dump("内排土场1", "L1", 1, 1, cx: 1000, cy: 2000);
        var noXy = Dump("内排土场1", "L1", 1, 2, cx: 0, cy: 0);      // 汇有行但没坐标
        var baseRows = new List<MiningUnitLedger.Row> { good, noXy };
        int maxLv = DumpSlotCode.MaxLevelOf(baseRows);

        var flows = new List<MiningUnitLedger.Row>
        {
            Src("正常", LedgerKind.Rock, DumpSlotCode.OfLedgerRow(good, maxLv)),
            Src("空去向", LedgerKind.Rock, ""),
            Src("解不出", LedgerKind.Rock, "内排土场1-L9-9999999"),
            Src("汇无坐标", LedgerKind.Rock, DumpSlotCode.OfLedgerRow(noXy, maxLv)),
            Src("源无坐标", LedgerKind.Rock, DumpSlotCode.OfLedgerRow(good, maxLv), cx: 0, cy: 0),
        };

        var res = LedgerOdBuilder.Build(baseRows, flows, Sink());

        Assert.Equal(5, res.FlowsTotal);
        Assert.True(res.Balanced, "分类账不自洽 —— 有流没记账");
        Assert.Equal(1, res.Emitted);
        Assert.Equal(1, res.SkipNoDestCode);
        Assert.Equal(1, res.SkipDestUnresolved);
        Assert.Equal(1, res.SkipDestNoXy);
        Assert.Equal(1, res.SkipSrcNoXy);
    }

    // ═══ H5：煤没卸点是降级、给了卸点是真点（反例对照）═══
    [Fact]
    public void H5_煤流没卸点时降级且不拿质心冒充()
    {
        var baseRows = new List<MiningUnitLedger.Row> { Dump("内排土场1", "L1", 1, 1) };
        var flows = new List<MiningUnitLedger.Row> { Src("煤1", LedgerKind.Coal, "CR-DEFAULT") };

        var without = LedgerOdBuilder.Build(baseRows, flows, null);
        Assert.Single(without.Ods);
        Assert.Equal(SimHaulTrust.Approximate, without.Ods[0].DestTrust);
        Assert.Equal(0, without.Ods[0].Dx);          // ★ 绝不拿采场质心冒充卸点
        Assert.Equal(0, without.Ods[0].Dy);
        Assert.Equal(1, without.EmittedCoalApprox);
        Assert.Contains(without.Notes, n => n.StartsWith("◆") && n.Contains("没有坐标"));

        var with = LedgerOdBuilder.Build(baseRows, flows, Sink());
        Assert.Single(with.Ods);
        Assert.Equal(SimHaulTrust.Real, with.Ods[0].DestTrust);
        Assert.Equal(9000, with.Ods[0].Dx);
        Assert.Equal(1, with.EmittedCoalReal);
        // 指了卸点之后必须提醒运距口径对不上 —— 线是真的、台账那个数是旧的
        Assert.Contains(with.Notes, n => n.Contains("排产时算运距用的不是这个点"));
    }

    // ═══ H6：没有排土行时整批解不出并醒目报出 ═══
    [Fact]
    public void H6_基表里没有排土行时醒目报出()
    {
        // 模拟"只读了月度台账"：传进来的 baseRows 里一条排土行都没有
        var flows = new List<MiningUnitLedger.Row> { Src("岩1", LedgerKind.Rock, "内排土场1-L0-1000100") };
        var res = LedgerOdBuilder.Build(flows, flows, Sink());

        Assert.Equal(0, res.SlotIndexed);
        Assert.Equal(0, res.MaxDumpLevel);
        Assert.Empty(res.Ods);
        Assert.Equal(1, res.SkipDestUnresolved);
        Assert.StartsWith("◆", res.Summary);
        Assert.Contains(res.Notes, n => n.Contains("只存在【基表】里"));
        // 没有这条留条的话，界面上只会显示"命中率 0%"，看起来像路网问题
    }

    // ═══ H7：吨量走物料目录，未知码不静默 ═══
    [Fact]
    public void H7_吨量走物料目录且未知码要报出来()
    {
        var d = Dump("内排土场1", "L1", 1, 1);
        var baseRows = new List<MiningUnitLedger.Row> { d };
        string code = DumpSlotCode.OfLedgerRow(d, DumpSlotCode.MaxLevelOf(baseRows));

        var known = LedgerOdBuilder.Build(baseRows,
            new List<MiningUnitLedger.Row> { Src("岩1", LedgerKind.Rock, code, m3: 1000, mat: "rock") }, Sink());
        Assert.Single(known.Ods);
        double rockT = known.Ods[0].TonnageT;
        Assert.True(rockT > 0);
        Assert.DoesNotContain(known.Notes, n => n.Contains("不在物料目录"));

        var unknown = LedgerOdBuilder.Build(baseRows,
            new List<MiningUnitLedger.Row> { Src("岩2", LedgerKind.Rock, code, m3: 1000, mat: "gravel") }, Sink());
        Assert.Single(unknown.Ods);
        Assert.Equal(rockT, unknown.Ods[0].TonnageT, 6);      // 回落硬岩
        // ★ 但必须说出来 —— 静默回落会让载荷分档偏而没人知道
        Assert.Contains(unknown.Notes, n => n.StartsWith("◆") && n.Contains("gravel"));
    }

    // ═══ H8：一笔流都没有时说得出是空的 ═══
    [Fact]
    public void H8_没有流时给出说明而不是假成功()
    {
        var res = LedgerOdBuilder.Build(new List<MiningUnitLedger.Row>(), new List<MiningUnitLedger.Row>(), null);
        Assert.Empty(res.Ods);
        Assert.Equal(0, res.FlowsTotal);
        Assert.True(res.Balanced);
        Assert.Contains("先排一次产", res.Summary);
    }

    // ═══ H9：排土行不当源（它是汇）═══
    [Fact]
    public void H9_排土行不会被当成源造出O_D()
    {
        var d = Dump("内排土场1", "L1", 1, 1);
        d.Flows.Add(new MiningUnitLedger.Flow { Destination = "内排土场1-L0-1000100", InSituM3 = 500 });
        var res = LedgerOdBuilder.Build(new List<MiningUnitLedger.Row> { d }, new List<MiningUnitLedger.Row> { d }, Sink());

        Assert.Equal(0, res.FlowsTotal);      // 排土行的流不计入
        Assert.Empty(res.Ods);
    }
}
