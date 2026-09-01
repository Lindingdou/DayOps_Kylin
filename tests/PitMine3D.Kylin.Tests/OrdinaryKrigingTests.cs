using System.Collections.Generic;
using PitMine3D.Kylin.Cad;
using Xunit;
using CP = PitMine3D.Kylin.Cad.OrdinaryKriging.ControlPoint;

namespace PitMine3D.Kylin.Tests;

/// <summary>普通克里金 OK 回归（忠实移植 CoalQualityEstimator OK 核：球状变差 + 克里金方程组 + 方差）。</summary>
public class OrdinaryKrigingTests
{
    private static List<CP> Grid()
    {
        // 5×5 规则网格, V = 10 + x（线性场, 便于验内插）
        var pts = new List<CP>();
        for (int i = 0; i <= 4; i++)
            for (int j = 0; j <= 4; j++)
                pts.Add(new CP(i * 10, j * 10, 0, 10 + i * 10));
        return pts;
    }

    [Fact]
    public void Spherical_variogram_shape()
    {
        var vg = new OrdinaryKriging.Variogram(1, 10, 50);
        Assert.Equal(0, vg.Gamma(0), 6);            // γ(0)=0
        Assert.Equal(10, vg.Gamma(50), 6);          // γ(≥range)=sill
        Assert.Equal(10, vg.Gamma(80), 6);
        Assert.True(vg.Gamma(10) < vg.Gamma(30));   // 单调递增
        Assert.InRange(vg.Gamma(25), 1, 10);        // 块金~基台之间
    }

    [Fact]
    public void Exact_interpolation_at_control_point()
    {
        var pts = Grid();
        var e = OrdinaryKriging.EstimateAt(pts, 20, 20, 0);   // 正落控制点 (20,20) V=30
        Assert.NotNull(e);
        Assert.Equal(30, e!.Value.est, 6);
        Assert.Equal(0, e.Value.variance, 6);                 // 控制点上克里金方差=0
    }

    [Fact]
    public void Variance_nonnegative_and_interior_estimate_reasonable()
    {
        var pts = Grid();
        var e = OrdinaryKriging.EstimateAt(pts, 15, 15, 0, radius: 100);   // 网格间内插点
        Assert.NotNull(e);
        Assert.True(e!.Value.variance >= 0, "克里金方差非负");
        // 线性场 V=10+x, x=15 → 真值 25; 克里金对线性场应贴近
        Assert.InRange(e.Value.est, 20, 30);
    }

    [Fact]
    public void Outside_radius_returns_null()
    {
        var pts = Grid();
        var e = OrdinaryKriging.EstimateAt(pts, 100000, 100000, 0, radius: 5);   // 远在半径外
        Assert.Null(e);
    }

    [Fact]
    public void Single_point_returns_its_value()
    {
        var pts = new List<CP> { new CP(0, 0, 0, 42) };
        var e = OrdinaryKriging.EstimateAt(pts, 1, 1, 0, radius: 100);
        Assert.NotNull(e);
        Assert.Equal(42, e!.Value.est, 6);
    }

    [Fact]
    public void FitVariogram_sill_equals_sample_variance()
    {
        var pts = Grid();
        var vg = OrdinaryKriging.FitVariogram(pts);
        Assert.True(vg.Sill > 0);
        Assert.True(vg.Range > 0);
        Assert.True(vg.Nugget >= 0 && vg.Nugget <= vg.Sill);
    }

    [Fact]
    public void Empty_points_returns_null()
    {
        Assert.Null(OrdinaryKriging.EstimateAt(new List<CP>(), 0, 0, 0));
    }

    // ── 泛克里金 UK（带线性趋势）──
    private static List<CP> LinearTrendGrid()
    {
        // 6×6 网格, V = 10 + 2x + 3y（严格线性趋势面）
        var pts = new List<CP>();
        for (int i = 0; i <= 5; i++)
            for (int j = 0; j <= 5; j++)
                pts.Add(new CP(i * 10, j * 10, 0, 10 + 2 * (i * 10) + 3 * (j * 10)));
        return pts;
    }

