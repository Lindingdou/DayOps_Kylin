// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimOverlayComposerCheck.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  SimOverlayComposer 的判据。
//
//  写法上的两条纪律（这里是刻意做到的，不是顺手）：
//   1) **每条判据都能证伪**。凡是「只判成功」的判据都会空过 —— 所以 A/B 两条各自带一个
//      **反例对照组**：拿「直接调 capability」的老做法（整通道替换）跑同一个场景，
//      判据必须在对照组上**判失败**。对照组要是也过了，说明判据本身没抓到东西。
//   2) **不吞降级**。丢弃的环、补的颜色都要在 Notes 里出现，判据 F 就是查这个。
//
//  跑法：<c>SimOverlayComposerCheck.Run()</c> 返回一行行的报告；<c>RunAll().AllPassed</c>
//  是布尔结论。不需要三维内核 —— 落地端是本文件里的假 sink。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>把每次推送录下来的假落地端（判据用；也可当调试探针）。</summary>
public sealed class RecordingOverlaySink : ISimOverlaySink
{
    /// <summary>通道当前内容（Show 整批替换，Clear 清空）—— 复刻真实通道的语义。</summary>
    public List<(double[] Ring, uint Rgb)> Channel { get; } = new();
    /// <summary>Show 的次数。</summary>
    public int ShowCount { get; private set; }
    /// <summary>Clear 的次数。</summary>
    public int ClearCount { get; private set; }

    public void Show(IReadOnlyList<double[]> rings, IReadOnlyList<uint> rgbPerRing)
    {
        ShowCount++;
        Channel.Clear();                                    // ← 真实 capability 第一行就是这个
        for (int i = 0; i < rings.Count; i++)
            Channel.Add((rings[i], i < rgbPerRing.Count ? rgbPerRing[i] : 0u));
    }

    public void Clear() { ClearCount++; Channel.Clear(); }
}

/// <summary>一条判据的结论。</summary>
public sealed record SimCheckItem(string Name, bool Passed, string Detail)
{
    public override string ToString() => $"[{(Passed ? "过" : "不过")}] {Name}　{Detail}";
}

/// <summary>全部判据的结论。</summary>
public sealed class SimCheckReport
{
    public List<SimCheckItem> Items { get; } = new();
    public bool AllPassed => Items.All(i => i.Passed);
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SimOverlayComposer 判据：{Items.Count(i => i.Passed)}/{Items.Count} 通过");
        foreach (var i in Items) sb.AppendLine("  " + i);
        return sb.ToString();
    }
}

/// <summary>SimOverlayComposer 的可证伪判据。</summary>
public static class SimOverlayComposerCheck
{
    private static double[] Ring(double x, double y, double s)
        => new[] { x, y, 0, x + s, y, 0, x + s, y + s, 0, x, y + s, 0 };

    /// <summary>跑全部判据，返回报告文本。</summary>
    public static string Run() => RunAll().ToString();

    /// <summary>跑全部判据。</summary>
    public static SimCheckReport RunAll()
    {
        var rep = new SimCheckReport();
        rep.Items.Add(A_TwoKeysCoexist());
        rep.Items.Add(A_NegativeControl());
        rep.Items.Add(B_RemoveOnlyOwn());
        rep.Items.Add(B_NegativeControl());
        rep.Items.Add(C_Deterministic());
        rep.Items.Add(D_ClearEmptiesChannel());
        rep.Items.Add(E_OnePushPerFrame());
        rep.Items.Add(F_DropsAreAccounted());
        rep.Items.Add(G_OpenPolylineSplit());
        rep.Items.Add(H_NeverClearWhatWeNeverWrote());
        rep.Items.Add(I_FrameScopeCoalesces());
        return rep;
    }

    // ── A 两个 key 各 attach 后两者都在（不互相擦）───────────────────────────────

