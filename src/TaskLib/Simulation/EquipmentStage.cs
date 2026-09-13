// 忠实移植自原 PitMine3D Modules/TaskLib/Simulation/EquipmentStage.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;
using PitMine3D.Kylin.Platform.Capabilities;

namespace PitMine3D.Kylin.TaskLib.Simulation;

// ═════════════════════════════════════════════════════════════════════════════
//  设备多色符号 —— 把「本月哪台设备在哪个采掘单元」摆成三维符号
//
//  ── 图层纪律（这是上一轮四路并行撞墙的直接原因，不要再犯）──
//  本包**只**往 <see cref="EquipmentStage.Layer"/>（__SIM_EQUIP__）建体，
//  **只**按这一个图层名收回。SolidGeometryPort.ClearSolids 扫的是 __SIM_PIT__ / __SIM_DUMP__
//  （SimGeometryPort.cs:574-594），两边互不相干 —— 谁都别去删别人的层。
//  overlay 通道（MINEABLE_AREA）本包**一次都不碰**：设备是实体不是 overlay，
//  碰了就会把 SimOverlayComposer 的 frame/haul 一起擦掉。
//
//  ── 为什么走实体而不走 overlay ──
//  overlay 只画线环，且对 n≥3 的点串无条件加半透明填充面（SimOverlayComposer.OpenPolylineNote
//  已经把内核证据写清楚了）。设备符号是**带体积的多色实心块**，overlay 表达不了。
//  实体这条路的代价是「入 Undo 栈」，所以本包**绝不逐帧建**，见下面的移动方案取舍。
//
//  ── 移动方案的取舍（实测代码依据，不是选项罗列）──
//  能改的只有「建 / 删 / 显隐」，**改不了变换矩阵**（IEntityCapability 没有 transform）。
//  三条候选逐条核过：
//
//   A. 每帧删建
//      · BuildColoredMeshOnLayer → AcDbBatchAddEntityCommand + CommandStack.Push
//        （xllAcEd.cpp:8784-8789）＝ **每建一个实体压一条 Undo**；
//        DeleteEntities → AcDbBatchDeleteEntityCommand 同样入栈（Editor.cpp:1022-1030）。
//      · 一帧 8 个实体就是 16 条 Undo 记录；播 12 个月帧 = 192 条，Undo 栈直接报废。
//      ⇒ 否。这也正是 SolidGeometryPort 只在按钮上建体、播放路径一律不碰实体的原因。
//
//   B. 预建多位置 + SetEntitiesVisible 切显隐
//      · **接口承诺的批量是假的**：EntityCapabilityImpl.SetEntitiesVisible(:266-277) 是
//        `foreach handle → PitMine_SetEntityProperty(h,"visible",…)`，逐个 P/Invoke，
//        而且每次都开一次 AcDbTransactionGuard（xllAcEd.cpp:11252-11257）。
//        上百个符号逐个切 = 上百次带事务的 P/Invoke，顶穿 30~40ms 帧预算。
//      · **更要命的是它可能根本不生效**：PitMine_SetEntityProperty 走 "visible" 分支
//        （xllAcEd.cpp:11319）只调 ent->setVisible()，**不调 db->MarkRenderDirty()**；
//        而三角网的可见性过滤只在视口重建时读（Viewport.cpp:427
//        `needRebuild = db->IsRenderDirty() || fillChanged`，:681 才判 ent->isVisible()），
//        mesh 一旦进了 MeshGpuRenderer 的持久缓存，不重建就一直画。
//        RequestRender 只是请求画一帧，**不标脏**（xllAcEd.cpp:1102-1109）。
//      ⇒ 否（作为**逐帧**机制）。本包**不实现**这条路，也不留一个默认关的开关假装留了后路 ——
//        要它就得先解决标脏（内核加一个便宜的 MarkRenderDirty 入口，或让 setVisible 自己标）。
//
//   C. 静态摆位（推荐 ✔）
//      口径已经定死：设备按**月帧**显示「本月在这个单元」，不做日内进退场。
//      那么一个月内符号根本不需要动 —— 建一次、看一个月、换月再建一次。
//      · 每次「摆设备」= 每类一个合批实体，**≤ 8 次** BuildColoredMeshOnLayer
//        （8 = 7 类 + 其他）＝ 8 条 Undo，和「生成本期三维体」一个量级；
//      · 播放 / 拖时间轴路径上 <see cref="EquipmentStage.ApplyFrame"/> **一次 P/Invoke 都不发**，
//        只比对期次字符串、更新状态文案。帧预算占用 ≈ 0。
//      ⇒ 选它。
//
//  ⚠ 上面三条里的「每帧 16 条 Undo」「≤8 次入库」是**按代码路径数出来的调用次数**，
//    不是实测耗时。本包没有跑过计时（三维要跑起整个宿主），耗时结论请自己压测；
//    结论不依赖耗时：A 的否决理由是 Undo 栈，B 的否决理由是不标脏，都与快慢无关。
// ═════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 设备类别。<b>数值与名字都与 <c>MineAssLib.Driving.MachineKind</c> 对齐</b>，
/// 但本包**不引用它**（TaskLib 没有 MineAssLib 的项目引用，见文件尾 <see cref="EquipmentStageAdapter"/> 的说明）。
/// 映射一律**按名字**做，不按序号 —— 上游重排枚举时按名字会立刻报错，按序号会静默错位。
/// </summary>
public enum EquipKind
{
    /// <summary>电铲。</summary>
    Shovel = 0,
    /// <summary>矿用卡车。</summary>
    Truck = 1,
    /// <summary>钻机。</summary>
    Drill = 2,
    /// <summary>前装机。</summary>
    Loader = 3,
    /// <summary>推土机。</summary>
    Dozer = 4,
    /// <summary>平路机。</summary>
    Grader = 5,
    /// <summary>洒水车。</summary>
    WaterTruck = 6,
    /// <summary>其他 / 未归类。<b>必须有这一档</b>：上游 MachineKind 有 Other，
    /// 不给它符号就会静默丢掉一批设备，而台数汇总看上去完全正常。</summary>
    Other = 7,
}

/// <summary>
/// 设备的作业状态。
///
/// <para><b>这个枚举是按「上游真能给出什么」定的，不是按想画什么定的</b>：</para>
/// <list type="bullet">
///   <item><see cref="Working"/> / <see cref="Hauling"/> / <see cref="Relocating"/>
///         ← <c>MachineAssignment.Role</c> 的 Excavate / Haul / Relocate，一一对应，有数据。</item>
///   <item><see cref="Standby"/>（待命）← <b>EquipmentAssigner 不输出</b>。它是「在籍可派但本月没派到活」，
///         只有调用方**另外**把在籍清单喂进来才能算出来（<see cref="EquipmentStage.FeedIdleFleet"/>）。
///         没喂就是一台都不画，并在 Notes 里留条。</item>
///   <item><see cref="Maintenance"/>（检修）← <b>EquipmentAssigner 不输出</b>。
///         它在设备台账里（<c>Machine.Dispatchable=false</c> / 状态列），同样只能由调用方喂。</item>
/// </list>
/// <para><b>「空驶」没有这一档</b>：空驶是车次层（Trip）的概念 —— 一台车这一趟是重车还是空车。
/// 已定口径是「设备按月帧显示本月在这个单元，不做日内进退场」，月帧上根本区分不出空重车。
/// 造一个「空驶」出来就是编状态。要看空重车请去车次层（<see cref="TripAnimator"/>）。</para>
/// </summary>
public enum EquipState
{
    /// <summary>作业·挖装。</summary>
    Working = 0,
    /// <summary>作业·运输（绑在某台挖装设备上）。</summary>
    Hauling = 1,
    /// <summary>转场（占工日不出量）。</summary>
    Relocating = 2,
    /// <summary>待命（在籍可派、本月无指派）。<b>需调用方另喂在籍清单</b>。</summary>
    Standby = 3,
    /// <summary>检修（本月不可派）。<b>需调用方另喂设备状态</b>。</summary>
    Maintenance = 4,
}

/// <summary>一个符号的平面位置是**哪一级**来源。数越小越贴近真实几何。</summary>
public enum EquipPositionSource
{
    /// <summary>没定位到 —— <b>不画</b>。（不是画在原点，那是编位置。）</summary>
    None = 0,
    /// <summary>本会话的**真轨**：采矿模型的采掘带 <c>CrestXyz</c> / 排土条带的 <c>Cell.CrestXyz</c>。
    /// 位置和走向都是图上真实存在的线。</summary>
    Track = 1,
    /// <summary>台账质心 + **同带各幅质心连线**反推的走向。位置真、走向是反推的。</summary>
    PanelChain = 2,
    /// <summary>台账质心 + **轴对齐**（走向按 +X 摆）。位置真、<b>走向是缺省值</b>，形态为近似。</summary>
    AxisDefault = 3,
}

// ─────────────────────────────────────────────────────────────────────────────
//  输入契约（中性 POCO —— 不吃 MineAssLib 的类型）
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 一笔「本月这台设备在这个单元」。
/// <para>与 <c>MineAssLib.Driving.MachineAssignment</c> 一一对应，字段名保持对得上
/// （<see cref="EquipmentStageAdapter"/> 按名字搬），但**不带量**：本包只画位置，
/// 方量的账在台账和排产报告里，摆两份迟早漂。</para>
/// </summary>
public sealed class EquipPlacementRow
{
    /// <summary>设备编号 —— 身份。空串会被拒（无法去重、无法追溯）。</summary>
    public string MachineId = "";
    public EquipKind Kind;
    /// <summary>型号（只带着走，用于状态文案）。</summary>
    public string Model = "";
    /// <summary>在哪个采掘单元 / 排土位置（<c>MiningUnitLedger.Row.UnitId</c> 口径）。</summary>
    public string UnitId = "";
    public EquipState State = EquipState.Working;
    /// <summary>运输笔绑在哪台挖装设备上（<see cref="EquipState.Hauling"/> 时非空）。</summary>
    public string ServesMachineId = "";
    /// <summary>月内推进序（上游 <c>Seq</c>，原样带出，只用于同单元多台设备的稳定排布）。</summary>
    public int Seq;
    /// <summary>起 / 止工日（闭区间，1 起）。只用于状态文案，不参与几何。</summary>
    public int StartDay, EndDay;
    /// <summary>这一行是哪来的（"EquipmentAssigner 2026-08" / "在籍清单兜底" …）。<b>不许留空</b>。</summary>
    public string SourceNote = "";
}

/// <summary>
/// 单元的位置兜底（台账口径）。真轨查不到时用它，<b>只提供质心 + 尺寸，不提供走向</b>。
/// 字段与 <c>MiningUnitLedger.Row</c> 同名同义，避免两套口径。
/// </summary>
public sealed class EquipUnitSite
{
    public string UnitId = "";
    /// <summary>质心 X / Y、坡底 / 坡顶标高。</summary>
    public double Cx, Cy, ZLo, ZHi;
    /// <summary>走向长 / 推进宽（m）。用于把同单元的多台设备沿走向排开。</summary>
    public double LengthM, WidthM;
    /// <summary>同带同幅的分组键（<c>Region|Seam|Band</c>）—— 反推走向时按它找同带的邻幅。</summary>
    public string ChainKey = "";
    /// <summary>沿走向第几幅（1 起）。反推走向时按它定顺序。</summary>
    public int Panel;
}

// ─────────────────────────────────────────────────────────────────────────────
//  配色
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 设备类别配色。
///
/// <para><b>依据</b>：Okabe &amp; Ito 的色觉障碍友好调色板（经 Wong, B.
/// "Points of view: Color blindness", <i>Nature Methods</i> 8:441, 2011 推广），
/// 8 色，设计目标就是在红绿色盲（protanopia / deuteranopia）与蓝黄色盲（tritanopia）下仍可分辨。
/// 本包**原样取用**，不自己调色 —— 自己调的色没有依据。</para>
///
/// <para><b>一处偏离，明说</b>：Okabe–Ito 的第 8 色是纯黑 <c>#000000</c>。
/// 三维视口里坑体与阴影都偏暗，纯黑符号看不见。所以 <see cref="EquipKind.Other"/>
/// 用中性灰 <c>#8A8F98</c>（与 SimGeometryPort 的「原始轮廓灰」同一个值，
/// 一个灰一个来源）。<b>它不在 Okabe–Ito 里</b>，所以 <see cref="EquipmentStageCheck"/>
/// 的色盲筛查把它一起扫进去，别假设它天然安全。</para>
///
/// <para><b>类别 ↔ 颜色的对应不是拍的，是搜出来的</b>：8 色配 8 类共 8! = 40320 种对应，
/// 全排列跑了一遍（台架 scratchpad/PaletteSearch，ΔE 与色觉模拟直接调
/// <see cref="EquipmentStageCheck.DeltaE76"/>，<b>不另写一份度量</b>）。目标依次是
/// ① 最大化 <see cref="EquipPalette.HotKinds"/>（排产真会画出来的<b>六类</b>）
///    在正常 / 红色盲 / 绿色盲 / 蓝黄色盲四种视觉下的**最小** ΔE76；
/// ② 再最大化 <b>电铲↔前装机</b>（同为采装工序、形态又最像的一对，现场最容易认错）；
/// ③ 再最大化全局最小；④ 再最大化总和。
/// 另有一条<b>钉死的约束</b>：矿卡 = 黄（现场惯例，矿用卡车就是黄的），不参与搜索。
///
/// <para><b>2026-08 重搜过一次</b>，起因是引擎加了穿孔与排土 ⇒ 钻机与推土机进了 <c>HotKinds</c>，
/// 原来那套按四类搜的对应在六类下露馅：蓝黄色盲 电铲↔推土机 9.8、绿色盲 矿卡↔钻机 10.0，
/// 而注释还写着"四类主力 ≥ 24.9"。<b>那个 24.9 是只数四类算出来的</b>。</para>
///
/// <b>实测结果</b>（<see cref="EquipmentStageCheck.CheckColorBlindSafe"/> 每次跑都会复算）：
/// <list type="bullet">
///   <item>六类主力：任意两两、任意色觉下 <b>ΔE76 ≥ 10.4</b>（最紧的一对在绿色盲下）。
///         比重搜前的 9.8 略好 —— <b>提升很有限，这是调色板的天花板，不是搜索没搜到</b>。</item>
///   <item>电铲↔前装机：<b>24.9 → 83.3</b>（同为采装、形态最像的一对，这一档是这次重搜的主要收益）。</item>
///   <item>全部八类：二色觉下最小仍是 <b>7.8</b> —— 调色板自身性质，换对应消不掉。</item>
/// </list>
/// <b>那个 7.8 换任何对应关系都消不掉</b> —— 它是调色板自身的性质
/// （#E69F00 与 #D55E00 在绿色盲模拟下恒为 7.8），8 个类别本来就超出了「靠色相分」的上限。
/// <b>六类主力那个 10.4 也是同一回事</b>：40320 种对应里最好的就是它。
/// 所以做法是：把相撞的对尽量赶到主力之外，并且**让形状而不是颜色当主通道**
/// —— 铲、卡、钻机、推土机的符号形态本来就完全不同。
/// ⚠ <b>别在图例上说「配色色盲友好」</b>：六类主力里仍有 ΔE ≈ 10 的对，
/// 相撞的配对由 <see cref="EquipmentStageCheck.ColorBlindCollisions"/> 逐条列出，照实标。
/// 相撞的配对由 <see cref="EquipmentStageCheck.ColorBlindCollisions"/> 逐条列出，
/// <b>图例上必须照实标</b>，不许说「配色色盲友好」就完事。</para>
///
/// <para><b>状态不换主色</b>：换色会让「这是什么设备」和「它在干嘛」抢同一个视觉通道。
/// 在「只能烤死顶点色」的约束下，状态走两条**别的**通道：
/// <list type="number">
///   <item><b>明度</b>：整体乘一个系数 k（<see cref="StateShade"/>）。
///         R/G/B 同乘一个数，HSV 的 H 与 S 严格不变、只有 V 变 —— 所以主色（色相）真的没动，
///         这一条由 <see cref="EquipmentStageCheck"/> 逐色验证，不是嘴上说说。</item>
///   <item><b>形状</b>：符号顶上加一根**中性白**的状态标记，形状本身编码状态
///         （竖杆 = 作业 / T 形 = 运输 / 前指箭头 = 转场 / 矮墩 = 待命 / 交叉 X = 检修）。
///         形状通道对任何色觉类型都成立，明度只是冗余备份。</item>
/// </list></para>
/// </summary>
public static class EquipPalette
{
    /// <summary>类别主色 0xRRGGBB。对应关系由全排列搜索确定，见类注释，<b>别手改</b>
    /// —— 要改就重跑搜索（scratchpad/PaletteSearch），并把类注释里的实测数一起更新。</summary>
    public static uint Rgb(EquipKind k) => k switch
    {
        EquipKind.Shovel     => 0x56B4E9,   // Okabe–Ito sky blue       天蓝
        EquipKind.Truck      => 0xF0E442,   // Okabe–Ito yellow         黄（现场惯例：矿卡就是黄的，钉死不参与搜索）
        EquipKind.Drill      => 0x0072B2,   // Okabe–Ito blue           蓝
        EquipKind.Loader     => 0xD55E00,   // Okabe–Ito vermillion     朱红
        EquipKind.Dozer      => 0xCC79A7,   // Okabe–Ito reddish purple 品红
        EquipKind.Grader     => 0xE69F00,   // Okabe–Ito orange         橙
        EquipKind.WaterTruck => 0x009E73,   // Okabe–Ito bluish green   青绿
        _                    => 0x8A8F98,   // 中性灰（替代 Okabe–Ito 的纯黑，见类注释）
    };

