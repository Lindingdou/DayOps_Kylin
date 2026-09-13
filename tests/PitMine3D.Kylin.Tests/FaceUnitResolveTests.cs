// 忠实移植自原 PitMine3D Tests/Tests.PitMineApp/FaceUnitResolveTests.cs（逐行对应；仅命名空间适配）
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
using WorkingFace = PitMine3D.Kylin.Cad.Plan.WorkingFace;
using DumpStripStore = PitMine3D.Kylin.UnitLedger.DumpStripStore;
namespace PitMine3D.Kylin.Tests;

/// <summary>
/// FA 组 · 采掘单元 → 作业面 的归属（<see cref="FaceUnitResolver"/>）。
///
/// <para>这一组要抓的是<b>"归属被悄悄编出来了"</b>：
/// 归属这件事有个天然的诱惑 —— 总得给每个单元配一个面，配不上就挑个最近的。
/// 那样做出来匹配率永远 100%，界面上一片绿，而"这个面的铲跑到别的面去了"
/// 在任何一张报表上都看不出来。</para>
///
/// <para>所以这里每一条都在判<b>"它有没有拒绝配"</b>，不是判"它配上了多少"。</para>
/// </summary>
public sealed class FaceUnitResolveTests
{
    private readonly ITestOutputHelper _out;
    public FaceUnitResolveTests(ITestOutputHelper o) => _out = o;

    private static MineUnit Unit(string id, double zLo, double zHi, UnitKind k = UnitKind.Rock)
        => new() { UnitId = id, Kind = k, ZLo = zLo, ZHi = zHi, InSituM3 = 10000 };

    private static WorkingFace Face(string name, double bench, string material = PlanMaterialCatalog.Rock,
                                    string loaderModel = "")
        => new() { Name = name, BenchElevationM = bench, MaterialCode = material, LoaderModel = loaderModel };

    private void Dump(FaceUnitResolution r)
    {
        _out.WriteLine(r.Summary());
        foreach (var n in r.Notes) _out.WriteLine("  " + n);
    }

    // ══════════════════════════════════════════════════════════════
    //  FA-1 正常算例：标高 + 物料都对得上就配上
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void FA1_标高与物料都命中时配上()
    {
        var units = new[] { Unit("L1-B1-P1", 1180, 1195), Unit("L2-B1-P1", 1165, 1180) };
        var faces = new[] { Face("一采区岩", 1195), Face("二采区岩", 1180) };

        var r = FaceUnitResolver.Resolve(units, faces);
        Dump(r);

        Assert.Equal(2, r.MatchedCount);
        Assert.Equal(1.0, r.MatchRate, 6);
        Assert.Equal("一采区岩", r.UnitToFace["L1-B1-P1"]);
        Assert.Equal("二采区岩", r.UnitToFace["L2-B1-P1"]);
        Assert.Empty(r.Unmatched);
        Assert.Empty(r.Ambiguous);
    }

    // ══════════════════════════════════════════════════════════════
    //  FA-2 物料不相容：煤面不配岩单元（哪怕标高正好）
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void FA2_煤面不会配到岩单元上()
    {
        var units = new[] { Unit("ROCK", 1180, 1195), Unit("COAL", 1180, 1195, UnitKind.Coal) };
        var faces = new[]
        {
            Face("采煤面", 1195, PlanMaterialCatalog.Coal),
            Face("剥离面", 1195, PlanMaterialCatalog.Rock),
        };

        var r = FaceUnitResolver.Resolve(units, faces);
        Dump(r);

        Assert.Equal("剥离面", r.UnitToFace["ROCK"]);
        Assert.Equal("采煤面", r.UnitToFace["COAL"]);
        Assert.Empty(r.Ambiguous);          // 标高一样，但物料把它们分开了 —— 不该判歧义
    }

    // ══════════════════════════════════════════════════════════════
    //  FA-5【核心】两个面并列 ⇒ 判歧义、【不配】，不许挑一个
    //     这是整组最重要的一条：随便挑一个的话匹配率 100%、界面全绿，而一半是错的。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void FA5_两个面并列命中时拒绝配而不是随便挑一个()
    {
        var units = new[] { Unit("U1", 1180, 1195) };
        var faces = new[] { Face("东帮", 1195), Face("西帮", 1195) };   // 同标高同物料

        var r = FaceUnitResolver.Resolve(units, faces);
        Dump(r);

        Assert.Equal(0, r.MatchedCount);
        Assert.False(r.UnitToFace.ContainsKey("U1"));   // 没有给它安一个面
        Assert.Single(r.Ambiguous);
        Assert.Contains("东帮", r.Ambiguous[0]);
        Assert.Contains("西帮", r.Ambiguous[0]);
        Assert.Contains(r.Notes, n => n.Contains("引擎不替你挑"));
    }

