// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/UnitSolidStage.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Platform.Capabilities;

namespace PitMine3D.Kylin.TaskLib.Simulation;
using PitMine3D.Kylin.Cad.Dump;   // DumpStripPlanner.Cell

// ─────────────────────────────────────────────────────────────────────────────
//  单元体舞台 —— 采掘单元 / 排土位置逐帧显隐（采场被挖掉、排土场长起来）
//
//  ── 与 SolidGeometryPort 的关系：套路照抄，图层各管各的 ──
//  建体（BuildColoredMeshOnLayer）→ 按图层整批收回（GetHandlesByLayer + DeleteEntities）
//  这一套完全沿用 <see cref="SolidGeometryPort"/>（SimGeometryPort.cs:388-509）。
//  **但图层是独立的两个**：<see cref="LayerUnitPit"/> / <see cref="LayerUnitDump"/>。
//  原因：SolidGeometryPort.ClearSolids 是「按图层整层抹除、不看 handle 是谁建的」，
//  两家共用一个图层就会互删。本类同样只清自己这两层，**绝不碰 __SIM_PIT__ / __SIM_DUMP__**。
//
//  ── 为什么是「建一次 + 逐帧显隐」而不是「逐帧建体」──
//  BuildColoredMeshOnLayer 入库并进 Undo 栈；DispatcherTimer 100ms 一 tick，
//  逐帧建体几十帧就把 Undo 栈刷爆。而 SetEntitiesVisible 不入库、可整批传，
//  于是：**开场建一次全部体（含未来期的），之后每帧只切可见性**。
//  一帧最多两次 P/Invoke（一次显、一次隐），传的是**整个 handle 数组**，绝不逐体调。
//
//  ── 几何：有真轨用真轨，没有就轴对齐盒子，且说得出用的是哪一种 ──
//   · 采掘单元 → 会话内 <see cref="MiningModelStore"/> 的 Strip（CrestXyz/ToeXyz）；
//   · 排土位置 → 会话内 <see cref="DumpStripStore"/> 的 Cell（CrestXyz/ToeXyz + CrestZ/ToeZ）；
//   · 都没有（台账是从 CSV 读回来的、真轨不持久化）→ 按质心 + 长宽厚建**轴对齐盒子**，
//     并把这一批标成 <see cref="UnitSolidGeometrySource.Box"/>。
//   台账 Row 只有质心 + 长宽厚，**没有方位角**，所以盒子只能轴对齐 —— 不拿 0 方位角当真值。
//   调用方读 <see cref="UnitSolidBuildResult.GeometrySourceLabel"/> 就能在界面上写明「形态为近似」。
//
//  ── 真轨体的形状：平面多边形竖向拉伸（与内核 StripPrism 同口径）──
//  平面轮廓 = 前脸轨（crest）+ 它沿推进方向偏 W 的后界轨（<see cref="DumpStripPlanner.OffsetRail"/>，
//  等距偏移、拐角走角平分线/圆弧，不是逐点法向平移）；竖向从底 Z 拉到顶 Z。
//  这正是内核 <c>StripPrism.hpp</c> 现在的造法（「平面形状由 Clipper2 保证不自交，再竖向拉伸」），
//  **不是**四条轨逐点 loft 的斜前脸。坡底轨在这里只用于两件事：定推进方向的正负号、给底 Z。
//  推进方向正负号沿用内核口径（CarveStrip.cpp:233）：采场 d 指坡顶/高墙，排土 d 指坡脚/外；
//  <see cref="DumpStripPlanner.AdvanceDirs"/> 返回的是**排土口径**（指坡脚），采场侧取反。
//
//  ── 口径：本类不算方量 ──
//  体积只算一个「散度积分几何体积」，用途仅有两个：① 分片之和 = 整体（分片没丢料，可证伪）；
//  ② 摆出来让人自己和台账对。**不做实方/占容方换算，不写死密度与膨胀系数**。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>单元体的几何来源。<b>近似必须说得出来</b>，所以它是结果的一部分而不是内部细节。</summary>
public enum UnitSolidGeometrySource
{
    /// <summary>会话内真轨（采矿模型 Strip / 排土条带 Cell 的坡顶+坡底线）。</summary>
    RealRail,
    /// <summary>质心 + 长宽厚的**轴对齐盒子**（台账里没有方位角，也没有折线）——形态为近似。</summary>
    Box,
}

/// <summary>建好的一个体（= 一个单元，或一个单元的一片）。</summary>
public sealed class UnitSolidBody
{
    public string UnitId = "";
    public LedgerKind Kind;
    public UnitSolidGeometrySource GeometrySource;

    /// <summary>本片覆盖本单元完成度区间 [<see cref="DoneFrom"/>, <see cref="DoneTo"/>]（沿走向等比例切）。</summary>
    public double DoneFrom, DoneTo;
    public int SliceIndex, SliceCount = 1;
    /// <summary>本片占整单元的份额（= DoneTo − DoneFrom）。</summary>
    public double Fraction => Math.Max(0, DoneTo - DoneFrom);

    /// <summary>
    /// 本片在第几帧「做完」。采场：该帧起隐藏（挖掉了）；排土：该帧起显示（排上去了）。
    /// <b>−1 = 时间轴内没做完</b>（计划只排到 DoneTo &lt; 1）：采场片一直可见，排土片一直不可见 ——
    /// 这不是缺省值，是「计划里就没安排它」的如实表达。
    /// </summary>
    public int CompleteFrame = -1;

    /// <summary>入库 handle（0 = 没入库）。</summary>
    public ulong Handle;
    public string LayerName = "";

    public int VertexCount, TriangleCount;
    /// <summary>散度积分几何体积 m³（本类唯一的体积口径，见文件头）。</summary>
    public double VolumeM3;
    public double TopZ, BottomZ;

    /// <summary>
    /// 体的世界 XY 包围。<b>没填过时是 NaN，不是 0</b>。
    /// <para>⚠ 哨兵必须是 NaN：本矿坐标在 X≈62万 / Y≈438万 量级，
    /// 用 0 当"没填"的话，一旦漏填就会得到一个横跨 62 万米的"合法"范围 ——
    /// 那种假值不会让任何判据变红，只会让界面上多一行看着正常的错数。</para>
    /// </summary>
    public double MinX = double.NaN, MaxX = double.NaN, MinY = double.NaN, MaxY = double.NaN;

    /// <summary>偏移后界轨折回的段数（&gt;0 = 凹弯处平面轮廓可能自交，只能看态势）。</summary>
    public int FoldedSegments;

    public string Caption =>
        $"{UnitId}"
      + (SliceCount > 1 ? $" 片{SliceIndex + 1}/{SliceCount}（{DoneFrom:0.##}~{DoneTo:0.##}）" : "")
      + $"　{MiningUnitLedger.KindToText(Kind)}　{TriangleCount} 面 / {VertexCount} 点"
      + $"　{BottomZ:0.##}~{TopZ:0.##}m"
      + (GeometrySource == UnitSolidGeometrySource.Box ? "　◆盒子（近似）" : "　真轨");
}

/// <summary>一个单元（跨帧）。</summary>
public sealed class UnitSolidUnit
{
    public string UnitId = "";
    public LedgerKind Kind;
    public string Region = "", Seam = "";
    public UnitSolidGeometrySource GeometrySource;
    /// <summary>几何来源的人读说明（真轨来自哪儿 / 为什么退成盒子）。</summary>
    public string GeometryNote = "";

    /// <summary>
    /// 建这个体用的<b>那一行台账</b>（几何取的就是它）。null = 没留（老路径）。
    ///
    /// <para><b>为什么要留着</b>：图里的实体身上一个字都没有 —— 只有 handle。
    /// 点中一块想问"这是谁、哪一期、排到哪儿、量多少、形状是真轨还是盒子"，
    /// 只能靠 handle 回查这里。留的是<b>引用</b>不是拷贝：拷一份出来就会有第二个真相，
    /// 表在这中间被改过时两边说的话不一样，而两边看上去都对。</para>
    /// </summary>
    public MiningUnitLedger.Row? Row;

    /// <summary>完成度台阶：(帧序, 该帧结束后的累计完成度)，按帧升序、完成度非降。</summary>
    public List<(int Frame, double DoneAfter)> Steps = new();

    public List<UnitSolidBody> Bodies = new();

    public double GeomVolumeM3 => Bodies.Sum(b => b.VolumeM3);
    /// <summary>本单元在时间轴内排到的最大完成度（&lt;1 = 计划期内没做完）。</summary>
    public double PlannedDone => Steps.Count == 0 ? 0 : Steps[^1].DoneAfter;
}

/// <summary>一次建体的结果。界面直接照它写状态栏与提示，不许再编一遍。</summary>
public sealed class UnitSolidBuildResult
{
    /// <summary>输入里有几个不同的单元（去重后）。</summary>
    public int PlannedUnitCount { get; set; }
    /// <summary>真的建出了至少一个体的单元数。</summary>
    public int BuiltUnitCount => Units.Count(u => u.Bodies.Count > 0);
    /// <summary>入库的体数（含分片）。</summary>
    public int BuiltBodyCount => Units.Sum(u => u.Bodies.Count(b => b.Handle != 0));
    /// <summary>一个体都没建出来的单元数。</summary>
    public int SkippedUnitCount => PlannedUnitCount - BuiltUnitCount;
    /// <summary>开工前清掉的旧体数（自己那两层）。</summary>
    public int Deleted { get; set; }

    public List<UnitSolidUnit> Units { get; } = new();

    public int RealRailUnits => Units.Count(u => u.GeometrySource == UnitSolidGeometrySource.RealRail && u.Bodies.Count > 0);
    public int BoxUnits => Units.Count(u => u.GeometrySource == UnitSolidGeometrySource.Box && u.Bodies.Count > 0);
    /// <summary>退成盒子的单元号（界面要能点开看是哪些）。</summary>
    public List<string> BoxUnitIds => Units.Where(u => u.GeometrySource == UnitSolidGeometrySource.Box && u.Bodies.Count > 0)
                                           .Select(u => u.UnitId).ToList();
    /// <summary>这一批用的是真轨还是盒子 —— 界面必须显示这一句。</summary>
    public string GeometrySourceLabel =>
        BoxUnits == 0
            ? (RealRailUnits > 0 ? $"几何来源：全部 {RealRailUnits} 个单元用**会话内真轨**（采矿模型 / 排土条带）。"
                                 : "几何来源：没有建出任何体。")
            : (RealRailUnits > 0
                ? $"几何来源：真轨 {RealRailUnits} 个 · **轴对齐盒子 {BoxUnits} 个（形态为近似）**。"
                  + "盒子那一批只有质心+长宽厚（台账里没有方位角、没有折线），朝向不代表真实采掘带走向。"
                : $"几何来源：**全部 {BoxUnits} 个单元都是轴对齐盒子（形态为近似）** —— "
                  + "本会话没跑过「采矿模型」/「排土条带」，真轨不持久化，台账里只有质心+长宽厚。");

    /// <summary>部分完成的处理方案（界面必须显示这一句）。</summary>
    public string PartialSchemeLabel { get; set; } = "";

    /// <summary>
    /// 并片带来的**最大显隐误差**（份额，0~1）。并片时被并掉的那一段会跟着相邻片的时刻显隐，
    /// 于是那一段在若干帧里「早出现 / 晚消失」。0 = 没并过片，逐帧显隐与完成度**精确**一致。
    /// <para>它是逐帧判据的容差来源 —— 容差不是拍的，是这里算出来的。</para>
    /// </summary>
    public double MaxSliceMergeError { get; set; }

    /// <summary>入库成功且填过包围盒的体。</summary>
    private IEnumerable<UnitSolidBody> Located =>
        Units.SelectMany(u => u.Bodies).Where(b => b.Handle != 0 && !double.IsNaN(b.MinX));

    /// <summary>有没有可用的位置信息。<b>先判它再取 Min/Max</b> —— 空序列上 Min() 会抛。</summary>
    public bool HasBounds => Located.Any();

    /// <summary>
    /// 体建在哪儿 —— <b>界面必须显示这一句</b>。
    /// <para>没有它的话，「上图 2312/2312 个单元、全部真轨、建体 8000 ms」
    /// 和「屏幕上什么都没有」是<b>完全兼容</b>的两件事：本矿坐标在 62 万量级，
    /// 视口不在那一带就是全黑，而每个数字看上去都正常。</para>
    /// </summary>
    public string LocationLabel
    {
        get
        {
            if (!HasBounds) return "位置：没建出体。";
            var b = Located.ToList();
            double x0 = b.Min(v => v.MinX), x1 = b.Max(v => v.MaxX);
            double y0 = b.Min(v => v.MinY), y1 = b.Max(v => v.MaxY);
            double z0 = b.Min(v => v.BottomZ), z1 = b.Max(v => v.TopZ);
            return $"位置：X {x0:0}~{x1:0}　Y {y0:0}~{y1:0}　Z {z0:0}~{z1:0}"
                 + $"（跨度 {x1 - x0:0} × {y1 - y0:0} m）—— 视口不在这一带就什么也看不到。";
        }
    }

    /// <summary>逐条降级/跳过原因，一条都不吞。</summary>
    public List<string> Messages { get; } = new();
    public string Summary { get; set; } = "";
    public bool Ok => BuiltBodyCount > 0;

    // ── 实测耗时（不是估的：Stopwatch 就在建体循环里）──
    /// <summary>托管侧三角化合计 ms。</summary>
    public double TriangulateMs { get; set; }
    /// <summary>BuildColoredMeshOnLayer 入库合计 ms（含 P/Invoke 与内核建体）。</summary>
    public double IngestMs { get; set; }
    /// <summary>建体总耗时 ms（清旧体 + 三角化 + 入库 + 首帧显隐）。</summary>
    public double TotalMs { get; set; }
    public string PerfLabel =>
        BuiltBodyCount <= 0 ? "耗时：没建出体"
        : $"建体实测：{BuiltBodyCount} 个体合计 {TotalMs:0.#} ms"
          + $"（三角化 {TriangulateMs:0.#} ms · 入库 {IngestMs:0.#} ms · 平均 {TotalMs / BuiltBodyCount:0.##} ms/体）";
}

