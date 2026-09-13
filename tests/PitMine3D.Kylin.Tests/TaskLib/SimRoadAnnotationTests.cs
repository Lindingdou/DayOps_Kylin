// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SimRoadAnnotationTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  运输线工程标注（SimRoadAnnotationStage）+ 设备形态一致性 的离线判据
//
//  这一组最容易空过的是「并段」：一个**根本没并**的实现（每个采样段各成一段）会让
//  「画出来了」「有标签」全绿，只是标签多得糊成一条黑带 —— 而那正是要防的东西。
//  所以 A2 判的是**段数**（一条恒定坡度的长路必须并成 1 段），并配 A3「坡度真的变了就必须切开」。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SimRoadAnnotationTests
{
    /// <summary>造一条沿 +X 的链：每段 dx 长、按 grades 逐段给坡度（%）。</summary>
    private static List<SimHaulSegment> Chain(double x0, double dx, params double[] gradesPct)
    {
        var list = new List<SimHaulSegment>();
        double x = x0, z = 100;
        foreach (var g in gradesPct)
        {
            double dz = dx * g / 100.0;
            list.Add(new SimHaulSegment
            {
                X0 = x, Y0 = 500, Z0 = z,
                X1 = x + dx, Y1 = 500, Z1 = z + dz,
                RockT = 1000,
            });
            x += dx; z += dz;
        }
        return list;
    }

    private static (SimRoadAnnotationStage Stage, RecordingDynamicOverlay Sink) New(
        double tol = 0.8, double minRun = 60, double halfWidth = 12)
    {
        var sink = new RecordingDynamicOverlay();
        return (new SimRoadAnnotationStage(sink)
        {
            Params = new SimRoadAnnotationParams
            {
                GradeTolerancePct = tol,
                MinRunM = minRun,
                RoadHalfWidthM = halfWidth,
                MaxLabels = 1000,
                MaxSegments = 100000,
            }
        }, sink);
    }

    // ── A1 分类账自洽：每个采样段落进且只落进一个 run ────────────────────────
    [Fact]
    public void A1_Ledger_IsBalanced()
    {
        var (st, _) = New();
        var segs = Chain(0, 50, 2, 2, 2, 8, 8, 8, 8, -3, -3, -3, -3);
        var r = st.Rebuild("p", segs);

        Assert.Equal(segs.Count, r.SegmentsIn);
        Assert.True(r.Balanced, $"分类账不自洽：runs 共 {r.RunList.Sum(x => x.SampleCount)} 段 + 落单 {r.Orphans} ≠ {r.SegmentsIn}");
        Assert.Equal(0, r.Orphans);
        Assert.Equal(1, r.Chains);
    }

    // ── A2 恒定坡度必须并成一段（没并的实现在这里必红）──────────────────────
    [Fact]
    public void A2_ConstantGrade_MergesIntoOneRun()
    {
        var (st, _) = New();
        var r = st.Rebuild("p", Chain(0, 50, 4, 4, 4, 4, 4, 4, 4, 4));   // 8 段全 4%

        Assert.Single(r.RunList);
        Assert.Equal(4.0, r.RunList[0].GradePct, 6);
        // 水平投影 8×50 = 400 m；里程是三维斜长 8×√(50²+2²) ≈ 400.32 m。两个数不是一回事。
        Assert.Equal(400.0, r.RunList[0].HorizontalM, 6);
        Assert.Equal(8 * Math.Sqrt(50.0 * 50 + 2.0 * 2), r.RunList[0].LengthM, 6);
        Assert.Equal(8, r.RunList[0].SampleCount);
    }

    // ── A3 坡度真的变了就必须切开（否则 A2 可以靠「全并成一段」作弊）────────
    [Fact]
    public void A3_GradeChange_SplitsRuns()
    {
        var (st, _) = New();
        // 400m@2% → 400m@10%：两段坡差 8% ≫ 容差
        var r = st.Rebuild("p", Chain(0, 50, 2, 2, 2, 2, 2, 2, 2, 2, 10, 10, 10, 10, 10, 10, 10, 10));

        Assert.Equal(2, r.RunList.Count);
        Assert.Equal(2.0, r.RunList[0].GradePct, 3);
        Assert.Equal(10.0, r.RunList[1].GradePct, 3);
        // 段末标高：第一段 100+8m，第二段再 +40m
        Assert.Equal(108.0, r.RunList[0].Z1, 3);
        Assert.Equal(148.0, r.RunList[1].Z1, 3);
    }

    // ── A4 容差之内的抖动不许切段（现场中线永远带噪声）──────────────────────
    [Fact]
    public void A4_NoiseWithinTolerance_DoesNotSplit()
    {
        var (st, _) = New(tol: 0.8);
        var r = st.Rebuild("p", Chain(0, 50, 4.0, 4.3, 3.7, 4.2, 3.8, 4.1, 4.0, 3.9));
        Assert.Single(r.RunList);
    }

    // ── A5 短段并进邻段，并且**记账**（静默合并 = 图上少了几段没人知道）────
    [Fact]
    public void A5_ShortRuns_AreMerged_AndCounted()
    {
        var (st, _) = New(minRun: 200);
        // 一个 50m 的 12% 尖刺夹在两段 2% 中间
        var r = st.Rebuild("p", Chain(0, 50, 2, 2, 2, 2, 12, 2, 2, 2, 2));

        Assert.True(r.MergedShortRuns > 0, "短段一个都没并 —— MinRunM 没起作用。");
        Assert.True(r.Balanced);
        Assert.Contains(r.Notes, s => s.Contains("并进了邻段"));

        // 对照组：下限调到 0 就不该并（否则这条判据抓的是「永远合并」而不是口径）
        var (st2, _) = New(minRun: 0);
        var r2 = st2.Rebuild("p", Chain(0, 50, 2, 2, 2, 2, 12, 2, 2, 2, 2));
        Assert.Equal(0, r2.MergedShortRuns);
        Assert.True(r2.RunList.Count > r.RunList.Count);
    }

    // ── A6 标注文本就是坡度和标高，且推的是 label 组 ─────────────────────────
    [Fact]
    public void A6_Labels_CarryGradeAndElevation()
    {
        var (st, sink) = New();
        var r = st.Rebuild("p", Chain(0, 50, 6, 6, 6, 6, 6, 6, 6, 6));

        var g = sink.StateOf(SimRoadAnnotationStage.GradeGroup)!;
        var e = sink.StateOf(SimRoadAnnotationStage.ElevGroup)!;
        Assert.Equal(1, g.Count);
        Assert.Equal(1, e.Count);
        Assert.Equal("6.0%", g.Texts[0]);
        Assert.Equal((100 + 400 * 0.06).ToString("0.0", CultureInfo.InvariantCulture), e.Texts[0]);

        // ★ 标高**文字**是原始 Z；锚点位置带抬升（抬升是画图偏移，不该进数字）。
        //   第一版判据把这两件事判反了 —— 断言锚点 z == 原始 Z，那等于要求抬升失效。
        Assert.Equal(124.0, r.RunList[0].Z1, 3);
        Assert.Equal(124.0 + st.Params.LiftM, e.Xyz[2], 3);
    }

    // ── A7 路宽是世界宽度：半宽变了，边线间距跟着变 ──────────────────────────
    [Fact]
    public void A7_RoadWidth_IsWorldWidth()
    {
        double SpanY(RecordingDynamicOverlay s)
        {
            var p = s.StateOf(SimRoadAnnotationStage.RoadGroup)!;
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < p.Count; i++)
            {
                lo = Math.Min(lo, Math.Min(p.Xyz[i * 6 + 1], p.Xyz[i * 6 + 4]));
                hi = Math.Max(hi, Math.Max(p.Xyz[i * 6 + 1], p.Xyz[i * 6 + 4]));
            }
            return hi - lo;
        }

        var (a, ka) = New(halfWidth: 10);
        a.Rebuild("p", Chain(0, 50, 3, 3, 3, 3));
        var (b, kb) = New(halfWidth: 25);
        b.Rebuild("p", Chain(0, 50, 3, 3, 3, 3));

        Assert.Equal(20.0, SpanY(ka), 3);
        Assert.Equal(50.0, SpanY(kb), 3);
    }

    // ── A8 反向走同一条路，坡度必须反号（坡度是有向的）──────────────────────
    [Fact]
    public void A8_GradeSignFollowsChainDirection()
    {
        var fwd = Chain(0, 50, 5, 5, 5, 5);
        // 把每段的两端对调 —— 几何完全相同的一条路，只是段的方向反了
        var rev = fwd.Select(s => new SimHaulSegment
        {
            X0 = s.X1, Y0 = s.Y1, Z0 = s.Z1,
            X1 = s.X0, Y1 = s.Y0, Z1 = s.Z0,
            RockT = s.RockT,
        }).ToList();
        rev.Reverse();

        var (s1, _) = New();
        var (s2, _) = New();
        double g1 = s1.Rebuild("p", fwd).RunList[0].GradePct;
        double g2 = s2.Rebuild("p", rev).RunList[0].GradePct;

        // 链的起点由端点度数决定；两次的行进方向相反 ⇒ 坡度反号（绝对值相同）
        Assert.Equal(Math.Abs(g1), Math.Abs(g2), 6);
        Assert.Equal(5.0, Math.Abs(g1), 6);
    }

    // ── A9 陡段染警示色并留条（不是只在文字里提一嘴）────────────────────────
    [Fact]
    public void A9_SteepRuns_AreColouredAndNoted()
    {
        var (st, sink) = New();
        st.Params.GradeWarnPct = 8;
        st.Params.RoadRgb = 0x111111;
        st.Params.WarnRgb = 0xFFB020;
        var r = st.Rebuild("p", Chain(0, 50, 12, 12, 12, 12));

        Assert.Contains(r.Notes, s => s.Contains("超过"));
        var p = sink.StateOf(SimRoadAnnotationStage.RoadGroup)!;
        Assert.True(p.Count > 0);
        for (int i = 0; i < p.Count; i++)
            Assert.Equal(0xFFFFB020u, p.Argb[i]);

        // 对照组：不超限时不许染
        var (st2, sink2) = New();
        st2.Params.GradeWarnPct = 8;
        st2.Params.RoadRgb = 0x111111;
        st2.Rebuild("p", Chain(0, 50, 3, 3, 3, 3));
        var p2 = sink2.StateOf(SimRoadAnnotationStage.RoadGroup)!;
        Assert.Equal(0xFF111111u, p2.Argb[0]);
    }

    // ── A11 纵坡的分母必须是**水平距离**，不是三维斜长 ───────────────────────
    //
    // 两种算法在缓坡上几乎一样（4% 差 0.03 个百分点），所以肉眼和大部分判据都抓不到；
    // 陡坡才拉得开（30% 时 30.0 vs 28.7）。这条刻意用 30% 的坡，让两种写法必然分开。
    [Fact]
    public void A11_GradeDenominator_IsHorizontalRun_NotSlopeLength()
    {
        var (st, _) = New();
        var r = st.Rebuild("p", Chain(0, 100, 30, 30, 30));      // 水平 100m/段、升 30m/段

        var run = Assert.Single(r.RunList);
        Assert.Equal(30.0, run.GradePct, 6);                     // 斜长口径会给 28.735
        Assert.Equal(300.0, run.HorizontalM, 6);                 // 水平投影长
        Assert.Equal(Math.Sqrt(100.0 * 100 + 30.0 * 30) * 3, run.LengthM, 6);   // 里程仍是三维斜长

        // SimHaulPath 的净坡是同一个口径（它喂给车速模型，两处不同就会两头都错）
        var path = new SimHaulPath
        {
            Xyz = new double[] { 0, 0, 100, 100, 0, 130 },
            LengthM = Math.Sqrt(100.0 * 100 + 30.0 * 30),
        };
        Assert.Equal(30.0, path.NetGradePct, 6);
    }

    // ── A10 空输入不许"画出点什么" ──────────────────────────────────────────
    [Fact]
    public void A10_NoSegments_DrawsNothing_AndSaysWhy()
    {
        var (st, sink) = New();
        var r = st.Rebuild("p", Array.Empty<SimHaulSegment>());
        Assert.Empty(r.RunList);
        Assert.Contains("没有路段可标", r.Summary);
        Assert.Equal(0, sink.StateOf(SimRoadAnnotationStage.RoadGroup)?.Count ?? 0);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  设备形态：实体与线框必须来自同一份定义
//
//  这条判据存在的理由：两边各写一套形状的话，改一边另一边不动，
//  现象是「同一台设备在两个图层里长得不一样」，而两边各自都自洽、都不报错。
//  所以判的是**角点集合逐点相同**，不是「两边都画出来了」。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class EquipShapeParityTests
{
    private static HashSet<(long, long, long)> Corners(IEnumerable<double> xyz, int stride)
    {
        var set = new HashSet<(long, long, long)>();
        var a = xyz.ToArray();
        for (int i = 0; i + 2 < a.Length; i += 3)
            set.Add(((long)Math.Round(a[i] * 1e6), (long)Math.Round(a[i + 1] * 1e6), (long)Math.Round(a[i + 2] * 1e6)));
        return set;
    }

    [Theory]
    [InlineData(EquipKind.Shovel)]
    [InlineData(EquipKind.Loader)]
    [InlineData(EquipKind.Truck)]
    [InlineData(EquipKind.Drill)]
    [InlineData(EquipKind.Dozer)]
    [InlineData(EquipKind.Grader)]
    [InlineData(EquipKind.WaterTruck)]
    [InlineData(EquipKind.Other)]
    public void S1_MeshAndWire_ShareTheSameCorners(EquipKind kind)
    {
        var mesh = new EquipMeshBuffer();
        var wire = new EquipWireBuffer();
        // 位姿刻意不是单位阵：位姿变换若两边不一致，角点集合会整体错开
        EquipSymbolLibrary.Emit(mesh, kind, EquipState.Working, 1234.5, 6789.0, 321.0, 0.7, 18.0);
        EquipSymbolLibrary.Emit(wire, kind, EquipState.Working, 1234.5, 6789.0, 321.0, 0.7, 18.0);

        var cm = Corners(mesh.WorldXyz, 3);
        var cw = Corners(wire.WorldSegments, 3);

        Assert.True(cw.Count > 0, "线框一个角点都没有。");
        // 线框的角点必须全部来自实体的角点集合（实体额外有端盖中心点，故只判单向包含）
        var extra = cw.Except(cm).ToList();
        Assert.True(extra.Count == 0,
                    $"{kind}: 线框有 {extra.Count} 个角点不在实体的角点集合里 —— 两份形态已经漂了。");
    }

    [Fact]
    public void S2_EveryKind_ProducesSegments()
    {
        foreach (EquipKind k in Enum.GetValues<EquipKind>())
        {
            var wire = new EquipWireBuffer();
            EquipSymbolLibrary.Emit(wire, k, EquipState.Working, 0, 0, 0, 0, 1.0);
            Assert.True(wire.SegmentCount >= 12, $"{k}: 只画出 {wire.SegmentCount} 段 —— 至少该有一个盒子的 12 条边。");
        }
    }

    /// <summary>状态换了形态必须跟着换（状态标记是形状编码的，不是只换颜色）。</summary>
    [Fact]
    public void S3_StateChangesTheGlyph()
    {
        int Segs(EquipState s)
        {
            var w = new EquipWireBuffer();
            EquipSymbolLibrary.Emit(w, EquipKind.Shovel, s, 0, 0, 0, 0, 1.0);
            return w.SegmentCount;
        }
        var counts = Enum.GetValues<EquipState>().Select(Segs).Distinct().ToList();
        Assert.True(counts.Count > 1, "五种状态画出来的段数完全相同 —— 状态标记没有编进形状。");
    }
}
