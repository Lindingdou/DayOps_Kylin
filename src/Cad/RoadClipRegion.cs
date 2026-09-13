namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 作业区域裁剪多边形（需求07：道路中心线只在作业区域内生成）。忠实移植原 PointCloudLib.RoadCenterline.RoadClipRegion。
/// 纯几何 DTO，<b>不依赖数据库层</b> —— 上层（MainWindow）把 <c>mineable_region</c> 台账行转成本类型再传入。
/// </summary>
public sealed class RoadClipRegion
{
    /// <summary>区域名称（仅用于对话框显示）。</summary>
    public string Name { get; set; } = "";

    /// <summary>类别 pit/external_dump/internal_dump/mineable（仅用于显示分色）。</summary>
    public string Category { get; set; } = "";

    /// <summary>边界环：扁平 [x0,y0,x1,y1,...]（仅 XY，闭合与否都可，裁剪按隐式闭合处理）。</summary>
    public double[] RingXy { get; set; } = System.Array.Empty<double>();

    /// <summary>
    /// 台账里的「本期选定」。<b>只决定对话框里默认勾不勾，不决定这块区域进不进清单</b> ——
    /// 未选定的区域照样列出来，人想临时用就能勾（原版教训：把 active=0 的整批扔掉，界面就报"未定义作业区域"，
    /// 把人指去重新圈定，那救不了；真正要动的是「作业区划分」里的选定列）。
    /// </summary>
    public bool Active { get; set; } = true;
}