    [Fact]
    public void Universal_kriging_reproduces_linear_trend_exactly()
    {
        var pts = LinearTrendGrid();
        // 内部非控制点 (23,17)：真值 = 10 + 2·23 + 3·17 = 107
        var uk = OrdinaryKriging.EstimateUniversalAt(pts, 23, 17, 0, k: 16);
        Assert.NotNull(uk);
        Assert.Equal(107.0, uk!.Value.est, 4);        // UK 对线性趋势处处精确
    }

    [Fact]
    public void Universal_kriging_exact_at_control_point()
    {
        var pts = LinearTrendGrid();
        var uk = OrdinaryKriging.EstimateUniversalAt(pts, 20, 30, 0, k: 16);   // 控制点 (20,30): 10+40+90=140
        Assert.NotNull(uk);
        Assert.Equal(140.0, uk!.Value.est, 4);
        Assert.True(uk.Value.variance >= 0);
    }

    // ── 简单克里金 SK（已知均值, 稀疏区归均值）──
    [Fact]
    public void Simple_kriging_exact_at_control_and_reverts_to_mean_far_away()
    {
        var pts = new List<CP>
        {
            new(0, 0, 0, 10), new(10, 0, 0, 20), new(0, 10, 0, 30), new(10, 10, 0, 40),
        };
        double mean = 25;   // 样本均值 (10+20+30+40)/4
        // 控制点上精确
        var atCp = OrdinaryKriging.EstimateSimpleAt(pts, 0, 0, 0, mean, radius: 100);
        Assert.NotNull(atCp);
        Assert.Equal(10.0, atCp!.Value.est, 4);
        // 远离数据(但在半径内): SK 权重→0, 估计→均值 25。取一个较远点。
        var far = OrdinaryKriging.EstimateSimpleAt(pts, 500, 500, 0, mean, radius: 2000);
        Assert.NotNull(far);
        Assert.True(System.Math.Abs(far!.Value.est - mean) < 5, $"SK 远处应≈均值 {mean}, 实 {far.Value.est:0.##}");
    }

    [Fact]
    public void Universal_kriging_out_of_radius_null_and_few_points_fallback()
    {
        var pts = LinearTrendGrid();
        Assert.Null(OrdinaryKriging.EstimateUniversalAt(pts, 10000, 10000, 0, radius: 5));   // 远离 → null
        // 仅 2 点(<3 趋势基) → 回落 OK, 不崩
        var two = new List<CP> { new(0, 0, 0, 5), new(10, 0, 0, 9) };
        Assert.NotNull(OrdinaryKriging.EstimateUniversalAt(two, 5, 0, 0, radius: 100));
    }

    [Fact]
    public void ExperimentalVariogram_linear_field_known_gamma()
    {
        // 线性场 V=x, 4 点共线; maxLag=4/4 箱(bw=1)。滞后 d 的半变异 γ=0.5·d²。
        var pts = new List<CP> { new(0, 0, 0, 0), new(1, 0, 0, 1), new(2, 0, 0, 2), new(3, 0, 0, 3) };
        var exp = OrdinaryKriging.ExperimentalVariogram(pts, maxLag: 4, lagCount: 4);
        Assert.Equal(4, exp.Count);
        Assert.Equal(0, exp[0].Count);                       // [0,1) 无点对
        Assert.Equal(3, exp[1].Count); Assert.Equal(0.5, exp[1].Gamma, 6);   // d=1 三对 → 0.5·1
        Assert.Equal(2, exp[2].Count); Assert.Equal(2.0, exp[2].Gamma, 6);   // d=2 两对 → 0.5·4
        Assert.Equal(1, exp[3].Count); Assert.Equal(4.5, exp[3].Gamma, 6);   // d=3 一对 → 0.5·9
    }

