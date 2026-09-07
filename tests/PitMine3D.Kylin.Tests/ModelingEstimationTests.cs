using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 地质统计学分析组（快速估值 / 克里金估值 选点插值）纯逻辑回归：
/// EstimationAlgorithms(逐字移植) · EstimationEngine 网格估值 · PickedPointEstimation(选择集→样本/布网/结点/建议值/分析面板数学)。
/// </summary>
public class ModelingEstimationTests
{
    private const string Z = PickedPointEstimation.ElevationKey;

    private static SampleRecord S(double x, double y, double z, double? v = null)
    {
        var r = new SampleRecord { X = x, Y = y, Z = z };
        r.Properties[Z] = v ?? z;
        return r;
    }

    /// <summary>5×5 规则网格(间距 10)，值 = 10 + x（线性场，克里金/IDW 内插可验）。</summary>
    private static List<SampleRecord> LinearGrid()
    {
        var pts = new List<SampleRecord>();
        for (int i = 0; i <= 4; i++)
            for (int j = 0; j <= 4; j++)
                pts.Add(S(i * 10, j * 10, 0, 10 + i * 10));
        return pts;
    }

    /// <summary>同布局但高程本身 = 10 + x（选择集样本形态：值存在 Z 里，交 Flatten 拍平）。</summary>
    private static List<SampleRecord> LinearGridZ()
    {
        var pts = new List<SampleRecord>();
        for (int i = 0; i <= 4; i++)
            for (int j = 0; j <= 4; j++)
                pts.Add(S(i * 10, j * 10, 10 + i * 10));
        return pts;
    }

    private static VariogramConfig Sph() => new() { Type = "Spherical", Nugget = 0, Sill = 100, Range = 60 };

    // ───────────── EstimationAlgorithms ─────────────

    [Fact]
    public void Gamma_three_models_match_original_formulas()
    {
        Assert.Equal(0, EstimationAlgorithms.Gamma("Spherical", 0, 1, 10, 50), 9);
        Assert.Equal(10, EstimationAlgorithms.Gamma("Spherical", 50, 1, 10, 50), 9);   // h≥range → sill
        Assert.Equal(1 + 9 * (1.5 * 0.5 - 0.5 * 0.125), EstimationAlgorithms.Gamma("Spherical", 25, 1, 10, 50), 9);
        Assert.Equal(1 + 9 * (1 - Math.Exp(-3.0)), EstimationAlgorithms.Gamma("Exponential", 50, 1, 10, 50), 9);
        Assert.Equal(1 + 9 * (1 - Math.Exp(-3.0)), EstimationAlgorithms.Gamma("Gaussian", 50, 1, 10, 50), 9);
        Assert.Equal(10, EstimationAlgorithms.Gamma("Unknown", 5, 1, 10, 50), 9);      // 未知模型 → sill
    }

    [Fact]
    public void FindNearestSamples_sorted_radius_clipped_and_capped()
    {
        var s = new List<SampleRecord> { S(0, 0, 0), S(3, 0, 0), S(1, 0, 0), S(10, 0, 0) };
        var hits = EstimationAlgorithms.FindNearestSamples(s, 0, 0, 0, 5, 2);
        Assert.Equal(2, hits.Count);
        Assert.Equal(0, hits[0].index); Assert.Equal(2, hits[1].index);   // 升序: d=0, d=1
        Assert.Equal(1, hits[1].dist, 9);
    }

    [Fact]
    public void IdwEstimate_exact_on_sample_and_weighted_between()
    {
        var s = new List<SampleRecord> { S(0, 0, 0, 10), S(10, 0, 0, 20) };
        var (onPt, used) = EstimationAlgorithms.IdwEstimate(0, 0, 0, s, Z, 2, 100, 1, 12, 0, -999);
        Assert.Equal(10, onPt, 9); Assert.Equal(1, used);
        var (mid, _) = EstimationAlgorithms.IdwEstimate(5, 0, 0, s, Z, 2, 100, 1, 12, 0, -999);
        Assert.Equal(15, mid, 9);                                       // 等距 → 均值
        var (far, n) = EstimationAlgorithms.IdwEstimate(500, 0, 0, s, Z, 2, 100, 1, 12, 0, -999);
        Assert.Equal(-999, far, 9); Assert.Equal(0, n);                 // 半径外 → 缺省值
    }

