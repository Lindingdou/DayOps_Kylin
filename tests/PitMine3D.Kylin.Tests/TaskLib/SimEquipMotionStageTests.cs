// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SimEquipMotionStageTests.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  会动的设备符号（SimEquipMotionStage）离线验收台架
//
//  这一组最容易空过的是 E1：一个「根本没实现位移」的版本会让所有「画出来了」的判据全绿。
//  所以 E1 判的是**位移量本身**（期末必须恰好走完 AdvanceM、期中恰好走一半），
//  并配一个「推进解不出来 ⇒ 位置逐位不变」的反例组 —— 两条一起才关得住。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SimEquipMotionStageTests
{
    private static EquipSymbol Sym(string id, EquipKind kind, double x = 1000, double y = 2000,
                                   double heading = 0, EquipState state = EquipState.Working)
        => new()
        {
            MachineId = id,
            Kind = kind,
            State = state,
            UnitId = "U1",
            X = x,
            Y = y,
            Z = 300,
            HeadingRad = heading,
            PositionSource = EquipPositionSource.Track,
            PositionRef = "台架",
        };

    private static SimEquipMotionParams P(double advanceM = 240, double azDeg = 0, bool directional = true) => new()
    {
        AdvanceM = advanceM,
        AdvanceAzimuthDeg = azDeg,
        DirectionalAdvance = directional,
        SymbolLengthM = 20,
        LiftM = 0,
        MaxSegments = 10000,
    };

    private static (SimEquipMotionStage Stage, RecordingDynamicOverlay Sink) New(SimEquipMotionParams p)
    {
        var sink = new RecordingDynamicOverlay();
        return (new SimEquipMotionStage(sink) { Params = p }, sink);
    }

    /// <summary>本帧所有线段端点的 X 均值 —— 整体平移量的稳健度量（形态不变时它就是位移）。</summary>
    private static double MeanX(RecordingDynamicOverlay sink)
    {
        var p = sink.StateOf(SimEquipMotionStage.Group)!;
        double s = 0; int n = 0;
        for (int i = 0; i < p.Count; i++) { s += p.Xyz[i * 6] + p.Xyz[i * 6 + 3]; n += 2; }
        return n > 0 ? s / n : double.NaN;
    }

    // ── E1 期内位移 = 推进距离 × 相位，一米不多一米不少 ──────────────────────
    [Fact]
    public void E1_MovesExactlyTheAdvanceDistance_OverThePeriod()
    {
        var (st, sink) = New(P(advanceM: 240, azDeg: 0));
        st.Rebuild("2026-08", new[] { Sym("EX3600_1", EquipKind.Shovel) });

        st.Tick(0.0); double x0 = MeanX(sink);
        st.Tick(0.5); double xh = MeanX(sink);
        st.Tick(1.0); double x1 = MeanX(sink);

        Assert.Equal(240.0, x1 - x0, 6);          // 期末恰好走完
        Assert.Equal(120.0, xh - x0, 6);          // 期中恰好一半（线性）
    }

    /// <summary>E1 的反例组：推进解不出来时**位置逐位不变**。缺了它，"没实现位移"也能让别的判据全绿。</summary>
    [Fact]
    public void E1b_UnresolvedAdvance_LeavesPositionsBitIdentical()
    {
        foreach (var p in new[]
        {
            P(advanceM: double.NaN),                       // 推进距离没解出来
            P(directional: false),                         // 全周等距：没有单一方位
        })
        {
            var (st, sink) = New(p);
            var r = st.Rebuild("2026-08", new[] { Sym("EX1", EquipKind.Shovel) });

            Assert.Equal(0, r.Moving);
            Assert.Equal(r.Drawn, r.Static);
            Assert.Contains(r.Notes, s => s.Contains("原地不动"));

            st.Tick(0.0);
            var a = (double[])sink.StateOf(SimEquipMotionStage.Group)!.Xyz.Clone();
            st.Tick(1.0);
            var b = sink.StateOf(SimEquipMotionStage.Group)!.Xyz;
            Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
                Assert.True(BitConverter.DoubleToInt64Bits(a[i]) == BitConverter.DoubleToInt64Bits(b[i]),
                            "推进没解出来却发生了位移 —— 那是编出来的巡回路线。");
        }
    }

    // ── E2 方位真的被用上（90° 时必须走 Y 不走 X）────────────────────────────
    [Fact]
    public void E2_AzimuthIsHonoured()
    {
        var (st, sink) = New(P(advanceM: 100, azDeg: 90));
        st.Rebuild("p", new[] { Sym("EX1", EquipKind.Shovel) });

        st.Tick(0.0);
        var a = (double[])sink.StateOf(SimEquipMotionStage.Group)!.Xyz.Clone();
        st.Tick(1.0);
        var b = sink.StateOf(SimEquipMotionStage.Group)!.Xyz;

        double dx = b[0] - a[0], dy = b[1] - a[1];
        Assert.Equal(0.0, dx, 6);
        Assert.Equal(100.0, dy, 6);
    }

    // ── E3 卡车不画（它们是车流动点，画两遍就是双份车队）──────────────────
    [Fact]
    public void E3_TrucksAreSkipped_AndAccountedFor()
    {
        var (st, _) = New(P());
        var r = st.Rebuild("p", new[]
        {
            Sym("EX1", EquipKind.Shovel),
            Sym("T1", EquipKind.Truck),
            Sym("T2", EquipKind.Truck),
            Sym("D1", EquipKind.Dozer),
        });

        Assert.Equal(4, r.FedSymbols);
        Assert.Equal(2, r.Drawn);
        Assert.Equal(2, r.SkippedTrucks);
        Assert.True(r.Balanced);
        Assert.Contains(r.Notes, s => s.Contains("双份车队"));

        // 对照组：显式要卡车时必须画出来（否则这条判据抓的是「永远不画卡车」而不是口径）
        var p = P(); p.IncludeTrucks = true;
        var (st2, _) = New(p);
        var r2 = st2.Rebuild("p", new[] { Sym("T1", EquipKind.Truck) });
        Assert.Equal(1, r2.Drawn);
        Assert.Equal(0, r2.SkippedTrucks);
    }

    // ── E4 铭牌：一台一块牌、文案就是设备编号、跟着一起走 ────────────────────
    [Fact]
    public void E4_Labels_MatchMachines_AndTranslateWithThem()
    {
        var (st, sink) = New(P(advanceM: 300, azDeg: 0));
        st.Rebuild("p", new[] { Sym("EX3600_1", EquipKind.Shovel), Sym("L1350_1", EquipKind.Loader, y: 2100) });

        st.Tick(0.0);
        var t0 = sink.StateOf(SimEquipMotionStage.LabelGroup)!;
        Assert.Equal(2, t0.Count);
        Assert.Equal(new[] { "EX3600_1", "L1350_1" }, t0.Texts);
        double lx0 = t0.Xyz[0];

        st.Tick(1.0);
        var t1 = sink.StateOf(SimEquipMotionStage.LabelGroup)!;
        Assert.Equal(300.0, t1.Xyz[0] - lx0, 6);   // 牌子跟着设备走，不是钉在期初位置

        // 关掉铭牌 ⇒ 该组必须被撤掉（不是留着上一帧的字）
        var p = P(); p.ShowLabels = false;
        var (st2, sink2) = New(p);
        st2.Rebuild("p", new[] { Sym("EX1", EquipKind.Shovel) });
        st2.Tick(0.5);
        Assert.Equal(0, sink2.StateOf(SimEquipMotionStage.LabelGroup)?.Count ?? 0);
    }

    // ── E5 符号是世界尺寸，且只随显式倍率变（不自动放大）────────────────────
    [Fact]
    public void E5_SymbolSize_ScalesOnlyByExplicitFactor()
    {
        double Span(RecordingDynamicOverlay s)
        {
            var p = s.StateOf(SimEquipMotionStage.Group)!;
            double lo = double.MaxValue, hi = double.MinValue;
            for (int i = 0; i < p.Count; i++)
            {
                lo = Math.Min(lo, Math.Min(p.Xyz[i * 6], p.Xyz[i * 6 + 3]));
                hi = Math.Max(hi, Math.Max(p.Xyz[i * 6], p.Xyz[i * 6 + 3]));
            }
            return hi - lo;
        }

        var (s1, k1) = New(P());
        s1.Rebuild("p", new[] { Sym("EX1", EquipKind.Shovel) });
        s1.Tick(0);

        var p2 = P(); p2.SymbolScale = 3;
        var (s2, k2) = New(p2);
        var r2 = s2.Rebuild("p", new[] { Sym("EX1", EquipKind.Shovel) });
        s2.Tick(0);

        Assert.Equal(3.0, Span(k2) / Span(k1), 6);
        Assert.Contains(r2.Notes, s => s.Contains("符号放大"));   // 改了尺寸必须说出口
    }

    // ── E6 帧代价与台数无关：每帧两组各一次 ─────────────────────────────────
    [Fact]
    public void E6_OnePushPerGroupPerFrame()
    {
        var syms = Enumerable.Range(0, 60)
                             .Select(i => Sym("EX" + i, EquipKind.Shovel, y: 2000 + i * 40))
                             .ToArray();
        var (st, sink) = New(P());
        st.Rebuild("p", syms);
        int before = sink.Calls;
        st.Tick(0.4);
        Assert.Equal(2, sink.Calls - before);            // 符号线 1 次 + 铭牌 1 次
        Assert.Equal(60, sink.StateOf(SimEquipMotionStage.LabelGroup)!.Count);
    }

    // ── E7 一台设备都没有时不许"画出点什么" ─────────────────────────────────
    [Fact]
    public void E7_NoSymbols_DrawsNothing_AndSaysWhy()
    {
        var (st, sink) = New(P());
        var r = st.Rebuild("p", Array.Empty<EquipSymbol>());
        Assert.Equal(0, r.Drawn);
        Assert.Contains("一台都没有", r.Summary);
        Assert.Equal(0, st.Tick(0.5));
        Assert.Equal(0, sink.StateOf(SimEquipMotionStage.Group)?.Count ?? 0);
    }
}
