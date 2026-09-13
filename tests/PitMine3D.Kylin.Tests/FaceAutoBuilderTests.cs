// 忠实移植自原 PitMine3D Tests/Tests.MineAssLib/FaceAutoBuilderTests.cs（逐行对应；仅命名空间适配）+ FE5 组（FaceDeriveEndToEndTests 中不依赖 FaceUnitResolver 的三条）+ 设备工艺树/型号目录判据
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Plan;
using Xunit;
using WorkingFace = PitMine3D.Kylin.Cad.Plan.WorkingFace;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 「按本期单元派生作业面」的判据（FB 组）。
///
/// <para><b>起因</b>：`ShortTermPlan.CreateDefaultFaces()` 写死了两个面 ——
/// 主采面·东 @ 台阶标高 <b>60 m</b>、辅采面·南 @ <b>48 m</b>，
/// 而这个矿真实的采掘单元在 <b>1120–1320 m</b>。归属口径 FA3 要求
/// 「面的台阶标高落在单元 [ZLo−2, ZHi+2] 之内」—— 差着一千多米，<b>永远不可能成立</b>。</para>
///
/// <para>后果不是"归属率低"：933 个单元全配不上 ⇒ 没有工艺参数 ⇒
/// 月度工序量 采出 0 · 排弃 0 · 穿孔 0 延米 · 炸药 0 t · 爆破 0 次 ⇒ 设备指派失败。
/// <b>而每一步都不报错</b>，报告里写的是「没归属到作业面」，读起来像台账没填全。</para>
///
/// <para><b>这一组最要紧的不是"派生得出来"，是"派生的东西不冒充录入的"</b>：
/// 一个带着缺省 90° 推进方位、2.6km 运距的派生面看着完全正常，
/// 而那两个数会一路进运输功、进班表、进达成度。FB4/FB5 专钉这条。</para>
/// </summary>
public class FaceAutoBuilderTests
{
    private static FaceSeedUnit Coal(string id, double zLo, double zHi, double t, string bench = "9煤")
        => new() { UnitId = id, IsCoal = true, ZLo = zLo, ZHi = zHi, CoalT = t, BenchName = bench };

    private static FaceSeedUnit Rock(string id, double zLo, double zHi, double m3, string bench = "1200台阶")
        => new() { UnitId = id, IsCoal = false, ZLo = zLo, ZHi = zHi, RockM3 = m3, BenchName = bench };

    // ══════════════════════════════════════════════════════════════
    //  FB1 派生出来的面必须真能被 FA3 命中
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// FB1 <b>派生面的标高落在它自己那组单元的 [ZLo−2, ZHi+2] 之内</b>。
    /// <para>这是整件事的目的 —— 派生一堆归属仍然对不上的面，等于什么都没做。
    /// 算例用真实量级（1200m 台阶），<b>不用 0~100 的假标高</b>：
    /// 出事的那两个样例面正是因为标高量级不对。</para>
    /// </summary>
    [Fact]
    public void FB1_派生面的标高能被FA3命中()
    {
        var units = new[]
        {
            Rock("R1", 1200, 1212, 5e4),
            Rock("R2", 1200, 1212, 6e4),
            Coal("C1", 1145, 1163, 8e4),
        };

        var r = FaceAutoBuilder.Build(units);
        Assert.True(r.Ok);

        const double tol = 2.0;                     // FaceUnitResolver.DefaultElevToleranceM
        foreach (var df in r.Faces)
            foreach (var id in df.UnitIds)
            {
                var u = units.Single(x => x.UnitId == id);
                Assert.InRange(df.Face.BenchElevationM, u.ZLo - tol, u.ZHi + tol);
            }
    }

    /// <summary>
    /// FB1b 每个单元都进了<b>恰好一个</b>面 —— 不重不漏。
    /// <para>漏了那个单元的量就没人承担；重了它的量会被两个面各算一遍。</para>
    /// </summary>
    [Fact]
    public void FB1b_每个单元恰好进一个面()
    {
        var units = new[] { Coal("C1", 1145, 1163, 1e4), Rock("R1", 1200, 1212, 2e4),
                            Rock("R2", 1296, 1308, 3e4) };
        var r = FaceAutoBuilder.Build(units);

        var placed = r.Faces.SelectMany(f => f.UnitIds).ToList();
        Assert.Equal(3, placed.Count);
        Assert.Equal(3, placed.Distinct().Count());
        Assert.Empty(r.Unplaced);
    }