    [Fact]
    public void ExperimentalVariogram_bins_and_pair_counts()
    {
        var s = new List<SampleRecord> { S(0, 0, 0, 1), S(10, 0, 0, 3), S(20, 0, 0, 5) };
        var exp = EstimationAlgorithms.ComputeExperimentalVariogram(s, Z, 30, 3);   // bin 宽 10: [0,10) [10,20) [20,30)
        Assert.Equal(3, exp.Count);
        Assert.Equal(2, exp[1].Count);                    // d=10 两对(0-10, 10-20) 落 bin1
        Assert.Equal(0.5 * 4, exp[1].Gamma, 9);           // 0.5·(Δ2)²
        Assert.Equal(1, exp[2].Count);                    // d=20 一对
        Assert.Equal(0.5 * 16, exp[2].Gamma, 9);
        Assert.Equal(5, exp[0].H, 9);                     // bin 中心 (i+0.5)·宽
    }

    [Fact]
    public void FitSpherical_recovers_synthetic_model_and_falls_back_when_sparse()
    {
        // 用已知球状模型造实验点(块金 2, 基台 10, 变程 60), 拟合应贴近
        var exp = new List<EstimationAlgorithms.ExperimentalLag>();
        for (int i = 0; i < 12; i++)
        {
            double h = (i + 0.5) * 8;
            exp.Add(new EstimationAlgorithms.ExperimentalLag { H = h, Gamma = EstimationAlgorithms.Gamma("Spherical", h, 2, 10, 60), Count = 20 });
        }
        var fit = EstimationAlgorithms.FitSpherical(exp, 8);
        Assert.Equal("Spherical", fit.Type);
        Assert.InRange(fit.Sill, 8, 12);
        Assert.InRange(fit.Range, 45, 75);
        Assert.InRange(fit.Nugget, 0, 4);
        // 有效 bin <3 → 忠实原回退 (0, 方差, 100)
        var sparse = EstimationAlgorithms.FitSpherical(exp.Take(2).ToList(), 7.5);
        Assert.Equal(0, sparse.Nugget, 9); Assert.Equal(7.5, sparse.Sill, 9); Assert.Equal(100, sparse.Range, 9);
    }

    [Fact]
    public void OrdinaryKriging_exact_at_sample_and_unbiased_on_linear_field()
    {
        var s = LinearGrid();
        var (est, var, n) = EstimationAlgorithms.OrdinaryKriging(20, 20, 0, s, Z, Sph(), 100, 1, 12, -999);
        Assert.Equal(30, est, 6);                         // 正落样点 (x=20 → 30)
        Assert.Equal(0, var, 6);
        Assert.True(n > 0);
        var (mid, v2, _) = EstimationAlgorithms.OrdinaryKriging(15, 20, 0, s, Z, Sph(), 100, 1, 12, -999);
        Assert.InRange(mid, 24, 26);                      // 线性场内插 ≈ 25
        Assert.True(v2 > 0);
    }

    [Fact]
    public void KrigingWithDrift_OK_equals_OrdinaryKriging_and_UK_exact_on_linear_trend()
    {
        var s = LinearGrid();
        var hits = EstimationAlgorithms.FindNearestSamples(s, 13, 27, 0, 100, 12);
        var (okA, varA, _) = EstimationAlgorithms.KrigingWithDrift(13, 27, 0, s, Z, Sph(), 0, hits, 1, -999);
        var (okB, varB, _) = EstimationAlgorithms.OrdinaryKriging(13, 27, 0, s, Z, Sph(), 100, 1, 12, -999);
        Assert.Equal(okB, okA, 6);                        // driftOrder=0 即 OK
        Assert.Equal(varB, varA, 6);
        var (uk1, _, _) = EstimationAlgorithms.KrigingWithDrift(13, 27, 0, s, Z, Sph(), 1, hits, 1, -999);
        Assert.Equal(23, uk1, 3);                         // 一阶漂移对线性趋势处处精确 (10 + 13)
        var (uk2, _, _) = EstimationAlgorithms.KrigingWithDrift(13, 27, 0, s, Z, Sph(), 2, hits, 1, -999);
        Assert.Equal(23, uk2, 2);
    }