/// <summary>一帧显隐切换的结果。</summary>
public sealed class UnitFrameApplyResult
{
    public int FrameIndex { get; set; }
    public int VisibleBodies { get; set; }
    public int HiddenBodies { get; set; }
    /// <summary>本帧真的改了几个体的可见性（0 = 与上一帧同集合，一次 P/Invoke 都没发）。</summary>
    public int Changed { get; set; }
    /// <summary>本帧发了几次 SetEntitiesVisible（0/1/2）。</summary>
    public int Calls { get; set; }
    /// <summary>实测耗时 ms。</summary>
    public double ElapsedMs { get; set; }
    public string Caption =>
        $"第 {FrameIndex} 帧：可见 {VisibleBodies} / 隐藏 {HiddenBodies}　"
      + $"改动 {Changed} 个（{Calls} 次批量调用）　{ElapsedMs:0.##} ms";
}

/// <summary>建体参数。</summary>
public sealed class UnitSolidStageOptions
{
    /// <summary>
    /// **有序**期次键（= 时间轴各帧的 <see cref="SimFrame.Period"/>）。
    /// 台账 Row.Period 按它查出帧序；查不到的行不建体（不猜它属于哪一帧），并如实记账。
    /// </summary>
    public IReadOnlyList<string> PeriodKeys { get; set; } = Array.Empty<string>();

    /// <summary>每个单元最多切几片（部分完成用）。台阶更多时并到这个数，并说出并了多少。</summary>
    public int MaxSlicesPerUnit { get; set; } = 4;

    /// <summary>小于这个份额的片并进相邻片（免得建出一堆看不见的薄片）。</summary>
    public double MinSliceFraction { get; set; } = 0.02;

    /// <summary>
    /// 完成度列为空（0）时，是否按「该期做完」处理。
    /// <para>true（默认）：末帧的空完成度按 1.0 记，并**逐个记账**（这是替用户做的决定）。
    /// false：空就是 0，采场单元永不消失、排土单元永不出现 —— 也会记账。</para>
    /// </summary>
    public bool TreatBlankDoneAsFull { get; set; } = true;

    /// <summary>建完后立刻切到这一帧（&lt;0 = 不切，保持内核新建实体的默认可见）。</summary>
    public int InitialFrame { get; set; } = 0;

    /// <summary>允许用会话内真轨（false = 强制全部走盒子，只用于对照验收）。</summary>
    public bool UseRealRails { get; set; } = true;

    /// <summary>建完发一次 ZOOMEXTENTS。见 <see cref="UnitSolidStage.StaticOptions.ZoomExtentsAfterBuild"/>。</summary>
    public bool ZoomExtentsAfterBuild { get; set; } = true;
}

/// <summary>
/// 单元体舞台：把本期计划涉及的**采掘单元 + 排土位置**各建成一个（或几片）三维体，
/// 之后按完成度逐帧切显隐。见文件头的分工说明。
/// <para><b>图层独占</b>：只用 <see cref="LayerUnitPit"/> / <see cref="LayerUnitDump"/>，
/// 清理只清这两层。别的图层（含 <c>__SIM_PIT__</c> / <c>__SIM_DUMP__</c>）一律不碰。</para>
/// </summary>
public sealed class UnitSolidStage
{
    /// <summary>采掘单元（煤 + 岩）专用图层。<b>别往里放别的东西</b> —— 清理是按图层整批删的。</summary>
    public const string LayerUnitPit = "__SIM_UNIT_PIT__";
    /// <summary>排土位置专用图层。</summary>
    public const string LayerUnitDump = "__SIM_UNIT_DUMP__";

    /// <summary>AcDbEntityType.TriangleMesh（样式源必须是一张三角网）。</summary>
    private const int TypeIdTriangleMesh = 6;

    // ── 配色：煤/岩沿用 SimMaterialColorProvider 的两个常量，排土沿用 SimSolidBuilder 的外排蓝 ──
    //    一个量一个来源：三维体和层体不能两套颜色。
    private const uint RgbCoal = SimMaterialColorProvider.RgbCoal;
    private const uint RgbRock = SimMaterialColorProvider.RgbRock;
    private const uint RgbDump = SimSolidBuilder.RgbExternalDump;
    /// <summary>盒子（降级）体整体调向的灰 —— 图上一眼能看出「这一批是近似形态」。</summary>
    private const uint RgbBoxGrey = 0x9AA0A6;
    /// <summary>盒子体与灰的混合比（0 = 不调灰，1 = 全灰）。</summary>
    private const double BoxGreyMix = 0.55;
    /// <summary>底面压暗系数（纯视觉，不带数据含义）。</summary>
    private const double BottomShade = 0.55;

    private readonly IEntityCapability _ent;

    /// <summary>样式源（取 basePoint / 实体色 / 透明度）。0 = 还没找到。</summary>
    private ulong _srcHandle;
    private string _reason = "单元体舞台：未建体";

    /// <summary>建过的体（收回时与图层查询取并集）。</summary>
    private readonly List<UnitSolidBody> _bodies = new();
    /// <summary>当前**已经是可见状态**的 handle 集合（差分用，避免每帧全量重设）。</summary>
    private readonly HashSet<ulong> _visible = new();
    private int _lastFrame = int.MinValue;
    private UnitFrameApplyResult? _lastApply;

    private UnitSolidBuildResult? _last;

    public UnitSolidStage(IEntityCapability ent) => _ent = ent ?? throw new ArgumentNullException(nameof(ent));

    /// <summary>接得上宿主实体能力就给一个舞台，接不上给 null（调用方据此退化，不抛）。</summary>
    public static UnitSolidStage? TryCreate(out string reason)
    {
        try
        {
            var ent = SimHost.Entities;
            if (ent == null)
            {
                reason = "单元体舞台：未注入实体能力 IEntityCapability"
                       + "（在 TaskLibPlugin.Initialize 调 SimHost.Inject(context.Capabilities) 即可启用）";
                return null;
            }
            reason = "";
            return new UnitSolidStage(ent);
        }
        catch (Exception ex)
        {
            reason = $"单元体舞台创建失败（{ex.GetType().Name}）";
            return null;
        }
    }

    /// <summary>最近一次建体的结果（null = 还没建过）。</summary>
    public UnitSolidBuildResult? LastBuild => _last;
    /// <summary>最近一次显隐切换的结果（null = 还没切过）。</summary>
    public UnitFrameApplyResult? LastApply => _lastApply;
    /// <summary>舞台上现有的体（含没入库的，Handle=0）。</summary>
    public IReadOnlyList<UnitSolidBody> Bodies => _bodies;
    /// <summary>状态 / 不可用原因（界面直接显示这一句）。</summary>
    public string StatusLabel => _last is { Ok: true }
        ? $"单元体舞台：{_last.BuiltBodyCount} 个体在图层 {LayerUnitPit} / {LayerUnitDump}"
        : _reason;

    // ═════════════════════════ 拾取回显：一块体自己的账 ═════════════════════════
    //
    //  图里的实体身上【一个字都没有】—— 建体走 BuildColoredMeshOnLayer，只回来一个 handle，
    //  UnitId / 期次 / 去向 / 量 / 形状是真轨还是盒子，全在本类内存里。
    //  于是"这块是谁"只能看窗口顶上那句全局 Caption：它说的是【整批】的情况，
    //  而人指着屏幕问的永远是【这一块】。两者长得都对，但答的不是同一个问题。
    //
    //  ⚠ 这条路只在【本进程、本次建体】内成立：句柄索引不进图纸，
    //    另存/重开之后就查不到了。要让信息跟着实体走，得给实体挂 XData（另一条路）。

    /// <summary>handle → 体。查不到返回 null（别的图层的实体、或不是本舞台建的）。</summary>
    public UnitSolidBody? FindBody(ulong handle)
        => handle == 0 ? null : _bodies.FirstOrDefault(b => b.Handle == handle);

    /// <summary>一块体自己的账（多行人读文本）。查不到就说清楚为什么查不到，不返回空串。</summary>
    public string Describe(ulong handle)
    {
        if (handle == 0) return "没有句柄。";
        var b = FindBody(handle);
        if (b == null)
            return $"句柄 {handle} 不是本舞台建的体"
                 + $"（本舞台只认图层 {LayerUnitPit} / {LayerUnitDump} 上、本次建体入库的 {_bodies.Count(x => x.Handle != 0)} 个）。"
                 + "\n· 选中的可能是别的图层的实体，也可能是上一次建体留下的 —— 句柄索引只在本次建体内有效。";

        var u = _last?.Units.FirstOrDefault(x => string.Equals(x.UnitId, b.UnitId, StringComparison.Ordinal));
        var row = u?.Row;
        var sb = new StringBuilder();
        sb.AppendLine($"【{b.UnitId}】{MiningUnitLedger.KindToText(b.Kind)}"
                    + (row != null && row.Region.Length > 0 ? $"　{row.Region}" : "")
                    + (row != null && row.Seam.Length > 0 ? $"　{row.Seam}" : ""));
        sb.AppendLine($"· 形状：{(b.GeometrySource == UnitSolidGeometrySource.Box ? "◆ 盒子（近似）" : "真轨")}"
                    + $"　{b.TriangleCount} 面 / {b.VertexCount} 点　顶底 {b.BottomZ:0.##}~{b.TopZ:0.##} m"
                    + $"　几何体积 {b.VolumeM3 / 1e4:0.###} 万m³"
                    + (b.FoldedSegments > 0 ? $"　◆ 后界轨折回 {b.FoldedSegments} 段（凹弯处轮廓可能自交）" : ""));
        if (b.SliceCount > 1)
            sb.AppendLine($"· 分片：第 {b.SliceIndex + 1}/{b.SliceCount} 片，完成度区间 {b.DoneFrom:0.##}~{b.DoneTo:0.##}"
                        + (b.CompleteFrame >= 0 ? $"，第 {b.CompleteFrame} 帧做完" : "，**时间轴内没做完**"));
        if (u != null && u.GeometryNote.Length > 0) sb.AppendLine("· 几何来源：" + u.GeometryNote);

        if (row == null)
            sb.AppendLine("◆ 这一块没有留住它的台账行 —— 期次/去向/量报不出来（老路径建的体）。");
        else
        {
            sb.AppendLine($"· 台账：期次 {(row.Period.Length > 0 ? row.Period : "(空)")}"
                        + $"　状态 {(row.Status.Length > 0 ? row.Status : "(空)")}　完成度 {row.Done * 100:0.#}%"
                        + $"　推进序 {row.Seq}");
            sb.AppendLine($"· 尺寸（台账列）：走向长 {row.LengthM:0.#} × 推进宽 {row.WidthM:0.#} × 厚 {row.ThickM:0.##} m"
                        + $"　走向方位 {(row.AzimuthDeg.HasValue ? row.AzimuthDeg.Value.ToString("0.#") + "°" : "**没有**（盒子只能轴对齐）")}"
                        + $"　质心 ({row.Cx:0.#}, {row.Cy:0.#}, {row.Cz:0.##})");
            if (row.Flows.Count == 0)
                sb.AppendLine("◆ 这一格【没有流】—— 排产没给它去向，下游按「没有这笔」算。");
            else
                foreach (var f in row.Flows)
                    sb.AppendLine($"· 流 → {(string.IsNullOrWhiteSpace(f.Destination) ? "**去向为空**" : f.Destination)}"
                                + $"　{f.InSituM3 / 1e4:0.###} 万m³实方　运距 {f.HaulKm:0.###} km"
                                + $"　物料 {(string.IsNullOrWhiteSpace(f.MaterialCode) ? "(空)" : f.MaterialCode)}");
        }
        sb.Append($"· 句柄 {b.Handle}　图层 {b.LayerName}");
        return sb.ToString();
    }