    // ══════════════════════════════════════════════════════════════
    //  FB2 分档
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// FB2 同一级台阶上的分幅分带归成<b>一个</b>面（否则一期会派生出几十个一模一样的面）。
    /// </summary>
    [Fact]
    public void FB2_同一台阶的分幅归成一个面()
    {
        var units = Enumerable.Range(0, 8)
            .Select(i => Rock($"R{i}", 1200, 1212 + i * 0.4, 1e4)).ToArray();   // 顶板 1212.0~1214.8

        Assert.Single(FaceAutoBuilder.Build(units).Faces);
    }

    /// <summary>
    /// FB2b <b>两级台阶不许并到一个面</b>。
    /// <para>档宽必须明显小于台阶高：12m 台阶用 6m 档。并了的话两级的量摊到同一个标高上，
    /// 归属会把下一级的单元也配给上一级。</para>
    /// </summary>
    [Fact]
    public void FB2b_两级台阶不并到一个面()
    {
        var units = new[] { Rock("R1", 1200, 1212, 1e4), Rock("R2", 1212, 1224, 1e4) };
        Assert.Equal(2, FaceAutoBuilder.Build(units).Faces.Count);
    }

    /// <summary>FB2c 煤与岩即使标高一样也分成两个面（物料不同，工艺不同）。</summary>
    [Fact]
    public void FB2c_煤岩同标高也分开()
    {
        var units = new[] { Coal("C1", 1200, 1212, 1e4), Rock("R1", 1200, 1212, 1e4) };
        var r = FaceAutoBuilder.Build(units);

        Assert.Equal(2, r.Faces.Count);
        Assert.Contains(r.Faces, f => f.Face.MaterialCode == PlanMaterialCatalog.Coal);
        Assert.Contains(r.Faces, f => f.Face.MaterialCode == PlanMaterialCatalog.Rock);
    }

    // ══════════════════════════════════════════════════════════════
    //  FB3 份额与量
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// FB3 份额按<b>同物料内</b>的量占比算 —— 煤的万t 与岩的万m³ <b>不进同一个分母</b>。
    /// <para>并进去的话两种口径相加，得出的百分比不对应任何真实东西，而它加起来正好 100%。</para>
    /// </summary>
    [Fact]
    public void FB3_份额按同物料内占比不跨口径()
    {
        var units = new[]
        {
            Coal("C1", 1140, 1152, 75e4),      // 煤 75%
            Coal("C2", 1200, 1212, 25e4),      // 煤 25%
            Rock("R1", 1260, 1272, 40e4),      // 岩 40%
            Rock("R2", 1296, 1308, 60e4),      // 岩 60%
        };

        var r = FaceAutoBuilder.Build(units);
        var coal = r.Faces.Where(f => f.Face.MaterialCode == PlanMaterialCatalog.Coal).ToList();
        var rock = r.Faces.Where(f => f.Face.MaterialCode == PlanMaterialCatalog.Rock).ToList();

        Assert.Equal(100, coal.Sum(f => f.Face.SharePct), 1);   // 煤自己一本账
        Assert.Equal(100, rock.Sum(f => f.Face.SharePct), 1);   // 岩自己一本账
        Assert.Equal(75, coal.Single(f => f.UnitIds.Contains("C1")).Face.SharePct, 1);
        Assert.Equal(60, rock.Single(f => f.UnitIds.Contains("R2")).Face.SharePct, 1);
    }

    /// <summary>
    /// FB3b 岩面的「备采储量(万t)」留 0 —— <b>岩不是储量</b>，它的量按万m³ 记在 Note 里。
    /// <para>把 m³ 填进一个写着「万t」的列，下游求和时就成了吨。</para>
    /// </summary>
    [Fact]
    public void FB3b_岩面不往万t列里塞立方()
    {
        var f = FaceAutoBuilder.Build(new[] { Rock("R1", 1200, 1212, 50e4) }).Faces.Single();

        Assert.Equal(0, f.Face.AvailableReserveWanT);
        Assert.Equal(50, f.RockWanM3, 1);
        Assert.Contains("万m³", f.Face.Note);
    }