    /// <summary>
    /// 「排产真会画出来的」那几类。配色搜索优先保这几类的可分辨度。
    ///
    /// <para><b>2026-08 扩过一次（四类 → 六类）</b>：<c>EquipmentAssigner</c> 加了穿孔与排土之后，
    /// <b>钻机与推土机也会被排出来</b>了，而原来这张表写的是"它们一台都不排"。
    /// 前提变了却没人改这张表的话，新进来的两类落在哪对颜色上就是没人管过的 ——
    /// 实测当时确实撞了：蓝黄色盲下 电铲↔推土机 只有 9.8、绿色盲下 矿卡↔钻机 只有 10.0，
    /// 而类注释还写着"四类主力 ≥ 24.9"。<b>那个 24.9 是只数四类算出来的，六类下根本不成立。</b></para>
    ///
    /// <para>⚠ 以后再给引擎加能排的设备类别（比如平路机/洒水车进了排产），
    /// <b>必须同时把它加进这张表并重跑配色搜索</b>。</para>
    /// </summary>
    public static readonly EquipKind[] HotKinds =
    {
        EquipKind.Shovel, EquipKind.Truck, EquipKind.Drill,
        EquipKind.Loader, EquipKind.Dozer, EquipKind.Other,
    };

    /// <summary>类别中文名（界面/状态文案一处出）。</summary>
    public static string Name(EquipKind k) => k switch
    {
        EquipKind.Shovel => "电铲",
        EquipKind.Truck => "矿用卡车",
        EquipKind.Drill => "钻机",
        EquipKind.Loader => "前装机",
        EquipKind.Dozer => "推土机",
        EquipKind.Grader => "平路机",
        EquipKind.WaterTruck => "洒水车",
        _ => "其他",
    };

    public static string Name(EquipState s) => s switch
    {
        EquipState.Working => "作业·挖装",
        EquipState.Hauling => "作业·运输",
        EquipState.Relocating => "转场",
        EquipState.Standby => "待命",
        _ => "检修",
    };

    /// <summary>
    /// 状态 → 明度系数。R/G/B 同乘它，<b>色相与饱和度不变</b>。
    /// 作业两态（挖装/运输）都是满明度 —— 它们都是在干活，靠标记形状区分，不靠明暗。
    /// </summary>
    public static double StateShade(EquipState s) => s switch
    {
        EquipState.Working => 1.00,
        EquipState.Hauling => 1.00,
        EquipState.Relocating => 0.78,
        EquipState.Standby => 0.58,
        _ => 0.40,          // 检修最暗
    };

    /// <summary>状态标记的颜色：中性近白，<b>所有状态一个值</b>（状态只由形状编码）。</summary>
    public const uint MarkerRgb = 0xF2F2F2;

    /// <summary>R/G/B 同乘 <paramref name="k"/>（0~1），四舍五入回 8bit。</summary>
    public static uint Shade(uint rgb, double k)
    {
        if (k >= 0.999) return rgb;
        int r = (int)Math.Round(((rgb >> 16) & 0xFF) * k);
        int g = (int)Math.Round(((rgb >> 8) & 0xFF) * k);
        int b = (int)Math.Round((rgb & 0xFF) * k);
        return (uint)((Clamp8(r) << 16) | (Clamp8(g) << 8) | Clamp8(b));
    }

    private static int Clamp8(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    /// <summary>全部 8 类（判据与图例都从这里取，别再各写一份）。</summary>
    public static readonly EquipKind[] AllKinds =
        (EquipKind[])Enum.GetValues(typeof(EquipKind));

    /// <summary>全部 5 态。</summary>
    public static readonly EquipState[] AllStates =
        (EquipState[])Enum.GetValues(typeof(EquipState));

    /// <summary>图例一行（界面直接显示，颜色值就是建体时烤进顶点的那个值）。</summary>
    public static string LegendLine(EquipKind k) => $"{Name(k)}　#{Rgb(k):X6}";
}

// ─────────────────────────────────────────────────────────────────────────────
//  符号几何
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 参数化符号库 —— 7 类设备 + 1 个「其他」，每类一个由**六面体拼出来**的简单 mesh。
/// 不读任何外部模型文件（<c>BuildColoredMeshOnLayer</c> 只吃顶点 + 索引 + 逐顶点色）。
///
/// <para><b>局部坐标系</b>：+X = 前进方向，+Y = 左，+Z = 上，原点在**接地面中心**。
/// 尺寸按「长 = 1.0」归一化（x ∈ [-0.5, 0.5]），落位时整体乘
/// <see cref="EquipmentStage.SymbolLengthM"/>。</para>
///
/// <para><b>每个盒子 24 顶点，不共享</b>：内核建体时会调 <c>computeSmoothNormals()</c>
/// （xllAcEd.cpp:8769），共享顶点会把方盒子的法线磨圆，光照下变成一坨糊的东西。
/// 逐面独立顶点是唯一能保住棱角的做法，代价是顶点数 ×3。</para>
///
/// <para><b>顶点量级</b>（含状态标记，单个符号）：
/// 电铲 120 / 前装机 120 / 矿卡 96 / 钻机 96 / 推土机 96 / 平路机 96 /
/// 洒水车 120 / 其他 72 —— <b>72 ~ 144 顶点、36 ~ 68 三角</b>。
/// 单月上限规模（48~65 采掘单元 + 46~48 排土位置，每个单元 1 铲 + 3 车）按 300 个符号算，
/// 合计约 3.3 万顶点 / 1.6 万三角面。这是**一次性**建体的量，不是每帧的量。
/// 真嫌大就调 <see cref="EquipmentStage.SymbolLengthM"/>（尺寸）或少喂几行，
/// 别去简化盒子 —— 盒子已经是能表达设备形态的最少面数了。</para>
///
/// <para><b>形态是示意，不是外廓</b>：各部件的比例只为「一眼分得出是铲还是车」，
/// <b>不对应任何一台真实设备的尺寸</b>。整体长度是显示参数
/// <see cref="EquipmentStage.SymbolLengthM"/>，默认 30 m —— 这个数是**显示尺度**，
/// 不是设备长度，界面上必须这么写。</para>
/// </summary>
/// <summary>
/// 设备形态的写入端。<b>抽成接口只为一件事：形态定义只有一份。</b>
///
/// <para>同一台铲要画两种介质 —— 实体层的多色实心块（<see cref="EquipMeshBuffer"/>，
/// 三角面，入库）和动画层的三维线框（<see cref="EquipWireBuffer"/>，overlay 线段，逐帧可动）。
/// 两边各写一套「铲长什么样」的话，改了一边另一边不动，现象是**同一台设备在两个图层里长得不一样**，
/// 而两边各自都自洽、都不报错。</para>
///
/// <para>所以 <see cref="EquipSymbolLibrary"/> 只描述形态、只调本接口，
/// 「怎么落成三角还是落成边」由实现去管。位姿变换也归实现（见 <see cref="BeginPose"/>）。</para>
/// </summary>
public interface IEquipShapeSink
{
    /// <summary>开一个位姿：之后写入的局部坐标都按 (尺度 → 绕 Z 旋 → 平移) 变换。</summary>
    void BeginPose(double ox, double oy, double oz, double headingRad, double scale);
    void EndPose();

    /// <summary>轴对齐盒子（局部坐标）。</summary>
    void Box(double x0, double x1, double y0, double y1, double z0, double z1, uint rgb);

    /// <summary>任意六面体（局部）：底面 4 点 (0→3) 逆时针，顶面 4 点 (4→7) 与之一一对应。</summary>
    void Hex(double x0, double y0, double z0, double x1, double y1, double z1,
             double x2, double y2, double z2, double x3, double y3, double z3,
             double x4, double y4, double z4, double x5, double y5, double z5,
             double x6, double y6, double z6, double x7, double y7, double z7,
             uint rgb);

    /// <summary>沿 X 轴的正棱柱（洒水车的水罐）。</summary>
    void PrismX(double x0, double x1, double zc0, double zc1, double radiusY, int sides, uint rgb);

    /// <summary>已写入的顶点数（<see cref="EquipSymbolLibrary.Emit"/> 靠它回报本次写了多少）。</summary>
    int VertexCount { get; }
}

public static class EquipSymbolLibrary
{
    /// <summary>把一类设备的符号写进 <paramref name="buf"/>（世界坐标，已按位姿变换）。</summary>
    /// <param name="buf">目标缓冲。</param>
    /// <param name="kind">设备类别。</param>
    /// <param name="state">作业状态（决定明度与标记形状）。</param>
    /// <param name="ox">落位点 X（世界）。</param>
    /// <param name="oy">落位点 Y（世界）。</param>
    /// <param name="oz">落位点 Z（世界，接地面）。</param>
    /// <param name="headingRad">前进方向方位（弧度，自 +X 轴逆时针）。</param>
    /// <param name="lengthM">符号总长（m）。</param>
    /// <param name="markerScale">状态标记高度倍数（1.0 = 缺省）。<b>&lt;= 0 = 不画状态标记</b>
    /// —— 图标那一路要的就是这个（小尺寸下那根杆会把设备本体挤小）。</param>
    /// <returns>本次写入的顶点数。</returns>
    public static int Emit(IEquipShapeSink buf, EquipKind kind, EquipState state,
                           double ox, double oy, double oz, double headingRad,
                           double lengthM, double markerScale = 1.0)
    {
        int v0 = buf.VertexCount;
        buf.BeginPose(ox, oy, oz, headingRad, lengthM);

        uint c = EquipPalette.Shade(EquipPalette.Rgb(kind), EquipPalette.StateShade(state));
        // 深一档的同色，用来把「底盘 / 履带」和「上装」分开 —— 仍是同一色相
        uint cDark = EquipPalette.Shade(c, 0.70);

        double top;
        switch (kind)
        {
            case EquipKind.Shovel: top = Shovel(buf, c, cDark); break;
            case EquipKind.Loader: top = Loader(buf, c, cDark); break;
            case EquipKind.Truck: top = Truck(buf, c, cDark); break;
            case EquipKind.Drill: top = Drill(buf, c, cDark); break;
            case EquipKind.Dozer: top = Dozer(buf, c, cDark); break;
            case EquipKind.Grader: top = Grader(buf, c, cDark); break;
            case EquipKind.WaterTruck: top = WaterTruck(buf, c, cDark); break;
            default: top = Other(buf, c, cDark); break;
        }

        Marker(buf, state, top, markerScale);
        buf.EndPose();
        return buf.VertexCount - v0;
    }

    /// <summary>该类符号的顶点数（不建体，判据用来对账「符号数 × 每类顶点数 = 总顶点数」）。</summary>
    public static int VertexCountOf(EquipKind kind, EquipState state)
    {
        var probe = new EquipMeshBuffer();
        return Emit(probe, kind, state, 0, 0, 0, 0, 1.0);
    }

    // ── 各类形态（全部归一化到「长 = 1.0」）───────────────────────────────
    // 返回值 = 本体最高点的局部 z（状态标记从这儿往上长）

    /// <summary>
    /// 电铲：两条履带（带驱动轮/导向轮）+ 底架 + 回转支承 + 回转平台 + 机房 + 司机室（含窗）
    /// + 尾部配重 + <b>A 字架</b> + 斜置动臂 + 斗杆 + <b>带齿铲斗</b>。
    /// <para>加细的原则不变：只加**远看能认出机型**的特征件。
    /// 履带两端的驱动轮/导向轮让"履带"不再是一根方条；A 字架是电铲最独特的轮廓；
    /// 斗齿让铲斗一眼可辨。螺栓法兰那种近看才有的一律不加 —— 缩小视图下只会糊成一坨。</para>
    /// </summary>
    private static double Shovel(IEquipShapeSink b, uint c, uint cd)
    {
        foreach (double sg in new[] { -1.0, 1.0 })
        {
            b.Box(-0.42, 0.16, sg * 0.16, sg * 0.28, 0.02, 0.14, cd);      // 履带板
            b.Box(-0.46, -0.36, sg * 0.17, sg * 0.27, 0.00, 0.16, c);      // 导向轮（后）
            b.Box(0.14, 0.24, sg * 0.17, sg * 0.27, 0.00, 0.16, c);        // 驱动轮（前）
        }
        b.Box(-0.36, 0.12, -0.17, 0.17, 0.06, 0.13, cd);                   // 底架横梁
        b.Box(-0.30, 0.06, -0.13, 0.13, 0.13, 0.18, c);                    // 回转支承

        b.Box(-0.36, 0.10, -0.23, 0.23, 0.18, 0.40, c);                    // 回转平台
        b.Box(-0.30, -0.02, -0.21, 0.21, 0.40, 0.52, c);                   // 机房
        b.Box(-0.42, -0.26, -0.21, 0.21, 0.16, 0.38, cd);                  // 尾部配重
        b.Box(0.00, 0.14, -0.20, -0.04, 0.40, 0.60, cd);                   // 司机室
        b.Box(0.01, 0.13, -0.21, -0.19, 0.44, 0.57, c);                    //   侧窗

        foreach (double sg in new[] { -1.0, 1.0 })                          // A 字架
            b.Hex(-0.10, sg * 0.10, 0.52, -0.02, sg * 0.10, 0.52,
                  -0.02, sg * 0.14, 0.52, -0.10, sg * 0.14, 0.52,
                  0.02, sg * 0.02, 0.86, 0.08, sg * 0.02, 0.86,
                  0.08, sg * 0.05, 0.86, 0.02, sg * 0.05, 0.86, c);

        b.Hex(0.06, -0.07, 0.34, 0.48, -0.07, 0.66,                        // 动臂
              0.48, 0.07, 0.66, 0.06, 0.07, 0.34,
              0.06, -0.07, 0.43, 0.46, -0.07, 0.75,
              0.46, 0.07, 0.75, 0.06, 0.07, 0.43, c);
        b.Hex(0.34, -0.05, 0.60, 0.52, -0.05, 0.40,                        // 斗杆
              0.52, 0.05, 0.40, 0.34, 0.05, 0.60,
              0.34, -0.05, 0.66, 0.50, -0.05, 0.46,
              0.50, 0.05, 0.46, 0.34, 0.05, 0.66, cd);
        b.Box(0.44, 0.60, -0.16, 0.16, 0.26, 0.50, cd);                    // 铲斗
        for (int i = -1; i <= 1; i++)                                       // 斗齿
            b.Box(0.60, 0.66, i * 0.10 - 0.02, i * 0.10 + 0.02, 0.27, 0.33, c);
        return 0.86;
    }

