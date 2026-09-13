// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/SimGeometryPort.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using PitMine3D.Kylin.Platform.Capabilities;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  三维接口端口 —— UI 不依赖三维是否可用。
//
//  ── 三维内核能力调研结论（2026-08，对 IPitDesignCapability 逐方法核过）──
//
//  【能接的】
//   · GetMineableRegions()          → 采场/排土场区域边界（Name + Category + 平面环）。
//                                     Category 已带采/排极性（pit|mineable / *_dump），
//                                     正是推进方向（内缩/外扩）的判据。本包用它取真实轮廓。
//   · ShowMineableAreaOverlay(rings) → 把逐期推进后的轮廓推回三维视口显示。
//     ClearMineableAreaOverlay()       **非破坏性**：overlay 不建实体、不进 Undo 栈、可整批重画，
//                                     天然适配「逐帧刷新」。本端口的 ApplyFrame 走的就是它。
//
//  【用不上的（用了会绕回死胡同，别再试）】
//   · CarveStripFromSelection / CarveStripModelForRegion 没有「推进游标」：
//     每次调用都从同一批台阶线起算，重复调用产出的是**位置重叠的多份采掘带体**。
//   · ExpandPitBenches / ExpandDumpBenches 返回的 PMEP/PMED 载荷是**纯几何**
//     （逐台阶 toe/crest 点串 + 网格顶点/索引），**不含新建实体 handle**，
//     而且按台阶参数放坡到停止条件，不接受「按本期体积推进 d 米」这种驱动 —— 不适合逐期推演。
//
//  ── 真三维体：走 IEntityCapability，不需要改内核（2026-08 复核结论）──
//   · BuildColoredMeshOnLayer(srcHandle, worldXyz, tris, vertexRgb, lift, layer)
//       调用方给顶点/索引/逐顶点色，直接建一张三角网落到指定图层，返回新实体 handle。
//       srcHandle 只用于取 basePoint/实体色/透明度，随便一张现有 mesh 即可。
//   · GetHandlesByLayer / DeleteEntities / SetEntitiesVisible → 按图层整批收回，可反复重来。
//   于是层体的「建—收」闭环成立：几何在托管层算（<see cref="SimSolidBuilder"/>），
//   入库/清除在 <see cref="SolidGeometryPort"/>。
//
//  ── 但它**入库并支持 Undo**，所以绝不逐帧建 ──
//   播放/拖时间轴几十帧就会把 Undo 栈刷爆。因此两条路分工固定：
//     · 播放 / 拖时间轴  → <see cref="PitDesignGeometryPort"/> 的 overlay（非破坏性，不进 Undo 栈）；
//     · 「生成本期三维体」按钮 → <see cref="SolidGeometryPort"/> 对**当前这一帧**建真实体。
//   界面必须把这个取舍写明，不能让人以为播放就是三维体动画。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 三维驱动端口。UI 只认这个接口：接得上就驱动三维，接不上就退平面示意，
/// 两条路都不影响数据层。
/// </summary>
public interface ISimGeometryPort
{
    /// <summary>三维驱动当前是否可用。</summary>
    bool Available { get; }

    /// <summary>状态/降级原因（界面直接显示这一句）。</summary>
    string StatusLabel { get; }

    /// <summary>把某一帧的推演状态推到三维。不可用时必须是无副作用的空操作。</summary>
    void ApplyFrame(SimFrame frame);

    /// <summary>清除本端口在三维里留下的一切显示。幂等。</summary>
    void Reset();

    /// <summary>
    /// 绑定平面场景上下文（区域轮廓 + 时间轴 + 推进模式）。
    /// 默认空实现：不需要几何上下文的端口（如 <see cref="NullGeometryPort"/>）无需重写。
    /// </summary>
    void Bind(SimRegionSet regions, SimTimeline timeline, SimAdvanceMode mode, double azimuthDeg) { }
}

/// <summary>什么都不做的端口（三维不可用时的默认实现）。</summary>
public sealed class NullGeometryPort : ISimGeometryPort
{
    public NullGeometryPort(string? reason = null)
        => StatusLabel = string.IsNullOrWhiteSpace(reason) ? "三维驱动不可用，已退平面推进示意" : reason!;