    /// <summary>
    /// 把<b>当前选中的</b>实体逐个报出来。选了几个就报几个，
    /// <b>不是本舞台的也要报</b> —— 悄悄跳过的话，人指着一块问"它是谁"会得到一片空白。
    /// </summary>
    public string DescribeSelection(int maxDetail = 5)
    {
        ulong[] sel;
        try { sel = _ent.GetSelectedHandles() ?? Array.Empty<ulong>(); }
        catch (Exception ex) { return $"取选中实体失败（{ex.GetType().Name}：{ex.Message}）。"; }

        if (sel.Length == 0)
            return "图上没有选中任何实体 —— 先在图里点一块单元体（本舞台的体在图层 "
                 + $"{LayerUnitPit} / {LayerUnitDump} 上），再点这个按钮。";

        var mine = sel.Where(h => FindBody(h) != null).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"选中 {sel.Length} 个实体，其中本舞台的单元体 {mine.Count} 个。");
        if (mine.Count == 0)
        {
            sb.Append("◆ 一个都不是本次建体入库的 —— 可能选的是别的图层的实体，"
                    + "也可能是上一次建体留下的（句柄索引只在本次建体内有效，重建一次就换了一批）。");
            return sb.ToString();
        }
        foreach (var h in mine.Take(maxDetail))
            sb.AppendLine(new string('-', 48)).AppendLine(Describe(h));
        if (mine.Count > maxDetail)
            sb.AppendLine($"…还有 {mine.Count - maxDetail} 个没展开（一次只详报 {maxDetail} 个）。");
        return sb.ToString().TrimEnd();
    }

    // ═════════════════════════ 建体（一次） ═════════════════════════

    /// <summary>
    /// 把台账行建成体。<b>同一个 UnitId 的多行 = 该单元跨了多期</b>（各行的 Period/Done 给出完成度台阶），
    /// 几何取第一行。全程不抛。
    /// </summary>
    /// <param name="rows">本次要上台的台账行（可以跨多期）。</param>
    /// <param name="opt">参数；<see cref="UnitSolidStageOptions.PeriodKeys"/> 必须是时间轴的有序期次键。</param>
    public UnitSolidBuildResult Build(IReadOnlyList<MiningUnitLedger.Row>? rows, UnitSolidStageOptions opt)
    {
        var res = new UnitSolidBuildResult();
        var swAll = Stopwatch.StartNew();
        opt ??= new UnitSolidStageOptions();

        try
        {
            res.Deleted = ClearSolids();

            if (rows == null || rows.Count == 0)
            {
                _reason = "单元体舞台：没有台账行可建体。";
                res.Messages.Add(_reason);
                res.Summary = _reason;
                _last = res;
                return res;
            }
            if (opt.PeriodKeys == null || opt.PeriodKeys.Count == 0)
            {
                _reason = "单元体舞台：没有给期次序列（PeriodKeys），定不了每个单元落在第几帧 → 不建体。";
                res.Messages.Add(_reason);
                res.Summary = _reason;
                _last = res;
                return res;
            }
            // 样式源找不到**不是**建不了体（见 ResolveSourceOrDegrade）——照建，只是留条说明。
            ulong src = ResolveSourceOrDegrade(res);

            // 期次键 → 帧序
            var frameOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < opt.PeriodKeys.Count; i++)
            {
                string k = (opt.PeriodKeys[i] ?? "").Trim();
                if (k.Length > 0 && !frameOf.ContainsKey(k)) frameOf[k] = i;
            }

            // 真轨索引（会话内，不持久化 —— 取不到就退盒子）
            var rails = opt.UseRealRails ? RailIndex.Snapshot() : RailIndex.Empty();
            res.Messages.Add(rails.Caption);

            // ── 分组：一个 UnitId 一个单元；同号多行 = 跨期 ──
            var groups = new Dictionary<string, List<MiningUnitLedger.Row>>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var r in rows)
            {
                if (r == null || string.IsNullOrWhiteSpace(r.UnitId)) continue;
                if (!groups.TryGetValue(r.UnitId, out var g)) { groups[r.UnitId] = g = new(); order.Add(r.UnitId); }
                g.Add(r);
            }
            res.PlannedUnitCount = order.Count;

            int unmatchedPeriod = 0, blankDoneFilled = 0, nonMonotonic = 0, mergedSlices = 0, geomConflict = 0;
            string firstUnmatched = "";

            foreach (var uid in order)
            {
                var g = groups[uid];
                var head = g[0];
                var unit = new UnitSolidUnit
                {
                    UnitId = uid,
                    Kind = head.Kind,
                    Region = head.Region,
                    Seam = head.Seam,
                    Row = head,          // 拾取回显要靠它回查这一格自己的账
                };
                res.Units.Add(unit);

                // 同号多行的几何应当一致（几何以模型为准，按 UnitId 刷新）。差得离谱说明表被手改过。
                foreach (var r in g)
                {
                    if (Math.Abs(r.Cx - head.Cx) > 1.0 || Math.Abs(r.Cy - head.Cy) > 1.0)
                    { geomConflict++; break; }
                }

                // ① 完成度台阶
                foreach (var r in g)
                {
                    string pk = (r.Period ?? "").Trim();
                    if (pk.Length == 0 || !frameOf.TryGetValue(pk, out int fi))
                    {
                        unmatchedPeriod++;
                        if (firstUnmatched.Length == 0) firstUnmatched = $"{uid}「{pk}」";
                        continue;
                    }
                    unit.Steps.Add((fi, Math.Clamp(r.Done, 0, 1)));
                }
                unit.Steps.Sort((a, b) => a.Frame.CompareTo(b.Frame));

                // 空完成度：只有末帧能按「该期做完」补 1.0，中间帧补不了（那是真的不知道）
                for (int i = 0; i < unit.Steps.Count; i++)
                {
                    var (f, d) = unit.Steps[i];
                    if (d > 1e-9) continue;
                    if (i == unit.Steps.Count - 1 && opt.TreatBlankDoneAsFull)
                    { unit.Steps[i] = (f, 1.0); blankDoneFilled++; }
                }
                // 单调化：累计完成度不该下降，下降说明表被手改坏了
                double run = 0;
                for (int i = 0; i < unit.Steps.Count; i++)
                {
                    var (f, d) = unit.Steps[i];
                    if (d < run - 1e-9) nonMonotonic++;
                    run = Math.Max(run, d);
                    unit.Steps[i] = (f, run);
                }
                // 同一帧出现多次 → 保留最大完成度那一条
                if (unit.Steps.Count > 1)
                {
                    var dedup = new List<(int Frame, double DoneAfter)>();
                    foreach (var s in unit.Steps)
                    {
                        if (dedup.Count > 0 && dedup[^1].Frame == s.Frame) dedup[^1] = s;
                        else dedup.Add(s);
                    }
                    unit.Steps = dedup;
                }

                if (unit.Steps.Count == 0)
                {
                    res.Messages.Add($"{uid}：期次不在时间轴上 → 不建体（不猜它属于哪一帧）。");
                    continue;
                }

                // ② 切片区间：[0,d1],[d1,d2]…；末尾 DoneTo<1 时还有一片「计划期内没做完」的尾巴
                double mergeErr = 0;
                var cuts = SliceCuts(unit, opt, ref mergedSlices, ref mergeErr);
                if (mergeErr > res.MaxSliceMergeError) res.MaxSliceMergeError = mergeErr;
                if (cuts.Count == 0)
                {
                    res.Messages.Add($"{uid}：完成度台阶算不出可用的分片（完成度全 0）→ 不建体。");
                    continue;
                }

                // ③ 几何
                var geo = ResolveGeometry(head, rails, opt);
                unit.GeometrySource = geo.Source;
                unit.GeometryNote = geo.Note;
                if (!geo.Ok)
                {
                    res.Messages.Add($"{uid}：{geo.Note}");
                    continue;
                }

                // ④ 逐片建体 + 入库
                for (int k = 0; k < cuts.Count; k++)
                {
                    var (t0, t1, frame) = cuts[k];
                    var body = new UnitSolidBody
                    {
                        UnitId = uid,
                        Kind = unit.Kind,
                        GeometrySource = geo.Source,
                        DoneFrom = t0,
                        DoneTo = t1,
                        SliceIndex = k,
                        SliceCount = cuts.Count,
                        CompleteFrame = frame,
                        LayerName = unit.Kind == LedgerKind.Dump ? LayerUnitDump : LayerUnitPit,
                    };

                    // 排土：一直不出现的尾巴不建体（建了也永远看不见），如实记一笔
                    if (unit.Kind == LedgerKind.Dump && frame < 0)
                    {
                        res.Messages.Add($"{uid} 片{k + 1}（{t0:0.##}~{t1:0.##}）：时间轴内没排到 → 排土体永不出现，不建。");
                        continue;
                    }

                    var swT = Stopwatch.StartNew();
                    bool ok;
                    double[] xyz; uint[] tris; string gwhy; int folded;
                    try { ok = geo.Build(t0, t1, out xyz, out tris, out folded, out gwhy); }
                    catch (Exception ex)
                    {
                        ok = false; folded = 0;
                        xyz = Array.Empty<double>(); tris = Array.Empty<uint>();
                        gwhy = $"三角化异常（{ex.GetType().Name}: {ex.Message}）";
                    }
                    swT.Stop();
                    res.TriangulateMs += swT.Elapsed.TotalMilliseconds;
                    body.FoldedSegments = folded;
                    if (folded > 0)
                        res.Messages.Add($"{uid} 片{k + 1}：后界轨有 {folded} 段折回（凹弯的曲率半径小于推进宽），"
                                       + "平面轮廓在那儿可能自交 —— 这个体只能看态势，不能量方。");

                    if (!ok || tris.Length < 12)
                    {
                        res.Messages.Add($"{uid} 片{k + 1}：{gwhy}");
                        continue;
                    }

                    body.VertexCount = xyz.Length / 3;
                    body.TriangleCount = tris.Length / 3;
                    body.VolumeM3 = MeshVolume(xyz, tris);
                    body.TopZ = MaxZ(xyz);
                    body.BottomZ = MinZ(xyz);
                    FillBounds(xyz, body);
                    var rgb = Colorize(xyz, unit.Kind, geo.Source);

                    var swI = Stopwatch.StartNew();
                    ulong h = 0;
                    try { h = _ent.BuildColoredMeshOnLayer(src, xyz, tris, rgb, 0.0, body.LayerName); }
                    catch (Exception ex) { res.Messages.Add($"{uid} 片{k + 1}：入库异常（{ex.GetType().Name}: {ex.Message}）"); }
                    swI.Stop();
                    res.IngestMs += swI.Elapsed.TotalMilliseconds;

                    if (h == 0)
                    {
                        res.Messages.Add($"{uid} 片{k + 1}：入库失败（引擎返回 handle 0；常见原因是样式源已被删除）。");
                        continue;
                    }
                    body.Handle = h;
                    unit.Bodies.Add(body);
                    _bodies.Add(body);
                    _visible.Add(h);        // 新建实体在内核里是可见的 —— 差分的起点必须是这个事实
                }
            }

            // ── 记账：每一次让步都说出来 ──
            if (unmatchedPeriod > 0)
                res.Messages.Add($"◆ 有 {unmatchedPeriod} 行的期次不在时间轴上（第一个是 {firstUnmatched}）→ 那些行不参与显隐。"
                               + "期次键要和时间轴的期标签一字不差，否则单元不会在任何一帧动。");
            if (blankDoneFilled > 0)
                res.Messages.Add($"· 有 {blankDoneFilled} 个单元的**末期完成度是空的**，已按「该期做完（1.0）」处理"
                               + "—— 这是替你做的决定。要改成「空就是没做完」，把 TreatBlankDoneAsFull 关掉。");
            if (nonMonotonic > 0)
                res.Messages.Add($"◆ 有 {nonMonotonic} 处累计完成度在时间上**下降**了 —— 累计量不该变小，"
                               + "已按「取到目前为止的最大值」处理。表可能被手改坏了。");
            if (mergedSlices > 0)
                res.Messages.Add($"· 有 {mergedSlices} 个完成度台阶被并进相邻片（每单元最多 {opt.MaxSlicesPerUnit} 片、"
                               + $"最小片 {opt.MinSliceFraction:0.##}）：被并掉的那段的显隐时刻会跟着相邻片走。");
            if (geomConflict > 0)
                res.Messages.Add($"◆ 有 {geomConflict} 个单元的多期行**质心不一致**（差 &gt;1m），已按第一行的几何建体。"
                               + "同一个 UnitId 在各期应当是同一个东西。");

            res.PartialSchemeLabel = PartialScheme(opt);

            // ⑤ 首帧
            if (opt.InitialFrame >= 0) ApplyFrame(opt.InitialFrame);

            swAll.Stop();
            res.TotalMs = swAll.Elapsed.TotalMilliseconds;

            var bits = new List<string>
            {
                $"上台 {res.BuiltUnitCount}/{res.PlannedUnitCount} 个单元，共 {res.BuiltBodyCount} 个体",
            };
            if (res.SkippedUnitCount > 0) bits.Add($"跳过 {res.SkippedUnitCount} 个（原因逐条在下面）");
            if (res.Deleted > 0) bits.Add($"先清旧体 {res.Deleted} 个");
            bits.Add(res.BoxUnits > 0 ? $"◆ 盒子近似 {res.BoxUnits} 个" : "全部真轨");
            bits.Add(res.PerfLabel);
            res.Summary = string.Join("　·　", bits);
            _reason = res.Summary;

            try { SimHost.View?.RequestRender(); } catch { }
            ZoomIfAsked(SimHost.View, opt.ZoomExtentsAfterBuild, res);
            _last = res;
            return res;
        }
        catch (Exception ex)
        {
            swAll.Stop();
            res.TotalMs = swAll.Elapsed.TotalMilliseconds;
            res.Messages.Add($"单元体舞台建体失败（{ex.GetType().Name}: {ex.Message}）");
            res.Summary = res.Messages[^1];
            _reason = res.Summary;
            _last = res;
            return res;
        }
    }

    private static string PartialScheme(UnitSolidStageOptions opt) =>
        "部分完成的处理：**沿走向按完成度比例切成多片**，每片是独立的体，到点整片消失/出现"
      + $"（每单元最多 {opt.MaxSlicesPerUnit} 片）。"
      + "**近似在哪儿**：切的方向是沿走向等比例，不是月内真实的推进顺序 —— "
      + "台账里没有月内推进方位，所以「这个月采的是这一幅的哪一段」是编不出来的。"
      + "量（份额）是准的，位置只是示意。";

    // ═════════════════════════ 逐帧显隐 ═════════════════════════

    /// <summary>
    /// 切到第 <paramref name="frameIndex"/> 帧。采场「采到了就隐藏」，排土「排到了就显示」。
    /// <para><b>一次性传整个 handle 数组</b>（最多两次 P/Invoke：一次显、一次隐），绝不逐体调；
    /// 与上一帧同集合时一次都不发。</para>
    /// </summary>
    public UnitFrameApplyResult ApplyFrame(int frameIndex)
    {
        var sw = Stopwatch.StartNew();
        var r = new UnitFrameApplyResult { FrameIndex = frameIndex };

        if (_bodies.Count == 0)
        {
            sw.Stop(); r.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            _lastApply = r; return r;
        }
        if (frameIndex == _lastFrame && _lastApply != null)
        {
            // 同帧不重发：播放时 10 次/秒，重发就是白烧 P/Invoke
            sw.Stop();
            var cached = new UnitFrameApplyResult
            {
                FrameIndex = frameIndex,
                VisibleBodies = _lastApply.VisibleBodies,
                HiddenBodies = _lastApply.HiddenBodies,
                Changed = 0,
                Calls = 0,
                ElapsedMs = sw.Elapsed.TotalMilliseconds,
            };
            _lastApply = cached;
            return cached;
        }

        var show = new List<ulong>();
        var hide = new List<ulong>();
        int vis = 0, hid = 0;
        foreach (var b in _bodies)
        {
            if (b.Handle == 0) continue;
            bool want = IsVisibleAt(b, frameIndex);
            if (want) vis++; else hid++;
            bool now = _visible.Contains(b.Handle);
            if (want == now) continue;
            if (want) show.Add(b.Handle); else hide.Add(b.Handle);
        }
        r.VisibleBodies = vis;
        r.HiddenBodies = hid;
        r.Changed = show.Count + hide.Count;

        if (show.Count > 0)
        {
            try { _ent.SetEntitiesVisible(show.ToArray(), true); r.Calls++; } catch { }
            foreach (var h in show) _visible.Add(h);
        }
        if (hide.Count > 0)
        {
            try { _ent.SetEntitiesVisible(hide.ToArray(), false); r.Calls++; } catch { }
            foreach (var h in hide) _visible.Remove(h);
        }
        if (r.Calls > 0) { try { SimHost.View?.RequestRender(); } catch { } }

        _lastFrame = frameIndex;
        sw.Stop();
        r.ElapsedMs = sw.Elapsed.TotalMilliseconds;
        _lastApply = r;
        return r;
    }

    /// <summary>
    /// 这一片在第 f 帧该不该看得见。
    /// <para>采场：没做完之前看得见（还没挖掉）；做完那一帧起隐藏。
    /// 排土：做完那一帧起才看得见（排上去了）。<see cref="UnitSolidBody.CompleteFrame"/>=−1
    /// 表示时间轴内就没排到 —— 采场片一直在，排土片一直没有。</para>
    /// </summary>
    private static bool IsVisibleAt(UnitSolidBody b, int frame)
    {
        bool done = b.CompleteFrame >= 0 && frame >= b.CompleteFrame;
        return b.Kind == LedgerKind.Dump ? done : !done;
    }

    // ═════════════════════════ 纯几何预览（不碰内核）═════════════════════════

    /// <summary>
    /// 预览出来的一个体 —— <b>三角网留在内存里</b>，没有入库、没有 handle、没有图层。
    /// </summary>
    public sealed class PreviewBody
    {
        public string UnitId = "", Region = "", Seam = "", GeometryNote = "";
        public LedgerKind Kind;
        /// <summary>建它用的那一行台账（拾取回显要回查）。</summary>
        public MiningUnitLedger.Row? Row;
        public UnitSolidGeometrySource GeometrySource;
        /// <summary>世界坐标三角网（xyz 平铺）。</summary>
        public double[] Xyz = Array.Empty<double>();
        public uint[] Tris = Array.Empty<uint>();
        public double VolumeM3, TopZ, BottomZ;
        public double MinX = double.NaN, MaxX = double.NaN, MinY = double.NaN, MaxY = double.NaN;
        public int FoldedSegments;
    }

    /// <summary>预览结果。记账口径与入图那条一致 —— 让步一条都不吞。</summary>
    public sealed class PreviewResult
    {
        public List<PreviewBody> Bodies { get; } = new();
        /// <summary>几何算不出来、没能建成体的单元号。</summary>
        public List<string> SkippedUnitIds { get; } = new();
        public List<string> Messages { get; } = new();
        public int PlannedUnitCount { get; set; }
        public double TriangulateMs { get; set; }

        public int RealRailBodies => Bodies.Count(b => b.GeometrySource == UnitSolidGeometrySource.RealRail);
        public int BoxBodies => Bodies.Count(b => b.GeometrySource == UnitSolidGeometrySource.Box);
        public bool Ok => Bodies.Count > 0;
    }

    /// <summary>
    /// 只算几何、<b>不入库</b>：台账行 → 内存里的三角网。
    ///
    /// <para><b>这条路一个前置条件都不需要</b> —— 不要 <see cref="IEntityCapability"/>、
    /// 不要打开的文档、不要图上先有一张三角网当样式源、不碰内核。
    /// 那三条正是「在图上看三维」会失败的全部原因；三维预览对话框走这条，
    /// 于是"点了没反应"这一整类问题在预览里<b>结构上不存在</b>。</para>
    ///
    /// <para><b>与 <see cref="BuildStatic"/> 共用同一份几何</b>：BuildStatic = 本方法 + 入库。
    /// 两边各算一遍的话会慢慢长歪，而且歪了没人发现 —— 预览好看、入图不对，或者反过来。</para>
    ///
    /// <para>真轨仍然来自会话内的 <see cref="MiningModelStore"/> / <c>DumpStripStore</c>（不持久化），
    /// 取不到就退盒子并在 Messages 里说清楚 —— 这一点预览和入图完全一样，<b>预览不许比入图好看</b>。</para>
    /// </summary>
    public static PreviewResult BuildPreview(IReadOnlyList<MiningUnitLedger.Row>? rows, bool useRealRails = true)
    {
        var res = new PreviewResult();
        if (rows == null || rows.Count == 0)
        { res.Messages.Add("没有台账行可建体。"); return res; }

        var rails = useRealRails ? RailIndex.Snapshot() : RailIndex.Empty();
        res.Messages.Add(rails.Caption);

        // 一个 UnitId 一个体。同号多行（跨期）取第一行的几何。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var heads = new List<MiningUnitLedger.Row>();
        int dupRows = 0;
        foreach (var r in rows)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.UnitId)) continue;
            if (seen.Add(r.UnitId)) heads.Add(r); else dupRows++;
        }
        res.PlannedUnitCount = heads.Count;
        if (dupRows > 0)
            res.Messages.Add($"· 有 {dupRows} 行与已建单元同号（跨期的多行），静态视图按第一行的几何建**一个**体。");

        var geoOpt = new UnitSolidStageOptions { UseRealRails = useRealRails };
        foreach (var head in heads)
        {
            var geo = ResolveGeometry(head, rails, geoOpt);
            if (!geo.Ok)
            { res.Messages.Add($"{head.UnitId}：{geo.Note}"); res.SkippedUnitIds.Add(head.UnitId); continue; }

            var swT = Stopwatch.StartNew();
            bool ok; double[] xyz; uint[] tris; string gwhy; int folded;
            try { ok = geo.Build(0, 1, out xyz, out tris, out folded, out gwhy); }
            catch (Exception ex)
            {
                ok = false; folded = 0;
                xyz = Array.Empty<double>(); tris = Array.Empty<uint>();
                gwhy = $"三角化异常（{ex.GetType().Name}: {ex.Message}）";
            }
            swT.Stop();
            res.TriangulateMs += swT.Elapsed.TotalMilliseconds;

            if (folded > 0)
                res.Messages.Add($"{head.UnitId}：后界轨有 {folded} 段折回 —— 平面轮廓可能自交，这个体只能看态势，不能量方。");
            if (!ok || tris.Length < 12)
            { res.Messages.Add($"{head.UnitId}：{gwhy}"); res.SkippedUnitIds.Add(head.UnitId); continue; }

            var pb = new PreviewBody
            {
                UnitId = head.UnitId, Kind = head.Kind, Region = head.Region, Seam = head.Seam,
                GeometrySource = geo.Source, GeometryNote = geo.Note, Row = head,
                Xyz = xyz, Tris = tris,
                VolumeM3 = MeshVolume(xyz, tris),
                TopZ = MaxZ(xyz), BottomZ = MinZ(xyz),
                FoldedSegments = folded,
            };
            FillBoundsP(xyz, pb);
            res.Bodies.Add(pb);
        }
        return res;
    }

    /// <summary>同 <see cref="FillBounds"/>，作用在预览体上。扫不到点就保持 NaN。</summary>
    private static void FillBoundsP(double[] xyz, PreviewBody b)
    {
        if (xyz == null || xyz.Length < 3) return;
        double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
        for (int i = 0; i + 2 < xyz.Length; i += 3)
        {
            double x = xyz[i], y = xyz[i + 1];
            if (x < x0) x0 = x; if (x > x1) x1 = x;
            if (y < y0) y0 = y; if (y > y1) y1 = y;
        }
        if (x0 > x1) return;
        b.MinX = x0; b.MaxX = x1; b.MinY = y0; b.MaxY = y1;
    }

    // ═════════════════════════ 静态建体（不分帧）═════════════════════════

    /// <summary>
    /// 静态建体参数。<b>刻意没有 PeriodKeys / TreatBlankDoneAsFull / 分片</b> ——
    /// 这条路就是"把台账里记的东西照原样摆到图上"，不涉及排产语义。
    /// </summary>
    public sealed class StaticOptions
    {
        /// <summary>允许用会话内真轨（false = 强制全部走盒子，只用于对照验收）。</summary>
        public bool UseRealRails { get; set; } = true;
        /// <summary>建之前先清掉本舞台的旧体。</summary>
        public bool ClearFirst { get; set; } = true;

        /// <summary>
        /// 建完请求重绘用的视图能力。给了就用给的，没给才退 <see cref="SimHost.View"/>。
        /// <para>调用方在 PlanLib 这类模块里时，<c>SimHost</c> 是由 TaskLibPlugin 注入的 ——
        /// 能拿到自己的视图能力就别赌别人的插件已经初始化过。</para>
        /// </summary>
        public IViewCapability? View { get; set; }

        /// <summary>
        /// 建完发一次 <c>ZOOMEXTENTS</c>（内核命令表 xllAcEd.cpp:1565 → <c>Editor::RequestZoomExtents</c>）。
        /// <para><b>⚠ 它框的是【整张图】，不是这批体</b> —— 内核只有 <c>ZoomExtentsAll</c>，
        /// 命令表里 <c>ZOOMEXTENTS/ZE/F</c> 三个键都不带包围盒参数，没有 zoom-to-AABB。
        /// 现状面很大时，体缩完仍然会很小。所以它<b>替代不了</b>
        /// <see cref="UnitSolidBuildResult.LocationLabel"/>，两个都要有。</para>
        /// </summary>
        public bool ZoomExtentsAfterBuild { get; set; } = true;
    }

    /// <summary>
    /// 把台账行<b>照原样</b>建成三维体：一个 UnitId 一个整体、全部可见、不分帧。
    ///
    /// <para><b>为什么要单开一条路，而不是给 <see cref="Build"/> 传个"全期次"</b>：
    /// Build 的骨架是【排产语义】—— 期次→帧序、完成度台阶、分片、逐帧显隐。
    /// 想"看一眼图上记了什么"的人，手里往往一个期次、一格完成度都没有
    /// （基表的这两列本来就是空的，要跑过「按目标排产」才会写）。
    /// 硬走 Build 就得先编一个期次、再编一个完成度，编出来的两个数会一路传到
    /// 显隐时刻和体积上，而每个数看上去都正常。所以这里<b>降一维</b>：
    /// 不需要时间的问题，就别用时间的机器去解。</para>
    ///
    /// <para>复用的是 Build 的几何段（<see cref="ResolveGeometry"/> / 三角化 / 着色 / 入库 / 图层），
    /// <b>没有复制一份</b> —— 几何口径只能有一处，否则两边会慢慢长歪。</para>
    ///
    /// <para><b>同样不许静默降级</b>：真轨还是盒子、跳过了哪几个单元、为什么跳，
    /// 全在 <see cref="UnitSolidBuildResult.GeometrySourceLabel"/> 与 Messages 里，界面照抄。</para>
    /// </summary>
    public UnitSolidBuildResult BuildStatic(IReadOnlyList<MiningUnitLedger.Row>? rows, StaticOptions? options = null)
    {
        var opt = options ?? new StaticOptions();
        var res = new UnitSolidBuildResult();
        var swAll = Stopwatch.StartNew();

        if (rows == null || rows.Count == 0)
        {
            _reason = "静态建体：没有台账行可建体。";
            res.Messages.Add(_reason); res.Summary = _reason; _last = res; return res;
        }
        ulong src = ResolveSourceOrDegrade(res);

        if (opt.ClearFirst) res.Deleted = ClearSolids();

        // ★ 几何全部由 BuildPreview 算 —— 这一条路和三维预览对话框走的是**同一份几何**。
        //   两边各算一遍的话会慢慢长歪，而且歪了没人发现（预览好看、入图不对，或者反过来）。
        var pre = BuildPreview(rows, opt.UseRealRails);
        res.Messages.AddRange(pre.Messages);
        res.PlannedUnitCount = pre.PlannedUnitCount;
        res.TriangulateMs = pre.TriangulateMs;

        foreach (var pb in pre.Bodies)
        {
            var unit = new UnitSolidUnit
            {
                UnitId = pb.UnitId, Kind = pb.Kind, Region = pb.Region, Seam = pb.Seam,
                GeometrySource = pb.GeometrySource, GeometryNote = pb.GeometryNote,
                Row = pb.Row,
            };
            res.Units.Add(unit);

            var body = new UnitSolidBody
            {
                UnitId = pb.UnitId,
                Kind = pb.Kind,
                GeometrySource = pb.GeometrySource,
                DoneFrom = 0, DoneTo = 1,          // 整体，不切片
                SliceIndex = 0, SliceCount = 1,
                CompleteFrame = -1,                 // 静态视图没有帧概念
                LayerName = pb.Kind == LedgerKind.Dump ? LayerUnitDump : LayerUnitPit,
                VertexCount = pb.Xyz.Length / 3,
                TriangleCount = pb.Tris.Length / 3,
                VolumeM3 = pb.VolumeM3,
                TopZ = pb.TopZ, BottomZ = pb.BottomZ,
                MinX = pb.MinX, MaxX = pb.MaxX, MinY = pb.MinY, MaxY = pb.MaxY,
                FoldedSegments = pb.FoldedSegments,
            };

            var rgb = Colorize(pb.Xyz, pb.Kind, pb.GeometrySource);
            var swI = Stopwatch.StartNew();
            ulong h = 0;
            try { h = _ent.BuildColoredMeshOnLayer(src, pb.Xyz, pb.Tris, rgb, 0.0, body.LayerName); }
            catch (Exception ex) { res.Messages.Add($"{pb.UnitId}：入库异常（{ex.GetType().Name}: {ex.Message}）"); }
            swI.Stop();
            res.IngestMs += swI.Elapsed.TotalMilliseconds;

            if (h == 0)
            { res.Messages.Add($"{pb.UnitId}：入库失败（引擎返回 handle 0；常见原因是样式源已被删除）。"); continue; }

            body.Handle = h;
            unit.Bodies.Add(body);
            _bodies.Add(body);
            _visible.Add(h);      // 静态视图全部可见，这也是内核新建实体的事实状态
        }
        // 几何算不出来的单元也要计入 PlannedUnitCount 才能算出 SkippedUnitCount —— 补上空壳
        foreach (var s in pre.SkippedUnitIds)
            res.Units.Add(new UnitSolidUnit { UnitId = s });

        // 静态视图不分帧 —— 把帧状态钉死，免得之后误调 ApplyFrame 把它们按"排产语义"藏起来
        _lastFrame = int.MinValue;
        _lastApply = null;

        res.PartialSchemeLabel = "静态视图：每个单元建一个**整体**，不按完成度切片、不分帧显隐。";
        swAll.Stop();
        res.TotalMs = swAll.Elapsed.TotalMilliseconds;

        var bits = new List<string> { $"上图 {res.BuiltUnitCount}/{res.PlannedUnitCount} 个单元，共 {res.BuiltBodyCount} 个体" };
        if (res.SkippedUnitCount > 0) bits.Add($"跳过 {res.SkippedUnitCount} 个（原因逐条在下面）");
        if (res.Deleted > 0) bits.Add($"先清旧体 {res.Deleted} 个");
        bits.Add(res.BoxUnits > 0 ? $"◆ 盒子近似 {res.BoxUnits} 个" : "全部真轨");
        bits.Add(res.PerfLabel);
        res.Summary = string.Join("　·　", bits);
        _reason = res.Summary;

        var view = opt.View ?? SimHost.View;
        try { view?.RequestRender(); } catch { }
        ZoomIfAsked(view, opt.ZoomExtentsAfterBuild, res);
        _last = res;
        return res;
    }

    /// <summary>
    /// 发一次 ZOOMEXTENTS 并<b>如实记账</b>。
    /// <para><b>措辞不许写"已缩放到位"</b>：<c>ExecuteCommand</c> 返回的是 <i>handled</i>
    /// （命令被分派到了内核，xllAcEd.cpp:1661），不是"相机真的动了" ——
    /// <c>ZoomExtentsAll</c> 内部 <c>hasBounds</c> 为假时一动不动，照样返回 true。</para>
    /// </summary>
    private static void ZoomIfAsked(IViewCapability? view, bool asked, UnitSolidBuildResult res)
    {
        if (!asked || res.BuiltBodyCount <= 0) return;
        bool sent = false;
        try { sent = view?.ExecuteCommand("ZOOMEXTENTS") ?? false; } catch { }
        res.Messages.Add(sent
            ? "· 已发 ZOOMEXTENTS 缩放到**全图**（不是只框这批体 —— 内核没有 zoom-to-AABB；现状面很大时体仍然会很小）。"
            : "◆ ZOOMEXTENTS 没被受理（引擎未就绪？），相机没动 —— 视口不在上面那个坐标带就看不到体。");
    }

    // ═════════════════════════ 清理 ═════════════════════════

    /// <summary>
    /// 清掉本舞台的全部体。<b>只清自己那两层</b>：<see cref="LayerUnitPit"/> / <see cref="LayerUnitDump"/>。
    /// 返回删掉的实体数。幂等。
    /// </summary>
    public int ClearSolids()
    {
        var hs = new List<ulong>();
        foreach (var lay in new[] { LayerUnitPit, LayerUnitDump })
        {
            try
            {
                var arr = _ent.GetHandlesByLayer(lay);
                if (arr != null) hs.AddRange(arr);
            }
            catch { }
        }
        foreach (var b in _bodies) if (b.Handle != 0 && !hs.Contains(b.Handle)) hs.Add(b.Handle);

        _bodies.Clear();
        _visible.Clear();
        _lastFrame = int.MinValue;
        _lastApply = null;

        if (hs.Count == 0) return 0;
        int n = 0;
        try { n = _ent.DeleteEntities(hs.Distinct().ToArray()); } catch { }
        try { SimHost.View?.RequestRender(); } catch { }
        return n;
    }

    /// <summary>清理 + 忘掉上一次的结果（窗口关闭时调）。幂等。</summary>
    public void Reset()
    {
        ClearSolids();
        _last = null;
        _reason = "单元体舞台：已清空";
    }

    // ═════════════════════════ 判据（能证伪的那种） ═════════════════════════

    /// <summary>一条判据的结论。</summary>
    public sealed class Check
    {
        public string Name = "";
        public bool Pass;
        public string Text = "";
        public override string ToString() => $"{(Pass ? "✔" : "✘")} {Name}：{Text}";
    }

    /// <summary>
    /// 跑全部判据。<b>每一条都能被证伪</b>：不是「有没有成功」，而是拿两条独立的路互相对。
    /// <list type="number">
    /// <item>建体数：每个有几何、有期次的单元都上了台，一个不少；跳掉的逐个有名有姓。</item>
    /// <item>逐帧一致：每一帧的可见份额 ↔ 该帧的累计完成度（采场 1−Done、排土 Done），逐单元逐帧对。</item>
    /// <item>分片不丢料：各片几何体积之和 = 整单元一次建出来的体积（分片切法没吃掉料）。</item>
    /// <item>清理无残：清完之后自己那两层里 GetHandlesByLayer 返回 0 个。<b>这一条会真的清掉体</b>，
    ///       所以默认不跑，要跑请传 <paramref name="includeDestructive"/>=true。</item>
    /// <item>降级有标记：盒子那一批在结果里点得出名字、界面能显示。</item>
    /// </list>
    /// </summary>
    public List<Check> Audit(bool includeDestructive = false)
    {
        var outv = new List<Check>();
        var res = _last;
        if (res == null)
        {
            outv.Add(new Check { Name = "前置", Pass = false, Text = "还没建过体，判据无从谈起。" });
            return outv;
        }

        // ① 建体数
        var missing = res.Units.Where(u => u.Bodies.Count == 0).Select(u => u.UnitId).ToList();
        outv.Add(new Check
        {
            // 计划单元数为 0 时判**不通过**：「0 个都建出来了」是一句永远成立的话，那种判据会空过。
            Name = "建体数 = 计划单元数",
            Pass = res.PlannedUnitCount > 0 && missing.Count == 0,
            Text = res.PlannedUnitCount == 0
                ? "输入里一个单元都没有 —— 这条**没跑成**，不是通过。"
                : missing.Count == 0
                ? $"{res.PlannedUnitCount} 个单元全部上台，共 {res.BuiltBodyCount} 个体（含分片）。"
                : $"{res.PlannedUnitCount} 个单元里 {missing.Count} 个没建出体："
                  + string.Join("、", missing.Take(5)) + (missing.Count > 5 ? " …" : "")
                  + " —— 原因逐条在 Messages 里，别当成「跳过了就算了」。",
        });

        // ② 逐帧可见集合 ↔ 完成度
        int frames = 0;
        foreach (var u in res.Units) foreach (var s in u.Steps) frames = Math.Max(frames, s.Frame + 1);
        double worst = 0; string worstWho = "";
        int checkedPairs = 0;
        for (int f = 0; f < frames; f++)
        {
            foreach (var u in res.Units)
            {
                if (u.Bodies.Count == 0) continue;
                double cum = 0;
                foreach (var s in u.Steps) if (s.Frame <= f) cum = Math.Max(cum, s.DoneAfter);
                // 期望：采场 = 还没采掉的份额；排土 = 已经排上去的份额
                double expect = u.Kind == LedgerKind.Dump ? cum : (1.0 - cum);
                // 实际：本帧可见的那些片的份额和。
                // 注意排土的「没排到的尾巴」不建体，所以采场的分母是 1，排土的分母也是 1 —— 两边同口径。
                double actual = u.Bodies.Where(b => b.Handle != 0 && IsVisibleAt(b, f)).Sum(b => b.Fraction);
                // 采场若有「计划期内没做完」的尾巴，它一直可见，正是 1−cum 的一部分，口径自洽。
                double d = Math.Abs(actual - expect);
                checkedPairs++;
                if (d > worst) { worst = d; worstWho = $"{u.UnitId} 第 {f} 帧（应 {expect:0.###} / 实 {actual:0.###}）"; }
            }
        }
        // 容差不是拍的：没并过片时**必须逐帧精确一致**（tol≈0）；并过片才允许并片误差那么多。
        double tol = res.MaxSliceMergeError + 1e-6;
        outv.Add(new Check
        {
            Name = "逐帧可见集合 ↔ 完成度",
            Pass = checkedPairs > 0 && worst <= tol,
            Text = checkedPairs == 0
                ? "没有可对的单元×帧 —— 这条**没跑成**，不是通过。"
                : $"对了 {checkedPairs} 组（{frames} 帧 × {res.BuiltUnitCount} 单元），最大偏差 {worst:0.######}"
                  + (worstWho.Length > 0 ? $"，出在 {worstWho}" : "")
                  + $"；容差 {tol:0.######}（= 并片误差 {res.MaxSliceMergeError:0.####} + 数值余量；没并过片时容差就是 0）。",
        });

        // ③ 分片不丢料
        double worstSlice = 0; string worstSliceWho = "";
        int sliced = 0;
        foreach (var u in res.Units)
        {
            if (u.Bodies.Count < 2) continue;
            sliced++;
            double sumFrac = u.Bodies.Sum(b => b.Fraction);
            // 排土的「没排到的尾巴」不建体，份额本来就少一截 —— 拿建出来的片的份额和去比它们的体积和
            double volSum = u.Bodies.Sum(b => b.VolumeM3);
            double perFrac = sumFrac > 1e-9 ? volSum / sumFrac : 0;
            foreach (var b in u.Bodies)
            {
                if (b.Fraction <= 1e-9 || perFrac <= 1e-9) continue;
                double rel = Math.Abs(b.VolumeM3 - perFrac * b.Fraction) / (perFrac * b.Fraction);
                if (rel > worstSlice) { worstSlice = rel; worstSliceWho = $"{u.UnitId} 片{b.SliceIndex + 1}"; }
            }
        }
        outv.Add(new Check
        {
            Name = "分片体积 ∝ 份额",
            Pass = sliced == 0 || worstSlice <= 0.15,
            Text = sliced == 0
                ? "本次没有单元被切片（没有部分完成），这条不适用。"
                : $"{sliced} 个单元切了片，最大相对偏差 {worstSlice * 100:0.##}%"
                  + (worstSliceWho.Length > 0 ? $"（{worstSliceWho}）" : "") + "。"
                  + "偏差来自沿走向切时两端宽窄不一（弯带上尤其明显），大于 15% 说明切法把料切偏了。",
        });

        // ⑤ 降级有标记（先于④，因为④会清体）
        outv.Add(new Check
        {
            Name = "降级（盒子）有标记",
            Pass = res.BoxUnits == 0 || (res.BoxUnitIds.Count == res.BoxUnits && res.GeometrySourceLabel.Contains("盒子")),
            Text = res.BoxUnits == 0
                ? "本次没有降级：全部用真轨。"
                : $"{res.BoxUnits} 个盒子单元，BoxUnitIds 里点得出名字，GeometrySourceLabel 里写着「形态为近似」。",
        });

        // ④ 清理无残（破坏性，默认不跑）
        if (includeDestructive)
        {
            int deleted = ClearSolids();
            int left = 0;
            foreach (var lay in new[] { LayerUnitPit, LayerUnitDump })
            {
                try { var arr = _ent.GetHandlesByLayer(lay); left += arr?.Length ?? 0; } catch { }
            }
            outv.Add(new Check
            {
                Name = "清理后自己图层不留残体",
                Pass = left == 0,
                Text = left == 0
                    ? $"清掉 {deleted} 个，{LayerUnitPit} / {LayerUnitDump} 现在各 0 个实体。"
                    : $"清掉 {deleted} 个，但两层里还剩 {left} 个 —— 有人往这两个专用图层里放了别的东西，或删除失败。",
            });
        }
        else
        {
            outv.Add(new Check
            {
                Name = "清理后自己图层不留残体",
                Pass = false,
                Text = "**没跑**（这条会真的清掉舞台上的体）。要跑请 Audit(includeDestructive: true)。"
                     + "「没跑」不是「通过」。",
            });
        }
        return outv;
    }

    // ═════════════════════════ 分片 ═════════════════════════

    /// <summary>
    /// 完成度台阶 → 分片区间 (t0, t1, 做完那一帧)。
    /// 末尾没到 1 时补一片「计划期内没做完」的尾巴（帧 = −1）。
    /// </summary>
    private static List<(double T0, double T1, int Frame)> SliceCuts(UnitSolidUnit u, UnitSolidStageOptions opt,
                                                                     ref int merged, ref double mergeErr)
    {
        var raw = new List<(double T0, double T1, int Frame)>();
        double prev = 0;
        foreach (var (f, d) in u.Steps)
        {
            if (d <= prev + 1e-9) continue;         // 这一帧没有推进，不出片
            raw.Add((prev, d, f));
            prev = d;
        }
        if (raw.Count == 0) return raw;
        if (prev < 1 - 1e-9) raw.Add((prev, 1.0, -1));   // 计划期内没做完的尾巴：帧 −1

        // 并片：先并掉过小的，再并到片数上限
        int maxSlices = Math.Max(1, opt.MaxSlicesPerUnit);
        double minFrac = Math.Max(0, opt.MinSliceFraction);

        bool changed = true;
        while (changed && raw.Count > 1)
        {
            changed = false;
            for (int i = 0; i < raw.Count; i++)
            {
                if (raw[i].T1 - raw[i].T0 >= minFrac) continue;
                mergeErr = Math.Max(mergeErr, MergeAt(raw, i));
                merged++;
                changed = true;
                break;
            }
        }
        while (raw.Count > maxSlices)
        {
            // 并掉最小的那一片
            int idx = 0;
            for (int i = 1; i < raw.Count; i++)
                if (raw[i].T1 - raw[i].T0 < raw[idx].T1 - raw[idx].T0) idx = i;
            mergeErr = Math.Max(mergeErr, MergeAt(raw, idx));
            merged++;
        }
        return raw;
    }

    /// <summary>
    /// 把第 i 片并进相邻片。<b>并进的是「更晚做完」的那一片</b>（保守：宁可晚消失/晚出现，
    /// 也不要提前把还没采的料抹掉）。帧 −1（没做完）视为最晚。
    /// <para>返回**这一次并片引入的显隐误差**（份额）= 被推迟的那一片的份额：
    /// 它原本该在自己那一帧显隐，现在跟着更晚的那一片走。</para>
    /// </summary>
    private static double MergeAt(List<(double T0, double T1, int Frame)> raw, int i)
    {
        if (raw.Count <= 1) return 0;
        int j;
        if (i == 0) j = 1;
        else if (i == raw.Count - 1) j = raw.Count - 2;
        else j = LaterFrame(raw[i - 1].Frame, raw[i + 1].Frame) == raw[i - 1].Frame ? i - 1 : i + 1;

        int lo = Math.Min(i, j), hi = Math.Max(i, j);
        int frame = LaterFrame(raw[lo].Frame, raw[hi].Frame);
        // 被推迟的是「本来更早做完」的那一片
        double err = raw[lo].Frame == frame ? raw[hi].T1 - raw[hi].T0 : raw[lo].T1 - raw[lo].T0;
        raw[lo] = (raw[lo].T0, raw[hi].T1, frame);
        raw.RemoveAt(hi);
        return err;
    }

    /// <summary>两个帧序里「更晚」的那个（−1 = 没做完，永远最晚）。</summary>
    private static int LaterFrame(int a, int b)
    {
        if (a < 0 || b < 0) return -1;
        return Math.Max(a, b);
    }

    // ═════════════════════════ 几何 ═════════════════════════

    /// <summary>建一片体的委托：给完成度区间，出顶点/三角。</summary>
    private delegate bool BuildSlice(double t0, double t1, out double[] xyz, out uint[] tris, out int folded, out string why);

    private readonly struct Geo
    {
        public readonly bool Ok;
        public readonly UnitSolidGeometrySource Source;
        public readonly string Note;
        public readonly BuildSlice Build;
        public Geo(bool ok, UnitSolidGeometrySource src, string note, BuildSlice build)
        { Ok = ok; Source = src; Note = note; Build = build; }
    }

    /// <summary>真轨优先，退盒子；两种都说得出理由。</summary>
    private static Geo ResolveGeometry(MiningUnitLedger.Row row, RailIndex rails, UnitSolidStageOptions opt)
    {
        if (opt.UseRealRails)
        {
            var rail = rails.Find(row);
            if (rail != null)
            {
                var r = rail.Value;
                var crest = r.Crest; var toe = r.Toe;
                double w = r.WidthM;
                bool towardCrest = row.Kind != LedgerKind.Dump;      // 采场 d 指坡顶，排土 d 指坡脚
                double? zTop = r.ConstTopZ, zBot = r.ConstBotZ;
                return new Geo(true, UnitSolidGeometrySource.RealRail, r.Note,
                    (double t0, double t1, out double[] xyz, out uint[] tris, out int folded, out string why)
                        => UnitPrism.FromRail(crest, toe, w, towardCrest, zTop, zBot, t0, t1, out xyz, out tris, out folded, out why));
            }
        }

        // ── 盒子（降级）：质心 + 长宽厚，轴对齐。台账里没有方位角，不猜 ──
        double len = row.LengthM, wid = row.WidthM;
        double zLo = row.ZLo, zHi = row.ZHi;
        if (!(zHi - zLo > 1e-6))
        {
            if (row.ThickM > 1e-6) { zLo = row.Cz - row.ThickM * 0.5; zHi = row.Cz + row.ThickM * 0.5; }
            else return new Geo(false, UnitSolidGeometrySource.Box,
                "既没有真轨，台账里也没有可用的厚度（ZHi−ZLo 与厚度m 都是 0）→ 建不出体，不拿缺省厚度冒充。", null!);
        }
        if (!(len > 1e-6) || !(wid > 1e-6))
            return new Geo(false, UnitSolidGeometrySource.Box,
                $"既没有真轨，台账的走向长/推进宽也不成立（{len:0.##} × {wid:0.##} m）→ 建不出体。", null!);

        double cx = row.Cx, cy = row.Cy;
        double? az = row.AzimuthDeg;
        string note = az.HasValue
            ? $"退**盒子**（按走向 {az.Value:0.#}° 摆正）：{len:0.#}×{wid:0.#}×{zHi - zLo:0.#} m，质心 ({cx:0.#}, {cy:0.#})。"
              + "会话内没有这个单元的真轨（真轨不持久化），所以是长方体近似 —— "
              + "**朝向是真的、轮廓不是**：真实采掘带沿走向可能是弯的，这里拉直了。"
            : $"退**轴对齐盒子**：{len:0.#}×{wid:0.#}×{zHi - zLo:0.#} m，质心 ({cx:0.#}, {cy:0.#})。"
              + "会话内没有这个单元的真轨，台账这一行也**没有走向方位角**（老格式台账没有这一列）—— "
              + "所以盒子长轴一律朝东，**朝向不代表采掘带走向**。重新从模型取一次台账就会带上方位角。";
        return new Geo(true, UnitSolidGeometrySource.Box, note,
            (double t0, double t1, out double[] xyz, out uint[] tris, out int folded, out string why)
                => UnitPrism.FromBox(cx, cy, len, wid, zLo, zHi, t0, t1, out xyz, out tris, out folded, out why, az));
    }

    // ═════════════════════════ 会话内真轨索引 ═════════════════════════

    /// <summary>
    /// 一条真轨（已按 UnitId 对上号）。
    /// <para><b>internal 而不是 private</b>：「作业区划分」的自动生成要按同一条真轨算占地
    /// （<see cref="Zoning.ZonePlanSource"/>）。另写一套真轨查找的后果是"对不上号 → 全退盒子"，
    /// 而且不报错 —— <see cref="UnitRailFile"/> 的文件头把这笔账记过一次了。</para>
    /// </summary>
    internal readonly struct Rail
    {
        public readonly double[] Crest, Toe;
        public readonly double WidthM;
        /// <summary>常数顶/底高程（排土位置整级不变，D4）；null = 逐点取轨自己的 Z。</summary>
        public readonly double? ConstTopZ, ConstBotZ;
        public readonly string Note;
        public Rail(double[] crest, double[] toe, double w, double? topZ, double? botZ, string note)
        { Crest = crest; Toe = toe; WidthM = w; ConstTopZ = topZ; ConstBotZ = botZ; Note = note; }
    }

    /// <summary>
    /// 会话内真轨索引。<b>快照一次</b>：MiningModelStore / DumpStripStore 是会话内交接点，
    /// 建体过程中不该被换掉。取不到就是取不到，不去别处找一份代替。
    /// </summary>
    internal sealed class RailIndex
    {
        private readonly Dictionary<string, Rail> _map = new(StringComparer.Ordinal);
        public string Caption { get; private set; } = "";

        public static RailIndex Empty() => new() { Caption = "真轨：**未启用**（UseRealRails=false），全部走盒子。" };

        /// <param name="ledgerRoot">
        /// 台账根目录（落盘真轨与台账同目录）。null = 默认目录。
        /// <b>调用方读的是哪个目录的台账，就要把哪个目录传进来</b> —— 台账从 A 读、真轨从 B 找，
        /// 结果是"一个单元都对不上号 → 全退盒子"，而且不报错。
        /// </param>
        public static RailIndex Snapshot(string? ledgerRoot = null)
        {
            var idx = new RailIndex();
            int coal = 0, rock = 0, dump = 0;
            var notes = new List<string>();

            try
            {
                var c = MiningModelStore.Coal;
                if (c != null) coal = idx.AddStrips(c.Strips, $"采矿模型·煤（{c.StampedAt:MM-dd HH:mm}）");
                var r = MiningModelStore.Rock;
                if (r != null) rock = idx.AddStrips(r.Strips, $"采矿模型·岩（{r.StampedAt:MM-dd HH:mm}）");
                if (MiningModelStore.LastAttemptFailed) notes.Add("◆ 采矿模型最近一次生成没出采掘带，真轨是**更早**那一次的");
            }
            catch (Exception ex) { notes.Add($"◆ 读采矿模型真轨异常（{ex.GetType().Name}）"); }

            try
            {
                var last = DumpStripStore.Last;
                if (last != null) dump = idx.AddCells(last.Cells, $"排土条带（{DumpStripStore.StampedAt:MM-dd HH:mm}）");
                if (DumpStripStore.LastAttemptFailed) notes.Add("◆ 排土条带最近一次生成没出位置，真轨是**更早**那一次的");
            }
            catch (Exception ex) { notes.Add($"◆ 读排土条带真轨异常（{ex.GetType().Name}）"); }

            // ── 落盘的真轨伴生文件：【逐 UnitId 补空缺】，不是"会话内一条都没有才读"──────
            //
            //   ★ 原来的闸是 `if (coal + rock + dump == 0)`。可这两个交接件是**各自独立**的：
            //     开一次软件只跑了「排土条带」，dump>0 ⇒ 这道闸关上 ⇒ 盘上明明有煤/岩的真轨，
            //     那 933 个单元照样全退盒子，**而且不报错**（屏幕上只是"形状不对"）。
            //     "会话内有一条"证明不了"会话内那一类也有"——【缺失不能证明不存在】。
            //
            //   会话内的优先：它是刚生成的，盘上那份可能是上一次的。
            //   补进来的一律带**自己的生成时刻**（v2 逐行时刻），Caption 里报最早的那个 ——
            //   补齐与"新鲜"是两回事，混着说等于宣称陈几何是新的。
            int fromDisk = 0;
            DateTime diskAt = default, oldestDisk = default;
            try
            {
                string path = UnitRailFile.PathIn(ledgerRoot ?? new MonthlyUnitLedgerStore().Root);
                if (UnitRailFile.TryRead(path, out var disk, out diskAt, out string dnote))
                {
                    foreach (var kv in disk)
                    {
                        if (idx._map.ContainsKey(kv.Key)) continue;      // 会话内已有 ⇒ 用刚生成的那条
                        DateTime at = kv.Value.StampedAt ?? diskAt;
                        idx._map[kv.Key] = new Rail(kv.Value.Crest, kv.Value.Toe, kv.Value.WidthM,
                                                    kv.Value.CrestZ, kv.Value.ToeZ,
                                                    $"真轨来自**落盘文件**（{at:MM-dd HH:mm} 生成）：前脸轨 {kv.Value.Crest.Length / 3} 点。");
                        fromDisk++;
                        if (at != default && (oldestDisk == default || at < oldestDisk)) oldestDisk = at;
                    }
                    if (fromDisk > 0) notes.Add(dnote);
                }
                else if (dnote.Length > 0 && !dnote.StartsWith("没有真轨文件", StringComparison.Ordinal))
                    notes.Add("◆ " + dnote);      // 读失败要说；"文件不存在"是常态，不刷屏
            }
            catch (Exception ex) { notes.Add($"◆ 读落盘真轨异常（{ex.GetType().Name}）"); }

            // 两个来源【可以同时有】（会话内只跑了一类，另一类从盘上补）——
            // 所以口径是"各来了多少条"，不是"来自哪一个"。
            string tail = notes.Count > 0 ? "　" + string.Join("　", notes) : "";
            int inSession = coal + rock + dump;
            idx.Caption = inSession + fromDisk == 0
                ? "真轨：会话内**一条都没有**、也没有落盘文件 → 全部退盒子。"
                  + "（跑一次「采矿模型」/「排土条带」，采掘单元清单会把真轨落到台账目录）" + tail
                : inSession == 0
                ? $"真轨：**{fromDisk} 条全部来自落盘文件**（{oldestDisk:MM-dd HH:mm} 起生成，"
                  + "可能已过期 —— 模型重跑过就重新取一次）。" + tail
                : fromDisk == 0
                ? $"真轨：煤 {coal} 条 · 岩 {rock} 条 · 排土 {dump} 个（会话内交接）。" + tail
                : $"真轨：会话内 {inSession} 条（煤 {coal} · 岩 {rock} · 排土 {dump}）"
                  + $"　+ **落盘文件补 {fromDisk} 条**（{oldestDisk:MM-dd HH:mm} 起生成）。"
                  + "会话内只跑过一类生成器时，另一类就是这么补上的；补进来的那批模型重跑过就不作数了。" + tail;
            return idx;
        }

        /// <summary>
        /// 采掘带 → UnitId。<b>键必须和 <see cref="MiningUnitLedger.FromStrips"/> 造的一模一样</b>，
        /// 那边是 <c>层-B带号-P幅号</c>；两处各造一套键就永远对不上号，而且不报错。
        /// </summary>
        private int AddStrips(IReadOnlyList<MiningModelPlanner.Strip>? strips, string src)
        {
            if (strips == null) return 0;
            int n = 0;
            foreach (var s in strips)
            {
                if (s == null || s.CrestXyz.Length < 6 || s.ToeXyz.Length < 6) continue;
                double w = s.AdvanceWidthM > 1e-6 ? s.AdvanceWidthM : 0;
                if (w <= 1e-6) continue;               // 推进宽度不知道就不用真轨（不拿 40 冒充）
                string id = MiningUnitLedger.StripUnitId(s.SeamCode, s.BandId, s.PanelIndex);
                _map[id] = new Rail(s.CrestXyz, s.ToeXyz, w, null, null,
                    $"真轨来自 {src}：前脸轨 {s.CrestXyz.Length / 3} 点，推进宽 {w:0.#} m，顶/底 Z 逐点取坡顶/坡底线。");
                n++;
            }
            return n;
        }

        /// <summary>排土位置 → UnitId（<see cref="DumpStripPlanner.Cell.Code"/>，与 FromDumpCells 同键）。</summary>
        private int AddCells(IReadOnlyList<DumpStripPlanner.Cell>? cells, string src)
        {
            if (cells == null) return 0;
            int n = 0;
            foreach (var c in cells)
            {
                if (c == null || c.CrestXyz.Length < 6 || c.ToeXyz.Length < 6) continue;
                if (!(c.StripWidthM > 1e-6) || !(c.CrestZ - c.ToeZ > 1e-6)) continue;
                string id = string.IsNullOrWhiteSpace(c.Code)
                    ? $"排土场-L{c.LevelIndex}-P{c.PanelIndex:00}-S{c.StepIndex:00}" + (c.SubIndex > 0 ? $"-{c.SubIndex}" : "")
                    : c.Code;
                _map[id] = new Rail(c.CrestXyz, c.ToeXyz, c.StripWidthM, c.CrestZ, c.ToeZ,
                    $"真轨来自 {src}：前脸轨 {c.CrestXyz.Length / 3} 点，条带宽 {c.StripWidthM:0.#} m，"
                    + $"顶/底 Z = 坡顶 {c.CrestZ:0.##} / 坡底 {c.ToeZ:0.##} m（整级不变）。"
                    + (c.IsClamped ? "　◆ 本带被凹弯夹窄过，宽度不是名义 W。" : ""));
                n++;
            }
            return n;
        }

        public Rail? Find(MiningUnitLedger.Row row)
            => _map.TryGetValue(row.UnitId ?? "", out var r) ? r : null;
    }

    // ═════════════════════════ 样式源 ═════════════════════════

    /// <summary>
    /// 取样式源；取不到就<b>降级为 0 并照建</b>，只往结果里留一条说明。
    ///
    /// <para><b>为什么这不是"没样式源就建不了"</b>（这道闸是 C# 侧自己加的，内核从来没要求过）：
    /// <c>PitMine_BuildColoredMeshOnLayer</c>（xllAcEd.cpp:8860-8868）对 <c>srcHandle</c> 解不出实体时
    /// 已经有退路 —— <c>basePoint</c> 退回**本网第一个顶点**，<c>baseColor</c> 退回白色。而且：</para>
    /// <list type="number">
    ///   <item><b>颜色根本用不到</b>：单元体是**逐顶点上色**的（<see cref="UnitSolidPalette"/>），
    ///         内核只在顶点色是哨兵 <c>0xFFFFFFFF</c> 时才回落到 baseColor，本包一个哨兵都不发。</item>
    ///   <item><b>精度不降反升</b>：basePoint 的作用是让顶点能用 float 存（CGCS2000 的 4e6 量级
    ///         直接塞 float 会丢到分米）。用**本网自己的**第一个顶点当 base，偏移量比借别人的 base 更小。</item>
    /// </list>
    ///
    /// <para>踩过的坑（2026-08-10）：空文档里点「建单元体」→「文档中没有任何实体，无三角网可作样式源」
    /// → 一个体都不建。而那 97 行台账、几何全齐，内核完全建得出来。
    /// **一道自己加的闸，把一条本来通的路堵死了**，且报的原因指向"去导入一张现状面"——
    /// 一个与真实障碍无关的方向。</para>
    /// </summary>
    private ulong ResolveSourceOrDegrade(UnitSolidBuildResult res)
    {
        if (TryResolveSource(out ulong src, out string why)) return src;
        res.Messages.Add($"· 没有样式源（{why}）→ 按**自身第一个顶点**定 basePoint 照常建体。"
                       + "颜色本来就逐顶点给（不取样式源的实体色），所以形态与配色都不受影响。");
        return 0;
    }

    /// <summary>
    /// 找一张现有三角网当样式源（BuildColoredMeshOnLayer 从它取 basePoint/实体色/透明度）。
    /// <b>找不到不是错</b> —— 调用方走 <see cref="ResolveSourceOrDegrade"/>。
    /// <b>排除本舞台自己的两个图层</b>，否则清完 handle 立刻悬空。
    /// </summary>
    private bool TryResolveSource(out ulong src, out string why)
    {
        src = 0; why = "";
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
        foreach (var lay in new[] { LayerUnitPit, LayerUnitDump })
        {
            try { var arr = _ent.GetHandlesByLayer(lay); if (arr != null) foreach (var h in arr) mine.Add(h); }
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
            + " —— 先导入或生成一张现状面";
        return false;
    }

    // ═════════════════════════ 着色 / 小件 ═════════════════════════

    /// <summary>
    /// 逐顶点烤死颜色。<b>建完就改不了</b> —— IEntityCapability 只导出 SetEntitiesVisible，
    /// 没有改色/改透明度的口子，这是已知取舍，不动 Platform 接口。
    /// 盒子（降级）体整体调灰，图上一眼能看出「这一批是近似形态」。
    /// </summary>
    private static uint[] Colorize(double[] xyz, LedgerKind kind, UnitSolidGeometrySource src)
        => ColorizeWith(xyz, BaseRgbOfKind(kind), src, greyBoxes: true);

    /// <summary>
    /// 类别基色 —— <b>与 <see cref="UnitColorScheme"/> 的类别维度是同一批常量</b>。
    /// 两边各写一套是"预览好看、入图不对"的原型（本仓已经发生过一次：排土差的是色相不是深浅）。
    /// </summary>
    internal static uint BaseRgbOfKind(LedgerKind kind) => kind switch
    {
        LedgerKind.Coal => RgbCoal,
        LedgerKind.Rock => RgbRock,
        _ => RgbDump,
    };

    /// <summary>
    /// 给定基色，铺成逐顶点色（下半压暗做体积感）。
    /// <param name="greyBoxes">盒子是否整体调灰。<b>只有类别维度该开</b> ——
    /// 别的分色维度也调灰的话，会话内没真轨时 2312 个单元全是盒子，所有色会被一起拉到灰附近。</param>
    /// </summary>
    private static uint[] ColorizeWith(double[] xyz, uint baseRgb, UnitSolidGeometrySource src, bool greyBoxes)
    {
        uint b = baseRgb;
        if (greyBoxes && src == UnitSolidGeometrySource.Box) b = Mix(b, RgbBoxGrey, BoxGreyMix);

        int nv = xyz.Length / 3;
        if (nv == 0) return Array.Empty<uint>();
        double zMin = MinZ(xyz), zMax = MaxZ(xyz);
        double span = zMax - zMin;
        var col = new uint[nv];
        uint dark = Shade(b, BottomShade);
        for (int i = 0; i < nv; i++)
            col[i] = span > 1e-6 && xyz[i * 3 + 2] < zMin + span * 0.5 ? dark : b;
        return col;
    }

    private static uint Mix(uint a, uint b, double k)
    {
        int r = (int)Math.Round(((a >> 16) & 0xFF) * (1 - k) + ((b >> 16) & 0xFF) * k);
        int g = (int)Math.Round(((a >> 8) & 0xFF) * (1 - k) + ((b >> 8) & 0xFF) * k);
        int c = (int)Math.Round((a & 0xFF) * (1 - k) + (b & 0xFF) * k);
        return (uint)((Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(c, 0, 255));
    }

    private static uint Shade(uint rgb, double k)
    {
        int r = Math.Clamp((int)Math.Round(((rgb >> 16) & 0xFF) * k), 0, 255);
        int g = Math.Clamp((int)Math.Round(((rgb >> 8) & 0xFF) * k), 0, 255);
        int b = Math.Clamp((int)Math.Round((rgb & 0xFF) * k), 0, 255);
        return (uint)((r << 16) | (g << 8) | b);
    }

    private static double MinZ(double[] xyz)
    {
        double v = double.MaxValue;
        for (int i = 2; i < xyz.Length; i += 3) if (xyz[i] < v) v = xyz[i];
        return v == double.MaxValue ? 0 : v;
    }

    private static double MaxZ(double[] xyz)
    {
        double v = double.MinValue;
        for (int i = 2; i < xyz.Length; i += 3) if (xyz[i] > v) v = xyz[i];
        return v == double.MinValue ? 0 : v;
    }

    /// <summary>
    /// 把体的 XY 包围填进去。点数不足时<b>保持 NaN 不动</b> ——
    /// 让"没填过"一路传下去，别在这儿悄悄变成 0（见 <see cref="UnitSolidBody.MinX"/> 的理由）。
    /// </summary>
    private static void FillBounds(double[] xyz, UnitSolidBody body)
    {
        if (xyz == null || xyz.Length < 3) return;
        double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
        for (int i = 0; i + 2 < xyz.Length; i += 3)
        {
            double x = xyz[i], y = xyz[i + 1];
            if (x < x0) x0 = x; if (x > x1) x1 = x;
            if (y < y0) y0 = y; if (y > y1) y1 = y;
        }
        if (x0 > x1) return;                       // 一个点都没扫到，仍然留 NaN
        body.MinX = x0; body.MaxX = x1; body.MinY = y0; body.MaxY = y1;
    }

    /// <summary>散度定理算封闭三角网的体积（只对水密且法向一致的壳成立 —— 本类的壳按构造闭合）。</summary>
    internal static double MeshVolume(double[] xyz, uint[] tris)
    {
        double v6 = 0;
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            int a = (int)tris[t] * 3, b = (int)tris[t + 1] * 3, c = (int)tris[t + 2] * 3;
            v6 += xyz[a] * (xyz[b + 1] * xyz[c + 2] - xyz[b + 2] * xyz[c + 1])
                - xyz[a + 1] * (xyz[b] * xyz[c + 2] - xyz[b + 2] * xyz[c])
                + xyz[a + 2] * (xyz[b] * xyz[c + 1] - xyz[b + 1] * xyz[c]);
        }
        return Math.Abs(v6) / 6.0;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  单元体的三角化 —— 纯几何，无宿主依赖，可单测
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 把「前脸轨 + 推进宽 + 顶底高程」变成一块封闭的多边形棱柱。
/// <para>造法与内核 <c>StripPrism.hpp</c> 一致：<b>平面轮廓竖向拉伸</b>
/// （平面轮廓 = 前脸轨 + 沿推进方向偏 W 的后界轨），不是四条轨逐点 loft 的斜前脸。</para>
/// <para>壳的构成：顶盖 + 底盖 + 前壁 + 后壁 + 两端封头，共 8(n−1)+4 个三角，按构造闭合；
/// 绕向由「散度积分为正」统一定死，与输入轨的绕向无关。</para>
/// </summary>
internal static class UnitPrism
{
    /// <summary>每片至少要有几个站位（少于它三角化不成立）。</summary>
    private const int MinStations = 2;

    /// <summary>
    /// <b>平面占地</b>：前脸轨 + 沿推进方向偏 W 的后界轨。体的竖向拉伸就是从这一对轨起的。
    ///
    /// <para><b>为什么单拿出来</b>：「作业区划分」的自动生成要的正是这块地的平面轮廓
    /// （<see cref="Zoning.ZoneAutoPlanner"/>）。占地在两处各写一遍的话，图上的体与圈出来的区
    /// 会慢慢分家 —— 而两边各自都"看着对"。所以三维建体与平面圈区**共用这一个函数**。</para>
    /// </summary>
    /// <param name="front2">前脸的平面点 [x,y,...]（= 切出来那一段的坡顶线）。</param>
    /// <param name="rear2">后界的平面点 [x,y,...]，与 front2 一一对应（同一站位）。</param>
    /// <param name="seg">切出来的那段坡顶线（扁平 xyz）—— 顶 Z 与 Z 拟合都要用它。</param>
    /// <param name="toeAt">坡底线投影到各站位的结果（扁平 xyz）。</param>
    public static bool PlanFootprint(double[] crest, double[] toe, double widthM, bool advanceTowardCrest,
                                     double t0, double t1,
                                     out double[] front2, out double[] rear2, out double[] seg, out double[] toeAt,
                                     out int folded, out string why)
    {
        front2 = Array.Empty<double>(); rear2 = Array.Empty<double>();
        seg = Array.Empty<double>(); toeAt = Array.Empty<double>();
        folded = 0; why = "";

        if (crest == null || crest.Length < 6) { why = "前脸轨不足 2 点。"; return false; }
        if (toe == null || toe.Length < 6) { why = "坡底线不足 2 点。"; return false; }
        if (!(widthM > 1e-6)) { why = $"推进宽不成立（{widthM:0.###} m）。"; return false; }

        seg = SliceByArc(crest, t0, t1);
        int n = seg.Length / 3;
        if (n < MinStations) { why = $"按走向切 [{t0:0.###},{t1:0.###}] 后只剩 {n} 个点，围不成面。"; return false; }

        // 坡底线按「投影到最近点」对应到本片的每个站位 —— 不用弧长比例对应
        // （两条轨长度/点数都不同，弧长对应在拐角处会飘开，这一点 DumpStripPlanner.ProjectOnto 的注释里写过）。
        try { toeAt = DumpStripPlanner.ProjectOnto(seg, toe); }
        catch (Exception ex) { why = $"坡底线投影失败（{ex.GetType().Name}）。"; return false; }
        if (toeAt.Length / 3 != n) { why = "坡底线投影结果点数对不上。"; return false; }

        // 推进方向：AdvanceDirs 返回指坡脚那一版；采场取反（内核 CarveStrip.cpp:233 口径）
        (double X, double Y)[] dirs;
        try { dirs = DumpStripPlanner.AdvanceDirs(seg, toeAt, Math.Max(widthM, 2.0)); }
        catch (Exception ex) { why = $"推进方向求解失败（{ex.GetType().Name}）。"; return false; }
        if (advanceTowardCrest)
            for (int i = 0; i < dirs.Length; i++) dirs[i] = (-dirs[i].X, -dirs[i].Y);

        // 后界轨：等距偏移（拐角走角平分线/圆弧，不是逐点法向平移）。
        // 圆角会插点，所以必须用 vertexAt 把「原轨第 i 点」映回返回轨的下标 —— 自己拿下标去减是错的。
        var vertexAt = new int[n];
        double[] back;
        try { back = DumpStripPlanner.OffsetRail(seg, dirs, widthM, out folded, null, vertexAt); }
        catch (Exception ex) { why = $"后界轨偏移失败（{ex.GetType().Name}）。"; return false; }
        int bn = back.Length / 3;
        if (bn < 2) { why = "后界轨退化。"; return false; }

        front2 = new double[n * 2];
        rear2 = new double[n * 2];
        for (int i = 0; i < n; i++)
        {
            front2[i * 2] = seg[i * 3]; front2[i * 2 + 1] = seg[i * 3 + 1];
            int bi = Math.Clamp(vertexAt[i], 0, bn - 1);
            rear2[i * 2] = back[bi * 3]; rear2[i * 2 + 1] = back[bi * 3 + 1];
        }
        return true;
    }

    /// <summary>
    /// 前脸 + 后界 → 一条闭合平面环（前脸正序 + 后界倒序）。<b>可能自交</b>：
    /// 凹弯处后界轨会折回（<c>folded &gt; 0</c>），环在那儿会打个结。
    /// 用它做栅格并集是安全的（偶奇填充），拿它直接当区域边界则不是 —— 调用方要看 folded。
    /// </summary>
    public static double[] PlanRing(double[] front2, double[] rear2)
    {
        int n = Math.Min(front2.Length, rear2.Length) / 2;
        if (n < 2) return Array.Empty<double>();
        var ring = new double[n * 4];
        for (int i = 0; i < n; i++)
        {
            ring[i * 2] = front2[i * 2];
            ring[i * 2 + 1] = front2[i * 2 + 1];
        }
        for (int i = 0; i < n; i++)
        {
            int j = n - 1 - i;
            ring[(n + i) * 2] = rear2[j * 2];
            ring[(n + i) * 2 + 1] = rear2[j * 2 + 1];
        }
        return ring;
    }

    /// <summary>
    /// 真轨 → 一片体。<paramref name="t0"/>/<paramref name="t1"/> 是沿走向（平面弧长）的归一化区间。
    /// </summary>
    /// <param name="crest">前脸轨（坡顶线）扁平 xyz。</param>
    /// <param name="toe">坡底线扁平 xyz —— 只用于两件事：定推进方向正负号、给底 Z。</param>
    /// <param name="widthM">推进宽 W（m）。</param>
    /// <param name="advanceTowardCrest">
    /// true = 推进方向指坡顶/高墙（采场），false = 指坡脚/外（排土）。
    /// 与内核 CarveStrip 的 isDump 口径一致；<see cref="DumpStripPlanner.AdvanceDirs"/> 返回的是指坡脚那一版。
    /// </param>
    /// <param name="constTopZ">常数顶高程（排土整级不变）；null = 逐点取坡顶线自己的 Z。</param>
    /// <param name="constBotZ">常数底高程；null = 逐点取坡底线在该处的 Z。</param>
    /// <param name="folded">后界轨折回的段数（&gt;0 = 凹弯处平面轮廓可能自交）。</param>
    public static bool FromRail(double[] crest, double[] toe, double widthM, bool advanceTowardCrest,
                                double? constTopZ, double? constBotZ,
                                double t0, double t1,
                                out double[] xyz, out uint[] tris, out int folded, out string why)
    {
        xyz = Array.Empty<double>(); tris = Array.Empty<uint>();

        if (!PlanFootprint(crest, toe, widthM, advanceTowardCrest, t0, t1,
                           out double[] front, out double[] rear, out double[] seg, out double[] toeAt,
                           out folded, out why))
            return false;

        int n = front.Length / 2;
        var top = new double[n];
        var bot = new double[n];
        for (int i = 0; i < n; i++)
        {
            double zt = constTopZ ?? seg[i * 3 + 2];
            double zb = constBotZ ?? toeAt[i * 3 + 2];
            if (double.IsNaN(zt) || double.IsNaN(zb)) { why = $"第 {i} 站的顶/底高程是 NaN。"; return false; }
            if (zt < zb) (zt, zb) = (zb, zt);
            top[i] = zt; bot[i] = zb;
        }

        double h = 0;
        for (int i = 0; i < n; i++) h += top[i] - bot[i];
        h /= n;
        if (!(h > 1e-6)) { why = $"顶底高差平均只有 {h:0.####} m，建不出体（不拿缺省台阶高冒充）。"; return false; }

        return Assemble(front, rear, top, bot, out xyz, out tris, out why);
    }

    /// <summary>
    /// 质心 + 长宽厚 → 一片**轴对齐**盒子。走向沿 +X、推进宽沿 +Y。
    /// <para><b>方位角是编不出来的</b>：台账 Row 里没有它，所以这里只能轴对齐 —— 调用方必须在界面上
    /// 标明「形态为近似」。</para>
    /// </summary>
    /// <param name="azimuthDeg">
    /// 走向方位角（度，从北起顺时针）。<b>null = 不知道 ⇒ 轴对齐</b>（长轴朝东）。
    /// <para>0 不是"不知道"：0° 是正南北走向。传 0 会让盒子一本正经地朝一个具体方向。</para>
    /// </param>
    public static bool FromBox(double cx, double cy, double lengthM, double widthM, double zLo, double zHi,
                               double t0, double t1,
                               out double[] xyz, out uint[] tris, out int folded, out string why,
                               double? azimuthDeg = null)
    {
        xyz = Array.Empty<double>(); tris = Array.Empty<uint>(); folded = 0; why = "";
        if (!(zHi - zLo > 1e-6)) { why = $"厚度不成立（{zHi - zLo:0.###} m）。"; return false; }
        if (!BoxPlanFootprint(cx, cy, lengthM, widthM, t0, t1, azimuthDeg,
                              out double[] front, out double[] rear, out why)) return false;

        var top = new[] { zHi, zHi };
        var bot = new[] { zLo, zLo };
        return Assemble(front, rear, top, bot, out xyz, out tris, out why);
    }

    /// <summary>
    /// 盒子的<b>平面占地</b>（质心 + 长宽 + 走向方位角 → 前脸/后界两条边）。
    /// 与 <see cref="PlanFootprint"/> 对称：三维盒子与平面圈区共用它，不各摆一遍角度。
    /// </summary>
    public static bool BoxPlanFootprint(double cx, double cy, double lengthM, double widthM,
                                        double t0, double t1, double? azimuthDeg,
                                        out double[] front2, out double[] rear2, out string why)
    {
        front2 = Array.Empty<double>(); rear2 = Array.Empty<double>(); why = "";
        if (!(lengthM > 1e-6) || !(widthM > 1e-6)) { why = $"长宽不成立（{lengthM:0.##} × {widthM:0.##} m）。"; return false; }

        double a = Math.Clamp(Math.Min(t0, t1), 0, 1), b = Math.Clamp(Math.Max(t0, t1), 0, 1);
        if (b - a < 1e-9) { why = $"分片区间为空（{t0:0.###}~{t1:0.###}）。"; return false; }

        // 局部坐标：u 沿走向（长 = lengthM）、v 沿推进（宽 = widthM），原点在质心
        double u0 = -lengthM * 0.5 + lengthM * a;
        double u1 = -lengthM * 0.5 + lengthM * b;
        double v0 = -widthM * 0.5, v1 = widthM * 0.5;

        // 走向单位向量。X=东、Y=北，方位角自北顺时针 ⇒ (sin, cos)。
        // 没有方位角时退回 (1,0)（长轴朝东）—— 与加这一列之前的行为一模一样。
        double ex = 1, ey = 0;
        if (azimuthDeg.HasValue)
        {
            double rad = azimuthDeg.Value * Math.PI / 180.0;
            ex = Math.Sin(rad); ey = Math.Cos(rad);
        }
        // 推进方向 = 走向的左垂线
        double nx = -ey, ny = ex;

        double PX(double u, double v) => cx + u * ex + v * nx;
        double PY(double u, double v) => cy + u * ey + v * ny;

        front2 = new[] { PX(u0, v0), PY(u0, v0), PX(u1, v0), PY(u1, v0) };
        rear2 = new[] { PX(u0, v1), PY(u0, v1), PX(u1, v1), PY(u1, v1) };
        return true;
    }

    /// <summary>
    /// 四条角轨（前平面点 / 后平面点 / 顶 Z / 底 Z）→ 封闭壳。
    /// 顶点布局：站位 i 的 4 个点 = [前顶, 后顶, 后底, 前底]，下标 4i+0..3。
    /// </summary>
    private static bool Assemble(double[] front2, double[] rear2, double[] topZ, double[] botZ,
                                 out double[] xyz, out uint[] tris, out string why)
    {
        xyz = Array.Empty<double>(); tris = Array.Empty<uint>(); why = "";
        int n = topZ.Length;
        if (n < MinStations || rear2.Length / 2 != n || front2.Length / 2 != n || botZ.Length != n)
        { why = "角轨点数对不上。"; return false; }

        var v = new double[n * 4 * 3];
        for (int i = 0; i < n; i++)
        {
            double fx = front2[i * 2], fy = front2[i * 2 + 1];
            double rx = rear2[i * 2], ry = rear2[i * 2 + 1];
            int o = i * 12;
            v[o + 0] = fx; v[o + 1] = fy; v[o + 2] = topZ[i];      // 4i+0 前顶
            v[o + 3] = rx; v[o + 4] = ry; v[o + 5] = topZ[i];      // 4i+1 后顶
            v[o + 6] = rx; v[o + 7] = ry; v[o + 8] = botZ[i];      // 4i+2 后底
            v[o + 9] = fx; v[o + 10] = fy; v[o + 11] = botZ[i];    // 4i+3 前底
        }

        var t = new List<uint>((n - 1) * 8 * 3 + 12);
        void Q(int a, int b, int c, int d)
        { t.Add((uint)a); t.Add((uint)b); t.Add((uint)c); t.Add((uint)a); t.Add((uint)c); t.Add((uint)d); }

        for (int i = 0; i + 1 < n; i++)
        {
            int A = i * 4, B = (i + 1) * 4;
            Q(A + 0, A + 1, B + 1, B + 0);   // 顶盖
            Q(A + 3, B + 3, B + 2, A + 2);   // 底盖
            Q(A + 0, B + 0, B + 3, A + 3);   // 前壁
            Q(A + 1, A + 2, B + 2, B + 1);   // 后壁
        }
        // 两端封头
        Q(0, 3, 2, 1);
        int L = (n - 1) * 4;
        Q(L + 0, L + 1, L + 2, L + 3);

        var arr = t.ToArray();

        // 绕向统一：散度积分为负就整体翻面。输入轨的绕向是画图的人随手定的，不该决定法向。
        double v6 = 0;
        for (int k = 0; k + 2 < arr.Length; k += 3)
        {
            int a = (int)arr[k] * 3, b = (int)arr[k + 1] * 3, c = (int)arr[k + 2] * 3;
            v6 += v[a] * (v[b + 1] * v[c + 2] - v[b + 2] * v[c + 1])
                - v[a + 1] * (v[b] * v[c + 2] - v[b + 2] * v[c])
                + v[a + 2] * (v[b] * v[c + 1] - v[b + 1] * v[c]);
        }
        if (v6 < 0)
            for (int k = 0; k + 2 < arr.Length; k += 3) (arr[k + 1], arr[k + 2]) = (arr[k + 2], arr[k + 1]);

        if (Math.Abs(v6) / 6.0 <= 1e-6) { why = "体积为 0（平面轮廓退化成线，或顶底重合）。"; return false; }

        xyz = v; tris = arr;
        return true;
    }

    /// <summary>
    /// 按**平面弧长**取轨的 [t0,t1] 段（两端插值，中间原样保留顶点）。
    /// 原样保留顶点是必须的：等间距重采会跳过折点，弯带上把面积削掉。
    /// </summary>
    internal static double[] SliceByArc(double[] xyz, double t0, double t1)
    {
        int n = xyz.Length / 3;
        if (n < 2) return Array.Empty<double>();
        double a = Math.Clamp(Math.Min(t0, t1), 0, 1), b = Math.Clamp(Math.Max(t0, t1), 0, 1);
        if (b - a < 1e-9) return Array.Empty<double>();
        if (a <= 1e-9 && b >= 1 - 1e-9) return (double[])xyz.Clone();

        var cum = new double[n];
        for (int i = 1; i < n; i++)
        {
            double dx = xyz[i * 3] - xyz[(i - 1) * 3], dy = xyz[i * 3 + 1] - xyz[(i - 1) * 3 + 1];
            cum[i] = cum[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }
        double total = cum[n - 1];
        if (!(total > 1e-9)) return Array.Empty<double>();

        double s0 = a * total, s1 = b * total;
        var raw = new List<double>((n + 2) * 3);
        AddAt(xyz, cum, s0, raw);
        for (int i = 0; i < n; i++)
            if (cum[i] > s0 + 1e-9 && cum[i] < s1 - 1e-9)
            { raw.Add(xyz[i * 3]); raw.Add(xyz[i * 3 + 1]); raw.Add(xyz[i * 3 + 2]); }
        AddAt(xyz, cum, s1, raw);

        // 去掉相邻重合点：0 长边会让 AdvanceDirs / OffsetRail 的法向退化
        var outv = new List<double>(raw.Count);
        for (int i = 0; i * 3 + 2 < raw.Count; i++)
        {
            if (outv.Count >= 3)
            {
                double dx = raw[i * 3] - outv[^3], dy = raw[i * 3 + 1] - outv[^2];
                if (dx * dx + dy * dy < 1e-12) continue;
            }
            outv.Add(raw[i * 3]); outv.Add(raw[i * 3 + 1]); outv.Add(raw[i * 3 + 2]);
        }
        return outv.Count >= 6 ? outv.ToArray() : Array.Empty<double>();
    }

    private static void AddAt(double[] xyz, double[] cum, double s, List<double> outv)
    {
        int n = cum.Length;
        int k = 0;
        while (k + 1 < n - 1 && cum[k + 1] < s) k++;
        double seg = cum[k + 1] - cum[k];
        double u = seg <= 1e-12 ? 0 : Math.Clamp((s - cum[k]) / seg, 0, 1);
        for (int c = 0; c < 3; c++)
            outv.Add(xyz[k * 3 + c] + (xyz[(k + 1) * 3 + c] - xyz[k * 3 + c]) * u);
    }
}