    // ══════════════════════════════════════════════════════════════
    //  FB4 不许冒充录入
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// FB4 <b>派生面必须自报是派生的</b>：Origin、名字、Note 三处都要看得出来。
    /// <para>只在一个内部枚举里标记不够 —— 「确定开采程序」那张表里显示的是名字和备注。</para>
    /// </summary>
    [Fact]
    public void FB4_派生面三处都自报来历()
    {
        var f = FaceAutoBuilder.Build(new[] { Rock("R1", 1200, 1212, 1e4) }).Faces.Single();

        Assert.Equal(FaceOrigin.DerivedFromPeriod, f.Origin);
        Assert.Contains("派生", f.Face.Name);
        Assert.Contains("没有人核过", f.Face.Note);
    }

    /// <summary>
    /// FB5 <b>推进方位不许给缺省值</b>。
    /// <para>`WorkingFace.AdvanceAzimuthDeg` 的字段缺省是 <b>90</b> —— 一个看着完全正常的数。
    /// 派生器必须把它置成 NaN（＝没有），否则整月的运输功都是按一个编出来的方位算的。
    /// 去向/运距同理：空就是空。</para>
    /// </summary>
    [Fact]
    public void FB5_推进方位与去向一律不猜()
    {
        var f = FaceAutoBuilder.Build(new[] { Coal("C1", 1140, 1152, 1e4) }).Faces.Single().Face;

        Assert.True(double.IsNaN(f.AdvanceAzimuthDeg), "推进方位给了缺省 90° —— 那是编的");
        Assert.Equal("", f.DestinationId);
        Assert.Equal("", f.DestinationName);
        Assert.Equal(0, f.HaulDistanceKm);
        Assert.Contains("猜了就是编", f.Note);
    }

    /// <summary>FB5b 结论文案要点明"没派生哪些"，不能只说派生成功。</summary>
    [Fact]
    public void FB5b_结论要点明没派生的那几项()
    {
        var r = FaceAutoBuilder.Build(new[] { Rock("R1", 1200, 1212, 1e4) });
        foreach (var word in new[] { "没有人核过", "推进方位", "去向", "运距" })
            Assert.Contains(word, r.Headline);
    }

    // ══════════════════════════════════════════════════════════════
    //  FB6 边界
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// FB6 <b>标高读不出来的单元要点名，不许静默丢</b>。
    /// <para>静默丢掉的话，派生看着成功、面也齐，而那几个单元的量整月没人承担。</para>
    /// </summary>
    [Fact]
    public void FB6_标高读不出来的单元要点名()
    {
        var bad = Rock("R-bad", double.NaN, double.NaN, 9e4);
        var r = FaceAutoBuilder.Build(new[] { Rock("R1", 1200, 1212, 1e4), bad });

        Assert.Equal("R-bad", Assert.Single(r.Unplaced));
        Assert.Contains("标高读不出来", r.Headline);
        Assert.DoesNotContain(r.Faces.SelectMany(f => f.UnitIds), id => id == "R-bad");
    }

    /// <summary>FB6b 空表 / null 不炸，且说清为什么派生不出来（空列表最容易被当成"成功但没面"）。</summary>
    [Fact]
    public void FB6b_空表不炸且说清原因()
    {
        foreach (var r in new[] { FaceAutoBuilder.Build(null),
                                  FaceAutoBuilder.Build(Array.Empty<FaceSeedUnit>()) })
        {
            Assert.False(r.Ok);
            Assert.Contains("一个采场单元都没有", r.Headline);
            Assert.Contains("一键排本月", r.Headline);      // 要给补法
        }
    }

    /// <summary>FB6c 档宽给 0 / 负数时拒绝，而不是除出 Infinity 悄悄归成一个面。</summary>
    [Fact]
    public void FB6c_档宽非正时拒绝()
    {
        var r = FaceAutoBuilder.Build(new[] { Rock("R1", 1200, 1212, 1e4) }, bandM: 0);
        Assert.False(r.Ok);
        Assert.Contains("必须为正", r.Headline);
    }

    /// <summary>
    /// FB7 真实量级下面数可控 —— 106 个单元横跨 1120~1320m 时派生出十几个面，不是上百个。
    /// <para>上百个面等于没归并；一两个面等于并过头了。</para>
    /// </summary>
    [Fact]
    public void FB7_真实量级下面数可控()
    {
        var units = new List<FaceSeedUnit>();
        int i = 0;
        for (double z = 1200; z < 1320; z += 12)                 // 10 级岩台阶
            for (int k = 0; k < 8; k++)                           // 每级 8 个分幅
                units.Add(Rock($"R{i++}", z, z + 12, 5e4));
        for (double z = 1140; z < 1176; z += 12)                  // 3 级煤台阶
            for (int k = 0; k < 5; k++)
                units.Add(Coal($"C{i++}", z, z + 12, 2e4));

        var r = FaceAutoBuilder.Build(units);
        Assert.Equal(13, r.Faces.Count);                          // 10 岩 + 3 煤
        Assert.Equal(units.Count, r.Faces.Sum(f => f.UnitIds.Count));
    }

