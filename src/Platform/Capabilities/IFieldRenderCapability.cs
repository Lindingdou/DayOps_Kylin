// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/IFieldRenderCapability.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System;
using System.Collections.Generic;
namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 标量场渲染能力——把估算/插值得到的"场"画成不同表现：彩色块体 / 区域等值云图 / 平滑云图。
    /// 与估算解耦：估算只产出场（规则网格逐单元值），本能力专管出图。供 MeshEditLib 等模块调用，
    /// 走 Capabilities 取用，不直接依赖 BlockModelLib / 渲染实现（与 IBlockVolumeCapability 同解耦模式）。
    /// 接口只用基础类型；所有方法成功返回句柄/图层名，失败返回 null（不抛）。
    /// </summary>
    public interface IFieldRenderCapability
    {
        /// <summary>
        /// 块体着色：3D 规则网格逐单元值 → 彩色体素块体。
        /// values 长度 = nx*ny*nz，线性序 = ix + iy*nx + iz*nx*ny；== noData/NaN 的单元不渲染、不参与色谱范围。
        /// colormap：Viridis/Magma/Plasma/RdYlBu/Jet/Gray/Turbo（大小写不敏感，未知回退 RdYlBu）。
        /// </summary>
        string? RenderVoxelBlock(
            string name, string attribute,
            double originX, double originY, double originZ,
            double sizeX, double sizeY, double sizeZ,
            int nx, int ny, int nz,
            double[] values, double noData, string colormap);

        /// <summary>
        /// 区域等值云图：2D 规则网格场 → 按 <paramref name="levels"/> 等值分级着色的彩色面，铺在 z0 高程。
        /// values 长度 = nx*ny，线性序 = ix + iy*nx；== noData/NaN 的单元留空。
        /// </summary>
        string? RenderContourMap(
            string name,
            double originX, double originY, double cellSizeX, double cellSizeY, int nx, int ny,
            double[] values, double noData, double z0, int levels, string colormap);

        /// <summary>
        /// 平滑云图：2D 规则网格场 → 连续（高分级）着色的彩色面，铺在 z0 高程。参数同上（不分级）。
        /// </summary>
        string? RenderSmoothMap(
            string name,
            double originX, double originY, double cellSizeX, double cellSizeY, int nx, int ny,
            double[] values, double noData, double z0, string colormap);
    }
}
