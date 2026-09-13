// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SinkFromLedgerTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.TaskLib.Engine;
using Xunit;
using Xunit.Abstractions;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「采掘单元台账 → 排土场」的判据（SF 组）。
///
/// <para><b>背景</b>：去向台账原来靠 <c>V004</c> 的两条种子撑着
/// （北排土场 12000/8500、内排土场 5000/3200，全是脚本里编的数）。代价是：
/// 名字对不上台账（「内排土场1」vs「内排土场」）⇒ 绑定按名字精确配 ⇒
/// <b>17 个排土面整月零入方</b>；库容差 40 倍（真实 1291.1 万m³ vs 种子 17000 万m³）⇒ 库容闸等于没闸。</para>
///
/// <para><b>这一组守两件事</b>：场名<b>原样</b>取台账那一列（差一个字符就绑不上）·
/// 派生不出来的列<b>一律留空，不给缺省</b>。</para>
/// </summary>
public class SinkFromLedgerTests
{
    private readonly ITestOutputHelper _out;
    public SinkFromLedgerTests(ITestOutputHelper o) => _out = o;

    private static MiningUnitLedger.Row Slot(string region, double capM3,
                                             double zlo = 1100, double zhi = 1112, string id = "")
        => new()
        {
            UnitId = id.Length > 0 ? id : $"{region}-L1-P{Guid.NewGuid().ToString("N")[..4]}",
            Kind = LedgerKind.Dump,
            Region = region,
            DumpCapM3 = capM3,
            ZLo = zlo, ZHi = zhi,
        };

    private static MiningUnitLedger.Row Pit(string region = "采场1")
        => new() { UnitId = "9-B1-P1", Kind = LedgerKind.Rock, Region = region, NetRockM3 = 5e4 };

    // ══════════════════════════════════════════════════════════════
    //  SF1 场名原样取
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// SF1 场名<b>原样取台账那一列</b> —— 不清洗、不去尾号、不归一化。
    /// <para>绑定端读的是同一列，差一个字符就配不上。
    /// 「内排土场1」被清洗成「内排土场」正是这次事故的形状。</para>
    /// </summary>
    [Fact]
    public void SF1_场名原样取不做清洗()
    {
        var r = SinkFromLedgerBuilder.Build(new[]
        {
            Slot("内排土场1", 1e4), Slot("内排土场1", 2e4),
        });

        var one = Assert.Single(r.Sinks);
        Assert.Equal("内排土场1", one.Name);          // 不是「内排土场」
        Assert.Equal(3e4, one.CapacityM3);
        Assert.Equal(2, one.SlotCount);
    }

    /// <summary>SF1b 不同场名分成不同的场，<b>不按前缀合并</b>。</summary>
    [Fact]
    public void SF1b_不同场名不合并()
    {
        var r = SinkFromLedgerBuilder.Build(new[]
        {
            Slot("内排土场1", 1e4), Slot("内排土场2", 2e4), Slot("北排土场", 3e4),
        });
        Assert.Equal(3, r.Sinks.Count);
    }

    /// <summary>SF1c 采场单元不参与 —— 它不是去向。</summary>
    [Fact]
    public void SF1c_采场单元不参与()
    {
        var r = SinkFromLedgerBuilder.Build(new[] { Pit(), Pit("采场2"), Slot("内排土场1", 1e4) });
        Assert.Equal("内排土场1", Assert.Single(r.Sinks).Name);
    }

    // ══════════════════════════════════════════════════════════════
    //  SF2 缺的东西不许编
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// SF2 <b>结论里必须写明哪些列派生不出来</b>。
    /// <para>已填/坐标/通过能力/工作线长/兜底运距/时窗都是现场的事实，台账里没有。
    /// 给个缺省值会让运距、库容告警、时窗全都看着正常而实际是假的。</para>
    /// </summary>
    [Fact]
    public void SF2_说明哪些列派生不出来()
    {
        var r = SinkFromLedgerBuilder.Build(new[] { Slot("内排土场1", 1e4) });

        Assert.Contains(r.Notes, n => n.Contains("派生不出来") && n.Contains("不给缺省值"));
        foreach (var col in new[] { "已填", "坐标", "兜底运距" })
            Assert.Contains(r.Notes, n => n.Contains(col));
    }

    /// <summary>
    /// SF2b 没有库容的位置<b>不按 0 计入</b>，而是单独点名。
    /// <para>按 0 算会让这个场的容量凭空缩水，而库容告警照常"正常"。</para>
    /// </summary>
    [Fact]
    public void SF2b_没库容的位置单独点名不按零算()
    {
        var noCap = Slot("内排土场1", 0);
        noCap.DumpCapM3 = null;                       // 没有这笔数，不是 0
        var r = SinkFromLedgerBuilder.Build(new[] { Slot("内排土场1", 1e4), noCap });

        var one = Assert.Single(r.Sinks);
        Assert.Equal(1e4, one.CapacityM3);            // 只算得出来的那条
        Assert.Equal(2, one.SlotCount);               // 但位置数照实报
        Assert.Contains(r.Notes, n => n.Contains("没有库容") && n.Contains("不是按 0 算"));
    }

