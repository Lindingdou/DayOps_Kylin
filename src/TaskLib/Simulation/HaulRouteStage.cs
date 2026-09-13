// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/HaulRouteStage.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.Cad.Road;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ─────────────────────────────────────────────────────────────────────────────
//  运输线阶段 —— 把本期每一笔 O-D（源单元质心 → 去向坐标）在**真实路网**上画出来。
//
//  ── ① 怎么画：overlay 逐段两点，**不建 mesh**（先确认再选，理由如下）──
//  内核证据（Kernel/xllAcEd/src/API/xllAcEd.cpp:7791 PitMine_AddMineableAreaRing）：
//      if (!xyz || n < 2) return;
//      if (n >= 3) ov.AddPolygon("MINEABLE_AREA", pts, {r,g,b,0.22f});   // ← 半透明填充面
//      if (closed && n >= 3) pts.push_back(pts.front());                 // ← 闭合描边
//      ov.AddPolyline("MINEABLE_AREA", pts, {r,g,b,1.0f});
//  n==2 时两个 if 都不成立 —— **不填面、不闭合，画出来就是一条纯线段**。
//  （PitDesignCapabilityImpl.cs:1029 把 closed 写死 true，所以 n≥3 一定被闭合，
//    整条运输线当一条 ring 传进去得到的是一个多边形加一张面，不是线。）
//  ⇒ 结论：**开放折线在本通道画得了**，办法是拆成逐段两点（<see cref="SimOverlayComposer.SplitOpenPolyline"/>）。
//    既然画得了，就不建 mesh —— 建 mesh 要走 IEntityCapability.BuildColoredMeshOnLayer，
//    那是**入库实体、进 Undo 栈**的（SimGeometryPort.cs:38-41 已经把这条纪律写死：绝不逐帧建），
//    而运输线是要跟着时间轴逐期变的。
//  ⇒ 分给本阶段的图层 <see cref="ReservedSolidLayer"/>（__SIM_HAUL__）本次**留空不用**。
//    什么时候才该动它：需要「线有宽度」（按载荷做粗细带）或需要出图/导出实体时。
//    那时**不要在本文件里再抄一份样式源解析** —— SolidGeometryPort.TryResolveSource
//    （SimGeometryPort.cs:600）已经有一份，两份迟早漂；正确做法是把它提成共用件。
//
//  ── ② 代价怎么控住：段级去重 + 内容不变不推 ──
//  一条 3 km 的路按 ~19 m 采样 ≈ 160 点 ⇒ 159 段 ⇒ 159 次 P/Invoke。
//  单月 ~85 笔 O-D 全量画就是上万次，直接顶穿 30~40 ms 的帧预算。
//  两条对策：
//   · **段级去重**：所有路径都长在同一张图的同一批中线上，共用的干线段坐标**逐位相同**，
//     用坐标做键就能合并 —— 这正是「只画本期用到的边」。合并时把吨量累加，于是
//     去重不但省，还顺手得到了每段的载荷（下面的分档就用它）。
//   · **不逐帧重算**：只有「期次键 + O-D 内容指纹」变了才重建并 Set；没变连 Set 都不调，
//     合成器走 _dirty=false 的快路，一次 P/Invoke 都没有。
//
//  ── ③ 通道纪律 ──
//  绝不直接调 ShowMineableAreaOverlay（那是整通道替换，谁后调谁擦掉别人的图）。
//  本阶段只往合成器的 <see cref="SimOverlayLayers.Haul"/>（重载）与
//  <see cref="EmptyRunKey"/>（空驶）两个键上 attach，合成器每帧统一推一次。
//
//  ── ④ 一条假线都不许画 ──
//  load_unload_point 是空表 ⇒ CoalSinkAdapter 会退回「引擎默认位置·采场质心」
//  （PlanLib/ShortTerm/CoalSinkAdapter.cs:271-282，Code="CR-DEFAULT"）。那个点在煤单元的
//  正中间，没有工程含义。往它画一条线，图上看起来和真运输线**一模一样**。
//  ⇒ 去向可信度由**知道自己降级的那一方**声明（<see cref="SimHaulOd.DestTrust"/>），
//    本阶段只画 <see cref="SimHaulTrust.Real"/>；<see cref="SimHaulTrust.Approximate"/> 与
//    **没声明**（<see cref="SimHaulTrust.Unknown"/>）一律不画、逐笔计数、界面上写清楚。
//    默认值刻意是 Unknown —— 调用方忘了填，结果是「一条都没画 + 一条很响的说明」，
//    而不是「悄悄画了一堆假线」。
//
//  ── ⑤ 命中率必须显示 ──
//  「接了路网」和「接了但一笔没中」画出来的图**一模一样**（都是空的）。所以
//  <see cref="SimHaulRouteResult.Summary"/> 在一条都没画出来时会以 ◆ 开头把原因逐类摆出来。
//  计数**本阶段自己数**，不调 SimRoadGraph.ResetStats() —— 那是进程级计数器，
//  别的阶段（设备符号等）也在用同一个单例问路，清它等于把别人的账抹了。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 去向坐标的可信度 —— <b>由声明方给，本阶段不猜</b>。
/// <para>默认 <see cref="Unknown"/>：没人声明就按不可信处理（fail-closed）。</para>
/// </summary>
public enum SimHaulTrust
{
    /// <summary>没声明。按不可信处理：不画、计数、留条。</summary>
    Unknown = 0,
    /// <summary>真实点位（装卸点台账里录了坐标 / 排土位置来自排土条带的真实质心）。画。</summary>
    Real = 1,
    /// <summary>
    /// 降级位置（例：出矿点退回「采场质心」这个引擎默认位置）。<b>不画</b> ——
    /// 它画出来和真线长得一样，会把人骗过去。
    /// </summary>
    Approximate = 2,
}

