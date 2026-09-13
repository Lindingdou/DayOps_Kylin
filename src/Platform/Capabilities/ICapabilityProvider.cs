// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/ICapabilityProvider.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System;
using System.Collections.Generic;
namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 能力发现入口：第三方插件通过此接口获取受控的 capability 引用。
    ///
    /// 使用：
    /// <code>
    ///   if (ctx.Capabilities.TryGet&lt;IEntityCapability&gt;(out var entities)) {
    ///       var handles = entities.GetHandlesByLayer("MyLayer");
    ///       entities.DeleteEntities(handles);
    ///   }
    /// </code>
    ///
    /// 设计原则：
    ///   - 用 Try 模式而非抛异常：插件能优雅降级（capability 不存在时不崩）
    ///   - 同名 capability 全局唯一；不存在版本号或命名空间冲突（Host 保证）
    ///   - 第三方插件可探测某个 capability 是否可用（例如 IPointCloudCapability 在没装点云模块时不可用）
    /// </summary>
    public interface ICapabilityProvider
    {
        /// <summary>
        /// 尝试获取 capability 实例。
        /// </summary>
        /// <typeparam name="TCapability">capability 接口类型，必须为引用类型</typeparam>
        /// <param name="capability">输出 capability 实例；不可用时为 null</param>
        /// <returns>true=拿到了；false=Host 未提供此 capability</returns>
        bool TryGet<TCapability>(out TCapability? capability) where TCapability : class;
    }
}
