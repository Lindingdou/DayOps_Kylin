// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/ProcessZoneTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.TaskLib.Zoning;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

/// <summary>
/// 「工序作业区」的判据（P 组规则）。
///
/// <para><b>这一层钉的是 P0</b>：同一个作业面、同一个月，五道工序的区域<b>不是同一块地</b>。
/// 把五道工序共用一份占地，界面上看不出任何异常 —— 每一块的面积都对、轮廓都规整，
/// 只是钻机被派到了电铲脚下、警戒区圈住了早就采完的地。所以下面每一条都判**位置**或**关系**，
/// 不只判面积：面积对而位置错，是这一层最典型的失败。</para>
///
/// <para><b>喂的是合成算例</b>：矩形占地的面积、闵可夫斯基和、带宽都能手算，
/// 所以每一条都能证伪 —— 把穿孔窗口关掉、把警戒区改成方的、把差集写反，对应那条必红。</para>
/// </summary>
public class ProcessZoneTests
{
    // ══════════════════════════════════════════════════════════════
    //  夹具
    // ══════════════════════════════════════════════════════════════

    /// <summary>月作业日固定 25 天 —— 超前期换算成量要除它，不钉死判据就不可复现。</summary>
    private const double Workdays = 25;

    /// <summary>
    /// 一个矩形采掘单元。<paramref name="cx"/>/<paramref name="cy"/> 是质心，
    /// 长沿 X（走向）、宽沿 Y（推进），方位角为 null ⇒ 与 BoxPlanFootprint 的退路一致。
    /// </summary>
    private static PlanBlock Blk(string id, LedgerKind kind, double cx, double cy,
                                 double lengthM, double widthM, double vol,
                                 int seq = 0, bool blast = true, string face = "面A",
                                 double z = 1200)
    {
        double x0 = cx - lengthM / 2, x1 = cx + lengthM / 2;
        double y0 = cy - widthM / 2, y1 = cy + widthM / 2;
        var b = new PlanBlock
        {
            UnitId = id,
            Kind = kind,
            Region = kind == LedgerKind.Dump ? "外排土场" : "采场1",
            Seam = kind == LedgerKind.Dump ? "L1" : "岩1200",
            FaceZone = face,
            FootprintXy = new[] { x0, y0, x1, y0, x1, y1, x0, y1 },
            TopXyz = new[] { x0, y0, z, x1, y0, z, x1, y1, z, x0, y1, z },
            RealRail = false,
            HasAzimuth = false,
            VolumeM3 = vol,
            Seq = seq,
            NeedsBlasting = blast,
            BlastFromMaterial = true,
            WidthM = widthM,
            LengthM = lengthM,
            Cx = cx, Cy = cy, ZHi = z,
            AzimuthDeg = null,
            TowardCrest = kind != LedgerKind.Dump,
        };
        return b;
    }

    private static PlanScope Scope(params PlanBlock[] blocks)
    {
        var s = new PlanScope { Month = "2026-08", Header = "算例" };
        s.Blocks.AddRange(blocks);
        s.RowsInMonth = blocks.Length;
        return s;
    }

    private static ProcessZoneOptions Opt(double lead = 0, double guard = 300, double tip = 25,
                                          double cell = 1.0)
    {
        var o = new ProcessZoneOptions
        {
            CellM = cell, BlastLeadDays = lead, GuardRadiusM = guard,
            TipBandWidthM = tip, MonthWorkdays = Workdays,
        };
        return o;
    }

    private static IEnumerable<ProcessZoneProposal> Of(ProcessZonePlanResult r, string proc)
        => r.Of(proc);

    private static double Area(ProcessZonePlanResult r, string proc)
        => r.Of(proc).Sum(p => p.AreaM2);

    // ══════════════════════════════════════════════════════════════
    //  P1 采装区
    // ══════════════════════════════════════════════════════════════

