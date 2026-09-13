// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/FaceDeriveEndToEndTests.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.IO;
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
using WorkingFace = PitMine3D.Kylin.Cad.Plan.WorkingFace;
using DumpStripStore = PitMine3D.Kylin.UnitLedger.DumpStripStore;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 派生作业面 → 归属 的端到端判据（FE 组）。
///
/// <para><b>为什么必须端到端</b>：FB 组证明的是"派生器算得对"，
/// 但真正要证的是<b>派生出来的面能不能把归属率打上去</b> ——
/// 派生一堆归属仍然对不上的面，每条单元判据都绿，而工序量还是 0。</para>
///
/// <para><b>算例用真实量级</b>（1120~1320m），不用 0~100 的假标高：
/// 出事的那两个样例面正是因为标高量级不对（60m / 48m）。</para>
/// </summary>
public class FaceDeriveEndToEndTests
{
    private readonly ITestOutputHelper _out;
    public FaceDeriveEndToEndTests(ITestOutputHelper o) => _out = o;

    private static MineUnit U(string id, bool coal, double zLo, double zHi)
        => new() { UnitId = id, Kind = coal ? UnitKind.Coal : UnitKind.Rock, ZLo = zLo, ZHi = zHi };

    private static FaceSeedUnit Seed(MineUnit u, double qty)
        => new()
        {
            UnitId = u.UnitId, IsCoal = u.IsCoal, ZLo = u.ZLo, ZHi = u.ZHi,
            CoalT = u.IsCoal ? qty : 0, RockM3 = u.IsCoal ? 0 : qty,
            BenchName = u.IsCoal ? "9煤" : $"{u.ZLo:0}台阶",
        };

    /// <summary>真实量级的一个月：10 级岩台阶 × 8 幅 + 3 级煤台阶 × 5 幅。</summary>
    private static List<MineUnit> RealisticMonth()
    {
        var list = new List<MineUnit>();
        int i = 0;
        for (double z = 1200; z < 1320; z += 12)
            for (int k = 0; k < 8; k++) list.Add(U($"R{i++}", false, z, z + 12));
        for (double z = 1140; z < 1176; z += 12)
            for (int k = 0; k < 5; k++) list.Add(U($"C{i++}", true, z, z + 12));
        return list;
    }

    // ══════════════════════════════════════════════════════════════
    //  FE1 样例面必须先证明是真的配不上
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// FE1 <b>缺省的两个样例面在真实标高下归属率是 0</b>。
    /// <para>这条先跑：不先证明"旧的确实全配不上"，FE2 的"新的全配上"就说明不了是派生的功劳。</para>
    /// </summary>
    [Fact]
    public void FE1_样例面在真实标高下归属率为零()
    {
        var units = RealisticMonth();
        var sample = new List<WorkingFace>
        {
            new() { Name = "主采面·东", BenchElevationM = 60, MaterialCode = PlanMaterialCatalog.Coal },
            new() { Name = "辅采面·南", BenchElevationM = 48, MaterialCode = PlanMaterialCatalog.Coal },
        };

        var r = FaceUnitResolver.Resolve(units, sample);
        _out.WriteLine($"样例面：{r.MatchedCount}/{r.UnitCount} 配上");

        Assert.Equal(0, r.MatchedCount);
        Assert.Equal(units.Count, r.UnitCount);
    }

    // ══════════════════════════════════════════════════════════════
    //  FE2 派生之后必须全配上
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// FE2 <b>派生的面把归属率打到 100%</b>。
    /// <para>这是整件事的目的。任何一个单元没配上，它的工序量就算不了。</para>
    /// </summary>
    [Fact]
    public void FE2_派生之后归属率百分之百()
    {
        var units = RealisticMonth();
        var built = FaceAutoBuilder.Build(units.Select(u => Seed(u, 5e4)).ToList());
        Assert.True(built.Ok);

        var r = FaceUnitResolver.Resolve(units, built.Faces.Select(f => f.Face));
        _out.WriteLine($"派生 {built.Faces.Count} 个面：{r.MatchedCount}/{r.UnitCount} 配上");
        foreach (var s in r.Unmatched.Take(5)) _out.WriteLine("  未配：" + s);

        Assert.Equal(units.Count, r.MatchedCount);
        Assert.Empty(r.Unmatched);
    }

