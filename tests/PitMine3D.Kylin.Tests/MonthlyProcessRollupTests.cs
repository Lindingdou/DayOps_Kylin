// 忠实移植自原 PitMine3D Tests/Tests.PitMineApp/MonthlyProcessRollupTests.cs（逐行对应；仅命名空间适配 —— 合成块体经 Tests.Synth.BlockModel 隐式转 InclineBlockSource）
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Cad.Units;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Tests.Synth;
using BlockModel = PitMine3D.Kylin.Tests.Synth.BlockModel;
using WorkLineGeometry = PitMine3D.Kylin.Cad.WorkLineSamples;
using MonthPeriod = PitMine3D.Kylin.Cad.Plan.MonthPeriod;
using ShortTermPlan = PitMine3D.Kylin.Cad.Plan.ShortTermPlan;
using DumpMode = PitMine3D.Kylin.Cad.DumpMode;
using DumpStripStore = PitMine3D.Kylin.UnitLedger.DumpStripStore;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// MR 组 · 月度工序量汇总（<see cref="MonthlyProcessRollup"/>）。
///
/// <para>这是任务编制真正要吃的那张表，它的失败方式全是<b>"少了一块而合计看着挺正常"</b>：
/// 没归属的单元被漏掉、算不出量的面被当成 0 加进去、免爆和算不出混成同一个 0。
/// 三种都不会让任何数字变成负的或爆掉 —— 合计仍然是一个像模像样的数。</para>
/// </summary>
public sealed class MonthlyProcessRollupTests
{
    private readonly ITestOutputHelper _out;
    public MonthlyProcessRollupTests(ITestOutputHelper o) => _out = o;

    private const int Y = 2026, M = 5;

    private static UnitAssignment Plan(string id, double m3, string? dest = null, double kr = 1.2,
                                       UnitKind k = UnitKind.Rock)
    {
        var a = new UnitAssignment { UnitId = id, Kind = k, Seq = 1, InSituM3 = m3 };
        if (dest != null)
            a.Flows.Add(new UnitFlow
            {
                DestinationCode = dest, DestinationName = dest, IsInternalDump = true,
                InSituM3 = m3, DumpM3 = m3 * kr,
            });
        return a;
    }

    /// <summary>孔网 7×8、台阶 15、超深 1.5、单耗 0.32 的标准面。</summary>
    private static WorkingFace Face(string name, string material, double bench = 15, string code = "")
    {
        var f = new WorkingFace { Name = name, MaterialCode = material, FaceCode = code };
        f.Process.HoleSpacingM = 7; f.Process.HoleBurdenM = 8;
        f.Process.BenchHeightM = bench; f.Process.SubDrillM = 1.5;
        f.Process.PowderFactorKgPerM3 = 0.32;
        return f;
    }

    private void Dump(MonthlyProcessSummary s)
    {
        _out.WriteLine(s.Summary());
        foreach (var n in s.Notes) _out.WriteLine("  " + n);
        _out.WriteLine(s.ToCsv());
    }

    // ══════════════════════════════════════════════════════════════
    //  MR1 正常算例：逐面工序量 + 合计
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void MR1_逐面工序量与合计()
    {
        var plan = new[] { Plan("U1", 100_000, "DP1"), Plan("U2", 50_000, "DP1") };
        var faces = new[] { Face("硬岩面", PlanMaterialCatalog.Rock) };
        var map = new Dictionary<string, string> { ["U1"] = "硬岩面", ["U2"] = "硬岩面" };

        var s = MonthlyProcessRollup.Build(plan, faces, map, Y, M);
        Dump(s);

        Assert.Single(s.Faces);
        Assert.Equal(150_000, s.TotalInSituM3, 3);
        Assert.Equal(150_000 * 1.2, s.TotalDumpM3, 3);              // 占容走 UnitFlow.DumpM3，没再乘一遍 Kr
        Assert.Equal(150_000 * 16.5 / 840.0, s.TotalDrillMeters, 3);
        Assert.Equal(150_000 / 840.0, s.TotalHoleCount, 3);
        Assert.Equal(150_000 * 0.32, s.TotalPowderKg, 3);
        Assert.Equal(0, s.UnattributedUnits);
    }

    // ══════════════════════════════════════════════════════════════
    //  MR2【核心】没归属的单元：量单列，且账要平
    //     并进某个面 = 那个面的延米凭空多出来；直接丢掉 = 合计少一块而没人知道。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void MR2_未归属单元的量单列且账要平()
    {
        var plan = new[]
        {
            Plan("U1", 100_000, "DP1"),
            Plan("ORPHAN-A", 30_000, "DP1"),
            Plan("ORPHAN-B", 20_000, "DP1"),
        };
        var faces = new[] { Face("硬岩面", PlanMaterialCatalog.Rock) };
        var map = new Dictionary<string, string> { ["U1"] = "硬岩面" };   // 两个孤儿不在归属里

        var s = MonthlyProcessRollup.Build(plan, faces, map, Y, M);
        Dump(s);

        Assert.Equal(2, s.UnattributedUnits);
        Assert.Equal(50_000, s.UnattributedInSituM3, 3);
        Assert.Equal(100_000, s.TotalInSituM3, 3);                  // 面上只有归属上的那部分

        // ★ 账要平：逐面 + 未归属 = 排产总量。没有这一条，漏一块也没人知道。
        Assert.Equal(150_000, s.AccountedInSituM3, 3);
        Assert.DoesNotContain(s.Notes, n => n.Contains("账没对上"));

        // 孤儿的延米没有被算进任何一个面
        Assert.Equal(100_000 * 16.5 / 840.0, s.TotalDrillMeters, 3);
        Assert.Contains(s.Notes, n => n.Contains("没归属到作业面"));
        Assert.Contains("未归属", s.ToCsv());
    }