/// <summary>
/// 一笔 O-D = 一个采掘单元的一条去向流。
///
/// <para><b>为什么不直接吃 <c>MineAssLib.Driving.UnitAssignment</c></b>：TaskLib 编译期看不到
/// MineAssLib（TaskLib.csproj 只引 Platform / GeoDataBase / RoadLib / BlockModelLib）。
/// 本阶段因此定一个**窄输入契约**，映射由接线方写（20 行），一处一份，不在这里反射猜签名。
/// 映射逐字如下（字段名均已对着源码核过，非猜测）：</para>
/// <code>
///  UnitAssignment a（MineAssLib/Driving/UnitPlanEngine.cs:139）
///  UnitFlow       f = a.Flows[i]（同文件:178）
///  MineUnit       u = 按 a.UnitId 关联到的单元（MineAssLib/Driving/MineUnit.cs:29）
///                     —— ★ UnitAssignment 上**没有**质心，源点只能从 MineUnit / 台账 Row 取
///
///  UnitId          = a.UnitId
///  IsCoal          = a.Kind == UnitKind.Coal
///  Sx,Sy,Sz        = u.Cx, u.Cy, u.Cz
///  DestinationCode = f.DestinationCode      DestinationName = f.DestinationName
///  Dx,Dy,Dz        = f.Dx, f.Dy, f.Dz
///  InSituM3        = f.InSituM3             TonnageT = f.TonnageT
///  HaulKm          = f.HaulKm               HaulFromNetwork = f.HaulFromNetwork
///  MaterialCode    = f.MaterialCode         MaterialName = f.MaterialName
///  IsInternalDump  = f.IsInternalDump       IsCoalSink = f.IsCoalSink
///  DestTrust       = f.IsCoalSink
///                      ? (coalSinkResolution.UsesRealPoints ? Real : Approximate)   // PlanLib/ShortTerm/CoalSinkAdapter.cs:72
///                      : Real                                                        // 排土位置来自排土条带的真实质心
/// </code>
/// </summary>
public sealed class SimHaulOd
{
    /// <summary>源采掘单元号（<c>层-B带号-P幅号</c>）。只用于说明文案与高亮定位。</summary>
    public string UnitId = "";
    /// <summary>煤流（true）还是岩/表土流（false）。决定物料色。</summary>
    public bool IsCoal;

    /// <summary>源点 = 采掘单元质心（世界坐标）。XY 全 0 视为「没有坐标」，不画。</summary>
    public double Sx, Sy, Sz;

    public string DestinationCode = "";
    public string DestinationName = "";
    /// <summary>汇点（世界坐标）。</summary>
    public double Dx, Dy, Dz;

    /// <summary>★ 汇点坐标可信度。<b>默认 Unknown = 不画</b>，见 <see cref="SimHaulTrust"/>。</summary>
    public SimHaulTrust DestTrust = SimHaulTrust.Unknown;
    /// <summary>降级说明（<see cref="DestTrust"/> 非 Real 时界面会显示它）。</summary>
    public string TrustNote = "";

    public double InSituM3;
    /// <summary>吨量 t —— 段载荷分档就是按它累加的。</summary>
    public double TonnageT;
    /// <summary>台账里这笔的运距 km（本阶段**不用它画线**，只在说明里与路网里程对照）。</summary>
    public double HaulKm;
    /// <summary>台账里这笔运距是不是路网解出来的（排产那一侧的结论，与本阶段自己的命中分开记）。</summary>
    public bool HaulFromNetwork;

    public string MaterialCode = "";
    public string MaterialName = "";
    public bool IsInternalDump;
    public bool IsCoalSink;

    public string Caption =>
        $"{UnitId} → {(DestinationName.Length > 0 ? DestinationName : DestinationCode)}"
      + $"（{(IsCoal ? "煤" : MaterialName.Length > 0 ? MaterialName : "岩")} {TonnageT / 1e4:0.##}万t）";
}

/// <summary>
/// 去重后的一段运输线（= 路网中线上的一小段，本期真的有车走）。
/// <para>★ <b>是「段」不是「边」</b>：一条 RoadEdge 的中线通常有十几个采样点、即十几段。
/// 本阶段拿到的是折线坐标（<see cref="SimRoute.Points3d"/>），拿不到 EdgeId，所以按坐标去重、
/// 报的是段数。段数 ≠ 边数，界面上不许把它当边数说。</para>
/// </summary>
public sealed class SimHaulSegment
{
    public double X0, Y0, Z0, X1, Y1, Z1;

    /// <summary>本段累计煤吨 / 岩（含表土等一切非煤）吨。</summary>
    public double CoalT, RockT;
    public double TonnageT => CoalT + RockT;
    /// <summary>有多少笔 O-D 走过本段。</summary>
    public int OdCount;

    /// <summary>煤岩混走时按吨量大的那一边上色（<see cref="Mixed"/> 会计数并留条）。</summary>
    public bool IsCoal => CoalT >= RockT;
    public bool Mixed => CoalT > 1e-9 && RockT > 1e-9;

    /// <summary>空驶专走段（重载不经过的那部分回程）。</summary>
    public bool EmptyRun;

    /// <summary>载荷档：0 干线（≥p85）· 1 支线（≥p50）· 2 末梢。空驶恒 −1（空车没有载荷）。</summary>
    public int LoadLevel = -1;

    /// <summary>最终上色 0xRRGGBB。</summary>
    public uint Rgb;

    public double LengthM
    {
        get { double dx = X1 - X0, dy = Y1 - Y0, dz = Z1 - Z0; return Math.Sqrt(dx * dx + dy * dy + dz * dz); }
    }
}

/// <summary>
/// 一笔 O-D 真正解出来的那条路径（重载方向：源 → 汇）。
///
/// <para><b>为什么要单独留一份、而不是让车流舞台自己再问一次路</b>：再问一次会得到
/// <b>另一条</b>路 —— 权重口径、吸附半径、甚至等距处的取舍任何一处不同，两条路就分岔了，
/// 于是「画出来的线」和「车走的线」不重合。那种不一致在图上表现为车飘在路外，
/// 而两边各自都自洽、都查不出错。所以车流一律吃这里留下的这份，**问路只发生一次**。</para>
///
/// <para>与 <see cref="SimHaulSegment"/> 的分工：段是**去重后**的路网几何（画线用，一段路只画一次）；
/// 路径是**逐笔**的完整折线（布点用，两笔共线也各留各的）。两者不可互相替代。</para>
/// </summary>
public sealed class SimHaulPath
{
    public string UnitId = "";
    public string DestinationCode = "";
    public string DestinationName = "";
    public bool IsCoal;
    public string MaterialCode = "";
    public string MaterialName = "";
    public bool IsInternalDump;

    /// <summary>本笔吨量 t（车流强度按它算）。</summary>
    public double TonnageT;

    /// <summary>扁平折线 [x,y,z,...]，重载方向（源 → 汇）。至少 2 点。</summary>
    public double[] Xyz = Array.Empty<double>();

    /// <summary>路网三维里程 m（<see cref="SimRoute.LengthM"/>，不是折线平面长）。</summary>
    public double LengthM;

    public int PointCount => Xyz.Length / 3;

