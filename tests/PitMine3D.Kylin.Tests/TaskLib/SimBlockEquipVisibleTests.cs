// 忠实移植自原 PitMine3D Tests/Tests.TaskLib/SimBlockEquipVisibleTests.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Simulation;
using Xunit;

namespace PitMine3D.Kylin.Tests.TaskLibTests;

// ─────────────────────────────────────────────────────────────────────────────
//  「设备真的画出去了」判据
//
//  ── 踩过的坑（2026-08-10）──
//  设备改成实心面之后往块体的 _mesh 里写，而那个缓冲**在调 PushShovels 之前就已经
//  推给落地端了** —— 推完才填，设备那批三角一帧都没到过屏幕，下一帧 Clear 就没了。
//  现象是「设备不见了」，而代码每一句看上去都对：Emit 调了、fs 非空、组名也对。
//
//  这类"顺序坑"靠读代码很难发现，靠界面只能看出"没了"。判据直接问落地端：
//  **这一帧你到底收到了几个设备三角**。
//
//  ⇒ 判两件事：
//     Q1 有面通道时，设备落在**设备自己那个面组**里，且数量 > 0。
//     Q2 没有面通道时退线框，同样不能是 0（退路不许是空的）。
//     Q3 反例组：关掉设备开关必须真的清零 —— 否则 Q1 恒真。
// ─────────────────────────────────────────────────────────────────────────────
public sealed class SimBlockEquipVisibleTests
{
    /// <summary>
    /// 能收面的落地端（面板那条就是这样的）。
    /// <para><see cref="RecordingDynamicOverlay"/> 是 sealed，所以这里自己实现两个接口，
    /// 只记本判据要的那几个数 —— 判据不需要真的画出来。</para>
    /// </summary>
    private sealed class FaceSink : ISimDynamicOverlay, ISimFaceSink
    {
        public readonly Dictionary<string, int> Tris = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Lines = new(StringComparer.Ordinal);

        public bool Available => true;
        public string StatusLabel => "台架";

        public bool SetFaces(string group, double[]? xyz, uint[]? argb, int triCount)
        { Tris[group] = xyz == null ? 0 : Math.Max(0, triCount); return true; }

        public bool SetLines(string group, double[]? xyz, uint[]? argb, int segCount)
        { Lines[group] = xyz == null ? 0 : Math.Max(0, segCount); return true; }

        public bool SetMarkers(string group, double[]? xyz, uint[]? argb, float[]? px, byte[]? st, int n)
        { return true; }

        public bool SetLabels(string group, double[]? xyz, IReadOnlyList<string>? texts, uint[]? argb,
                              float[]? h, byte[]? ha, byte[]? va, int n) => true;

        public void Clear(string group) { Tris[group] = 0; Lines[group] = 0; }
        public void RequestRender() { }

        public int LineCount(string group) => Lines.TryGetValue(group, out int n) ? n : 0;
    }

    private static SimBlockUnit Unit(string id, int seq, double cx, double cy)
        => new()
        {
            UnitId = id, Seq = seq,
            Sx = cx, Sy = cy, Sz = 1200,
            LenM = 300, WidM = 40, ThkM = 12,
            Dx = cx + 1500, Dy = cy + 900, Dz = 1150,
            InSituM3 = 144000, LooseM3 = 194400, CapacityM3 = 161280,
            // 给一条两点的"真路径"，让搬运/堆填段都跑得起来
            Xyz = new[] { cx, cy, 1200.0, cx + 1500, cy + 900, 1150.0 },
        };

    private static List<SimBlockUnit> Units(int n)
        => Enumerable.Range(0, n).Select(i => Unit("U" + i, i + 1, 620000 + i * 120, 4380000)).ToList();

    /// <summary>把一整期扫一遍，取各组的峰值（三段发生在不同时刻，单相位会误判）。</summary>
    private static (int EquipFaces, int EquipWire) Sweep(SimBlockTransferStage st, FaceSink sink)
    {
        int f = 0, w = 0;
        foreach (double t in new[] { 0.0, 0.1, 0.25, 0.4, 0.55, 0.7, 0.85, 0.99 })
        {
            st.Tick(t);
            sink.Tris.TryGetValue(SimBlockTransferStage.EquipFaceGroup, out int nf);
            f = Math.Max(f, nf);
            w = Math.Max(w, sink.LineCount(SimBlockTransferStage.ShovelGroup));
        }
        return (f, w);
    }

    // ── Q1 有面通道：设备必须落进设备面组，且不为 0 ─────────────────────────
    [Fact]
    public void Q1_有面通道时设备画成实心面()
    {
        var sink = new FaceSink();
        var st = new SimBlockTransferStage(sink);
        st.Params.WorkPoints = 3;
        st.Rebuild("2026-08", Units(9));

        var (faces, _) = Sweep(st, sink);
        Assert.True(faces > 0,
            "有面通道，设备却一个三角都没送到落地端 —— 多半是往**块体的**缓冲里写，"
          + "而那个缓冲在调设备那一步之前就已经推走了（推完才填，一帧都到不了屏幕）。");
    }

    // ── Q2 没有面通道：退线框，同样不许是空的 ───────────────────────────────
    [Fact]
    public void Q2_没有面通道时退线框且不为空()
    {
        var sink = new RecordingDynamicOverlay();      // 不实现 ISimFaceSink
        var st = new SimBlockTransferStage(sink);
        st.Params.WorkPoints = 3;
        st.Rebuild("2026-08", Units(9));

        int w = 0;
        foreach (double t in new[] { 0.0, 0.25, 0.5, 0.75, 0.99 })
        {
            st.Tick(t);
            w = Math.Max(w, sink.StateOf(SimBlockTransferStage.ShovelGroup)?.Count ?? 0);
        }
        Assert.True(w > 0, "没有面通道时线框退路也是空的 —— 那条退路等于不存在。");
    }

    // ── Q3 反例组：关掉设备必须真的清零 ─────────────────────────────────────
    [Fact]
    public void Q3_关掉设备必须清零_反例组()
    {
        var sink = new FaceSink();
        var st = new SimBlockTransferStage(sink);
        st.Params.WorkPoints = 3;
        st.Params.ShowShovels = false;
        st.Params.ShowDozers = false;
        st.Rebuild("2026-08", Units(9));

        var (faces, wire) = Sweep(st, sink);
        Assert.Equal(0, faces);
        Assert.Equal(0, wire);
    }

    // ── Q4 电铲在整个块段周期都在（不因为转入运输而消失）─────────────────────
    [Fact]
    public void Q4_电铲在整个块段周期都在()
    {
        var sink = new FaceSink();
        var st = new SimBlockTransferStage(sink);
        st.Params.WorkPoints = 1;      // 一个点，时间轴上每一格都属于它
        st.Params.ShowDozers = false;  // 只看电铲
        st.Rebuild("2026-08", Units(4));

        int zero = 0;
        for (int i = 0; i <= 40; i++)
        {
            st.Tick(i / 40.0);
            sink.Tris.TryGetValue(SimBlockTransferStage.EquipFaceGroup, out int nf);
            if (nf == 0) zero++;
        }
        Assert.Equal(0, zero);   // 一格都不许空 —— 铲一直在那儿装车
    }
}