    /// <summary>SF2c 没有场名的位置归不到任何场，单独点名（场名是绑定用的钥匙）。</summary>
    [Fact]
    public void SF2c_没场名的位置单独点名()
    {
        var r = SinkFromLedgerBuilder.Build(new[] { Slot("", 1e4), Slot("内排土场1", 2e4) });

        Assert.Single(r.Sinks);
        Assert.Contains(r.Notes, n => n.Contains("没有场名") && n.Contains("钥匙"));
    }

    // ══════════════════════════════════════════════════════════════
    //  SF3 台阶高
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// SF3 台阶高取<b>中位数</b>不取均值 —— 混进一个跨两级的厚位置时均值会被拉高。
    /// </summary>
    [Fact]
    public void SF3_台阶高取中位数()
    {
        var r = SinkFromLedgerBuilder.Build(new[]
        {
            Slot("内排土场1", 1e4, 1100, 1112), Slot("内排土场1", 1e4, 1100, 1112),
            Slot("内排土场1", 1e4, 1100, 1112), Slot("内排土场1", 1e4, 1100, 1112),
            Slot("内排土场1", 1e4, 1040, 1112),          // 厚 72m 的异类
        });
        Assert.Equal(12, Assert.Single(r.Sinks).BenchHeightM, 1);
    }

    /// <summary>SF3b 一条都算不出台阶高时是 <b>0 = 没有</b>，不是"平的"。</summary>
    [Fact]
    public void SF3b_台阶高算不出时是零()
    {
        var r = SinkFromLedgerBuilder.Build(new[] { Slot("内排土场1", 1e4, 1100, 1100) });
        Assert.Equal(0, Assert.Single(r.Sinks).BenchHeightM);
    }

    // ══════════════════════════════════════════════════════════════
    //  SF4 编号稳定
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// SF4 同一个场名<b>永远得到同一个编号</b> —— 重建不会长出重复行。
    /// <para>编号不稳的话，每点一次「按台账重建」就多一份同名场，
    /// 而每一份看着都对，库容却被分成了两半。</para>
    /// </summary>
    [Fact]
    public void SF4_编号对同一场名稳定()
    {
        string a = SinkFromLedgerBuilder.IdOf("内排土场1");
        Assert.Equal(a, SinkFromLedgerBuilder.IdOf("内排土场1"));
        Assert.NotEqual(a, SinkFromLedgerBuilder.IdOf("内排土场2"));
        Assert.StartsWith("DL-", a);                  // 与人工建的 D- 分得开
        Assert.Equal("", SinkFromLedgerBuilder.IdOf("  "));
    }

    // ══════════════════════════════════════════════════════════════
    //  SF5 空台账
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// SF5 台账里一个排土位置都没有时<b>说清楚该去哪补</b>，不是静默返回空。
    /// </summary>
    [Fact]
    public void SF5_没有排土位置时说清楚去哪补()
    {
        foreach (var rows in new[] { null, Array.Empty<MiningUnitLedger.Row>(), new[] { Pit() } })
        {
            var r = SinkFromLedgerBuilder.Build(rows);
            Assert.False(r.Ok);
            Assert.Contains("排土条带", r.Headline);
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  SF6 对着真台账跑（有就跑）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// SF6 真台账上派生出来的场名，必须<b>正好等于</b>台账里出现过的场名集合。
    /// <para>这条是名字对齐的终极检验：两边取的是同一列，集合就该一模一样。</para>
    /// </summary>
    [Fact]
    public void SF6_真台账上场名集合完全一致()
    {
        const string Root = @"C:\Users\0doudou\Desktop\DayOps\PitMine3D\bin\Debug\Data\采掘单元台账";
        if (!System.IO.Directory.Exists(Root)) { _out.WriteLine("SKIP: 台账目录不在"); return; }

        var store = new MonthlyUnitLedgerStore(Root);
        if (!store.TryLoadBase(out var rows, out _)) { _out.WriteLine("SKIP: 基表读不出来"); return; }

        var built = SinkFromLedgerBuilder.Build(rows);
        _out.WriteLine(built.Headline);
        foreach (var n in built.Notes) _out.WriteLine("  " + n);

        var fromLedger = rows.Where(r => r != null && r.Kind == LedgerKind.Dump)
                             .Select(r => (r.Region ?? "").Trim())
                             .Where(x => x.Length > 0)
                             .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var fromBuild = built.Sinks.Select(s => s.Name)
                             .OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(fromLedger, fromBuild);
    }
}