    /// <summary>P1 采装区 = 本期单元占地并集，面积对得上。</summary>
    [Fact]
    public void P1_采装区是本期单元的占地并集()
    {
        var r = ProcessZonePlanner.Plan(
            Scope(Blk("U1", LedgerKind.Rock, 50, 50, 100, 100, 5000),
                  Blk("U2", LedgerKind.Rock, 150, 50, 100, 100, 5000)),
            Opt());

        var load = Assert.Single(Of(r, ProcessZone.ProcLoad));   // 两块搭边 ⇒ 一个连通块
        Assert.InRange(load.AreaM2, 20000 * 0.97, 20000 * 1.03);
        Assert.Equal(2, load.Blocks.Count);
        Assert.Equal("原位实方", load.Basis);
        Assert.Equal("电铲", load.EquipRole);
    }

    // ══════════════════════════════════════════════════════════════
    //  P2 穿孔窗口（一维前推）
    // ══════════════════════════════════════════════════════════════

    private static PlanScope FourInARow()
        => Scope(Blk("U1", LedgerKind.Rock, 50, 50, 100, 100, 1000, seq: 1),
                 Blk("U2", LedgerKind.Rock, 150, 50, 100, 100, 1000, seq: 2),
                 Blk("U3", LedgerKind.Rock, 250, 50, 100, 100, 1000, seq: 3),
                 Blk("U4", LedgerKind.Rock, 350, 50, 100, 100, 1000, seq: 4));

    /// <summary>
    /// P2a 超前 0 工日时穿孔区 == 采装区。
    /// <para>F0 纪律：关掉这个闸必须真的什么都不做。前推的实现里少一个边界判断，
    /// 就会在 lead=0 时也切掉队头一个单元 —— 而面积只差 25%，看着"也挺合理"。</para>
    /// </summary>
    [Fact]
    public void P2a_超前为零时穿孔区等于采装区()
    {
        var r = ProcessZonePlanner.Plan(FourInARow(), Opt(lead: 0));
        var drill = Assert.Single(Of(r, ProcessZone.ProcDrill));
        var load = Assert.Single(Of(r, ProcessZone.ProcLoad));
        Assert.Equal(4, drill.Blocks.Count);
        Assert.InRange(drill.AreaM2, load.AreaM2 * 0.99, load.AreaM2 * 1.01);
    }

    /// <summary>
    /// P2b 超前 lead 工日 ⇒ 队头 lead/工日 那部分量被切掉（那是<b>上期</b>打的孔）。
    /// <para>4 个单元各 1000m³、25 个工日、超前 9.375 日 ⇒ V_lead = 1500 ⇒
    /// 队头 U1（占 [0,1000)）整个落在窗外，U2（占 [1000,2000)）跨界整取。</para>
    /// <para>判的是<b>哪一个</b>被切掉，不只是"少了一块"：按 Seq 倒序排队的话个数一样、面积一样，
    /// 而钻机会被派到反方向。</para>
    /// </summary>
    [Fact]
    public void P2b_超前前推切掉队头且切的是排产序最小的那个()
    {
        var r = ProcessZonePlanner.Plan(FourInARow(), Opt(lead: 9.375));
        var drill = Assert.Single(Of(r, ProcessZone.ProcDrill));
        Assert.Equal(3, drill.Blocks.Count);
        Assert.DoesNotContain(drill.Blocks, b => b.UnitId == "U1");
        Assert.Contains(drill.Blocks, b => b.UnitId == "U2");
        Assert.Contains(drill.Blocks, b => b.UnitId == "U4");
    }

    /// <summary>
    /// P2c 越过本期末尾的那一段要<b>如实报缺</b>，不拿本期的顶上去。
    /// <para>不报的话，"这个月的孔打完了"与"这个月的孔只够打到 25 号"长得一模一样。</para>
    /// </summary>
    [Fact]
    public void P2c_越过本期末尾的那段要报缺()
    {
        var r = ProcessZonePlanner.Plan(FourInARow(), Opt(lead: 9.375));
        Assert.Contains(r.Notes, s => s.Contains("落在**下期**"));
        Assert.Contains(r.Notes, s => s.Contains("下一期还没排产"));
    }