    // ══════════════════════════════════════════════════════════════
    //  MR3【核心】「免爆」与「算不出」的延米都是 0，但必须分得开
    //     混成一个 0 的话，缺的那一块穿孔需求就永远问不出来了。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void MR3_免爆与算不出必须分得开()
    {
        var plan = new[]
        {
            Plan("SOIL", 40_000, "DP1"),
            Plan("BAD", 60_000, "DP1"),
        };
        var faces = new[]
        {
            Face("表土面", PlanMaterialCatalog.Topsoil),                  // 免爆
            Face("参数缺的硬岩面", PlanMaterialCatalog.Rock, bench: 0),   // 要穿爆，但台阶高是 0
        };
        var map = new Dictionary<string, string> { ["SOIL"] = "表土面", ["BAD"] = "参数缺的硬岩面" };

        var s = MonthlyProcessRollup.Build(plan, faces, map, Y, M);
        Dump(s);

        var soil = s.Faces.Single(f => f.FaceName == "表土面");
        var bad = s.Faces.Single(f => f.FaceName == "参数缺的硬岩面");

        // 两者的延米都是 0 ——
        Assert.Equal(0, soil.DrillMeters, 6);
        Assert.Equal(0, bad.DrillMeters, 6);
        // —— 但状态完全不同，报表上分得开
        Assert.Equal("免爆", soil.StateText);
        Assert.Equal("◆ 算不出", bad.StateText);
        Assert.False(soil.NeedsBlast);
        Assert.True(bad.NeedsBlast);
        Assert.False(bad.Computable);
        Assert.NotEmpty(bad.Note);

        // 缺口单列：算不出的那 6 万方不在合计里，且明说是缺口不是 0
        Assert.Equal(60_000, s.NotComputableInSituM3, 3);
        Assert.Equal(40_000, s.NoBlastInSituM3, 3);
        Assert.Equal(0, s.TotalDrillMeters, 6);
        Assert.Contains(s.Notes, n => n.Contains("要穿爆但参数算不出来") && n.Contains("没有计入合计"));
        Assert.Contains("这是缺口，不是 0", s.Summary());
    }

    // ══════════════════════════════════════════════════════════════
    //  MR4 台阶高来源可溯：工艺里填的 / 库表兜底 / 没有
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void MR4_台阶高来源可溯并能用库表兜底()
    {
        var plan = new[] { Plan("U1", 100_000, "DP1"), Plan("U2", 100_000, "DP1") };
        var faces = new[]
        {
            Face("工艺填了的面", PlanMaterialCatalog.Rock, bench: 15),
            Face("靠台账的面", PlanMaterialCatalog.Rock, bench: 0, code: "WF-A"),
        };
        var map = new Dictionary<string, string> { ["U1"] = "工艺填了的面", ["U2"] = "靠台账的面" };

        // 库表兜底：WF-A → 12m
        var s = MonthlyProcessRollup.Build(plan, faces, map, Y, M,
                                           benchHeightOf: code => code == "WF-A" ? 12.0 : 0.0);
        Dump(s);

        var a = s.Faces.Single(f => f.FaceName == "工艺填了的面");
        var b = s.Faces.Single(f => f.FaceName == "靠台账的面");

        Assert.Contains("工艺", a.BenchSource);
        Assert.Contains("台账", b.BenchSource);
        Assert.True(b.Computable);                                   // 兜底之后算得出来了

        // 台阶高不同 ⇒ 单孔控制方量不同 ⇒ 延米不同（12m 的面孔浅但孔多）
        Assert.Equal(100_000 * 16.5 / (7 * 8 * 15.0), a.DrillMeters, 3);
        Assert.Equal(100_000 * 13.5 / (7 * 8 * 12.0), b.DrillMeters, 3);
        Assert.NotEqual(a.DrillMeters, b.DrillMeters, 3);
    }

    // ══════════════════════════════════════════════════════════════
    //  MR5 没量的面不出行（出了就是一排 0，读表的人得逐行确认它是不是真没干）
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void MR5_本月没量的面不出行()
    {
        var plan = new[] { Plan("U1", 100_000, "DP1") };
        var faces = new[]
        {
            Face("在采的面", PlanMaterialCatalog.Rock),
            Face("本月没排的面", PlanMaterialCatalog.Rock),
        };
        var map = new Dictionary<string, string> { ["U1"] = "在采的面" };

        var s = MonthlyProcessRollup.Build(plan, faces, map, Y, M);
        Dump(s);

        Assert.Single(s.Faces);
        Assert.Equal("在采的面", s.Faces[0].FaceName);
    }

    // ══════════════════════════════════════════════════════════════
    //  MR6 空输入不崩，也不假装有量
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void MR6_空输入不崩()
    {
        var s = MonthlyProcessRollup.Build(Array.Empty<UnitAssignment>(), Array.Empty<WorkingFace>(), null, Y, M);
        Assert.Empty(s.Faces);
        Assert.Equal(0, s.TotalInSituM3, 6);
        Assert.Contains(s.Notes, n => n.Contains("没有排产结果"));

        var s2 = MonthlyProcessRollup.Build(null, null, null, Y, M);
        Assert.Equal(0, s2.TotalDrillMeters, 6);
    }
}
