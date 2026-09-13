// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimOverlayComposer.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Platform.Capabilities;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  overlay 帧合成器 —— MINEABLE_AREA 这条 overlay 通道的**唯一**写入口
//
//  ── 为什么必须有它（这是一个已经咬过人的 bug）──
//  IPitDesignCapability.ShowMineableAreaOverlay 是**整通道替换**，不是追加：
//      PitDesignCapabilityImpl.cs:1018-1034
//        public void ShowMineableAreaOverlay(rings, rgbPerRing) {
//            EngineInterop.PitMine_ClearMineableAreaOverlay();     // ← 第一行就把通道清空
//            for (...) EngineInterop.AddMineableAreaRing(...);
//            EngineInterop.PitMine_RequestRender();
//        }
//  于是「谁后调，谁把前面所有人的图都擦掉」。多方各画各的时，现象是**有时候有有时候没有**
//  （取决于这一帧谁最后调），而且不报错、不抛异常，极难查。
//
//  ⇒ 纪律：**任何人不得直接调 ShowMineableAreaOverlay / ClearMineableAreaOverlay**。
//     各方按【图层键】把自己那份 rings+colors attach 进本合成器，合成器合并后每帧只推一次。
//
//  ── 本合成器仲裁得了谁、仲裁不了谁（不吹牛）──
//  仲裁得了：走本合成器 attach 的参与方（frame / equip / haul …）。
//  仲裁不了：**仍在直接调 capability 的既有窗口**——
//      · Modules/PlanLib/ShortTerm/ShortTermMineableAreaWindow.xaml.cs:463,471,505
//      · Modules/PlanLib/ShortTerm/ShortTermFieldWindow.xaml.cs:196,42
//      · Host/PitMineApp/MainWindow.PanelEvents.cs:193,227,264（直接调 EngineInterop）
//    它们和本合成器**互相**擦除，这是既有行为，本合成器不声称修好它。真要修，
//    只有让它们也改走 Set(key,…)（各占一个键即可，不需要改它们的几何代码）。
//    本合成器对这种外部擦除的自救手段是 <see cref="SimOverlayComposer.ForceNextFlush"/>。
//
//  ── 线程 ──
//  Set/Remove/Compose 有锁，可跨线程调；**Flush/Clear 必须在 UI 线程**（内部走 P/Invoke 到
//  渲染器并 RequestRender）。DispatcherTimer 的 tick 本来就在 UI 线程，照常用即可。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// overlay 通道的落地端。抽出来是为了让合成器本身**不依赖宿主能力**，
/// 判据可以在没有三维内核的环境里跑（见 <see cref="SimOverlayComposerCheck"/>）。
/// </summary>
public interface ISimOverlaySink
{
    /// <summary>把整通道内容替换为这一批环（实现方必须是「先清后画」的整通道语义）。</summary>
    void Show(IReadOnlyList<double[]> rings, IReadOnlyList<uint> rgbPerRing);

    /// <summary>清空整通道。幂等。</summary>
    void Clear();
}

/// <summary>接 <see cref="IPitDesignCapability"/> 的落地端（生产用）。</summary>
public sealed class PitDesignOverlaySink : ISimOverlaySink
{
    private readonly IPitDesignCapability _cap;
    public PitDesignOverlaySink(IPitDesignCapability cap) => _cap = cap ?? throw new ArgumentNullException(nameof(cap));

    public void Show(IReadOnlyList<double[]> rings, IReadOnlyList<uint> rgbPerRing)
        => _cap.ShowMineableAreaOverlay(rings, rgbPerRing);

    public void Clear() => _cap.ClearMineableAreaOverlay();
}

/// <summary>
/// 约定的图层键与叠放次序。<b>次序只由这里定，不由调用先后定</b> —— 见
/// <see cref="SimOverlayComposer.Compose"/> 的排序口径。
/// </summary>
public static class SimOverlayLayers
{
    /// <summary>逐期推进轮廓（期初灰 + 期末本色）。<see cref="PitDesignGeometryPort"/> 占用。</summary>
    public const string Frame = "frame";
    /// <summary>设备符号 / 本月在哪个采掘单元。</summary>
    public const string Equip = "equip";
    /// <summary>运输线。⚠ 开放折线在本通道有约束，先读 <see cref="SimOverlayComposer.OpenPolylineNote"/>。</summary>
    public const string Haul = "haul";