    /// <summary>
    /// P2d 台账没排产序时要<b>说出来</b>，而不是当成"超前期为 0"。
    /// <para>Seq 全为 0 ⇒ 队列排不出来，穿孔区的位置不代表真实采掘顺序。
    /// 这与"超前期填了 0"生成出来的区域一模一样，必须靠文案分开。</para>
    /// </summary>
    [Fact]
    public void P2d_没有排产序要点名并给补法()
    {
        var s = Scope(Blk("U1", LedgerKind.Rock, 50, 50, 100, 100, 1000),
                      Blk("U2", LedgerKind.Rock, 150, 50, 100, 100, 1000));
        var r = ProcessZonePlanner.Plan(s, Opt(lead: 5));
        Assert.Contains(r.Notes, x => x.Contains("都没有排产序") && x.Contains("采掘单元清单"));
    }

    // ══════════════════════════════════════════════════════════════
    //  P3 免爆
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// P3 免爆的面<b>没有</b>穿孔区，也<b>没有</b>警戒区。
    /// <para>不筛这一条会在免爆面上一本正经地圈出一块钻机作业区 —— 而所有数字都正常。</para>
    /// </summary>
    [Fact]
    public void P3_免爆的面没有穿孔区也没有警戒区()
    {
        var r = ProcessZonePlanner.Plan(
            Scope(Blk("C1", LedgerKind.Coal, 50, 50, 100, 100, 1000, seq: 1, blast: false),
                  Blk("C2", LedgerKind.Coal, 150, 50, 100, 100, 1000, seq: 2, blast: false)),
            Opt(lead: 3));

        Assert.Empty(Of(r, ProcessZone.ProcDrill));
        Assert.Empty(Of(r, ProcessZone.ProcBlastGuard));
        Assert.Single(Of(r, ProcessZone.ProcLoad));               // 采装区照出
        Assert.Contains(r.Notes, s => s.Contains("没有穿孔区"));
    }

    /// <summary>P3b 面里混着爆与不爆时，只有需爆破的那些进穿孔区，且把排除掉几个说出来。</summary>
    [Fact]
    public void P3b_混合面只取需爆破的单元()
    {
        var r = ProcessZonePlanner.Plan(
            Scope(Blk("R1", LedgerKind.Rock, 50, 50, 100, 100, 1000, seq: 1, blast: true),
                  Blk("C1", LedgerKind.Coal, 150, 50, 100, 100, 1000, seq: 2, blast: false)),
            Opt(lead: 0));

        var drill = Assert.Single(Of(r, ProcessZone.ProcDrill));
        Assert.Equal(new[] { "R1" }, drill.Blocks.Select(b => b.UnitId).ToArray());
        Assert.Contains(r.Notes, s => s.Contains("个单元免爆，已排除"));
    }

    // ══════════════════════════════════════════════════════════════
    //  P4 / P5 爆破警戒区
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// P4 警戒区面积 = 穿孔区闵可夫斯基和（原面积 + 周长×R + πR²）。
    /// <para>把膨胀改成切比雪夫（棋盘）距离的话，100m 见方外扩 50m 会得到 200×200=40000
    /// 而不是 37854 —— 差 5.7%，这条判得出来。</para>
    /// </summary>
    [Fact]
    public void P4_警戒区面积等于闵可夫斯基和()
    {
        var r = ProcessZonePlanner.Plan(
            Scope(Blk("R1", LedgerKind.Rock, 50, 50, 100, 100, 1000, seq: 1)),
            Opt(lead: 0, guard: 50));

        var guard = Assert.Single(Of(r, ProcessZone.ProcBlastGuard));
        double want = 100 * 100 + 2 * (100 + 100) * 50 + Math.PI * 50 * 50;
        Assert.InRange(guard.AreaM2, want * 0.97, want * 1.03);
        Assert.Equal(50, guard.GuardRadiusM);
    }

