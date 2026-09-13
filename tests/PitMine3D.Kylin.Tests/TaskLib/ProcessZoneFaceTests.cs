// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/ProcessZoneFaceTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Engine;
using PitMine3D.Kylin.TaskLib.Zoning;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「工序作业区 → 作业面」的判据（F 组规则）。
///
/// <para><b>这一环之前是断的</b>：任务编制的作业面只认 <c>working_face_routing</c> 一张表，
/// 那张表空着时整盘面落到样例 —— 月计划排得再细，甘特里排的仍是「示例露天矿」的面，
/// 而界面上只有一行小字。这一组钉的就是那条真实链路：本期工序区就是本期的作业面清单。</para>
///
/// <para><b>喂的是合成算例</b>（走 <c>DeriveFrom</c>，不碰数据库）：
/// 裸台架里所有台账路径都静默走兜底，拿 <c>Derive()</c> 判等于什么都没判。</para>
/// </summary>
[Collection("PlanContext")]
public class ProcessZoneFaceTests
{
    private const string M = "2026-08";
    private const double Workdays = 25;

    // ══════════════════════════════════════════════════════════════
    //  夹具
    // ══════════════════════════════════════════════════════════════

    private static ProcessZone Zone(string name, string process, string unitIds,
                                    double vol = 10000, double z = 1200, long active = 1)
    {
        var ring = new List<ZonePoint>
        {
            new(0, 0, z), new(100, 0, z), new(100, 100, z), new(0, 100, z),
        };
        return new ProcessZone
        {
            Period = M, Process = process, Name = name, GroupKey = name,
            PointsJson = ZoneStore.SerializeRing(ring),
            ZSource = "算例", CellM = 1, AreaM2 = 10000,
            UnitIds = unitIds, UnitCount = unitIds.Split('、').Length,
            VolumeM3 = vol, Basis = ProcessZone.VolumeBasis(process),
            EquipRoleName = ProcessZone.EquipRole(process),
            Active = active,
        };
    }

    private static MiningUnitLedger.Row Row(string id, LedgerKind kind, double vol,
                                            int seq = 0, double done = 0, string seam = "岩1200",
                                            double widthM = 40, double? az = 90)
    {
        var r = new MiningUnitLedger.Row
        {
            UnitId = id, Kind = kind, Region = "采场1", Seam = seam,
            Seq = seq, Done = done, Period = M,
            WidthM = widthM, LengthM = 100, AzimuthDeg = az,
        };
        if (kind == LedgerKind.Coal) r.CoalM3 = vol;
        else if (kind == LedgerKind.Rock) r.NetRockM3 = vol;
        else r.DumpCapM3 = vol;
        return r;
    }

    private static Dictionary<string, MiningUnitLedger.Row> Ledger(params MiningUnitLedger.Row[] rows)
        => rows.ToDictionary(r => r.UnitId, r => r, StringComparer.OrdinalIgnoreCase);

    // ══════════════════════════════════════════════════════════════
    //  F1 只有采装与排土卸载构成作业面
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// F1a 穿孔区与爆破警戒区<b>不构成作业面</b>。
    /// <para>穿孔区是同一个面的另一段地 —— 把它也算成一个面，同一块料会被排两遍产，
    /// 而两条任务各自都"看着对"。警戒区更是反的：它是禁入区。</para>
    /// </summary>
    [Fact]
    public void F1a_穿孔区与警戒区不构成作业面()
    {
        var zones = new[]
        {
            Zone("面A", ProcessZone.ProcLoad, "U1"),
            Zone("面A", ProcessZone.ProcDrill, "U1"),
            Zone("警戒·面A", ProcessZone.ProcBlastGuard, "U1"),
            Zone("排土1", ProcessZone.ProcDumpTip, "D1"),
            Zone("排土1", ProcessZone.ProcDumpDoze, "D1"),
        };
        var faces = ProcessZoneFaceSource.DeriveFrom(M, zones,
            Ledger(Row("U1", LedgerKind.Rock, 10000, seq: 1), Row("D1", LedgerKind.Dump, 8000)), Workdays);

        Assert.NotNull(faces);
        Assert.Equal(2, faces!.Count);
        Assert.Contains(faces, f => f.Zone == "面A" && f.Process == ProcessType.Load);
        Assert.Contains(faces, f => f.Zone == "排土1" && f.Process == ProcessType.Dump);
    }

