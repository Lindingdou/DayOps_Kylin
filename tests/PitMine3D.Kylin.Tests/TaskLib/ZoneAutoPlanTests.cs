// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/ZoneAutoPlanTests.cs（逐行对应；仅命名空间/依赖适配）
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.TaskLib.Simulation;
using PitMine3D.Kylin.TaskLib.Zoning;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「作业区域按采矿模型 + 月度计划自动生成」的判据（Z 组规则）。
///
/// <para><b>这一层的失败全是静默的</b>，所以判据钉的是**性质**不是数字：
/// 区域名对不上源 ⇒ 推演里那块地整期不动、界面没有红字；
/// 环的 Z 落成 0 ⇒ 层体整体摆到 0 米、界面看不出异常（<c>ZoneStore</c> 文件头记过这笔账）；
/// 不相连的两块地被合成一个外包轮廓 ⇒ 中间根本不作业的地被圈进本期范围，面积照样"看着对"。</para>
///
/// <para><b>喂的是合成算例</b>：矩形占地的并集面积、连通性、Z 区间都能手算，
/// 所以每一条都能证伪 —— 把实现换成凸包 / 换成"补 0" / 换成"合并所有块"，对应那条必红。</para>
/// </summary>
public class ZoneAutoPlanTests
{
    // ══════════════════════════════════════════════════════════════
    //  夹具：一块矩形占地 = 一个采掘单元
    // ══════════════════════════════════════════════════════════════

    /// <summary>轴对齐矩形占地（左下角 + 宽高），Z 全取 <paramref name="z"/>。</summary>
    private static PlanBlock Rect(string id, double x, double y, double w, double h, double z = 1200,
                                  LedgerKind kind = LedgerKind.Coal, string face = "", string region = "采场1",
                                  double vol = 10000)
    {
        var xy = new[] { x, y, x + w, y, x + w, y + h, x, y + h };
        var top = new List<double>();
        for (int i = 0; i + 1 < xy.Length; i += 2) { top.Add(xy[i]); top.Add(xy[i + 1]); top.Add(z); }
        return new PlanBlock
        {
            UnitId = id, Kind = kind, Region = region, Seam = "4",
            FaceZone = face, FaceCount = face.Length > 0 ? 1 : 0,
            FootprintXy = xy, TopXyz = top.ToArray(),
            RealRail = true, VolumeM3 = vol,
        };
    }

    private static PlanScope Scope(params PlanBlock[] blocks) => new()
    {
        Month = "2026-08",
        Blocks = blocks.ToList(),
        Header = "夹具",
    };

    // ══════════════════════════════════════════════════════════════
    //  ZA：占地并集与分组
    // ══════════════════════════════════════════════════════════════

    /// <summary>ZA1 相邻两块并成一块，面积 ≈ 并集真值（不是两块之和的重复计，也不是只剩一块）。</summary>
    [Fact]
    public void ZA1_相邻两块并成一个连通块且面积对得上()
    {
        // 100×100 与它右侧紧邻的 100×100 ⇒ 并集 200×100 = 2 万 m²
        var res = ZoneAutoPlanner.Plan(Scope(
            Rect("A", 0, 0, 100, 100, face: "采装1#"),
            Rect("B", 100, 0, 100, 100, face: "采装1#")), null, cellM: 1.0);

        var p = Assert.Single(res.Proposals);
        Assert.Equal(1, p.PieceCount);
        Assert.InRange(p.AreaM2, 20000 * 0.95, 20000 * 1.06);   // 栅格误差按格距 1m 计
    }

    /// <summary>
    /// ZA2 分开的两块**不合并**成一个外包轮廓（Z6）。
    /// <para>这条是给"图省事改成凸包 / 取整体包围盒"准备的：那样做 1 个候选、面积翻好几倍，本条必红。</para>
    /// </summary>
    [Fact]
    public void ZA2_不相连的两块不合成一个外包轮廓()
    {
        var res = ZoneAutoPlanner.Plan(Scope(
            Rect("A", 0, 0, 100, 100, face: "采装1#"),
            Rect("B", 600, 0, 100, 100, face: "采装1#")), null, cellM: 2.0);

        Assert.Equal(2, res.Proposals.Count);
        Assert.All(res.Proposals, p => Assert.InRange(p.AreaM2, 10000 * 0.9, 10000 * 1.12));
        // 合成外包的话面积会是 700×100 = 7 万 m²
        Assert.True(res.Proposals.Sum(p => p.AreaM2) < 30000,
                    $"两块分开的地被合并了：合计 {res.Proposals.Sum(p => p.AreaM2):0} m²");
    }

