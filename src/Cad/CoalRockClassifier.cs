using System;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 煤/岩判别器 —— 忠实移植原 BlockModelLib.Domain.CoalRockClassifier: 按显式类别码集(多层煤/多层岩)
/// 或单煤值容差, 判某属性取值属煤 / 属岩 / 忽略。供分类块体(尤类别型模型: 属性为岩性码而非品位)。
///   IsCoal(v): v 在容差内命中任一煤码;
///   IsRock(v): 非煤 → 岩集合空则"非煤即岩", 非空则须命中岩码(既非煤又非岩 = 忽略)。
/// 纯逻辑、可单测。与 Kylin 既有"品位≥限值=煤"互补(后者对连续品位, 此对离散码)。
/// </summary>
public sealed class CoalRockClassifier
{
    public double[] CoalCodes = Array.Empty<double>();   // 属煤的取值/类别码(可多层)
    public double[] RockCodes = Array.Empty<double>();   // 属岩的取值/类别码(可多层); 空 = 非煤即岩
    public double Tol = 0.5;

    public bool IsCoal(double v)
    {
        foreach (var c in CoalCodes) if (Math.Abs(v - c) <= Tol) return true;
        return false;
    }

    /// <summary>是否属岩。岩集合为空 → 非煤即岩; 非空 → 须命中岩集合(既非煤又非岩 = 忽略)。</summary>
    public bool IsRock(double v)
    {
        if (IsCoal(v)) return false;
        if (RockCodes.Length == 0) return true;
        foreach (var r in RockCodes) if (Math.Abs(v - r) <= Tol) return true;
        return false;
    }
}
