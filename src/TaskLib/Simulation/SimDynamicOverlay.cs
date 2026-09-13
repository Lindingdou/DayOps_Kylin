// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimDynamicOverlay.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Platform.Capabilities;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ═════════════════════════════════════════════════════════════════════════════
//  动态 overlay 通道 —— 逐帧动画（车流动点 / 会动的设备符号）唯一走得通的那条路
//
//  ── 为什么另起一条通道，而不是复用 SimOverlayComposer ──
//  两者解决的是**不同的问题**，合成一条会两头都做不好：
//   · SimOverlayComposer 仲裁的是 MINEABLE_AREA 这**一条**整通道替换的口
//     （谁后调谁擦掉别人），代价是**一条环一次 P/Invoke** —— 静态图一期推一次，划算；
//     逐帧动画每帧推几百条，顶穿帧预算。
//   · 本通道（内核 PitMine_SetOverlayLineBatch / MarkerBatch）**一次调用换一整组**，
//     组名由调用方给且内核侧强制加 "X:" 前缀 ⇒ **构造上撞不上**内置组与彼此。
//     没有共享通道，就不需要仲裁者 —— 各舞台各占一个组名即可。
//  ⇒ 纪律：**静态图走合成器，会动的东西走本通道**。别把动画塞进合成器（帧代价），
//    也别把静态区域图搬到本通道（那会绕过合成器已经建好的叠放序与丢弃记账）。
//
//  ── 为什么不走实体 ──
//  已在 EquipmentStage.cs:25-61 逐条核过，结论没变（内核仍是那样）：
//   · 建/删实体每次压一条 Undo（xllAcEd.cpp:8784）—— 逐帧建几十帧刷爆 Undo 栈；
//   · IEntityCapability **没有变换矩阵** —— 建出来就动不了，这是「设备符号不会动」的根因；
//   · 显隐（PitMine_SetEntityProperty "visible"）**不 MarkRenderDirty**（xllAcEd.cpp:11318），
//     mesh 进了持久缓存就一直画，逐帧切显隐不一定生效。
//  本通道三条全绕开：不入库、不占 Undo、不进 mesh 缓存。
//
//  ── 代价（按调用次数数出来的，不是实测耗时）──
//  一帧 = 每个组 1 次 P/Invoke + 帧末 1 次 RequestRender。
//  车流（重载/空驶）+ 设备（符号线/朝向）≈ 4~5 次/帧，**与图元数量无关**。
//  内核侧每帧把 overlay 几何全量重投影一次（Viewport.cpp:816 → BuildOverlayGeometry），
//  那本来就发生，与本通道推多少无关。
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 动态 overlay 通道的落地端。抽成接口是为了判据能在**没有三维内核**的环境里跑
/// （<see cref="RecordingDynamicOverlay"/>）。
/// </summary>
/// <summary>
/// 能画**实心面**的落地端（可选扩展）。
///
/// <para>为什么单独一个接口而不是塞进 <see cref="ISimDynamicOverlay"/>：
/// 内核那条落地端（<see cref="SimDynamicOverlay"/>）走的是 overlay 线通道，
/// **画不了带明暗的实心面**；面板落地端（<see cref="SimPanelOverlay"/>）可以。
/// 强行塞进主接口的话，内核那边只能实现成空方法 —— 那是一个"接口承诺了、实现是假的"的洞，
/// 调用方看不出来。分开之后，舞台用 <c>sink is ISimFaceSink</c> 显式问一句，
/// 有就画实体、没有就退线框，**两条路都是真的**。</para>
/// </summary>
public interface ISimFaceSink
{
    /// <summary>
    /// 整组替换三角面。<paramref name="xyzFlat"/> 每个三角 9 个 double（三个顶点）；
    /// <paramref name="argbPerTri"/> 逐三角 0xAARRGGBB。<paramref name="triCount"/>&lt;=0 = 抹掉该组。
    /// <para>明暗由落地端按**世界法线**算（调用方给的是本色，不要自己先乘明暗 ——
    /// 那样同一个块在不同视角下的明暗就不会变，立体感反而没了）。</para>
    /// </summary>
    bool SetFaces(string group, double[]? xyzFlat, uint[]? argbPerTri, int triCount);
}

public interface ISimDynamicOverlay
{
    /// <summary>接得上真内核吗（false = 空转，所有调用静默无效）。</summary>
    bool Available { get; }

    /// <summary>接线状态一句话（界面直接显示）。</summary>
    string StatusLabel { get; }