    /// <summary>
    /// 键的默认叠放序（小的先画 = 在下）。未登记的键统一 500，落在已知键之后，
    /// 同序再按键名序数排 —— 不会因为「谁先 attach」而变。
    /// </summary>
    public static int DefaultOrder(string key) => key switch
    {
        Frame => 0,      // 轮廓是底图
        Equip => 100,    // 设备压在轮廓上
        Haul => 200,     // 运输线最上
        _ => 500,
    };
}

/// <summary>一次合成的结果快照（不可变）。判据直接拿它做逐位比较。</summary>
public sealed class SimOverlayFrame
{
    internal SimOverlayFrame(double[][] rings, uint[] colors, ulong signature)
    { Rings = rings; Colors = colors; Signature = signature; }

    /// <summary>合并后的环（扁平 [x,y,z,...]）。与 <see cref="Colors"/> 等长。</summary>
    public IReadOnlyList<double[]> Rings { get; }
    /// <summary>合并后的逐环颜色 0xRRGGBB。</summary>
    public IReadOnlyList<uint> Colors { get; }
    /// <summary>内容指纹（FNV-1a/64，覆盖每个坐标的 IEEE 位型与每个颜色值）。仅用于变更检测/日志。</summary>
    public ulong Signature { get; }
    /// <summary>环数。</summary>
    public int Count => Rings.Count;
    /// <summary>空合成（没有任何参与方，或参与方都没给出合法环）。</summary>
    public bool IsEmpty => Rings.Count == 0;

    /// <summary>
    /// **逐位**相等（坐标按 IEEE 位型比，不是按容差比；NaN 位型相同也算相等）。
    /// 判据用这个，不用 Signature —— 指纹相等只是必要条件。
    /// </summary>
    public bool ContentEquals(SimOverlayFrame? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (Rings.Count != other.Rings.Count) return false;
        for (int i = 0; i < Rings.Count; i++)
        {
            if (Colors[i] != other.Colors[i]) return false;
            var a = Rings[i]; var b = other.Rings[i];
            if (a.Length != b.Length) return false;
            for (int j = 0; j < a.Length; j++)
                if (BitConverter.DoubleToInt64Bits(a[j]) != BitConverter.DoubleToInt64Bits(b[j])) return false;
        }
        return true;
    }

    internal static readonly SimOverlayFrame Empty =
        new(Array.Empty<double[]>(), Array.Empty<uint>(), Fnv.Seed);
}

/// <summary>
/// overlay 帧合成器：各方 <see cref="Set"/> 自己那份，合成器合并后**每帧只调一次**通道。
/// <para>
/// <b>数组所有权</b>：<see cref="Set"/> 进来的 <c>double[]</c> 被合成器直接持有（不深拷贝），
/// 调用方 attach 之后**不得再改数组内容**，否则「同输入两次合成结果相同」这条就不成立了。
/// 每帧重建数组（<see cref="PitDesignGeometryPort"/> 就是这么干的）是最省心的用法。
/// </para>
/// </summary>
public sealed class SimOverlayComposer
{
    // ── 单例（通道只有一条，仲裁者也只能有一个）──

    private static readonly object _bindLock = new();
    private static SimOverlayComposer? _current;

    /// <summary>
    /// 全局合成器。第一次访问时若还没 <see cref="Bind"/> 过，会尽力从
    /// <see cref="SimHost.PitDesign"/> 取能力自动接上；取不到就是**无落地端**的空转合成器
    /// （Set/Compose 照常工作，Flush 返回 false），不抛。
    /// </summary>
    public static SimOverlayComposer Current
    {
        get
        {
            if (_current != null) return _current;
            lock (_bindLock)
            {
                if (_current != null) return _current;
                ISimOverlaySink? sink = null;
                try { var cap = SimHost.PitDesign; if (cap != null) sink = new PitDesignOverlaySink(cap); }
                catch { sink = null; }
                _current = new SimOverlayComposer(sink);
                return _current;
            }
        }
    }

    /// <summary>
    /// 换掉全局合成器的落地端（判据/测试用；传 null = 空转）。
    /// <b>会连同已 attach 的内容一起重来</b>：旧通道内容先清干净再换。
    /// </summary>
    public static void Bind(ISimOverlaySink? sink)
    {
        lock (_bindLock)
        {
            try { _current?.Clear(); } catch { }
            _current = new SimOverlayComposer(sink);
        }
    }