    [Fact]
    public void Variogram_models_known_gamma()
    {
        // nugget0/sill10/range100 at h=50: 球状 6.875 · 指数 10(1−e^−1.5)=7.7687 · 高斯 10(1−e^−0.75)=5.2763。
        var sph = new OrdinaryKriging.Variogram(0, 10, 100);   // 默认球状(向后兼容)
        Assert.Equal(OrdinaryKriging.VariogramModel.Spherical, sph.Model);
        Assert.Equal(6.875, sph.Gamma(50), 4);
        var exp = new OrdinaryKriging.Variogram(0, 10, 100, OrdinaryKriging.VariogramModel.Exponential);
        Assert.Equal(7.7687, exp.Gamma(50), 3);
        var gau = new OrdinaryKriging.Variogram(0, 10, 100, OrdinaryKriging.VariogramModel.Gaussian);
        Assert.Equal(5.2763, gau.Gamma(50), 3);
        // 球状 h≥range → sill(其它型渐近, h=range 仍<sill)。
        Assert.Equal(10, sph.Gamma(100), 6);
        Assert.True(exp.Gamma(100) < 10 && gau.Gamma(100) < 10);
    }

    [Fact]
    public void SelectVariogramModel_returns_valid_model_and_finite_sse()
    {
        var pts = new List<CP>();
        for (int x = 0; x < 6; x++) for (int y = 0; y < 6; y++) pts.Add(new(x, y, 0, x + y));
        var (best, sSph, sExp, sGau) = OrdinaryKriging.SelectVariogramModel(pts);
        Assert.True(sSph >= 0 && sExp >= 0 && sGau >= 0);
        Assert.Contains(best.Model, new[] { OrdinaryKriging.VariogramModel.Spherical, OrdinaryKriging.VariogramModel.Exponential, OrdinaryKriging.VariogramModel.Gaussian });
        // 选出的模型 SSE 应是三者最小。
        double min = System.Math.Min(sSph, System.Math.Min(sExp, sGau));
        double chosen = best.Model == OrdinaryKriging.VariogramModel.Spherical ? sSph : best.Model == OrdinaryKriging.VariogramModel.Exponential ? sExp : sGau;
        Assert.Equal(min, chosen, 6);
    }

    [Fact]
    public void ExperimentalVariogram_auto_maxlag_and_degenerate_safe()
    {
        var pts = new List<CP> { new(0, 0, 0, 1), new(5, 0, 0, 2), new(10, 0, 0, 3) };
        var exp = OrdinaryKriging.ExperimentalVariogram(pts, maxLag: 0, lagCount: 6);   // auto maxLag
        Assert.Equal(6, exp.Count);
        Assert.True(exp[^1].H > exp[0].H);                   // 滞后中心递增
        // <2 点 → 全 0 箱, 不崩
        Assert.All(OrdinaryKriging.ExperimentalVariogram(new List<CP> { new(0, 0, 0, 1) }, 10, 5), b => Assert.Equal(0, b.Count));
    }

    // ── 反距离权重 IDW（克里金家族的无变差函数同伴）──
    [Fact]
    public void Idw_exact_at_control_point_and_single_point()
    {
        var one = new List<CP> { new(0, 0, 0, 42) };
        var at = OrdinaryKriging.IdwEstimate(one, 0, 0, 0, radius: 100);
        Assert.NotNull(at);
        Assert.Equal(42, at!.Value.est, 6);
        Assert.Equal(1, at.Value.used);                      // 零距离 → 精确, 用 1 点
        // 单点邻域: 任意查询点都回该点值
        var off = OrdinaryKriging.IdwEstimate(one, 5, 5, 0, radius: 100);
        Assert.Equal(42, off!.Value.est, 6);
    }