    private static SimCheckItem A_TwoKeysCoexist()
    {
        var sink = new RecordingOverlaySink();
        var c = new SimOverlayComposer(sink);
        var frameRings = new[] { Ring(0, 0, 10), Ring(20, 0, 10) };
        var equipRings = new[] { Ring(0, 40, 5) };

        c.Set(SimOverlayLayers.Frame, frameRings, new uint[] { 0xD85A30, 0x8A8F98 });
        c.Flush();
        c.Set(SimOverlayLayers.Equip, equipRings, new uint[] { 0x1D9E75 });
        c.Flush();

        bool frameAlive = frameRings.All(r => sink.Channel.Any(t => ReferenceEquals(t.Ring, r)));
        bool equipAlive = equipRings.All(r => sink.Channel.Any(t => ReferenceEquals(t.Ring, r)));
        bool countOk = sink.Channel.Count == frameRings.Length + equipRings.Length;
        bool ok = frameAlive && equipAlive && countOk;
        return new SimCheckItem("A 两个 key 共存",
            ok,
            $"通道内 {sink.Channel.Count} 环（期望 3）· frame 在={frameAlive} · equip 在={equipAlive}");
    }

    /// <summary>反例对照：老做法（各自直接调整通道替换）在同一场景下**必须**丢内容。</summary>
    private static SimCheckItem A_NegativeControl()
    {
        var sink = new RecordingOverlaySink();
        var frameRings = new[] { Ring(0, 0, 10), Ring(20, 0, 10) };
        var equipRings = new[] { Ring(0, 40, 5) };

        sink.Show(frameRings, new uint[] { 0xD85A30, 0x8A8F98 });   // frame 那边自己调
        sink.Show(equipRings, new uint[] { 0x1D9E75 });             // equip 那边自己调 → 把 frame 擦了

        bool frameLost = !frameRings.Any(r => sink.Channel.Any(t => ReferenceEquals(t.Ring, r)));
        return new SimCheckItem("A' 反例对照：不走合成器必丢图",
            frameLost,
            frameLost
                ? $"直接调 capability 后通道只剩 {sink.Channel.Count} 环，frame 的 2 环被 equip 擦掉 —— 判据 A 抓的就是这个"
                : "对照组没丢内容 ⇒ 判据 A 是空过的，别信它");
    }

    // ── B Remove 只删自己那份 ──────────────────────────────────────────────────

    private static SimCheckItem B_RemoveOnlyOwn()
    {
        var sink = new RecordingOverlaySink();
        var c = new SimOverlayComposer(sink);
        var frameRings = new[] { Ring(0, 0, 10) };
        var equipRings = new[] { Ring(0, 40, 5), Ring(10, 40, 5) };

        c.Set(SimOverlayLayers.Frame, frameRings, new uint[] { 0xD85A30 });
        c.Set(SimOverlayLayers.Equip, equipRings, new uint[] { 0x1D9E75, 0x1D9E75 });
        c.Flush();
        bool removed = c.Remove(SimOverlayLayers.Frame);
        c.Flush();

        bool frameGone = !frameRings.Any(r => sink.Channel.Any(t => ReferenceEquals(t.Ring, r)));
        bool equipAlive = equipRings.All(r => sink.Channel.Any(t => ReferenceEquals(t.Ring, r)));
        bool ok = removed && frameGone && equipAlive && sink.Channel.Count == 2;
        return new SimCheckItem("B Remove 只删自己那份",
            ok,
            $"Remove(frame)={removed} · frame 已消失={frameGone} · equip 仍在={equipAlive} · 通道 {sink.Channel.Count} 环（期望 2）");
    }

    /// <summary>反例对照：Remove 一个**没 attach 过**的键必须返回 false，且一环都不许动。</summary>
    private static SimCheckItem B_NegativeControl()
    {
        var sink = new RecordingOverlaySink();
        var c = new SimOverlayComposer(sink);
        c.Set(SimOverlayLayers.Frame, new[] { Ring(0, 0, 10) }, new uint[] { 0xD85A30 });
        c.Flush();
        int before = sink.Channel.Count;
        bool removed = c.Remove(SimOverlayLayers.Haul);       // 没 attach 过
        c.Flush();
        bool ok = !removed && sink.Channel.Count == before && before == 1;
        return new SimCheckItem("B' 反例对照：删不存在的键不许动别人",
            ok,
            $"Remove(haul)={removed}（期望 false）· 通道 {before}→{sink.Channel.Count} 环");
    }