    /// <summary>
    /// 还没有落地端时才接上（有了就什么都不做，返回 false）。
    /// 端口在构造时用它把**注入给自己的那个能力**交给合成器，不依赖 SimHost 的探测时序。
    /// </summary>
    public static bool BindIfUnbound(ISimOverlaySink sink)
    {
        if (sink == null) return false;
        lock (_bindLock)
        {
            var c = Current;                 // 注意：这一步可能已经自动接上了
            if (c.Available) return false;
            c._sink = sink;
            return true;
        }
    }

    // ── 实例 ──

    private sealed class Entry
    {
        public required string Key;
        public required int Order;
        public required double[][] Rings;
        public required uint[] Colors;
        public required List<string> Notes;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private ISimOverlaySink? _sink;

    private bool _dirty;
    private bool _forceNext;
    /// <summary>帧作用域嵌套深度（&gt;0 时 Flush 只攒不推，出最外层作用域统一推一次）。</summary>
    private int _deferDepth;
    /// <summary>本合成器是否往通道里真写过东西 —— 没写过就别去 Clear 通道（那会误伤非参与方）。</summary>
    private bool _owns;

    public SimOverlayComposer(ISimOverlaySink? sink) => _sink = sink;

    /// <summary>有落地端吗（没有 = 空转，Flush 恒返回 false）。</summary>
    public bool Available => _sink != null;

    /// <summary>当前已 attach 的键（按合成次序，稳定）。</summary>
    public IReadOnlyList<string> Keys
    {
        get { lock (_lock) return Ordered().Select(e => e.Key).ToArray(); }
    }

    /// <summary>上一次真正推到通道的内容（还没推过 = <see cref="SimOverlayFrame.IsEmpty"/> 的空帧）。</summary>
    public SimOverlayFrame LastPush { get; private set; } = SimOverlayFrame.Empty;

    /// <summary>真正调到 <see cref="ISimOverlaySink.Show"/>/<see cref="ISimOverlaySink.Clear"/> 的次数（判据看这个证明「每帧一次」）。</summary>
    public int PushCount { get; private set; }

    /// <summary>各方 attach 时被丢弃/降级的记录（例如非法环、颜色数不齐）。一条都不吞，界面可以直接显示。</summary>
    public IReadOnlyList<string> Notes
    {
        get { lock (_lock) return Ordered().SelectMany(e => e.Notes).ToArray(); }
    }

    /// <summary>
    /// attach / 覆盖某个键的内容。同一个键重复 Set = 覆盖（不是追加），
    /// 这样调用方每帧无脑 Set 自己那份即可，不用管上一帧。
    /// </summary>
    /// <param name="key">图层键，见 <see cref="SimOverlayLayers"/>。空/空白会被拒（返回 false）。</param>
    /// <param name="rings">扁平 [x,y,z,...] 的环。null/空 = 该键这一帧什么都不画（但键仍在）。</param>
    /// <param name="rgbPerRing">逐环颜色 0xRRGGBB。为 null 或不够长时用 <paramref name="defaultRgb"/> 补齐，并记一条 Note。</param>
    /// <param name="order">叠放序；不给就用 <see cref="SimOverlayLayers.DefaultOrder"/>。</param>
    /// <param name="defaultRgb">颜色缺位时的兜底色（默认与内核一致的绿）。</param>
    public bool Set(string key, IReadOnlyList<double[]>? rings,
                    IReadOnlyList<uint>? rgbPerRing = null, int? order = null, uint defaultRgb = 0x26D973u)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        var keep = new List<double[]>(rings?.Count ?? 0);
        var cols = new List<uint>(rings?.Count ?? 0);
        var notes = new List<string>();
        int badShape = 0, badNumber = 0, tooShort = 0, missingColor = 0;