    /// <summary>ZA3 分组键是作业面，区域名直接取作业面名 —— 推演按这个名字配源。</summary>
    [Fact]
    public void ZA3_按作业面分组且名字取作业面名()
    {
        var res = ZoneAutoPlanner.Plan(Scope(
            Rect("A", 0, 0, 100, 100, face: "采装1#"),
            Rect("B", 600, 0, 100, 100, face: "采装2#")), null, cellM: 2.0);

        Assert.Equal(2, res.Proposals.Count);
        Assert.Contains(res.Proposals, p => p.Name == "采装1#" && p.ByFace);
        Assert.Contains(res.Proposals, p => p.Name == "采装2#" && p.ByFace);
    }

    /// <summary>ZA4 没有作业面绑的单元按「采场 + 台阶」兜底成一组（Z3：不静默丢、也不并进别人的区）。</summary>
    [Fact]
    public void ZA4_没有面绑的按采场加台阶兜底且标明不是按面分的()
    {
        var res = ZoneAutoPlanner.Plan(Scope(
            Rect("A", 0, 0, 100, 100, region: "采场1"),
            Rect("B", 100, 0, 100, 100, region: "采场1")), null, cellM: 2.0);

        var p = Assert.Single(res.Proposals);
        Assert.Equal("采场1·4", p.Name);      // 夹具的煤层号是 4
        Assert.False(p.ByFace);
        Assert.Contains(res.Notes, s => s.Contains("兜底"));
    }

    /// <summary>
    /// ZA4b 兜底键**必须带台阶**：同一个采场的两个台阶不许并成一组。
    /// <para>真台账里 <c>采场</c> 一栏全是同一个「采场1」——只按它并，几十个台阶的地会叠成一组，
    /// 而各台阶在平面上本就互不相接，结果是一组切成 93 块不相连的地（2026-08 真台账实测）。</para>
    /// </summary>
    [Fact]
    public void ZA4b_同一采场的不同台阶不并成一组()
    {
        var a = Rect("A", 0, 0, 100, 100, z: 1400, region: "采场1"); a.Seam = "岩1400";
        var b = Rect("B", 300, 0, 100, 100, z: 1385, region: "采场1"); b.Seam = "岩1385";
        var res = ZoneAutoPlanner.Plan(Scope(a, b), null, cellM: 2.0);

        Assert.Equal(2, res.Proposals.Count);
        Assert.All(res.Proposals, p => Assert.Equal(1, p.PieceCount));   // 各自成组，不是"一组两块"
        Assert.Contains(res.Proposals, p => p.Name == "采场1·岩1400");
        Assert.Contains(res.Proposals, p => p.Name == "采场1·岩1385");
    }

    /// <summary>
    /// ZA5b 碎成一堆时要**说出根因**，而不是让人以为这块地本来就碎。
    /// </summary>
    [Fact]
    public void ZA5b_碎成六块以上要报根因()
    {
        var blocks = new List<PlanBlock>();
        for (int i = 0; i < 7; i++) blocks.Add(Rect($"U{i}", i * 400, 0, 100, 100, face: "采装1#"));
        var res = ZoneAutoPlanner.Plan(Scope(blocks.ToArray()), null, cellM: 4.0);

        Assert.Equal(7, res.Proposals.Count);
        Assert.Contains(res.Notes, s => s.Contains("真轨") && s.Contains("不相连"));
    }

    /// <summary>ZA5c 没有真轨又没有方位角时，要点名说「盒子一律长轴朝东」并给补法。</summary>
    [Fact]
    public void ZA5c_没有真轨也没有方位角要点名并给补法()
    {
        var b = Rect("A", 0, 0, 100, 100, face: "采装1#");
        b.RealRail = false; b.HasAzimuth = false;
        var res = ZoneAutoPlanner.Plan(Scope(b), null, cellM: 2.0);
        Assert.Contains(res.Notes, s => s.Contains("走向方位角") && s.Contains("采掘单元清单"));
    }

