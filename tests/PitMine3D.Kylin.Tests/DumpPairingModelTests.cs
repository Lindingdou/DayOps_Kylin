using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Tasks;
using PitMine3D.Kylin.Cad.Tasks.Scheduling;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 采排配对（§三六一）。
///
/// 三条头号判据：
///   ① <b>库容按占容方扣</b>（V容 = V实 × Kr）。拿实方去扣会把排土场算得比实际能装得多 ——
///      Kr &gt; 1，同样的实方占的库容更大，扣少了就会出现"计划排得下、现场排不下"。
///   ② <b>设计库容没录 ⇒ 期末剩余判不了</b>，不是 0、更不是"够用"。写 0 会被看成刚好排满。
///   ③ <b>过站不占库容</b>：破碎站/煤仓/堆场不是排弃去向，把它们的入方也扣进排土库容，
///      排土场会凭空少掉一大块。
/// </summary>
public class DumpPairingModelTests
{
    private const string Period = "2026-09";

    private static MaterialFlow F(string src = "北一采", string mat = "rock", double m3 = 1000,
                                  string sinkId = "D1", string sinkName = "北排土场",
                                  SinkKind kind = SinkKind.ExternalDump, double km = 2.5)
        => new()
        {
            Period = Period, SourceName = src, SourceId = src, MaterialCode = mat, InSituM3 = m3,
            SinkId = sinkId, SinkName = sinkName, SinkKind = kind, EquivHaulKm = km,
        };

    private static SinkRegistry Reg(params SinkNode[] nodes)
    {
        var r = new SinkRegistry();
        foreach (var n in nodes) r.Put(n);
        return r;
    }

    private static SinkNode N(string id = "D1", string name = "北排土场", SinkKind kind = SinkKind.ExternalDump,
                              double design = 1e6, double filled = 0)
        => new() { Id = id, Name = name, Kind = kind, DesignCapacityM3 = design, FilledM3 = filled };

    // ── 库容按占容方扣 ──────────────────────────────────────
    [Fact]
    public void 库容_本期入方按占容方不是实方()
    {
        // ★ Kr > 1：同样的实方占的库容更大，按实方扣就扣少了
        var spec = MaterialCatalog.Resolve(MaterialCatalog.CodeFromText("rock"));
        var v = DumpPairingModel.Build(new[] { F(m3: 1000) }, Reg(N()), Period);
        var bar = v.Sinks.Single();
        Assert.Equal(spec.ToDumpM3(1000), bar.InboundDumpM3, 6);
        Assert.True(bar.InboundDumpM3 > 1000, "占容方应大于实方（Kr > 1）");
    }

    [Fact]
    public void 库容_期末剩余是设计减已填减本期入方()
    {
        var spec = MaterialCatalog.Resolve(MaterialCatalog.CodeFromText("rock"));
        var v = DumpPairingModel.Build(new[] { F(m3: 1000) }, Reg(N(design: 1e5, filled: 2e4)), Period);
        var bar = v.Sinks.Single();
        Assert.Equal(1e5 - 2e4 - spec.ToDumpM3(1000), bar.RemainM3!.Value, 3);
    }

    [Fact]
    public void 库容_设计没录时剩余判不了不是零()
    {
        // ★ 写 0 会被看成"刚好排满"
        var v = DumpPairingModel.Build(new[] { F() }, Reg(N(design: 0)), Period);
        var bar = v.Sinks.Single();
        Assert.Null(bar.RemainM3);
        Assert.Null(bar.UsedPct);
        Assert.False(bar.Overflow);                       // 判不了不是"排不下"
        Assert.Contains("判不了", bar.Caption);
        Assert.Contains("判不了", string.Join("|", v.Notes));
        Assert.Contains("不是「够用」", string.Join("|", v.Notes));
    }

    [Fact]
    public void 库容_排不下时点名还差多少()
    {
        var v = DumpPairingModel.Build(new[] { F(m3: 1e5) }, Reg(N(design: 1e4)), Period);
        Assert.True(v.Sinks.Single().Overflow);
        Assert.Contains("本期排不下", string.Join("|", v.Notes));
        Assert.Contains("还差", string.Join("|", v.Notes));
    }

    [Fact]
    public void 库容_过站类去向不占排土库容()
    {
        // ★ 把破碎站的入方也扣进排土库容，排土场会凭空少掉一大块
        var v = DumpPairingModel.Build(
            new[] { F(sinkId: "C1", sinkName: "破碎站一", kind: SinkKind.Crusher, mat: "coal") },
            Reg(N("C1", "破碎站一", SinkKind.Crusher, design: 0)), Period);
        Assert.Equal(0, v.Sinks.Single().InboundDumpM3, 9);
    }

    [Fact]
    public void 库容_没有去向台账时设计与已填判不了但入方照算()
    {
        var v = DumpPairingModel.Build(new[] { F(m3: 1000) }, null, Period);
        var bar = v.Sinks.Single();
        Assert.Equal(0, bar.DesignM3, 9);
        Assert.Null(bar.RemainM3);
        Assert.True(bar.InboundDumpM3 > 0);
    }

    // ── 源—汇矩阵 ───────────────────────────────────────────
    [Fact]
    public void 矩阵_一行是一个作业面乘一种物料()
    {
        var v = DumpPairingModel.Build(new[]
        {
            F(src: "北一采", mat: "coal", sinkId: "C1", sinkName: "破碎站", kind: SinkKind.Crusher),
            F(src: "北一采", mat: "rock"),
            F(src: "南二采", mat: "rock"),
        }, null, Period);
        Assert.Equal(3, v.Rows.Count);
        Assert.Contains(v.Rows, r => r.SourceName == "北一采" && r.MaterialCode == "coal");
    }