    /// <summary>
    /// FE2b <b>煤单元不许配到岩面上</b>（FA2 物料相容）——
    /// 配串了的话工艺参数用错一套，穿爆量整月都是错的，而归属率是漂亮的 100%。
    /// </summary>
    [Fact]
    public void FE2b_煤不配到岩面上()
    {
        var units = RealisticMonth();
        var built = FaceAutoBuilder.Build(units.Select(u => Seed(u, 5e4)).ToList());
        var faceByName = built.Faces.ToDictionary(f => f.Face.Name, f => f.Face);

        var r = FaceUnitResolver.Resolve(units, built.Faces.Select(f => f.Face));
        foreach (var u in units)
        {
            string faceName = r.UnitToFace[u.UnitId];
            bool faceIsCoal = PlanMaterialCatalog.Resolve(faceByName[faceName].MaterialCode).IsOre;
            Assert.Equal(u.IsCoal, faceIsCoal);
        }
    }

    /// <summary>
    /// FE2c <b>派生面之间不许出现"标高一样近"的歧义</b>（FA5 判歧义就不配）。
    /// <para>同物料两个面标高相同时，FA5 一个都不配 —— 归属率会掉回去，而派生器自己看着成功。</para>
    /// </summary>
    [Fact]
    public void FE2c_派生面之间没有标高歧义()
    {
        var built = FaceAutoBuilder.Build(RealisticMonth().Select(u => Seed(u, 5e4)).ToList());

        foreach (var g in built.Faces.GroupBy(f => f.Face.MaterialCode))
        {
            var zs = g.Select(f => Math.Round(f.Face.BenchElevationM, 3)).ToList();
            Assert.Equal(zs.Count, zs.Distinct().Count());
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  FE3 追加 vs 替换
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// FE3 <b>把派生面【追加】到样例面上比只有样例面更糟</b> —— 这条钉的是界面里"替换不追加"那个决定。
    /// <para>追加之后煤面有两个候选（一个 1152m 对得上、一个 60m 差一千米）。
    /// 60m 那个过不了 FA3 所以不进候选，看着没事 —— 但只要有人把样例面的标高改成一个
    /// <b>恰好也落在区间内</b>的值，FA5 就判歧义、一个都不配。替换掉才是把这条路彻底堵死。</para>
    /// </summary>
    [Fact]
    public void FE3_同标高同物料两个面时判歧义不配()
    {
        var units = new List<MineUnit> { U("C1", true, 1140, 1152) };
        var faces = new List<WorkingFace>
        {
            new() { Name = "派生·煤1152m", BenchElevationM = 1152, MaterialCode = PlanMaterialCatalog.Coal },
            new() { Name = "有人手填的面", BenchElevationM = 1152, MaterialCode = PlanMaterialCatalog.Coal },
        };

        var r = FaceUnitResolver.Resolve(units, faces);
        Assert.Equal(0, r.MatchedCount);                       // FA5：并列就不配
        // 并列命中进的是 Ambiguous，**不是** Unmatched —— 两者刻意分开：
        // 「没有面对得上」和「有两个面都对得上、引擎不替你挑」要人做的事完全不同。
        Assert.Contains(r.Ambiguous, s => s.Contains("C1"));
        Assert.Empty(r.Unmatched);
    }

    // ══════════════════════════════════════════════════════════════
    //  FE5 工序量：算不出的 0 与不用穿的 0
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// FE5 <b>派生面的工序量必须算得出来</b>（Computable = true）。
    /// <para>`FaceProcessChain` 里孔网（7×8m）和炸药单耗（0.32kg/m³）都有缺省，
    /// <b>只有台阶高缺省是 0</b>；`CheckComputable` 一看它是 0 就判"算不出"，
    /// 于是整个面的穿孔延米/孔数/炸药量全是 0 ——
    /// <b>而那个 0 的意思是「算不出」，不是「不用穿」，两者在报表上长得一模一样。</b></para>
    /// <para>台阶高不用猜：它就是 ZHi − ZLo。这条钉住派生器真的把它填了。</para>
    /// </summary>
    [Fact]
    public void FE5_派生面的穿爆量算得出来()
    {
        var built = FaceAutoBuilder.Build(RealisticMonth().Select(u => Seed(u, 5e4)).ToList());

        foreach (var df in built.Faces)
        {
            var p = df.Face.Process;
            Assert.NotNull(p);
            Assert.True(p!.BenchHeightM > 0, $"{df.Face.Name} 的台阶高是 0 —— 穿爆量会被判成算不出");
            Assert.Empty(p.CheckComputable(df.Face.Name, df.Face.MaterialCode));
        }
    }

    /// <summary>
    /// FE5b 台阶高取<b>中位数</b>而不是均值。
    /// <para>一组里混进一个跨两级的厚单元时，均值会被拉高（12m 的台阶算成 14m），
    /// 单孔控制方量随之偏大、穿孔延米偏小。中位数不受这一个单元影响。</para>
    /// </summary>
    [Fact]
    public void FE5b_台阶高取中位数不取均值()
    {
        // 顶板都在同一档（1212 附近），厚度 12/12/12/12/60 —— 均值 21.6，中位数 12
        var seeds = new List<FaceSeedUnit>
        {
            new() { UnitId = "A", ZLo = 1200, ZHi = 1212, RockM3 = 1e4 },
            new() { UnitId = "B", ZLo = 1200, ZHi = 1212, RockM3 = 1e4 },
            new() { UnitId = "C", ZLo = 1200, ZHi = 1212, RockM3 = 1e4 },
            new() { UnitId = "D", ZLo = 1200, ZHi = 1212, RockM3 = 1e4 },
            new() { UnitId = "E", ZLo = 1152, ZHi = 1212, RockM3 = 1e4 },   // 厚 60m 的异类
        };

        var f = FaceAutoBuilder.Build(seeds).Faces.Single();
        Assert.Equal(12, f.Face.Process.BenchHeightM, 1);        // 均值会是 21.6
    }

    /// <summary>
    /// FE5c 结论里必须说清<b>孔网与单耗是缺省值不是实测</b>。
    /// <para>穿爆量算得出来之后最危险的一刻：那几个数看着完全正常，
    /// 而它们是一般值，不是这个矿的。</para>
    /// </summary>
    [Fact]
    public void FE5c_要说清孔网与单耗是缺省值()
    {
        var r = FaceAutoBuilder.Build(new[]
        { new FaceSeedUnit { UnitId = "R1", ZLo = 1200, ZHi = 1212, RockM3 = 1e4 } });

        Assert.Contains(r.Notes, n => n.Contains("缺省值") && n.Contains("不是本矿实测"));
        Assert.Contains("台阶高", r.Faces.Single().Face.Note);
    }

    // ══════════════════════════════════════════════════════════════
    //  FE4 对着真台账跑一遍（有就跑，没有就跳过）
    // ══════════════════════════════════════════════════════════════

    private const string RealRoot =
        @"C:\Users\0doudou\Desktop\DayOps\PitMine3D\bin\Debug\Data\采掘单元台账";

    /// <summary>
    /// FE4 真台账上的归属率 —— <b>诊断</b>。
    /// <para>台账不在就跳过（别的机器上跑不炸）。跑得到时把数字打出来：
    /// 这是唯一能说明"这个矿的这一期到底配上多少"的地方。</para>
    /// </summary>
    [Fact]
    public void FE4_真台账上的归属率()
    {
        if (!Directory.Exists(RealRoot)) { _out.WriteLine("SKIP: 真台账目录不在"); return; }

        string period = FaceSeedLoader.LatestPeriod(out string note, RealRoot);
        _out.WriteLine($"期次 = 「{period}」　{note}");
        if (period.Length == 0) { _out.WriteLine("SKIP: 盘上一期都没有"); return; }

        var seed = FaceSeedLoader.Load(period, RealRoot);
        _out.WriteLine(seed.Label);
        if (!seed.Ok) { _out.WriteLine("SKIP: 这一期没有采场单元"); return; }

        var built = FaceAutoBuilder.Build(seed.Units);
        _out.WriteLine(built.Headline);
        foreach (var f in built.Faces)
            _out.WriteLine($"  {f.Face.Name}　标高 {f.Face.BenchElevationM:0.#}m　"
                         + $"份额 {f.Face.SharePct:0.#}%　{f.UnitIds.Count} 个单元");

        var units = seed.Units.Select(s => U(s.UnitId, s.IsCoal, s.ZLo, s.ZHi)).ToList();
        var r = FaceUnitResolver.Resolve(units, built.Faces.Select(f => f.Face));
        _out.WriteLine($"→ 归属：{r.MatchedCount}/{r.UnitCount}");
        foreach (var s in r.Unmatched.Take(8)) _out.WriteLine("  未配：" + s);

        Assert.True(r.MatchedCount == r.UnitCount,
            $"真台账上仍有 {r.UnitCount - r.MatchedCount} 个单元配不上 —— 派生的面没覆盖全");
    }
}
