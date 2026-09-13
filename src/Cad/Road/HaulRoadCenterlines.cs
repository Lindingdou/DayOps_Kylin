using System;
using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 一条【已落地坑线】(开拓运输通道)的中线 + 断面参数，跨模块共享。
///
/// 单一数据源(见 docs/开拓运输系统_设计.md §3「RoadEdge.Centerline ← 复用坑线落地输出」):
/// MineAssLib【坑线落地】写、RoadLib 读并转成 <c>RoadNode/RoadEdge</c> 并入路网图，
/// 经 <see cref="PitMine.Platform.IUserSettings"/> 全局持久化(key <see cref="HaulRoadCenterlineSet.SettingsKey"/>)
/// 跨越模块边界——MineAssLib 与 RoadLib 互不引用(也不许互引)，故中线定义放 Platform 这一两侧都引用的程序集，
/// 范式完全照抄 <see cref="LoadUnloadPointSet"/>。
///
/// 只承载「路网建图 + 寻径/运距」所需的最小字段：几何(中线) + 断面(路宽/车道) + 纵坡 + 来源标识。
/// 挖填方量/边坡/路面结构层等落地富字段不进这里(它们属设计侧，路网算不到)。
/// </summary>
[Serializable]
public sealed class HaulRoadCenterline
{
    /// <summary>本条坑线的稳定标识(同名再落地=覆盖)。建议形如 <c>坑线-2026-08-02-01</c> 或段号。</summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// 来源标识：这条中线是谁、怎么产生的，供路网侧回显与追溯。
    /// 约定取值：<c>"坑线落地"</c>(斜坡道逐面切) / <c>"坑线落地:折返"</c> / <c>"直线坑线预览"</c>。
    /// 落到 <c>RoadEdge.SourceRef</c>，路网预览/校验报告里据此把设计出来的路与图上抽的路分开。
    /// </summary>
    public string Source { get; set; } = "";

    /// <summary>
    /// 中线点列，扁平 <c>[x,y,z, x,y,z, ...]</c>(与 MineAssLib 内部缓存 <c>FlattenXyz</c> 同口径)。
    /// 至少 2 个点(6 个 double)才有意义；不足则读取侧丢弃该条。
    /// 世界坐标，与图上实体同一坐标系，不做任何偏移。
    /// </summary>
    public double[] CenterlineXyz { get; set; } = Array.Empty<double>();

    /// <summary>路面宽 m(基宽；弯道加宽后逐段变宽的取该段实用宽)。0=未知。</summary>
    public double RoadWidthM { get; set; }

    /// <summary>
    /// 设计纵坡 %(正值，落地时实际采用的限坡值，非几何反算值)。
    /// 读取侧只在中线退化(点数&lt;2、算不出几何坡度)时回退用它；正常一律按中线几何逐段反算，
    /// 免得「设计限坡」与「实际中线」两个口径打架。
    /// </summary>
    public double GradePct { get; set; }

    /// <summary>车道数。默认 1。</summary>
    public int LaneCount { get; set; } = 1;

    /// <summary>临时道路(随采延拓采空后可回收)。映射 <c>RoadEdge.IsTemporary</c>。默认 false=永久。</summary>
    public bool IsTemporary { get; set; }

    /// <summary>中线点数(由 <see cref="CenterlineXyz"/> 长度推出；方法而非属性，避免被 JSON 序列化)。</summary>
    public int PointCount() => (CenterlineXyz?.Length ?? 0) / 3;
}

/// <summary>
/// 已落地坑线中线集合(持久化容器)。
///
/// ── 写入侧接线说明(MineAssLib，本轮未接，下一轮补) ───────────────────────────────
/// 位置：<c>Modules/MineAssLib/MineAssLibPlugin.cs</c> → <c>CreateMaterializeRouteCommand()</c>
/// (「坑线落地」命令)，在逐面切成功、回显「✓ 坑线已落地」那一段之后追加落盘。
/// 数据来源(命令里现成就有，无需重算)：
///   · 中线点列 → 【直线坑线】的 <c>StraightRampRouteResult.Centerline</c>
///     (类型正是 <c>IReadOnlyList&lt;(double X,double Y,double Z)&gt;</c>，直接 <c>FlattenXyz(rr.Centerline)</c> 即可)。
///     ⚠ 当前【直线坑线】只缓存了 <c>_lastFaceCutOPoints</c> 逐面切 O 点，**整条中线没留** ——
///      需在布线命令的缓存段(与 <c>_lastFaceCutOPoints</c> 同处)把 <c>rr.Centerline</c> 一并存进
///      <c>RouteCacheDto</c>(比照 <c>OPoints</c> 加一个 <c>Centerline</c> 扁平数组)，
///      否则跨会话落地时拿不到中线。这是接通此 DTO 唯一缺的一份数据。
///   · 路宽   → <c>_lastRouteRoadWidth</c>(或逐段 <c>_lastRouteSegWidths</c> 取实用宽)
///   · 纵坡   → <c>_lastRouteGradePct</c>
///   · 车道数 → <c>TransportConstraintSettings.LaneCount</c>
///   · 来源   → <c>"坑线落地"</c>
/// 写法(照抄同文件 <c>SaveRouteCache()</c>)：
/// <code>
///   var set = _context.UserSettings.Get&lt;HaulRoadCenterlineSet&gt;(HaulRoadCenterlineSet.SettingsKey)
///             ?? new HaulRoadCenterlineSet();
///   set.Roads.RemoveAll(r =&gt; r.Id == id);      // 同 Id 覆盖，别累积重复中线
///   set.Roads.Add(new HaulRoadCenterline { Id = id, Source = "坑线落地", ... });
///   set.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
///   set.Summary = $"{set.Roads.Count} 条坑线中线";
///   _context.UserSettings.Set(HaulRoadCenterlineSet.SettingsKey, set);
///   _context.UserSettings.Flush();
/// </code>
/// 撤销侧(【撤销坑线】)对应 <c>set.Roads.RemoveAll(r =&gt; r.Id == 被撤那段的 Id)</c> + 回写，
/// 否则撤销后路网里还留着已经不存在的路。
///
/// ── 读取侧(RoadLib，本轮已接) ─────────────────────────────────────────────────
/// <c>RoadLib.Network.HaulRoadImporter</c>：中线 → 多段线喂 <c>RoadGraphBuilder.FromPolylines</c>
/// (与图上抽的中线一起做交叉打断/端点吸附/缺口桥接)，建图后再把路宽/车道/来源回贴到匹配上的边。
/// </summary>
[Serializable]
public sealed class HaulRoadCenterlineSet
{
    /// <summary>IUserSettings 持久化键。</summary>
    public const string SettingsKey = "transport.haulroads";

    /// <summary>存盘时刻(本地时间 yyyy-MM-dd HH:mm)。跨会话使用时回显给用户核对是不是当前工程。</summary>
    public string SavedAt { get; set; } = "";

    /// <summary>几何自述(条数 / 总长 / 标高范围等)，同上，供跨会话核对。</summary>
    public string Summary { get; set; } = "";

    /// <summary>已落地的坑线中线，按 <see cref="HaulRoadCenterline.Id"/> 唯一(写入侧同 Id 覆盖)。</summary>
    public List<HaulRoadCenterline> Roads { get; set; } = new();
}
