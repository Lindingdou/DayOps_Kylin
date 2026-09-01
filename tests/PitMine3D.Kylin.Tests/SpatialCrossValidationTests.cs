using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using Xunit;
using CP = PitMine3D.Kylin.Cad.OrdinaryKriging.ControlPoint;

namespace PitMine3D.Kylin.Tests;

/// <summary>
/// 空间估值留一交叉验证 回归 —— 忠实移植原 CoalQualityEstimator.CrossValidate 的已知值验证。
/// </summary>
public class SpatialCrossValidationTests
{
    [Fact]
    public void Constant_field_has_zero_error_every_method()
    {
        var pts = new List<CP>
        {
            new(0, 0, 0, 7), new(1, 0, 0, 7), new(0, 1, 0, 7), new(1, 1, 0, 7), new(2, 2, 0, 7),
        };
        foreach (var m in new[] { "IDW", "NN", "MA", "OK" })
        {
            var r = SpatialCrossValidation.CrossValidate(pts, m);
            Assert.Equal(5, r.Predicted);
            Assert.Equal(0, r.ME, 6);
            Assert.Equal(0, r.MAE, 6);
            Assert.Equal(0, r.RMSE, 6);
        }
    }

    [Fact]
    public void Nearest_neighbor_loo_known_errors()
    {
        // (0,0)V10 (1,0)V20 (5,0)V99 (6,0)V99; 自动半径 1.25 → 各点最近邻 d=1。
        // LOO 误差(pred−actual): 0→20−10=+10; 1→10−20=−10; 5→99−99=0; 6→99−99=0。
        var pts = new List<CP> { new(0, 0, 0, 10), new(1, 0, 0, 20), new(5, 0, 0, 99), new(6, 0, 0, 99) };
        var r = SpatialCrossValidation.CrossValidate(pts, "NN");
        Assert.Equal(4, r.Predicted);
        Assert.Equal(0, r.ME, 6);                          // +10−10 平均 0
        Assert.Equal(5, r.MAE, 6);                         // (10+10+0+0)/4
        Assert.Equal(System.Math.Sqrt(50), r.RMSE, 6);     // √((100+100)/4)
    }

    [Fact]
    public void Fewer_than_four_points_returns_empty()
    {
        var pts = new List<CP> { new(0, 0, 0, 1), new(1, 0, 0, 2), new(2, 0, 0, 3) };
        var r = SpatialCrossValidation.CrossValidate(pts, "IDW");
        Assert.Equal(0, r.Predicted);
        Assert.Empty(r.Pairs);
    }

    [Fact]
    public void OK_yields_standardized_errors()
    {
        // 3×3 网格线性场 V=x+y → OK 方差>0 → 逐点有标准化误差 → MSE 有值。
        var pts = new List<CP>();
        for (int x = 0; x < 3; x++)
            for (int y = 0; y < 3; y++)
                pts.Add(new(x, y, 0, x + y));
        var r = SpatialCrossValidation.CrossValidate(pts, "OK");
        Assert.True(r.Predicted >= 6);
        Assert.True(r.MSE.HasValue, "OK 应产标准化误差均值");
        Assert.Contains(r.Pairs, p => p.StdError.HasValue);
    }

    [Fact]
    public void Smooth_field_idw_has_high_r2_and_low_bias()
    {
        // 4×4 网格线性场 V=2x+3y, IDW LOO → 相关高、系统偏差小。
        var pts = new List<CP>();
        for (int x = 0; x < 4; x++)
            for (int y = 0; y < 4; y++)
                pts.Add(new(x, y, 0, 2 * x + 3 * y));
        var r = SpatialCrossValidation.CrossValidate(pts, "IDW");
        Assert.Equal(16, r.Predicted);
        Assert.True(r.R2 > 0.6, $"R²={r.R2}");
        Assert.True(System.Math.Abs(r.ME) < 1.5, $"ME={r.ME}");
    }

    [Fact]
    public void ToCsv_has_summary_and_rows()
    {
        var pts = new List<CP> { new(0, 0, 0, 10), new(1, 0, 0, 20), new(5, 0, 0, 99), new(6, 0, 0, 99) };
        var csv = SpatialCrossValidation.ToCsv(SpatialCrossValidation.CrossValidate(pts, "NN"), "NN");
        Assert.Contains("均方根误差RMSE", csv);
        Assert.Contains("序号,实测,预测,标准化误差", csv);
    }
}