    /// <summary>
    /// ZA5 同一个面分成两块不相连的地时，**只有最大那块默认勾**（Z6）。
    /// <para>两块同名区域都进推演的话，推演会让每一块各吃下该面的全部量 —— 总量凭空翻倍。</para>
    /// </summary>
    [Fact]
    public void ZA5_同组多块时只默认勾最大的一块()
    {
        var res = ZoneAutoPlanner.Plan(Scope(
            Rect("A", 0, 0, 200, 200, face: "采装1#"),      // 4 万 m²
            Rect("B", 900, 0, 100, 100, face: "采装1#")),   // 1 万 m²
            null, cellM: 2.0);

        Assert.Equal(2, res.Proposals.Count);
        var big = res.Proposals.Single(p => p.PieceIndex == 1);
        var small = res.Proposals.Single(p => p.PieceIndex == 2);
        Assert.True(big.AreaM2 > small.AreaM2);
        Assert.True(big.Selected, "最大的一块应该默认勾上");
        Assert.False(small.Selected, "次要块段默认不勾 —— 两块同名都进推演会让量翻倍");
        Assert.Equal("采装1#", big.Name);
        Assert.Equal("采装1#-2", small.Name);
    }

    /// <summary>
    /// ZA6 <b>环的 Z 来自采矿模型，绝不落 0</b>。
    /// <para>这条对着 <c>SimRegions.ParseXyz</c> 那个坑：写 0 拿回来是 0.0 而不是 NaN ⇒
    /// "没有 Z 就去现状面采"的兜底整条不跑，层体全部摆在 0 米，而界面上没有任何异常。</para>
    /// </summary>
    [Fact]
    public void ZA6_环的高程取自模型且不落零()
    {
        var res = ZoneAutoPlanner.Plan(Scope(
            Rect("A", 0, 0, 100, 100, z: 1200, face: "采装1#"),
            Rect("B", 100, 0, 100, 100, z: 1180, face: "采装1#")), null, cellM: 2.0);

        var p = Assert.Single(res.Proposals);
        Assert.NotEmpty(p.Ring);
        Assert.All(p.Ring, v => Assert.InRange(v.Z, 1180, 1200));
        Assert.Contains("采矿模型", p.Provenance);
    }

    /// <summary>ZA7 类别由块段类型定（Z8）：采场 pit / 内排 internal_dump / 外排 external_dump。</summary>
    [Theory]
    [InlineData(LedgerKind.Coal, "采场1", "pit")]
    [InlineData(LedgerKind.Rock, "采场1", "pit")]
    [InlineData(LedgerKind.Dump, "内排土场1", "internal_dump")]
    [InlineData(LedgerKind.Dump, "外排土场1", "external_dump")]
    public void ZA7_类别由块段类型与内外排名定(LedgerKind kind, string region, string want)
    {
        var res = ZoneAutoPlanner.Plan(Scope(Rect("A", 0, 0, 100, 100, kind: kind, region: region)), null, cellM: 2.0);
        Assert.Equal(want, Assert.Single(res.Proposals).Category);
    }

    /// <summary>ZA7b 排土但名字看不出内外排时按外排，且**必须说出来**（内外排的运距与容量口径不同）。</summary>
    [Fact]
    public void ZA7b_排土名看不出内外排时按外排并提示()
    {
        var res = ZoneAutoPlanner.Plan(Scope(
            Rect("A", 0, 0, 100, 100, kind: LedgerKind.Dump, region: "土场甲")), null, cellM: 2.0);
        var p = Assert.Single(res.Proposals);
        Assert.Equal("external_dump", p.Category);
        Assert.Contains(p.Issues, s => s.Contains("内排") && s.Contains("外排"));
    }

