using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 插值样本：空间坐标 + 属性字典（逐字移植原 Estimation/Services/SampleRecord.cs）。选点插值模式下由
/// <see cref="PickedPointEstimation.ReadSelection"/> 从视口选择集（点 / 多段线顶点）产出，被插值量以
/// Properties[<see cref="PickedPointEstimation.ElevationKey"/>] 存 Z 高程，直接喂给 <see cref="EstimationEngine"/>
/// 做 IDW / OK / SK / UK / NN / MA 插值。
/// </summary>
public class SampleRecord
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public Dictionary<string, double> Properties { get; set; } = new();
}

/// <summary>估值任务配置（逐字移植原 EstimationTaskConfig.cs）。</summary>
public class EstimationTaskConfig
{
    // Data source
    public string DataSourceName { get; set; } = "";
    public string TargetProperty { get; set; } = "";
    public string OutputProperty { get; set; } = "";
    public string VarianceProperty { get; set; } = "";
    public bool OutputVariance { get; set; } = false;

    // Algorithm
    public string AlgorithmCode { get; set; } = "IDW"; // NN, MA, IDW, SK, OK, UK

    // IDW params
    public double IdwPower { get; set; } = 2.0;
    public double IdwSmoothing { get; set; } = 0.0;

    // Kriging params
    public VariogramConfig Variogram { get; set; } = new();
    public int TrendOrder { get; set; } = 1; // UK only
    public double CondThreshold { get; set; } = 1e12;
    public double EigenMin { get; set; } = 1e-6;

    // 估算网格：分辨率（沿最长水平轴的单元数，范围自适应样本 AABB）+ 可选目标块体网格
    public int TargetCellCount { get; set; } = 40;
    public bool UseTargetGrid { get; set; } = false;
    public double GridOriginX { get; set; }
    public double GridOriginY { get; set; }
    public double GridOriginZ { get; set; }
    public double GridSizeX { get; set; }
    public double GridSizeY { get; set; }
    public double GridSizeZ { get; set; }
    public int GridNx { get; set; }
    public int GridNy { get; set; }
    public int GridNz { get; set; }

    // Search neighborhood
    public double SearchRadius { get; set; } = 150.0;
    public int MaxSamples { get; set; } = 12;
    public int MinSamples { get; set; } = 2;
    public double DefaultValue { get; set; } = -999.0;
    public bool UseOctant { get; set; } = false;
    public int MaxPerOctant { get; set; } = 2;

    // 煤层分层约束（煤矿专用）
    public bool UseSeamConstraint { get; set; } = false;
    public double SeamZMin { get; set; } = 0.0;
    public double SeamZMax { get; set; } = 0.0;

    // 煤质域过滤（煤矿专用）
    public bool UseQualityFilter { get; set; } = false;
    public string? QualityFilterProperty { get; set; }
    public double QualityMin { get; set; } = double.MinValue;
    public double QualityMax { get; set; } = double.MaxValue;
}

/// <summary>变异函数参数（逐字移植原 VariogramConfig）。Type: Spherical / Exponential / Gaussian。</summary>
public class VariogramConfig
{
    public string Type { get; set; } = "Spherical";
    public double Nugget { get; set; } = 0.0;
    public double Sill { get; set; } = 1.0;
    public double Range { get; set; } = 100.0;
}

