// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/IPolylineOpsCapability.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System;
using System.Collections.Generic;
namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// CAD 端多段线 / 点 批量编辑能力。
    /// 与 IMeshOpsCapability 平级 — 后者桥接 AlgoCore/MeshLib（mesh 算法），
    /// 本接口纯 AcDb 端操作（不依赖 AlgoCore）。
    ///
    /// 所有方法都从当前选集抓 AcDbPolyline / AcDbPoint，按算子语义批量处理。
    /// 返回 PMxx 二进制 buffer，由对应 Report.ParseSafe 解析。
    /// </summary>
    public interface IPolylineOpsCapability
    {
        /// <summary>闭合所有选中的多段线（设 Closed=true）。返回 PMPC。</summary>
        byte[] CloseSelectedPolylines();

        /// <summary>把所有选中多段线的顶点 Z 统一置为 newZ。返回 PMPZ。</summary>
        byte[] UnifyZOfSelectedPolylines(double newZ);

        /// <summary>把所有选中点的 Z 置为 newZ。返回 PMTZ。</summary>
        byte[] SetZOfSelectedPoints(double newZ);

        /// <summary>按 tolerance 容差去重所有选中点。返回 PMTD。</summary>
        byte[] DedupeSelectedPoints(double tolerance = 1e-3);

        /// <summary>按 tolerance 容差去重所有选中多段线（含反向相同视为重复）。返回 PMPD。</summary>
        byte[] DedupeSelectedPolylines(double tolerance = 1e-3);

        /// <summary>按 maxStep 等距加密所有选中多段线。返回 PMPN。</summary>
        byte[] DensifySelectedPolylines(double maxStep);

        /// <summary>抽稀（简化）选中多段线：Douglas-Peucker，2D XY 垂距，tolerance 越大点越少。
        /// 只动节点数 > minNodes 的线（密集等值线）；节点少的（台阶线/简单线）整条跳过不动。
        /// 用于"抽稀过密等值线"——建三角网前先减点。返回 PMPN 报告（处理条数/原顶点/简化后顶点）。</summary>
        byte[] SimplifySelectedPolylines(double tolerance, int minNodes);

        /// <summary>连接多段线：按端点容差合并 N 条为更少条。返回 PMPJ。</summary>
        byte[] JoinSelectedPolylines(double tolerance);

        /// <summary>两线交点：选恰好 2 条 polyline，2D 求交后创建 POINT。返回 PMPI。</summary>
        byte[] IntersectSelectedPolylines(double tolerance);

        /// <summary>闭合线裁剪：选 1 闭合 polyline (裁刀) + 1+ polyline，保留圈内/外段。返回 PMPL。</summary>
        byte[] ClipPolylinesByLoop(double tolerance, bool keepInside = true);

        /// <summary>闭合线裁剪 —— 交互式入口：kernel 进入"单次拾取"状态机，
        /// 等用户点中闭合多段线/矩形/多边形作为裁刀，然后对模型空间所有其它多段线裁剪。
        /// 结果异步通过 <c>CommandLineMessageEvent</c> 回到命令行（C# 不需要同步处理返回值）。
        /// </summary>
        void StartClipByLoopInteractive(double tolerance, bool keepInside);

        /// <summary>点落到面上：选 N POINT + 1 mesh，把每个点 Z 投影到 mesh 上。返回 PMTP。</summary>
        byte[] ProjectPointsToMesh(double tolerance = 1e-6);

        /// <summary>线落到面上：选 N polyline + 1 mesh，每条 polyline 顶点 Z 投影到 mesh。返回 PMLP。</summary>
        byte[] ProjectPolylinesToMesh(double tolerance = 1e-6);
    }
}
