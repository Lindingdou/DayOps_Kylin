// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/RegionAdvanceAzimuthTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.TaskLib.Adjust;
using PitMine3D.Kylin.TaskLib.Domain;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  推进方位角（RegionAdvanceAzimuth）的判据
//
//  它回答的是「这块地往哪个方向推」，答错的后果是轮廓朝着一个不存在的方向平移，
//  而画面上看起来仍然像那么回事 —— 这类错只有判据抓得住。
//
//  ── 判据清单 ──
//   Z0 非退化  ：合成算例真的解出方位（否则后面几条空过）。
//   Z1 方向对  ：单元沿 +X 排开 ⇒ 0°；沿 +Y ⇒ 90°；沿 −X ⇒ 180°。
//                约定必须与 RingOffset 一致（0°=+X、90°=+Y、逆时针正），差 90° 就是整体转错。
//   Z2 不靠名字：单元的 Region 字段**故意填错**，仍按质心落在轮廓内配上。
//   Z3 框外不算：质心在轮廓外的单元不参与（把远处另一个采场的顺序算进来就全反了）。
//   Z4 单元不足：< 2 个 ⇒ 不解，且说清是几个。
//   Z5 走位太短：单元挤成一堆 ⇒ 不解（方向是噪声），且给出实测位移。
//   Z6 示意图形：区域坐标是本包生成的 ⇒ 不解（拿台账套它没有意义）。
//   Z7 体积加权：大方量单元把半程质心拉过去 —— 换权重结果必须变。
//   Z8 顺序驱动：把 Seq 倒过来，方位必须反向 180°（证明用的是顺序不是别的）。
//   Z9 联动    ：解不出时 StageSimPlayer 必须落到「全周等距」并在 Notes 里说出来。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class RegionAdvanceAzimuthTests
{
    /// <summary>2000×1000 的矩形区域，覆盖单元排布的整条走位。</summary>
    private static SimRegion Region(bool synthetic = false) => new()
    {
        Name = "试验采场", Category = "pit", Z = 100, Synthetic = synthetic,
        Ring = { new SimPoint(0, 0), new SimPoint(2000, 0), new SimPoint(2000, 1000), new SimPoint(0, 1000) },
    };

    /// <summary>n 个单元，从 (x0,y0) 起每步 (dx,dy)，Seq 递增，等方量。</summary>
    private static List<MiningUnitLedger.Row> Units(int n, double x0, double y0, double dx, double dy,
                                                    double volEach = 100_000, string period = "2026-08")
    {
        var list = new List<MiningUnitLedger.Row>();
        for (int i = 0; i < n; i++)
            list.Add(new MiningUnitLedger.Row
            {
                UnitId = $"U{i:00}", Region = "台账里写的名字", Period = period, Seq = i,
                Cx = x0 + dx * i, Cy = y0 + dy * i, Cz = 100, ZLo = 94, ZHi = 106,
                Kind = LedgerKind.Rock, GrossM3 = volEach,
            });
        return list;
    }

    private static double Norm(double deg) { deg %= 360; return deg < 0 ? deg + 360 : deg; }

    // ═══════════════════════ 判据 ═══════════════════════

    /// <summary>Z0 非退化。</summary>
    [Fact]
    public void Z0_Resolves_OnAWellFormedCase()
    {
        var info = RegionAdvanceAzimuth.Resolve(Region(), Units(6, 200, 500, 250, 0));

        Assert.True(info.Resolved, "算例没解出方位：" + info.SourceLabel);
        Assert.Equal(6, info.UnitCount);
        Assert.True(info.TravelM >= RegionAdvanceAzimuth.MinTravelM);
        Assert.NotEmpty(info.SourceLabel);
    }

    /// <summary>Z1 方向对，且约定与 RingOffset 一致（0°=+X、90°=+Y、逆时针正）。</summary>
    [Theory]
    [InlineData(250, 0, 0)]        // 往东
    [InlineData(0, 150, 90)]       // 往北
    [InlineData(-250, 0, 180)]     // 往西
    [InlineData(0, -150, 270)]     // 往南
    public void Z1_Azimuth_MatchesTravelDirection(double dx, double dy, double expectDeg)
    {
        // 起点挪到区域中央，保证正反两个方向的单元都还落在轮廓内
        var info = RegionAdvanceAzimuth.Resolve(Region(), Units(6, 1000, 500, dx / 5, dy / 5));

        Assert.True(info.Resolved, info.SourceLabel);
        double diff = Math.Abs(Norm(info.AzimuthDeg - expectDeg));
        if (diff > 180) diff = 360 - diff;
        Assert.True(diff < 1.0, $"方位 {info.AzimuthDeg:0.#}° 与期望 {expectDeg}° 差了 {diff:0.#}°");
    }

    /// <summary>Z2 配对靠质心几何，不靠名字 —— Region 字段填的是完全无关的名字也照样配上。</summary>
    [Fact]
    public void Z2_MatchesByCentroid_NotByRegionName()
    {
        var rows = Units(6, 200, 500, 250, 0);
        Assert.All(rows, r => Assert.NotEqual("试验采场", r.Region));   // 名字确实对不上

        var info = RegionAdvanceAzimuth.Resolve(Region(), rows);
        Assert.True(info.Resolved);
        Assert.Equal(6, info.UnitCount);
    }

    /// <summary>Z3 质心落在轮廓外的单元不参与 —— 把远处另一个采场的顺序算进来方向就全反了。</summary>
    [Fact]
    public void Z3_UnitsOutsideTheRing_AreIgnored()
    {
        var inside = Units(4, 200, 500, 250, 0);                       // 往东
        var outside = Units(4, 50_000, 50_000, 0, -300, period: "2026-09");  // 远处、往南
        foreach (var (r, i) in outside.Select((r, i) => (r, i))) r.Seq = 100 + i;

        var info = RegionAdvanceAzimuth.Resolve(Region(), inside.Concat(outside).ToList());

        Assert.True(info.Resolved);
        Assert.Equal(4, info.UnitCount);                               // 只数了框内那 4 个
        double diff = Math.Abs(Norm(info.AzimuthDeg - 0));
        if (diff > 180) diff = 360 - diff;
        Assert.True(diff < 1.0, $"远处单元把方位带偏到了 {info.AzimuthDeg:0.#}°");
    }

    /// <summary>Z4 单元不足 2 个：不解，且说清框内有几个、台账共几个。</summary>
    [Fact]
    public void Z4_TooFewUnits_DoesNotResolve()
    {
        var info = RegionAdvanceAzimuth.Resolve(Region(), Units(1, 200, 500, 250, 0));

        Assert.False(info.Resolved);
        Assert.Contains("采掘单元", info.SourceLabel);
        Assert.Contains("等距", info.SourceLabel);
    }

    /// <summary>Z5 单元挤成一堆：走位是噪声，不解，并把实测位移写出来。</summary>
    [Fact]
    public void Z5_DegenerateTravel_DoesNotResolve()
    {
        // 每步 1 m，6 个单元 ⇒ 半程质心位移远小于阈值
        var info = RegionAdvanceAzimuth.Resolve(Region(), Units(6, 1000, 500, 1, 0));

        Assert.False(info.Resolved);
        Assert.Contains("走位", info.SourceLabel);
        Assert.Contains("等距", info.SourceLabel);
    }

    /// <summary>Z6 示意图形区域不解 —— 它的坐标是本包生成的，与台账不在一套系里。</summary>
    [Fact]
    public void Z6_SyntheticRegion_DoesNotResolve()
    {
        var info = RegionAdvanceAzimuth.Resolve(Region(synthetic: true), Units(6, 200, 500, 250, 0));

        Assert.False(info.Resolved);
        Assert.Contains("示意图形", info.SourceLabel);
    }

    /// <summary>
    /// Z7 体积加权：半程质心按方量加权。
    /// <para>⚠ 不平衡必须制造在<b>半程内部</b>：把「后半段整体加重」是没用的 ——
    /// 两个半程各自算各自的质心，组内权重仍然均匀，加权与否结果一模一样。
    /// 第一版就是这么写的，测例空过了才发现。这里把两端各压一个重单元，
    /// 半程质心分别被拉向两头 ⇒ 走位变长。</para>
    /// </summary>
    [Fact]
    public void Z7_CentroidIsVolumeWeighted()
    {
        var even = Units(6, 200, 500, 250, 0);
        var skew = Units(6, 200, 500, 250, 0);
        skew[0].GrossM3 = 900_000;      // 前半程里最靠后的那个压重 ⇒ 起点质心往回拉
        skew[5].GrossM3 = 900_000;      // 后半程里最靠前的那个压重 ⇒ 终点质心往前拉

        var a = RegionAdvanceAzimuth.Resolve(Region(), even);
        var b = RegionAdvanceAzimuth.Resolve(Region(), skew);

        Assert.True(a.Resolved && b.Resolved);
        Assert.True(b.TravelM > a.TravelM + 1.0,
            $"加权后走位 {b.TravelM:0.#} m 没有超过等权的 {a.TravelM:0.#} m ⇒ 质心没有按方量加权");
    }

    /// <summary>Z8 顺序驱动：把 Seq 倒过来，方位必须反向 180° —— 证明用的是采掘顺序。</summary>
    [Fact]
    public void Z8_ReversingSeq_FlipsAzimuth()
    {
        var fwd = Units(6, 200, 500, 250, 0);
        var rev = Units(6, 200, 500, 250, 0);
        for (int i = 0; i < rev.Count; i++) rev[i].Seq = 100 - i;      // 顺序颠倒，位置不变

        var a = RegionAdvanceAzimuth.Resolve(Region(), fwd);
        var b = RegionAdvanceAzimuth.Resolve(Region(), rev);

        Assert.True(a.Resolved && b.Resolved);
        double diff = Math.Abs(Norm(a.AzimuthDeg - b.AzimuthDeg));
        if (diff > 180) diff = 360 - diff;
        Assert.True(diff > 179, $"倒转采掘顺序后方位只差 {diff:0.#}°（应为 180°）—— 说明方位不是从顺序来的");
    }

    // ── 定序档位 ─────────────────────────────────────────────────────────────
    //  Seq（推进序）是**人填/算法填**的计划列：FromStrips / FromDumpCells 都不设它。
    //  模型新算出来的台账整表 Seq 全是 0 —— 这是常态，不是异常。
    //  下面三条钉的就是「此时不许拿 CSV 行序冒充推进顺序」。

    /// <summary>
    /// Z10 <b>整表 Seq 全为 0 且带号/幅号也全一样</b> ⇒ 排不出先后 ⇒ 不解。
    /// <para>这一条是本类最要紧的判据：不加它的话，稳定排序会退化成 CSV 行序，
    /// 而结果会被理直气壮地写成「由采掘顺序反算」—— 一个假的方向配一句假的出处。</para>
    /// </summary>
    [Fact]
    public void Z10_NoOrderingInformation_DoesNotResolve()
    {
        var rows = Units(6, 200, 500, 250, 0);
        foreach (var r in rows) { r.Seq = 0; r.Band = 1; r.Panel = 1; }   // 三个键全一样

        var info = RegionAdvanceAzimuth.Resolve(Region(), rows);

        Assert.False(info.Resolved);
        Assert.Contains("推进序", info.SourceLabel);
        Assert.Contains("等距", info.SourceLabel);
    }

    /// <summary>
    /// Z11 Seq 全 0 时**即使带号有变化也不许解**。
    /// <para>这条是被真台账 A/B 逼出来的（<c>RealAzimuthDiagnosticTests</c>，2026-08-11）：
    /// 同一份台账上「人排推进序」与「模型带号序」给出的方位，2016-08 差 25.4°、
    /// <b>2026-08 差 125.2°</b>。带号是几何生成序，真实推进会跳带、会折返。
    /// 退用带号 = 在「没人排推进序」时把轮廓推向一个错方向，而画面上照样平行推进、
    /// 看着完全正常。上一版就是这么写的，靠合成算例根本发现不了。</para>
    /// </summary>
    [Fact]
    public void Z11_BandOrder_IsNotAcceptedAsAdvanceOrder()
    {
        var rows = Units(6, 200, 500, 250, 0);
        for (int i = 0; i < rows.Count; i++) { rows[i].Seq = 0; rows[i].Band = i; }  // 带号有序，推进序没排

        var info = RegionAdvanceAzimuth.Resolve(Region(), rows);

        Assert.False(info.Resolved, "带号有序就解出方位了 ⇒ 带号序又被当成推进序了");
        Assert.Contains("推进序", info.SourceLabel);
        Assert.Contains("带号", info.SourceLabel);      // 必须说清为什么不退用带号
        Assert.Contains("等距", info.SourceLabel);
    }

    /// <summary>Z12 推进序有变化时正常解，且方位跟着 Seq 走（与带号相反也一样）。</summary>
    [Fact]
    public void Z12_AzimuthFollowsPlannedSeq_NotBand()
    {
        var rows = Units(6, 200, 500, 250, 0);
        for (int i = 0; i < rows.Count; i++) { rows[i].Seq = 100 - i; rows[i].Band = i; }  // 两者相反

        var info = RegionAdvanceAzimuth.Resolve(Region(), rows);

        Assert.True(info.Resolved);
        Assert.Equal(RegionAdvanceAzimuth.OrderKey.PlannedSeq, info.OrderedBy);
        double diff = Math.Abs(Norm(info.AzimuthDeg - 180));   // 跟 Seq（倒序）走 ⇒ 往西
        if (diff > 180) diff = 360 - diff;
        Assert.True(diff < 1.0, $"方位 {info.AzimuthDeg:0.#}° 跟着带号走了，没听人排的推进序");
    }

    /// <summary>Z9 解不出时演示必须落到「全周等距」，并在 Notes 里说出来（不静默）。</summary>
    [Fact]
    public void Z9_Player_FallsBackToUniform_AndSaysSo()
    {
        var rec = new RecordingDynamicOverlay();
        SimDynamicOverlay.Bind(rec);
        try
        {
            var p = new StageSimPlayer(rec);
            p.Reload();
            p.Regions.Regions.Clear();
            // 示意图形 ⇒ 方位必定解不出，走的是确定的降级路径
            p.Regions.Regions.Add(Region(synthetic: true));

            var tl = new DayStageTimeline { ActualDayIndex = 0 };
            for (int i = 0; i < 3; i++)
                tl.Days.Add(new DayPlan
                {
                    Date = new DateTime(2026, 8, 1).AddDays(i), IsWorkday = true,
                    Stages = { new DayStage
                    {
                        Date = new DateTime(2026, 8, 1).AddDays(i), RegionName = "试验采场",
                        Process = ProcessType.Load, TargetVolumeM3 = 120_000, EquipCount = 3,
                    } },
                });

            var cell = StageGanttModel.From(tl).Groups
                .SelectMany(g => g.Lanes).SelectMany(l => l.Cells).First(c => c.HasWork);
            var info = p.Play(cell, tl, 1.0);

            Assert.True(info.Ok);
            Assert.False(info.Azimuth.Resolved);
            Assert.Contains(info.Notes, n => n.Contains("全周等距"));
            Assert.DoesNotContain(info.Notes, n => n.Contains("定向平移"));
        }
        finally { SimDynamicOverlay.Bind(null); }
    }
}