    /// <summary>
    /// 前装机：<b>四个大胶轮</b>（带轮毂）+ 前后车架（铰接）+ 驾驶室（含窗、顶棚）
    /// + 举升臂（两侧）+ 翻斗油缸 + <b>带齿铲斗</b>。
    /// <para>与电铲的分界：**轮式**（不是履带）+ **铰接车架**（中间有折点）——
    /// 这两点让它在远看时不会被当成小一号的电铲。</para>
    /// </summary>
    private static double Loader(IEquipShapeSink b, uint c, uint cd)
    {
        foreach (double sg in new[] { -1.0, 1.0 })                          // 四个大胶轮
        {
            b.Box(-0.36, -0.16, sg * 0.18, sg * 0.28, 0.00, 0.20, cd);
            b.Box(-0.29, -0.23, sg * 0.28, sg * 0.30, 0.06, 0.14, c);       //   轮毂
            b.Box(0.06, 0.26, sg * 0.18, sg * 0.28, 0.00, 0.20, cd);
            b.Box(0.13, 0.19, sg * 0.28, sg * 0.30, 0.06, 0.14, c);         //   轮毂
        }
        b.Box(-0.40, -0.06, -0.17, 0.17, 0.14, 0.26, cd);                   // 后车架
        b.Box(-0.08, -0.02, -0.09, 0.09, 0.14, 0.28, c);                    // 铰接节（中间折点）
        b.Box(0.00, 0.30, -0.17, 0.17, 0.14, 0.26, cd);                     // 前车架

        b.Box(-0.34, -0.10, -0.19, 0.19, 0.26, 0.36, c);                    // 发动机罩
        b.Box(-0.08, 0.14, -0.16, 0.16, 0.26, 0.52, c);                     // 驾驶室
        b.Box(-0.07, 0.13, -0.17, -0.155, 0.32, 0.48, cd);                  //   侧窗
        b.Box(-0.10, 0.16, -0.18, 0.18, 0.52, 0.55, cd);                    //   顶棚

        foreach (double sg in new[] { -1.0, 1.0 })                          // 举升臂（两侧）
            b.Hex(0.06, sg * 0.13, 0.28, 0.42, sg * 0.13, 0.16,
                  0.42, sg * 0.18, 0.16, 0.06, sg * 0.18, 0.28,
                  0.06, sg * 0.13, 0.36, 0.42, sg * 0.13, 0.24,
                  0.42, sg * 0.18, 0.24, 0.06, sg * 0.18, 0.36, c);
        b.Box(0.14, 0.34, -0.05, 0.05, 0.30, 0.36, cd);                     // 翻斗油缸

        b.Box(0.40, 0.56, -0.24, 0.24, 0.02, 0.28, cd);                     // 铲斗
        for (int i = -2; i <= 2; i++)                                        //   斗齿
            b.Box(0.56, 0.62, i * 0.09 - 0.02, i * 0.09 + 0.02, 0.03, 0.09, c);
        return 0.55;
    }

    /// <summary>
    /// 矿卡：车架 + 前保险杠 + <b>六轮（前单后双）带轮毂</b> + 梯形车厢（带侧加强筋）
    /// + 车厢前挡板 + 驾驶室（含侧窗/前窗/爬梯/顶护栏）+ 排气管。
    /// <para>轮子是"这是台车"最强的识别特征；车厢的加强筋和前挡板让它区别于一块料。</para>
    /// </summary>
    private static double Truck(IEquipShapeSink b, uint c, uint cd)
    {
        b.Box(-0.46, 0.44, -0.19, 0.19, 0.11, 0.21, cd);                   // 车架
        b.Box(0.38, 0.48, -0.20, 0.20, 0.14, 0.24, cd);                    // 前保险杠

        foreach (double sg in new[] { -1.0, 1.0 })
        {
            b.Box(0.24, 0.40, sg * 0.19, sg * 0.29, 0.00, 0.17, cd);       // 前轮
            b.Box(0.29, 0.35, sg * 0.29, sg * 0.31, 0.05, 0.12, c);        //   轮毂
            b.Box(-0.36, -0.16, sg * 0.19, sg * 0.30, 0.00, 0.17, cd);     // 后轮外
            b.Box(-0.36, -0.16, sg * 0.11, sg * 0.18, 0.00, 0.17, cd);     // 后轮内
            b.Box(-0.29, -0.23, sg * 0.30, sg * 0.32, 0.05, 0.12, c);      //   轮毂
        }

        b.Hex(-0.44, -0.20, 0.21, 0.16, -0.20, 0.21,                       // 车厢：下窄
              0.16, 0.20, 0.21, -0.44, 0.20, 0.21,
              -0.51, -0.29, 0.50, 0.20, -0.29, 0.50,                       //         上宽
              0.20, 0.29, 0.50, -0.51, 0.29, 0.50, c);
        foreach (double sg in new[] { -1.0, 1.0 })                          // 车厢侧加强筋
            for (int i = 0; i < 3; i++)
                b.Box(-0.36 + i * 0.16, -0.32 + i * 0.16, sg * 0.235, sg * 0.265, 0.24, 0.47, cd);
        b.Box(0.13, 0.21, -0.29, 0.29, 0.42, 0.66, c);                     // 车厢前挡板

        b.Box(0.22, 0.44, -0.17, 0.17, 0.21, 0.44, cd);                    // 驾驶室
        b.Box(0.23, 0.43, -0.185, -0.165, 0.28, 0.41, c);                  //   侧窗
        b.Box(0.44, 0.455, -0.15, 0.15, 0.26, 0.42, c);                    //   前窗
        b.Box(0.20, 0.24, -0.22, -0.18, 0.02, 0.22, cd);                   //   爬梯
        b.Box(0.22, 0.44, -0.19, 0.19, 0.44, 0.465, cd);                   //   顶护栏
        b.Box(0.09, 0.15, -0.07, -0.01, 0.44, 0.62, cd);                   // 排气管
        return 0.66;
    }

    /// <summary>
    /// 钻机：两条履带 + 机体 + 司机室 + <b>桁架式立桅</b>（四根立杆 + 五道横撑）+ 钻杆 + 支腿。
    /// <para>立桅是钻机唯一的识别特征 —— 做成桁架（而不是一根实心方条）之后，
    /// 远看就与"竖着的杆状物"区别开了。</para>
    /// </summary>
    private static double Drill(IEquipShapeSink b, uint c, uint cd)
    {
        foreach (double sg in new[] { -1.0, 1.0 })
            b.Box(-0.38, 0.22, sg * 0.14, sg * 0.26, 0.02, 0.16, cd);      // 履带
        b.Box(-0.32, 0.16, -0.15, 0.15, 0.06, 0.14, cd);                   // 底架
        b.Box(-0.30, 0.14, -0.18, 0.18, 0.16, 0.36, c);                    // 机体
        b.Box(-0.28, -0.10, -0.16, 0.16, 0.36, 0.56, cd);                  // 司机室
        b.Box(-0.27, -0.11, -0.17, -0.15, 0.40, 0.52, c);                  //   窗

        // 桁架立桅：四根立杆 + 五道横撑
        foreach (double sx in new[] { 0.10, 0.24 })
            foreach (double sy in new[] { -0.07, 0.07 })
                b.Box(sx - 0.02, sx + 0.02, sy - 0.02, sy + 0.02, 0.16, 0.96, c);
        for (int i = 0; i < 5; i++)
        {
            double z = 0.24 + i * 0.17;
            b.Box(0.08, 0.26, -0.09, 0.09, z, z + 0.02, cd);
        }
        b.Box(0.16, 0.19, -0.02, 0.02, 0.10, 0.90, cd);                    // 钻杆
        foreach (double sg in new[] { -1.0, 1.0 })                          // 支腿
            b.Box(0.18, 0.26, sg * 0.20, sg * 0.26, 0.00, 0.10, cd);
        b.Box(-0.36, -0.28, -0.06, 0.06, 0.00, 0.10, cd);
        return 0.96;
    }

    /// <summary>
    /// 推土机：两条履带（带驱动轮/导向轮/支重轮）+ 车体 + 发动机罩 + 司机室（含窗、顶棚）
    /// + 前推铲（带侧护板、刀刃、推杆）+ 尾部松土器（三齿）。
    /// <para>推铲的**侧护板 + 刀刃**和尾部的**松土齿**是远看认出推土机的关键 ——
    /// 只有一块平板的话，与前装机的铲斗分不开。</para>
    /// </summary>
    private static double Dozer(IEquipShapeSink b, uint c, uint cd)
    {
        foreach (double sg in new[] { -1.0, 1.0 })
        {
            b.Box(-0.40, 0.16, sg * 0.15, sg * 0.28, 0.03, 0.17, cd);      // 履带板
            b.Box(-0.44, -0.34, sg * 0.16, sg * 0.27, 0.00, 0.19, c);      // 导向轮
            b.Box(0.12, 0.22, sg * 0.16, sg * 0.27, 0.00, 0.19, c);        // 驱动轮
            for (int i = 0; i < 3; i++)                                     // 支重轮
                b.Box(-0.26 + i * 0.13, -0.20 + i * 0.13, sg * 0.17, sg * 0.26, 0.00, 0.08, c);
        }
        b.Box(-0.34, 0.10, -0.16, 0.16, 0.08, 0.16, cd);                   // 底架

        b.Box(-0.32, 0.12, -0.21, 0.21, 0.17, 0.38, c);                    // 车体
        b.Box(0.00, 0.14, -0.18, 0.18, 0.38, 0.50, c);                     // 发动机罩
        b.Box(-0.24, 0.00, -0.16, 0.16, 0.38, 0.62, cd);                   // 司机室
        b.Box(-0.23, -0.01, -0.17, -0.15, 0.43, 0.58, c);                  //   侧窗
        b.Box(-0.28, 0.04, -0.19, 0.19, 0.62, 0.65, cd);                   //   顶棚

        b.Hex(0.28, -0.36, 0.02, 0.36, -0.36, 0.02,                        // 推铲
              0.36, 0.36, 0.02, 0.28, 0.36, 0.02,
              0.36, -0.36, 0.36, 0.46, -0.36, 0.36,
              0.46, 0.36, 0.36, 0.36, 0.36, 0.36, cd);
        b.Box(0.28, 0.40, -0.36, 0.36, 0.00, 0.05, c);                     //   刀刃
        b.Box(0.28, 0.46, -0.39, -0.33, 0.02, 0.36, cd);                   //   左护板
        b.Box(0.28, 0.46, 0.33, 0.39, 0.02, 0.36, cd);                     //   右护板
        foreach (double sg in new[] { -1.0, 1.0 })                          //   推杆
            b.Box(0.12, 0.34, sg * 0.16, sg * 0.21, 0.10, 0.17, cd);

        b.Box(-0.46, -0.34, -0.22, 0.22, 0.08, 0.24, cd);                  // 松土器横梁
        for (int i = -1; i <= 1; i++)                                       //   三齿
            b.Box(-0.52, -0.44, i * 0.14 - 0.03, i * 0.14 + 0.03, 0.00, 0.22, c);
        return 0.65;
    }

    /// <summary>
    /// 平路机：<b>细长车架</b> + 前双轮 + 后四轮 + 驾驶室（含窗、顶棚）+ 发动机罩
    /// + <b>中部斜刮刀</b>（带回转圈与牵引架）+ 前推土板。
    /// <para>斜刮刀是平路机唯一的标志；细长的车架 + 前后轮距很大，是它与推土机的分界。</para>
    /// </summary>
    private static double Grader(IEquipShapeSink b, uint c, uint cd)
    {
        b.Box(-0.52, 0.52, -0.09, 0.09, 0.14, 0.24, cd);                    // 细长车架

        foreach (double sg in new[] { -1.0, 1.0 })
        {
            b.Box(0.38, 0.52, sg * 0.11, sg * 0.20, 0.00, 0.18, cd);        // 前轮
            b.Box(-0.46, -0.30, sg * 0.11, sg * 0.21, 0.00, 0.18, cd);      // 后轮（前对）
            b.Box(-0.26, -0.10, sg * 0.11, sg * 0.21, 0.00, 0.18, cd);      // 后轮（后对）
        }

        b.Box(-0.46, -0.16, -0.15, 0.15, 0.24, 0.40, c);                    // 发动机罩
        b.Box(-0.12, 0.16, -0.14, 0.14, 0.24, 0.50, c);                     // 驾驶室
        b.Box(-0.11, 0.15, -0.15, -0.14, 0.30, 0.46, cd);                   //   侧窗
        b.Box(-0.14, 0.18, -0.16, 0.16, 0.50, 0.53, cd);                    //   顶棚

        b.Box(0.10, 0.34, -0.10, 0.10, 0.16, 0.22, cd);                     // 牵引架
        b.Box(0.02, 0.14, -0.13, 0.13, 0.08, 0.16, c);                      // 回转圈
        b.Hex(-0.26, -0.32, 0.00, 0.04, 0.26, 0.00,                         // 斜刮刀
              0.08, 0.30, 0.00, -0.22, -0.28, 0.00,
              -0.26, -0.32, 0.22, 0.04, 0.26, 0.22,
              0.08, 0.30, 0.22, -0.22, -0.28, 0.22, cd);
        b.Box(0.50, 0.58, -0.24, 0.24, 0.02, 0.20, cd);                     // 前推土板
        return 0.53;
    }