    // ══════════════════════════════════════════════════════════════
    //  FA-6【核心】一个面都没命中 ⇒ 不配，且说得出是哪一条挡住的
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void FA6_没命中就不配并说明原因()
    {
        var units = new[]
        {
            Unit("远标高", 900, 915),                       // 物料相容，但标高差很远
            Unit("无同料面", 1180, 1195, UnitKind.Coal),     // 一个煤面都没有
        };
        var faces = new[] { Face("岩面", 1195, PlanMaterialCatalog.Rock) };

        var r = FaceUnitResolver.Resolve(units, faces);
        Dump(r);

        Assert.Equal(0, r.MatchedCount);
        Assert.Equal(2, r.Unmatched.Count);
        // 两种"没配上"的原因必须能分开 —— 都写"没配上"帮不了任何人
        Assert.Contains(r.Unmatched, s => s.Contains("远标高") && s.Contains("没有台阶标高落在"));
        Assert.Contains(r.Unmatched, s => s.Contains("无同料面") && s.Contains("物料相容"));
    }

    // ══════════════════════════════════════════════════════════════
    //  FA-1b 手工覆盖优先；指定了一个不存在的面 ⇒ 不退回自动匹配
    //     悄悄退回的话，人写错的名字会被一个"看起来对"的结果盖住。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void FA1b_手工指定优先且写错面名不退回自动匹配()
    {
        var units = new[] { Unit("U1", 1180, 1195), Unit("U2", 1180, 1195) };
        var faces = new[] { Face("自动会配上的面", 1195), Face("人指定的面", 1195) };

        var manual = new Dictionary<string, string>
        {
            ["U1"] = "人指定的面",
            ["U2"] = "这个面不存在",
        };

        var r = FaceUnitResolver.Resolve(units, faces, manual);
        Dump(r);

        Assert.Equal("人指定的面", r.UnitToFace["U1"]);      // 手工优先，且没被判歧义
        Assert.Equal(1, r.ManualCount);

        Assert.False(r.UnitToFace.ContainsKey("U2"));        // 写错的名字不给兜底
        Assert.Contains(r.Unmatched, s => s.Contains("U2") && s.Contains("不存在"));
        Assert.Contains(r.Notes, n => n.Contains("没有</b>退回自动匹配") || n.Contains("退回自动匹配"));
    }

    // ══════════════════════════════════════════════════════════════
    //  FA-7 免爆清单：面的物料不需爆破 ⇒ 该面的单元进免爆清单
    //     这条直接喂 EquipmentAssignInput.NoBlastUnitIds，错了会整月高估钻机需求。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void FA7_表土面的单元进免爆清单硬岩面的不进()
    {
        var units = new[] { Unit("SOIL", 1195, 1210), Unit("ROCK", 1180, 1195) };
        var faces = new[]
        {
            Face("表土剥离面", 1210, PlanMaterialCatalog.Topsoil),
            Face("硬岩剥离面", 1195, PlanMaterialCatalog.Rock),
        };

        var r = FaceUnitResolver.Resolve(units, faces);
        Dump(r);

        Assert.Equal(2, r.MatchedCount);
        Assert.Contains("SOIL", r.NoBlastUnits);
        Assert.DoesNotContain("ROCK", r.NoBlastUnits);
    }

    // ══════════════════════════════════════════════════════════════
    //  FA-8 配了型号却一个单元都没配上的面，必须报出来
    //     这是"界面上钉得满满当当、排产一条都不认"的那个状态。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void FA8_钉了型号却没配上单元的面要报出来()
    {
        var units = new[] { Unit("U1", 1180, 1195) };
        var faces = new[]
        {
            Face("有单元的面", 1195, PlanMaterialCatalog.Rock, loaderModel: "WK-10"),
            Face("没单元的面", 800, PlanMaterialCatalog.Rock, loaderModel: "WK-12"),
        };

        var r = FaceUnitResolver.Resolve(units, faces);
        Dump(r);

        Assert.Equal(1, r.MatchedCount);
        Assert.Equal(0, r.PerFace["没单元的面"]);
        Assert.Contains(r.Notes, n => n.Contains("配了设备型号的") && n.Contains("等于没配"));
    }