    public bool Available => false;
    public string StatusLabel { get; }
    public void ApplyFrame(SimFrame frame) { }
    public void Reset() { }
}

/// <summary>
/// 接 <see cref="IPitDesignCapability"/> 的端口：把逐期推进后的采场/排土场轮廓
/// 以 **overlay** 形式推回三维视口（非破坏性，不建实体、不进 Undo 栈）。
/// </summary>
/// <remarks>
/// <b>不直接调 capability</b>：overlay 是整通道替换（<c>ShowMineableAreaOverlay</c> 第一行就 Clear），
/// 谁后调谁把别人的图擦掉。本端口只往 <see cref="SimOverlayComposer"/> 的
/// <see cref="SimOverlayLayers.Frame"/> 键上 attach 自己那份，由合成器每帧统一推一次；
/// 设备符号 / 运输线各占别的键，互不擦除。
/// </remarks>
public sealed class PitDesignGeometryPort : ISimGeometryPort
{
    // 刻意**不**保留 IPitDesignCapability 字段：留着它就等于把「直接调 ShowMineableAreaOverlay」
    // 这条整通道替换的路继续摆在手边，早晚有人顺手调回去。通道只经 SimOverlayComposer。
    private SimRegionSet? _regions;
    private SimTimeline? _timeline;
    private SimAdvanceMode _mode = SimAdvanceMode.Uniform;
    private double _azimuth;
    private bool _dirty;

    // 配色与 PlanLib「采场/排土场圈定」一致，看图的人不用换脑子
    private const uint RgbPit = 0xD85A30;        // 采场 橙
    private const uint RgbExternal = 0x2E6FCF;   // 外排土场 蓝
    private const uint RgbInternal = 0x1D9E75;   // 内排土场 青
    private const uint RgbAlert = 0xE23B3B;      // 库容见顶 红
    private const uint RgbOrigin = 0x8A8F98;     // 原始（期初）轮廓 灰

    public PitDesignGeometryPort(IPitDesignCapability cap)
    {
        // 把**注入给本端口的这个**能力交给合成器（还没落地端时才接），
        // 不依赖 SimHost 的探测时序。已经接上了就沿用，绝不换掉别人接好的那条通道。
        try { SimOverlayComposer.BindIfUnbound(new PitDesignOverlaySink(cap)); } catch { }
    }

    public string StatusLabel { get; private set; } = "三维端口未绑定";

    public bool Available
    {
        get
        {
            if (_regions == null || _regions.IsEmpty) return false;
            // 示意图形的坐标是本包造的，推进三维视口只会画出一堆和现场对不上的框——宁可不画。
            if (_regions.AllSynthetic) return false;
            return true;
        }
    }

    public void Bind(SimRegionSet regions, SimTimeline timeline, SimAdvanceMode mode, double azimuthDeg)
    {
        _regions = regions;
        _timeline = timeline;
        _mode = mode;
        _azimuth = azimuthDeg;

        if (regions.IsEmpty)
            StatusLabel = "三维端口：无区域边界可驱动（未圈画可采区域）→ 只走平面示意";
        else if (regions.AllSynthetic)
            StatusLabel = "三维端口：当前只有示意轮廓（非真实边界），不推送三维 → 只走平面示意";
        else
            StatusLabel = $"三维端口：已接 IPitDesignCapability，逐期轮廓以 overlay 推送三维视口（{regions.Regions.Count} 块区域，非破坏性）";
    }

    public void ApplyFrame(SimFrame frame)
    {
        if (!Available || _regions == null || _timeline == null) return;
        try
        {
            var scene = SimPlanScene.Build(_regions, _timeline, frame.Index, _mode, _azimuth);
            var rings = new List<double[]>();
            var colors = new List<uint>();

            foreach (var s in scene.Shapes)
            {
                double z = double.IsNaN(s.Region.Z) ? 0 : s.Region.Z;

                // 期初轮廓画灰，期末轮廓画本色 —— 一眼看出「这一期推了多远」
                if (s.Before.Count >= 3 && s.CumAdvanceM > 1e-6)
                {
                    rings.Add(Flat(s.Before, z));
                    colors.Add(RgbOrigin);
                }
                if (s.After.Count < 3) continue;
                rings.Add(Flat(s.After, z));
                colors.Add(s.Alert ? RgbAlert
                         : s.Region.IsInternalDump ? RgbInternal
                         : s.Region.IsDump ? RgbExternal
                         : RgbPit);
            }

            // 走合成器：只 attach 本端口的 frame 那份，合成器合并全部参与方后每帧推一次。
            // 这里绝不调 _cap.ShowMineableAreaOverlay —— 那会把设备/运输线的图一起擦掉。
            var composer = SimOverlayComposer.Current;
            composer.Set(SimOverlayLayers.Frame, rings, colors);
            composer.Flush();
            _dirty = rings.Count > 0;
        }
        catch (Exception ex)
        {
            StatusLabel = $"三维端口推送失败（{ex.GetType().Name}），已退平面示意";
        }
    }