    /// <summary>ZA8 记账：候选覆盖的量必须等于输入块段的总量（一块都不许掉队）。</summary>
    [Fact]
    public void ZA8_候选覆盖的量等于本期块段总量()
    {
        var res = ZoneAutoPlanner.Plan(Scope(
            Rect("A", 0, 0, 100, 100, face: "采装1#", vol: 12345),
            Rect("B", 600, 0, 100, 100, face: "采装1#", vol: 6789),
            Rect("C", 0, 600, 100, 100, region: "采场2", vol: 999)), null, cellM: 2.0);

        Assert.Equal(12345d + 6789 + 999, res.Proposals.Sum(p => p.VolumeM3), 3);
        Assert.DoesNotContain(res.Notes, s => s.Contains("记账漏洞"));
    }

    /// <summary>
    /// ZA9 候选名对得上推演的源 —— <b>包括带分块后缀的那些</b>。
    /// <para>判据直接调推演真正用的 <c>SimPlanScene.NameHit</c>，不照抄一份镜像：
    /// 抄一份的话那个函数一改，这里还是绿的，而推演里区域已经配不上源了。</para>
    /// </summary>
    [Fact]
    public void ZA9_候选名能被推演的配对判据认出来()
    {
        var res = ZoneAutoPlanner.Plan(Scope(
            Rect("A", 0, 0, 200, 200, face: "采装1#"),
            Rect("B", 900, 0, 100, 100, face: "采装1#")), null, cellM: 2.0);

        Assert.All(res.Proposals, p => Assert.True(SimPlanScene.NameHit(p.Name, "采装1#"),
            $"候选名「{p.Name}」对不上作业面「采装1#」—— 推演里这块区域会整期不动"));
    }

    /// <summary>ZA10 空输入不炸，并且说得出为什么没有候选。</summary>
    [Fact]
    public void ZA10_没有块段时不炸且有话说()
    {
        var res = ZoneAutoPlanner.Plan(new PlanScope { Month = "2026-08", Header = "没有排到本期的单元" }, null);
        Assert.Empty(res.Proposals);
        Assert.False(res.Ok);
        Assert.NotEmpty(res.Header);
    }

    /// <summary>ZA11 同名的既有区域走「改边界」而不是新增一块同名的（Z9）。</summary>
    [Fact]
    public void ZA11_同名既有区域是改边界不是新增()
    {
        var existing = new List<ZoneRecord>
        {
            new() { Id = 7, Name = "采装1#", Category = "pit",
                    Ring = { new ZonePoint(0, 0, 1200), new ZonePoint(10, 0, 1200), new ZonePoint(10, 10, 1200) } },
        };
        var res = ZoneAutoPlanner.Plan(Scope(Rect("A", 0, 0, 100, 100, face: "采装1#")), existing, cellM: 2.0);

        var p = Assert.Single(res.Proposals);
        Assert.NotNull(p.Existing);
        Assert.Equal(7L, p.Existing!.Id);
        Assert.Equal("改既有区域的边界", p.ActionText);
    }

    // ══════════════════════════════════════════════════════════════
    //  ZR：栅格件本身
    // ══════════════════════════════════════════════════════════════

    /// <summary>ZR1 栅格面积与真值的偏差在格距量级内（面积口径可信）。</summary>
    [Fact]
    public void ZR1_栅格面积贴近真值()
    {
        var poly = new[] { 0.0, 0, 300, 0, 300, 200, 0, 200 };     // 6 万 m²
        var u = ZoneRaster.Union(new List<double[]> { poly }, 1.0);
        var piece = Assert.Single(u.Pieces);
        Assert.InRange(piece.CellAreaM2, 60000 * 0.97, 60000 * 1.03);
        Assert.True(piece.Trustworthy, "规整矩形的描边必须可信");
    }

    /// <summary>ZR2 点数不足的输入不炸，且计入 Degenerate（不静默当成 0 块）。</summary>
    [Fact]
    public void ZR2_退化输入被记账而不是静默跳过()
    {
        var u = ZoneRaster.Union(new List<double[]> { new double[] { 0, 0, 1, 1 } });
        Assert.False(u.Ok);
        Assert.Equal(1, u.Degenerate);
        Assert.NotEmpty(u.Note);
    }

    // ══════════════════════════════════════════════════════════════
    //  ZE：手工调整（Z12 只改几何）
    // ══════════════════════════════════════════════════════════════

    private static List<ZonePoint> Square(double s = 100, double z = 1200) => new()
    {
        new ZonePoint(0, 0, z), new ZonePoint(s, 0, z), new ZonePoint(s, s, z), new ZonePoint(0, s, z),
    };