    /// <summary>
    /// 净纵坡 %（重载方向，首末高差 ÷ <b>水平距离</b>）。上坡为正。
    /// <para>★ 分母是水平距离不是三维里程 —— 工程上纵坡 i = Δh / 水平距离，
    /// <see cref="RoadLib.Routing.HaulMetrics"/> 的坡阻模型吃的也是这个口径。
    /// 拿三维里程做分母会系统性偏小（12% 的坡偏 0.7 个百分点），而两种算法都"看着对"。</para>
    /// <para>这里用的是**首末两点**的水平距离，不是折线水平长 —— 净坡说的就是两端高差摊在直线距离上。</para>
    /// </summary>
    public double NetGradePct
    {
        get
        {
            int n = PointCount;
            if (n < 2) return 0;
            double dx = Xyz[3 * (n - 1)] - Xyz[0];
            double dy = Xyz[3 * (n - 1) + 1] - Xyz[1];
            double hor = Math.Sqrt(dx * dx + dy * dy);
            if (hor < 1e-6) return 0;
            return (Xyz[3 * (n - 1) + 2] - Xyz[2]) / hor * 100.0;
        }
    }
}

/// <summary>一次重建的全部结论（界面直接照它写状态栏；判据也打在它上面）。</summary>
public sealed class SimHaulRouteResult
{
    /// <summary>期次键（<c>SimFrame.Period</c> 或调用方自定）。</summary>
    public string PeriodKey { get; internal set; } = "";

    // ── O-D 分类账（★ 每一笔都必须落进且只落进一个桶）──
    /// <summary>本期 O-D 总笔数（= 传进来的条数）。</summary>
    public int OdTotal { get; internal set; }
    /// <summary>空条目（null）。</summary>
    public int OdInvalid { get; internal set; }
    /// <summary>去向是降级位置（<see cref="SimHaulTrust.Approximate"/>）→ 不画。</summary>
    public int OdSkippedApprox { get; internal set; }
    /// <summary>去向可信度**没声明** → 按不可信处理，不画。</summary>
    public int OdSkippedUnknownTrust { get; internal set; }
    /// <summary>源单元没有坐标（XY 全 0）→ 不画。</summary>
    public int OdSkippedNoSourcePos { get; internal set; }
    /// <summary>真的向路网问了路的笔数。</summary>
    public int OdRouted { get; internal set; }
    /// <summary>问路命中（走通了真实路网）的笔数。</summary>
    public int OdHit { get; internal set; }
    /// <summary>命中但折线拆不出任何一段（端点重合等）→ 没画出线，单列，不混进命中。</summary>
    public int OdHitNoSegment { get; internal set; }

    /// <summary>逐类未命中计数，下标 = <see cref="SimRouteMiss"/>。</summary>
    internal readonly int[] MissBy = new int[Enum.GetValues<SimRouteMiss>().Length];
    public int Miss(SimRouteMiss m) => MissBy[(int)m];
    public int MissTotal => MissBy.Sum() - MissBy[(int)SimRouteMiss.None];

    /// <summary>跳过（压根没问路）的合计。</summary>
    public int OdSkippedTotal => OdInvalid + OdSkippedApprox + OdSkippedUnknownTrust + OdSkippedNoSourcePos;

    /// <summary>
    /// **分类账自洽**（判据用，能证伪）：
    /// ① 每笔 O-D 落进且只落进一个桶；② 问路笔数 = 命中 + 未命中。
    /// 任何一条新分支忘了计数，这里立刻 false。
    /// </summary>
    public bool Balanced =>
        OdTotal == OdSkippedTotal + OdHit + OdHitNoSegment + MissTotal
     && OdRouted == OdHit + OdHitNoSegment + MissTotal;

    /// <summary>路网命中率（按问路笔数；跳过的不进分母 —— 那些压根没问）。</summary>
    public double HitRate => OdRouted > 0 ? (double)(OdHit + OdHitNoSegment) / OdRouted : 0;

    // ── 几何账 ──
    /// <summary>去重前的段数（各条路径逐段展开的总数）。</summary>
    public int RawSegments { get; internal set; }
    /// <summary>去重后的段数（= 本期真的用到的那部分路网几何）。</summary>
    public int DistinctSegments { get; internal set; }
    /// <summary>真的画出去的段数（= 推给合成器的环数）。</summary>
    public int DrawnSegments { get; internal set; }
    /// <summary>因超出 <see cref="HaulRouteStage.MaxRings"/> 上限被舍掉的段数（**绝不静默**）。</summary>
    public int DroppedByCap { get; internal set; }
    /// <summary>其中空驶专走段数。</summary>
    public int EmptyRunSegments { get; internal set; }
    /// <summary>煤岩混走的段数（按吨量大的一边上色）。</summary>
    public int MixedSegments { get; internal set; }

    /// <summary>去重后的全部段（重载 + 空驶）。想建 mesh / 做统计的拿这个，不用重算。</summary>
    public List<SimHaulSegment> Segments { get; } = new();

    /// <summary>
    /// 本期**逐笔**解出来的完整路径（车流示意沿它布点）。见 <see cref="SimHaulPath"/> 头上那段
    /// ——「问路只发生一次」是硬纪律，车流不许自己再解一次。
    /// <para>与 <see cref="OdHit"/> 等长：命中一笔留一条；未命中/跳过的一条不留。</para>
    /// </summary>
    public List<SimHaulPath> Paths { get; } = new();

    // ── 口径与降级说明（一条都不吞）──
    public List<string> Notes { get; } = new();

    /// <summary>路网来源文案（<c>SimRoadGraph.Label</c>）。</summary>
    public string GraphLabel { get; internal set; } = "";
    /// <summary>吸附半径 + 这个数打哪来（缺省值还是现场标定的）。</summary>
    public string SnapRadiusText { get; internal set; } = "";
    /// <summary>全网边数（拿来和「本期用到 N 段」对照；★ 段≠边）。</summary>
    public int GraphEdgeCount { get; internal set; }

    /// <summary>命中率一行（界面必须显示它）。</summary>
    public string HitText { get; internal set; } = "";
    /// <summary>状态栏一行。一条线都没画出来时以 ◆ 开头并给出逐类原因。</summary>
    public string Summary { get; internal set; } = "";
    /// <summary>图例（颜色规则的口径说明）。</summary>
    public string LegendText { get; internal set; } = "";

    /// <summary>把命中率 + 口径 + 全部留条拼成界面能直接贴的一段。</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary);
        sb.AppendLine(HitText);
        sb.AppendLine(LegendText);
        if (GraphLabel.Length > 0) sb.AppendLine("路网：" + GraphLabel);
        if (SnapRadiusText.Length > 0) sb.AppendLine("吸附半径：" + SnapRadiusText);
        // 已经自带记号的（◆ 降级 / · 说明）原样输出，别叠成「· ·」
        foreach (var n in Notes)
            sb.AppendLine(n.StartsWith("◆", StringComparison.Ordinal) || n.StartsWith("·", StringComparison.Ordinal)
                          ? n : "· " + n);
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// 运输线阶段。把本期 O-D 在真实路网上求路 → 段级去重 → 按物料/载荷上色 →
/// attach 到 overlay 帧合成器。<b>永不抛</b>：路网不可用/解不出都只是「没画 + 说清楚」。
/// </summary>
public sealed class HaulRouteStage
{
    // ── 键与图层 ────────────────────────────────────────────────────────────