    // ── C 同输入两次合成结果逐位相同（且与 attach 先后无关）────────────────────

    private static SimCheckItem C_Deterministic()
    {
        var f = new[] { Ring(0, 0, 10), Ring(20, 0, 10) };
        var e = new[] { Ring(0, 40, 5) };
        var h = new[] { Ring(60, 60, 7) };

        var c1 = new SimOverlayComposer(new RecordingOverlaySink());
        c1.Set(SimOverlayLayers.Frame, f, new uint[] { 1, 2 });
        c1.Set(SimOverlayLayers.Equip, e, new uint[] { 3 });
        c1.Set(SimOverlayLayers.Haul, h, new uint[] { 4 });

        var c2 = new SimOverlayComposer(new RecordingOverlaySink());
        c2.Set(SimOverlayLayers.Haul, h, new uint[] { 4 });      // 故意换 attach 顺序
        c2.Set(SimOverlayLayers.Frame, f, new uint[] { 1, 2 });
        c2.Set(SimOverlayLayers.Equip, e, new uint[] { 3 });

        var a1 = c1.Compose(); var a2 = c1.Compose();            // 同一实例两次
        var b1 = c2.Compose();                                   // 另一实例、乱序 attach

        bool twice = a1.ContentEquals(a2) && a1.Signature == a2.Signature;
        bool orderFree = a1.ContentEquals(b1);
        // 次序判据要真的判到「次序」：frame 在前、haul 在后
        bool layered = a1.Count == 4 && a1.Colors[0] == 1 && a1.Colors[1] == 2 && a1.Colors[2] == 3 && a1.Colors[3] == 4;

        bool ok = twice && orderFree && layered;
        return new SimCheckItem("C 合成确定性（两次逐位相同 + 与 attach 先后无关 + 叠放序按声明）",
            ok,
            $"两次相同={twice} · 乱序 attach 结果相同={orderFree} · 叠放序 frame→equip→haul={layered}"
            + $" · 实际色序=[{string.Join(",", a1.Colors)}]（期望 1,2,3,4）");
    }

    // ── D Clear 后通道空 ──────────────────────────────────────────────────────

    private static SimCheckItem D_ClearEmptiesChannel()
    {
        var sink = new RecordingOverlaySink();
        var c = new SimOverlayComposer(sink);
        c.Set(SimOverlayLayers.Frame, new[] { Ring(0, 0, 10) }, new uint[] { 0xD85A30 });
        c.Set(SimOverlayLayers.Equip, new[] { Ring(0, 40, 5) }, new uint[] { 0x1D9E75 });
        c.Flush();
        bool hadContent = sink.Channel.Count == 2;
        c.Clear();
        bool channelEmpty = sink.Channel.Count == 0;
        bool keysGone = c.Keys.Count == 0;
        bool composeEmpty = c.Compose().IsEmpty;
        bool ok = hadContent && channelEmpty && keysGone && composeEmpty;
        return new SimCheckItem("D Clear 后通道空",
            ok,
            $"清前 {(hadContent ? 2 : sink.Channel.Count)} 环 · 清后通道 {sink.Channel.Count} 环 · 键 {c.Keys.Count} 个 · Compose 空={composeEmpty}");
    }

    // ── E 每帧只推一次 / 内容没变不重推 ────────────────────────────────────────