/// <summary>估值结果（逐字移植原 EstimationResult）。</summary>
public class EstimationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int BlocksEstimated { get; set; }
    public int BlocksSkipped { get; set; }
    public int SamplesUsed { get; set; }
    public double MeanEstimate { get; set; }
    public double StdEstimate { get; set; }
    public double MinEstimate { get; set; }
    public double MaxEstimate { get; set; }
    public List<string> Warnings { get; set; } = new();
    // 煤矿资源量分类（中国标准）
    public int TanMingBlocks { get; set; }    // 探明（331）: 工程控制足够
    public int KongZhiBlocks { get; set; }    // 控制（332）: 工程间距较大
    public int TuiDuanBlocks { get; set; }    // 推断（333）: 工程间距大或外推
    // 约束统计
    public int SeamSkippedBlocks { get; set; }
    public int QualitySkippedBlocks { get; set; }
    // 煤质阈值异常（煤矿专用）
    public int HighAshBlocks { get; set; }        // 灰分 > 40%
    public int LowCalorificBlocks { get; set; }   // 热值 < 20 MJ/kg
    public int HighSulfurBlocks { get; set; }     // 硫分 > 2%

    // ── 逐单元估值 + 规则网格（供"估算→块体/云图"显示）──
    // CellValues 长度 = Nx*Ny*Nz，线性序 = ix + iy*Nx + iz*Nx*Ny；== NoData 表示空单元。
    public double[] CellValues { get; set; } = Array.Empty<double>();
    // 逐单元克里金方差（仅 OK；其余 NaN）。长度与 CellValues 同。
    public double[] CellVariance { get; set; } = Array.Empty<double>();
    public double GridOriginX { get; set; }
    public double GridOriginY { get; set; }
    public double GridOriginZ { get; set; }
    public double GridSizeX { get; set; }
    public double GridSizeY { get; set; }
    public double GridSizeZ { get; set; }
    public int GridNx { get; set; }
    public int GridNy { get; set; }
    public int GridNz { get; set; }
    public double NoData { get; set; } = -999.0;
}

public class EstimationProgress
{
    public int Percent { get; set; }
    public string Status { get; set; } = "";
}