    /// <summary>
    /// P5 警戒区<b>按炮次分，不按作业面</b>：两个面挨得近就并成一块，并说出它盖住了哪几个面。
    /// <para>硬套「分组键 = 作业面」会得到两块各自不完整的地，而每一块看着都挺规整 ——
    /// 于是放炮时只撤了一个面的人。</para>
    /// </summary>
    [Fact]
    public void P5_警戒区跨面合并并点名盖住了哪几个面()
    {
        var s = Scope(Blk("A1", LedgerKind.Rock, 50, 50, 100, 100, 1000, seq: 1, face: "面A"),
                      Blk("B1", LedgerKind.Rock, 450, 50, 100, 100, 1000, seq: 1, face: "面B"));
        // 两块相距 300m（边缘间隔 300−100=300m）；R=200 ⇒ 两侧各扩 200 ⇒ 连上
        var r = ProcessZonePlanner.Plan(s, Opt(lead: 0, guard: 200, cell: 2.0));

        Assert.Equal(2, Of(r, ProcessZone.ProcDrill).Count());     // 穿孔区仍按面分成两块
        var guard = Assert.Single(Of(r, ProcessZone.ProcBlastGuard));
        Assert.Contains("面A", guard.Name);
        Assert.Contains("面B", guard.Name);
        Assert.Contains(guard.Issues, i => i.Contains("跨 2 个作业面"));
    }

    /// <summary>P5b 两个面离得远时警戒区分成两块（不求外包轮廓，中间那片不作业的地不圈进来）。</summary>
    [Fact]
    public void P5b_离得远的两个面各自一块警戒区()
    {
        var s = Scope(Blk("A1", LedgerKind.Rock, 50, 50, 100, 100, 1000, seq: 1, face: "面A"),
                      Blk("B1", LedgerKind.Rock, 1050, 50, 100, 100, 1000, seq: 1, face: "面B"));
        var r = ProcessZonePlanner.Plan(s, Opt(lead: 0, guard: 100, cell: 2.0));
        Assert.Equal(2, Of(r, ProcessZone.ProcBlastGuard).Count());
    }

    /// <summary>
    /// P5c 警戒区是<b>禁入区</b>：不派设备、没有量口径、且把这件事写在 Issues 上。
    /// <para>给它一个 0 的量会被下游 SUM 进工程量 —— 而 0 是合法值，没有任何东西会报错。</para>
    /// </summary>
    [Fact]
    public void P5c_警戒区不派设备且没有量()
    {
        var r = ProcessZonePlanner.Plan(
            Scope(Blk("R1", LedgerKind.Rock, 50, 50, 100, 100, 1000, seq: 1)),
            Opt(lead: 0, guard: 50));

        var guard = Assert.Single(Of(r, ProcessZone.ProcBlastGuard));
        Assert.True(guard.IsKeepOut);
        Assert.Equal("", guard.EquipRole);
        Assert.Null(guard.VolumeM3);                              // ★ 不是 0
        Assert.Contains(guard.Issues, i => i.Contains("禁入区"));
    }

    // ══════════════════════════════════════════════════════════════
    //  P6 / P7 / P8 排土两条带
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// P6 卸载带 = 靠坡顶线那一侧、宽 w 的前缘带 —— <b>判位置不只判面积</b>。
    /// <para>把带切到后方去面积一模一样，而卡车会被指到推土机的位置上。</para>
    /// </summary>
    [Fact]
    public void P6_卸载带贴在前缘且宽度对得上()
    {
        var r = ProcessZonePlanner.Plan(
            Scope(Blk("D1", LedgerKind.Dump, 150, 50, 300, 100, 30000)),
            Opt(tip: 25));

        var tip = Assert.Single(Of(r, ProcessZone.ProcDumpTip));
        Assert.InRange(tip.AreaM2, 300 * 25 * 0.94, 300 * 25 * 1.06);
        double lo = tip.Ring.Min(p => p.Y), hi = tip.Ring.Max(p => p.Y);
        Assert.InRange(lo, -2, 3);                                 // 贴着 y=0 那条前缘
        Assert.InRange(hi, 23, 28);
        Assert.Equal("卡车", tip.EquipRole);
        Assert.Equal("排弃占容", tip.Basis);
    }