    private static SimCheckItem E_OnePushPerFrame()
    {
        var sink = new RecordingOverlaySink();
        var c = new SimOverlayComposer(sink);
        var f = new[] { Ring(0, 0, 10) };
        var e = new[] { Ring(0, 40, 5) };

        // 一帧里三方各 attach，帧末只 Flush 一次 → 只能推 1 次
        c.Set(SimOverlayLayers.Frame, f, new uint[] { 1 });
        c.Set(SimOverlayLayers.Equip, e, new uint[] { 2 });
        c.Set(SimOverlayLayers.Haul, Array.Empty<double[]>());
        c.Flush();
        int afterFrame1 = sink.ShowCount;

        // 下一帧内容没变 → 不该再推（overlay 是保留态）
        c.Set(SimOverlayLayers.Frame, f, new uint[] { 1 });
        c.Set(SimOverlayLayers.Equip, e, new uint[] { 2 });
        c.Flush();
        int afterFrame2 = sink.ShowCount;

        // 内容变了 → 必须推
        c.Set(SimOverlayLayers.Equip, new[] { Ring(99, 99, 5) }, new uint[] { 2 });
        c.Flush();
        int afterFrame3 = sink.ShowCount;

        // 外部擦了通道 → ForceNextFlush 必须能救回来
        sink.Clear();
        c.ForceNextFlush();
        c.Flush();
        bool rescued = sink.Channel.Count == 2;

        bool ok = afterFrame1 == 1 && afterFrame2 == 1 && afterFrame3 == 2 && rescued;
        return new SimCheckItem("E 每帧一次 / 无变化不重推 / 被外部擦掉能强推回来",
            ok,
            $"帧1 后 Show={afterFrame1}（期望 1）· 帧2（无变化）后={afterFrame2}（期望 1）"
            + $" · 帧3（有变化）后={afterFrame3}（期望 2）· 强推救回={rescued}");
    }

    // ── F 丢弃/降级不许静默 ───────────────────────────────────────────────────

    private static SimCheckItem F_DropsAreAccounted()
    {
        var c = new SimOverlayComposer(new RecordingOverlaySink());
        var rings = new[]
        {
            Ring(0, 0, 10),                                     // 好环
            new[] { 0.0, 0.0, 0.0 },                            // 只有 1 点 → 内核静默忽略
            new[] { 0.0, 0.0, 0.0, 1.0, 1.0 },                  // 坐标数不是 3 的倍数
            new[] { 0.0, 0.0, 0.0, double.NaN, 1.0, 0.0 },      // 含 NaN
        };
        c.Set(SimOverlayLayers.Frame, rings, new uint[] { 0xD85A30 });   // 颜色只给了 1 个

        var notes = c.Notes;
        int kept = c.Compose().Count;
        bool keptOne = kept == 1;
        bool sawShort = notes.Any(n => n.Contains("不足 2 点"));
        bool sawShape = notes.Any(n => n.Contains("3 的倍数"));
        bool sawNaN = notes.Any(n => n.Contains("NaN"));
        // 颜色缺位：好环只有 1 条且它拿到了给定色 ⇒ 不该报缺色；把好环加到 2 条才该报
        var c2 = new SimOverlayComposer(new RecordingOverlaySink());
        c2.Set(SimOverlayLayers.Frame, new[] { Ring(0, 0, 10), Ring(20, 0, 10) }, new uint[] { 0xD85A30 });
        bool sawColor = c2.Notes.Any(n => n.Contains("兜底色"));

        bool ok = keptOne && sawShort && sawShape && sawNaN && sawColor;
        return new SimCheckItem("F 丢弃/降级全部记账（不静默）",
            ok,
            $"留下 {kept} 环（期望 1）· 短环记账={sawShort} · 形状记账={sawShape} · NaN 记账={sawNaN} · 缺色记账={sawColor}"
            + $" · Notes {notes.Count} 条");
    }

    // ── G 开放折线拆段：每段必须**恰好 2 点**（3 点起内核就强制填面）──────────

    private static SimCheckItem G_OpenPolylineSplit()
    {
        var route = new double[] { 0, 0, 0, 100, 0, 0, 100, 0, 0, /*重合点*/ 100, 80, 0, 200, 80, 0 };
        var segs = SimOverlayComposer.SplitOpenPolyline(route);
        bool countOk = segs.Count == 3;                                  // 5 点 - 1 重合段 = 3 段
        bool allTwoPoint = segs.All(s => s.Length == 6);                 // ← 这条是要害
        bool chained = segs.Count >= 2
            && segs[0][3] == segs[1][0] && segs[0][4] == segs[1][1];     // 首尾相接，画出来才连续
        bool degenerateRejected = SimOverlayComposer.SplitOpenPolyline(new double[] { 1, 2, 3 }).Count == 0;
        bool ok = countOk && allTwoPoint && chained && degenerateRejected;
        return new SimCheckItem("G 开放折线拆段（每段恰好 2 点 → 内核不填面不闭合）",
            ok,
            $"段数={segs.Count}（期望 3，含跳过 1 个零长段）· 全为 2 点段={allTwoPoint}"
            + $" · 首尾相接={chained} · 单点串被拒={degenerateRejected}");
    }