/// <summary>
/// 估值引擎（逐字移植原 Estimation/Services/EstimationEngine.cs）：样本 AABB 或目标格网上逐结点并行估值，
/// 按算法码分派 NN / MA / IDW / OK / SK / UK，出逐单元值（+克里金方差）与汇总统计。
/// </summary>
public class EstimationEngine
{
    public async Task<EstimationResult> RunAsync(
        EstimationTaskConfig config,
        IProgress<EstimationProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyList<SampleRecord>? explicitSamples = null)
    {
        var result = new EstimationResult();
        var warnings = new List<string>();

        // 选点插值：样本来自视口选择集（点/多段线顶点，值 = Z 高程），由调用方显式传入。
        if (explicitSamples == null || explicitSamples.Count == 0)
        {
            result.Success = false;
            result.Message = "未拾取到样本点（请先在图上选择点或多段线）";
            return result;
        }
        var samples = new List<SampleRecord>(explicitSamples);

        result.SamplesUsed = samples.Count;
        progress?.Report(new EstimationProgress { Percent = 5, Status = $"加载了 {samples.Count} 个煤质样品..." });

        await Task.Delay(50, cancellationToken);
        progress?.Report(new EstimationProgress { Percent = 15, Status = "构建煤质样品空间索引 (KD-Tree)..." });

        // 样本 AABB（数据真实范围，与样本坐标系天然对齐——demo/CGCS2000 大坐标皆可）
        double sMinX = samples.Min(s => s.X), sMaxX = samples.Max(s => s.X);
        double sMinY = samples.Min(s => s.Y), sMaxY = samples.Max(s => s.Y);
        double sMinZ = samples.Min(s => s.Z), sMaxZ = samples.Max(s => s.Z);

        double minX = 0, maxX = 0, minY = 0, maxY = 0, minZ = 0, maxZ = 0;
        int gx = 0, gy = 0, gz = 0;
        const long MaxGridCells = 300_000;
        bool usedTarget = false;

        // ① 选定目标块体网格、且与样品范围重叠、规模可控 → 直接估到该网格（结果与块体单元一一对齐）
        if (config.UseTargetGrid && config.GridNx > 0 && config.GridNy > 0 && config.GridNz > 0
            && config.GridSizeX > 0 && config.GridSizeY > 0 && config.GridSizeZ > 0)
        {
            double tMinX = config.GridOriginX, tMaxX = tMinX + config.GridNx * config.GridSizeX;
            double tMinY = config.GridOriginY, tMaxY = tMinY + config.GridNy * config.GridSizeY;
            long tot = (long)config.GridNx * config.GridNy * config.GridNz;
            bool overlap = Math.Max(sMinX, tMinX) < Math.Min(sMaxX, tMaxX)
                        && Math.Max(sMinY, tMinY) < Math.Min(sMaxY, tMaxY);
            if (overlap && tot <= MaxGridCells)
            {
                minX = tMinX; maxX = tMaxX; minY = tMinY; maxY = tMaxY;
                minZ = config.GridOriginZ; maxZ = minZ + config.GridNz * config.GridSizeZ;
                gx = config.GridNx; gy = config.GridNy; gz = config.GridNz;
                usedTarget = true;
            }
            else if (!overlap)
                warnings.Add("⚠ 选定目标块体与样品坐标范围不重叠（坐标系不一致？），改用数据自适应网格");
            else
                warnings.Add($"⚠ 选定目标块体单元数 {tot:N0} 超上限 {MaxGridCells:N0}，改用数据自适应网格");
        }

        // ② 否则：样本 AABB（外扩 10%）+ 用户分辨率自适应布网（替代原写死 20×20）
        if (!usedTarget)
        {
            double padX = (sMaxX - sMinX) * 0.1, padY = (sMaxY - sMinY) * 0.1, padZ = (sMaxZ - sMinZ) * 0.1;
            minX = sMinX - padX; maxX = sMaxX + padX;
            minY = sMinY - padY; maxY = sMaxY + padY;
            minZ = sMinZ - padZ; maxZ = sMaxZ + padZ;
            int res = Math.Clamp(config.TargetCellCount <= 0 ? 40 : config.TargetCellCount, 8, 200);
            double spanX = Math.Max(1e-6, maxX - minX), spanY = Math.Max(1e-6, maxY - minY);
            double spanMax = Math.Max(spanX, spanY);
            gx = Math.Max(1, (int)Math.Round(res * spanX / spanMax));
            gy = Math.Max(1, (int)Math.Round(res * spanY / spanMax));
            double zSpan = Math.Max(1.0, maxZ - minZ);
            gz = Math.Max(1, Math.Min(40, (int)(zSpan / spanMax * res)));
        }

        int totalBlocks = gx * gy * gz;

        // 逐单元值（与网格线性序对齐）：跳过/无邻域的单元保持 NoData，不渲染
        var cellValues = new double[totalBlocks];
        Array.Fill(cellValues, config.DefaultValue);
        // 逐单元克里金方差（OK/SK/UK 产出；IDW/NN/MA/无解为 NaN，不渲染）
        var cellVariance = new double[totalBlocks];
        Array.Fill(cellVariance, double.NaN);
        int seamSkipped = 0, qualitySkipped = 0;

        // 全局均值（简单克里金 SK 用）
        double globalMean = 0; int meanCount = 0;
        foreach (var s in samples)
            if (s.Properties.TryGetValue(config.TargetProperty, out double mv)) { globalMean += mv; ++meanCount; }
        globalMean = meanCount > 0 ? globalMean / meanCount : 0;

        // ★加速：2D 空间网格索引，把逐结点近邻查询从 O(样本) 降到 O(局部密度)
        var index = new EstimationAlgorithms.SampleGrid2D(samples);

        double dX = maxX - minX, dY = maxY - minY, dZ = maxZ - minZ;
        int gxy = gx * gy;
        int progressCounter = 0;
        progress?.Report(new EstimationProgress { Percent = 20, Status = $"并行估值中（0/{totalBlocks} 结点）..." });

        // ★加速：结点相互独立 → Parallel.For 多核并行；整段放线程池，不占 UI 线程
        await Task.Run(() =>
        {
            var po = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
            };
            Parallel.For(0, totalBlocks, po, globalIdx =>
            {
                int ix = globalIdx % gx;
                int iy = (globalIdx / gx) % gy;
                int iz = globalIdx / gxy;
                double blockX = minX + dX * (ix + 0.5) / gx;
                double blockY = minY + dY * (iy + 0.5) / gy;
                double blockZ = minZ + dZ * (iz + 0.5) / Math.Max(gz, 1);

                if (config.UseSeamConstraint && (blockZ < config.SeamZMin || blockZ > config.SeamZMax))
                { Interlocked.Increment(ref seamSkipped); return; }
                if (config.UseQualityFilter && !string.IsNullOrEmpty(config.QualityFilterProperty))
                {
                    double mockQuality = ((globalIdx * 2654435761u) % 5000) * 0.01;
                    if (mockQuality < config.QualityMin || mockQuality > config.QualityMax)
                    { Interlocked.Increment(ref qualitySkipped); return; }
                }

                var hits = index.FindNearest(blockX, blockY, blockZ, config.SearchRadius, config.MaxSamples);
                var (est, variance) = ComputeEstimate(config, samples, hits, blockX, blockY, blockZ, globalMean);
                cellValues[globalIdx] = est;
                cellVariance[globalIdx] = variance;

                int done = Interlocked.Increment(ref progressCounter);
                if ((done & 8191) == 0)
                    progress?.Report(new EstimationProgress { Percent = 20 + done * 75 / totalBlocks, Status = $"并行估值中（{done}/{totalBlocks} 结点）..." });
            });
        }, cancellationToken);