    /// <summary>
    /// 洒水车：车架 + 六轮 + 驾驶室（含窗）+ <b>卧式八棱柱水罐</b>（带前后封头环箍）
    /// + 顶部人孔 + 尾部喷洒杆（三个喷头）+ 排气管。
    /// <para>圆柱形水罐是它唯一的识别特征 —— 加上环箍和尾部喷洒杆之后，
    /// 远看不会再和矿卡的方形车厢混。</para>
    /// </summary>
    private static double WaterTruck(IEquipShapeSink b, uint c, uint cd)
    {
        b.Box(-0.50, 0.48, -0.16, 0.16, 0.08, 0.16, cd);                    // 车架

        foreach (double sg in new[] { -1.0, 1.0 })
        {
            b.Box(0.26, 0.42, sg * 0.16, sg * 0.24, 0.00, 0.15, cd);        // 前轮
            b.Box(-0.34, -0.18, sg * 0.16, sg * 0.25, 0.00, 0.15, cd);      // 后轮外
            b.Box(-0.34, -0.18, sg * 0.09, sg * 0.15, 0.00, 0.15, cd);      // 后轮内
        }

        b.Box(0.24, 0.46, -0.15, 0.15, 0.16, 0.40, cd);                     // 驾驶室
        b.Box(0.25, 0.45, -0.16, -0.145, 0.22, 0.36, c);                    //   侧窗
        b.Box(0.46, 0.475, -0.13, 0.13, 0.21, 0.37, c);                     //   前窗

        b.PrismX(-0.46, 0.20, 0.16, 0.42, 0.20, 8, c);                      // 卧式水罐
        b.PrismX(-0.48, -0.44, 0.15, 0.43, 0.21, 8, cd);                    //   后封头
        b.PrismX(0.18, 0.22, 0.15, 0.43, 0.21, 8, cd);                      //   前封头
        b.PrismX(-0.16, -0.12, 0.15, 0.43, 0.21, 8, cd);                    //   中环箍
        b.Box(-0.14, -0.04, -0.05, 0.05, 0.42, 0.47, cd);                   // 顶部人孔

        b.Box(-0.54, -0.48, -0.26, 0.26, 0.06, 0.12, cd);                   // 尾部喷洒杆
        for (int i = -1; i <= 1; i++)                                        //   三个喷头
            b.Box(-0.58, -0.52, i * 0.18 - 0.03, i * 0.18 + 0.03, 0.02, 0.08, c);
        b.Box(0.14, 0.20, -0.06, -0.01, 0.40, 0.56, cd);                    // 排气管
        return 0.56;
    }

    /// <summary>其他 / 未归类：底座 + 立柱。2 盒 = 48 顶点。刻意做得**不像任何一种设备**。</summary>
    private static double Other(IEquipShapeSink b, uint c, uint cd)
    {
        b.Box(-0.28, 0.28, -0.20, 0.20, 0.00, 0.12, cd);
        b.Box(-0.10, 0.10, -0.10, 0.10, 0.12, 0.62, c);
        return 0.62;
    }

    /// <summary>
    /// 状态标记：中性白，<b>形状编码状态</b>。杆底贴在本体顶上，往上长。
    /// 竖杆 = 作业挖装 / 竖杆 + 顶横杆 = 作业运输 / 竖杆 + 前指臂 = 转场 /
    /// 矮墩 = 待命 / 交叉 X = 检修。
    /// </summary>
    private static void Marker(IEquipShapeSink b, EquipState s, double baseZ, double scale)
    {
        // ★ scale <= 0 = 【不画标记】。
        //   原来这里是 `scale > 0 ? scale : 1.0` —— 传 0 想关掉，结果<b>回落成满尺寸</b>，
        //   而调用方看到的是"我明明传了 0"。图标那一路正是这么踩到的：
        //   每个图标底下都挂着一根状态杆，还把设备本体挤小了。
        //   全仓没有别的调用方传 0（其余都走缺省 1.0），所以这个语义可以直接改对。
        if (!(scale > 0)) return;

        const uint m = EquipPalette.MarkerRgb;
        double h = 0.34 * scale;                              // 杆高（归一化）
        double z0 = baseZ + 0.02, z1 = z0 + h;
        const double t = 0.035;                               // 杆半宽

        switch (s)
        {
            case EquipState.Working:                          // │
                b.Box(-t, t, -t, t, z0, z1, m);
                break;

            case EquipState.Hauling:                          // ┬
                b.Box(-t, t, -t, t, z0, z1, m);
                b.Box(-t, t, -0.16, 0.16, z1 - 2 * t, z1, m);
                break;

            case EquipState.Relocating:                       // ├→（横臂指向前进方向）
                b.Box(-t, t, -t, t, z0, z1, m);
                b.Box(t, 0.20, -t, t, z1 - 2.4 * t, z1 - 0.4 * t, m);
                break;

            case EquipState.Standby:                          // ▄（矮墩，明显比别人矮）
                b.Box(-0.09, 0.09, -0.09, 0.09, z0, z0 + 0.09, m);
                break;

            default:                                          // ✕（两根交叉斜杆）
                b.Hex(-0.13, -t, z0, 0.13, -t, z1, 0.13, t, z1, -0.13, t, z0,
                      -0.13, -t, z0 + 2 * t, 0.13, -t, z1 + 2 * t,
                       0.13, t, z1 + 2 * t, -0.13, t, z0 + 2 * t, m);
                b.Hex(-0.13, -t, z1, 0.13, -t, z0, 0.13, t, z0, -0.13, t, z1,
                      -0.13, -t, z1 + 2 * t, 0.13, -t, z0 + 2 * t,
                       0.13, t, z0 + 2 * t, -0.13, t, z1 + 2 * t, m);
                break;
        }
    }
}

/// <summary>
/// 顶点 / 索引 / 逐顶点色的累加缓冲，带一个「位姿」栈（落位点 + 方位 + 尺度）。
/// <para>写进来的都是**局部**坐标，<see cref="BeginPose"/> 之后由缓冲自己变换到世界系 ——
/// 这样符号库只描述形态，一处也不碰坐标变换。</para>
/// </summary>
public sealed class EquipMeshBuffer : IEquipShapeSink
{
    private readonly List<double> _v = new();
    private readonly List<uint> _t = new();
    private readonly List<uint> _c = new();

    private double _ox, _oy, _oz, _cos = 1, _sin, _s = 1;
    private bool _posed;

    public int VertexCount => _v.Count / 3;
    public int TriangleCount => _t.Count / 3;
    public bool IsEmpty => _t.Count < 3;

    public double[] WorldXyz => _v.ToArray();
    public uint[] Triangles => _t.ToArray();
    public uint[] VertexRgb => _c.ToArray();

    /// <summary>开一个位姿：之后写入的局部坐标都按 (尺度 → 绕 Z 旋 → 平移) 变换。</summary>
    public void BeginPose(double ox, double oy, double oz, double headingRad, double scale)
    {
        _ox = ox; _oy = oy; _oz = oz;
        _cos = Math.Cos(headingRad); _sin = Math.Sin(headingRad);
        _s = scale;
        _posed = true;
    }

    public void EndPose() => _posed = false;

    /// <summary>清空缓冲以便逐帧复用（每帧 new 一个在 10fps 下是纯浪费）。</summary>
    public void Clear() { _v.Clear(); _t.Clear(); _c.Clear(); _posed = false; }

    /// <summary>轴对齐盒子（局部）。24 顶点 / 12 三角。</summary>
    public void Box(double x0, double x1, double y0, double y1, double z0, double z1, uint rgb)
        => Hex(x0, y0, z0, x1, y0, z0, x1, y1, z0, x0, y1, z0,
               x0, y0, z1, x1, y0, z1, x1, y1, z1, x0, y1, z1, rgb);

    /// <summary>
    /// 任意六面体（局部）：底面 4 点 (0→3) 逆时针，顶面 4 点 (4→7) 与之一一对应。
    /// 6 个面各自 4 个**独立**顶点（不共享）→ 24 顶点 / 12 三角。
    /// </summary>
    public void Hex(double x0, double y0, double z0, double x1, double y1, double z1,
                    double x2, double y2, double z2, double x3, double y3, double z3,
                    double x4, double y4, double z4, double x5, double y5, double z5,
                    double x6, double y6, double z6, double x7, double y7, double z7,
                    uint rgb)
    {
        // 底（法线朝下：0,3,2,1）
        Quad(x0, y0, z0, x3, y3, z3, x2, y2, z2, x1, y1, z1, rgb);
        // 顶
        Quad(x4, y4, z4, x5, y5, z5, x6, y6, z6, x7, y7, z7, rgb);
        // 四个侧面
        Quad(x0, y0, z0, x1, y1, z1, x5, y5, z5, x4, y4, z4, rgb);
        Quad(x1, y1, z1, x2, y2, z2, x6, y6, z6, x5, y5, z5, rgb);
        Quad(x2, y2, z2, x3, y3, z3, x7, y7, z7, x6, y6, z6, rgb);
        Quad(x3, y3, z3, x0, y0, z0, x4, y4, z4, x7, y7, z7, rgb);
    }

    /// <summary>
    /// 沿 X 轴的正棱柱（洒水车的水罐）。<paramref name="sides"/> 个侧面，
    /// 每面 4 顶点独立 + 两端盖各 <paramref name="sides"/> 顶点。
    /// </summary>
    public void PrismX(double x0, double x1, double zc0, double zc1, double radiusY, int sides, uint rgb)
    {
        if (sides < 3) sides = 3;
        double zc = (zc0 + zc1) * 0.5, rz = (zc1 - zc0) * 0.5;
        double ry = radiusY;

        var py = new double[sides + 1];
        var pz = new double[sides + 1];
        for (int i = 0; i <= sides; i++)
        {
            double a = 2 * Math.PI * i / sides;
            py[i] = ry * Math.Cos(a);
            pz[i] = zc + rz * Math.Sin(a);
        }

        for (int i = 0; i < sides; i++)
            Quad(x0, py[i], pz[i], x1, py[i], pz[i], x1, py[i + 1], pz[i + 1], x0, py[i + 1], pz[i + 1], rgb);

        // 端盖：扇形三角（每盖 sides+1 顶点）
        Fan(x0, 0, zc, py, pz, sides, rgb, true);
        Fan(x1, 0, zc, py, pz, sides, rgb, false);
    }

    private void Fan(double x, double yc, double zc, double[] py, double[] pz, int sides, uint rgb, bool flip)
    {
        uint c0 = (uint)VertexCount;
        Push(x, yc, zc, rgb);
        for (int i = 0; i < sides; i++) Push(x, py[i], pz[i], rgb);
        for (int i = 0; i < sides; i++)
        {
            uint a = c0 + 1 + (uint)i;
            uint b = c0 + 1 + (uint)((i + 1) % sides);
            if (flip) { _t.Add(c0); _t.Add(b); _t.Add(a); }
            else { _t.Add(c0); _t.Add(a); _t.Add(b); }
        }
    }

    /// <summary>四边形（4 独立顶点 + 2 三角），顶点序按外法线右手。</summary>
    private void Quad(double ax, double ay, double az, double bx, double by, double bz,
                      double cx, double cy, double cz, double dx, double dy, double dz, uint rgb)
    {
        uint i0 = (uint)VertexCount;
        Push(ax, ay, az, rgb); Push(bx, by, bz, rgb); Push(cx, cy, cz, rgb); Push(dx, dy, dz, rgb);
        _t.Add(i0); _t.Add(i0 + 1); _t.Add(i0 + 2);
        _t.Add(i0); _t.Add(i0 + 2); _t.Add(i0 + 3);
    }