    // ── H 没写过的通道不许去 Clear（否则会误伤非参与方的图）────────────────────

    private static SimCheckItem H_NeverClearWhatWeNeverWrote()
    {
        var sink = new RecordingOverlaySink();
        sink.Show(new[] { Ring(0, 0, 1) }, new uint[] { 0x26D973 });     // 非参与方（如短期可采区域窗口）先画了
        int outsiderRings = sink.Channel.Count;

        var c = new SimOverlayComposer(sink);
        c.Flush();                                                        // 空合成器 Flush
        c.Set(SimOverlayLayers.Frame, Array.Empty<double[]>());           // attach 了但什么都没画
        c.Flush();
        c.Remove(SimOverlayLayers.Frame);
        c.Flush();
        c.Clear();                                                        // 从没写过 → Clear 也不该动通道

        bool untouched = sink.Channel.Count == outsiderRings && sink.ClearCount == 0;
        return new SimCheckItem("H 没写过就不碰通道（不误伤非参与方）",
            untouched,
            $"外部内容 {outsiderRings}→{sink.Channel.Count} 环 · Clear 调用 {sink.ClearCount} 次（期望 0）");
    }

    // ── I 帧作用域把三方各自的 Flush 合并成一次通道调用 ────────────────────────

    private static SimCheckItem I_FrameScopeCoalesces()
    {
        var sink = new RecordingOverlaySink();
        var c = new SimOverlayComposer(sink);

        // 不用帧作用域：三方各 Set+Flush → 通道被调 3 次（画得对，但调用次数不对）
        c.Set(SimOverlayLayers.Frame, new[] { Ring(0, 0, 10) }, new uint[] { 1 }); c.Flush();
        c.Set(SimOverlayLayers.Equip, new[] { Ring(0, 40, 5) }, new uint[] { 2 }); c.Flush();
        c.Set(SimOverlayLayers.Haul, new[] { Ring(60, 60, 7) }, new uint[] { 3 }); c.Flush();
        int naive = sink.ShowCount;

        // 用帧作用域：同样三方各 Set+Flush，只该推 1 次
        var sink2 = new RecordingOverlaySink();
        var c2 = new SimOverlayComposer(sink2);
        using (c2.BeginFrame())
        {
            c2.Set(SimOverlayLayers.Frame, new[] { Ring(0, 0, 10) }, new uint[] { 1 }); c2.Flush();
            using (c2.BeginFrame())   // 嵌套也不许提前推
            {
                c2.Set(SimOverlayLayers.Equip, new[] { Ring(0, 40, 5) }, new uint[] { 2 }); c2.Flush();
            }
            c2.Set(SimOverlayLayers.Haul, new[] { Ring(60, 60, 7) }, new uint[] { 3 }); c2.Flush();
            if (sink2.ShowCount != 0)
                return new SimCheckItem("I 帧作用域合并为一次通道调用", false,
                    $"作用域**内**就推了 {sink2.ShowCount} 次 —— 攒批没生效");
        }
        int scoped = sink2.ShowCount;

        bool sameContent = sink.Channel.Count == 3 && sink2.Channel.Count == 3;
        bool ok = naive == 3 && scoped == 1 && sameContent;
        return new SimCheckItem("I 帧作用域合并为一次通道调用",
            ok,
            $"不用作用域 Show={naive}（期望 3，说明判据抓得到差别）· 用作用域 Show={scoped}（期望 1）"
            + $" · 两者通道内容都是 3 环={sameContent}");
    }
}