    /// <summary>ZE1 拖顶点只动那一个点，Z 原样保留。</summary>
    [Fact]
    public void ZE1_移动顶点保留高程且只动一个点()
    {
        var ring = Square();
        var res = ZoneEdit.MoveVertex(ring, 1, 150, 20);
        Assert.True(res.Ok);
        Assert.Equal(4, res.Ring.Count);
        Assert.Equal(150, res.Ring[1].X, 6);
        Assert.Equal(1200, res.Ring[1].Z, 6);
        Assert.Equal(ring[0], res.Ring[0]);
        Assert.Equal(ring[2], res.Ring[2]);
    }

    /// <summary>ZE2 插点的 Z 按该边两端内插（不去现状面重采，否则边界会起伏）。</summary>
    [Fact]
    public void ZE2_插点的高程按边内插()
    {
        var ring = new List<ZonePoint>
        {
            new(0, 0, 1000), new(100, 0, 1100), new(100, 100, 1100), new(0, 100, 1000),
        };
        var res = ZoneEdit.InsertVertex(ring, 0, 50, 0);
        Assert.True(res.Ok);
        Assert.Equal(5, res.Ring.Count);
        Assert.Equal(1050, res.Ring[1].Z, 6);
    }

    /// <summary>
    /// ZE3 删到只剩 3 个点就拒绝 —— 顶点 &lt;3 的区域会被推演装载时**静默跳过**
    /// （台账里看得见、推演里没有）。
    /// </summary>
    [Fact]
    public void ZE3_删点不许把区域删到不成环()
    {
        var tri = new List<ZonePoint> { new(0, 0, 1), new(10, 0, 1), new(10, 10, 1) };
        var res = ZoneEdit.DeleteVertex(tri, 0);
        Assert.False(res.Ok);
        Assert.Empty(res.Ring);
        Assert.Contains("静默跳过", res.Message);
    }

    /// <summary>ZE4 整体平移：面积与 Z 都不变。</summary>
    [Fact]
    public void ZE4_平移不改面积也不改高程()
    {
        var ring = Square();
        var res = ZoneEdit.Translate(ring, 37, -21);
        Assert.True(res.Ok);
        Assert.Equal(ZoneEdit.Area(ring), ZoneEdit.Area(res.Ring), 3);
        Assert.All(res.Ring, p => Assert.Equal(1200, p.Z, 6));
    }

    /// <summary>ZE5 等距外扩变大、内缩变小。</summary>
    [Fact]
    public void ZE5_等距外扩内缩的面积朝对的方向走()
    {
        var ring = Square(200);
        var outw = ZoneEdit.OffsetRing(ring, +10);
        var inw = ZoneEdit.OffsetRing(ring, -10);
        Assert.True(outw.Ok);
        Assert.True(inw.Ok);
        Assert.True(ZoneEdit.Area(outw.Ring) > ZoneEdit.Area(ring));
        Assert.True(ZoneEdit.Area(inw.Ring) < ZoneEdit.Area(ring));
    }

    /// <summary>
    /// ZE6 内缩过头要<b>拒绝</b>，而不是把区域悄悄收成一个点。
    /// <para><c>RingOffset</c> 在自交时会退化成一个收口小环 —— 那是推演表达"采空"用的，
    /// 当成编辑结果存回台账就是把这块区域抹掉，而界面上只是"变小了"。</para>
    /// </summary>
    [Fact]
    public void ZE6_内缩过头被拒绝而不是收成一个点()
    {
        var ring = Square(100);
        var res = ZoneEdit.OffsetRing(ring, -80);
        Assert.False(res.Ok);
        Assert.Empty(res.Ring);
        Assert.Contains("没有改动", res.Message);
    }

    /// <summary>ZE7 命中测试：容差之外一律不认（否则随手一点就把边界拖走了）。</summary>
    [Fact]
    public void ZE7_命中测试在容差外返回负一()
    {
        var ring = Square(100);
        Assert.Equal(0, ZoneEdit.HitVertex(ring, 2, 2, 5));
        Assert.Equal(-1, ZoneEdit.HitVertex(ring, 50, 50, 5));
        Assert.Equal(0, ZoneEdit.HitEdge(ring, 50, 1, 5));
        Assert.Equal(-1, ZoneEdit.HitEdge(ring, 50, 50, 5));
    }

