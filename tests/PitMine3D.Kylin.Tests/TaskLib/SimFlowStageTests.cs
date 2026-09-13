// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SimFlowStageTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  车流示意（SimFlowStage）离线验收台架 —— 不需要三维内核
//
//  写法纪律（照 HaulRouteStageCheck 同一套）：**每条判据都能证伪**。
//  凡是只判「跑通了」的判据都会空过，所以每条都带一个必然让它红的反例或对照组。
//
//  最要紧的一条是 F2（不许取 mod）：
//    取 mod 的实现在 n<1 时会让一台车绕圈跑 ⇒ 线上**永远有 1 个点**，
//    把强度夸大到 1/n 倍，而画面看不出任何异常、其余判据全绿。
//    所以 F2 不判「有没有点」，判的是「一个发车周期内，有的时刻 0 个、有的时刻 1 个」，
//    并配一个 n>2 的对照组要求「任何时刻都 ≥2 个」—— 两条一起才关得住这个洞。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SimFlowStageTests
{
    // ── 合成算例：一条 2000 m 的平路（首末同高 ⇒ 净坡 0，车速取平路值）──
    private const double L = 2000.0;

    private static SimHaulPath Path(double tonnage, bool coal = false, double dz = 0, string unit = "U1")
    {
        // 5 点等距，便于「点必须落在折线上」那条判据用线段插值复核
        var xyz = new double[5 * 3];
        for (int i = 0; i < 5; i++)
        {
            xyz[3 * i] = 1000 + L * i / 4.0;
            xyz[3 * i + 1] = 1000;
            xyz[3 * i + 2] = 100 + dz * i / 4.0;
        }
        return new SimHaulPath
        {
            UnitId = unit,
            DestinationCode = "D1",
            DestinationName = "外排场",
            IsCoal = coal,
            TonnageT = tonnage,
            Xyz = xyz,
            LengthM = Math.Sqrt(L * L + dz * dz),
        };
    }

    /// <summary>载重 100 t、期作业 100 h ⇒ λ = 吨量/10000 车/h。数字凑成整的，手算能复核。</summary>
    private static SimFlowParams P(double scale = 1.0, bool empty = false) => new()
    {
        TruckPayloadT = 100,
        PayloadSource = "台架",
        WorkHoursPerPeriod = 100,
        WorkHoursSource = "台架",
        SpeedSource = "台架",
        DisplayScale = scale,
        ShowEmptyRun = empty,
        MaxParticles = 10000,
        ChevronSpacingM = 0,          // 箭头与本组判据无关，关掉省得干扰段数统计
    };

    private static (SimFlowStage Stage, RecordingDynamicOverlay Sink) New(SimFlowParams p)
    {
        var sink = new RecordingDynamicOverlay();
        var st = new SimFlowStage(sink) { Params = p };
        return (st, sink);
    }

    private static int PointsAt(SimFlowStage st, RecordingDynamicOverlay sink, double hours)
    {
        st.Tick(hours);
        return sink.StateOf(SimFlowStage.LoadedGroup)?.Count ?? 0;
    }

    // ── F1 在途车数就是 Little 定律，不是显示参数 ──────────────────────────────
    // 反例守卫：把吨量翻倍，n 必须跟着翻倍。只判「n>0」的话，一个写死常数的实现也能过。
    [Fact]
    public void F1_OnRoadCount_IsLittlesLaw_AndScalesWithTonnage()
    {
        var (st, _) = New(P());
        var r = st.Rebuild("2026-08", new[] { Path(50000), Path(100000, unit: "U2") });

        Assert.Equal(2, r.PathUsed);
        Assert.True(r.Balanced, "分类账不自洽：有分支漏计。");

        var a = r.Lines.Single(l => l.UnitId == "U1");
        var b = r.Lines.Single(l => l.UnitId == "U2");

        // λ = T /(W·H)：50000/(100·100) = 5 车/h；100000/… = 10 车/h
        Assert.Equal(5.0, a.RealTripsPerHour, 6);
        Assert.Equal(10.0, b.RealTripsPerHour, 6);

        // n = λ·t_重，且 t_重 = 折线长 / 重车速度 —— 两个数必须严格互推
        Assert.Equal(a.TripsPerHour * (a.LoadedMin / 60.0), a.OnRoadLoaded, 9);
        // 吨量翻倍 ⇒ n 翻倍（同一条路，t 不变）
        Assert.Equal(2.0, b.OnRoadLoaded / a.OnRoadLoaded, 6);

        // 车头间距 Δs = L / n —— Little 的距离形式，与上面是同一个数
        Assert.Equal(a.PolyLengthM / a.OnRoadLoaded, a.HeadwayM, 6);
    }

    // ── F2 n<1 必须「时有时无」；取 mod 的实现在这里必红 ─────────────────────
    [Fact]
    public void F2_SparseLine_IsIntermittent_NotAlwaysOne()
    {
        // λ = 1 车/h、L=2000m、重车 25km/h ⇒ t_重 = 0.08 h ⇒ n = 0.08 台
        // 也就是「每小时过一辆，路上只有 4.8 分钟有车」。
        var (st, sink) = New(P());
        var r = st.Rebuild("2026-08", new[] { Path(10000) });
        var line = Assert.Single(r.Lines);
        Assert.True(line.OnRoadLoaded < 1.0, $"算例没造出 n<1（实际 {line.OnRoadLoaded}）—— 判据会空过。");

        // 在一个发车周期（1/λ = 1 h）内密集采样
        int zero = 0, some = 0;
        for (int i = 0; i < 200; i++)
        {
            int n = PointsAt(st, sink, 3.0 + i / 200.0);
            if (n == 0) zero++; else some++;
        }
        Assert.True(zero > 0, "n<1 的线在整个发车周期里**一直有车** —— 这正是「取了 mod」的症状："
                            + "一台车绕圈跑，把强度夸大到 1/n 倍。");
        Assert.True(some > 0, "n<1 的线一辆车都没出现过 —— 发车窗口算错了。");

        // 有车的时间占比应当 ≈ n（这才是「强度没被夸大」的正面证据）
        double duty = (double)some / (zero + some);
        Assert.InRange(duty, line.OnRoadLoaded * 0.5, line.OnRoadLoaded * 2.0 + 0.02);
    }

    /// <summary>F2 的对照组：n>2 时任何时刻都必须 ≥2 个点。缺了它，「一个点都不画」也能让 F2 过。</summary>
    [Fact]
    public void F2b_DenseLine_IsAlwaysPopulated()
    {
        // λ = 100 车/h ⇒ n = 8 台
        var (st, sink) = New(P());
        var r = st.Rebuild("2026-08", new[] { Path(1_000_000) });
        Assert.True(r.Lines[0].OnRoadLoaded > 2.0);

        for (int i = 0; i < 100; i++)
            Assert.True(PointsAt(st, sink, 5.0 + i / 100.0) >= 2, "n>2 的线出现过 <2 个点。");
    }

    /// <summary>
    /// 一段时间窗内画出来的点数的**时间平均**。
    /// <para>不能拿瞬时点数比：一个长 T 的发车窗里落几个发车时刻，天然是 floor(λT) 或 +1，
    /// 瞬时值带 ±1 的相位抖动。而**时间平均恰好等于 Little 的 n** —— 所以比平均值
    /// 不但更稳，还顺手把「渲染出来的东西真的满足 Little 定律」这条钉住了。</para>
    /// <para>★ 采样步长刻意与车头时距**不同周期**：发车窗两端是闭区间，样本正好落在发车时刻上时
    /// 会多数一个（零测度事件）。若采样周期与车头时距成整数比，这个零测度事件会被
    /// **系统性地**命中固定比例的样本，均值稳定偏高约 1/每周期样本数
    /// （实测 500 样本铺满 50 个车头时距 ⇒ 8 被测成 8.1）。那是判据的采样假象，不是实现的偏差。</para>
    /// </summary>
    private static double MeanPoints(SimFlowStage st, RecordingDynamicOverlay sink,
                                     double t0, double span, int samples = 997)
    {
        double sum = 0;
        for (int i = 0; i < samples; i++) sum += PointsAt(st, sink, t0 + span * i / samples);
        return sum / samples;
    }

    // ── F3 时间平均点数 = Little 的 n；显示倍率成比例放大它，且不改真数 ──────
    [Fact]
    public void F3_RenderedDensity_EqualsLittlesN_AndScalesWithDisplayFactor()
    {
        var (s1, k1) = New(P());
        var (s2, k2) = New(P(scale: 4));
        var r1 = s1.Rebuild("p", new[] { Path(1_000_000) });
        var r2 = s2.Rebuild("p", new[] { Path(1_000_000) });

        // 真数不受倍率影响（报给人的 λ 必须还是真的）
        Assert.Equal(r1.TotalTripsPerHour, r2.TotalTripsPerHour, 9);

        // ★ 画出来的点数（时间平均）必须就是 n —— 这是「密度不是显示参数」的正面证据。
        //   按**相对误差**判：端点双含 + 有限采样注定有零点几个点的残差（见 MeanPoints 的说明），
        //   要求逐位相等只会把判据变成对采样网格的测试。
        double n1 = r1.Lines[0].OnRoadLoaded, n2 = r2.Lines[0].OnRoadLoaded;
        double m1 = MeanPoints(s1, k1, 7.0003, 0.4999);
        double m2 = MeanPoints(s2, k2, 7.0003, 0.4999);

        Assert.InRange(m1 / n1, 0.97, 1.03);
        Assert.InRange(m2 / n2, 0.97, 1.03);
        Assert.InRange(m2 / m1, 3.88, 4.12);

        // 且必须留条说明「一个点不再等于一台车」
        Assert.Contains(r2.Notes, s => s.Contains("显示倍率"));
    }

    // ── F4 空车更快 ⇒ 在途空车数必须比重车少（两边取同一个数是错的）──────────
    [Fact]
    public void F4_EmptyRun_HasItsOwnSpeedAndCount()
    {
        var (st, _) = New(P(empty: true));
        var r = st.Rebuild("p", new[] { Path(500_000) });
        var l = r.Lines[0];
        Assert.True(l.EmptyMin < l.LoadedMin, "空车行车时间没有比重车短 —— 速度模型没按 loaded 区分。");
        Assert.True(l.OnRoadEmpty < l.OnRoadLoaded, "在途空车数不小于重车数 —— n空=λ·t空 的 t 取错了。");
    }

    // ── F5 输入缺来源 ⇒ 整体标「示意」（不是静默用缺省）────────────────────
    [Fact]
    public void F5_MissingSource_MarksResultAsIndicativeOnly()
    {
        var p = P();
        p.WorkHoursSource = "";                 // 只抽掉一项
        var (st, _) = New(p);
        var r = st.Rebuild("p", new[] { Path(100_000) });

        Assert.False(p.Resolved);
        Assert.Contains(r.Notes, s => s.Contains("缺省值"));

        // 对照组：三项都有来源时不许再标
        var (st2, _) = New(P());
        var r2 = st2.Rebuild("p", new[] { Path(100_000) });
        Assert.DoesNotContain(r2.Notes, s => s.Contains("强度输入里有缺省值"));
    }

    // ── F6 分类账：每条路径落进且只落进一个桶 ───────────────────────────────
    [Fact]
    public void F6_Ledger_IsBalanced_ForEveryBucket()
    {
        var bad = new SimHaulPath { UnitId = "X", TonnageT = 100, Xyz = new double[3] };   // 1 个点 ⇒ 无几何
        var zero = Path(0, unit: "Z");                                                     // 吨量 0
        var ok = Path(100_000, unit: "K");

        var (st, _) = New(P());
        var r = st.Rebuild("p", new[] { bad, zero, ok });

        Assert.Equal(3, r.PathTotal);
        Assert.Equal(1, r.PathNoGeometry);
        Assert.Equal(1, r.PathNoTonnage);
        Assert.Equal(1, r.PathUsed);
        Assert.True(r.Balanced);
        // 每个桶都要有话说 —— 一个不吭声的桶等于静默丢弃
        Assert.Contains(r.Notes, s => s.Contains("点数不足"));
        Assert.Contains(r.Notes, s => s.Contains("吨量为 0"));
    }

    // ── F7 帧代价与车数无关：每帧每组只推一次 ────────────────────────────────
    [Fact]
    public void F7_OnePushPerGroupPerFrame_RegardlessOfCount()
    {
        var (st, sink) = New(P());
        st.Rebuild("p", new[] { Path(2_000_000), Path(2_000_000, unit: "U2") });
        int callsAfterRebuild = sink.Calls;

        st.Tick(1.0);
        int perFrame = sink.Calls - callsAfterRebuild;

        int drawn = sink.StateOf(SimFlowStage.LoadedGroup)?.Count ?? 0;
        Assert.True(drawn > 20, $"算例没造出足够多的点（{drawn}）—— 判据会空过。");
        // 重载 1 次 + 空驶那一组 1 次（关着也要撤一次），与点数无关
        Assert.InRange(perFrame, 1, 2);
    }

    // ── F8 点必须落在折线上（不是插在两点之间的直线上）──────────────────────
    [Fact]
    public void F8_ParticlesLieOnThePolyline()
    {
        // 折线故意带高差：错误实现若按平面长插值，z 会对不上
        var (st, sink) = New(P());
        st.Rebuild("p", new[] { Path(1_000_000, dz: 120) });
        st.Tick(3.3);
        var push = sink.StateOf(SimFlowStage.LoadedGroup)!;
        Assert.True(push.Count > 0);

        for (int i = 0; i < push.Count; i++)
        {
            double x = push.Xyz[3 * i], y = push.Xyz[3 * i + 1], z = push.Xyz[3 * i + 2];
            Assert.Equal(1000.0, y, 6);                       // 折线整条 y=1000
            Assert.InRange(x, 1000 - 1e-6, 1000 + L + 1e-6);
            // 抬升量已知（缺省 3 m），z 必须落在 [100, 220] + 3
            double t = (x - 1000) / L;
            Assert.Equal(100 + 120 * t + st.Params.LiftM, z, 3);
        }
    }

    // ── F9 点只前进不倒退，且前进速度就是重车速度 ───────────────────────────
    //
    // ★ 必须在**稀疏线**上比（n<1 ⇒ 路上最多一台车，且两台之间有大段空窗）：
    //   密线上「点数不变」并不意味着是同一台车 —— 领头的驶出、后面的补进来，
    //   最靠前那个点的坐标会正常地变小。拿密线比会红，而红的是判据不是代码
    //   （第一版就是这么写的）。稀疏线上「连续两帧都恰好 1 个点」必是同一台车。
    [Fact]
    public void F9_ParticleAdvancesMonotonically_AtLoadedSpeed()
    {
        var (st, sink) = New(P());
        var r = st.Rebuild("p", new[] { Path(10_000) });        // λ=1 车/h ⇒ n≈0.08
        var line = r.Lines[0];
        Assert.True(line.OnRoadLoaded < 0.5, "算例不够稀疏，判据会退化。");

        double dt = 0.0005;                                      // h
        double expectStep = line.LoadedMps * dt * 3600.0;        // 每步应走的米数
        double prev = double.NaN; int compared = 0;

        for (int i = 0; i <= 4000; i++)
        {
            st.Tick(3.0 + i * dt);
            var p = sink.StateOf(SimFlowStage.LoadedGroup)!;
            if (p.Count != 1) { prev = double.NaN; continue; }   // 空窗期：下一段重新起算
            double s = p.Xyz[0] - 1000;                          // 折线沿 +X，弧长 = x−x0
            if (!double.IsNaN(prev))
            {
                Assert.True(s >= prev - 1e-6, $"点在第 {i} 帧倒退了（{prev} → {s}）。");
                Assert.Equal(expectStep, s - prev, 3);           // 速度必须就是重车速度
                compared++;
            }
            prev = s;
        }
        Assert.True(compared > 20, $"只比到 {compared} 次 —— 判据没真正跑起来。");
    }

    // ── F10 关掉车流后必须真的撤掉那两组（不是留着上一帧的车不动）───────────
    [Fact]
    public void F10_Clear_RemovesBothGroups()
    {
        var (st, sink) = New(P(empty: true));
        st.Rebuild("p", new[] { Path(1_000_000) });
        st.Tick(2.0);
        Assert.True((sink.StateOf(SimFlowStage.LoadedGroup)?.Count ?? 0) > 0);

        st.Clear();
        Assert.Equal(0, sink.StateOf(SimFlowStage.LoadedGroup)!.Count);
        Assert.Equal(0, sink.StateOf(SimFlowStage.EmptyGroup)!.Count);
        Assert.Equal(0, sink.StateOf(SimFlowStage.ChevronGroup)!.Count);
    }

    // ── F11 λ 的分母无效时不画，且说清楚是参数问题不是几何问题 ───────────────
    [Fact]
    public void F11_ZeroPayloadOrHours_DrawsNothing_AndSaysWhy()
    {
        var p = P();
        p.WorkHoursPerPeriod = 0;
        var (st, sink) = New(p);
        var r = st.Rebuild("p", new[] { Path(1_000_000) });

        Assert.Empty(r.Lines);
        Assert.Equal(0, PointsAt(st, sink, 1.0));
        Assert.Contains(r.Notes, s => s.Contains("分母为零"));
        Assert.Contains("载重或期作业小时无效", r.Summary);
    }

    // ── F12 流向箭头：开了才有、间距变大段数变少（不是画了就算过）────────────
    [Fact]
    public void F12_Chevrons_RespondToSpacing()
    {
        var pa = P(); pa.ChevronSpacingM = 250;
        var pb = P(); pb.ChevronSpacingM = 1000;
        var (sa, ka) = New(pa);
        var (sb, kb) = New(pb);
        var ra = sa.Rebuild("p", new[] { Path(100_000) });
        var rb = sb.Rebuild("p", new[] { Path(100_000) });

        Assert.True(ra.ChevronSegments > rb.ChevronSegments,
                    $"间距调大后箭头段数没变少（{ra.ChevronSegments} vs {rb.ChevronSegments}）。");
        Assert.Equal(ra.ChevronSegments, ka.StateOf(SimFlowStage.ChevronGroup)!.Count);
        Assert.Equal(rb.ChevronSegments, kb.StateOf(SimFlowStage.ChevronGroup)!.Count);
    }
}
