using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Controls.Charts;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 煤质 × 块体 交会分析（忠实原 CoalBlockCrossPlotDialog）：在每个钻孔煤质点位置采样块体属性 → 与煤质指标做散点（按煤层分色）+ 相关系数；
/// 块体属性与指标同名 → 估计 vs 实测校验（1:1 线 + RMSE）。图表用 ChartView Scatter/Line。
/// </summary>
public partial class CoalBlockCrossPlotWindow : Window
{
    private readonly ModelingContext _ctx;
    private readonly BlockModelMeta _model;

    public CoalBlockCrossPlotWindow() { _ctx = null!; _model = null!; InitializeComponent(); }

    public CoalBlockCrossPlotWindow(ModelingContext ctx, BlockModelMeta model)
    {
        _ctx = ctx; _model = model;
        InitializeComponent();
        Title += $" — {_model.Name}";
        blockAttrCombo.ItemsSource = _model.PropertySchema.Select(c => c.Name).ToList();
        if (_model.PropertySchema.Count > 0) blockAttrCombo.SelectedIndex = 0;
    }

    private static Color SeamColor(string seam) => seam switch
    {
        "4" => Color.FromRgb(0xD4, 0xA0, 0x17), "7-1" => Color.FromRgb(0x8B, 0x69, 0x14), "9" => Color.FromRgb(0x4A, 0x7C, 0x2E), "11" => Color.FromRgb(0x2C, 0x5F, 0x8D), _ => Colors.Gray,
    };

    private async void OnRun(object? sender, RoutedEventArgs e)
    {
        try
        {
            var code = (indicatorCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "ad";
            bool clean = rbClean.IsChecked == true;
            var (col, get, name) = BlockCoalQualityLink.Indicator(code, clean);
            var blockAttr = blockAttrCombo.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(blockAttr)) { await BlockMsgBox.WarnAsync(this, "提示", "请选择块体属性。"); return; }
            var conn = _ctx.Conn();
            var points = conn != null ? GeoDataQueries.GetCoalSamples(conn) : new List<CoalSample>();
            var bySeam = new Dictionary<string, List<(double bx, double cy)>>();
            var allB = new List<double>(); var allC = new List<double>();
            foreach (var p in points)
            {
                var cv = get(p);
                if (!cv.HasValue || !p.Z.HasValue) continue;
                var bv = BlockCoalQualityLink.BlockValueAt(_model, p.X, p.Y, p.Z.Value, blockAttr);
                if (!bv.HasValue) continue;
                var seam = string.IsNullOrEmpty(p.SeamCode) ? "?" : p.SeamCode;
                if (!bySeam.TryGetValue(seam, out var l)) bySeam[seam] = l = new();
                l.Add((bv.Value, cv.Value)); allB.Add(bv.Value); allC.Add(cv.Value);
            }
            scatterChart.Series.Clear();
            if (allB.Count < 2)
            {
                scatterChart.Refresh();
                statsText.Text = "没有可配对的点（钻孔点是否落在块体范围内、块体是否有该属性值？）";
                return;
            }
            scatterChart.NumericX = true;
            foreach (var (seam, pairs) in bySeam.OrderBy(kv => kv.Key))
                scatterChart.Series.Add(new ChartSeries { Name = $"{seam} ({pairs.Count})", Kind = SeriesKind.Scatter, Color = SeamColor(seam), Points = pairs.Select(t => (t.bx, t.cy)).ToList(), MarkerSize = 6 });
            double r = BlockCoalQualityLink.Pearson(allB, allC);
            bool validation = string.Equals(blockAttr, col, StringComparison.Ordinal);
            string extra = "";
            if (validation)
            {
                double lo = Math.Min(allB.Min(), allC.Min()), hi = Math.Max(allB.Max(), allC.Max());
                scatterChart.Series.Add(new ChartSeries { Name = "1:1", Kind = SeriesKind.Line, Color = Color.FromRgb(0x88, 0x88, 0x88), Points = new List<(double, double)> { (lo, lo), (hi, hi) }, ShowMarkers = false, StrokeThickness = 1.5, Dashed = true });
                double rmse = Math.Sqrt(allB.Zip(allC, (b, c) => (b - c) * (b - c)).Average());
                extra = $"    RMSE(估−测) = {rmse:F3}";
            }
            scatterChart.XTitle = validation ? $"块体 {blockAttr}（估）" : $"块体 {blockAttr}";
            scatterChart.YTitle = validation ? $"{name}（实测）" : name;
            scatterChart.Legend = LegendPlacement.Right;
            scatterChart.Refresh();
            statsText.Text = $"配对点 n = {allB.Count}    相关系数 r = {r:F3}{extra}" + (validation ? "    （块体属性与所选指标同名 → 估计 vs 实测校验：点越贴 1:1 线越准）" : "");
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "错误", $"运行失败：{ex.Message}"); }
    }
}