    /// <summary>
    /// P7 卸载带 + 推排带 = 全幅（面积守恒），且推排带在后方。
    /// <para>差集写反的话两块面积照样加得起来，位置整个翻过来 —— 所以位置也要判。</para>
    /// </summary>
    [Fact]
    public void P7_两条带加起来等于全幅且推排带在后方()
    {
        var r = ProcessZonePlanner.Plan(
            Scope(Blk("D1", LedgerKind.Dump, 150, 50, 300, 100, 30000)),
            Opt(tip: 25));

        double tip = Area(r, ProcessZone.ProcDumpTip);
        double doze = Area(r, ProcessZone.ProcDumpDoze);
        Assert.InRange(tip + doze, 30000 * 0.96, 30000 * 1.04);

        var d = Assert.Single(Of(r, ProcessZone.ProcDumpDoze));
        Assert.InRange(d.Ring.Min(p => p.Y), 23, 28);              // 从卸载带后沿起
        Assert.InRange(d.Ring.Max(p => p.Y), 97, 103);
        Assert.Equal("推土机", d.EquipRole);
    }

    /// <summary>
    /// P8 幅宽 ≤ 卸载带宽 ⇒ 整幅都是卸载带、<b>没有推排带</b>，并点名报出来。
    /// <para>切一条 0.3m 宽的推排带会让人以为推土机有地方站 —— 那块地在图上还挺像样。</para>
    /// </summary>
    [Fact]
    public void P8_幅宽不够时不切零宽推排带且点名()
    {
        var r = ProcessZonePlanner.Plan(
            Scope(Blk("D1", LedgerKind.Dump, 150, 10, 300, 20, 6000)),
            Opt(tip: 25));

        Assert.Single(Of(r, ProcessZone.ProcDumpTip));
        Assert.Empty(Of(r, ProcessZone.ProcDumpDoze));
        Assert.Contains(r.Notes, s => s.Contains("推进宽 ≤ 卸载带宽") && s.Contains("D1"));
    }

    // ══════════════════════════════════════════════════════════════
    //  P9 / P10 / P11
    // ══════════════════════════════════════════════════════════════

    /// <summary>P9 运输<b>不出</b>面状区域 —— 结果里不许有第六类。</summary>
    [Fact]
    public void P9_运输不出面状区域()
    {
        var r = ProcessZonePlanner.Plan(
            Scope(Blk("R1", LedgerKind.Rock, 50, 50, 100, 100, 1000, seq: 1),
                  Blk("D1", LedgerKind.Dump, 150, 250, 300, 100, 30000)),
            Opt());

        Assert.All(r.Proposals, p => Assert.Contains(p.Process, ProcessZone.AllProcesses));
        Assert.Contains(r.Notes, s => s.Contains("运输没有面状区域"));
    }

    /// <summary>
    /// P10 解不出高程的候选<b>不勾、不入库</b>，且 ZSource 为空。
    /// <para>写 0 会被下游当成实测高程（只有零顶点才返回 NaN），层体整体摆到 0 米而界面正常。</para>
    /// </summary>
    [Fact]
    public void P10_解不出高程的候选拒绝入库()
    {
        var b = Blk("R1", LedgerKind.Rock, 50, 50, 100, 100, 1000, seq: 1);
        b.TopXyz = Array.Empty<double>();                          // 模型侧一个 Z 都没有
        var r = ProcessZonePlanner.Plan(Scope(b), Opt(lead: 0, guard: 0));

        var load = Assert.Single(Of(r, ProcessZone.ProcLoad));
        Assert.Equal("", load.ZSource);
        Assert.False(load.Selected);
        Assert.Contains(load.Issues, i => i.Contains("解不出高程"));

        ProcessZoneStore.Apply("2026-08", new[] { load }, out int ins, out int upd, out var fails);
        Assert.Equal(0, ins);
        Assert.Equal(0, upd);
        Assert.Contains(fails, f => f.Contains("解不出高程"));
    }

