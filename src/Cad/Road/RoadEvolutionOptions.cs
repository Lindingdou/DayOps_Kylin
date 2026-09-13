// 忠实移植自原 PitMine3D Modules/RoadLib/Evolution/RoadEvolutionOptions.cs（逐行对应；仅命名空间/依赖适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
namespace PitMine3D.Kylin.Cad.Road;

/// <summary>
/// 两期演化判定参数。全部带务实默认值（吸纳台阶推进的横移 + 提取抖动）；
/// 真实矿区标定时只需调这几个阈值，无需改算法。
/// </summary>
public sealed class RoadEvolutionOptions
{
    /// <summary>重采样步长 m（沿中线打密点判覆盖）。默认 2 m。</summary>
    public double SampleStepM { get; set; } = 2.0;

    /// <summary>匹配横向容差 m：两期中线平面距 ≤ 此值才视为"可能同一条"。默认 8 m。</summary>
    public double MatchToleranceM { get; set; } = 8.0;

    /// <summary>走向夹角阈值°：超过则即使近也不算同一条（挡住垂直穿越/交叉）。默认 35°。</summary>
    public double MatchAngleDeg { get; set; } = 35.0;

    /// <summary>"新建入网"判据：本期边被上期覆盖比例 &lt; 此值 → 整条出一行（不再拆段）。默认 0.35。</summary>
    public double NewCoverFrac { get; set; } = 0.35;

    /// <summary>"已废除"判据：上期边被本期覆盖比例 &lt; 此值 → 整条出一行（不再拆段）。默认 0.40。</summary>
    public double GoneCoverFrac { get; set; } = 0.40;

    /// <summary>
    /// 最小段长 m（RE5）：短于它的游程并入相邻段，滤掉提取抖动切出来的碎段。
    /// 也就是"多长的一截才值得单独报一行"。默认 15 m。
    /// </summary>
    public double GrowMinLenM { get; set; } = 15.0;

    /// <summary>
    /// 移位阈值 m（RE7）：共有段的带符号横移中位数 ≥ 此值 → 判「移位」而不是「保持」。
    ///
    /// <b>为什么与匹配容差分开</b>：匹配容差(8m)回答的是"是不是同一条路"，它必须宽——
    /// 宽到能吸收台阶推进的横移与提取抖动，否则同一条路会被拆成"新建+废除"两条。
    /// 而"变没变"要窄：容差内一律算没变的话，一次台阶推进（往往就是几米）永远不报。
    /// 两个问题两个阈值。默认 3 m。
    /// </summary>
    public double ShiftThresholdM { get; set; } = 3.0;

    /// <summary>(可选) 推进方向先验单位向量(XY)；提供后给延伸方向打"合/逆推进"标。null=不校验（仅作证据，不改分类）。</summary>
    public (double X, double Y)? AdvanceDirXY { get; set; }
}