    /// <summary>ZE8 重画只换几何：新环点数/面积换掉，旧环不被改写。</summary>
    [Fact]
    public void ZE8_重画只换几何()
    {
        var oldRing = Square(100);
        var newRing = Square(50);
        var res = ZoneEdit.Replace(oldRing, newRing);
        Assert.True(res.Ok);
        Assert.Equal(4, res.Ring.Count);
        Assert.Equal(2500, ZoneEdit.Area(res.Ring), 3);
        Assert.Equal(10000, ZoneEdit.Area(oldRing), 3);      // 原环没被就地改
    }

    // ══════════════════════════════════════════════════════════════
    //  ZP：范围来源（月度台账）—— 用临时台账目录，绝不碰桌面上那份真台账
    // ══════════════════════════════════════════════════════════════

    private static string TempLedger(params MiningUnitLedger.Row[] rows)
    {
        string root = Path.Combine(Path.GetTempPath(), "pm3d_zone_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "2026-08.csv"),
                          MiningUnitLedger.ToCsv(rows, "期次 2026-08"), new UTF8Encoding(true));
        return root;
    }

    private static MiningUnitLedger.Row LedgerRow(string id, string period, double cx, double cy)
        => new()
        {
            UnitId = id, Kind = LedgerKind.Coal, Region = "采场1", Seam = "4", Band = 1, Panel = 1,
            Cx = cx, Cy = cy, Cz = 1200, ZLo = 1190, ZHi = 1210,
            LengthM = 100, WidthM = 40, ThickM = 20,
            CoalM3 = 50000, Period = period, Status = "计划",
        };