    /// <summary>重载线的图层键（合成器口径，叠放序 200）。</summary>
    public const string LoadedKey = SimOverlayLayers.Haul;

    /// <summary>
    /// 空驶线的图层键。压在重载之上（order 210），<b>只画重载不经过的那些段</b> ——
    /// 两者在图上永不重叠，所以看图时不会有「这条灰线到底是空驶还是末梢」的歧义。
    /// </summary>
    public const string EmptyRunKey = "haul.empty";

    private const int LoadedOrder = 200;
    private const int EmptyRunOrder = 210;

    /// <summary>
    /// 分给本阶段的实体图层。<b>本次留空不用</b>（overlay 画得了线，见文件头 ①）。
    /// 登记在这里是为了别人别拿它放东西，也为了将来要建带宽度的 mesh 时有个既定去处。
    /// </summary>
    public const string ReservedSolidLayer = "__SIM_HAUL__";

    // ── 可调项（每一个都是「替用户做的决定」，都摆到界面上）────────────────

    /// <summary>
    /// 线的抬升量 m —— <b>纯显示偏移</b>，避免与地表/层体 z-fight。
    /// 不改任何里程数（里程报的是 <see cref="SimRoute.LengthM"/>，与抬升无关）。
    /// </summary>
    public double LiftM { get; set; } = 2.0;

    /// <summary>
    /// 一帧最多推多少条线段。超了按载荷从大到小截取，<b>并把舍掉的段数报出来</b>。
    /// 3000 条 ≈ 3000 次 P/Invoke，只在期次切换那一帧发生（内容不变的帧一次都不推）。
    /// </summary>
    public int MaxRings { get; set; } = 3000;

    /// <summary>
    /// 画空驶（回程）。默认 <b>关</b>：绝大多数回程与重载同路，画上去只是把图压暗一层，
    /// 而问路笔数要翻倍。打开时按 <c>loaded=false</c> 反向问一次路，只画差异段。
    /// </summary>
    public bool ShowEmptyRun { get; set; }

    /// <summary>
    /// 高亮某个采掘单元：填了单元号后，只有该单元的段用本色，其余段压成灰。
    /// 空 = 不高亮（全部按载荷档上色）。
    /// </summary>
    public string FocusUnitId { get; set; } = "";

    /// <summary>吸附半径 m；NaN = 用 <see cref="SimRoadGraph.SnapRadiusM"/>。</summary>
    public double SnapRadiusM { get; set; } = double.NaN;

    /// <summary>寻径权重口径（与排产侧取运距时用的口径保持一致，别两处不同）。</summary>
    public WeightMode Mode { get; set; } = WeightMode.Distance;

    // ── 状态 ────────────────────────────────────────────────────────────────

    private readonly SimOverlayComposer? _composer;   // null = 用全局 Current（生产路径）
    private SimOverlayComposer Composer => _composer ?? SimOverlayComposer.Current;

    private bool _hasSig;
    private ulong _sig;
    private SimHaulRouteResult? _last;
    private bool _attached;   // 往通道 attach 过键（Reset 时才需要撤）

    /// <param name="composer">
    /// 落地的合成器。生产路径传 null（用全局唯一那个）；判据/测试传自己的一份，
    /// <b>免得把全局合成器的落地端换掉</b>。
    /// </param>
    /// <summary>
    /// 面板落地端。<b>非 null 时整条线改推它，完全不碰 overlay 合成器</b> ——
    /// 「画到主视图」和「画到窗体面板」是同一份几何的两个去处，绝不同时推两边
    /// （同时推的话，主视图那份没人清，关窗后会一直挂在图纸上）。
    /// </summary>
    private readonly ISimDynamicOverlay? _dyn;

    public HaulRouteStage(SimOverlayComposer? composer = null, ISimDynamicOverlay? dynamicSink = null)
    { _composer = composer; _dyn = dynamicSink; }

    /// <summary>上一次重建的结论（还没跑过 = null）。</summary>
    public SimHaulRouteResult? Last => _last;

    /// <summary>
    /// 本期 O-D → 运输线。<b>同期次同内容会直接返回上次结果、连 Set 都不调</b>
    /// （合成器因此走 _dirty=false 的快路，零 P/Invoke）。
    /// </summary>
    /// <param name="periodKey">期次键（<c>SimFrame.Period</c>）。</param>
    /// <param name="ods">本期全部 O-D（<c>UnitAssignment.Flows</c> 逐笔展开）。</param>
    /// <param name="force">无视指纹强制重建（改了 <see cref="ShowEmptyRun"/>/<see cref="FocusUnitId"/> 等显示项时用）。</param>
    public SimHaulRouteResult Apply(string periodKey, IReadOnlyList<SimHaulOd>? ods, bool force = false)
    {
        ulong sig = Signature(periodKey, ods);
        if (!force && _hasSig && sig == _sig && _last != null) return _last;

        // 先清空待推产物：重建万一半路抛了，绝不能把上一期的线当本期的推上去。
        _pendingLoaded = new List<SimHaulSegment>();
        _pendingEmpty = new List<SimHaulSegment>();

        SimHaulRouteResult res;
        try { res = Rebuild(periodKey ?? "", ods); }
        catch (Exception ex)
        {
            // 路网层承诺永不抛；这里再兜一层，动画绝不因为画线崩掉。
            res = new SimHaulRouteResult { PeriodKey = periodKey ?? "" };
            res.Notes.Add($"运输线重建异常（{ex.GetType().Name}: {ex.Message}）→ 本期不画线。");
            res.Summary = "◆ 运输线：重建异常，本期一条线都没画。";
            res.HitText = "路网命中率：本次未统计（重建异常）。";
            res.LegendText = "";
        }

        _sig = sig; _hasSig = true; _last = res;
        Push(res);
        return res;
    }

    /// <summary>按帧调的便捷重载（期次键取 <see cref="SimFrame.Period"/>，空则退回 Index）。</summary>
    public SimHaulRouteResult ApplyFrame(SimFrame? frame, IReadOnlyList<SimHaulOd>? ods, bool force = false)
        => Apply(frame == null ? "" : frame.Period.Length > 0 ? frame.Period : "#" + frame.Index, ods, force);

    /// <summary>撤掉本阶段的两个键并让合成器重推（只撤自己那份，别人的原样留着）。幂等。</summary>
    public void Reset()
    {
        _hasSig = false; _sig = 0; _last = null;
        if (!_attached) return;
        _attached = false;
        try
        {
            if (_dyn != null)
            {
                _dyn.SetLines(LoadedKey, null, null, 0);
                _dyn.SetLines(EmptyRunKey, null, null, 0);
                return;
            }
            var c = Composer;
            c.Remove(LoadedKey);
            c.Remove(EmptyRunKey);
            c.Flush();
        }
        catch { }
    }