    /// <summary>
    /// 整组替换线段。<paramref name="xyzFlat"/> 每段 6 个 double；数组允许长于实际用量
    /// （动画侧按上限一次分配、逐帧只填前 n 段）。<paramref name="segCount"/>&lt;=0 = 抹掉该组。
    /// </summary>
    bool SetLines(string group, double[]? xyzFlat, uint[]? argbPerSeg, int segCount);

    /// <summary>
    /// 整组替换点标记。<paramref name="xyzFlat"/> 每点 3 个 double。
    /// 尺寸是**屏幕像素**（缩放时大小不变）；style 见 <see cref="SimMarkerStyle"/>。
    /// </summary>
    bool SetMarkers(string group, double[]? xyzFlat, uint[]? argbPerPt,
                    float[]? pixelSizePerPt, byte[]? stylePerPt, int count);

    /// <summary>
    /// 整组替换世界锚点文字（设备铭牌）。字高是**世界米**，视口按相机投影出像素字号
    /// （随缩放变大变小，&lt;6px 省绘）。<paramref name="texts"/> 与点一一对应，空串占位不画。
    /// </summary>
    bool SetLabels(string group, double[]? xyzFlat, IReadOnlyList<string>? texts,
                   uint[]? argbPerPt, float[]? heightMPerPt,
                   byte[]? hAlignPerPt, byte[]? vAlignPerPt, int count);

    /// <summary>抹掉一个组（点 + 线 + 文字一起）。幂等。</summary>
    void Clear(string group);

    /// <summary>请求重绘。**帧末调一次**（Set* 内部刻意不 render）。</summary>
    void RequestRender();
}

/// <summary>点标记样式（与内核 <c>AcGi::MarkerStyle</c> 序号一致）。</summary>
public enum SimMarkerStyle : byte
{
    Cross = 0,
    Plus = 1,
    /// <summary>空心方框。</summary>
    Square = 2,
    /// <summary>空心圆（内核按 16 段近似）。</summary>
    Circle = 3,
    /// <summary>「实心」方块（内核用 8 条短线模拟 —— overlay 是 line list，没有真填充）。</summary>
    Dot = 4,
}

/// <summary>接不上内核时的空转端（所有调用返回 false / 无副作用，绝不抛）。</summary>
public sealed class NullDynamicOverlay : ISimDynamicOverlay
{
    private readonly string _why;
    public NullDynamicOverlay(string why = "") => _why = why;

    public bool Available => false;
    public string StatusLabel => _why.Length > 0 ? _why : "动态 overlay 通道未接上（无 IPitDesignCapability）";
    public bool SetLines(string group, double[]? xyzFlat, uint[]? argbPerSeg, int segCount) => false;
    public bool SetMarkers(string group, double[]? xyzFlat, uint[]? argbPerPt,
                           float[]? pixelSizePerPt, byte[]? stylePerPt, int count) => false;
    public bool SetLabels(string group, double[]? xyzFlat, IReadOnlyList<string>? texts,
                          uint[]? argbPerPt, float[]? heightMPerPt,
                          byte[]? hAlignPerPt, byte[]? vAlignPerPt, int count) => false;
    public void Clear(string group) { }
    public void RequestRender() { }
}

/// <summary>接 <see cref="IPitDesignCapability"/> 的落地端（生产用）。</summary>
public sealed class PitDesignDynamicOverlay : ISimDynamicOverlay
{
    private readonly IPitDesignCapability _cap;
    public PitDesignDynamicOverlay(IPitDesignCapability cap) => _cap = cap ?? throw new ArgumentNullException(nameof(cap));

    public bool Available => true;
    public string StatusLabel => "动态 overlay 通道：已就位（批量组，逐帧 1 次/组）";

    public bool SetLines(string group, double[]? xyzFlat, uint[]? argbPerSeg, int segCount)
    { try { return _cap.SetOverlayLines(group, xyzFlat, argbPerSeg, segCount); } catch { return false; } }

    public bool SetMarkers(string group, double[]? xyzFlat, uint[]? argbPerPt,
                           float[]? pixelSizePerPt, byte[]? stylePerPt, int count)
    { try { return _cap.SetOverlayMarkers(group, xyzFlat, argbPerPt, pixelSizePerPt, stylePerPt, count); } catch { return false; } }