    private void Push(double x, double y, double z, uint rgb)
    {
        if (_posed)
        {
            double sx = x * _s, sy = y * _s, sz = z * _s;
            _v.Add(_ox + sx * _cos - sy * _sin);
            _v.Add(_oy + sx * _sin + sy * _cos);
            _v.Add(_oz + sz);
        }
        else { _v.Add(x); _v.Add(y); _v.Add(z); }
        // 0xFFFFFFFF 在内核里是「用源面底色」的哨兵（xllAcEd.cpp:8744），
        // 本包永远给真实色，撞上哨兵就退一档，免得整块变成源面色。
        _c.Add(rgb == 0xFFFFFFFFu ? 0xFEFEFEu : rgb);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  摆位结果
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一个符号真正落在哪儿（判据与右侧面板都读它）。</summary>
public sealed class EquipSymbol
{
    public string MachineId = "";
    public EquipKind Kind;
    public EquipState State;
    public string UnitId = "";
    public string Model = "";
    /// <summary>落位点（世界坐标，接地面）。</summary>
    public double X, Y, Z;
    /// <summary>前进方向（弧度，自 +X 逆时针）。</summary>
    public double HeadingRad;
    public EquipPositionSource PositionSource;
    /// <summary>位置是哪一条真实记录给的（采掘带 UnitId / 排土位置 Code / 台账行）。<b>不许留空</b>。</summary>
    public string PositionRef = "";
    /// <summary>本符号写进网格的顶点数（判据对账用）。</summary>
    public int VertexCount;

    public string Caption =>
        $"{MachineId}（{EquipPalette.Name(Kind)}{(Model.Length > 0 ? " " + Model : "")}）"
        + $" @ {UnitId}　{EquipPalette.Name(State)}　位置来源：{SourceText(PositionSource)}";

    public static string SourceText(EquipPositionSource s) => s switch
    {
        EquipPositionSource.Track => "真轨（图上线）",
        EquipPositionSource.PanelChain => "台账质心 + 同带邻幅反推走向",
        EquipPositionSource.AxisDefault => "台账质心 + 轴对齐（走向为缺省，形态为近似）",
        _ => "未定位",
    };
}

/// <summary>
/// 留条的**类别**。判据靠它判「有没有说清楚」，<b>不靠在 Notes 文本里搜关键字</b> ——
/// 搜关键字的判据会在有人改一个字的文案时静默变绿，而那正是它唯一要抓的东西。
/// </summary>
public enum EquipNoteKind
{
    /// <summary>本期一行指派都没有。</summary>
    NoAssignment = 0,
    /// <summary>有单元定位不到，那几台没画。</summary>
    UnlocatedUnits = 1,
    /// <summary>有行身份不全被拒收。</summary>
    RejectedRows = 2,
    /// <summary>有符号的走向是缺省值（轴对齐），形态为近似。</summary>
    ApproxHeading = 3,
    /// <summary>真轨的可用情况（有几条 / 一条都没有）。</summary>
    TrackAvailability = 4,
    /// <summary>入库失败 / 异常。</summary>
    BuildFailed = 5,
    /// <summary>同单元设备排开超出了单元长度（显示排布，非真实站位）。</summary>
    LayoutOverflow = 6,
    /// <summary>单元位置数据本身的降级（缺 ZHi 等）。</summary>
    SiteDegraded = 7,
}

/// <summary>一次「摆设备」的结果。界面照它写状态栏，判据照它对账。</summary>
public sealed class EquipStageResult
{
    /// <summary>期次键（<c>2026-08</c>）。</summary>
    public string PeriodKey = "";
    /// <summary>喂进来的行数。</summary>
    public int FedRows;
    /// <summary>真正摆出符号的行数。</summary>
    public int Placed => Symbols.Count;
    /// <summary>因为定位不到而跳过的行数（<b>不画</b>，不是画在原点）。</summary>
    public int SkippedNoPosition;
    /// <summary>因为身份不合法（无 MachineId / 无 UnitId）而拒收的行数。</summary>
    public int Rejected;
    /// <summary>
    /// 已经算好位置、但因为**入库失败**而最终没有画出来的台数。
    /// <para><b>单独记一列</b>：这批设备既不是「定位不到」也不是「拒收」，
    /// 不给它一列的话，一旦建体全失败，「摆出 + 未定位 + 拒收 = 喂进来」这条账就凭空缺一大块，
    /// 而缺的原因说不清楚 —— 整段丢弃必须记账。</para>
    /// </summary>
    public int DroppedOnBuildFailure;
    /// <summary>建成的实体数（= 用到的类别数，每类一个合批实体）。</summary>
    public int Entities;
    /// <summary>建体前清掉的旧实体数。</summary>
    public int Deleted;
    /// <summary>总顶点 / 三角数（一次性建体的量）。</summary>
    public int VertexTotal, TriangleTotal;

    public readonly List<EquipSymbol> Symbols = new();
    public readonly List<ulong> Handles = new();
    /// <summary>逐类符号数（图例上显示的就是它）。</summary>
    public readonly Dictionary<EquipKind, int> ByKind = new();
    /// <summary>逐状态符号数。</summary>
    public readonly Dictionary<EquipState, int> ByState = new();
    /// <summary>位置来源分布 —— <b>AxisDefault 占比高就说明图上大半是近似形态</b>，必须显示。</summary>
    public readonly Dictionary<EquipPositionSource, int> BySource = new();

    /// <summary>替用户做的每一个决定 / 每一次降级 / 每一行丢弃。<b>一条都不吞</b>。</summary>
    public readonly List<string> Notes = new();

    /// <summary>已经留过条的类别（判据判这个，不判 <see cref="Notes"/> 的文本）。</summary>
    public readonly HashSet<EquipNoteKind> NoteKinds = new();

    /// <summary>加一条留条并登记类别。<b>只有走这个口子加的条才算数</b>。</summary>
    public void Note(EquipNoteKind kind, string text)
    {
        Notes.Add(text);
        NoteKinds.Add(kind);
    }

    public bool Ok => Entities > 0;

    /// <summary>轴对齐（走向为缺省）的符号数 —— 界面必须标「形态为近似」的那部分。</summary>
    public int ApproxShapeCount => BySource.TryGetValue(EquipPositionSource.AxisDefault, out var n) ? n : 0;

    public string Summary
    {
        get
        {
            if (FedRows == 0)
                return "设备符号：本期没有任何设备指派 —— 图上一个符号都没画。";
            var bits = new List<string> { $"摆出 {Placed}/{FedRows} 个设备符号（{Entities} 个合批实体，图层 {EquipmentStage.Layer}）" };
            if (Deleted > 0) bits.Add($"先清旧符号 {Deleted} 个实体");
            if (SkippedNoPosition > 0) bits.Add($"⚠ {SkippedNoPosition} 台定位不到，未画");
            if (Rejected > 0) bits.Add($"⚠ {Rejected} 行身份不全，已拒收");
            if (DroppedOnBuildFailure > 0) bits.Add($"⚠ {DroppedOnBuildFailure} 台已定位但入库失败，未画");
            if (ApproxShapeCount > 0) bits.Add($"◆ {ApproxShapeCount} 台走向为缺省值（轴对齐），形态为近似");
            bits.Add($"顶点 {VertexTotal:#,0} / 三角 {TriangleTotal:#,0}");
            return string.Join("　·　", bits);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  主类
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 设备符号台 —— 把一期的设备指派摆成三维符号，落在**独立图层** <see cref="Layer"/>。
///
/// <para><b>用法（三步，都在 UI 线程）</b>：
/// <code>
/// var stage = EquipmentStage.Current;
/// stage.SetUnitSites(sites);                      // ① 台账兜底位置（可选；真轨优先）
/// var res = stage.Stage(rows, "2026-08");         // ② 建体：显式动作，不在播放路径上
/// stage.ApplyFrame(frame);                        // ③ 每帧：只比期次、不发 P/Invoke
/// stage.Clear();                                  // 收工：只删自己那一层
/// </code></para>
///
/// <para><b>本类不主动去任何地方拉数据。</b> 指派必须由调用方喂进来 ——
/// 「界面上不许出现一个编出来的数」的直接落实：没有指派就是没有符号，
/// 绝不会「每个作业面摆一台铲」冒充排产结果。</para>
/// </summary>
public sealed class EquipmentStage
{
    /// <summary>设备符号专用图层。<b>别往里放别的东西</b>，清除是按图层整批删的。</summary>
    public const string Layer = "__SIM_EQUIP__";

    // ── 单例 ──
    private static EquipmentStage? _current;
    private static readonly object _lock = new();

    /// <summary>全局台（一个图层只能有一个主人）。</summary>
    public static EquipmentStage Current
    {
        get
        {
            if (_current != null) return _current;
            lock (_lock) { _current ??= new EquipmentStage(); return _current; }
        }
    }

    /// <summary>换掉全局台（判据/测试用）。会先把旧台的符号清干净。</summary>
    public static void Bind(EquipmentStage? stage)
    {
        lock (_lock)
        {
            try { _current?.Clear(); } catch { }
            _current = stage;
        }
    }

    // ── 显示参数 ──

    /// <summary>
    /// 符号总长（m）。<b>这是显示尺度，不是设备外廓尺寸</b> —— 界面上必须这么写。
    /// 缺省 30 m：采掘带宽 W 缺省 40 m（<c>MiningModelPlanner.Options.StripWidthM</c>），
    /// 取略小于一个带宽，让符号在带上看得见又不跨带。
    /// </summary>
    public double SymbolLengthM { get; set; } = 30.0;

    /// <summary>状态标记的高度倍数（1.0 = 缺省，杆高约 0.34×符号长）。</summary>
    public double MarkerScale { get; set; } = 1.0;

    /// <summary>符号离地抬升（m），避免与台阶面 z-fight。</summary>
    public double ZLiftM { get; set; } = 0.5;

    /// <summary>同一个单元上多台设备的符号间距（× 符号长）。</summary>
    public double SpacingFactor { get; set; } = 1.6;

    /// <summary>
    /// 样式源实体 handle。<b>0 = 不取样式源</b>（缺省）。
    /// <para>内核对 srcHandle 取不到实体时会退回「basePoint = 第一个顶点、底色白」
    /// （xllAcEd.cpp:8722-8730），而本包**每个顶点都给了真实色**、且
    /// <c>shadingModeOverride = VertexColor</c>，底色用不上 ——
    /// 所以设备符号<b>不需要文档里先有一张现状面</b>。
    /// 这一点和 <see cref="SolidGeometryPort"/> 不同：那边要靠源面继承 basePoint 与透明度。
    /// 想让符号继承某张面的透明度，把那张面的 handle 塞进来即可。</para>
    /// </summary>
    public ulong StyleSourceHandle { get; set; }

    // 【故意没有 MultiPeriodStaging 这个开关】
    // 「同时摆多个期次、靠 SetEntitiesVisible 切显隐」这条路本包**没有实现**，
    // 理由在文件头的移动方案 B 里（setVisible 不标脏 + 逐个 P/Invoke）。
    // 留一个默认关的开关看起来更"周到"，但那是在承诺一个没验证过的能力 ——
    // 早晚有人打开它，然后花一下午查"为什么切了没反应"。要它就得先把标脏那条路解决掉。

    // ── 状态 ──

    private readonly Dictionary<string, EquipUnitSite> _sites = new(StringComparer.Ordinal);
    private readonly List<string> _siteNotes = new();
    private EquipStageResult? _last;
    private string _frameNote = "";

    /// <summary>已摆的那一期（空 = 还没摆过）。</summary>
    public string StagedPeriod => _last?.PeriodKey ?? "";

    /// <summary>上一次摆位的结果（null = 还没摆过）。</summary>
    public EquipStageResult? Last => _last;

    private IEntityCapability? Ent => SimHost.Entities;

    /// <summary>能建符号吗（宿主实体能力在不在）。</summary>
    public bool Available => Ent != null;

    /// <summary>状态一行（界面直接显示）。</summary>
    public string StatusLabel
    {
        get
        {
            if (Ent == null)
                return "设备符号：未注入实体能力 IEntityCapability（在 TaskLibPlugin.Initialize 调 SimHost.Inject 即可启用）";
            if (_last == null)
                return $"设备符号：可用（图层 {Layer}），本会话还没摆过 —— 需要先喂一期设备指派";
            string s = $"设备符号【{_last.PeriodKey}】{_last.Summary}";
            return _frameNote.Length > 0 ? s + "　" + _frameNote : s;
        }
    }

    /// <summary>期次键的唯一写法（与 <c>SimFrame.Period</c> 的月粒度标签对齐）。</summary>
    public static string PeriodKey(int year, int month) =>
        year <= 0 || month <= 0 ? "" : $"{year:0000}-{month:00}";

    // ── ① 台账兜底位置 ────────────────────────────────────────────────────

    /// <summary>
    /// 喂单元的台账位置（<b>兜底</b>：真轨查得到时不用它）。重复喂 = 覆盖。
    /// </summary>
    public void SetUnitSites(IEnumerable<EquipUnitSite>? sites)
    {
        _sites.Clear();
        _siteNotes.Clear();
        if (sites == null) return;
        int bad = 0;
        foreach (var s in sites)
        {
            if (s == null || string.IsNullOrWhiteSpace(s.UnitId)) { bad++; continue; }
            if (double.IsNaN(s.Cx) || double.IsNaN(s.Cy) || double.IsInfinity(s.Cx) || double.IsInfinity(s.Cy))
            { bad++; continue; }
            _sites[s.UnitId.Trim()] = s;
        }
        if (bad > 0) _siteNotes.Add($"单元位置：{bad} 条无 UnitId 或坐标非法，已丢弃（不拿 0 冒充坐标）。");
    }

    /// <summary>
    /// 从 <c>MiningUnitLedger.Row</c> 直接灌单元位置（最常用的一条）。
    /// <para><b>Z 取 <c>ZHi</c> 不取 <c>Cz</c></b>：<c>Cz</c> 是体的中心高度，设备是站在**台阶顶**上的。
    /// <c>ZHi</c> 非法时退 <c>Cz</c> 并留条 —— 那是「没有坡顶标高」，不是「坡顶在 0 米」。</para>
    /// </summary>
    public void SetUnitSitesFromLedger(IEnumerable<MiningUnitLedger.Row>? rows)
    {
        if (rows == null) { SetUnitSites(null); return; }
        var list = new List<EquipUnitSite>();
        int zFallback = 0;
        foreach (var r in rows)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.UnitId)) continue;
            double zhi = r.ZHi;
            if (double.IsNaN(zhi) || double.IsInfinity(zhi) || zhi <= double.MinValue + 1)
            { zhi = r.Cz; zFallback++; }
            list.Add(new EquipUnitSite
            {
                UnitId = r.UnitId,
                Cx = r.Cx, Cy = r.Cy,
                ZLo = r.ZLo, ZHi = zhi,
                LengthM = r.LengthM, WidthM = r.WidthM,
                ChainKey = $"{r.Region}|{r.Seam}|{r.Band}",
                Panel = r.Panel,
            });
        }
        SetUnitSites(list);
        if (zFallback > 0)
            _siteNotes.Add($"单元位置：{zFallback} 行台账没有坡顶标高 ZHi，退用质心高 Cz 落位（设备实际站位应在台阶顶，这是降级）。");
    }

    // ── ② 摆位（显式动作，**不在播放路径上**）───────────────────────────

    /// <summary>
    /// 把一期的设备指派摆成符号。<b>先清本图层的旧符号，再建</b>（同一位置不叠两份）。
    /// </summary>
    /// <param name="rows">设备指派。<b>null / 空 = 什么都不画</b>，并在结果里说清为什么。</param>
    /// <param name="periodKey">期次键（<see cref="PeriodKey(int,int)"/>）。</param>
    public EquipStageResult Stage(IReadOnlyList<EquipPlacementRow>? rows, string periodKey)
    {
        var res = new EquipStageResult { PeriodKey = (periodKey ?? "").Trim(), FedRows = rows?.Count ?? 0 };
        foreach (var n in _siteNotes) res.Note(EquipNoteKind.SiteDegraded, n);

        var ent = Ent;
        if (ent == null)
        {
            res.Note(EquipNoteKind.BuildFailed, "未注入 IEntityCapability —— 设备符号建不出来（数据层不受影响）。");
            _last = res;
            return res;
        }

        // 先清旧的（本图层，不碰别人的层）
        try { res.Deleted = ClearInternal(ent); }
        catch (Exception ex) { res.Note(EquipNoteKind.BuildFailed, $"清旧符号异常（{ex.GetType().Name}），继续建新的。"); }

        if (rows == null || rows.Count == 0)
        {
            res.Note(EquipNoteKind.NoAssignment, NoAssignmentNote(periodKey));
            _last = res;
            return res;
        }

        // ── 定位 ──
        var track = EquipTrackIndex.Build();
        foreach (var n in track.Notes) res.Note(EquipNoteKind.TrackAvailability, n);

        // 同一单元上的多台设备要沿走向排开：先按单元分组，组内排稳定序
        var byUnit = new Dictionary<string, List<EquipPlacementRow>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            if (r == null || string.IsNullOrWhiteSpace(r.MachineId) || string.IsNullOrWhiteSpace(r.UnitId))
            { res.Rejected++; continue; }
            string u = r.UnitId.Trim();
            if (!byUnit.TryGetValue(u, out var l)) byUnit[u] = l = new List<EquipPlacementRow>();
            l.Add(r);
        }
        if (res.Rejected > 0)
            res.Note(EquipNoteKind.RejectedRows,
                $"{res.Rejected} 行没有设备编号或没有单元号，已拒收 —— 身份不全的行画出来也追不回是谁。");

        // 逐类一个缓冲（每类一个合批实体：8 次入库封顶，而不是每台一次）
        var bufs = new Dictionary<EquipKind, EquipMeshBuffer>();
        var missing = new List<string>();

        foreach (var kv in byUnit.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var place = ResolveUnit(kv.Key, track);
            if (place == null)
            {
                res.SkippedNoPosition += kv.Value.Count;
                if (missing.Count < 12) missing.Add(kv.Key);
                continue;
            }

            // 组内稳定序：挖装在前（Working），其后按 Seq、再按设备号
            var ordered = kv.Value
                .OrderBy(r => r.State == EquipState.Working ? 0 : r.State == EquipState.Hauling ? 1 : 2)
                .ThenBy(r => r.Seq)
                .ThenBy(r => r.MachineId, StringComparer.Ordinal)
                .ToList();

            // 沿走向、以质心 / 轨点为中心排开。
            // 单元装不下时**先压间距**（压到符号刚好不重叠为止），压不下再如实报超出 ——
            // 让符号跑到单元外面去，看图的人会以为设备在别的地方。
            // ⚠ 真轨来源同样要查：真轨的走向长是从折线算出来的，一样有装不下的问题
            //   （旧版把 Track 排除在外，于是 4 台设备排开 144 m 塞进 100 m 的幅里也不报）。
            double step = SymbolLengthM * (SpacingFactor > 0 ? SpacingFactor : 1.6);
            int gaps = Math.Max(0, ordered.Count - 1);
            double len = place.Value.LengthM;
            bool squeezed = false;
            if (gaps > 0 && len > 1e-6)
            {
                double fit = len / gaps;
                if (fit < step) { step = Math.Max(SymbolLengthM, fit); squeezed = true; }
            }
            double span = step * gaps;
            double t0 = -span * 0.5;

            if (len > 1e-6 && span > len + 1e-6)
                res.Note(EquipNoteKind.LayoutOverflow,
                    $"{kv.Key}：{ordered.Count} 台设备沿走向排开 {span:0} m，已超出单元走向长 {len:0} m"
                  + "（间距已压到「符号不重叠」的下限，再压就叠在一起了）—— 这是**显示排布**，不是真实站位。");
            else if (squeezed)
                res.Note(EquipNoteKind.LayoutOverflow,
                    $"{kv.Key}：{ordered.Count} 台设备的符号间距被压到 {step:0} m 才放得进 {len:0} m 的单元"
                  + " —— 这是**显示排布**，不是真实站位。");

            for (int i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i];
                double t = t0 + i * step;
                double x = place.Value.X + t * Math.Cos(place.Value.HeadingRad);
                double y = place.Value.Y + t * Math.Sin(place.Value.HeadingRad);
                double z = place.Value.Z + ZLiftM;

                if (!bufs.TryGetValue(r.Kind, out var buf)) bufs[r.Kind] = buf = new EquipMeshBuffer();
                int nv = EquipSymbolLibrary.Emit(buf, r.Kind, r.State, x, y, z,
                                                 place.Value.HeadingRad, SymbolLengthM, MarkerScale);

                var sym = new EquipSymbol
                {
                    MachineId = r.MachineId, Kind = r.Kind, State = r.State,
                    UnitId = kv.Key, Model = r.Model,
                    X = x, Y = y, Z = z, HeadingRad = place.Value.HeadingRad,
                    PositionSource = place.Value.Source, PositionRef = place.Value.Reference,
                    VertexCount = nv,
                };
                res.Symbols.Add(sym);
                Bump(res.ByKind, r.Kind);
                Bump(res.ByState, r.State);
                Bump(res.BySource, place.Value.Source);
            }
        }

        if (res.SkippedNoPosition > 0)
            res.Note(EquipNoteKind.UnlocatedUnits,
                $"{res.SkippedNoPosition} 台设备所在的单元在真轨和台账里都查不到位置，**没有画** —— "
              + $"举例：{string.Join("、", missing)}{(missing.Count >= 12 ? " …" : "")}。"
              + "（画在原点就是编位置。要让它们出现，请把这些单元的台账行喂进 SetUnitSitesFromLedger。）");

        if (res.ApproxShapeCount > 0)
            res.Note(EquipNoteKind.ApproxHeading,
                $"{res.ApproxShapeCount} 台设备所在的单元既没有真轨、同带也只有一幅，走向取了缺省值（+X 轴）—— "
              + "这些符号的**朝向是近似的**，位置是真的。界面上要标「形态为近似」。");

        // ── 建体：每类一个实体 ──
        var failedKinds = new List<EquipKind>();
        foreach (var kv in bufs.OrderBy(k => (int)k.Key))
        {
            var buf = kv.Value;
            if (buf.IsEmpty) continue;
            ulong h = 0;
            try
            {
                // lift=0：顶点已带绝对高程，抬升由 ZLiftM 在几何里做完了
                h = ent.BuildColoredMeshOnLayer(StyleSourceHandle, buf.WorldXyz, buf.Triangles, buf.VertexRgb, 0.0, Layer);
            }
            catch (Exception ex)
            {
                res.Note(EquipNoteKind.BuildFailed, $"{EquipPalette.Name(kv.Key)}：符号入库异常（{ex.GetType().Name}: {ex.Message}）");
            }
            if (h == 0)
            {
                res.Note(EquipNoteKind.BuildFailed,
                    $"{EquipPalette.Name(kv.Key)}：符号入库失败（引擎返回 handle 0），这一类没有画出来。");
                failedKinds.Add(kv.Key);
                continue;
            }
            res.Handles.Add(h);
            res.Entities++;
            res.VertexTotal += buf.VertexCount;
            res.TriangleTotal += buf.TriangleCount;
        }

        // 入库失败的那几类，符号台账要跟着退 —— 不然「符号数 = 指派数」会在图上根本没这几类的情况下报绿。
        // 退掉的台数记进 DroppedOnBuildFailure，让 C1 的账仍然合得上（整段丢弃必须记账）。
        if (failedKinds.Count > 0)
        {
            var drop = new HashSet<EquipKind>(failedKinds);
            int n = res.Symbols.RemoveAll(s => drop.Contains(s.Kind));
            res.DroppedOnBuildFailure += n;
            foreach (var k in drop) { res.ByKind.Remove(k); }
            // 状态 / 来源分布按剩下的符号重算，别留半份旧账
            res.ByState.Clear(); res.BySource.Clear();
            foreach (var s in res.Symbols) { Bump(res.ByState, s.State); Bump(res.BySource, s.PositionSource); }
            res.Note(EquipNoteKind.BuildFailed,
                $"入库失败的 {drop.Count} 类共 {n} 台已从符号台账里退掉 —— 图上没有它们，账上也不许有。");
        }

        try { SimHost.View?.RequestRender(); } catch { }
        _last = res;
        _frameNote = "";
        return res;
    }