    // ── 重建 ────────────────────────────────────────────────────────────────

    private SimHaulRouteResult Rebuild(string periodKey, IReadOnlyList<SimHaulOd>? ods)
    {
        var res = new SimHaulRouteResult { PeriodKey = periodKey };
        res.OdTotal = ods?.Count ?? 0;

        // 路网口径先取到（不可用也要显示，别让人以为是「没有 O-D」）
        res.GraphLabel = SimRoadGraph.Label;
        res.GraphEdgeCount = SimRoadGraph.EdgeCount;
        res.SnapRadiusText = $"{SimRoadGraph.SnapRadiusM:0} m —— {SimRoadGraph.SnapRadiusSource}";

        // segKey → 段。重载与空驶各一张表：空驶只保留重载没走过的段。
        var loadedSegs = new Dictionary<SegKey, SimHaulSegment>();
        var emptySegs = new Dictionary<SegKey, SimHaulSegment>();
        // 高亮用：这些段有被 FocusUnitId 走过
        var focus = new HashSet<SegKey>();

        var missSamples = new Dictionary<SimRouteMiss, string>();
        var approxDest = new SortedSet<string>(StringComparer.Ordinal);
        var unknownDest = new SortedSet<string>(StringComparer.Ordinal);
        var noPosUnits = new SortedSet<string>(StringComparer.Ordinal);
        var nonCoalMaterials = new SortedSet<string>(StringComparer.Ordinal);
        bool focusOn = !string.IsNullOrWhiteSpace(FocusUnitId);

        for (int i = 0; i < (ods?.Count ?? 0); i++)
        {
            var od = ods![i];
            if (od == null) { res.OdInvalid++; continue; }

            // ① 去向可信度 —— 在问路**之前**判。不可信就压根不去问：
            //    拿一个编出来的坐标去吸附，会污染 SimRoadGraph 的进程级命中率计数。
            if (od.DestTrust == SimHaulTrust.Approximate)
            {
                res.OdSkippedApprox++;
                approxDest.Add(Label(od));
                continue;
            }
            if (od.DestTrust != SimHaulTrust.Real)
            {
                res.OdSkippedUnknownTrust++;
                unknownDest.Add(Label(od));
                continue;
            }

            // ② 源点必须有坐标。XY 全 0 是「没录」，不是「在原点」。
            if (!HasPosition(od.Sx, od.Sy))
            {
                res.OdSkippedNoSourcePos++;
                noPosUnits.Add(od.UnitId.Length > 0 ? od.UnitId : "(无单元号)");
                continue;
            }
            // 汇点同理 —— 声明成 Real 但坐标是 0，仍然不画（声明管不了坐标）。
            if (!HasPosition(od.Dx, od.Dy))
            {
                res.OdSkippedNoSourcePos++;
                noPosUnits.Add(Label(od) + "（汇点无坐标）");
                continue;
            }

            if (!od.IsCoal && od.MaterialCode.Length > 0) nonCoalMaterials.Add(od.MaterialCode);

            // ③ 问路（重载方向）
            res.OdRouted++;
            var route = SimRoadGraph.TryGetRoute(
                new Point3d(od.Sx, od.Sy, od.Sz), new Point3d(od.Dx, od.Dy, od.Dz),
                SnapRadiusM, loaded: true, mode: Mode);

            if (!route.Hit)
            {
                res.MissBy[(int)route.Miss]++;
                if (!missSamples.ContainsKey(route.Miss)) missSamples[route.Miss] = $"{Label(od)}：{route.MissText}";
                continue;   // ★ 解不出就是解不出 —— 不画直线兜底，一条假线都不许有
            }

            var segs = Split(route.Points3d);
            if (segs.Count == 0)
            {
                res.OdHitNoSegment++;
                continue;   // 命中但拆不出段：单列，不混进「画出来了」
            }

            res.OdHit++;
            res.RawSegments += segs.Count;

            // 留一份逐笔路径给车流（问路只发生一次，见 SimHaulPath 的说明）。
            // 坐标取**原始折线**、不带 LiftM —— 抬升是画线时的避 z-fight 显示偏移，
            // 车流有自己的抬升量，两处各抬各的，不在这里预先掺进去。
            {
                var flat = new double[route.Points3d.Count * 3];
                for (int p = 0; p < route.Points3d.Count; p++)
                {
                    flat[3 * p] = route.Points3d[p].X;
                    flat[3 * p + 1] = route.Points3d[p].Y;
                    flat[3 * p + 2] = route.Points3d[p].Z;
                }
                res.Paths.Add(new SimHaulPath
                {
                    UnitId = od.UnitId,
                    DestinationCode = od.DestinationCode,
                    DestinationName = od.DestinationName,
                    IsCoal = od.IsCoal,
                    MaterialCode = od.MaterialCode,
                    MaterialName = od.MaterialName,
                    IsInternalDump = od.IsInternalDump,
                    TonnageT = Math.Max(0, od.TonnageT),
                    Xyz = flat,
                    LengthM = route.LengthM,
                });
            }

            bool isFocus = focusOn && string.Equals(od.UnitId, FocusUnitId, StringComparison.Ordinal);
            foreach (var s in segs)
            {
                var key = KeyOf(s);
                if (!loadedSegs.TryGetValue(key, out var seg))
                    loadedSegs[key] = seg = new SimHaulSegment { X0 = s[0], Y0 = s[1], Z0 = s[2], X1 = s[3], Y1 = s[4], Z1 = s[5] };
                if (od.IsCoal) seg.CoalT += Math.Max(0, od.TonnageT); else seg.RockT += Math.Max(0, od.TonnageT);
                seg.OdCount++;
                if (isFocus) focus.Add(key);
            }

            // ④ 空驶（可选）：反向 + loaded=false 再问一次，只留重载没走过的段。
            if (!ShowEmptyRun) continue;
            var back = SimRoadGraph.TryGetRoute(
                new Point3d(od.Dx, od.Dy, od.Dz), new Point3d(od.Sx, od.Sy, od.Sz),
                SnapRadiusM, loaded: false, mode: Mode);
            if (!back.Hit) continue;                     // 空驶解不出不算未命中：这一笔的重载线已经画了
            foreach (var s in Split(back.Points3d))
            {
                var key = KeyOf(s);
                if (loadedSegs.ContainsKey(key)) continue;   // 与重载重合的部分不重复画
                if (!emptySegs.TryGetValue(key, out var seg))
                    emptySegs[key] = seg = new SimHaulSegment
                    { X0 = s[0], Y0 = s[1], Z0 = s[2], X1 = s[3], Y1 = s[4], Z1 = s[5], EmptyRun = true };
                seg.OdCount++;
            }
        }

        res.DistinctSegments = loadedSegs.Count + emptySegs.Count;
        res.EmptyRunSegments = emptySegs.Count;
        res.MixedSegments = loadedSegs.Values.Count(s => s.Mixed);

        // ── 载荷分档：段吨量的 p50 / p85（最近秩，不插值）──
        var tons = loadedSegs.Values.Select(s => s.TonnageT).OrderBy(v => v).ToArray();
        double p50 = Quantile(tons, 0.50), p85 = Quantile(tons, 0.85);
        foreach (var s in loadedSegs.Values)
            s.LoadLevel = tons.Length == 0 ? 2 : s.TonnageT >= p85 ? 0 : s.TonnageT >= p50 ? 1 : 2;

        // ── 上色 ──
        foreach (var kv in loadedSegs)
        {
            var s = kv.Value;
            uint baseRgb = s.IsCoal ? SimMaterialColorProvider.RgbCoal : SimMaterialColorProvider.RgbRock;
            double gray = LevelGray(s.LoadLevel);
            if (focusOn && !focus.Contains(kv.Key)) gray = FocusDimGray;   // 高亮：非焦点段压灰
            s.Rgb = TowardGray(baseRgb, gray);
        }
        foreach (var s in emptySegs.Values) s.Rgb = TowardGray(SimMaterialColorProvider.RgbRock, EmptyRunGray);

        // ── 推给合成器：重载与空驶各一个键，各自按载荷从大到小截断 ──
        var loadedOrdered = loadedSegs.Values.OrderByDescending(s => s.TonnageT).ThenByDescending(s => s.OdCount).ToList();
        var emptyOrdered = emptySegs.Values.OrderByDescending(s => s.OdCount).ToList();

        int budget = Math.Max(0, MaxRings);
        var keepLoaded = loadedOrdered.Take(Math.Min(budget, loadedOrdered.Count)).ToList();
        int leftBudget = Math.Max(0, budget - keepLoaded.Count);
        var keepEmpty = emptyOrdered.Take(Math.Min(leftBudget, emptyOrdered.Count)).ToList();
        res.DroppedByCap = (loadedOrdered.Count - keepLoaded.Count) + (emptyOrdered.Count - keepEmpty.Count);

        res.Segments.AddRange(keepLoaded);
        res.Segments.AddRange(keepEmpty);
        res.DrawnSegments = keepLoaded.Count + keepEmpty.Count;

        _pendingLoaded = keepLoaded;
        _pendingEmpty = keepEmpty;

        // ── 留条：一条都不吞 ──
        if (res.OdSkippedApprox > 0)
            res.Notes.Add($"◆ 有 {res.OdSkippedApprox} 笔的**去向坐标是降级位置**（调用方声明 Approximate），"
                        + "已整笔不画 —— 画出来和真运输线长得一模一样，会把人骗过去。"
                        + $"涉及去向：{Join(approxDest)}。"
                        + "（现状：load_unload_point 是空表，煤的去向退回了「引擎默认位置·采场质心」，"
                        + "见 PlanLib/ShortTerm/CoalSinkAdapter.cs。要画煤的真运输线，先在「出矿点录入」里录破碎站/原煤仓/储煤场并拾取坐标。）");
        if (res.OdSkippedUnknownTrust > 0)
            res.Notes.Add($"◆ 有 {res.OdSkippedUnknownTrust} 笔**没有声明去向可信度**（SimHaulOd.DestTrust 还是缺省的 Unknown），"
                        + "按不可信处理，未画。这不是路网问题，是接线没填 —— 接线方必须显式给 Real / Approximate。"
                        + $"涉及去向：{Join(unknownDest)}。");
        if (res.OdSkippedNoSourcePos > 0)
            res.Notes.Add($"◆ 有 {res.OdSkippedNoSourcePos} 笔的源/汇点没有坐标（XY 全 0 = 没录，不是在原点），未画：{Join(noPosUnits)}。");
        foreach (var kv in missSamples)
            res.Notes.Add($"· 未命中「{MissLabel(kv.Key)}」×{res.MissBy[(int)kv.Key]}，例：{kv.Value}");
        if (res.OdHitNoSegment > 0)
            res.Notes.Add($"· 有 {res.OdHitNoSegment} 笔解出了路径但折线拆不出任何一段（端点重合/零长），没画出线，单列不算进命中。");
        if (res.DroppedByCap > 0)
            res.Notes.Add($"◆ 段数超过一帧上限 {MaxRings}，按载荷从大到小只画了 {res.DrawnSegments} 段，"
                        + $"**舍掉 {res.DroppedByCap} 段**（不是没有，是没画）。要全画请调大 MaxRings，代价是每次期次切换多 {res.DroppedByCap} 次 P/Invoke。");
        if (res.MixedSegments > 0)
            res.Notes.Add($"· 有 {res.MixedSegments} 段是煤岩混走（同一段路两种料都过），按吨量大的那一边上色 —— 一段路只有一个颜色，这是显示口径不是统计口径。");
        if (nonCoalMaterials.Count > 1 || (nonCoalMaterials.Count == 1 && !nonCoalMaterials.Contains("rock")))
            res.Notes.Add($"· 非煤物料 {nonCoalMaterials.Count} 种（{Join(nonCoalMaterials)}）**共用岩色**：物料着色源 "
                        + "SimMaterialColorProvider 只给了煤/岩两个色常量，本阶段复用它，不另起一套配色。"
                        + "要把表土单独分色，得先在着色源那边加，而不是在这里编一个。");
        if (ShowEmptyRun)
            res.Notes.Add($"· 空驶已开：按 loaded=false 反向各问一次路（问路笔数翻倍），"
                        + $"只画重载不经过的 {res.EmptyRunSegments} 段；与重载重合的回程不重复画。");
        if (res.GraphEdgeCount > 0)
            res.Notes.Add($"· 本期用到 {res.DistinctSegments} **段**（去重前 {res.RawSegments} 段）；全网 {res.GraphEdgeCount} 条边。"
                        + "★ 段是边中线的分段，**段数不等于边数**，别把这个数当边数报。");

        // 合成器丢弃/降级记账（只取本阶段两个键的）
        try
        {
            foreach (var n in Composer.Notes)
                if (n.StartsWith($"overlay[{LoadedKey}]", StringComparison.Ordinal)
                 || n.StartsWith($"overlay[{EmptyRunKey}]", StringComparison.Ordinal))
                    res.Notes.Add("· " + n);
        }
        catch { }

        BuildText(res);
        return res;
    }