    /// <summary>
    /// ZP1 <b>只认本期</b>（Z1）：没有期次 / 别的期次的行不算本期作业范围。
    /// <para>算进来就等于把全矿家底当成本月任务 —— 而生成出来的区域大得"看着也挺合理"。</para>
    /// </summary>
    [Fact]
    public void ZP1_只认本期期次的行()
    {
        string root = TempLedger(
            LedgerRow("4-B1-P1", "2026-08", 1000, 1000),
            LedgerRow("4-B1-P2", "2026-08", 1200, 1000),
            LedgerRow("4-B1-P3", "2026-07", 5000, 5000),     // 上一期
            LedgerRow("4-B1-P4", "", 9000, 9000));           // 基表里还没排产的
        try
        {
            var scope = ZonePlanSource.Load("2026-08", root);
            Assert.Equal(2, scope.RowsInMonth);
            Assert.Equal(2, scope.Blocks.Count);
            Assert.All(scope.Blocks, b => Assert.StartsWith("4-B1-P", b.UnitId));
            Assert.DoesNotContain(scope.Blocks, b => b.UnitId is "4-B1-P3" or "4-B1-P4");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>ZP2 没有这一期的台账时不炸，也**不拿基表全量顶替**，并说得出为什么。</summary>
    [Fact]
    public void ZP2_没有这一期的台账时说清楚而不是顶替()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm3d_zone_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var scope = ZonePlanSource.Load("2026-09", root);
            Assert.False(scope.Ok);
            Assert.Empty(scope.Blocks);
            Assert.Contains("没有月度台账", scope.Header);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    /// <summary>
    /// ZP3 没有真轨时退盒子，<b>并且把这件事说出来</b>（形态是近似的，不能拿去量边界）。
    /// </summary>
    [Fact]
    public void ZP3_没有真轨退盒子且如实说明()
    {
        string root = TempLedger(LedgerRow("4-B1-P1", "2026-08", 1000, 1000));
        try
        {
            var scope = ZonePlanSource.Load("2026-08", root);
            var b = Assert.Single(scope.Blocks);
            Assert.False(b.RealRail);
            Assert.True(b.FootprintXy.Length >= 8);
            Assert.Contains(scope.Notes, s => s.Contains("盒子"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ══════════════════════════════════════════════════════════════
    //  ZG：格网算子（膨胀 / 差集）—— 工序区域的地基
    //
    //  这两个算子是给「爆破警戒区 = 爆区外扩 R」和「推排带 = 全幅 − 卸载带」用的。
    //  两件事都**不许走多边形**：等距偏移在大偏移下会翻面（ZE6 记过一笔真账，
    //  面积比与收口守卫两条都判不出来），多边形布尔在自交输入上是几何雪崩（Z5）。
    //  所以这一组判的是：膨胀是**圆**的不是方的、边界截断**说得出来**、
    //  差集**格数守恒**、格网对不上时**拒绝相减**而不是悄悄换一片。
    // ══════════════════════════════════════════════════════════════

    /// <summary>指定格网上填一条矩形（左下角 + 宽高）。</summary>
    private static double[] RectXy(double x0, double y0, double w, double h)
        => new[] { x0, y0, x0 + w, y0, x0 + w, y0 + h, x0, y0 + h };

    /// <summary>
    /// ZG1 膨胀 R 米之后的面积 = 原面积 + 周长×R + πR²（闵可夫斯基和）。
    /// <para>这条同时钉死了"膨胀了多少"：实现少算一半半径、或按格数而不是米数膨胀，都会红。</para>
    /// </summary>
    [Fact]
    public void ZG1_膨胀面积等于闵可夫斯基和()
    {
        double w = 300, h = 200, r = 50;
        var g = ZoneRaster.Grid.Lattice(new List<double[]> { RectXy(0, 0, w, h) }, 1.0, r, out string why);
        Assert.True(g != null, why);
        g!.Fill(RectXy(0, 0, w, h));

        double want = w * h + 2 * (w + h) * r + Math.PI * r * r;
        double got = g.Dilate(r).AreaM2;
        Assert.InRange(got, want * 0.97, want * 1.03);
    }

    /// <summary>
    /// ZG1b 半径 0 时结果必须与原掩膜<b>逐格一致</b>。
    /// <para>F0 纪律的一条：关掉这个闸必须真的什么都不做。膨胀实现里少写一个 <c>&lt;=</c>
    /// 就会把整张图外扩一格，而面积只差 1% —— 靠 ZG1 的 3% 容差是抓不出来的。</para>
    /// </summary>
    [Fact]
    public void ZG1b_半径为零时逐格不变()
    {
        var g = ZoneRaster.Grid.Lattice(new List<double[]> { RectXy(0, 0, 100, 100) }, 1.0, 0, out _);
        g!.Fill(RectXy(0, 0, 100, 100));
        var d = g.Dilate(0);
        Assert.Equal(g.Count, d.Count);
        for (int i = 0; i < g.Mask.Length; i++) Assert.Equal(g.Mask[i], d.Mask[i]);
    }

    /// <summary>
    /// ZG2 膨胀出来的是<b>圆</b>不是方。
    /// <para>正方形的角外沿对角线量：距角 1.06R 的点必须在外面、0.85R 的点必须在里面。
    /// 换成切比雪夫（棋盘）距离的话前者会被覆盖 —— 300m 警戒半径在角上会多圈出 124m，
    /// 而轮廓仍是一条规规矩矩的闭合环，图上看不出来。</para>
    /// </summary>
    [Fact]
    public void ZG2_膨胀是圆的不是方的()
    {
        double s = 100, r = 50;
        var g = ZoneRaster.Grid.Lattice(new List<double[]> { RectXy(0, 0, s, s) }, 1.0, r, out _);
        g!.Fill(RectXy(0, 0, s, s));
        var d = g.Dilate(r);

        double k = 0.75;                                   // 对角偏移 → 实距 = 0.75·√2·R ≈ 1.06R
        Assert.False(d.At(s + r * k, s + r * k), "角外 1.06R 处被覆盖了 —— 膨胀成了方的（切比雪夫距离）");
        double k2 = 0.6;                                   // 实距 = 0.6·√2·R ≈ 0.85R
        Assert.True(d.At(s + r * k2, s + r * k2), "角外 0.85R 处没被覆盖 —— 膨胀半径给小了");
    }

    /// <summary>
    /// ZG3 膨胀顶到格网边界要<b>报出来</b>。
    /// <para>顶到 = 警戒区被截断，而截断后的轮廓仍是一条闭合环，面积也"看着挺合理"。
    /// 不留证据的话，人会拿一块少了一角的警戒区去清场。</para>
    /// </summary>
    [Fact]
    public void ZG3_膨胀顶到格网边界要报出来()
    {
        var poly = RectXy(0, 0, 100, 100);
        var tight = ZoneRaster.Grid.Lattice(new List<double[]> { poly }, 1.0, 0, out _);   // 不预留
        tight!.Fill(poly);
        Assert.True(tight.Dilate(60).ClippedAtBorder, "没预留就膨胀 60m，必须报截断");

        var roomy = ZoneRaster.Grid.Lattice(new List<double[]> { poly }, 1.0, 60, out _);  // 预留够
        roomy!.Fill(poly);
        Assert.False(roomy.Dilate(60).ClippedAtBorder, "预留够了不该报截断");
    }

    /// <summary>
    /// ZG4 差集格数守恒：全幅 = 前带 + （全幅 − 前带）。
    /// <para>推排带就是这么算出来的。守恒破了说明有格子被算了两遍或漏掉，
    /// 而两条带各自的面积都还"看着对"。</para>
    /// </summary>
    [Fact]
    public void ZG4_差集格数守恒()
    {
        var all = RectXy(0, 0, 300, 100);
        var tip = RectXy(0, 0, 300, 25);                    // 卸载带：前 25m
        var g = ZoneRaster.Grid.Lattice(new List<double[]> { all }, 1.0, 0, out _);

        var gAll = g!.Blank(); gAll.Fill(all);
        var gTip = g.Blank(); gTip.Fill(tip);
        var gDoze = gAll.Minus(gTip, out string why);
        Assert.True(gDoze != null, why);

        Assert.Equal(gAll.Count, gTip.Count + gDoze!.Count);
        Assert.Equal(0, gTip.Minus(gAll, out _)!.Count);    // 前带整个含在全幅里
    }

    /// <summary>
    /// ZG5 推排带落在<b>后方</b>，不是随便剩下的一块。
    /// <para>判的是位置不是面积：把差集写反（全幅 − 后带）面积一样对，位置整个翻到前边。</para>
    /// </summary>
    [Fact]
    public void ZG5_推排带落在后方()
    {
        var g = ZoneRaster.Grid.Lattice(new List<double[]> { RectXy(0, 0, 300, 100) }, 1.0, 0, out _);
        var gAll = g!.Blank(); gAll.Fill(RectXy(0, 0, 300, 100));
        var gTip = g.Blank(); gTip.Fill(RectXy(0, 0, 300, 25));

        var piece = Assert.Single(gAll.Minus(gTip, out _)!.Trace().Pieces);
        double lo = piece.Ring.Min(p => p.Y), hi = piece.Ring.Max(p => p.Y);
        Assert.InRange(lo, 23, 28);                         // 贴着卸载带的后沿起
        Assert.InRange(hi, 97, 102);
    }

    /// <summary>
    /// ZG6 两张不在同一片格网上的掩膜<b>拒绝相减</b>，并说清为什么。
    /// <para>各自 Union 一次再相减是这里最容易犯的错：两次自适应格距不同、原点也不同，
    /// 减出来的边缘全是锯齿状假空洞 —— 而它是一张能画、能量面积、看着完全正常的图。</para>
    /// </summary>
    [Fact]
    public void ZG6_格网对不上时拒绝相减()
    {
        var a = ZoneRaster.Grid.Lattice(new List<double[]> { RectXy(0, 0, 300, 100) }, 1.0, 0, out _);
        var b = ZoneRaster.Grid.Lattice(new List<double[]> { RectXy(0, 0, 300, 100) }, 2.0, 0, out _);
        Assert.Null(a!.Minus(b, out string why));
        Assert.Contains("同一片格网", why);
    }

    /// <summary>ZG7 空掩膜膨胀仍是空 —— 不许凭空长出一块地。</summary>
    [Fact]
    public void ZG7_空掩膜膨胀仍是空()
    {
        var g = ZoneRaster.Grid.Lattice(new List<double[]> { RectXy(0, 0, 100, 100) }, 1.0, 50, out _);
        Assert.False(g!.Blank().Dilate(50).Any);
    }
}