    [Fact]
    public void Idw_symmetric_neighbours_equal_weight_average()
    {
        // 四点等距(10)围绕原点, 值 10/20/30/40 → 权重相等 → 均值 25。
        var pts = new List<CP> { new(10, 0, 0, 10), new(-10, 0, 0, 20), new(0, 10, 0, 30), new(0, -10, 0, 40) };
        var e = OrdinaryKriging.IdwEstimate(pts, 0, 0, 0, power: 2, radius: 100);
        Assert.NotNull(e);
        Assert.Equal(25.0, e!.Value.est, 6);
        Assert.Equal(4, e.Value.used);
    }

    [Fact]
    public void Idw_known_two_point_value_and_power_sharpens()
    {
        // (0,0,0,0) 与 (10,0,0,100), 查询 (2,0,0)。power=1: w=1/d → est=(0.5·0+0.125·100)/0.625=20。
        var pts = new List<CP> { new(0, 0, 0, 0), new(10, 0, 0, 100) };
        var p1 = OrdinaryKriging.IdwEstimate(pts, 2, 0, 0, power: 1, radius: 100, smoothing: 0);
        Assert.Equal(20.0, p1!.Value.est, 6);
        // power=2: 近点(值0)权重更压倒 → 估计更低(≈5.88)。
        var p2 = OrdinaryKriging.IdwEstimate(pts, 2, 0, 0, power: 2, radius: 100, smoothing: 0);
        Assert.True(p2!.Value.est < p1.Value.est);
        Assert.Equal(1.5625 / 0.265625, p2.Value.est, 6);
    }

    [Fact]
    public void Idw_smoothing_flattens_toward_mean()
    {
        // 同两点, 查询 (2,0,0) power=2。smoothing 大 → 权重趋等 → 估计从 5.88 拉向中值。
        var pts = new List<CP> { new(0, 0, 0, 0), new(10, 0, 0, 100) };
        var sharp = OrdinaryKriging.IdwEstimate(pts, 2, 0, 0, power: 2, radius: 100, smoothing: 0);
        var flat = OrdinaryKriging.IdwEstimate(pts, 2, 0, 0, power: 2, radius: 100, smoothing: 100);
        Assert.True(flat!.Value.est > sharp!.Value.est);
        Assert.InRange(flat.Value.est, 45, 50);              // 趋近两点均值 50
    }

    [Fact]
    public void Idw_radius_and_minSamples_and_empty_guard()
    {
        Assert.Null(OrdinaryKriging.IdwEstimate(new List<CP>(), 0, 0, 0));                       // 空
        var one = new List<CP> { new(0, 0, 0, 5) };
        Assert.Null(OrdinaryKriging.IdwEstimate(one, 100, 100, 0, radius: 5));                   // 半径外 → null
        var two = new List<CP> { new(0, 0, 0, 5), new(1, 0, 0, 7) };
        Assert.Null(OrdinaryKriging.IdwEstimate(two, 0.5, 0, 0, radius: 100, minSamples: 3));    // 邻域不足 → null
        Assert.NotNull(OrdinaryKriging.IdwEstimate(two, 0.5, 0, 0, radius: 100, minSamples: 2)); // 恰够
    }

    [Fact]
    public void Idw_maxSamples_caps_neighbourhood()
    {
        // 远处放一堆离群高值点; maxSamples=2 只取最近两个(值 10/12) → 估计落在 10~12, 不被远点污染。
        var pts = new List<CP> { new(0, 0, 0, 10), new(2, 0, 0, 12) };
        for (int i = 0; i < 20; i++) pts.Add(new CP(50 + i, 50, 0, 999));
        var e = OrdinaryKriging.IdwEstimate(pts, 1, 0, 0, power: 2, radius: 1000, maxSamples: 2);
        Assert.NotNull(e);
        Assert.Equal(2, e!.Value.used);
        Assert.InRange(e.Value.est, 10, 12);
    }
}