    [Fact]
    public void SimpleKriging_returns_mean_far_from_data_and_exact_on_sample()
    {
        var s = LinearGrid();
        double mean = s.Average(q => q.Properties[Z]);   // 30
        var hitsOn = EstimationAlgorithms.FindNearestSamples(s, 40, 40, 0, 100, 12);
        var (on, varOn, _) = EstimationAlgorithms.SimpleKriging(40, 40, 0, s, Z, Sph(), mean, hitsOn, 1, -999);
        Assert.Equal(50, on, 4); Assert.Equal(0, varOn, 4);
        // 远离数据(> 变程)且仍在搜索半径内 → 权重≈0 → 回归全局均值, 方差≈基台
        var hitsFar = EstimationAlgorithms.FindNearestSamples(s, 500, 500, 0, 2000, 12);
        var (far, varFar, _) = EstimationAlgorithms.SimpleKriging(500, 500, 0, s, Z, Sph(), mean, hitsFar, 1, -999);
        Assert.Equal(mean, far, 6);
        Assert.Equal(100, varFar, 6);
    }

    [Fact]
    public void SampleGrid2D_FindNearest_matches_linear_scan()
    {
        var rnd = new Random(7);
        var s = new List<SampleRecord>();
        for (int i = 0; i < 400; i++) s.Add(S(rnd.NextDouble() * 1000, rnd.NextDouble() * 1000, 0, rnd.NextDouble()));
        var grid = new EstimationAlgorithms.SampleGrid2D(s);
        for (int t = 0; t < 20; t++)
        {
            double x = rnd.NextDouble() * 1000, y = rnd.NextDouble() * 1000;
            var a = grid.FindNearest(x, y, 0, 120, 8);
            var b = EstimationAlgorithms.FindNearestSamples(s, x, y, 0, 120, 8);
            Assert.Equal(b.Count, a.Count);
            for (int k = 0; k < a.Count; k++) { Assert.Equal(b[k].index, a[k].index); Assert.Equal(b[k].dist, a[k].dist, 9); }
        }
        // 单点/重合样本(cell→极小)不卡死
        var one = new EstimationAlgorithms.SampleGrid2D(new List<SampleRecord> { S(5, 5, 0), S(5, 5, 0) });
        Assert.Equal(2, one.FindNearest(100, 100, 0, 1000, 5).Count);
    }

    // ───────────── EstimationEngine ─────────────

    private static EstimationTaskConfig Cfg(string algo, List<SampleRecord> samples, double spacing = 10)
    {
        var cfg = new EstimationTaskConfig
        {
            TargetProperty = Z, AlgorithmCode = algo, SearchRadius = 100, MaxSamples = 12, MinSamples = 1, DefaultValue = -999,
            Variogram = Sph(), TrendOrder = 1,
        };
        PickedPointEstimation.ApplyGrid(cfg, samples, spacing, null);
        return cfg;
    }

    [Theory]
    [InlineData("IDW")]
    [InlineData("NN")]
    [InlineData("MA")]
    [InlineData("OK")]
    [InlineData("SK")]
    [InlineData("UK")]
    public async Task Engine_estimates_every_target_grid_node_within_sample_range(string algo)
    {
        var samples = PickedPointEstimation.Flatten(LinearGridZ());
        var cfg = Cfg(algo, samples);
        var r = await new EstimationEngine().RunAsync(cfg, null, CancellationToken.None, samples);
        Assert.True(r.Success, r.Message);
        Assert.Equal(cfg.GridNx * cfg.GridNy, r.GridNx * r.GridNy);
        Assert.Equal(1, r.GridNz);
        Assert.Equal(r.GridNx * r.GridNy, r.BlocksEstimated);            // 半径覆盖全网 → 无 NoData 结点
        Assert.InRange(r.MinEstimate, 10 - 1e-6, 50 + 1e-6);
        Assert.InRange(r.MaxEstimate, 10 - 1e-6, 50 + 1e-6);
        Assert.InRange(r.MeanEstimate, 20, 40);
        Assert.Equal(25, r.SamplesUsed);
        Assert.Contains("估值完成", r.Message);
        bool kriging = algo is "OK" or "SK" or "UK";
        Assert.Equal(kriging, r.CellVariance.Any(v => !double.IsNaN(v)));   // 克里金才出方差
    }