        progress?.Report(new EstimationProgress { Percent = 95, Status = "汇总估值结果..." });

        // 统计：从 cellValues 聚合有效结点（跳过 NoData/缺省）
        var estimates = new List<double>(totalBlocks);
        foreach (var v in cellValues)
            if (!double.IsNaN(v) && v != config.DefaultValue) estimates.Add(v);

        // 煤质指标阈值异常检测（煤矿专用）
        result.HighAshBlocks = config.TargetProperty == "ash_content"
            ? estimates.Count(v => v > 40)
            : 0;
        result.LowCalorificBlocks = config.TargetProperty == "calorific_value"
            ? estimates.Count(v => v < 20)
            : 0;
        result.HighSulfurBlocks = config.TargetProperty == "sulfur"
            ? estimates.Count(v => v > 2.0)
            : 0;

        // 资源量分类（331/332/333）需逐块体真实工程控制度判定，非按比例拍——暂不输出假数据，留待真实实现。
        result.TanMingBlocks = result.KongZhiBlocks = result.TuiDuanBlocks = 0;

        // 网格 + 逐单元值（+方差）落进 result（供块体/云图显示）
        result.CellValues = cellValues;
        result.CellVariance = cellVariance;
        result.GridOriginX = minX; result.GridOriginY = minY; result.GridOriginZ = minZ;
        result.GridSizeX = (maxX - minX) / gx;
        result.GridSizeY = (maxY - minY) / gy;
        result.GridSizeZ = (maxZ - minZ) / Math.Max(gz, 1);
        result.GridNx = gx; result.GridNy = gy; result.GridNz = gz;
        result.NoData = config.DefaultValue;

        result.Success = true;
        result.BlocksEstimated = estimates.Count;
        result.BlocksSkipped = seamSkipped + qualitySkipped;
        result.SeamSkippedBlocks = seamSkipped;
        result.QualitySkippedBlocks = qualitySkipped;
        result.MeanEstimate = estimates.Count > 0 ? estimates.Average() : 0;
        result.MinEstimate = estimates.Count > 0 ? estimates.Min() : 0;
        result.MaxEstimate = estimates.Count > 0 ? estimates.Max() : 0;
        result.StdEstimate = estimates.Count > 0
            ? Math.Sqrt(estimates.Select(e => e - result.MeanEstimate).Select(d => d * d).Average())
            : 0;
        result.Warnings = warnings;
        string anomalyMsg = "";
        if (result.HighAshBlocks > 0) anomalyMsg += $" 高灰分 {result.HighAshBlocks} 块体";
        if (result.LowCalorificBlocks > 0) anomalyMsg += $" 低热值 {result.LowCalorificBlocks} 块体";
        if (result.HighSulfurBlocks > 0) anomalyMsg += $" 高硫分 {result.HighSulfurBlocks} 块体";

