// 忠实移植自原 PitMine3D Modules/GeoDataBase/Public/Services/IInterpolationServiceHook.cs（逐行对应；仅命名空间/依赖适配）
using System.Collections.Generic;

namespace PitMine3D.Kylin.Data.Services;

/// <summary>
/// 煤质空间窗的"插值服务"挂点接口。
/// 本模块自己只定义契约 + 默认 IDW 实现兜底；
/// 等系统级 IInterpolationService（含 OK、Co-Kriging 等）就绪后，
/// 将本接口实现委托过去即可，UI 不变。
/// </summary>
public interface IInterpolationServiceHook
{
    /// <summary>支持的方法清单（给 UI 下拉用）。</summary>
    IReadOnlyList<string> AvailableMethods { get; }

    /// <summary>
    /// 在指定网格上对 (x, y, z, v) 数据点做插值。
    /// 返回 (x, y, z, value) 的体素阵列；Bounds 之外的格点会被丢弃。
    /// </summary>
    /// <param name="points">控制点 (X, Y, Z, V)。</param>
    /// <param name="method">如 "IDW" / "RBF" / "OK"。</param>
    /// <param name="bounds">包围盒 (xMin, xMax, yMin, yMax, zMin, zMax)。</param>
    /// <param name="resolution">网格分辨率（m）。</param>
    /// <param name="parameters">算法参数（如 IDW 的 p, k）。</param>
    IReadOnlyList<InterpolatedVoxel> Interpolate(
        IReadOnlyList<ControlPoint> points,
        string method,
        SpatialBounds bounds,
        double resolution,
        IDictionary<string, double>? parameters = null);

    /// <summary>
    /// 在给定目标点上直接估值（供块体 cell 中心等任意点，非规则网格）。
    /// 返回与 targets 一一对应的估计值；半径内无数据支撑返回 null（对应"不赋值"）。
    /// 搜索半径 / 变差函数由控制点自身范围自动定（可经 parameters 覆盖）。
    /// </summary>
    IReadOnlyList<double?> EstimateAt(
        IReadOnlyList<ControlPoint> points,
        IReadOnlyList<TargetPoint> targets,
        string method,
        IDictionary<string, double>? parameters = null);

    /// <summary>
    /// 与 <see cref="EstimateAt"/> 同，但同时返回**克里金方差**（普通克里金才有，其余方法方差为 null）。
    /// 供块体煤质模型写"估值 + 可信度"双列：方差越大 → 该处质量估得越不准。半径外返回 (null, null)。
    /// </summary>
    IReadOnlyList<EstimatedValue> EstimateWithVariance(
        IReadOnlyList<ControlPoint> points,
        IReadOnlyList<TargetPoint> targets,
        string method,
        IDictionary<string, double>? parameters = null);

    /// <summary>
    /// 留一交叉验证（LOO）：逐个剔除控制点、用其余点估其位置，比对预测↔实测。
    /// 证明质量估值模型是否可信（均值误差≈0 无偏、RMSE 小、标准化误差方差≈1 则方差模型合理）。
    /// </summary>
    CrossValidationResult CrossValidate(
        IReadOnlyList<ControlPoint> points,
        string method,
        IDictionary<string, double>? parameters = null);
}

/// <summary>带方差的估值结果：Value=估计值，Variance=克里金方差（仅 OK 有，其余 null）。</summary>
public sealed record EstimatedValue(double? Value, double? Variance);

/// <summary>交叉验证单点：实测 vs 预测 vs 标准化误差（= 误差/√克里金方差，仅 OK 有）。</summary>
public sealed record CrossValidationPair(double Actual, double Predicted, double? StdError);

/// <summary>
/// 交叉验证汇总。<paramref name="MeanError"/>≈0 表示无偏；<paramref name="RmsError"/> 越小越准；
/// <paramref name="StdErrorVariance"/>≈1（配合 <paramref name="MeanStdError"/>≈0）表示克里金方差量级合理。
/// </summary>
public sealed record CrossValidationResult(
    int N, int Predicted,
    double MeanError, double RmsError, double MeanAbsError,
    double? MeanStdError, double? StdErrorVariance, double R2,
    IReadOnlyList<CrossValidationPair> Pairs);

/// <summary>控制点 (X, Y, Z, V)。</summary>
public sealed record ControlPoint(double X, double Y, double Z, double V);

/// <summary>估值目标点 (X, Y, Z)——如块体 cell 中心。</summary>
public sealed record TargetPoint(double X, double Y, double Z);

/// <summary>插值结果体素。</summary>
public sealed record InterpolatedVoxel(double X, double Y, double Z, double Value, double? Variance);

/// <summary>3D 包围盒。</summary>
public sealed record SpatialBounds(
    double XMin, double XMax,
    double YMin, double YMax,
    double ZMin, double ZMax);