    // 重建产物暂存（Rebuild 纯算、Push 只负责推通道，判据可以只跑 Rebuild）
    private List<SimHaulSegment> _pendingLoaded = new();
    private List<SimHaulSegment> _pendingEmpty = new();

    private void Push(SimHaulRouteResult res)
    {
        // ── 面板落地端：一次调用推一整组，不经过 overlay 合成器 ──
        if (_dyn != null)
        {
            try
            {
                PushDynamic(LoadedKey, _pendingLoaded);
                PushDynamic(EmptyRunKey, _pendingEmpty);
                _attached = true;
            }
            catch (Exception ex)
            {
                res.Notes.Add($"◆ 运输线推送面板失败（{ex.GetType().Name}），本期图上没有线。");
            }
            return;
        }

        try
        {
            var c = Composer;
            c.Set(LoadedKey, Rings(_pendingLoaded), _pendingLoaded.Select(s => s.Rgb).ToArray(), LoadedOrder);
            if (_pendingEmpty.Count > 0)
                c.Set(EmptyRunKey, Rings(_pendingEmpty), _pendingEmpty.Select(s => s.Rgb).ToArray(), EmptyRunOrder);
            else
                c.Remove(EmptyRunKey);
            _attached = true;
            c.Flush();   // 帧驱动方若用 BeginFrame() 包住，这里只攒不推
        }
        catch (Exception ex)
        {
            res.Notes.Add($"◆ 运输线推送 overlay 失败（{ex.GetType().Name}），本期图上没有线。");
        }
    }

