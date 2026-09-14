using System.Collections.Generic;

namespace PitMine3D.Kylin.Cad;

/// <summary>工作帮台阶参数（忠实原 <c>BenchTemplateParams</c>；「驱动量」面板采集，台阶生成器 <c>BenchTemplateBuilder</c> 消费）。</summary>
public sealed class BenchTemplateParams
{
    public double RockBenchH   = 15;   // 岩台阶高度(m)：岩台阶按此为标高格步长
    public double CoalFaceDeg  = 65;   // 煤台阶坡面角(°)【前界卡位模型下不用于坡面：坡面=量驱动坡角α；保留供未来分级】
    public double RockFaceDeg  = 65;   // 岩台阶坡面角(°)【同上，保留】
    public double WorkAngleDeg = 10;   // 工作帮总坡角(°)【保留供控制线等用】
    public double MinBerm      = 80;   // 最小工作平盘(m)

    /// <summary>逐层煤台阶高度(m)，与 seams 同序：&lt;0 采全高（整层一个台阶）| 0/null 跟岩台阶高 | &gt;0 用该值。</summary>
    public IReadOnlyList<double>? CoalBenchHBySeam;

    /// <summary>端帮预判：只回报"多少条现状台阶线跨了模板边界、能衔接几处"，不入图、不改实体。默认关。</summary>
    public bool JoinEndWall = false;

    /// <summary>帮顶标高 zTopS：≤0 = 按工作线自身标高自动；原版默认 1550（帮顶必须高于现状面最高点）。</summary>
    public double WallTopZ = 0;

    /// <summary>R48 工作平盘放宽：每一级平盘都顶到设计值（整条线刚性平移）。原版默认 true。</summary>
    public bool WidenBerm = true;

    /// <summary>R60 因煤而断的端怎么收：false=平直收尖（默认）；true=端头整体平移吸到最近煤台阶线。</summary>
    public bool HandoffToCoal = false;

    /// <summary>R58 非满高岩台阶也要有最小工作平盘（只推岩台阶）。原版默认关。</summary>
    public bool MinBermOnRock = false;

    /// <summary>按地形尖灭：台阶线超出现状面处削顶/尖灭。原版默认关。</summary>
    public bool PinchByTerrain = false;

    /// <summary>R52 坑沿取最外侧交点（原版实测不成立，默认关）。</summary>
    public bool RimOutermost = false;

    /// <summary>R53 坑沿之上的煤照出煤台阶（原版默认开）。</summary>
    public bool CoalAboveRim = true;

    /// <summary>R54 解除坑沿对岩台阶的封顶：岩台阶堆到帮顶（原版默认开）。</summary>
    public bool RockAboveRim = true;

    /// <summary>标高格基准 z：台阶标高 = BenchGridAnchorZ + k × RockBenchH；≤0 = 绝对标高整数倍（原版默认）。</summary>
    public double BenchGridAnchorZ = 0;
}