    public void Reset()
    {
        if (!_dirty) return;
        // 只撤自己那一份；别的键（设备/运输线）原样留着。合成器发现整通道空了才会去 Clear。
        try
        {
            var composer = SimOverlayComposer.Current;
            composer.Remove(SimOverlayLayers.Frame);
            composer.Flush();
        }
        catch { }
        _dirty = false;
    }

    /// <summary>平面环 + 代表高程 → 内核要的扁平 [x,y,z,...]。</summary>
    private static double[] Flat(IReadOnlyList<SimPoint> ring, double z)
    {
        var a = new double[ring.Count * 3];
        for (int i = 0; i < ring.Count; i++)
        {
            a[3 * i] = ring[i].X;
            a[3 * i + 1] = ring[i].Y;
            a[3 * i + 2] = z;
        }
        return a;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  实体端口 —— 把一期的推进量建成**真三维体**（入库、可 Undo、可整批收回）
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一次「生成本期三维体」的结果（界面直接照它写状态栏）。</summary>
public sealed class SimSolidResult
{
    /// <summary>至少建成了一个层体。</summary>
    public bool Ok => Built > 0;
    /// <summary>建成并入库的层体数。</summary>
    public int Built { get; set; }
    /// <summary>跳过的区块数（几何不成立 / 入库失败，原因见 <see cref="Messages"/>）。</summary>
    public int Skipped { get; set; }
    /// <summary>本次开工前清掉的旧层体数（非累计模式）。</summary>
    public int Deleted { get; set; }
    /// <summary>逐区层体（含体积自检结论）。</summary>
    public List<SimSolidLayer> Layers { get; } = new();
    /// <summary>人读的降级/跳过原因，一条都不吞。</summary>
    public List<string> Messages { get; } = new();
    /// <summary>各层体里最严重的体积自检结论。</summary>
    public SimCheckLevel WorstCheck { get; set; } = SimCheckLevel.NotAvailable;
    /// <summary>状态栏一行。</summary>
    public string Summary { get; set; } = "";

    /// <summary>物料着色源的状态 / 降级原因（界面直接显示这一句）。</summary>
    public string MaterialLabel { get; set; } = "";
    /// <summary>本次真的按块体煤岩分色的层体数（0 = 全部回落到去向类型着色）。</summary>
    public int MaterialColoredLayers => Layers.Count(l => l.MaterialColored);
    /// <summary>全部层体合计跳过的零厚度环段数（治 z-fight，对体积无影响）。</summary>
    public int SkippedZeroSegments => Layers.Sum(l => l.SkippedZeroSegments);
    /// <summary>入库层体里网格自检（棱台解析 ↔ 散度积分）没通过的个数 —— 大于 0 就是几何有问题。</summary>
    public int MeshCheckFailures => Layers.Count(l => l.Handle != 0 && l.HasGeometry && !l.MeshWatertight);
}

/// <summary>三维实体端口：显式地把某一期建成真三维体，并能整批收回。</summary>
public interface ISimSolidPort
{
    /// <summary>能建真三维体吗。</summary>
    bool Available { get; }
    /// <summary>状态 / 不可用原因（按钮的 ToolTip 与置灰提示直接用这一句）。</summary>
    string StatusLabel { get; }
    /// <summary>
    /// 是否按**块体模型的煤岩类别**给层体逐顶点分色（false = 一律按去向类型着色）。
    /// 打开也不保证生效：没有活动块体模型 / 该模型无煤岩属性列时整条链降级，结果里会写清是哪一环断的。
    /// </summary>
    bool MaterialColoring { get; set; }
    /// <summary>绑定几何上下文（与轮廓端口同一份区域/时间轴/推进模式）。</summary>
    void Bind(SimRegionSet regions, SimTimeline timeline, SimAdvanceMode mode, double azimuthDeg);
    /// <summary>把当前这一帧建成真实体。<paramref name="cumulative"/>=true 时不清上期（叠层）。</summary>
    SimSolidResult MaterializeFrame(SimFrame frame, bool cumulative);
    /// <summary>清除本端口建过的全部层体。返回清掉的实体数。幂等。</summary>
    int ClearSolids();
}

/// <summary>三维实体端口不可用时的空实现。</summary>
public sealed class NullSolidPort : ISimSolidPort
{
    public NullSolidPort(string? reason = null)
        => StatusLabel = string.IsNullOrWhiteSpace(reason) ? "三维层体不可用" : reason!;

    public bool Available => false;
    public string StatusLabel { get; }
    public bool MaterialColoring { get; set; } = true;
    public void Bind(SimRegionSet regions, SimTimeline timeline, SimAdvanceMode mode, double azimuthDeg) { }
    public SimSolidResult MaterializeFrame(SimFrame frame, bool cumulative)
    {
        var r = new SimSolidResult { Summary = StatusLabel };
        r.Messages.Add(StatusLabel);
        return r;
    }
    public int ClearSolids() => 0;
}

/// <summary>
/// 接 <see cref="IEntityCapability"/> 的实体端口：把某一期的采场挖除层 / 排土堆填层
/// 三角化成封闭层体，落到专用图层。
/// <para>
/// **与轮廓端口的分工**：这里建的是入库实体（进 Undo 栈），只在用户显式点按钮时跑一次；
/// 播放/拖时间轴仍走 <see cref="PitDesignGeometryPort"/> 的 overlay。
/// </para>
/// <para>
/// **收回策略**：全部层体只落在 <see cref="LayerPit"/> / <see cref="LayerDump"/> 两个专用图层，
/// 清除时 GetHandlesByLayer 取回 + DeleteEntities 整批删 —— 不依赖本端口自己记的 handle 列表，
/// 所以窗口重开、甚至上次会话残留的层体也能被清干净（本端口记的 handle 只是并集里的一份保险）。
/// </para>
/// </summary>
public sealed class SolidGeometryPort : ISimSolidPort
{
    /// <summary>采场挖除层专用图层（清除时按图层整批删，别往里放别的东西）。</summary>
    public const string LayerPit = "__SIM_PIT__";
    /// <summary>排土堆填层专用图层。</summary>
    public const string LayerDump = "__SIM_DUMP__";

    /// <summary>AcDbEntityType.TriangleMesh。</summary>
    private const int TypeIdTriangleMesh = 6;

    private readonly IEntityCapability _ent;
    private SimRegionSet? _regions;
    private SimTimeline? _timeline;
    private SimAdvanceMode _mode = SimAdvanceMode.Uniform;
    private double _azimuth;

    /// <summary>样式源（取 basePoint/实体色/透明度）。0 = 还没找到。</summary>
    private ulong _srcHandle;
    /// <summary>本端口建过的层体 handle（收回时与图层查询取并集）。</summary>
    private readonly List<ulong> _built = new();
    private bool _srcOk;
    private string _reason = "三维层体端口未绑定";

    /// <summary>上次样式源探测的时刻（节流用）。</summary>
    private DateTime _lastProbe = DateTime.MinValue;
    /// <summary>
    /// 样式源复验的最小间隔。
    /// <para>
    /// 复验本身不贵（命中缓存时只是一次 TryGetEntityAABB），但 <see cref="Available"/> 会被
    /// 右侧面板在**每一帧**读到（播放时 10 次/秒），全扫一遍文档实体就太贵了。
    /// 所以：缓存命中直接过，缓存失效才重扫，且两次重扫至少隔这么久。
    /// </para>
    /// </summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(2);

    /// <summary>物料着色源（块体模型）。每次建体前重新探测一次，用户中途换活动模型也能跟上。</summary>
    private SimMaterialColorProvider? _material;

    /// <inheritdoc/>
    public bool MaterialColoring { get; set; } = true;

    public SolidGeometryPort(IEntityCapability ent) => _ent = ent;

    public bool Available
    {
        get
        {
            if (_regions == null || _regions.IsEmpty) return false;
            // 示意轮廓的坐标是本包造的，建成实体会在图里留下一堆和现场对不上的方盒子 —— 宁可不建。
            if (_regions.AllSynthetic) return false;
            EnsureSource(force: false);
            return _srcOk;
        }
    }

    public string StatusLabel => Available
        ? $"三维层体：可用（图层 {LayerPit} / {LayerDump}，显式生成、可整批清除）"
        : _reason;

    public void Bind(SimRegionSet regions, SimTimeline timeline, SimAdvanceMode mode, double azimuthDeg)
    {
        _regions = regions;
        _timeline = timeline;
        _mode = mode;
        _azimuth = azimuthDeg;

        if (regions.IsEmpty)
        {
            _srcOk = false;
            _reason = "三维层体：无区域边界可建体（未圈画可采区域）";
            return;
        }
        if (regions.AllSynthetic)
        {
            _srcOk = false;
            _reason = "三维层体：当前只有示意轮廓（非真实边界），不往图里建实体";
            return;
        }
        EnsureSource(force: true);
    }

    /// <summary>
    /// 样式源的**便宜复验/重探**。
    /// <para>
    /// 原来只在 <see cref="Bind"/> 里探一次：用户开着窗口去导入了一张现状面，回来按钮还是灰的，
    /// 必须手动点「刷新数据」整条链重建 —— 这不合理。现在每次建体前（以及面板每次读 Available 时）
    /// 都复验一次，节流 <see cref="ProbeInterval"/> 避免每帧重扫全表。
    /// </para>
    /// </summary>
    private void EnsureSource(bool force)
    {
        if (_regions == null || _regions.IsEmpty || _regions.AllSynthetic) return;

        // 缓存命中：只花一次 AABB 查询确认样式源还活着
        if (_srcOk && _srcHandle != 0 && !force)
        {
            if (DateTime.UtcNow - _lastProbe < ProbeInterval) return;
            try
            {
                if (_ent.TryGetEntityAABB(_srcHandle, out _, out _)) { _lastProbe = DateTime.UtcNow; return; }
            }
            catch { }
            _srcHandle = 0; _srcOk = false;      // 样式源被删了，落到下面重扫
        }
        else if (!force && DateTime.UtcNow - _lastProbe < ProbeInterval)
        {
            return;                              // 上次没找到，先别急着再扫一遍全表
        }

        _lastProbe = DateTime.UtcNow;
        // ★ _srcOk 只表示「有没有找到样式源」，**不表示能不能建体** —— 没有样式源照建（见 MaterializeFrame）。
        //   所以这里不再把它写进 _reason 当成不可用的理由，否则界面会说"三维层体不可用"，而其实建得出来。
        _srcOk = TryResolveSource(out _, out _);
        _reason = "";
    }

    public SimSolidResult MaterializeFrame(SimFrame frame, bool cumulative)
    {
        var res = new SimSolidResult();
        if (_regions == null || _timeline == null)
        {
            res.Messages.Add("三维层体：端口未绑定几何上下文");
            res.Summary = res.Messages[0];
            return res;
        }
        // 建体前强制复验一次样式源：用户可能刚导入了现状面，不该还要求他先点「刷新数据」
        EnsureSource(force: true);
        if (!Available)
        {
            res.Messages.Add(StatusLabel);
            res.Summary = StatusLabel;
            return res;
        }

        // 物料着色源也每次重探：活动块体模型/煤岩关联可能刚被改过
        if (!MaterialColoring)
        {
            _material = null;
            res.MaterialLabel = "物料分色：**用户关闭**（上方「按块体分色物料」未勾）→ 层体按去向类型着色，也不统计煤/岩构成。";
        }
        else
        {
            try { _material = SimMaterialColorProvider.Create(); }
            catch (Exception ex) { _material = null; res.Messages.Add($"物料分色探测异常（{ex.GetType().Name}），按去向类型着色。"); }
            res.MaterialLabel = _material?.StatusLabel ?? "物料分色不可用（探测异常）→ 层体按去向类型着色。";
        }

        try
        {
            // 非累计：先清上期，避免同一位置叠出两份体
            if (!cumulative) res.Deleted = ClearSolids();

            // 样式源找不到**不是**建不了体：内核 PitMine_BuildColoredMeshOnLayer(xllAcEd.cpp:8860)
            // 对 srcHandle 解不出实体时 basePoint 退回本网第一个顶点、baseColor 退回白色；
            // 而本端口逐顶点给色（一个 0xFFFFFFFF 哨兵都不发），所以 baseColor 用不到。
            // 详细理由见 UnitSolidStage.ResolveSourceOrDegrade —— 那边是同一条结论的正本。
            if (!TryResolveSource(out ulong src, out string why))
            {
                src = 0;
                res.Messages.Add($"· 没有样式源（{why}）→ 按自身第一个顶点定 basePoint 照常建体。");
            }

            SimPlanScene scene;
            try { scene = SimPlanScene.Build(_regions, _timeline, frame.Index, _mode, _azimuth); }
            catch (Exception ex)
            {
                res.Messages.Add($"三维层体：本期轮廓装配失败（{ex.GetType().Name}: {ex.Message}）");
                res.Summary = res.Messages[^1];
                return res;
            }
            foreach (var n in scene.Notes) res.Messages.Add(n);
            foreach (var n in _regions.ElevationNotes) res.Messages.Add(n);

            if (scene.Shapes.Count == 0)
            {
                res.Messages.Add("本期没有任何区域轮廓可建体。");
                res.Summary = "三维层体：本期无可建区域";
                return res;
            }

            foreach (var s in scene.Shapes)
            {
                // 采场侧台阶高兜底走界面/计划的 H（SimMiningParams）；排土侧不兜底 ——
                // 汇台账没录台阶高就跳过，不拿别处的台阶高冒充。
                double fallbackH = s.IsDump ? 0
                    : (_timeline.MiningParams.Usable ? _timeline.MiningParams.BenchHeightM : 0);

                SimSolidLayer layer;
                try { layer = SimSolidBuilder.FromShape(s, frame.Index, fallbackH, _material); }
                catch (Exception ex)
                {
                    res.Skipped++;
                    res.Messages.Add($"{s.Region.Name}：层体三角化异常（{ex.GetType().Name}）");
                    continue;
                }
                res.Layers.Add(layer);

                if (!layer.HasGeometry)
                {
                    res.Skipped++;
                    foreach (var w in layer.Warnings) res.Messages.Add($"{s.Region.Name}：{w}");
                    continue;
                }

                // 本期该层体范围内的**真实**煤/岩体积构成（块体口径，不是按份额估的）。
                // 统计不出来会带着原因回来，照样显示——那是「没数据」，不是「没有煤」。
                if (_material != null)
                {
                    try { layer.Composition = _material.SampleComposition(layer); }
                    catch (Exception ex)
                    {
                        layer.Composition = new SimBlockComposition
                        { Available = false, Reason = $"煤/岩构成：统计异常（{ex.GetType().Name}）→ 本层不给构成数。" };
                    }
                }

                string layerName = s.IsDump ? LayerDump : LayerPit;
                ulong h = 0;
                try
                {
                    // lift=0：层体本身就带绝对高程，不需要再抬。
                    h = _ent.BuildColoredMeshOnLayer(src, layer.WorldXyz, layer.Triangles, layer.VertexRgb, 0.0, layerName);
                }
                catch (Exception ex)
                {
                    res.Messages.Add($"{s.Region.Name}：层体入库异常（{ex.GetType().Name}: {ex.Message}）");
                }

                if (h == 0)
                {
                    res.Skipped++;
                    res.Messages.Add($"{s.Region.Name}：层体入库失败（引擎返回 handle 0；常见原因是样式源已被删除）");
                    continue;
                }

                layer.Handle = h;
                _built.Add(h);
                res.Built++;
            }

            // 体积自检取最严重的一条
            foreach (var l in res.Layers)
            {
                if (l.Handle == 0) continue;
                if (l.VolumeCheck == SimCheckLevel.Error) { res.WorstCheck = SimCheckLevel.Error; break; }
                if (l.VolumeCheck == SimCheckLevel.Warn) res.WorstCheck = SimCheckLevel.Warn;
                else if (l.VolumeCheck == SimCheckLevel.Ok && res.WorstCheck == SimCheckLevel.NotAvailable)
                    res.WorstCheck = SimCheckLevel.Ok;
            }

            try { SimHost.View?.RequestRender(); } catch { }

            var bits = new List<string>
            {
                $"本期建成 {res.Built} 个层体" + (res.Skipped > 0 ? $"（跳过 {res.Skipped}）" : ""),
            };
            if (res.Deleted > 0) bits.Add($"先清旧体 {res.Deleted} 个");
            else if (cumulative) bits.Add("累计模式：上期层体保留");
            bits.Add(res.MeshCheckFailures > 0
                ? $"⚠ 网格自检 {res.MeshCheckFailures} 个不通过"
                : "网格自检通过（棱台解析↔散度积分）");
            if (res.SkippedZeroSegments > 0) bits.Add($"跳过零厚度段 {res.SkippedZeroSegments} 段（免 z-fight，不影响体积）");
            bits.Add(res.MaterialColoredLayers > 0
                ? $"物料分色 {res.MaterialColoredLayers}/{res.Built} 个（块体煤岩）"
                : "物料分色：未启用（按去向类型着色）");
            bits.Add($"体积自检：{res.WorstCheck.Label()}");
            var worst = res.Layers.Where(l => l.Handle != 0 && l.VolumeCheck == res.WorstCheck)
                                  .Select(l => $"{l.RegionName} {l.VolumeCheckText}").FirstOrDefault();
            if (worst != null) bits.Add(worst);
            res.Summary = string.Join("　·　", bits);
            return res;
        }
        catch (Exception ex)
        {
            res.Messages.Add($"三维层体生成失败（{ex.GetType().Name}: {ex.Message}）");
            res.Summary = res.Messages[^1];
            return res;
        }
    }

    public int ClearSolids()
    {
        var hs = new List<ulong>();
        foreach (var lay in new[] { LayerPit, LayerDump })
        {
            try
            {
                var arr = _ent.GetHandlesByLayer(lay);
                if (arr != null) hs.AddRange(arr);
            }
            catch { }
        }
        foreach (var h in _built) if (h != 0 && !hs.Contains(h)) hs.Add(h);
        _built.Clear();
        if (hs.Count == 0) return 0;

        int n = 0;
        try { n = _ent.DeleteEntities(hs.Distinct().ToArray()); } catch { }
        try { SimHost.View?.RequestRender(); } catch { }
        return n;
    }

    /// <summary>
    /// 找一张现有三角网当样式源。缓存 + 便宜的存在性复验（AABB），
    /// 失效才重新扫全表。**排除本端口自己建的层体**，否则清除后 handle 立刻悬空。
    /// </summary>
    private bool TryResolveSource(out ulong src, out string why)
    {
        src = 0;
        why = "";

        if (_srcHandle != 0)
        {
            try { if (_ent.TryGetEntityAABB(_srcHandle, out _, out _)) { src = _srcHandle; return true; } }
            catch { }
            _srcHandle = 0;
        }

        ulong[] all;
        try { all = _ent.GetAllHandles() ?? Array.Empty<ulong>(); }
        catch (Exception ex) { why = $"读取文档实体失败（{ex.GetType().Name}）"; return false; }
        if (all.Length == 0) { why = "文档中没有任何实体，无三角网可作样式源"; return false; }

        var mine = new HashSet<ulong>();
        foreach (var lay in new[] { LayerPit, LayerDump })
        {
            try
            {
                var arr = _ent.GetHandlesByLayer(lay);
                if (arr != null) foreach (var h in arr) mine.Add(h);
            }
            catch { }
        }

        int[] types;
        try { types = _ent.GetEntityTypes(all); }
        catch (Exception ex) { why = $"读取实体类型失败（{ex.GetType().Name}）"; return false; }

        int n = Math.Min(all.Length, types.Length);
        for (int i = 0; i < n; i++)
        {
            if (types[i] != TypeIdTriangleMesh) continue;
            if (mine.Contains(all[i])) continue;
            _srcHandle = all[i];
            src = _srcHandle;
            return true;
        }

        why = "文档中无三角网可作样式源（建体要从一张现有 mesh 取 basePoint / 实体色 / 透明度）"
            + " —— 先导入或生成一张现状面，再点上方「刷新数据」即可启用";
        return false;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  宿主能力注入点
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 推演模块 ↔ 宿主能力 的桥（与 MeshEditLib 的 EstimationHost 同款做法）。
/// <para>
/// **接线点**：请在 <c>TaskLibPlugin.Initialize</c> 里加一行
/// <code>TaskLib.Simulation.SimHost.Inject(context.Capabilities);</code>
/// 窗口本身拿不到 IPluginContext，只能由插件入口注入。
/// </para>
/// <para>
/// 未注入时本类会做一次**尽力探测**（反射读同宿主里已注入的 MeshEditLib EstimationHost），
/// 探不到就干脆返回 null，端口自动退成 <see cref="NullGeometryPort"/>。
/// </para>
/// </summary>
public static class SimHost
{
    private static IPitDesignCapability? _pitDesign;
    private static bool _probed;

    /// <summary>台阶/区域设计能力。</summary>
    public static IPitDesignCapability? PitDesign
    {
        get { if (_pitDesign == null && !_probed) Probe(); return _pitDesign; }
        set { _pitDesign = value; _probed = true; }
    }

    /// <summary>视图能力（重绘/着色；本包只用来在推送 overlay 后请求重绘）。</summary>
    public static IViewCapability? View { get; set; }

    /// <summary>实体能力（<see cref="SolidGeometryPort"/> 建/收真三维层体用）。</summary>
    public static IEntityCapability? Entities { get; set; }

    /// <summary>由 TaskLibPlugin 注入宿主能力。</summary>
    public static void Inject(ICapabilityProvider? caps)
    {
        _probed = true;
        if (caps == null) return;
        try { if (caps.TryGet<IPitDesignCapability>(out var p)) _pitDesign = p; } catch { }
        try { if (caps.TryGet<IViewCapability>(out var v)) View = v; } catch { }
        try { if (caps.TryGet<IEntityCapability>(out var e)) Entities = e; } catch { }
    }

    /// <summary>
    /// 尽力探测：插件入口还没接线时，从同宿主里**已经**注入过能力的静态桥上借一份
    /// （MeshEditLib 的 EstimationHost）。纯只读、全程 try/catch，探不到就当没有。
    /// 这不是长久之计，正式接线仍应走 <see cref="Inject"/>。
    /// </summary>
    private static void Probe()
    {
        _probed = true;
        try
        {
            var t = Type.GetType("PitMine3D.Estimation.Services.EstimationHost, MeshEditLib");
            var v = t?.GetProperty("PitDesign", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            _pitDesign = v as IPitDesignCapability;
        }
        catch { _pitDesign = null; }
    }

    /// <summary>能力是否已就位（界面显示接线状态用）。</summary>
    public static string StatusLabel => PitDesign != null
        ? "宿主三维能力：已就位（IPitDesignCapability）"
        : "宿主三维能力：未注入（在 TaskLibPlugin.Initialize 调 SimHost.Inject(context.Capabilities) 即可启用三维轮廓推送）";
}

/// <summary>端口工厂：能接就接，接不上给 <see cref="NullGeometryPort"/>。</summary>
public static class SimGeometryPortFactory
{
    public static ISimGeometryPort Create()
    {
        try
        {
            var cap = SimHost.PitDesign;
            if (cap == null) return new NullGeometryPort(SimHost.StatusLabel);
            return new PitDesignGeometryPort(cap);
        }
        catch (Exception ex)
        {
            return new NullGeometryPort($"三维端口创建失败（{ex.GetType().Name}），已退平面推进示意");
        }
    }
}

/// <summary>实体端口工厂：接得上 <see cref="IEntityCapability"/> 就能建真三维层体。</summary>
public static class SimSolidPortFactory
{
    public static ISimSolidPort Create()
    {
        try
        {
            var ent = SimHost.Entities;
            if (ent == null)
                return new NullSolidPort("三维层体：未注入实体能力 IEntityCapability（在 TaskLibPlugin.Initialize 调 SimHost.Inject 即可启用）");
            return new SolidGeometryPort(ent);
        }
        catch (Exception ex)
        {
            return new NullSolidPort($"三维层体端口创建失败（{ex.GetType().Name}）");
        }
    }
}