    // ══════════════════════════════════════════════════════════════
    //  FA-9 边界：没有面 / 没有单元时不许崩，也不许假装配上了
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void FA9_空输入不崩也不假装配上()
    {
        var noFace = FaceUnitResolver.Resolve(new[] { Unit("U1", 1180, 1195) }, Array.Empty<WorkingFace>());
        Dump(noFace);
        Assert.Equal(0, noFace.MatchedCount);
        Assert.Single(noFace.Unmatched);
        Assert.Contains(noFace.Notes, n => n.Contains("一个作业面都没有"));

        var noUnit = FaceUnitResolver.Resolve(Array.Empty<MineUnit>(), new[] { Face("面", 1195) });
        Assert.Equal(0, noUnit.UnitCount);
        Assert.Equal(0, noUnit.MatchRate);
        Assert.Empty(noUnit.UnitToFace);

        var bothNull = FaceUnitResolver.Resolve(null, null);
        Assert.Equal(0, bothNull.MatchedCount);
    }

    // ══════════════════════════════════════════════════════════════
    //  FA-11 待定单元号是【单独给】的，不是从展示文案里抠的
    //     归属覆盖窗口按 Id 列待定项。若靠正则从"UnitId（原因）"里抠，
    //     文案一改就静默抠空 —— 窗口里一行不显示，看起来像"这一轮全配上了"。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void FA11_待定单元号与展示文案同序等长()
    {
        var units = new[]
        {
            Unit("AMBIG", 1180, 1195),                  // 两个面并列 ⇒ 歧义
            Unit("MISS", 700, 715),                     // 标高差太远 ⇒ 没命中
            Unit("OK", 1180, 1195),
        };
        var faces = new[] { Face("东帮", 1195), Face("西帮", 1195) };

        var r = FaceUnitResolver.Resolve(units, faces);
        Dump(r);

        // Id 表与文案表同序等长
        Assert.Equal(r.Ambiguous.Count, r.AmbiguousIds.Count);
        Assert.Equal(r.Unmatched.Count, r.UnmatchedIds.Count);

        // 每个 Id 都是干净的单元号（不带括号原因）
        Assert.All(r.PendingIds, id => Assert.DoesNotContain("（", id));
        Assert.Contains("AMBIG", r.AmbiguousIds);
        Assert.Contains("MISS", r.UnmatchedIds);
        Assert.Contains("OK", r.AmbiguousIds);          // OK 也是同标高，同样歧义

        // 文案里确实带着对应的单元号（两张表指的是同一批单元）
        for (int i = 0; i < r.AmbiguousIds.Count; i++)
            Assert.StartsWith(r.AmbiguousIds[i], r.Ambiguous[i], StringComparison.Ordinal);
        for (int i = 0; i < r.UnmatchedIds.Count; i++)
            Assert.StartsWith(r.UnmatchedIds[i], r.Unmatched[i], StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════
    //  FA-12【闭环】人工覆盖真的把歧义单元定下来了
    //     FA5 的提示写着"要定就手工指定" —— 这一条验证那句话兑现了。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void FA12_人工覆盖把歧义单元定下来()
    {
        var units = new[] { Unit("U1", 1180, 1195) };
        var faces = new[] { Face("东帮", 1195), Face("西帮", 1195) };

        // 覆盖之前：歧义、不配
        var before = FaceUnitResolver.Resolve(units, faces);
        Assert.Equal(0, before.MatchedCount);
        Assert.Single(before.AmbiguousIds);

        // 人按 FA5 的提示定了一个 —— 这正是归属覆盖窗口写回的那张表
        var manual = new Dictionary<string, string> { ["U1"] = "西帮" };
        var after = FaceUnitResolver.Resolve(units, faces, manual);
        Dump(after);

        Assert.Equal(1, after.MatchedCount);
        Assert.Equal("西帮", after.UnitToFace["U1"]);
        Assert.Equal(1, after.ManualCount);
        Assert.Empty(after.AmbiguousIds);               // 不再待定
        Assert.Empty(after.PendingIds);
    }

    // ══════════════════════════════════════════════════════════════
    //  FA-10 匹配率是【报出来】的，不是凑到 100% 的
    //     一半单元配不上时，MatchRate 必须真的是 0.5 —— 而不是把它们塞进最近的面。
    // ══════════════════════════════════════════════════════════════
    [Fact]
    public void FA10_匹配率如实反映配不上的那一半()
    {
        var units = new[]
        {
            Unit("配得上1", 1180, 1195), Unit("配得上2", 1180, 1195),
            Unit("配不上1", 700, 715),   Unit("配不上2", 600, 615),
        };
        var faces = new[] { Face("唯一的面", 1195) };

        var r = FaceUnitResolver.Resolve(units, faces);
        Dump(r);

        Assert.Equal(4, r.UnitCount);
        Assert.Equal(2, r.MatchedCount);
        Assert.Equal(0.5, r.MatchRate, 6);
        Assert.Equal(2, r.Unmatched.Count);
        Assert.Contains(r.Notes, n => n.Contains("不受任何面级约束"));
    }
}