        if (rings != null)
        {
            for (int i = 0; i < rings.Count; i++)
            {
                var r = rings[i];
                // 丢弃要记账：内核对这些是**静默忽略**的，不记就等于凭空少了几条线还没人知道。
                if (r == null) { tooShort++; continue; }
                if (r.Length % 3 != 0) { badShape++; continue; }          // 不截断——截断会造出一个没人要过的点
                if (r.Length < 6) { tooShort++; continue; }               // 内核 n<2 直接 return
                bool ok = true;
                for (int j = 0; j < r.Length; j++)
                    if (double.IsNaN(r[j]) || double.IsInfinity(r[j])) { ok = false; break; }
                if (!ok) { badNumber++; continue; }                       // NaN 转 float 会污染整帧渲染

                uint rgb;
                if (rgbPerRing != null && i < rgbPerRing.Count) rgb = rgbPerRing[i];
                else { rgb = defaultRgb; missingColor++; }

                keep.Add(r);
                cols.Add(rgb);
            }
        }

        if (badShape > 0) notes.Add($"overlay[{key}]：{badShape} 条环坐标数不是 3 的倍数，已整条丢弃（不截断）。");
        if (tooShort > 0) notes.Add($"overlay[{key}]：{tooShort} 条环不足 2 点，内核画不出来，已丢弃。");
        if (badNumber > 0) notes.Add($"overlay[{key}]：{badNumber} 条环含 NaN/Inf 坐标，已丢弃（转 float 会毁掉整帧渲染）。");
        if (missingColor > 0) notes.Add($"overlay[{key}]：{missingColor} 条环没给颜色，按兜底色 0x{defaultRgb:X6} 画。");

        lock (_lock)
        {
            _entries[key] = new Entry
            {
                Key = key,
                Order = order ?? SimOverlayLayers.DefaultOrder(key),
                Rings = keep.ToArray(),
                Colors = cols.ToArray(),
                Notes = notes,
            };
            _dirty = true;
        }
        return true;
    }