    /// <summary>把段推给动态落地端（扁平数组，一次 P/Invoke 级调用）。</summary>
    private void PushDynamic(string group, List<SimHaulSegment> segs)
    {
        if (_dyn == null) return;
        if (segs.Count == 0) { _dyn.SetLines(group, null, null, 0); return; }
        double lift = double.IsNaN(LiftM) || double.IsInfinity(LiftM) ? 0 : LiftM;
        var xyz = new double[segs.Count * 6];
        var argb = new uint[segs.Count];
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            int o = i * 6;
            xyz[o] = s.X0; xyz[o + 1] = s.Y0; xyz[o + 2] = s.Z0 + lift;
            xyz[o + 3] = s.X1; xyz[o + 4] = s.Y1; xyz[o + 5] = s.Z1 + lift;
            argb[i] = 0xFF000000u | s.Rgb;
        }
        _dyn.SetLines(group, xyz, argb, segs.Count);
    }

    private List<double[]> Rings(List<SimHaulSegment> segs)
    {
        var rings = new List<double[]>(segs.Count);
        double lift = double.IsNaN(LiftM) || double.IsInfinity(LiftM) ? 0 : LiftM;
        foreach (var s in segs)
            rings.Add(new[] { s.X0, s.Y0, s.Z0 + lift, s.X1, s.Y1, s.Z1 + lift });
        return rings;
    }

    // ── 文案 ────────────────────────────────────────────────────────────────

    private void BuildText(SimHaulRouteResult res)
    {
        var miss = new List<string>();
        foreach (SimRouteMiss m in Enum.GetValues<SimRouteMiss>())
            if (m != SimRouteMiss.None && res.MissBy[(int)m] > 0) miss.Add($"{MissLabel(m)} {res.MissBy[(int)m]}");

        var skip = new List<string>();
        if (res.OdSkippedApprox > 0) skip.Add($"去向是降级位置 {res.OdSkippedApprox}");
        if (res.OdSkippedUnknownTrust > 0) skip.Add($"去向可信度未声明 {res.OdSkippedUnknownTrust}");
        if (res.OdSkippedNoSourcePos > 0) skip.Add($"源/汇无坐标 {res.OdSkippedNoSourcePos}");
        if (res.OdInvalid > 0) skip.Add($"空条目 {res.OdInvalid}");

        res.HitText =
            $"本期 O-D {res.OdTotal} 笔 · 问路 {res.OdRouted} 笔 · 真路网命中 {res.OdHit + res.OdHitNoSegment}"
          + $"（{res.HitRate:P1}）"
          + (miss.Count > 0 ? $" · 未命中 {res.MissTotal}（{string.Join(" · ", miss)}）" : " · 无未命中")
          + (skip.Count > 0 ? $" · 未问路 {res.OdSkippedTotal}（{string.Join(" · ", skip)}）" : "")
          + $" · 画出 {res.DrawnSegments} 段"
          + (res.DroppedByCap > 0 ? $"（另有 {res.DroppedByCap} 段超上限没画）" : "")
          + $"　[分类账自洽：{(res.Balanced ? "是" : "**否 —— 有分支漏计**")}]";

        if (res.OdTotal == 0)
            res.Summary = "运输线：本期没有 O-D（排产结果里一条流都没有）—— 没画不是画失败。";
        else if (res.DrawnSegments == 0)
            res.Summary = "◆ 运输线：本期**一条线都没画出来**。原因："
                        + string.Join("；", new[] { string.Join(" · ", skip), string.Join(" · ", miss) }
                                            .Where(s => s.Length > 0))
                        + "。（图上空白 ≠ 没接路网 —— 具体是哪一类见上一行与下面的逐条说明。）";
        else
            res.Summary = $"运输线：画出 {res.DrawnSegments} 段"
                        + (res.EmptyRunSegments > 0 ? $"（含空驶 {res.EmptyRunSegments} 段）" : "")
                        + $"　·　命中 {res.OdHit}/{res.OdRouted} 笔"
                        + (res.OdSkippedTotal > 0 ? $"　·　{res.OdSkippedTotal} 笔未问路（见说明）" : "")
                        + (string.IsNullOrWhiteSpace(FocusUnitId) ? "" : $"　·　高亮单元 {FocusUnitId}");

        res.LegendText =
            $"图例：**色相=物料**（煤 #{SimMaterialColorProvider.RgbCoal:X6} / 岩 #{SimMaterialColorProvider.RgbRock:X6}，"
          + "取自 SimMaterialColorProvider，不另起一套）；**越接近本色=本期载荷越大**"
          + "（本色 干线 ≥p85 · 半灰 支线 ≥p50 · 淡灰 末梢）；"
          + (ShowEmptyRun ? "**纯灰=空驶专走段**（重载不经过的回程）；" : "空驶未画（ShowEmptyRun=false）；")
          + (string.IsNullOrWhiteSpace(FocusUnitId) ? "" : $"高亮「{FocusUnitId}」，其余段压灰；")
          + $"线整体抬升 {LiftM:0.##} m 只为避 z-fight，**不改任何里程数**。";
    }

    private static string Label(SimHaulOd od)
        => (od.UnitId.Length > 0 ? od.UnitId : "(无单元号)") + "→"
         + (od.DestinationName.Length > 0 ? od.DestinationName
            : od.DestinationCode.Length > 0 ? od.DestinationCode : "(无去向码)");

