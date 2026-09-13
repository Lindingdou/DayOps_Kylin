// 忠实移植自原 PitMine3D Modules/RoadLib/Render/RoadSymbology.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>轻量 RGB（与 PmbiWriter.EntityColor.TrueColor 的 r/g/b 对接）。</summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// 路网显示统一符号字典（gap#3）：所有 overlay 的图层名 + 颜色语义集中一处，
/// 避免「绿在路网预览=主干、在演化=新增、在编辑=新增边」同色不同义而互相误读。
///
/// 约定两套语汇：
///   · 路网显示（路况/结构路面/路网预览）走实线分色——表达「这是路网」；
///   · 变更 diff（编辑预览 / 演化分色）走 新增绿 / 废除红×虚 / 改线橙 等专用语汇——
///     刻意区别于路网显示，只突出「变了什么」。
/// 动态图例（Phase 2）也按本字典出条目。
/// </summary>
public static class RoadSymbology
{
    // ── 图层名（集中维护，新代码引用此处；旧 overlay 层值保持一致，便于后续统一迁移） ──
    public const string PathPreviewLayer = "寻径结果_预览";
    public const string MarkerPreviewLayer = "装卸点_预览";
    public const string RoadConditionLayer = "路况_预览";
    public const string StructurePavementLayer = "结构路面_预览";
    public const string NetworkPreviewLayer = "路网_预览";
    /// <summary>编辑期 diff 预览层（暂存编辑的新增/待删/改状态，提交或退出即清）。</summary>
    public const string EditDiffLayer = "路网_编辑预览";
    public const string EvoExtendLayer = "道路演化_延拓候选";
    public const string EvoAbolishLayer = "道路演化_已废除";
    public const string EvoKeepLayer = "道路演化_保持";

    // ── 路网显示色（实线分色语汇） ──
    public static readonly Rgb Trunk = new(60, 200, 90);       // 干线=主干绿
    public static readonly Rgb Junction = new(215, 220, 230);  // 交叉口白环
    public static readonly Rgb Gap = new(255, 45, 35);         // 悬挂端点红环
    public static readonly Rgb Loading = new(255, 60, 30);     // 采剥点(源)
    public static readonly Rgb Unloading = new(30, 150, 255);  // 卸载点(汇)

    /// <summary>
    /// 路段拓扑分类色（R-T4，「路网预览」只用这三色）。
    ///
    /// <b>替掉了原先的「连通片循环配色」</b>：对库里真实路网量过 —— 127 个连通片、调色板 10 色，
    /// 于是同色不同片，颜色既多又不表意（现场 2026-08-11：「转换之后颜色太多」）；
    /// 且那版把「最大片」画成主干绿，而最大片只占 30.5% 里程 / 9% 节点，
    /// <b>69.5% 的里程根本不在它上面</b>，绿的名不副实。连通片数改由回显文字报，不再占颜色通道。
    ///
    /// 取色两条：干线复用 <see cref="Trunk"/>（同义，不新造一个绿）；
    /// 孤立段<b>刻意等于</b> <see cref="Gap"/>（同义：没接上 —— 孤立段两端本来就各顶着一个缺口红环，
    /// 线与环同色恰恰是对的，这不是「同色不同义」）。支线取灰蓝而不是饱和蓝：
    /// 它占 52.3% 里程，是图上面积最大的一类，饱和色会盖过真正要看的干线与断点。
    /// </summary>
    public static Rgb ColorOf(RoadSegmentClass c) => c switch
    {
        RoadSegmentClass.Trunk => Trunk,
        RoadSegmentClass.Spur => SegSpur,
        _ => Gap,
    };

    /// <summary>支线色（灰蓝）。见 <see cref="ColorOf"/> 的取色理由。</summary>
    public static readonly Rgb SegSpur = new(120, 170, 205);