    private static string NoAssignmentNote(string? periodKey)
        => $"【{(string.IsNullOrWhiteSpace(periodKey) ? "本期" : periodKey)}】没有喂进来任何设备指派 —— 图上一个符号都没画。"
         + "不摆缺省设备是刻意的：「每个作业面摆一台铲」看上去像排产结果，其实是编的。"
         + "　设备指派来自 MineAssLib.Driving.EquipmentAssigner；注意它**只排挖装（电铲/前装机）+ 与之绑定的卡车**，"
         + "钻机 / 推土机 / 平路机 / 洒水车它不排（EquipmentAssignInput 的类注释写了原因：前者的穿爆先后没建模，"
         + "后三者在实测台效表里一条记录都没有）—— 所以这四类**永远不会**自己出现在图上，"
         + "要画它们必须另有数据源（设备台账 / 人工排班），由调用方喂进来。";

    // ── ③ 每帧（**不发 P/Invoke**）──────────────────────────────────────

    /// <summary>
    /// 帧回调。<b>缺省什么都不做</b>，只比对期次并更新状态文案 —— 摆位是月粒度的静态摆位，
    /// 帧预算占用 ≈ 0（无 P/Invoke、无分配路径上的建/删）。
    /// <para>期次对不上时**如实报**，绝不默默让上个月的设备冒充本月的。</para>
    /// </summary>
    public void ApplyFrame(SimFrame? frame)
    {
        if (_last == null || frame == null) { _frameNote = ""; return; }
        string p = (frame.Period ?? "").Trim();
        if (p.Length == 0 || string.Equals(p, _last.PeriodKey, StringComparison.Ordinal))
        { _frameNote = ""; return; }

        _frameNote = $"⚠ 当前期次【{frame.Label}】与图上已摆的【{_last.PeriodKey}】不是同一期 —— "
                   + "图上是后者的设备。要看本期请重新喂本期的指派再摆一次。";
    }

    // ── 清除 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 清掉本图层的全部符号（含上次会话残留）。<b>只删 <see cref="Layer"/></b>，
    /// 一个别人的图层都不碰。幂等。返回删掉的实体数。
    /// </summary>
    public int Clear()
    {
        var ent = Ent;
        if (ent == null) { _last = null; _frameNote = ""; return 0; }
        int n = ClearInternal(ent);
        _last = null;
        _frameNote = "";
        return n;
    }

    private int ClearInternal(IEntityCapability ent)
    {
        var hs = new List<ulong>();
        try { var arr = ent.GetHandlesByLayer(Layer); if (arr != null) hs.AddRange(arr); } catch { }
        if (_last != null) foreach (var h in _last.Handles) if (h != 0 && !hs.Contains(h)) hs.Add(h);
        if (hs.Count == 0) return 0;
        int n = 0;
        try { n = ent.DeleteEntities(hs.Distinct().ToArray()); } catch { }
        try { SimHost.View?.RequestRender(); } catch { }
        return n;
    }

    // ── 定位 ────────────────────────────────────────────────────────────

    private readonly struct Placement
    {
        public Placement(double x, double y, double z, double heading,
                         EquipPositionSource src, string reference, double lengthM)
        { X = x; Y = y; Z = z; HeadingRad = heading; Source = src; Reference = reference; LengthM = lengthM; }
        public readonly double X, Y, Z, HeadingRad, LengthM;
        public readonly EquipPositionSource Source;
        public readonly string Reference;
    }

    /// <summary>
    /// 单元 → 落位点 + 走向。三级：真轨 → 台账质心+邻幅反推走向 → 台账质心+轴对齐。
    /// 都查不到返回 null（<b>不造位置</b>）。
    /// </summary>
    private Placement? ResolveUnit(string unitId, EquipTrackIndex track)
    {
        // ① 真轨（本会话的采矿模型 / 排土条带）
        if (track.TryGet(unitId, out var tr))
            return new Placement(tr.X, tr.Y, tr.Z, tr.HeadingRad, EquipPositionSource.Track, tr.Reference, tr.LengthM);

        // ② / ③ 台账
        if (!_sites.TryGetValue(unitId, out var s)) return null;

        double z = s.ZHi;
        if (double.IsNaN(z) || double.IsInfinity(z)) z = s.ZLo;
        if (double.IsNaN(z) || double.IsInfinity(z)) return null;   // 没有高程就是没有，不落 0

        // ② 同带邻幅连线反推走向 —— 这是从真数据推出来的，不是拍的
        if (TryChainHeading(s, out double h))
            return new Placement(s.Cx, s.Cy, z, h, EquipPositionSource.PanelChain, $"台账 {unitId}（走向由同带邻幅连线反推）", s.LengthM);

        // ③ 轴对齐：走向取 +X（东）。这是**缺省值**，界面必须标「形态为近似」
        return new Placement(s.Cx, s.Cy, z, 0.0, EquipPositionSource.AxisDefault, $"台账 {unitId}", s.LengthM);
    }

    /// <summary>
    /// 用同一条带（同 Region/Seam/Band）里相邻幅的质心连线反推走向。
    /// 只有一幅、或邻幅质心重合时返回 false（那就没有可反推的方向，别硬凑）。
    /// </summary>
    private bool TryChainHeading(EquipUnitSite s, out double headingRad)
    {
        headingRad = 0;
        if (string.IsNullOrWhiteSpace(s.ChainKey)) return false;

        EquipUnitSite? prev = null, next = null;
        foreach (var o in _sites.Values)
        {
            if (!string.Equals(o.ChainKey, s.ChainKey, StringComparison.Ordinal)) continue;
            if (o.Panel == s.Panel - 1) prev = o;
            else if (o.Panel == s.Panel + 1) next = o;
        }

        double dx, dy;
        if (prev != null && next != null) { dx = next.Cx - prev.Cx; dy = next.Cy - prev.Cy; }
        else if (next != null) { dx = next.Cx - s.Cx; dy = next.Cy - s.Cy; }
        else if (prev != null) { dx = s.Cx - prev.Cx; dy = s.Cy - prev.Cy; }
        else return false;

        if (dx * dx + dy * dy < 1e-6) return false;
        headingRad = Math.Atan2(dy, dx);
        return true;
    }

    private static void Bump<T>(Dictionary<T, int> d, T k) where T : notnull
        => d[k] = d.TryGetValue(k, out var v) ? v + 1 : 1;

    // ── 在籍未派（待命 / 检修）───────────────────────────────────────────