    [Fact]
    public void 矩阵_同一条OD多笔流合成一格且运距按吨量加权()
    {
        // 直接取平均会让小笔和大笔一样重
        var v = DumpPairingModel.Build(new[]
        { F(m3: 9000, km: 1), F(m3: 1000, km: 11) }, null, Period);
        var cell = v.Rows.Single().Cells.Values.Single();
        Assert.Equal(10000, cell.InSituM3, 6);
        Assert.Equal(2.0, cell.HaulKm, 6);               // (9000×1 + 1000×11) / 10000
    }

    [Fact]
    public void 矩阵_没指派去向的量不进格子而是单独报()
    {
        var v = DumpPairingModel.Build(new[] { F(sinkId: "", sinkName: "") }, null, Period);
        var row = v.Rows.Single();
        Assert.Empty(row.Cells);
        Assert.Equal(1000, row.UnroutedM3, 6);
        Assert.Contains("没有去向", string.Join("|", v.Notes));
        Assert.Contains("采了没处去", string.Join("|", v.Notes));
    }

    [Fact]
    public void 矩阵_格子文案没量时是破折号有量时带运距()
    {
        Assert.Equal("—", new PairingCell().Caption);
        var v = DumpPairingModel.Build(new[] { F(m3: 1234, km: 3) }, null, Period);
        string cap = v.Rows.Single().Cells.Values.Single().Caption;
        Assert.Contains("1,234 m³", cap);
        Assert.Contains("3 km", cap);
    }

    [Fact]
    public void 矩阵_没运距时格子写未录不写零km()
    {
        var v = DumpPairingModel.Build(new[] { F(km: 0) }, null, Period);
        Assert.Contains("运距未录", v.Rows.Single().Cells.Values.Single().Caption);
        Assert.Contains("没录运距", string.Join("|", v.Notes));
    }

    [Fact]
    public void 矩阵_去向列排弃类排在过站类前面()
    {
        var v = DumpPairingModel.Build(new[]
        {
            F(sinkId: "C1", sinkName: "破碎站", kind: SinkKind.Crusher, mat: "coal"),
            F(sinkId: "D1", sinkName: "北排土场"),
        }, null, Period);
        Assert.Equal("北排土场", v.Sinks[0].SinkName);
    }

    // ── 汇总复用 PeriodBalance ──────────────────────────────
    [Fact]
    public void 汇总_直接复用期物料平衡不另算一份()
    {
        var flows = new[]
        {
            F(mat: "coal", m3: 2000, sinkId: "C1", sinkName: "破碎站", kind: SinkKind.Crusher, km: 1),
            F(mat: "rock", m3: 8000, km: 3),
        };
        var v = DumpPairingModel.Build(flows, null, Period);
        var direct = new PeriodBalance { Period = Period, Flows = flows.ToList() };
        Assert.Equal(direct.OreWanT, v.Balance.OreWanT, 9);
        Assert.Equal(direct.StripWanM3, v.Balance.StripWanM3, 9);
        Assert.Equal(direct.StripRatio, v.Balance.StripRatio, 9);
        Assert.Equal(direct.DumpedWanM3, v.Balance.DumpedWanM3, 9);
        Assert.Equal(direct.InternalDumpPct, v.Balance.InternalDumpPct, 9);
        Assert.Equal(direct.TransportWorkWanTKm, v.Balance.TransportWorkWanTKm, 9);
    }

    [Fact]
    public void 汇总_没有煤时剥采比写破折号不写零()
    {
        var v = DumpPairingModel.Build(new[] { F(mat: "rock") }, null, Period);
        Assert.Contains("剥采比 —", v.SummaryCaption);
    }

    [Fact]
    public void 汇总_内排率按占容方算且只在有排弃量时给()
    {
        var v = DumpPairingModel.Build(new[]
        {
            F(sinkId: "D1", sinkName: "外排", kind: SinkKind.ExternalDump, m3: 7000),
            F(sinkId: "D2", sinkName: "内排", kind: SinkKind.InternalDump, m3: 3000),
        }, null, Period);
        Assert.Equal(30, v.Balance.InternalDumpPct, 6);   // 同物料 ⇒ 占容比 = 实方比
        Assert.Contains("内排率 30%", v.SummaryCaption);
    }

    [Fact]
    public void 汇总_没有流时说清楚并给补法()
    {
        var v = DumpPairingModel.Build(Array.Empty<MaterialFlow>(), null, Period);
        Assert.True(v.Empty);
        Assert.Contains("无从谈起", v.SummaryCaption);
        Assert.Contains("作业面台账", string.Join("|", v.Notes));
    }

    [Fact]
    public void 汇总_零量的流不进矩阵()
        => Assert.True(DumpPairingModel.Build(new[] { F(m3: 0) }, null, Period).Empty);

    // ── 导出 ────────────────────────────────────────────────
    [Fact]
    public void 导出_两段表头都在且占容方口径写在里面()
    {
        var v = DumpPairingModel.Build(new[] { F() }, Reg(N()), Period);
        string csv = DumpPairingModel.ToCsv(v);
        Assert.Contains("作业面,物料,去向,实方m3,运距km", csv);
        Assert.Contains("V容 = V实 × Kr", csv);
        Assert.Contains("去向,种类,设计m3,已填m3,本期入方占容m3,期末剩余m3", csv);
    }

    [Fact]
    public void 导出_设计库容未录时剩余那一格留空不写零()
    {
        var v = DumpPairingModel.Build(new[] { F() }, Reg(N(design: 0)), Period);
        string csv = DumpPairingModel.ToCsv(v);
        var line = csv.Split('\n').First(l => l.StartsWith("北排土场", StringComparison.Ordinal));
        Assert.EndsWith(",", line.TrimEnd('\r'));        // 末列（期末剩余）为空
    }
}
