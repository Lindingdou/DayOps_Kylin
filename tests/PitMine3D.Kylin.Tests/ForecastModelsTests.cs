using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;

namespace PitMine3D.Kylin.Tests;

/// <summary>时间序列预测回归（忠实移植 ForecastModels：LSQ趋势+EWMA融合/Holt + 区间 + 异常）。</summary>
public class ForecastModelsTests
{
    [Fact]
    public void Perfect_linear_series_recovers_slope_and_r2()
    {
        // y = 10 + 2x, x=0..9
        var s = Enumerable.Range(0, 10).Select(i => 10.0 + 2 * i).ToList();
        var r = ForecastModels.Forecast(s, 3);
        Assert.Equal(2.0, r.Slope, 3);              // 斜率还原
        Assert.Equal(1.0, r.R2, 3);                 // 完美拟合 R²=1
        Assert.Equal("上升", r.TrendLabel);
        Assert.True(r.Next >= 28 - 0.5, $"下期应≈趋势外推(x=10→30), 实 {r.Next}");   // 融合含EWMA故略低于30
        Assert.Equal(0, r.AnomalyCount);            // 无异常
    }

    [Fact]
    public void Flat_series_is_stable_zero_slope()
    {
        var s = Enumerable.Repeat(50.0, 8).ToList();
        var r = ForecastModels.Forecast(s, 3);
        Assert.Equal(0.0, r.Slope, 3);
        Assert.Equal("平稳", r.TrendLabel);
        Assert.Equal(50.0, r.Next, 3);
    }

    [Fact]
    public void Anomaly_spike_is_flagged()
    {
        var s = new List<double> { 10, 10, 10, 10, 100, 10, 10, 10, 10, 10 };   // 一个尖峰
        var r = ForecastModels.Forecast(s, 2);
        Assert.True(r.AnomalyCount >= 1, "尖峰应被识别为异常");
    }

    [Fact]
    public void Interval_widens_with_horizon()
    {
        var s = new List<double> { 10, 12, 11, 13, 12, 14, 13, 15 };
        var r = ForecastModels.Forecast(s, 6);
        Assert.True(r.HalfWidthAt(5) > r.HalfWidthAt(0), "越远区间越宽(喇叭张开)");
    }

    [Fact]
    public void Small_sample_falls_back_to_mean()
    {
        var s = new List<double> { 20, 40 };        // n<3
        var r = ForecastModels.Forecast(s, 3);
        Assert.Equal(30.0, r.Next, 3);              // 均值
        Assert.Equal("样本不足", r.TrendLabel);
        Assert.All(r.Path, v => Assert.Equal(30.0, v, 3));
    }

    [Fact]
    public void Holt_follows_upward_trend()
    {
        var s = Enumerable.Range(0, 10).Select(i => 10.0 + 3 * i).ToList();   // 强上升
        var r = ForecastModels.Forecast(s, 4, method: ForecastMethod.Holt);
        Assert.True(r.Slope > 2, $"Holt 末端趋势应≈3, 实 {r.Slope}");
        for (int k = 1; k < r.Path.Length; k++) Assert.True(r.Path[k] > r.Path[k - 1], "上升趋势路径应递增");
        Assert.Contains("Holt", r.Method);
    }

    [Fact]
    public void PathToCsv_history_and_forecast()
    {
        var s = Enumerable.Range(0, 8).Select(i => 10.0 + 2 * i).ToList();
        var r = ForecastModels.Forecast(s, 4);
        string csv = ForecastModels.PathToCsv(s, r);
        Assert.Contains("index,kind,value,lower95,upper95", csv);   // 表头
        Assert.Contains(",history,", csv);                          // 历史行
        Assert.Contains(",forecast,", csv);                         // 预测行
        var lines = csv.TrimEnd('\n').Split('\n');
        Assert.Equal(1 + 1 + 8 + 4, lines.Length);                  // 元信息 + 表头 + 8史 + 4测
    }

    [Fact]
    public void Empty_series_no_crash()
    {
        var r = ForecastModels.Forecast(new List<double>(), 3);
        Assert.Equal(3, r.Path.Length);
        Assert.Equal("无数据", r.Method);
    }
}