    // ── 寻径结果色（route 语汇：「这一趟车走哪」，画在路网之上） ──
    /// <summary>
    /// 去程重车。取<b>品红</b>而不是绿：寻径结果几乎总是叠在「路网预览」上看，
    /// 而那层已经把绿（干线）和灰蓝（支线）铺满整张图 —— 原来的去程绿 (0,200,90) 与 <see cref="Trunk"/> (60,200,90)
    /// 差 60/0/0，回程蓝 (0,120,255) 与 <see cref="SegSpur"/> (120,170,205)、<see cref="Unloading"/> (30,150,255)
    /// 也都贴着，结果就是<b>算出来的路根本认不出来</b>（现场 2026-08-17：「计算出来的线路的显示不明显」）。
    /// 品红是整本字典里没人占的色相，压在绿蓝底图上一眼跳出来。
    /// </summary>
    public static readonly Rgb RouteOutbound = new(255, 20, 140);
    /// <summary>
    /// 仅回程（空车走了去程没走的段）。同属品红—紫这一族，读作"同一趟的另一半"，但与去程留足通道差。
    /// 取值受 T13b 那道闸约束：与<b>底图每一种色</b>的最大通道差 ≥90 —— 第一版 (200,110,255) 就是
    /// 因为离支线灰蓝 (120,170,205) 只有 80 被判红的。
    /// </summary>
    public static readonly Rgb RouteReturn = new(150, 70, 255);
    /// <summary>路线廊带（近黑）。沿路线左右外扩的边界线，给亮芯线镶一条世界尺度的边。</summary>
    public static readonly Rgb RouteCasing = new(18, 18, 26);


    // ── 线宽（mm）。渲染端：widthPx = lineweight × 4，**封顶 16px**（EntityGpuCache.cpp）──
    //    集中在这里是为了能被判据直接比：「结果线必须比它压着的底图粗」这句话，
    //    散在两个文件里各写一个字面量就没法钉。
    /// <summary>「路网预览」干线线宽 → 8px。图上最粗的一类路网线。</summary>
    public const float NetworkTrunkLineweight = 2.0f;
    /// <summary>寻径结果芯线线宽 → 16px，引擎上限，正好是干线的两倍。</summary>
    public const float RouteCoreLineweight = 4.0f;

    // ── 变更 diff 色（编辑 + 演化共用的「变了什么」语汇，区别于路网实线） ──
    public static readonly Rgb DiffAdd = new(40, 200, 90);      // 新增 / 新建入网 / 延长——亮绿加粗
    public static readonly Rgb DiffRemove = new(216, 90, 48);   // 删除 / 已废除——红虚线 + ×
    public static readonly Rgb DiffModify = new(240, 150, 40);  // 改线 / 平移(reroute)——橙
    public static readonly Rgb DiffShorten = new(235, 120, 70); // 截断 / 缩短——红橙
    public static readonly Rgb DiffKeep = new(140, 150, 160);   // 未变——淡灰 ghost(退背景)
    public static readonly Rgb DiffStatus = new(250, 210, 60);  // 改状态(检修等)——黄

    // ── 边状态色（「路况显示」按 R-L3 整条压色；与 PathSolver 的禁行口径同源） ──
    public static readonly Rgb StatusMaintenance = new(250, 210, 60);  // 检修=黄
    public static readonly Rgb StatusClosed = new(220, 60, 40);        // 封闭/不能通过=红

    // ── 路况分档色（R-L1/R-L2：主色=逐段纵坡，上坡暖色 / 下坡冷色 / 平段中性） ──
    // 「上/下」的口径：沿中线点序行进 —— 也正是 chevron 指的那个方向，两个通道必须说同一个方向的事。
    // 取色刻意避开上面已占的语义色（检修黄 / 封闭红 / 主干绿 / 交叉口白 / 源红 / 汇蓝），
    // 免得「黄在路况显示=向标、在状态=检修」那类同色不同义（见本文件开头的约定）。
    public static readonly Rgb GradeLevel = new(165, 172, 182);     // |坡| ≤3%    平段——中性灰
    public static readonly Rgb GradeMildUp = new(245, 200, 130);    // 3~6%   上坡——浅沙
    public static readonly Rgb GradeMildDown = new(150, 215, 235);  // 3~6%   下坡——浅青
    public static readonly Rgb GradeSteepUp = new(235, 130, 45);    // 6~10%  上坡——橙
    public static readonly Rgb GradeSteepDown = new(45, 175, 195);  // 6~10%  下坡——青
    public static readonly Rgb GradeOverUp = new(170, 45, 30);      // >10%   上坡——深砖红
    public static readonly Rgb GradeOverDown = new(90, 60, 190);    // >10%   下坡——深紫蓝

    /// <summary>
    /// 路况向标（chevron）=中性白。原先用的黄 (250,210,60) 与 <see cref="StatusMaintenance"/>
    /// <b>RGB 完全相同</b>——一旦按 R-L3 加上状态压色就必然同色不同义，故把黄整个让给「检修」。
    /// </summary>
    public static readonly Rgb ConditionChevron = new(240, 244, 250);
}