    // ══════════════════════════════════════════════════════════════
    //  FE5 工序量：算不出的 0 与不用穿的 0（原 FaceDeriveEndToEndTests）
    // ══════════════════════════════════════════════════════════════

    /// <summary>FE5 派生面的工序量必须算得出来（台阶高 = ZHi − ZLo 已填，CheckComputable 为空）。</summary>
    [Fact]
    public void FE5_派生面的穿爆量算得出来()
    {
        var units = new List<FaceSeedUnit>();
        int i = 0;
        for (double z = 1200; z < 1320; z += 12) for (int k = 0; k < 8; k++) units.Add(Rock($"R{i++}", z, z + 12, 5e4));
        for (double z = 1140; z < 1176; z += 12) for (int k = 0; k < 5; k++) units.Add(Coal($"C{i++}", z, z + 12, 5e4));
        var built = FaceAutoBuilder.Build(units);

        foreach (var df in built.Faces)
        {
            var p = df.Face.Process;
            Assert.NotNull(p);
            Assert.True(p!.BenchHeightM > 0, $"{df.Face.Name} 的台阶高是 0 —— 穿爆量会被判成算不出");
            Assert.Empty(p.CheckComputable(df.Face.Name, df.Face.MaterialCode));
        }
    }

    /// <summary>FE5b 台阶高取中位数而不是均值（混进一个跨两级的厚单元时均值被拉高）。</summary>
    [Fact]
    public void FE5b_台阶高取中位数不取均值()
    {
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

    /// <summary>FE5c 结论里必须说清孔网与单耗是缺省值不是实测。</summary>
    [Fact]
    public void FE5c_要说清孔网与单耗是缺省值()
    {
        var r = FaceAutoBuilder.Build(new[] { new FaceSeedUnit { UnitId = "R1", ZLo = 1200, ZHi = 1212, RockM3 = 1e4 } });
        Assert.Contains(r.Notes, n => n.Contains("缺省值") && n.Contains("不是本矿实测"));
        Assert.Contains("台阶高", r.Faces.Single().Face.Note);
    }
}

/// <summary>「确定开采程序」设备工艺树 + 型号目录（库未就绪时全程降级）的判据。</summary>
public class FaceProcessTreeTests
{
    /// <summary>免爆面（煤）在树上根本没有穿孔/爆破分支；硬岩面五支齐全；爆破支不排设备。</summary>
    [Fact]
    public void 免爆面无穿爆分支_硬岩面五支()
    {
        var coal = new WorkingFace { Name = "煤面", MaterialCode = PlanMaterialCatalog.Coal };
        var rock = new WorkingFace { Name = "岩面", MaterialCode = PlanMaterialCatalog.Rock };
        var tree = new FaceProcessTree();
        tree.Rebuild(new[] { coal, rock }, null, null, () => { });

        Assert.Equal(2, tree.Roots.Count);
        var cn = tree.Roots[0]; var rn = tree.Roots[1];
        Assert.Equal(3, cn.Children.Count);
        Assert.DoesNotContain(cn.Children, c => c.Process is FaceProcess.Drill or FaceProcess.Blast);
        Assert.Equal(5, rn.Children.Count);
        Assert.Equal(new[] { FaceProcess.Drill, FaceProcess.Blast, FaceProcess.Load, FaceProcess.Haul, FaceProcess.Dump }, rn.Children.Select(c => c.Process).ToArray());
        Assert.False(rn.Children[1].HasEquipment);
        Assert.Empty(rn.Children[1].ModelOptions);
        Assert.True(rn.Children.Single(c => c.Process == FaceProcess.Haul).HasTruckCount);
    }