    /// <summary>撤掉某个键（只撤自己那份，别人的原样留着）。返回是否真撤掉了。</summary>
    public bool Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        lock (_lock)
        {
            if (!_entries.Remove(key)) return false;
            _dirty = true;
            return true;
        }
    }

    /// <summary>某个键在不在。</summary>
    public bool Has(string key) { lock (_lock) return !string.IsNullOrWhiteSpace(key) && _entries.ContainsKey(key); }

    /// <summary>
    /// 纯合成：把各键内容按稳定次序合并成一帧，**不碰通道**。
    /// <para>
    /// 次序口径：先按 <c>Order</c> 升序，同序按**键名序数**升序。也就是说合成结果只取决于
    /// 「有哪些键 + 各自的内容」，**与谁先 attach 无关**；键内环序保持调用方给的顺序。
    /// 这是「不闪」的充要条件。
    /// </para>
    /// </summary>
    public SimOverlayFrame Compose()
    {
        lock (_lock)
        {
            var ordered = Ordered();
            int n = 0;
            foreach (var e in ordered) n += e.Rings.Length;
            if (n == 0) return SimOverlayFrame.Empty;

            var rings = new double[n][];
            var colors = new uint[n];
            ulong sig = Fnv.Seed;
            int k = 0;
            foreach (var e in ordered)
                for (int i = 0; i < e.Rings.Length; i++)
                {
                    var r = e.Rings[i];
                    rings[k] = r;
                    colors[k] = e.Colors[i];
                    sig = Fnv.Step(sig, (ulong)r.Length);
                    for (int j = 0; j < r.Length; j++) sig = Fnv.Step(sig, (ulong)BitConverter.DoubleToInt64Bits(r[j]));
                    sig = Fnv.Step(sig, e.Colors[i]);
                    k++;
                }
            return new SimOverlayFrame(rings, colors, sig);
        }
    }

    /// <summary>
    /// 合成并推到通道。**每帧只该由帧驱动方调一次**。
    /// <para>
    /// 内容与上次推的逐位相同时默认跳过这次 P/Invoke（overlay 是保留态，重推同样的东西没有意义，
    /// 而且一帧上百个环意味着上百次 P/Invoke）。<see cref="LastPush"/> 无论跳没跳都会更新成
    /// 「通道当前应有的内容」，所以判据看 <see cref="LastPush"/> 是准的。
    /// </para>
    /// </summary>
    /// <param name="force">true = 无视「内容没变」直接重推（外部有人擦了通道时用）。</param>
    /// <returns>是否真的调到了落地端。</returns>
    public bool Flush(bool force = false)
    {
        // 快路：自上次 Flush 起没人 Set/Remove 过 ⇒ 合成结果不可能变（数组所有权契约保证），
        // 连 Compose 的那趟指纹都省掉。播放暂停/同一帧重绘时这条最常走。
        lock (_lock)
        {
            // 帧作用域内：各方随手 Flush 都只记账，出最外层作用域才真推 —— 这样
            // 「每帧只调一次通道」不依赖各方守规矩，谁多调都不会变成多次 P/Invoke。
            if (_deferDepth > 0) { if (force) _forceNext = true; return false; }
            if (!_dirty && !force && !_forceNext) return false;
        }

        var frame = Compose();
        bool forceNow;
        lock (_lock) { forceNow = force || _forceNext; _forceNext = false; _dirty = false; }

        // 一次都没写过、现在也没东西可写 → 绝不去 Clear 通道（那会误伤非参与方的图）
        if (frame.IsEmpty && !_owns) { LastPush = frame; return false; }

        if (!forceNow && frame.ContentEquals(LastPush)) { LastPush = frame; return false; }

        var sink = _sink;
        LastPush = frame;
        if (sink == null) return false;

        try
        {
            if (frame.IsEmpty) { sink.Clear(); _owns = false; }
            else { sink.Show(frame.Rings, frame.Colors); _owns = true; }
            PushCount++;
            return true;
        }
        catch
        {
            // 落地端炸了不该拖垮播放；下一帧强制重推一次，别让 LastPush 谎报通道状态
            lock (_lock) { _forceNext = true; }
            return false;
        }
    }

    /// <summary>下一次 <see cref="Flush"/> 强制重推（有人在合成器外面动了通道时自救）。</summary>
    public void ForceNextFlush() { lock (_lock) { _forceNext = true; } }

    /// <summary>
    /// 开一个**帧作用域**：作用域内各方怎么 <see cref="Set"/>/<see cref="Flush"/> 都不真推，
    /// 退出最外层作用域时按合并结果推**一次**。帧驱动方（渲染那一趟）这样用：
    /// <code>
    /// using (SimOverlayComposer.Current.BeginFrame())
    /// {
    ///     _port.ApplyFrame(frame);   // frame 键
    ///     _equip.ApplyFrame(frame);  // equip 键
    ///     _haul.ApplyFrame(frame);   // haul 键
    /// }   // ← 这里才推，且只推一次
    /// </code>
    /// 不用它也不会画错（每次 Flush 推的都是合并后的全量），只是通道会被调多次。
    /// 可嵌套。必须在 UI 线程用。
    /// </summary>
    public IDisposable BeginFrame()
    {
        lock (_lock) { _deferDepth++; }
        return new FrameScope(this);
    }

    private sealed class FrameScope : IDisposable
    {
        private SimOverlayComposer? _owner;
        public FrameScope(SimOverlayComposer owner) => _owner = owner;
        public void Dispose()
        {
            var o = _owner;
            if (o == null) return;               // Dispose 幂等
            _owner = null;
            bool outermost;
            lock (o._lock) { o._deferDepth--; outermost = o._deferDepth <= 0; if (outermost) o._deferDepth = 0; }
            if (outermost) o.Flush();
        }
    }

    /// <summary>有没有未推送的改动。</summary>
    public bool IsDirty { get { lock (_lock) return _dirty; } }

    /// <summary>撤掉全部键并清空通道（只在本合成器真写过通道时才去 Clear）。幂等。</summary>
    public void Clear()
    {
        lock (_lock) { _entries.Clear(); _dirty = false; _forceNext = false; }
        LastPush = SimOverlayFrame.Empty;
        if (!_owns) return;
        _owns = false;
        var sink = _sink;
        if (sink == null) return;                 // 空转时不许虚报 PushCount
        try { sink.Clear(); PushCount++; } catch { }
    }

    private List<Entry> Ordered()
        => _entries.Values
                   .OrderBy(e => e.Order)
                   .ThenBy(e => e.Key, StringComparer.Ordinal)
                   .ToList();

    // ── 开放折线（运输线）在本通道能不能画：结论与用法 ───────────────────────────

    /// <summary>
    /// <b>不能直接画。</b>本通道吃的是「线环」，内核 <c>PitMine_AddMineableAreaRing</c>
    /// （Kernel/xllAcEd/src/API/xllAcEd.cpp:7791）对 n≥3 的点串**无条件**加一张
    /// 半透明填充面（alpha 0.22），且 <c>PitDesignCapabilityImpl</c> 调它时把 closed 写死成 true
    /// （PitDesignCapabilityImpl.cs:1029），会把首点补到末尾闭合描边。
    /// 也就是说：把一条 K(≥3) 点的运输线当一条 ring 传进来，得到的是**一个闭合多边形 + 一张填充面**，
    /// 不是一条线。
    /// <para>
    /// <b>可用的绕法（内核证据）</b>：n==2 时那两个分支都不成立 —— 不填面、不闭合 —— 画出来就是
    /// 一条纯线段。所以把开放折线拆成逐段两点即可，见 <see cref="SplitOpenPolyline"/>。
    /// <b>代价按总点数计</b>：K 点的线 → K-1 个 ring → K-1 次 P/Invoke。几条高亮路径没问题；
    /// 整张路网（几十条 × 几十点 ≈ 上千次/帧）会顶穿 30~40ms 的帧预算 ⇒ 那种规模请走
    /// <c>IEntityCapability.BuildColoredMeshOnLayer</c> 建 mesh（另起自己的图层，别碰
    /// <c>__SIM_PIT__</c>/<c>__SIM_DUMP__</c>）。
    /// </para>
    /// <para>
    /// <b>另一条通道</b>：<c>IPitDesignCapability.ShowExpandDirectionOverlay</c>（内核 EXPAND_DIR 组）
    /// 天生画开放折线且不填面 —— 但它同样是整通道替换，且**已被
    /// MineAssLib/Views/ExpandDirectionPreview.cs:57 占用**，直接抢会重演同一个 bug；
    /// 而且它整批只吃一个颜色。要用得先给它也配一个合成器。
    /// </para>
    /// </summary>
    public const string OpenPolylineNote =
        "MINEABLE_AREA 通道只画闭合环并对 n≥3 强制填充面（xllAcEd.cpp:7791 + PitDesignCapabilityImpl.cs:1029 写死 closed=true）。"
      + "开放折线要么拆成逐段两点（SplitOpenPolyline，代价 = 点数）、要么走 IEntityCapability 建 mesh。"
      + "EXPAND_DIR 通道能画开放折线但已被 ExpandDirectionPreview 占用且整批单色。";

    /// <summary>
    /// 开放折线 → 逐段两点的 ring 列表（本通道画线的唯一正确姿势，理由见 <see cref="OpenPolylineNote"/>）。
    /// 相邻重合点会被跳过（否则内核收到一条零长段）。点数不足 2 / 坐标非法 → 返回空列表。
    /// </summary>
    /// <param name="xyzFlat">扁平 [x,y,z,...]。</param>
    public static List<double[]> SplitOpenPolyline(double[]? xyzFlat)
    {
        var segs = new List<double[]>();
        if (xyzFlat == null || xyzFlat.Length < 6 || xyzFlat.Length % 3 != 0) return segs;
        int n = xyzFlat.Length / 3;
        for (int i = 0; i + 1 < n; i++)
        {
            double x0 = xyzFlat[3 * i], y0 = xyzFlat[3 * i + 1], z0 = xyzFlat[3 * i + 2];
            double x1 = xyzFlat[3 * i + 3], y1 = xyzFlat[3 * i + 4], z1 = xyzFlat[3 * i + 5];
            if (double.IsNaN(x0 + y0 + z0 + x1 + y1 + z1) || double.IsInfinity(x0 + y0 + z0 + x1 + y1 + z1)) continue;
            if (x0 == x1 && y0 == y1 && z0 == z1) continue;   // 零长段
            segs.Add(new[] { x0, y0, z0, x1, y1, z1 });
        }
        return segs;
    }
}

/// <summary>FNV-1a/64。只用于内容指纹（变更检测/日志），相等性判定看 <see cref="SimOverlayFrame.ContentEquals"/>。</summary>
internal static class Fnv
{
    public const ulong Seed = 14695981039346656037UL;
    public static ulong Step(ulong h, ulong v)
    {
        for (int i = 0; i < 8; i++) { h ^= (v >> (i * 8)) & 0xFF; h *= 1099511628211UL; }
        return h;
    }
}