    [Fact]
    public async Task Engine_rejects_empty_samples_and_marks_nodes_outside_radius_as_nodata()
    {
        var eng = new EstimationEngine();
        var empty = await eng.RunAsync(new EstimationTaskConfig(), null, CancellationToken.None, new List<SampleRecord>());
        Assert.False(empty.Success);
        Assert.Contains("未拾取到样本点", empty.Message);

        // 两个样本相距 100, 半径 20, 间距 10 → 中间结点无邻域 → 缺省值(不计入 BlocksEstimated)
        var s = PickedPointEstimation.Flatten(new List<SampleRecord> { S(0, 0, 5), S(100, 10, 15) });
        var cfg = Cfg("IDW", s); cfg.SearchRadius = 20;
        var r = await eng.RunAsync(cfg, null, CancellationToken.None, s);
        Assert.True(r.Success);
        Assert.True(r.BlocksEstimated < r.GridNx * r.GridNy);
        Assert.Contains(r.CellValues, v => v == r.NoData);
        Assert.Equal(r.BlocksEstimated, PickedPointEstimation.GridNodes(r).Count);   // 结点枚举跳过 NoData
    }

    [Fact]
    public async Task Engine_falls_back_to_adaptive_grid_when_target_grid_does_not_overlap()
    {
        var s = PickedPointEstimation.Flatten(LinearGridZ());
        var cfg = Cfg("IDW", s);
        cfg.GridOriginX = 10_000; cfg.GridOriginY = 10_000;   // 目标网格远离样本
        var r = await new EstimationEngine().RunAsync(cfg, null, CancellationToken.None, s);
        Assert.True(r.Success);
        Assert.Contains(r.Warnings, w => w.Contains("不重叠"));
        Assert.True(r.GridOriginX < 0 && r.GridOriginX > -10);   // 样本 AABB 外扩 10%
        Assert.Equal(40, Math.Max(r.GridNx, r.GridNy));           // 默认分辨率 40
    }