    /// <summary>
    /// P11 每一块都记账：期次 / 工序 / 格距 / Z 出处 / 单元清单，全在 note 里。
    /// <para>三个月后有人问"这条边界哪来的"，答案得在库里，不能只在当时那个状态栏上。</para>
    /// </summary>
    [Fact]
    public void P11_note里记着期次工序格距和Z出处()
    {
        var r = ProcessZonePlanner.Plan(
            Scope(Blk("R1", LedgerKind.Rock, 50, 50, 100, 100, 1000, seq: 1)),
            Opt(lead: 2, guard: 50));

        var drill = Assert.Single(Of(r, ProcessZone.ProcDrill));
        Assert.Contains("2026-08", drill.Note);
        Assert.Contains("穿孔", drill.Note);
        Assert.Contains("格距", drill.Note);
        Assert.Contains("穿爆超前 2", drill.Note);
        Assert.Contains("R1", drill.Note);
        Assert.Contains("Z：", drill.Note);
    }

    /// <summary>
    /// P12 三个参数（超前 / 警戒半径 / 卸载带宽）都不在台账里，必须写进结果文案。
    /// <para>它们改了区域就变，而库里存的是生成当时的值 —— 不说清楚，
    /// 三个月后没人知道这批区域是按哪组参数出的。</para>
    /// </summary>
    [Fact]
    public void P12_三个参数写进结果文案()
    {
        var r = ProcessZonePlanner.Plan(
            Scope(Blk("R1", LedgerKind.Rock, 50, 50, 100, 100, 1000, seq: 1)),
            Opt(lead: 4, guard: 250, tip: 30));

        Assert.Contains(r.Notes, s => s.Contains("穿爆超前 4") && s.Contains("警戒半径 250")
                                   && s.Contains("卸载带宽 30"));
    }

    /// <summary>
    /// P13 工序码、显示名、设备角色、量口径四张表<b>一一对得上</b>，没有哪一个落空。
    /// <para>加一道工序只改了枚举没改映射的话，界面上会出现一个显示成原始英文码、
    /// 派不出设备、量口径为空的区域 —— 而它照样能入库。</para>
    /// </summary>
    [Fact]
    public void P13_工序码的四张映射表都不落空()
    {
        foreach (var p in ProcessZone.AllProcesses)
        {
            Assert.NotEqual(p, ProcessZone.DisplayName(p));        // 有中文名，不是把原码抛回来
            if (ProcessZone.IsKeepOut(p))
            {
                Assert.Equal("", ProcessZone.EquipRole(p));        // 禁入区不派设备
                Assert.Equal("", ProcessZone.VolumeBasis(p));      // 也没有量口径
            }
            else
            {
                Assert.NotEqual("", ProcessZone.EquipRole(p));
                Assert.NotEqual("", ProcessZone.VolumeBasis(p));
            }
            Assert.NotEqual(0x808080u, ProcessZone.Color(p));      // 有专属色，不是兜底灰
        }
    }

    /// <summary>
    /// P14 三个量口径<b>绝不许并成一列</b>：穿孔=控制方量、采装=原位实方、排土=排弃占容。
    /// <para>混进同一个「量」列，谁一 SUM 就得到一个不对应任何真实量的数，且不会有任何东西报错。</para>
    /// </summary>
    [Fact]
    public void P14_三个量口径互不相同()
    {
        var bases = new[] { ProcessZone.ProcDrill, ProcessZone.ProcLoad, ProcessZone.ProcDumpTip }
            .Select(ProcessZone.VolumeBasis).ToList();
        Assert.Equal(3, bases.Distinct().Count());
        Assert.Equal(ProcessZone.VolumeBasis(ProcessZone.ProcDumpTip),
                     ProcessZone.VolumeBasis(ProcessZone.ProcDumpDoze));   // 排土两条带同一口径
    }

    /// <summary>P15 没有块段时不炸，且有话说。</summary>
    [Fact]
    public void P15_没有块段时不炸且有话说()
    {
        var r = ProcessZonePlanner.Plan(new PlanScope { Month = "2026-08", Header = "空" }, Opt());
        Assert.False(r.Ok);
        Assert.Empty(r.Proposals);
        Assert.NotEmpty(r.Header);
    }
}