    /// <summary>树上就地改型号/配车直接写回 WorkingFace（没有第二份状态），并触发回调；改物料后 Rebuild 让分支跟着变。</summary>
    [Fact]
    public void 就地改型号写回作业面并回调()
    {
        var f = new WorkingFace { Name = "F", MaterialCode = PlanMaterialCatalog.Rock };
        int changed = 0;
        var tree = new FaceProcessTree();
        tree.Rebuild(new[] { f }, null, null, () => changed++);
        var root = tree.Roots[0];

        root.Children.Single(c => c.Process == FaceProcess.Load).Model = " WK-35 ";
        root.Children.Single(c => c.Process == FaceProcess.Haul).TrucksPerLoader = -3;
        Assert.Equal("WK-35", f.LoaderModel);
        Assert.Equal(0, f.TrucksPerLoader);          // 负数钳到 0
        Assert.Equal(2, changed);

        f.MaterialCode = PlanMaterialCatalog.Coal;   // 左表改了物料 → RefreshAll 重建分支
        tree.RefreshAll();
        Assert.Equal(3, tree.Roots[0].Children.Count);
    }

    /// <summary>未归属的面必须在面级告警里点出来；统计只数有设备的工序。</summary>
    [Fact]
    public void 未归属面告警_统计口径()
    {
        var a = new WorkingFace { Name = "A", MaterialCode = PlanMaterialCatalog.Rock, DrillModel = "KY-250" };
        var b = new WorkingFace { Name = "B", MaterialCode = PlanMaterialCatalog.Coal };
        var tree = new FaceProcessTree();
        var attribution = new Dictionary<string, string> { ["U1"] = "A", ["U2"] = "A" };
        tree.Rebuild(new[] { a, b }, attribution, new Dictionary<string, double> { ["A"] = 25000 }, () => { });

        Assert.Equal(2, tree.Roots[0].AttributedUnits);
        Assert.Contains("归属2单元", tree.Roots[0].Sub);
        Assert.Contains("本月2.5万m³", tree.Roots[0].Sub);
        Assert.Contains("未归属到任何单元", tree.Roots[1].Sub);
        Assert.Contains("没有单元归属", tree.Roots[1].Warn);

        var (cfg, tot, warn) = tree.Stats();
        Assert.Equal(4 + 3, tot);                    // 岩 4 支有设备 + 煤 3 支
        Assert.Equal(1, cfg);
        // 型号字典读不到（测试里库未初始化）⇒ 钉了的型号仍要出现在下拉里并挂告警，而不是显示成空
        var drill = tree.Roots[0].Children.Single(c => c.Process == FaceProcess.Drill);
        Assert.Contains(drill.ModelOptions, m => m.Model == "KY-250");
        Assert.True(drill.HasWarn);
        Assert.Equal(1, warn);
    }

    /// <summary>型号目录：库未就绪时不抛，来源文案自述，Check 只报「选了但用不了」不报「没选」，配车数负数点名。</summary>
    [Fact]
    public void 型号目录库未就绪时降级()
    {
        var all = PlanEquipModelCatalog.Reload();
        Assert.Empty(all);
        Assert.Contains("型号字典读不出来", PlanEquipModelCatalog.SourceText);
        Assert.Equal("（不约束 · 全矿挑）", PlanEquipModel.Any.PickerText);
        Assert.Single(PlanEquipModelCatalog.DrillOptions);            // 只有「不约束」
        Assert.Empty(PlanEquipModelCatalog.FaceCodes);
        Assert.Equal(0, PlanEquipModelCatalog.BenchHeightOf("F-01"));

        Assert.Empty(PlanEquipModelCatalog.Check(new WorkingFace { Name = "空" }));
        var bad = PlanEquipModelCatalog.Check(new WorkingFace { Name = "X", TruckModel = "NTE240", TrucksPerLoader = -1 });
        Assert.Equal(2, bad.Count);
        Assert.Contains(bad, s => s.Contains("NTE240") && s.Contains("不在型号字典"));
        Assert.Contains(bad, s => s.Contains("配车数"));
    }

    /// <summary>PickerText：台数与台效缺失都写在脸上。</summary>
    [Fact]
    public void 型号下拉文案自述台数与台效()
    {
        Assert.Equal("WK-35（可派 3 台）", new PlanEquipModel { Model = "WK-35", OnRoll = 4, Dispatchable = 3, StdDailyCapWanM3 = 1.2 }.PickerText);
        Assert.Equal("WK-35（◆ 在册 4 台但一台都不可派 · 无缺省台效）", new PlanEquipModel { Model = "WK-35", OnRoll = 4, Dispatchable = 0 }.PickerText);
        Assert.Equal("KY-250（◆ 在册 0 台 · 无缺省台效）", new PlanEquipModel { Model = "KY-250" }.PickerText);
        Assert.False(new PlanEquipModel { Model = "KY-250" }.Usable);
    }
}