    public bool SetLabels(string group, double[]? xyzFlat, IReadOnlyList<string>? texts,
                          uint[]? argbPerPt, float[]? heightMPerPt,
                          byte[]? hAlignPerPt, byte[]? vAlignPerPt, int count)
    { try { return _cap.SetOverlayLabels(group, xyzFlat, texts, argbPerPt, heightMPerPt, hAlignPerPt, vAlignPerPt, count); } catch { return false; } }

    public void Clear(string group) { try { _cap.ClearOverlayGroup(group); } catch { } }

    public void RequestRender() { try { _cap.RequestOverlayRender(); } catch { } }
}

/// <summary>
/// 判据用的记录端：不碰内核，把每次推送原样存下来供逐位比较。
/// <para>★ 存的是**深拷贝的前 n 项**，不是调用方那个数组的引用 —— 动画侧刻意复用同一个
/// 大数组逐帧改写，存引用的话所有帧都会指向同一份内容，判据会全绿而实际在动。</para>
/// </summary>
public sealed class RecordingDynamicOverlay : ISimDynamicOverlay
{
    public sealed record Push(string Group, string Kind, int Count, double[] Xyz, uint[] Argb)
    {
        /// <summary>文字组才有（其余为空）。</summary>
        public string[] Texts { get; init; } = Array.Empty<string>();
    }

    private readonly Dictionary<string, Push> _state = new(StringComparer.Ordinal);

    /// <summary>逐次推送流水（含被抹掉的组：Count=0）。</summary>
    public List<Push> Pushes { get; } = new();
    /// <summary>Set*/Clear 的总调用次数（判据据此证明「每帧每组一次」）。</summary>
    public int Calls { get; private set; }
    /// <summary>RequestRender 次数。</summary>
    public int Renders { get; private set; }

    public bool Available => true;
    public string StatusLabel => "动态 overlay 通道：记录端（判据用，不碰内核）";

    /// <summary>某组当前的内容（没推过 = null）。</summary>
    public Push? StateOf(string group) => _state.TryGetValue(group, out var p) ? p : null;

    public bool SetLines(string group, double[]? xyzFlat, uint[]? argbPerSeg, int segCount)
        => Record(group, "line", 6, xyzFlat, argbPerSeg, segCount);

    public bool SetMarkers(string group, double[]? xyzFlat, uint[]? argbPerPt,
                           float[]? pixelSizePerPt, byte[]? stylePerPt, int count)
        => Record(group, "marker", 3, xyzFlat, argbPerPt, count);

    public bool SetLabels(string group, double[]? xyzFlat, IReadOnlyList<string>? texts,
                          uint[]? argbPerPt, float[]? heightMPerPt,
                          byte[]? hAlignPerPt, byte[]? vAlignPerPt, int count)
    {
        Calls++;
        if (string.IsNullOrEmpty(group)) return false;
        if (xyzFlat == null || texts == null || count <= 0)
        {
            var e = new Push(group, "label", 0, Array.Empty<double>(), Array.Empty<uint>());
            _state[group] = e; Pushes.Add(e);
            return true;
        }
        int cap = Math.Min(xyzFlat.Length / 3, texts.Count);
        if (count > cap) count = cap;
        var x = new double[count * 3];
        Array.Copy(xyzFlat, x, x.Length);
        var c = new uint[count];
        if (argbPerPt != null) Array.Copy(argbPerPt, c, Math.Min(count, argbPerPt.Length));
        var t = new string[count];
        for (int i = 0; i < count; i++) t[i] = texts[i] ?? "";
        var p = new Push(group, "label", count, x, c) { Texts = t };
        _state[group] = p; Pushes.Add(p);
        return true;
    }

    private bool Record(string group, string kind, int stride, double[]? xyz, uint[]? argb, int n)
    {
        Calls++;
        if (string.IsNullOrEmpty(group)) return false;
        if (xyz == null || n <= 0)
        {
            var empty = new Push(group, kind, 0, Array.Empty<double>(), Array.Empty<uint>());
            _state[group] = empty; Pushes.Add(empty);
            return true;
        }
        int cap = xyz.Length / stride;
        if (argb != null && argb.Length < cap) cap = argb.Length;
        if (n > cap) n = cap;

        var x = new double[n * stride];
        Array.Copy(xyz, x, x.Length);
        var c = new uint[n];
        if (argb != null) Array.Copy(argb, c, n);

        var p = new Push(group, kind, n, x, c);
        _state[group] = p; Pushes.Add(p);
        return true;
    }

    public void Clear(string group)
    {
        Calls++;
        if (string.IsNullOrEmpty(group)) return;
        var empty = new Push(group, "clear", 0, Array.Empty<double>(), Array.Empty<uint>());
        _state[group] = empty; Pushes.Add(empty);
    }