    [Fact]
    public async Task Engine_honours_cancellation()
    {
        var s = PickedPointEstimation.Flatten(LinearGridZ());
        var cfg = Cfg("OK", s, 0.5);                               // 80×80 结点
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EstimationEngine().RunAsync(cfg, null, cts.Token, s));
    }

    // ───────────── PickedPointEstimation ─────────────

    [Fact]
    public void ReadSelection_takes_points_and_polyline_vertices_with_Z_as_value()
    {
        var pts = new[] { (1.0, 2.0, 30.0) };
        var polys = new List<IReadOnlyList<(double, double, double)>> { new List<(double, double, double)> { (0, 0, 5), (10, 0, 7) } };
        var s = PickedPointEstimation.ReadSelection(pts, polys);
        Assert.Equal(3, s.Count);
        Assert.All(s, r => Assert.Equal(r.Z, r.Properties[Z]));
        Assert.Contains(s, r => r.X == 1 && r.Y == 2 && r.Z == 30);
        Assert.Contains(s, r => r.X == 10 && r.Z == 7);
        Assert.Empty(PickedPointEstimation.ReadSelection(Array.Empty<(double, double, double)>(), Array.Empty<IReadOnlyList<(double, double, double)>>()));
    }

    [Fact]
    public void Flatten_zeroes_Z_for_distance_but_keeps_elevation_as_value()
    {
        var flat = PickedPointEstimation.Flatten(new[] { S(1, 2, 33) });
        Assert.Single(flat);
        Assert.Equal(0, flat[0].Z);
        Assert.Equal(33, flat[0].Properties[Z]);
    }

    [Fact]
    public void AverageNearestNeighbor_and_suggestions_on_regular_grid()
    {
        var s = LinearGridZ();                                  // 间距 10 → 平均最近邻 10
        Assert.Equal(10, PickedPointEstimation.AverageNearestNeighbor(s), 6);
        Assert.Equal(0, PickedPointEstimation.AverageNearestNeighbor(new List<SampleRecord> { S(0, 0, 0) }));
        // 半径 = max(4×10, 0.15×40) = 40
        Assert.Equal(40, PickedPointEstimation.SuggestSearchRadius(s)!.Value, 6);
        Assert.Null(PickedPointEstimation.SuggestSearchRadius(new List<SampleRecord>()));
        var vg = PickedPointEstimation.SuggestVariogram(s)!.Value;
        double mean = s.Average(q => q.Z), variance = s.Average(q => (q.Z - mean) * (q.Z - mean));
        Assert.Equal(Math.Round(variance, 3), vg.sill, 6);
        Assert.Equal(Math.Round(variance * 0.15, 3), vg.nugget, 6);
        Assert.Equal(40, vg.range!.Value, 6);
        Assert.Null(PickedPointEstimation.SuggestVariogram(new List<SampleRecord> { S(0, 0, 1) }));
    }

    [Fact]
    public void Summaries_match_original_wording()
    {
        Assert.Equal("选择集为空 —— 请先在视口选中点 / 多段线", PickedPointEstimation.PickedSummary(new List<SampleRecord>()));
        Assert.Equal("选择集样点 2 个  |  Z ∈ [1.00, 3.00]  均值 2.00", PickedPointEstimation.PickedSummary(new List<SampleRecord> { S(0, 0, 1), S(1, 0, 3) }));
        Assert.Equal("未框选 —— 默认用样本点包围盒", PickedPointEstimation.RangeSummary(null));
        Assert.Equal("框选范围  X[0.0, 10.0]  Y[1.0, 5.5]", PickedPointEstimation.RangeSummary((0, 1, 10, 5.5)));
    }

    [Fact]
    public void ApplyGrid_lays_square_grid_over_bbox_or_picked_range()
    {
        var s = new List<SampleRecord> { S(0, 0, 1), S(100, 50, 2) };
        var cfg = new EstimationTaskConfig { SearchRadius = 30 };
        double sp = PickedPointEstimation.ApplyGrid(cfg, s, 10, null);
        Assert.Equal(10, sp, 9);
        Assert.True(cfg.UseTargetGrid);
        Assert.Equal(0, cfg.GridOriginX, 9); Assert.Equal(0, cfg.GridOriginY, 9); Assert.Equal(-0.5, cfg.GridOriginZ, 9);
        Assert.Equal(10, cfg.GridNx); Assert.Equal(5, cfg.GridNy); Assert.Equal(1, cfg.GridNz);
        Assert.Equal(30, cfg.SearchRadius, 9);                          // 未框选不动半径

        // 框选范围: 原点 = 框选角, 半径至少覆盖 (框选∪样本) 跨度
        var cfg2 = new EstimationTaskConfig { SearchRadius = 30 };
        PickedPointEstimation.ApplyGrid(cfg2, s, 25, (200, 200, 300, 250));
        Assert.Equal(200, cfg2.GridOriginX, 9); Assert.Equal(4, cfg2.GridNx); Assert.Equal(2, cfg2.GridNy);
        Assert.Equal(Math.Sqrt(300.0 * 300 + 250.0 * 250), cfg2.SearchRadius, 6);
    }

    [Fact]
    public void ApplyGrid_enlarges_spacing_to_cap_grid_points()
    {
        var s = new List<SampleRecord> { S(0, 0, 1), S(100_000, 100_000, 2) };   // 1e10 面积, 间距 1 会 1e10 点
        var cfg = new EstimationTaskConfig();
        double sp = PickedPointEstimation.ApplyGrid(cfg, s, 1, null);
        Assert.Equal(Math.Ceiling(Math.Sqrt(1e10 / PickedPointEstimation.MaxGridPoints)), sp, 9);   // 200
        Assert.True((long)cfg.GridNx * cfg.GridNy <= PickedPointEstimation.MaxGridPoints);
    }

    [Fact]
    public void GridNodes_centres_and_marker_size()
    {
        var r = new EstimationResult
        {
            GridNx = 2, GridNy = 2, GridNz = 1, GridOriginX = 100, GridOriginY = 200, GridSizeX = 10, GridSizeY = 10, NoData = -999,
            CellValues = new[] { 1.0, -999.0, 3.0, 4.0 },
        };
        var nodes = PickedPointEstimation.GridNodes(r);
        Assert.Equal(3, nodes.Count);
        Assert.Contains(nodes, n => n.x == 105 && n.y == 205 && n.z == 1);   // ix=0,iy=0
        Assert.Contains(nodes, n => n.x == 115 && n.y == 215 && n.z == 4);   // ix=1,iy=1
        Assert.DoesNotContain(nodes, n => n.x == 115 && n.y == 205);         // NoData 跳过
        Assert.Equal(1.5, PickedPointEstimation.MarkerSize(r), 9);           // 0.15×10
        Assert.Equal(0.5, PickedPointEstimation.MarkerSize(new EstimationResult { GridSizeX = 1, GridSizeY = 1 }), 9);   // 下限 0.5
    }

    [Fact]
    public void VariogramCurve_validation_and_autofit()
    {
        var curve = PickedPointEstimation.VariogramCurve(0, 1, 10, 50);
        Assert.Equal(51, curve.Count);
        Assert.Equal(0, curve[0].gamma, 9);
        Assert.Equal(100, curve[^1].h, 9);                                       // 2×range
        Assert.Equal(10, curve[^1].gamma, 9);                                    // ≥range → sill
        Assert.Equal(EstimationAlgorithms.Gamma("Gaussian", 20, 1, 10, 50), PickedPointEstimation.VariogramGamma(2, 20, 1, 10, 50), 9);
        Assert.Equal("⚠️ 块金值不能大于基台值", PickedPointEstimation.VariogramValidation(5, 1, 10));
        Assert.Equal("⚠️ 变程必须大于 0", PickedPointEstimation.VariogramValidation(0, 1, 0));
        Assert.Equal("⚠️ 基台值必须大于 0", PickedPointEstimation.VariogramValidation(0, 0, 10));
        Assert.Equal("", PickedPointEstimation.VariogramValidation(0, 1, 10));

        var (fit, exp, bins) = PickedPointEstimation.AutoFit(LinearGridZ());
        Assert.NotNull(fit);
        Assert.Equal(15, exp.Count);
        Assert.True(bins >= 3);
        Assert.True(fit!.Sill > 0 && fit.Range > 0);
        Assert.Null(PickedPointEstimation.AutoFit(new List<SampleRecord> { S(0, 0, 1), S(1, 1, 2) }).fit);   // <3 样本
    }

    [Fact]
    public void DataAnalysis_stats_histogram_cdf()
    {
        var v = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var st = PickedPointEstimation.Stats(v)!.Value;
        Assert.Equal(10, st.count); Assert.Equal(5.5, st.mean, 9); Assert.Equal(1, st.min); Assert.Equal(10, st.max);
        Assert.Equal(Math.Sqrt(8.25), st.std, 9);                                // 总体标准差
        Assert.Equal(Math.Sqrt(8.25) / 5.5, st.cv, 9);
        Assert.Null(PickedPointEstimation.Stats(null));

        var h = PickedPointEstimation.Histogram(v);                              // bins = min(20, √10) = 3
        Assert.Equal(3, h.Count);
        Assert.Equal(10, h.Sum(b => b.count));
        Assert.Equal(1, h[0].lo, 9); Assert.Equal(10, h[^1].hi, 9);
        Assert.Empty(PickedPointEstimation.Histogram(new double[] { 5, 5, 5 }));   // 极差 0

        var c = PickedPointEstimation.Cdf(v);
        Assert.Equal(10, c.Count);
        Assert.Equal(0.1, c[0].cdf, 9); Assert.Equal(1.0, c[^1].cdf, 9);
        Assert.True(c.Zip(c.Skip(1), (a, b) => b.value >= a.value && b.cdf > a.cdf).All(x => x));
    }

    [Fact]
    public void DataAnalysis_mock_points_heatgrid_marching_squares_colors()
    {
        var vals = new double[] { 1, 2, 3, 4, 5 };
        var p1 = PickedPointEstimation.MockPoints(vals);
        var p2 = PickedPointEstimation.MockPoints(vals);
        Assert.Equal(5, p1.Count);
        Assert.Equal(p1, p2);                                                    // 固定种子可重复
        Assert.All(p1, p => Assert.InRange(p.X, 0, 2000));

        var grid = PickedPointEstimation.HeatGrid(p1, 0, 2000, 0, 2000, 20, 20);
        Assert.Equal(400, grid.Length);
        foreach (var g in grid) Assert.InRange(g, 1, 5);                        // IDW 不越样本值域

        // 左低右高的斜坡: 等值线 0.5 → 每行一段, 共 ny-1 段, 且 x 落 0.5 附近
        var ramp = new double[3, 3];
        for (int ix = 0; ix < 3; ix++) for (int iy = 0; iy < 3; iy++) ramp[ix, iy] = ix / 2.0;
        var segs = PickedPointEstimation.MarchingSquares(ramp, 0.5);
        Assert.Equal(2, segs.Count);
        Assert.All(segs, s => { Assert.InRange(s.p1.x, 0.3, 0.7); Assert.InRange(s.p2.x, 0.3, 0.7); });
        Assert.Empty(PickedPointEstimation.MarchingSquares(ramp, 5));            // 全低于阈值 → 无线

        Assert.Equal(((byte)0, (byte)0, (byte)139), PickedPointEstimation.HeatmapColor(0));
        Assert.Equal(((byte)255, (byte)0, (byte)0), PickedPointEstimation.HeatmapColor(1));
        Assert.Equal(((byte)0, (byte)255, (byte)128), PickedPointEstimation.HeatmapColor(0.5));
    }
}
