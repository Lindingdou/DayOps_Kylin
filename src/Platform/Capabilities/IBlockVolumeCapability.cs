// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/IBlockVolumeCapability.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System;
using System.Collections.Generic;
namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 块体体积/转换能力域：让任意模块（如 MeshEditLib「体编辑」组）触发 BlockModelLib 的
    /// 「体素格网体积」「实体转块体」流程，而无需直接依赖 BlockModelLib（保持依赖方向）。
    /// 由宿主实现并注册；内部转调 BlockModelLib.Dialogs.VolumeCommands。
    /// </summary>
    public interface IBlockVolumeCapability
    {
        /// <summary>体素格网体积：选封闭三角网体(ssget) → 体素化 → 整体+分标高报量 → 可导报表/生成块体。</summary>
        void StartVoxelGridVolume();

        /// <summary>实体转块体：选封闭三角网体(ssget) → 设块尺寸 → 快速体素化逼近成块体模型。</summary>
        void StartEntityToBlocks();

        /// <summary>三角网直接体积：选三角网体(ssget) → 解析散度法(+容错修复/格网兜底)算体积+分标高 → 报告窗(可导 HTML/CSV/PDF)。不转块体。</summary>
        void StartMeshVolume();
    }
}
