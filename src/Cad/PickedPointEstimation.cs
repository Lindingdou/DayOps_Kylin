using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad;

/// <summary>
/// 「快速估值 / 克里金估值」选点插值窗口的纯逻辑（忠实原 PickedPointSampler / RangePicker /
/// QuickEstimateViewModel / KrigingViewModel 中不依赖 WPF 与宿主的部分）：
/// 选择集 → 样本、Z 拍平、建议半径/变异函数、固定间距布网、结果格网 → 结点、摘要文案。
/// </summary>
public static class PickedPointEstimation
{
    /// <summary>被插值属性键：高程。样本 Properties[ElevationKey] == 样本 Z。</summary>
    public const string ElevationKey = "高程";

    /// <summary>均匀格网点数上限：超过则自动放大间距（与引擎 MaxGridCells 对齐留余量）。</summary>
    public const long MaxGridPoints = 250_000;

    /// <summary>选择集里的点(x,y,z) 与多段线(逐顶点 x,y,z) → 样本（值 = Z）。忠实原 PickedPointSampler.ReadSelection。</summary>
    public static List<SampleRecord> ReadSelection(
        IEnumerable<(double x, double y, double z)> points,
        IEnumerable<IReadOnlyList<(double x, double y, double z)>> polylines)
    {
        var samples = new List<SampleRecord>();
        foreach (var poly in polylines)
            foreach (var (x, y, z) in poly) Add(samples, x, y, z);
        foreach (var (x, y, z) in points) Add(samples, x, y, z);
        return samples;
    }

    private static void Add(List<SampleRecord> list, double x, double y, double z)
    {
        var rec = new SampleRecord { X = x, Y = y, Z = z };
        rec.Properties[ElevationKey] = z;
        list.Add(rec);
    }

    /// <summary>
    /// 水平插值：把样本用于测距的 Z 拍平为常量（保留真实高程作被插值量），
    /// 使 NN/MA/IDW/克里金只按水平距离加权，且自适应网格自然坍缩为单层 (gz=1)。
    /// </summary>
    public static List<SampleRecord> Flatten(IEnumerable<SampleRecord> samples)
        => samples.Select(s =>
        {
            var r = new SampleRecord { X = s.X, Y = s.Y, Z = 0.0 };
            r.Properties[ElevationKey] = s.Z;
            return r;
        }).ToList();

