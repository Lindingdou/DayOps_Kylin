// 忠实移植自原 PitMine3D Platform/PitMine.Platform/Capabilities/MineableRegionInfo.cs（逐行对应；仅命名空间适配。宿主能力接口：Kylin 侧由 MainWindow 对场景实现，未注入时各消费方按原版降级）
using System;
using System.Collections.Generic;
namespace PitMine3D.Kylin.Platform.Capabilities
{
    /// <summary>
    /// 作业区域（可采区域/采场/排土场）的轻量只读快照——供不依赖 GeoDataBase 的模块
    /// （如 PointCloudLib）通过 <see cref="IPitDesignCapability.GetMineableRegions"/> 读取，
    /// 用于把坡顶/坡底线、道路中心线等提取结果裁剪到区域「内/外」。纯几何 DTO，零数据库耦合。
    /// </summary>
    public sealed class MineableRegionInfo
    {
        /// <summary>区域名称（对话框显示用）。</summary>
        public string Name { get; set; } = "";

        /// <summary>类别 mineable/pit/external_dump/internal_dump（对话框分色/标注用）。</summary>
        public string Category { get; set; } = "";

        /// <summary>边界环：扁平 [x0,y0,x1,y1,...]（仅 XY，隐式闭合）。</summary>
        public double[] RingXy { get; set; } = System.Array.Empty<double>();
    }
}