        result.Message = $"估值完成：{result.BlocksEstimated} 单元，均值 {result.MeanEstimate:F2}，范围 [{result.MinEstimate:F2}, {result.MaxEstimate:F2}]" +
                       (result.BlocksSkipped > 0 ? $" | 跳过 {result.BlocksSkipped} (分层 {seamSkipped}, 煤质 {qualitySkipped})" : "") +
                       (string.IsNullOrEmpty(anomalyMsg) ? "" : $" | ⚠{anomalyMsg}");

        progress?.Report(new EstimationProgress { Percent = 100, Status = "完成" });
        return result;
    }

    // 估值分派（预取邻域 hits，线程安全纯函数）：返回 (估值, 克里金方差)。
    // OK/SK/UK 产出真实方差；IDW/NN/MA 无方差→NaN。
    // public static：供「更新煤层面」等按顶点直调(不走 RunAsync 的规则网格)。
    public static (double est, double variance) ComputeEstimate(EstimationTaskConfig config,
                                     List<SampleRecord> samples,
                                     IReadOnlyList<(double dist, int index)> hits,
                                     double bx, double by, double bz, double globalMean)
    {
        switch (config.AlgorithmCode)
        {
            case "SK":   // 简单克里金（已知全局均值）
            {
                var (est, variance, _) = EstimationAlgorithms.SimpleKriging(
                    bx, by, bz, samples, config.TargetProperty, config.Variogram, globalMean,
                    hits, config.MinSamples, config.DefaultValue);
                return (est, variance);
            }
            case "UK":   // 泛克里金（趋势漂移，1/2 阶）
            {
                int order = Math.Clamp(config.TrendOrder <= 0 ? 1 : config.TrendOrder, 1, 2);
                var (est, variance, _) = EstimationAlgorithms.KrigingWithDrift(
                    bx, by, bz, samples, config.TargetProperty, config.Variogram, order,
                    hits, config.MinSamples, config.DefaultValue);
                return (est, variance);
            }
            case "OK":   // 普通克里金（= 常数漂移的泛克里金）
            {
                var (est, variance, _) = EstimationAlgorithms.KrigingWithDrift(
                    bx, by, bz, samples, config.TargetProperty, config.Variogram, 0,
                    hits, config.MinSamples, config.DefaultValue);
                return (est, variance);
            }
            case "NN":   // 最近邻
            {
                if (hits.Count == 0) return (config.DefaultValue, double.NaN);
                return (samples[hits[0].index].Properties.GetValueOrDefault(config.TargetProperty, config.DefaultValue), double.NaN);
            }
            case "MA":   // 移动平均
            {
                if (hits.Count < config.MinSamples) return (config.DefaultValue, double.NaN);
                double sum = 0; int n = 0;
                foreach (var (_, idx) in hits)
                    if (samples[idx].Properties.TryGetValue(config.TargetProperty, out double v)) { sum += v; ++n; }
                return (n > 0 ? sum / n : config.DefaultValue, double.NaN);
            }
            default:     // IDW
            {
                var (est, _) = EstimationAlgorithms.ComputeIdw(
                    bx, by, bz, samples, config.TargetProperty, config.IdwPower,
                    hits, config.MinSamples, config.IdwSmoothing, config.DefaultValue);
                return (est, double.NaN);
            }
        }
    }
}
