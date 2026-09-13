// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/IObliqueCapability.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System;
using System.Collections.Generic;
namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 倾斜摄影能力域：OSGB / .obtile 倾斜模型的 LOD 分页浏览（加载 / 卸载 / 可见性 / 清晰度）。
    ///
    /// 设计目的（仿 <see cref="IPointCloudCapability"/>）：
    ///   - 把倾斜模型相关的 native API 收敛到独立 capability，与 Document / Entity / PointCloud 同级
    ///   - 第三方插件无需引用 Platform.Internal，只通过 ctx.Capabilities.TryGet&lt;T&gt; 访问
    ///   - 失败语义：方法本身不抛异常；Host 实现会吞 P/Invoke 异常并返回保守值
    ///
    /// M0（宿主接线）：<see cref="LoadModel"/> 走 demoMode 合成演示瓦片，真实 .osgb 解析待离线转换器。
    /// 详见 docs/OSGB倾斜摄影_LOD浏览_设计.md。
    /// </summary>
    public interface IObliqueCapability
    {
        /// <summary>
        /// 加载倾斜模型。<paramref name="demoMode"/>=true 时用合成演示瓦片（<paramref name="path"/> 可为 null）。
        /// 返回 false 时调 <see cref="GetLastError"/> 取详细原因。
        /// </summary>
        /// <param name="path">倾斜数据集路径（.osgb / .obtile）；demoMode 下可为 null。</param>
        /// <param name="demoMode">true = 合成演示瓦片（真实解析待转换器）。</param>
        bool LoadModel(string? path, bool demoMode);

        /// <summary>最近一次 <see cref="LoadModel"/> 返回 false 时的人类可读错误（成功时空字符串）。</summary>
        string GetLastError();

        /// <summary>卸载倾斜模型，释放其 GPU 缓存 / 瓦片树。</summary>
        void Unload();

        /// <summary>切换倾斜模型可见性。false = 隐藏渲染但保留缓存，再切回零成本。</summary>
        void SetVisible(bool visible);

        /// <summary>当前倾斜模型是否可见。</summary>
        bool IsVisible();

        /// <summary>设置清晰度旋钮：屏幕空间误差(SSE)阈值，单位像素（越小越清晰、驻显存瓦片越多）。</summary>
        void SetSseThreshold(float px);

        /// <summary>当前驻显存（已加载）瓦片数。</summary>
        int GetLoadedTileCount();

        // ── 「倾斜转三角网」：把倾斜模型按原生 LOD 层级固化成带顶点色的三角网实体 ──
        /// <summary>探测当前倾斜模型出现过的所有原生 LOD 层级（升序，含根级 0）。未加载 → 空数组。</summary>
        int[] GetAvailableLodLevels();

        /// <summary>
        /// 按原生 LOD 层级把倾斜模型固化成一张带顶点色的三角网实体（加入活动文档的指定图层）。
        /// 返回生成的三角形数（0 = 失败 / 该层级无瓦片）。会解析 + 解 JPEG，较耗时，宜后台调用。
        /// </summary>
        int GenerateTin(int lodLevel, string layerName);
    }
}
