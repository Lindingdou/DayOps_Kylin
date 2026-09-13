// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/ISelectionCapability.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System.Collections.Generic;
using System;

namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 选集能力域：选择集查询与变更，以及订阅选集变化事件。
    ///
    /// 终态设计（P-2）：
    ///   - 第三方插件不直接访问 IEngineService.GetSelectedHandles 等内部 P/Invoke
    ///   - 通过本接口拿到 ulong[]，可零拷贝转 Span 处理
    ///   - SelectionChanged 事件订阅与 IEventBus 风格统一，但参数为强类型 ulong[]
    /// </summary>
    public interface ISelectionCapability
    {
        /// <summary>当前选中的 AcDb 实体 handle 数组。每次调用返回一个新副本。</summary>
        ulong[] GetSelectedHandles();

        /// <summary>当前选中实体的数量。</summary>
        int SelectedCount { get; }

        /// <summary>是否有任何选中。</summary>
        bool HasSelection { get; }

        /// <summary>把单个实体加入或替换为选集。</summary>
        /// <param name="handle">实体 handle</param>
        /// <param name="addToSelection">true=加入；false=替换</param>
        void SelectByHandle(ulong handle, bool addToSelection);

        /// <summary>
        /// 批量把一组实体填进选集，只播一次 <see cref="SelectionChanged"/>。
        ///
        /// <b>凡是"程序化选中一批"都走这个，不要在调用方写 foreach + SelectByHandle</b> —— 单条版
        /// 每次都播一次事件、载荷是变更后的完整选集，逐条调是 O(N²) 的搬运量，上千条会卡死 UI。
        /// </summary>
        /// <param name="handles">要选中的实体；空数组 + addToSelection=false 即清空选集</param>
        /// <param name="addToSelection">true=追加（去重）；false=顶替当前选集</param>
        /// <returns>最终选集里的实体数；-1 表示失败</returns>
        int SelectByHandles(ulong[] handles, bool addToSelection);

        /// <summary>清空选集。</summary>
        void ClearSelection();

        /// <summary>选集变化事件：参数为变化后的完整 handle 列表。</summary>
        event Action<ulong[]>? SelectionChanged;

        /// <summary>
        /// 启动一次「交互式对象选择」：进入 native 通用选择态（命令行提示"选择对象：点选/框选累加，
        /// 右键或回车结束，Esc 取消"），用户结束后回调一次 <paramref name="onConfirmed"/>，参数为选中的
        /// AcDb handle 列表（Esc 取消时为空数组）。回调在 UI 线程触发，且只触发一次（一次性订阅）。
        /// 供"运行命令→选对象→右键→弹面板"的流程（如体素格网体积）使用。
        /// </summary>
        void BeginInteractiveSelection(Action<ulong[]> onConfirmed);
    }
}