    private static string Join(IEnumerable<string> xs, int cap = 6)
    {
        var l = xs.ToList();
        return l.Count <= cap ? string.Join("、", l) : string.Join("、", l.Take(cap)) + $"…（共 {l.Count} 个）";
    }

    internal static string MissLabel(SimRouteMiss m) => m switch
    {
        SimRouteMiss.NoGraph => "无路网",
        SimRouteMiss.NoPosition => "无坐标",
        SimRouteMiss.SourceUnsnapped => "源未吸附",
        SimRouteMiss.SinkUnsnapped => "汇未吸附",
        SimRouteMiss.SameNode => "源汇同点",
        SimRouteMiss.Unreachable => "不连通",
        SimRouteMiss.Degenerate => "路径退化",
        SimRouteMiss.Error => "异常",
        _ => m.ToString(),
    };

    // ── 颜色：色相=物料，灰度=载荷档 ────────────────────────────────────────

    /// <summary>载荷档 → 向中性灰插值的比例。0=本色。</summary>
    private static double LevelGray(int level) => level switch { 0 => 0.00, 1 => 0.38, _ => 0.68 };

    /// <summary>空驶：几乎纯灰（它与重载段在图上永不重叠，所以不会和末梢混淆）。</summary>
    private const double EmptyRunGray = 0.88;

    /// <summary>高亮模式下非焦点段的灰度。</summary>
    private const double FocusDimGray = 0.86;

    /// <summary>
    /// 向中性灰 0x808080 插值。
    /// <para>★ 为什么是「向灰插值」而不是「乘一个亮度系数」：煤色 #2B3138 已经接近黑，
    /// 再乘 0.5 就成了看不见的纯黑；向灰插值对**任何**基色都是「越淡=载荷越小」，
    /// 深色变亮、浅色变淡，语义一致。</para>
    /// </summary>
    internal static uint TowardGray(uint rgb, double t)
    {
        t = Math.Clamp(t, 0, 1);
        int r = (int)(rgb >> 16 & 0xFF), g = (int)(rgb >> 8 & 0xFF), b = (int)(rgb & 0xFF);
        r = (int)Math.Round(r + (0x80 - r) * t);
        g = (int)Math.Round(g + (0x80 - g) * t);
        b = (int)Math.Round(b + (0x80 - b) * t);
        return (uint)((r & 0xFF) << 16 | (g & 0xFF) << 8 | (b & 0xFF));
    }

    // ── 几何小工具 ──────────────────────────────────────────────────────────

    /// <summary>与 SimRoadGraph / CoalSinkAdapter 同一判据：XY 任一非零即算有坐标。</summary>
    private static bool HasPosition(double x, double y)
        => !double.IsNaN(x) && !double.IsNaN(y) && (Math.Abs(x) > 1e-6 || Math.Abs(y) > 1e-6);

    /// <summary>
    /// 三维折线 → 逐段两点。<b>刻意复用 <see cref="SimOverlayComposer.SplitOpenPolyline"/></b>：
    /// NaN/Inf 与零长段的过滤口径只有一份，两处写迟早漂。
    /// </summary>
    private static List<double[]> Split(IReadOnlyList<Point3d> pts)
    {
        if (pts == null || pts.Count < 2) return new List<double[]>();
        var flat = new double[pts.Count * 3];
        for (int i = 0; i < pts.Count; i++)
        {
            flat[3 * i] = pts[i].X; flat[3 * i + 1] = pts[i].Y; flat[3 * i + 2] = pts[i].Z;
        }
        return SimOverlayComposer.SplitOpenPolyline(flat);
    }

    /// <summary>
    /// 段的去重键：坐标量化到 1 mm，并把两个端点排序（方向无关 —— 一来一回是同一段路）。
    /// <para>量化不会像吸附那样在等距处悄悄换目标：这里比的是**同一张图上同一条中线**产生的
    /// 同一批点，本来就逐位相同，量化只是防浮点尾巴。</para>
    /// </summary>
    private static SegKey KeyOf(double[] s)
    {
        long ax = Q(s[0]), ay = Q(s[1]), az = Q(s[2]);
        long bx = Q(s[3]), by = Q(s[4]), bz = Q(s[5]);
        bool swap = ax > bx || (ax == bx && (ay > by || (ay == by && az > bz)));
        return swap ? new SegKey(bx, by, bz, ax, ay, az) : new SegKey(ax, ay, az, bx, by, bz);
    }

    private static long Q(double v) => (long)Math.Round(v * 1000.0);

    private readonly record struct SegKey(long AX, long AY, long AZ, long BX, long BY, long BZ);

    /// <summary>最近秩分位数（不插值），与 <see cref="SimSnapCalibration.Quantile"/> 同口径。</summary>
    private static double Quantile(double[] sortedAsc, double q)
    {
        if (sortedAsc.Length == 0) return 0;
        int i = (int)Math.Ceiling(Math.Clamp(q, 0, 1) * sortedAsc.Length) - 1;
        return sortedAsc[Math.Clamp(i, 0, sortedAsc.Length - 1)];
    }

    /// <summary>期次 + O-D 内容指纹（变了才重建）。FNV-1a/64。</summary>
    private static ulong Signature(string? periodKey, IReadOnlyList<SimHaulOd>? ods)
    {
        ulong h = Fnv.Seed;
        foreach (char c in periodKey ?? "") h = Fnv.Step(h, c);
        h = Fnv.Step(h, (ulong)(ods?.Count ?? 0));
        for (int i = 0; i < (ods?.Count ?? 0); i++)
        {
            var od = ods![i];
            if (od == null) { h = Fnv.Step(h, 0xDEAD); continue; }
            foreach (char c in od.UnitId) h = Fnv.Step(h, c);
            foreach (char c in od.DestinationCode) h = Fnv.Step(h, c);
            h = Fnv.Step(h, (ulong)BitConverter.DoubleToInt64Bits(od.Sx));
            h = Fnv.Step(h, (ulong)BitConverter.DoubleToInt64Bits(od.Sy));
            h = Fnv.Step(h, (ulong)BitConverter.DoubleToInt64Bits(od.Sz));
            h = Fnv.Step(h, (ulong)BitConverter.DoubleToInt64Bits(od.Dx));
            h = Fnv.Step(h, (ulong)BitConverter.DoubleToInt64Bits(od.Dy));
            h = Fnv.Step(h, (ulong)BitConverter.DoubleToInt64Bits(od.Dz));
            h = Fnv.Step(h, (ulong)BitConverter.DoubleToInt64Bits(od.TonnageT));
            h = Fnv.Step(h, (ulong)(int)od.DestTrust);
            h = Fnv.Step(h, od.IsCoal ? 1UL : 0UL);
        }
        return h;
    }
}