    /// <summary>F1b 未纳入本期（active=0）的工序区不构成作业面。</summary>
    [Fact]
    public void F1b_未纳入本期的工序区不算()
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "U1", active: 0) },
            Ledger(Row("U1", LedgerKind.Rock, 10000)), Workdays);
        Assert.Null(faces);
        Assert.Contains("没有一块是", ProcessZoneFaceSource.LastSourceLabel);
    }

    /// <summary>
    /// F1c 一块工序区都没有时返回 null，并给<b>可执行的补法</b>，不是"暂无数据"。
    /// </summary>
    [Fact]
    public void F1c_没有工序区时给可执行的补法()
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M, Array.Empty<ProcessZone>(), null, Workdays);
        Assert.Null(faces);
        Assert.Contains("作业区划分", ProcessZoneFaceSource.LastSourceLabel);
    }

    // ══════════════════════════════════════════════════════════════
    //  F2 补齐不顶替
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// F2 台账里已有的面<b>原样保留</b>，派生只补台账里没有的。
    /// <para>反过来做（派生覆盖台账）会把人填的去向/运距/主设备/煤质冲掉 —— 而且不报错。</para>
    /// </summary>
    [Fact]
    public void F2_补齐不顶替且按面名比对()
    {
        var cfg = new ExploderConfig();
        cfg.Faces.Add(new FaceInput
        {
            Zone = "面A", UnitId = "老单元", DestinationName = "破碎站", HaulDistanceKm = 3.2,
        });

        var derived = new List<FaceInput>
        {
            new() { Zone = "面A", UnitId = "U9" },      // 同名 —— 不许覆盖
            new() { Zone = "面B", UnitId = "U2" },      // 台账没有 —— 补进来
        };

        int added = ProductionPlanContext.MergeDerivedFaces(cfg, derived);

        Assert.Equal(1, added);
        Assert.Equal(2, cfg.Faces.Count);
        var a = cfg.Faces.Single(f => f.Zone == "面A");
        Assert.Equal("老单元", a.UnitId);                // ★ 人填的没被冲掉
        Assert.Equal("破碎站", a.DestinationName);
        Assert.InRange(a.HaulDistanceKm, 3.19, 3.21);
        Assert.Contains(cfg.Faces, f => f.Zone == "面B");
    }

    // ══════════════════════════════════════════════════════════════
    //  F3 队首 vs 全部剩余
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// F3 <c>UnitId</c> 取<b>队首</b>（排产序最小且未采完），<c>AvailableReserveM3</c> 是
    /// 这个面脚下<b>全部</b>剩余量之和 —— <b>两者不是一个数</b>。
    /// <para>混成一个数，「这个面还能采几天」就永远只算得出一个单元的天数。</para>
    /// </summary>
    [Fact]
    public void F3_单元号取队首而备采是全部剩余之和()
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "U1、U2、U3") },
            Ledger(Row("U1", LedgerKind.Rock, 1000, seq: 1, done: 1.0),   // 已采完 ⇒ 不当队首
                   Row("U2", LedgerKind.Rock, 2000, seq: 2),
                   Row("U3", LedgerKind.Rock, 3000, seq: 3)), Workdays);

        var f = Assert.Single(faces!);
        Assert.Equal("U2", f.UnitId);                                    // 队首 = 未采完里 Seq 最小
        Assert.InRange(f.AvailableReserveM3, 4999, 5001);                // 2000 + 3000（U1 已采完不计）
        Assert.Contains(ProcessZoneFaceSource.LastNotes, s => s.Contains("队首"));
    }

    /// <summary>F3b 排产序为 0 的单元排到队尾，不许抢占队首。</summary>
    [Fact]
    public void F3b_没有排产序的单元排队尾()
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "UX、U5") },
            Ledger(Row("UX", LedgerKind.Rock, 1000, seq: 0),
                   Row("U5", LedgerKind.Rock, 1000, seq: 5)), Workdays);

        Assert.Equal("U5", Assert.Single(faces!).UnitId);
    }

    // ══════════════════════════════════════════════════════════════
    //  F4 量口径
    // ══════════════════════════════════════════════════════════════

    /// <summary>F4a 采装面的日目标 = 备采 / 月作业日。</summary>
    [Fact]
    public void F4a_采装面日目标按作业日摊()
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "U1") },
            Ledger(Row("U1", LedgerKind.Rock, 25000, seq: 1)), Workdays);

        var f = Assert.Single(faces!);
        Assert.Equal(ProcessType.Load, f.Process);
        Assert.InRange(f.DayTargetM3, 999, 1001);        // 25000 / 25
        Assert.False(f.DerivedFromInbound);
    }

    /// <summary>
    /// F4b 排土面<b>不给日目标</b>，走 <c>DerivedFromInbound</c>（由入方物料流推导）。
    /// <para>手填一个「排弃占容 / 工日」会与采装侧算出来的入方对不上，采排不再守恒 ——
    /// 而两边各自都合理，谁也看不出来。</para>
    /// </summary>
    [Fact]
    public void F4b_排土面的日目标由入方推导而不是手填()
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M,
            new[] { Zone("排土1", ProcessZone.ProcDumpTip, "D1") },
            Ledger(Row("D1", LedgerKind.Dump, 25000)), Workdays);

        var f = Assert.Single(faces!);
        Assert.Equal(ProcessType.Dump, f.Process);
        Assert.True(f.DerivedFromInbound);
        Assert.Equal(0, f.DayTargetM3);                  // ★ 不是 25000/25
    }

    /// <summary>F4c 已采部分不计入备采（`Done` 要扣）。</summary>
    [Fact]
    public void F4c_已采部分不计入备采()
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "U1") },
            Ledger(Row("U1", LedgerKind.Rock, 10000, seq: 1, done: 0.4)), Workdays);

        Assert.InRange(Assert.Single(faces!).AvailableReserveM3, 5999, 6001);
    }

    // ══════════════════════════════════════════════════════════════
    //  F5 物料
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// F5a 层名认得出表土 / 风化岩 / 夹矸时按它判 —— <b>不是一律按台账大类</b>。
    /// <para>同一个岩台阶，表土不用爆破且只能进表土堆场，硬岩要爆破且能进内外排；
    /// 台账的 <c>Kind</c> 两者都是「岩」。判错的后果是表土被排进内排场，而没有任何东西报错。</para>
    /// </summary>
    [Theory]
    [InlineData("表土层", MaterialCatalog.Topsoil)]
    [InlineData("风化带", MaterialCatalog.Weathered)]
    [InlineData("夹矸1", MaterialCatalog.Interburden)]
    [InlineData("岩1200", MaterialCatalog.Rock)]
    public void F5a_层名认得出的物料按层名判(string seam, string want)
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "U1") },
            Ledger(Row("U1", LedgerKind.Rock, 1000, seq: 1, seam: seam)), Workdays);

        Assert.Equal(want, Assert.Single(faces!).MaterialCode);
    }

    /// <summary>F5b 煤台阶判成煤（免爆、只能进破碎站/煤仓/储煤场）。</summary>
    [Fact]
    public void F5b_煤台阶判成煤()
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M,
            new[] { Zone("采煤面", ProcessZone.ProcLoad, "C1") },
            Ledger(Row("C1", LedgerKind.Coal, 1000, seq: 1, seam: "4煤")), Workdays);

        var f = Assert.Single(faces!);
        Assert.Equal(MaterialCatalog.Coal, f.MaterialCode);
        Assert.False(MaterialCatalog.Resolve(f.MaterialCode).NeedsBlasting);
    }

    /// <summary>
    /// F5c 台账整个读不到时**照样出面**（备采退到工序区记的数），但要<b>点名说清楚</b>。
    /// <para>不说的话，"备采是台账算的"与"备采是三天前生成区域时记下的"长得一模一样。</para>
    /// </summary>
    [Fact]
    public void F5c_台账读不到时照样出面但要点名()
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "U1", vol: 12345) },
            null, Workdays);

        var f = Assert.Single(faces!);
        Assert.InRange(f.AvailableReserveM3, 12344, 12346);
        Assert.Contains(ProcessZoneFaceSource.LastNotes, s => s.Contains("月度台账读不到"));
    }

    // ══════════════════════════════════════════════════════════════
    //  位置
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// F6 面带得出<b>源端坐标</b>与台阶标高（环质心 + 环平均 Z）。
    /// <para>没有源端坐标时，路网求运距只能靠面名去匹配节点，对不上就整条链落到兜底运距，
    /// 配车数与编组班产跟着一起偏 —— 而每一步都不报错。</para>
    /// </summary>
    [Fact]
    public void F6_面带得出源端坐标与台阶标高()
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "U1", z: 1180) },
            Ledger(Row("U1", LedgerKind.Rock, 1000, seq: 1)), Workdays);

        var f = Assert.Single(faces!);
        Assert.True(f.HasSourcePosition);
        Assert.InRange(f.SourceX, 49, 51);                // 0..100 见方的环
        Assert.InRange(f.SourceY, 49, 51);
        Assert.InRange(f.BenchElevationM, 1179, 1181);
        Assert.Equal(f.BenchElevationM, f.SourceZ);
    }

    /// <summary>F7 面带得出推进方位与推进宽（作业区布置画箭头、动画走位要用）。</summary>
    [Fact]
    public void F7_面带得出推进方位与推进宽()
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "U1") },
            Ledger(Row("U1", LedgerKind.Rock, 1000, seq: 1, widthM: 35, az: 127)), Workdays);

        var f = Assert.Single(faces!);
        Assert.Equal(127, f.AdvanceAzimuthDeg);
        Assert.Equal(35, f.MiningWidthM);
    }

    /// <summary>
    /// F8 台账里没填方位角时是 <b>null</b>，不是 0。
    /// <para>0° 是一个完全合法的方位（正南北走向）。拿 0 当"没填"，
    /// 所有没录方位的面都会一本正经地朝着正南北推进 —— 而每个数看上去都正常。</para>
    /// </summary>
    [Fact]
    public void F8_没填方位角是null不是零()
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M,
            new[] { Zone("面A", ProcessZone.ProcLoad, "U1") },
            Ledger(Row("U1", LedgerKind.Rock, 1000, seq: 1, az: null)), Workdays);

        Assert.Null(Assert.Single(faces!).AdvanceAzimuthDeg);
    }

    /// <summary>F9 工序区没有单元号时**点名**：那个面没有身份证，备采核销与图上定位都做不了。</summary>
    [Fact]
    public void F9_没有单元号的工序区要点名()
    {
        var faces = ProcessZoneFaceSource.DeriveFrom(M,
            new[] { Zone("手工圈的", ProcessZone.ProcLoad, "") },
            Ledger(Row("U1", LedgerKind.Rock, 1000, seq: 1)), Workdays);

        Assert.Single(faces!);
        Assert.Contains(ProcessZoneFaceSource.LastNotes, s => s.Contains("没有单元号"));
    }

    // ══════════════════════════════════════════════════════════════
    //  G：没有数据时必须自报家门（2026-08-18 改口径）
    //
    //  **样例已经不兜底了**。此前缺数据时会补一盘「示例露天矿」的面 ——
    //  那份假盘子长得和真盘子完全一样：有面、有量、有编组、有甘特、有达成度，
    //  每一个数都自洽，人分不出自己看的是哪个矿。现在空着就是空着。
    //  所以这一组判的是「排不出计划时说没说出来」，而不再是「落到样例时说没说出来」。
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// G1 <b>不变式</b>：来源文案带记号，当且仅当这盘面不是真实数据。
    /// <para>写成不变式而不是"裸台架下应该有记号"，是为了两个世界里都判得动 ——
    /// 判据工程哪天接上真库副本，这一条照样成立，不会变成一条空过的判据。</para>
    /// </summary>
    [Fact]
    public void G1_无数据记号与是否真实数据严格一致()
    {
        var cfg = ProductionPlanContext.Config();
        Assert.NotNull(cfg);

        bool marked = ProductionPlanContext.SourceLabel.StartsWith("【", StringComparison.Ordinal);
        Assert.Equal(!ProductionPlanContext.FacesAreReal, marked);
    }

    /// <summary>
    /// G2 裸台架里<b>一个作业面都没有</b>，而且必须挂着那条响亮的备注 + 补法。
    /// <para>第一条 Assert 是故意的：它红了不是这一层坏了，而是判据工程连上了真库。</para>
    /// </summary>
    [Fact]
    public void G2_没有作业面时挂着响亮的备注()
    {
        var cfg = ProductionPlanContext.Config();

        Assert.False(ProductionPlanContext.FacesAreReal,
            "裸台架里不该解出真实作业面 —— 这一条红了说明判据工程连上了真库副本，见 offline-real-db-harness");
        Assert.Equal(ProductionPlanContext.FaceOriginKind.None, ProductionPlanContext.FaceOrigin);
        Assert.True(ProductionPlanContext.NoFaces);
        Assert.Empty(cfg.Faces);                                   // ★ 一个样例面都不许混进来
        Assert.Contains(ProductionPlanContext.AssemblyNotes,
                        n => n.Contains("一个作业面都没有") && n.Contains("作业区划分"));
    }

    /// <summary>
    /// G2b <b>样例这一档已经产生不出来了</b>。
    /// <para>把兜底加回去，这一条必红 —— 它钉的正是"不许再编一盘假的"。</para>
    /// </summary>
    [Fact]
    public void G2b_样例这一档产生不出来()
    {
        ProductionPlanContext.Config();
        Assert.NotEqual(ProductionPlanContext.FaceOriginKind.Sample, ProductionPlanContext.FaceOrigin);
        Assert.DoesNotContain("示例露天矿", ProductionPlanContext.SourceLabel);
    }

    /// <summary>
    /// G3 只有「台账 / 派生 / 台账+派生」三档算真实数据；<b>「没有」的枚举值是 0</b>。
    /// <para>默认值就是"不真实"，新工程一开始就带记号，而不是等谁去设一下。</para>
    /// </summary>
    [Fact]
    public void G3_只有三档算真实数据()
    {
        Assert.Equal(0, (int)ProductionPlanContext.FaceOriginKind.None);
        foreach (var k in Enum.GetValues<ProductionPlanContext.FaceOriginKind>())
        {
            bool real = k is ProductionPlanContext.FaceOriginKind.Ledger
                          or ProductionPlanContext.FaceOriginKind.Derived
                          or ProductionPlanContext.FaceOriginKind.LedgerPlusDerived;
            // 逐档核一遍归类，别让新加的档默认落到某一边
            Assert.Equal(real, k is not (ProductionPlanContext.FaceOriginKind.None
                                       or ProductionPlanContext.FaceOriginKind.Sample));
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  F10–F12：源端坐标（运距的入口）
    // ══════════════════════════════════════════════════════════════

    /// <summary>一侧顶点加密的方环 —— 顶点均值会被拖过去，面积质心不会。</summary>
    private static List<ZonePoint> LopsidedSquare(double z = 1200)
    {
        var r = new List<ZonePoint> { new(0, 0, z), new(100, 0, z) };
        // 右边界上塞 9 个共线点：几何一模一样，顶点均值却整体右移
        for (int k = 1; k <= 9; k++) r.Add(new ZonePoint(100, k * 10, z));
        r.Add(new ZonePoint(100, 100, z));
        r.Add(new ZonePoint(0, 100, z));
        return r;
    }

    /// <summary>
    /// F10 <b>顶点均值与面积质心在这种环上确实不同</b> —— 先证明这条判据不是空过的。
    /// <para>栅格描边 + DP 抽稀出来的环天生就是这样：弯的那一侧留的点更多。</para>
    /// </summary>
    [Fact]
    public void F10_顶点均值与面积质心在偏心环上确实不同()
    {
        var ring = LopsidedSquare();
        double meanX = ring.Average(p => p.X);
        var (cx, _) = HaulResolver.AreaCentroid(ring.Select(p => (p.X, p.Y)).ToList(), out bool deg);

        Assert.False(deg);
        Assert.InRange(cx, 49, 51);                 // 面积质心仍在正中
        Assert.True(meanX > 70, $"顶点均值应被拖到右侧，实际 {meanX:0.#}");
    }

    /// <summary>
    /// F11 派生的源端坐标取<b>面积质心</b>，不是顶点均值。
    /// <para>装车点更接近区域的几何中心；用顶点均值的话，一条弯带的源点会系统性地偏向弯的那一头，
    /// 而偏出来的点看着还挺像个中心 —— 运距、循环时间、配车数、编组班产跟着一起偏，每一步都不报错。</para>
    /// </summary>
    [Fact]
    public void F11_源端坐标取面积质心不是顶点均值()
    {
        var z = Zone("面A", ProcessZone.ProcLoad, "U1");
        z.PointsJson = ZoneStore.SerializeRing(LopsidedSquare());

        var f = Assert.Single(ProcessZoneFaceSource.DeriveFrom(M, new[] { z },
                    Ledger(Row("U1", LedgerKind.Rock, 1000, seq: 1)), Workdays)!);

        Assert.True(f.HasSourcePosition);
        Assert.InRange(f.SourceX, 49, 51);          // 顶点均值会给 ~77
    }

    /// <summary>
    /// F12 派生的坐标标成<b>「工序区质心」</b>，不冒充「录入坐标」。
    /// <para>坐标本身分不出「人在台账上录的铲位」与「按几何推的质心」——
    /// 前者是实测装车点，后者只是一块地的中心，两者运距能差几百米。
    /// 报表上写「录入 N」而其实没人录过，是在给一个数字冒充精度。</para>
    /// </summary>
    [Fact]
    public void F12_派生坐标不冒充录入坐标()
    {
        var f = Assert.Single(ProcessZoneFaceSource.DeriveFrom(M,
                    new[] { Zone("面A", ProcessZone.ProcLoad, "U1") },
                    Ledger(Row("U1", LedgerKind.Rock, 1000, seq: 1)), Workdays)!);

        Assert.Equal(HaulResolver.OriginDerived, f.SourceOrigin);
        Assert.NotEqual(HaulResolver.OriginEntered, f.SourceOrigin);
        Assert.NotEqual(HaulResolver.OriginRegion, f.SourceOrigin);   // 也不是按 mineable_region 推的
    }

    /// <summary>
    /// F13 三档来源两两不同 —— 合并任意两档都会让报表少说一件事。
    /// </summary>
    [Fact]
    public void F13_三档源端来源两两不同()
    {
        var all = new[] { HaulResolver.OriginEntered, HaulResolver.OriginRegion, HaulResolver.OriginDerived };
        Assert.Equal(3, all.Distinct().Count());
        Assert.All(all, x => Assert.False(string.IsNullOrWhiteSpace(x)));
    }

    /// <summary>F14 面积退化的环要点名（退回顶点均值，那个点不代表装车位置）。</summary>
    [Fact]
    public void F14_面积退化的环要点名()
    {
        var z = Zone("面A", ProcessZone.ProcLoad, "U1");
        // 三点共线 ⇒ 面积为 0
        z.PointsJson = ZoneStore.SerializeRing(new List<ZonePoint>
        { new(0, 0, 1200), new(50, 0, 1200), new(100, 0, 1200) });

        ProcessZoneFaceSource.DeriveFrom(M, new[] { z },
            Ledger(Row("U1", LedgerKind.Rock, 1000, seq: 1)), Workdays);

        Assert.Contains(ProcessZoneFaceSource.LastNotes, x => x.Contains("面积退化"));
    }

    /// <summary>
    /// F15 运距汇总里<b>三档分开数</b>：派生的面不许被算进「录入」。
    /// <para>此前 `entered` 把一切带坐标的面都算进去 —— 一盘全派生的面会报成「录入 N / 区域质心 0」，
    /// 看的人会以为这些铲位都是现场实测的。</para>
    /// </summary>
    [Fact]
    public void F15_运距汇总里派生不算进录入()
    {
        var cfg = new ExploderConfig();
        cfg.Faces.Add(new FaceInput
        {
            Zone = "派生面", SourceX = 100, SourceY = 200,
            SourceOrigin = HaulResolver.OriginDerived,
        });
        cfg.Faces.Add(new FaceInput { Zone = "手录面", SourceX = 300, SourceY = 400 });

        string label = HaulResolver.FillSourcePositions(cfg);

        Assert.Contains("录入 1", label);
        Assert.Contains("工序区质心 1", label);
    }

    // ══════════════════════════════════════════════════════════════
    //  F16–F19：排土面绑汇（不绑上就整月零入方，且静默）
    // ══════════════════════════════════════════════════════════════

    private static SinkRegistry Sinks(params (string Id, string Name, SinkKind Kind)[] items)
    {
        var reg = new SinkRegistry();
        reg.Load(items.Select(x => new SinkNode { Id = x.Id, Name = x.Name, Kind = x.Kind }).ToList());
        return reg;
    }

    /// <summary>
    /// F16 排土面按<b>台账的排土场名</b>绑汇，不按面名。
    /// <para>派生的面名是工序区的分组键，兜底时带着台阶（「外排土场·L1」），而汇叫「外排土场」——
    /// 按面名去配根本配不上，而配不上的后果是这个面整月零入方。</para>
    /// </summary>
    [Fact]
    public void F16_排土面按台账排土场名绑汇()
    {
        var row = Row("D1", LedgerKind.Dump, 8000);
        row.Region = "外排土场";                                  // 台账里的权威名字
        var reg = Sinks(("SK-1", "外排土场", SinkKind.ExternalDump));

        var f = Assert.Single(ProcessZoneFaceSource.DeriveFrom(M,
                    new[] { Zone("外排土场·L1", ProcessZone.ProcDumpTip, "D1") },   // ★ 面名带台阶
                    Ledger(row), Workdays, reg)!);

        Assert.Equal("SK-1", f.DestinationId);
        Assert.Equal("外排土场", f.DestinationName);
        Assert.True(f.HasDestination);
    }

    /// <summary>
    /// F17 绑上之后 <c>FlowAssigner.MatchesFace</c> <b>真的认得出来</b>。
    /// <para>这一条判的是"接上了"而不是"填了字段"：只判 DestinationId 有值是不够的 ——
    /// 下游认不认是另一回事，而认不出来时同样是静默的零入方。</para>
    /// </summary>
    [Fact]
    public void F17_绑上之后下游真的认得出来()
    {
        var row = Row("D1", LedgerKind.Dump, 8000);
        row.Region = "外排土场";
        var reg = Sinks(("SK-1", "外排土场", SinkKind.ExternalDump));

        var f = Assert.Single(ProcessZoneFaceSource.DeriveFrom(M,
                    new[] { Zone("外排土场·L1", ProcessZone.ProcDumpTip, "D1") },
                    Ledger(row), Workdays, reg)!);

        Assert.True(FlowAssigner.MatchesFace(new SinkLoad { SinkId = "SK-1", SinkName = "外排土场" }, f));
        // 反证：不绑的话（面名与汇名不等）下游认不出来 —— 这就是这条修复存在的理由
        var bare = new FaceInput { Zone = "外排土场·L1" };
        Assert.False(FlowAssigner.MatchesFace(new SinkLoad { SinkId = "SK-1", SinkName = "外排土场" }, bare));
    }

    /// <summary>
    /// F18 去向台账里没有同名排土场时<b>不绑，并点名</b> —— 不许随便挑一个顶上。
    /// <para>挑错的那一个不会报错，只是这个面的料一整月都排到了别的排土场，而两边的量各自都平。</para>
    /// </summary>
    [Fact]
    public void F18_对不上时不绑且点名()
    {
        var row = Row("D1", LedgerKind.Dump, 8000);
        row.Region = "南排土场";
        var reg = Sinks(("SK-1", "外排土场", SinkKind.ExternalDump),
                        ("SK-2", "北排土场", SinkKind.ExternalDump));

        var f = Assert.Single(ProcessZoneFaceSource.DeriveFrom(M,
                    new[] { Zone("南排土场·L1", ProcessZone.ProcDumpTip, "D1") },
                    Ledger(row), Workdays, reg)!);

        Assert.False(f.HasDestination);                       // ★ 没有随便挑一个
        Assert.Contains(ProcessZoneFaceSource.LastNotes,
                        x => x.Contains("没绑上去向") && x.Contains("南排土场"));
        Assert.Contains(ProcessZoneFaceSource.LastNotes, x => x.Contains("整月零入方"));
    }

    /// <summary>F19 采装面<b>不</b>绑汇 —— 那是 FlowAssigner 按运输功最小逐物料解出来的，不该在这里定死。</summary>
    [Fact]
    public void F19_采装面不在这里定去向()
    {
        var reg = Sinks(("SK-1", "外排土场", SinkKind.ExternalDump));
        var f = Assert.Single(ProcessZoneFaceSource.DeriveFrom(M,
                    new[] { Zone("面A", ProcessZone.ProcLoad, "U1") },
                    Ledger(Row("U1", LedgerKind.Rock, 1000, seq: 1)), Workdays, reg)!);

        Assert.False(f.HasDestination);
        Assert.Empty(ProcessZoneFaceSource.LastNotes.Where(x => x.Contains("没绑上去向")));
    }
}