    public void RequestRender() => Renders++;
}

/// <summary>
/// 全局动态通道。第一次访问时从 <see cref="SimHost.PitDesign"/> 取能力自动接上；
/// 取不到就是空转端（不抛、不假装）。判据用 <see cref="Bind"/> 换成记录端。
/// </summary>
public static class SimDynamicOverlay
{
    private static readonly object _lock = new();
    private static ISimDynamicOverlay? _current;

    public static ISimDynamicOverlay Current
    {
        get
        {
            if (_current != null) return _current;
            lock (_lock)
            {
                if (_current != null) return _current;
                try
                {
                    var cap = SimHost.PitDesign;
                    _current = cap != null ? new PitDesignDynamicOverlay(cap)
                                           : new NullDynamicOverlay(SimHost.StatusLabel);
                }
                catch { _current = new NullDynamicOverlay(); }
                return _current;
            }
        }
    }

    /// <summary>换掉全局端（判据/测试用；传 null = 恢复自动探测）。</summary>
    public static void Bind(ISimDynamicOverlay? sink)
    {
        lock (_lock) { _current = sink; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  期内时钟
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 期内连续时钟 —— 「月/班之内」也能动起来的那个自由度。
///
/// <para><b>本仓库最容易搞错的一条口径，先说清楚：车流时钟与期次时钟是两个钟，不是一个。</b></para>
/// <list type="number">
///   <item><b>期次钟</b>：一期（月/班）压缩成几秒播完，管的是「挖了多少、堆了多高、推进了多远」。
///         压缩比 = 期长 ÷ 播放秒数，月粒度下是 10 万倍量级。</item>
///   <item><b>车流钟</b>（本类）：按 <b>真实矿山时间</b>走，1× = 实时。管的是「车长什么样地在跑」。</item>
/// </list>
/// <para><b>为什么必须分开</b>：拿期次钟去驱动车流，月粒度下 25 km/h 的车会以十万倍速掠过 ——
/// 一帧就跑完全程，屏幕上只剩闪烁。把车速降下来「看着舒服」则是**编速度**：图上量出来的
/// 车速不再是任何真实的东西。分开之后两个数各自都是真的，代价只是要在界面上讲一句：
/// <b>车流演的是「这一期的平均车流长什么样」，不是「把这一个月压缩播放」。</b></para>
///
/// <para>本类只管一件事：把墙上时钟的流逝换算成**矿山秒**并累加。它不知道期次、不知道车，
/// 所以谁都能拿它驱动（车流、设备移动、以后的转载机摆臂）。</para>
/// </summary>
public sealed class SimMineClock
{
    /// <summary>播放倍速（1 = 实时；4 = 一秒钟看四秒钟的矿山时间）。&lt;=0 视为暂停。</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>累计矿山时间 s。只增不减（除非 <see cref="Reset"/>）。</summary>
    public double MineSeconds { get; private set; }

    /// <summary>累计矿山时间 h（车流按小时口径算强度，省得到处 /3600）。</summary>
    public double MineHours => MineSeconds / 3600.0;

    /// <summary>
    /// 推进一步。<paramref name="wallSeconds"/> = 距上次推进过去的真实秒数。
    /// <para>负数/NaN/Inf 一律当 0（定时器补帧、系统时钟回拨都会给出这种值，
    /// 让时钟倒退会让所有粒子瞬移）。单步上限 1 s：窗口被拖动/断点停住之后，
    /// 定时器会一次性补上几十秒，不夹住的话粒子会跳一大段。</para>
    /// </summary>
    public void Advance(double wallSeconds)
    {
        if (double.IsNaN(wallSeconds) || double.IsInfinity(wallSeconds) || wallSeconds <= 0) return;
        if (wallSeconds > 1.0) wallSeconds = 1.0;
        double sp = Speed;
        if (double.IsNaN(sp) || double.IsInfinity(sp) || sp <= 0) return;
        MineSeconds += wallSeconds * sp;
    }

    /// <summary>归零（换期/换轨时用 —— 上一期的相位没有意义）。</summary>
    public void Reset() => MineSeconds = 0;

    /// <summary>界面一行：现在走到矿山时间的第几分钟，以及倍速。</summary>
    public string Label => $"车流钟 {MineSeconds / 60.0:0.0} 矿山分钟　·　{Speed:0.##}×（1× = 实时）";
}