    /// <summary>样点平均最近邻水平距（子采样 ≤100 点，避免 O(n²) 爆）。用于推荐间距/搜索半径。</summary>
    public static double AverageNearestNeighbor(List<SampleRecord> samples)
    {
        int n = Math.Min(100, samples.Count);
        if (n < 2) return 0;
        var pts = samples.Take(n).Select(s => (s.X, s.Y)).ToList();
        double total = 0; int cnt = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            double best = double.MaxValue;
            for (int j = 0; j < pts.Count; j++)
            {
                if (i == j) continue;
                double dx = pts[i].X - pts[j].X, dy = pts[i].Y - pts[j].Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < best) best = d;
            }
            if (best < double.MaxValue) { total += best; cnt++; }
        }
        return cnt > 0 ? total / cnt : 0;
    }

    /// <summary>读取选择集后自动建议搜索半径：max(4×平均最近邻距, 0.15×最大跨度)，保证范围内格网填满；无法建议返回 null。</summary>
    public static double? SuggestSearchRadius(List<SampleRecord> samples)
    {
        if (samples.Count == 0) return null;
        double avgNn = AverageNearestNeighbor(samples);
        double spanX = samples.Max(s => s.X) - samples.Min(s => s.X);
        double spanY = samples.Max(s => s.Y) - samples.Min(s => s.Y);
        double spanMax = Math.Max(spanX, spanY);
        if (avgNn <= 0) return null;
        return Math.Round(Math.Max(avgNn * 4.0, spanMax * 0.15), 1);
    }

    /// <summary>变异函数按 Z 的样本统计给建议值（基台≈方差，块金≈15%，变程≈平均点距×4；点距无法估时 range 为 null）。样本 &lt;2 返回 null。</summary>
    public static (double sill, double nugget, double? range)? SuggestVariogram(List<SampleRecord> samples)
    {
        var zs = samples.Select(s => s.Z).ToArray();
        if (zs.Length <= 1) return null;
        double mean = zs.Average();
        double variance = zs.Select(v => v - mean).Select(d => d * d).Average();
        double avgNn = AverageNearestNeighbor(samples);
        return (Math.Round(variance, 3), Math.Round(variance * 0.15, 3), avgNn > 0 ? Math.Round(avgNn * 4.0, 1) : null);
    }

    /// <summary>选择集样本摘要（原 PickedSummary）。</summary>
    public static string PickedSummary(List<SampleRecord> samples)
    {
        if (samples.Count == 0) return "选择集为空 —— 请先在视口选中点 / 多段线";
        var zs = samples.Select(s => s.Z).ToArray();
        return $"选择集样点 {zs.Length} 个  |  Z ∈ [{zs.Min():F2}, {zs.Max():F2}]  均值 {zs.Average():F2}";
    }

    /// <summary>框选范围摘要（原 RangePicker.Summary）。range=null 表示未框选。</summary>
    public static string RangeSummary((double minX, double minY, double maxX, double maxY)? range)
        => range is { } r
            ? $"框选范围  X[{r.minX:F1}, {r.maxX:F1}]  Y[{r.minY:F1}, {r.maxY:F1}]"
            : "未框选 —— 默认用样本点包围盒";

    /// <summary>
    /// 把插值范围(框选矩形或样本包围盒)按固定间距均匀布网（正方形）写进 cfg（原 BuildConfig 的格网部分）：
    /// 点数上限保护——间距太小则放大到刚好不超上限（返回实际用的间距供回填 UI）；
    /// 框选范围要铺满——搜索半径至少覆盖 (框选∪样本) 的整个跨度，否则远离样本的结点邻域为空。
    /// </summary>
    public static double ApplyGrid(EstimationTaskConfig cfg, List<SampleRecord> samples, double gridSpacing,
        (double minX, double minY, double maxX, double maxY)? range)
    {
        double minX, maxX, minY, maxY;
        if (range is { } r) { minX = r.minX; maxX = r.maxX; minY = r.minY; maxY = r.maxY; }
        else
        {
            minX = samples.Min(s => s.X); maxX = samples.Max(s => s.X);
            minY = samples.Min(s => s.Y); maxY = samples.Max(s => s.Y);
        }
        double spanX = Math.Max(1e-6, maxX - minX), spanY = Math.Max(1e-6, maxY - minY);

        double spacing = Math.Max(1e-6, gridSpacing);
        double minSpacing = Math.Sqrt(spanX * spanY / MaxGridPoints);
        if (spacing < minSpacing) spacing = Math.Ceiling(minSpacing);

        int nx = Math.Max(1, (int)Math.Ceiling(spanX / spacing));
        int ny = Math.Max(1, (int)Math.Ceiling(spanY / spacing));

        cfg.UseTargetGrid = true;
        cfg.GridOriginX = minX; cfg.GridOriginY = minY; cfg.GridOriginZ = -0.5; // 单层，结点 Z≈0 对齐拍平样本
        cfg.GridSizeX = spacing; cfg.GridSizeY = spacing; cfg.GridSizeZ = 1.0;
        cfg.GridNx = nx; cfg.GridNy = ny; cfg.GridNz = 1;

        if (range != null && samples.Count > 0)
        {
            double sMinX = samples.Min(s => s.X), sMaxX = samples.Max(s => s.X);
            double sMinY = samples.Min(s => s.Y), sMaxY = samples.Max(s => s.Y);
            double uSpanX = Math.Max(maxX, sMaxX) - Math.Min(minX, sMinX);
            double uSpanY = Math.Max(maxY, sMaxY) - Math.Min(minY, sMinY);
            double cover = Math.Sqrt(uSpanX * uSpanX + uSpanY * uSpanY);
            if (cfg.SearchRadius < cover) cfg.SearchRadius = cover;
        }
        return spacing;
    }

    /// <summary>估值格网的每个有效结点 → (XY = 结点中心, Z = 插值高程)；多层则沿列取有效均值（原 InsertGridPoints 的结点枚举）。</summary>
    public static List<(double x, double y, double z)> GridNodes(EstimationResult r)
    {
        var list = new List<(double, double, double)>();
        int nx = r.GridNx, ny = r.GridNy, nz = Math.Max(1, r.GridNz);
        if (r.CellValues.Length < nx * ny * nz) return list;
        for (int iy = 0; iy < ny; iy++)
        for (int ix = 0; ix < nx; ix++)
        {
            double sum = 0; int cnt = 0;
            for (int iz = 0; iz < nz; iz++)
            {
                double v = r.CellValues[ix + iy * nx + iz * nx * ny];
                if (double.IsNaN(v) || v == r.NoData) continue;
                sum += v; cnt++;
            }
            if (cnt == 0) continue;
            double z = sum / cnt;
            double x = r.GridOriginX + r.GridSizeX * (ix + 0.5);
            double y = r.GridOriginY + r.GridSizeY * (iy + 0.5);
            list.Add((x, y, z));
        }
        return list;
    }

    /// <summary>插入十字点的标记大小：网格步长的一小份，保证可见又不糊成一片。</summary>
    public static double MarkerSize(EstimationResult r) => Math.Max(0.5, Math.Min(r.GridSizeX, r.GridSizeY) * 0.15);

    /// <summary>变异函数 γ(h)（原 VariogramEditor.ComputeGamma；与 EstimationAlgorithms.Gamma 同式，type 0/1/2 = 球状/指数/高斯）。</summary>
    public static double VariogramGamma(int type, double h, double nugget, double sill, double range)
    {
        double c = sill - nugget;
        if (h <= 0) return 0;
        switch (type)
        {
            case 0:
                if (h >= range) return sill;
                return nugget + c * (1.5 * (h / range) - 0.5 * Math.Pow(h / range, 3));
            case 1:
                return nugget + c * (1.0 - Math.Exp(-3.0 * h / range));
            case 2:
                return nugget + c * (1.0 - Math.Exp(-3.0 * Math.Pow(h / range, 2)));
            default: return 0;
        }
    }

    /// <summary>理论曲线预览点：h ∈ [0, 2·range] 取 51 点，γ 夹到 [0, 1.2·sill]（原 DrawCurve）。</summary>
    public static List<(double h, double gamma)> VariogramCurve(int type, double nugget, double sill, double range)
    {
        var pts = new List<(double, double)>(51);
        double maxH = sill * 1.2; if (maxH <= 0) maxH = 1;
        for (int i = 0; i <= 50; i++)
        {
            double h = (i / 50.0) * range * 2.0;
            double g = VariogramGamma(type, h, nugget, sill, range);
            pts.Add((h, Math.Clamp(g, 0, maxH)));
        }
        return pts;
    }

    /// <summary>变异函数参数校验提示（原 UpdateValidation）；合法返回 ""。</summary>
    public static string VariogramValidation(double nugget, double sill, double range)
    {
        if (nugget > sill) return "⚠️ 块金值不能大于基台值";
        if (range <= 0) return "⚠️ 变程必须大于 0";
        if (sill <= 0) return "⚠️ 基台值必须大于 0";
        return "";
    }

    /// <summary>
    /// 「从样品自动拟合」（原 VariogramEditor.OnAutoFitClicked 的计算部分）：maxLag 取样本 AABB 对角线 0.5，15 个 bin，
    /// 有效 bin ≥3 才拟合 Spherical。返回 (拟合, 实验变异函数, 有效 bin 数)；样本 &lt;3 或 bin 不足时 fit=null。
    /// </summary>
    public static (VariogramConfig? fit, List<EstimationAlgorithms.ExperimentalLag> exp, int filledBins) AutoFit(List<SampleRecord> samples)
    {
        if (samples.Count < 3) return (null, new List<EstimationAlgorithms.ExperimentalLag>(), 0);
        string prop = ElevationKey;
        double minX = samples.Min(s => s.X), maxX = samples.Max(s => s.X);
        double minY = samples.Min(s => s.Y), maxY = samples.Max(s => s.Y);
        double minZ = samples.Min(s => s.Z), maxZ = samples.Max(s => s.Z);
        double diag = Math.Sqrt((maxX - minX) * (maxX - minX)
                              + (maxY - minY) * (maxY - minY)
                              + (maxZ - minZ) * (maxZ - minZ));
        double maxLag = Math.Max(diag * 0.5, 1.0);

        var exp = EstimationAlgorithms.ComputeExperimentalVariogram(samples, prop, maxLag, 15);
        int filledBins = exp.Count(b => b.Count > 0);
        if (filledBins < 3) return (null, exp, filledBins);

        var vals = samples.Select(s => s.Properties.GetValueOrDefault(prop, 0)).ToList();
        double mean = vals.Average();
        double variance = vals.Select(v => (v - mean) * (v - mean)).Average();
        return (EstimationAlgorithms.FitSpherical(exp, variance), exp, filledBins);
    }

    // ═══════════════════ 数据分析面板（原 DataAnalysisPanel 的计算部分）═══════════════════

    /// <summary>统计摘要：样品数 / 均值 / 标准差(总体) / 最小 / 最大 / 变异系数。空样本返回 null。</summary>
    public static (int count, double mean, double std, double min, double max, double cv)? Stats(double[]? samples)
    {
        if (samples == null || samples.Length == 0) return null;
        double mean = samples.Average();
        double variance = samples.Select(v => v - mean).Select(d => d * d).Average();
        double std = Math.Sqrt(variance);
        double cv = mean != 0 ? std / Math.Abs(mean) : 0;
        return (samples.Length, mean, std, samples.Min(), samples.Max(), cv);
    }

    /// <summary>直方图分箱：bins = min(20, √n)，等宽；极差为 0 返回空。返回 (箱下限, 箱上限, 频数)。</summary>
    public static List<(double lo, double hi, int count)> Histogram(double[]? samples)
    {
        var res = new List<(double, double, int)>();
        if (samples == null || samples.Length == 0) return res;
        int bins = Math.Min(20, (int)Math.Sqrt(samples.Length));
        if (bins < 1) return res;
        double min = samples.Min(), max = samples.Max();
        if (max - min < 1e-9) return res;
        double binWidth = (max - min) / bins;
        int[] counts = new int[bins];
        foreach (var v in samples)
        {
            int idx = Math.Min((int)((v - min) / binWidth), bins - 1);
            counts[idx]++;
        }
        for (int i = 0; i < bins; i++) res.Add((min + i * binWidth, min + (i + 1) * binWidth, counts[i]));
        return res;
    }

    /// <summary>累积频率分布：升序 (值, (i+1)/n)。极差为 0 返回空。</summary>
    public static List<(double value, double cdf)> Cdf(double[]? samples)
    {
        var res = new List<(double, double)>();
        if (samples == null || samples.Length == 0) return res;
        var sorted = samples.OrderBy(x => x).ToArray();
        int n = sorted.Length;
        if (sorted[^1] - sorted[0] < 1e-9) return res;
        for (int i = 0; i < n; i++) res.Add((sorted[i], (i + 1.0) / n));
        return res;
    }

    public readonly record struct SpatialPoint(double X, double Y, double Value);

    /// <summary>为每个样品生成 mock XY 坐标（基于固定种子保证可重复性；原 GenerateMockPoints 的 2000×2000 随机布点）。</summary>
    public static List<SpatialPoint> MockPoints(double[] values)
    {
        var list = new List<SpatialPoint>();
        var rand = new Random(42);
        for (int i = 0; i < values.Length; i++)
        {
            double x = rand.NextDouble() * 2000;
            double y = rand.NextDouble() * 2000;
            list.Add(new SpatialPoint(x, y, values[i]));
        }
        return list;
    }

    /// <summary>规则网格 (nx×ny) IDW(1/d²) 插值（原 IdwInterpolate 逐格）。</summary>
    public static double[,] HeatGrid(List<SpatialPoint> points, double minX, double maxX, double minY, double maxY, int nx, int ny)
    {
        var grid = new double[nx, ny];
        for (int ix = 0; ix < nx; ix++)
            for (int iy = 0; iy < ny; iy++)
            {
                double cx = minX + (ix + 0.5) / nx * (maxX - minX);
                double cy = minY + (iy + 0.5) / ny * (maxY - minY);
                double num = 0, den = 0; bool exact = false; double ev = 0;
                foreach (var p in points)
                {
                    double d = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
                    if (d < 1e-6) { exact = true; ev = p.Value; break; }
                    double w = 1.0 / (d * d);
                    num += w * p.Value;
                    den += w;
                }
                grid[ix, iy] = exact ? ev : (den > 0 ? num / den : 0);
            }
        return grid;
    }

    /// <summary>热力图配色 t∈[0,1]：深蓝→浅蓝→绿→黄→红（原 GetHeatmapColor）。</summary>
    public static (byte r, byte g, byte b) HeatmapColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (t < 0.25) return Lerp(t / 0.25, (0, 0, 139), (0, 128, 255));
        if (t < 0.50) return Lerp((t - 0.25) / 0.25, (0, 128, 255), (0, 255, 128));
        if (t < 0.75) return Lerp((t - 0.50) / 0.25, (0, 255, 128), (255, 255, 0));
        return Lerp((t - 0.75) / 0.25, (255, 255, 0), (255, 0, 0));
    }

    private static (byte, byte, byte) Lerp(double t, (int r, int g, int b) c1, (int r, int g, int b) c2)
    {
        t = Math.Clamp(t, 0, 1);
        return ((byte)(c1.r + (c2.r - c1.r) * t), (byte)(c1.g + (c2.g - c1.g) * t), (byte)(c1.b + (c2.b - c1.b) * t));
    }

    /// <summary>
    /// Marching Squares 追踪等值线（原 DataAnalysisPanel.MarchingSquares）：返回归一化 [0,1]² 坐标的线段
    /// (x 向右, y 向上; 画布时 y 翻转)。
    /// </summary>
    public static List<((double x, double y) p1, (double x, double y) p2)> MarchingSquares(double[,] grid, double level)
    {
        int nx = grid.GetLength(0), ny = grid.GetLength(1);
        var segments = new List<((double, double), (double, double))>();
        for (int ix = 0; ix < nx - 1; ix++)
        {
            for (int iy = 0; iy < ny - 1; iy++)
            {
                double v00 = grid[ix, iy];
                double v10 = grid[ix + 1, iy];
                double v01 = grid[ix, iy + 1];
                double v11 = grid[ix + 1, iy + 1];

                int caseIndex = 0;
                if (v00 >= level) caseIndex |= 1;
                if (v10 >= level) caseIndex |= 2;
                if (v11 >= level) caseIndex |= 4;
                if (v01 >= level) caseIndex |= 8;
                if (caseIndex == 0 || caseIndex == 15) continue;

                double x0 = ix / (double)nx, x1 = (ix + 1) / (double)nx;
                double y0 = iy / (double)ny, y1 = (iy + 1) / (double)ny;
                double Lerp(double a, double b) => a == b ? 0.5 : (level - a) / (b - a);
                double tTop = Lerp(v00, v10), tRight = Lerp(v10, v11), tBottom = Lerp(v01, v11), tLeft = Lerp(v00, v01);
                (double, double) Top = (x0 + tTop * (x1 - x0), y0);
                (double, double) Right = (x1, y0 + tRight * (y1 - y0));
                (double, double) Bottom = (x0 + tBottom * (x1 - x0), y1);
                (double, double) Left = (x0, y0 + tLeft * (y1 - y0));
                void AddSeg((double, double) a, (double, double) b) => segments.Add((a, b));
                switch (caseIndex)
                {
                    case 1: AddSeg(Top, Left); break;
                    case 2: AddSeg(Right, Top); break;
                    case 3: AddSeg(Right, Left); break;
                    case 4: AddSeg(Bottom, Right); break;
                    case 5: AddSeg(Top, Right); AddSeg(Bottom, Left); break;
                    case 6: AddSeg(Bottom, Top); break;
                    case 7: AddSeg(Bottom, Left); break;
                    case 8: AddSeg(Left, Bottom); break;
                    case 9: AddSeg(Top, Bottom); break;
                    case 10: AddSeg(Left, Top); AddSeg(Right, Bottom); break;
                    case 11: AddSeg(Right, Bottom); break;
                    case 12: AddSeg(Left, Right); break;
                    case 13: AddSeg(Top, Right); break;
                    case 14: AddSeg(Left, Top); break;
                }
            }
        }
        return segments;
    }
}