    /// <summary>
    /// 把「在籍但本月没派到活」的设备补成 <see cref="EquipState.Standby"/> 行。
    ///
    /// <para><b>这不是排产结果，是差集</b>：<paramref name="fleetMachineIds"/> 里有、
    /// <paramref name="assigned"/> 里没有的那些。<b>它们没有单元</b> —— 待命设备本来就不在作业面上，
    /// 所以本方法要求调用方给一个停放点单元 <paramref name="parkUnitId"/>；给不出来就一台都不补，
    /// 并返回一条说明。<b>绝不把待命设备摆到某个作业面上冒充在干活。</b></para>
    /// </summary>
    /// <returns>补出来的行（可能是空列表）。<paramref name="note"/> 一定非空，调用方要显示。</returns>
    public static List<EquipPlacementRow> FeedIdleFleet(
        IEnumerable<(string MachineId, EquipKind Kind, string Model, bool Dispatchable)>? fleetMachineIds,
        IReadOnlyList<EquipPlacementRow>? assigned,
        string parkUnitId, string sourceNote, out string note)
    {
        var outv = new List<EquipPlacementRow>();
        if (fleetMachineIds == null)
        {
            note = "待命 / 检修：没有喂在籍设备清单 —— 图上不会出现任何待命或检修设备。"
                 + "（EquipmentAssigner 的结果里没有这两种状态，它只输出被派上活的那些。）";
            return outv;
        }
        if (string.IsNullOrWhiteSpace(parkUnitId))
        {
            note = "待命 / 检修：喂了在籍清单但没给停放点单元号 —— 一台都没补。"
                 + "待命设备不在作业面上，把它摆到某个采掘单元上就是编位置。";
            return outv;
        }

        var busy = new HashSet<string>(
            (assigned ?? Array.Empty<EquipPlacementRow>()).Select(a => a.MachineId ?? ""), StringComparer.Ordinal);

        int standby = 0, maint = 0;
        foreach (var m in fleetMachineIds)
        {
            if (string.IsNullOrWhiteSpace(m.MachineId)) continue;
            if (busy.Contains(m.MachineId)) continue;
            var st = m.Dispatchable ? EquipState.Standby : EquipState.Maintenance;
            if (st == EquipState.Standby) standby++; else maint++;
            outv.Add(new EquipPlacementRow
            {
                MachineId = m.MachineId, Kind = m.Kind, Model = m.Model ?? "",
                UnitId = parkUnitId, State = st,
                SourceNote = string.IsNullOrWhiteSpace(sourceNote) ? "在籍清单差集（非排产结果）" : sourceNote,
            });
        }

        note = $"待命 / 检修：按「在籍清单 − 本月已派」的差集补出 {standby} 台待命、{maint} 台检修，"
             + $"全部摆在停放点单元「{parkUnitId}」上。**这不是排产结果**，"
             + "检修与否取自设备台账的可派标志，不是算出来的。";
        return outv;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  真轨索引
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 本会话的**真轨**索引：采矿模型的采掘带（<c>MiningModelStore.Coal/Rock</c>）+
/// 排土条带的位置（<c>DumpStripStore.Last</c>）。
///
/// <para><b>只读这两个静态交接点，不落盘、不缓存跨会话</b>：它们本来就只在会话内有效
/// （见 <c>MiningModelStore</c> 类注释），缓存一份就会拿陈的轨去摆设备。</para>
///
/// <para><b>UnitId 的拼法必须和台账一致</b>：采场 = <c>{SeamCode}-B{BandId}-P{PanelIndex}</c>
/// （与 <c>MiningUnitLedger.FromStrips</c> 同一行代码的口径）；排土 = <c>Cell.Code</c>。
/// 两处各拼一次就会漂，所以这里逐字照抄那边的写法，改了那边这里必须跟着改 ——
/// <see cref="EquipmentStageCheck"/> 有一条专门验这个（真轨命中数为 0 就报）。</para>
/// </summary>
internal sealed class EquipTrackIndex
{
    internal readonly struct Hit
    {
        public Hit(double x, double y, double z, double heading, string reference, double lengthM)
        { X = x; Y = y; Z = z; HeadingRad = heading; Reference = reference; LengthM = lengthM; }
        public readonly double X, Y, Z, HeadingRad, LengthM;
        public readonly string Reference;
    }

    private readonly Dictionary<string, Hit> _map = new(StringComparer.Ordinal);
    public readonly List<string> Notes = new();

    /// <summary>真轨命中的单元数（判据用）。</summary>
    public int Count => _map.Count;

    public bool TryGet(string unitId, out Hit hit) => _map.TryGetValue(unitId ?? "", out hit);

    public static EquipTrackIndex Build()
    {
        var idx = new EquipTrackIndex();
        int strips = 0, cells = 0;

        try
        {
            AddStrips(idx, MiningModelStore.Coal, ref strips);
            AddStrips(idx, MiningModelStore.Rock, ref strips);
        }
        catch (Exception ex) { idx.Notes.Add($"真轨：读采矿模型交接点异常（{ex.GetType().Name}），本次只能走台账质心。"); }

        try
        {
            var last = DumpStripStore.Last;
            if (last != null)
            {
                foreach (var c in last.Cells)
                {
                    if (c == null || string.IsNullOrWhiteSpace(c.Code)) continue;
                    if (!Mid(c.CrestXyz, out double x, out double y, out double z, out double h, out double len)) continue;
                    idx._map[c.Code] = new Hit(x, y, double.IsNaN(c.CrestZ) ? z : c.CrestZ, h,
                                               $"排土条带 {c.Code}（前脸坡顶轨）", len);
                    cells++;
                }
            }
        }
        catch (Exception ex) { idx.Notes.Add($"真轨：读排土条带交接点异常（{ex.GetType().Name}），本次只能走台账质心。"); }

        if (strips + cells == 0)
            idx.Notes.Add("真轨：本会话没有生成过采矿模型 / 排土条带，一条真轨都没有 —— "
                        + "设备将全部落在台账质心上，走向为缺省值（形态为近似）。");
        else
            idx.Notes.Add($"真轨：采掘带 {strips} 条 + 排土位置 {cells} 个（会话内，未持久化）。");

        return idx;
    }

    private static void AddStrips(EquipTrackIndex idx, MiningModelStore.Snapshot? snap, ref int n)
    {
        if (snap == null) return;
        foreach (var s in snap.Strips)
        {
            if (s == null) continue;
            // ⚠ 与 MiningUnitLedger.FromStrips 的 UnitId 拼法逐字一致
            string id = MiningUnitLedger.StripUnitId(s.SeamCode, s.BandId, s.PanelIndex);
            if (!Mid(s.CrestXyz, out double x, out double y, out double z, out double h, out double len)) continue;
            idx._map[id] = new Hit(x, y, z, h, $"采矿模型 {id}（前脸顶板轨）", len);
            n++;
        }
    }

    /// <summary>
    /// 折线中点 + 该处切向。点数 &lt; 2 或全部重合时返回 false（<b>没有方向就说没有</b>）。
    /// </summary>
    private static bool Mid(double[]? xyz, out double x, out double y, out double z,
                            out double headingRad, out double lengthM)
    {
        x = y = z = headingRad = lengthM = 0;
        if (xyz == null || xyz.Length < 6 || xyz.Length % 3 != 0) return false;
        int n = xyz.Length / 3;
        for (int i = 0; i < xyz.Length; i++)
            if (double.IsNaN(xyz[i]) || double.IsInfinity(xyz[i])) return false;

        int m = n / 2;
        x = xyz[3 * m]; y = xyz[3 * m + 1]; z = xyz[3 * m + 2];

        int a = Math.Max(0, m - 1), b = Math.Min(n - 1, m + 1);
        double dx = xyz[3 * b] - xyz[3 * a], dy = xyz[3 * b + 1] - xyz[3 * a + 1];
        if (dx * dx + dy * dy < 1e-9)
        { dx = xyz[3 * (n - 1)] - xyz[0]; dy = xyz[3 * (n - 1) + 1] - xyz[1]; }
        if (dx * dx + dy * dy < 1e-9) return false;
        headingRad = Math.Atan2(dy, dx);

        for (int i = 0; i + 1 < n; i++)
        {
            double ex = xyz[3 * i + 3] - xyz[3 * i], ey = xyz[3 * i + 4] - xyz[3 * i + 1];
            lengthM += Math.Sqrt(ex * ex + ey * ey);
        }
        return true;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  上游适配（反射 —— 因为 TaskLib 没有 MineAssLib 的项目引用）
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// <c>MineAssLib.Driving.EquipmentAssignResult</c> → <see cref="EquipPlacementRow"/>。
///
/// <para><b>为什么走反射（这是降级，说清楚）</b>：<c>Modules/TaskLib/TaskLib.csproj</c> 里
/// <b>没有</b> MineAssLib 的 ProjectReference（只引了 Platform / GeoDataBase / RoadLib / BlockModelLib），
/// 所以 <c>MachineAssignment</c> 在 TaskLib 里**编译期不可见**。
/// 加引用是无环的（MineAssLib 不引 TaskLib），但那是构建图的改动，不在本任务的改动面内 ——
/// 由接线的人决定。加上之后，把本类换成十行的强类型适配即可，
/// <see cref="EquipmentStage"/> 一个字都不用改（它只认中性 POCO）。</para>
///
/// <para><b>反射不许静默</b>：字段名对不上、枚举名不认识，一律**丢弃该行 + 记一条**，
/// 绝不给一个缺省值蒙混过去。上游改名时判据会立刻红（映射成功数 = 0），而不是悄悄少一半设备。</para>
/// </summary>
public static class EquipmentStageAdapter
{
    /// <summary>
    /// 从 <c>EquipmentAssignResult</c>（或任何带 <c>Assignments</c> 集合的对象）取指派行。
    /// </summary>
    /// <param name="assignResult">上游结果对象。null → 空列表 + 一条说明。</param>
    /// <param name="sourceNote">溯源（"EquipmentAssigner 2026-08"）。</param>
    /// <param name="notes">逐条降级说明，调用方要显示。</param>
    public static List<EquipPlacementRow> FromAssignerResult(object? assignResult, string sourceNote, out List<string> notes)
    {
        notes = new List<string>();
        var outv = new List<EquipPlacementRow>();
        if (assignResult == null)
        {
            notes.Add("设备指派：上游结果对象为 null —— 没有任何设备可画。");
            return outv;
        }

        var t = assignResult.GetType();

        // ⚠ 只认 Assignments，**绝不退到 Excavation / Haulage 这些派生视图**。
        //   EquipmentAssignResult.Assignments 是 readonly **字段**（不是属性），
        //   而 Excavation 是属性且只筛 Role==Excavate。第一版写成
        //     GetProperty("Assignments") ?? GetProperty("Excavation")
        //   于是字段查不到、静默落到 Excavation —— 运输笔与转场笔**全部消失**，
        //   而返回的列表非空、每个数看上去都正常（台架实测：4 笔只进来 2 笔）。
        //   宁可整条报失败，也不要一个「看着对」的子集。
        object? seqObj = t.GetField("Assignments")?.GetValue(assignResult)
                      ?? t.GetProperty("Assignments")?.GetValue(assignResult);
        if (seqObj is not System.Collections.IEnumerable seq)
        {
            notes.Add($"设备指派：{t.FullName} 上找不到 Assignments 集合（字段和属性都没有）—— "
                    + "上游结构变了，请改 EquipmentStageAdapter，别当成「本月没派设备」。");
            return outv;
        }

        int total = 0, badRole = 0, badKind = 0, badId = 0;
        var unknownRoles = new HashSet<string>(StringComparer.Ordinal);
        var unknownKinds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in seq)
        {
            if (item == null) continue;
            total++;
            var it = item.GetType();

            string machineId = Str(it, item, "MachineId");
            string unitId = Str(it, item, "UnitId");
            if (machineId.Length == 0 || unitId.Length == 0) { badId++; continue; }

            string roleName = EnumName(it, item, "Role");
            EquipState state;
            switch (roleName)
            {
                case "Excavate": state = EquipState.Working; break;
                case "Haul": state = EquipState.Hauling; break;
                case "Relocate": state = EquipState.Relocating; break;
                default: badRole++; unknownRoles.Add(roleName.Length == 0 ? "(读不到)" : roleName); continue;
            }

            string kindName = EnumName(it, item, "MachineKind");
            if (!Enum.TryParse<EquipKind>(kindName, ignoreCase: false, out var kind))
            { badKind++; unknownKinds.Add(kindName.Length == 0 ? "(读不到)" : kindName); kind = EquipKind.Other; }

            outv.Add(new EquipPlacementRow
            {
                MachineId = machineId,
                UnitId = unitId,
                Kind = kind,
                Model = Str(it, item, "Model"),
                State = state,
                ServesMachineId = Str(it, item, "ServesMachineId"),
                Seq = Int(it, item, "Seq"),
                StartDay = Int(it, item, "StartDay"),
                EndDay = Int(it, item, "EndDay"),
                SourceNote = string.IsNullOrWhiteSpace(sourceNote) ? "EquipmentAssigner" : sourceNote,
            });
        }

        notes.Add($"设备指派：上游 {total} 笔，取到 {outv.Count} 笔（反射映射，TaskLib 无 MineAssLib 编译期引用）。");
        if (badId > 0) notes.Add($"设备指派：{badId} 笔没有 MachineId 或 UnitId，已丢弃。");
        if (badRole > 0)
            notes.Add($"设备指派：{badRole} 笔的 Role 不认识（{string.Join("/", unknownRoles)}）已丢弃 —— "
                    + "上游 MachineRole 的成员名变了，这里要跟着改，不是补个缺省值就完事。");
        if (badKind > 0)
            notes.Add($"设备指派：{badKind} 笔的 MachineKind 不认识（{string.Join("/", unknownKinds)}），"
                    + "按「其他」画并保留 —— 类别不对但台数是真的，丢掉更糟。");
        if (total > 0 && outv.Count == 0)
            notes.Add("设备指派：上游有数据但**一笔都没映射成功** —— 字段名对不上了，别当成「本月没派设备」。");

        return outv;
    }

    private static string Str(Type t, object o, string name)
    {
        try { return (t.GetField(name)?.GetValue(o) ?? t.GetProperty(name)?.GetValue(o)) as string ?? ""; }
        catch { return ""; }
    }

    private static int Int(Type t, object o, string name)
    {
        try
        {
            object? v = t.GetField(name)?.GetValue(o) ?? t.GetProperty(name)?.GetValue(o);
            return v is int i ? i : 0;
        }
        catch { return 0; }
    }

    /// <summary>枚举成员的**名字**（不是序号）。读不到返回空串 —— 空串会被调用方当成「不认识」丢掉。</summary>
    private static string EnumName(Type t, object o, string name)
    {
        try
        {
            object? v = t.GetField(name)?.GetValue(o) ?? t.GetProperty(name)?.GetValue(o);
            if (v == null) return "";
            var vt = v.GetType();
            return vt.IsEnum ? Enum.GetName(vt, v) ?? "" : v.ToString() ?? "";
        }
        catch { return ""; }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  判据
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 设备符号的自检。<b>每一条都能证伪</b> —— 只报「成功」的判据会空过。
/// 返回空列表 = 全部通过；否则每条是一句人读的失败描述。
/// </summary>
public static class EquipmentStageCheck
{
    /// <summary>顶点预算上限（单个符号）。超了说明符号库被人改精细了，该报。</summary>
    public const int MaxVerticesPerSymbol = 200;

    /// <summary>
    /// C1–C4：摆位结果的账。
    /// </summary>
    /// <param name="res">一次 <see cref="EquipmentStage.Stage"/> 的结果。</param>
    /// <param name="ent">实体能力（给 null 就跳过「清理不留残体」那条，并留一条说明）。</param>
    public static List<string> CheckResult(EquipStageResult? res, IEntityCapability? ent = null)
    {
        var bad = new List<string>();
        if (res == null) { bad.Add("C0 摆位结果为 null —— 一次都没摆过，无法自检。"); return bad; }

        // C1 符号数 = 指派数（不许多、不许少；丢弃必须记账）
        int accounted = res.Placed + res.SkippedNoPosition + res.Rejected + res.DroppedOnBuildFailure;
        if (accounted != res.FedRows)
            bad.Add($"C1 台数对不上：喂进来 {res.FedRows} 行，摆出 {res.Placed} + 未定位 {res.SkippedNoPosition}"
                  + $" + 拒收 {res.Rejected} + 入库失败退掉 {res.DroppedOnBuildFailure} = {accounted}。"
                  + "差额说明有行被静默丢弃了。");
        if (res.Symbols.Select(s => s.MachineId + "" + s.UnitId).Distinct().Count() != res.Symbols.Count)
            bad.Add("C1 有重复符号：同一台设备在同一个单元上画了不止一次。");

        // C2 类别配色一一对应
        var seen = new Dictionary<uint, EquipKind>();
        foreach (var k in EquipPalette.AllKinds)
        {
            uint c = EquipPalette.Rgb(k);
            if (seen.TryGetValue(c, out var other))
                bad.Add($"C2 配色撞车：{EquipPalette.Name(k)} 与 {EquipPalette.Name(other)} 都是 #{c:X6}，图上分不出来。");
            else seen[c] = k;
        }
        if (res.ByKind.Values.Sum() != res.Placed)
            bad.Add($"C2 逐类台数合计 {res.ByKind.Values.Sum()} ≠ 符号数 {res.Placed} —— 有符号没被计进任何类别。");

        // C3 无指派 / 有降级时必须留条。
        //    ⚠ 判的是**留条类别**（NoteKinds），不是在 Notes 文本里搜关键字 ——
        //      搜关键字的写法在别人改一个字的文案时会静默变绿（本判据自己踩过一次）。
        if (res.FedRows == 0 && !res.NoteKinds.Contains(EquipNoteKind.NoAssignment))
            bad.Add("C3 一行指派都没有，却没留 NoAssignment 条 —— 用户只会看到一张空图，不知道是「没派设备」还是「画挂了」。");
        if (res.SkippedNoPosition > 0 && !res.NoteKinds.Contains(EquipNoteKind.UnlocatedUnits))
            bad.Add($"C3 有 {res.SkippedNoPosition} 台没画出来，却没留 UnlocatedUnits 条。");
        if (res.Rejected > 0 && !res.NoteKinds.Contains(EquipNoteKind.RejectedRows))
            bad.Add($"C3 有 {res.Rejected} 行被拒收，却没留 RejectedRows 条。");
        if (res.DroppedOnBuildFailure > 0 && !res.NoteKinds.Contains(EquipNoteKind.BuildFailed))
            bad.Add($"C3 有 {res.DroppedOnBuildFailure} 台因入库失败没画出来，却没留 BuildFailed 条。");
        if (res.ApproxShapeCount > 0 && !res.NoteKinds.Contains(EquipNoteKind.ApproxHeading))
            bad.Add($"C3 有 {res.ApproxShapeCount} 台走向是缺省值，却没留 ApproxHeading 条（界面上会看不到「形态为近似」）。");
        // 留条类别登记了，文本却是空的 —— 那等于没留
        if (res.NoteKinds.Count > 0 && res.Notes.Count == 0)
            bad.Add("C3 登记了留条类别但一条文本都没有。");

        // C4 位置不是编的：每个符号都得指得出出处
        foreach (var s in res.Symbols)
        {
            if (s.PositionSource == EquipPositionSource.None)
            { bad.Add($"C4 {s.MachineId} 的位置来源是「未定位」却被画出来了 —— 那个坐标是编的。"); break; }
            if (string.IsNullOrWhiteSpace(s.PositionRef))
            { bad.Add($"C4 {s.MachineId} 没有位置出处（PositionRef 空），追不回这个坐标是哪来的。"); break; }
            if (double.IsNaN(s.X) || double.IsNaN(s.Y) || double.IsNaN(s.Z) ||
                double.IsInfinity(s.X) || double.IsInfinity(s.Y) || double.IsInfinity(s.Z))
            { bad.Add($"C4 {s.MachineId} 的落位坐标是 NaN/Inf。"); break; }
        }

        // C5 顶点账：逐符号顶点数之和 = 建体的总顶点数
        int sum = res.Symbols.Sum(s => s.VertexCount);
        if (res.Entities > 0 && sum != res.VertexTotal)
            bad.Add($"C5 顶点对不上：逐符号合计 {sum}，实际建体 {res.VertexTotal} —— 有几何被漏建或多建。");
        var over = res.Symbols.Where(s => s.VertexCount > MaxVerticesPerSymbol).ToList();
        if (over.Count > 0)
            bad.Add($"C5 有 {over.Count} 个符号超过 {MaxVerticesPerSymbol} 顶点的预算（最大 {over.Max(s => s.VertexCount)}）"
                  + " —— 上百个符号会把一次性建体的量顶上去。");

        // C6 清理不留残体
        if (ent == null)
            bad.Add("C6 跳过（没给 IEntityCapability，验不了「清理不留残体」）—— 这是**没验**，不是通过。");
        else
        {
            try
            {
                var left = ent.GetHandlesByLayer(EquipmentStage.Layer) ?? Array.Empty<ulong>();
                if (res.Entities > 0 && left.Length < res.Entities)
                    bad.Add($"C6 图层 {EquipmentStage.Layer} 上只查到 {left.Length} 个实体，少于刚建的 {res.Entities} 个。");
            }
            catch (Exception ex) { bad.Add($"C6 查图层实体异常（{ex.GetType().Name}）—— 验不了残体。"); }
        }

        return bad;
    }

    /// <summary>
    /// C7：清理之后图层上必须一个实体都不剩。<b>调用它会真的清</b>，只在自检流程里用。
    /// </summary>
    public static List<string> CheckClearLeavesNothing(EquipmentStage stage)
    {
        var bad = new List<string>();
        var ent = SimHost.Entities;
        if (ent == null) { bad.Add("C7 跳过（未注入 IEntityCapability）—— 这是**没验**，不是通过。"); return bad; }
        try
        {
            stage.Clear();
            var left = ent.GetHandlesByLayer(EquipmentStage.Layer) ?? Array.Empty<ulong>();
            if (left.Length != 0)
                bad.Add($"C7 清理后图层 {EquipmentStage.Layer} 上还剩 {left.Length} 个实体。");
        }
        catch (Exception ex) { bad.Add($"C7 清理异常（{ex.GetType().Name}: {ex.Message}）。"); }
        return bad;
    }

    /// <summary>
    /// C8：<b>状态调制不换主色</b>。R/G/B 同乘一个系数时，HSV 的 H 与 S 必须不变
    /// （只有 8bit 取整带来的微小误差）。这一条一旦红，就说明有人把明度调制改成了「换个色」。
    /// </summary>
    public static List<string> CheckStateKeepsHue(double hueTolDeg = 2.0, double satTol = 0.02)
    {
        var bad = new List<string>();
        foreach (var k in EquipPalette.AllKinds)
        {
            uint baseRgb = EquipPalette.Rgb(k);
            ToHsv(baseRgb, out double h0, out double s0, out _);
            foreach (var st in EquipPalette.AllStates)
            {
                uint c = EquipPalette.Shade(baseRgb, EquipPalette.StateShade(st));
                ToHsv(c, out double h1, out double s1, out _);
                // 圆周上的最短夹角。⚠ 别写成 abs(((h1-h0+540)%360)-180)：那个式子在 h1==h0 时给 180°，
                // 判据会对**每一条**都报红（本判据第一版就是这么错的，是台架抓出来的）。
                double dh = Math.Abs(h1 - h0) % 360;
                if (dh > 180) dh = 360 - dh;
                if (s0 > 1e-6 && dh > hueTolDeg)
                    bad.Add($"C8 {EquipPalette.Name(k)}·{EquipPalette.Name(st)}：色相偏了 {dh:0.0}°（#{baseRgb:X6} → #{c:X6}）—— 状态换了主色。");
                if (Math.Abs(s1 - s0) > satTol)
                    bad.Add($"C8 {EquipPalette.Name(k)}·{EquipPalette.Name(st)}：饱和度偏了 {Math.Abs(s1 - s0):0.000}。");
            }
        }
        return bad;
    }

    /// <summary>四种色觉的名字（下标与 <c>Simulate</c> 的 mode 对齐）。</summary>
    public static readonly string[] VisionModes = { "正常", "红色盲", "绿色盲", "蓝黄色盲" };

    /// <summary>两类主色在某种色觉下的 CIE76 色差。</summary>
    public static double DeltaE76(EquipKind a, EquipKind b, int visionMode)
    {
        var la = RgbToLab(Simulate(EquipPalette.Rgb(a), visionMode));
        var lb = RgbToLab(Simulate(EquipPalette.Rgb(b), visionMode));
        return Math.Sqrt((la.L - lb.L) * (la.L - lb.L) + (la.A - lb.A) * (la.A - lb.A) + (la.B - lb.B) * (la.B - lb.B));
    }

    /// <summary>
    /// C9：<b>色盲筛查</b>。三条，各判各的：
    /// <list type="number">
    ///   <item><b>C9a 正常色觉</b>：全部八类两两 ΔE76 ≥ <paramref name="normalMin"/>（缺省 18）。</item>
    ///   <item><b>C9b 主力四类</b>（<see cref="EquipPalette.HotKinds"/> —— 排产真会画出来的那几类）：
    ///         在**四种色觉下都**要 ≥ <paramref name="hotMin"/>（缺省 18）。</item>
    ///   <item><b>C9c 全部八类在二色觉下</b>：只要求 ≥ <paramref name="floorMin"/>（缺省 6，
    ///         约为 ΔE76 恰可觉差 2.3 的 2.5 倍）。<b>为什么门槛低</b>：8 个类别本来就超出了
    ///         「靠色相分」的上限 —— 见 <see cref="ColorBlindCollisions"/>，那几对不管怎么排都撞。
    ///         设成 18 的话这条判据永远红，而永远红的判据没人看，等于没有。</item>
    /// </list>
    ///
    /// <para><b>这是筛查，不是认证</b>：二色觉模拟用的是 Viénot, Brettel &amp; Mollon (1999)
    /// 的简化线性矩阵，本身是近似；ΔE76 也只是粗糙的色差度量。
    /// 调色板的**依据**是 Okabe–Ito 本身，这一条只是拦「有人往里塞了一个新颜色」。</para>
    /// </summary>
    public static List<string> CheckColorBlindSafe(double normalMin = 18.0, double hotMin = 18.0, double floorMin = 6.0)
    {
        var bad = new List<string>();
        var kinds = EquipPalette.AllKinds;

        for (int i = 0; i < kinds.Length; i++)
            for (int j = i + 1; j < kinds.Length; j++)
            {
                double de0 = DeltaE76(kinds[i], kinds[j], 0);
                if (de0 < normalMin)
                    bad.Add($"C9a 正常色觉下 {Pair(kinds[i], kinds[j])} 的 ΔE76 只有 {de0:0.0}（阈值 {normalMin:0}）。");

                bool hot = EquipPalette.HotKinds.Contains(kinds[i]) && EquipPalette.HotKinds.Contains(kinds[j]);
                for (int m = 1; m < VisionModes.Length; m++)
                {
                    double de = DeltaE76(kinds[i], kinds[j], m);
                    if (hot && de < hotMin)
                        bad.Add($"C9b {VisionModes[m]}下**主力**两类 {Pair(kinds[i], kinds[j])} 的 ΔE76 只有 {de:0.0}"
                              + $"（阈值 {hotMin:0}）—— 这两类排产一定会同时出现在图上。");
                    if (de < floorMin)
                        bad.Add($"C9c {VisionModes[m]}下 {Pair(kinds[i], kinds[j])} 的 ΔE76 只有 {de:0.0}"
                              + $"（下限 {floorMin:0}）—— 已经到了「看成同一个颜色」的程度。");
                }
            }
        return bad;
    }

    private static string Pair(EquipKind a, EquipKind b)
        => $"{EquipPalette.Name(a)}(#{EquipPalette.Rgb(a):X6}) ↔ {EquipPalette.Name(b)}(#{EquipPalette.Rgb(b):X6})";

    /// <summary>
    /// <b>明账</b>：二色觉下 ΔE76 低于 <paramref name="comfortMin"/>（缺省 18）的全部配对。
    /// <para>这不是失败清单，是**必须显示出来的事实** —— 这几对不能只靠颜色分，
    /// 分辨它们要靠符号形态。图例上照实标，别写一句「配色色盲友好」就完事。</para>
    /// </summary>
    public static List<string> ColorBlindCollisions(double comfortMin = 18.0)
    {
        var outv = new List<string>();
        var kinds = EquipPalette.AllKinds;
        for (int i = 0; i < kinds.Length; i++)
            for (int j = i + 1; j < kinds.Length; j++)
                for (int m = 1; m < VisionModes.Length; m++)
                {
                    double de = DeltaE76(kinds[i], kinds[j], m);
                    if (de < comfortMin)
                        outv.Add($"{VisionModes[m]}：{EquipPalette.Name(kinds[i])} ↔ {EquipPalette.Name(kinds[j])}　ΔE76={de:0.0}（靠形状分）");
                }
        return outv;
    }

    /// <summary>
    /// C10：符号库自检 —— 8 类 × 5 态都能生成非空几何，索引不越界，顶点数在预算内。
    /// </summary>
    public static List<string> CheckSymbolLibrary()
    {
        var bad = new List<string>();
        foreach (var k in EquipPalette.AllKinds)
            foreach (var st in EquipPalette.AllStates)
            {
                var buf = new EquipMeshBuffer();
                int nv = EquipSymbolLibrary.Emit(buf, k, st, 1000, 2000, 300, 0.7, 30.0);
                if (nv <= 0 || buf.IsEmpty)
                { bad.Add($"C10 {EquipPalette.Name(k)}·{EquipPalette.Name(st)} 生成了空几何。"); continue; }
                if (nv > MaxVerticesPerSymbol)
                    bad.Add($"C10 {EquipPalette.Name(k)}·{EquipPalette.Name(st)} 有 {nv} 顶点，超过预算 {MaxVerticesPerSymbol}。");

                var tris = buf.Triangles;
                uint n = (uint)buf.VertexCount;
                for (int i = 0; i < tris.Length; i++)
                    if (tris[i] >= n)
                    { bad.Add($"C10 {EquipPalette.Name(k)}·{EquipPalette.Name(st)} 有越界索引 {tris[i]} ≥ {n}（内核会静默丢面）。"); break; }

                var cols = buf.VertexRgb;
                if (cols.Length != buf.VertexCount)
                    bad.Add($"C10 {EquipPalette.Name(k)}·{EquipPalette.Name(st)} 颜色数 {cols.Length} ≠ 顶点数 {buf.VertexCount}。");
                if (cols.Any(c => c == 0xFFFFFFFFu))
                    bad.Add($"C10 {EquipPalette.Name(k)}·{EquipPalette.Name(st)} 有顶点色撞上内核哨兵 0xFFFFFFFF（会被当成「用源面底色」）。");

                var xyz = buf.WorldXyz;
                if (xyz.Any(v => double.IsNaN(v) || double.IsInfinity(v)))
                    bad.Add($"C10 {EquipPalette.Name(k)}·{EquipPalette.Name(st)} 有 NaN/Inf 顶点。");
            }
        return bad;
    }

    /// <summary>全部判据跑一遍（除 C7，它有副作用）。</summary>
    public static List<string> CheckAll(EquipStageResult? res, IEntityCapability? ent = null)
    {
        var bad = new List<string>();
        bad.AddRange(CheckSymbolLibrary());
        bad.AddRange(CheckStateKeepsHue());
        bad.AddRange(CheckColorBlindSafe());
        bad.AddRange(CheckResult(res, ent));
        return bad;
    }

    // ── 色彩数学 ───────────────────────────────────────────────────────

    private static void ToHsv(uint rgb, out double h, out double s, out double v)
    {
        double r = ((rgb >> 16) & 0xFF) / 255.0, g = ((rgb >> 8) & 0xFF) / 255.0, b = (rgb & 0xFF) / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), d = mx - mn;
        v = mx;
        s = mx <= 1e-9 ? 0 : d / mx;
        if (d <= 1e-9) { h = 0; return; }
        if (mx == r) h = 60 * (((g - b) / d) % 6);
        else if (mx == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    /// <summary>
    /// 二色觉模拟。<paramref name="mode"/>：0 正常 / 1 红色盲 / 2 绿色盲 / 3 蓝黄色盲。
    /// 矩阵取自 Viénot, Brettel &amp; Mollon (1999) 的简化线性 RGB 形式（**近似**，见 C9 注释）。
    /// </summary>
    private static uint Simulate(uint rgb, int mode)
    {
        if (mode == 0) return rgb;
        double r = Lin(((rgb >> 16) & 0xFF) / 255.0);
        double g = Lin(((rgb >> 8) & 0xFF) / 255.0);
        double b = Lin((rgb & 0xFF) / 255.0);

        double r2, g2, b2;
        switch (mode)
        {
            case 1:  // protanopia
                r2 = 0.000 * r + 1.05118294 * g - 0.05116099 * b;
                g2 = g; b2 = b; break;
            case 2:  // deuteranopia
                r2 = r;
                g2 = 0.9513092 * r + 0.000 * g + 0.04866992 * b;
                b2 = b; break;
            default: // tritanopia
                r2 = r; g2 = g;
                b2 = -0.86744736 * r + 1.86727089 * g + 0.000 * b; break;
        }

        return (uint)((C8(r2) << 16) | (C8(g2) << 8) | C8(b2));
    }

    private static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    private static double Srgb(double c) => c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;

    private static uint C8(double lin)
    {
        double v = Srgb(Math.Max(0, Math.Min(1, lin)));
        int i = (int)Math.Round(v * 255);
        return (uint)(i < 0 ? 0 : i > 255 ? 255 : i);
    }

    /// <summary>sRGB → CIE Lab（D65）。</summary>
    private static (double L, double A, double B) RgbToLab(uint rgb)
    {
        double r = Lin(((rgb >> 16) & 0xFF) / 255.0);
        double g = Lin(((rgb >> 8) & 0xFF) / 255.0);
        double b = Lin((rgb & 0xFF) / 255.0);

        double X = (0.4124564 * r + 0.3575761 * g + 0.1804375 * b) / 0.95047;
        double Y = (0.2126729 * r + 0.7151522 * g + 0.0721750 * b) / 1.00000;
        double Z = (0.0193339 * r + 0.1191920 * g + 0.9503041 * b) / 1.08883;

        double fx = F(X), fy = F(Y), fz = F(Z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));

        static double F(double t) => t > 0.008856451679 ? Math.Cbrt(t) : (7.787037037 * t + 16.0 / 116.0);
    }
}
