using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Controls.Charts;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 样本数据分析面板（忠实原 Estimation/Controls/DataAnalysisPanel）：统计摘要 + 样品直方图 + 累积频率分布 +
/// 煤层平面等值线预览（XY 投影：原为 mock XY 上的 IDW 热力 + Marching Squares 四级等值线 + 样品点，照原实现）。
/// 原 Canvas 直方图/CDF 改用仓库 ChartView(Bar / Line)；等值线预览保留 Canvas 叠加绘制。
/// </summary>
public partial class DataAnalysisPanel : UserControl
{
    private double[]? _samples;

    public DataAnalysisPanel()
    {
        InitializeComponent();
        UpdateStats(null);
    }

    /// <summary>样本值（被插值量 = Z 高程）。赋值即刷新四块子图。</summary>
    public double[]? Samples
    {
        get => _samples;
        set
        {
            _samples = value;
            UpdateStats(value);
            DrawHistogram(value);
            DrawCdf(value);
            DrawContourHeatmap(value);
        }
    }

    private void UpdateStats(double[]? samples)
    {
        var st = PickedPointEstimation.Stats(samples);
        if (st == null)
        {
            CountTextBlock.Text = "样品数: -";
            MeanTextBlock.Text = "均值: -";
            StdTextBlock.Text = "标准差: -";
            MinTextBlock.Text = "最小值: -";
            MaxTextBlock.Text = "最大值: -";
            CvTextBlock.Text = "变异系数: -";
            return;
        }
        var s = st.Value;
        CountTextBlock.Text = $"样品数: {s.count}";
        MeanTextBlock.Text = $"均值: {s.mean:F2}";
        StdTextBlock.Text = $"标准差: {s.std:F2}";
        MinTextBlock.Text = $"最小值: {s.min:F2}";
        MaxTextBlock.Text = $"最大值: {s.max:F2}";
        CvTextBlock.Text = $"变异系数: {s.cv:F2}";
    }

    private void OnCanvasSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_samples != null) DrawContourHeatmap(_samples);
    }

    private void DrawHistogram(double[]? samples)
    {
        HistogramChart.Series.Clear();
        HistogramChart.Categories.Clear();
        var bins = PickedPointEstimation.Histogram(samples);
        if (bins.Count > 0)
        {
            var s = new ChartSeries { Name = "频数", Kind = SeriesKind.Bar, Color = Color.FromRgb(13, 110, 253), Opacity = 0.7 };
            foreach (var (lo, hi, count) in bins)
            {
                HistogramChart.Categories.Add(((lo + hi) / 2).ToString("0.#"));
                s.Values.Add(count);
            }
            HistogramChart.Series.Add(s);
        }
        HistogramChart.Legend = LegendPlacement.Hidden;
        HistogramChart.YMin = 0;
        HistogramChart.Refresh();
    }

    private void DrawCdf(double[]? samples)
    {
        CdfChart.Series.Clear();
        var pts = PickedPointEstimation.Cdf(samples);
        if (pts.Count > 0)
        {
            var s = new ChartSeries { Name = "累积频率", Kind = SeriesKind.Line, Color = Color.FromRgb(25, 135, 84), ShowMarkers = false, StrokeThickness = 2 };
            foreach (var (v, c) in pts) s.Points.Add((v, c));
            CdfChart.Series.Add(s);
        }
        CdfChart.NumericX = true;
        CdfChart.Legend = LegendPlacement.Hidden;
        CdfChart.YMin = 0; CdfChart.YMax = 1;
        CdfChart.Refresh();
    }

    // ========== 煤层平面等值线热力图预览 ==========

    private void DrawContourHeatmap(double[]? samples)
    {
        ContourCanvas.Children.Clear();
        if (samples == null || samples.Length == 0) return;

        double canvasW = ContourCanvas.Bounds.Width > 0 ? ContourCanvas.Bounds.Width : 300;
        double canvasH = ContourCanvas.Bounds.Height > 0 ? ContourCanvas.Bounds.Height : 140;
        if (canvasW < 50 || canvasH < 50) return;

        // 1. 为每个样品生成 mock XY 坐标（基于固定种子保证可重复性）
        var points = PickedPointEstimation.MockPoints(samples);
        if (points.Count == 0) return;

        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        double minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        double minV = points.Min(p => p.Value), maxV = points.Max(p => p.Value);
        if (maxV - minV < 1e-9 || maxX - minX < 1e-9 || maxY - minY < 1e-9) return;

        // 2. 构建规则网格 (20x20)，IDW 插值
        int nx = 20, ny = 20;
        var grid = PickedPointEstimation.HeatGrid(points, minX, maxX, minY, maxY, nx, ny);

        // 3. 绘制热力图网格单元
        double cellW = canvasW / nx, cellH = canvasH / ny;
        for (int ix = 0; ix < nx; ix++)
            for (int iy = 0; iy < ny; iy++)
            {
                double t = (grid[ix, iy] - minV) / (maxV - minV);
                var (r, g, b) = PickedPointEstimation.HeatmapColor(t);
                var rect = new Rectangle
                {
                    Width = cellW + 1, Height = cellH + 1,   // +1 消除缝隙
                    Fill = new SolidColorBrush(Color.FromRgb(r, g, b)), Opacity = 0.85
                };
                Canvas.SetLeft(rect, ix * cellW);
                Canvas.SetTop(rect, (ny - 1 - iy) * cellH);   // Y 翻转
                ContourCanvas.Children.Add(rect);
            }

        // 4. Marching Squares 追踪等值线（4 条：低/中低/中高/高）
        double[] levels =
        {
            minV + (maxV - minV) * 0.20, minV + (maxV - minV) * 0.40,
            minV + (maxV - minV) * 0.60, minV + (maxV - minV) * 0.80
        };
        var levelColors = new[] { Color.FromRgb(0, 0, 139), Color.FromRgb(0, 128, 0), Color.FromRgb(255, 165, 0), Color.FromRgb(139, 0, 0) };
        for (int li = 0; li < levels.Length; li++)
        {
            foreach (var (p1, p2) in PickedPointEstimation.MarchingSquares(grid, levels[li]))
            {
                ContourCanvas.Children.Add(new Line
                {
                    StartPoint = new Point(p1.x * canvasW, (1 - p1.y) * canvasH),
                    EndPoint = new Point(p2.x * canvasW, (1 - p2.y) * canvasH),
                    Stroke = new SolidColorBrush(levelColors[li]), StrokeThickness = 1.5, Opacity = 0.9
                });
            }
        }

        // 5. 叠加样品点位置
        foreach (var p in points)
        {
            double px = (p.X - minX) / (maxX - minX) * canvasW;
            double py = (1 - (p.Y - minY) / (maxY - minY)) * canvasH;
            var ellipse = new Ellipse { Width = 4, Height = 4, Fill = Brushes.Black, Opacity = 0.6 };
            Canvas.SetLeft(ellipse, px - 2);
            Canvas.SetTop(ellipse, py - 2);
            ContourCanvas.Children.Add(ellipse);
        }
    }
}
