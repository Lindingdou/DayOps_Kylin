using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace PitMine3D.Kylin.Views.PointCloud;

/// <summary>
/// 剖面图窗（忠实原 PointCloudLib.Profile.ProfileChartDialog）：横轴里程、纵轴标高，
/// 画出剖面折线 + 网格 + 刻度 + 关键指标（里程 / 高程范围 / 高差 / 站点数 / 缺口数）。
///
/// 点云剖面**留缺口不插值**：没测到的站点在这里断开成两段折线，而不是连成一条假地面 ——
/// 这正是「点云剖面」相对「TIN 剖面」的关键差别，图上必须看得出来。
/// 代码构建，无 XAML；非模态（用户要一边转视口一边对着剖面看）。
/// </summary>
internal sealed class PcProfileChartWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>一个剖面站点：里程 + 高程；<see cref="Z"/> 为 NaN 表示该站没测到（图上断开）。</summary>
    public readonly record struct Station(double Dist, double Z);

    private readonly Canvas _canvas = new() { Background = Brushes.White };
    private readonly IReadOnlyList<Station> _stations;
    private readonly double _dMin, _dMax, _zMin, _zMax;

    public PcProfileChartWindow(string title, string subtitle, IReadOnlyList<Station> stations)
    {
        _stations = stations;
        Title = title;
        Width = 660; Height = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("geodb");

        var good = stations.Where(s => !double.IsNaN(s.Z)).ToList();
        _dMin = stations.Count > 0 ? stations.Min(s => s.Dist) : 0;
        _dMax = stations.Count > 0 ? stations.Max(s => s.Dist) : 1;
        _zMin = good.Count > 0 ? good.Min(s => s.Z) : 0;
        _zMax = good.Count > 0 ? good.Max(s => s.Z) : 1;
        if (_dMax - _dMin < 1e-9) _dMax = _dMin + 1;
        if (_zMax - _zMin < 1e-9) _zMax = _zMin + 1;

        int gaps = stations.Count(s => double.IsNaN(s.Z));
        var head = new TextBlock
        {
            Text = subtitle, FontSize = 12, Foreground = Brushes.Gray,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 10, 14, 4),
        };
        var stat = new TextBlock
        {
            FontSize = 12, Margin = new Thickness(14, 0, 14, 6),
            Text = $"里程 {(_dMax - _dMin).ToString("0.#", Inv)} m · 高程 {_zMin.ToString("0.##", Inv)} ~ {_zMax.ToString("0.##", Inv)} m"
                 + $"（高差 {(_zMax - _zMin).ToString("0.##", Inv)} m）· 站点 {good.Count}/{stations.Count}"
                 + (gaps > 0 ? $" · 缺口 {gaps} 站（没测到，按缺口断开不插值）" : ""),
        };
        var close = new Button { Content = "关闭", MinWidth = 80, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(14, 6, 14, 10) };
        close.Click += (_, _) => Close();

        var frame = new Border
        {
            BorderBrush = Brush.Parse("#DCDFE4"), BorderThickness = new Thickness(1),
            Margin = new Thickness(14, 0, 14, 4), Child = _canvas,
        };
        var root = new DockPanel();
        DockPanel.SetDock(head, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(stat, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(close, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(head); root.Children.Add(stat); root.Children.Add(close); root.Children.Add(frame);
        Content = root;

        _canvas.SizeChanged += (_, _) => Redraw();
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        double w = _canvas.Bounds.Width, h = _canvas.Bounds.Height;
        if (w < 60 || h < 60) return;
        const double padL = 56, padR = 12, padT = 10, padB = 26;
        double plotW = w - padL - padR, plotH = h - padT - padB;
        if (plotW <= 10 || plotH <= 10) return;

        double X(double d) => padL + (d - _dMin) / (_dMax - _dMin) * plotW;
        double Y(double z) => padT + (1 - (z - _zMin) / (_zMax - _zMin)) * plotH;

        void Line(double x1, double y1, double x2, double y2, IBrush b, double th = 1)
            => _canvas.Children.Add(new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = b, StrokeThickness = th });
        void Text(string t, double x, double y, HorizontalAlignment ha = HorizontalAlignment.Left)
        {
            var tb = new TextBlock { Text = t, FontSize = 10, Foreground = Brushes.Gray };
            Canvas.SetLeft(tb, ha == HorizontalAlignment.Right ? x - 46 : x);
            Canvas.SetTop(tb, y);
            if (ha == HorizontalAlignment.Right) { tb.Width = 44; tb.TextAlignment = TextAlignment.Right; }
            _canvas.Children.Add(tb);
        }

        var grid = Brush.Parse("#EEF1F5");
        var axis = Brush.Parse("#B6BDC6");
        // 网格 + 刻度：纵轴标高 5 档、横轴里程 5 档
        for (int i = 0; i <= 5; i++)
        {
            double z = _zMin + (_zMax - _zMin) * i / 5.0, y = Y(z);
            Line(padL, y, padL + plotW, y, grid);
            Text(z.ToString("0.#", Inv), padL - 6, y - 7, HorizontalAlignment.Right);
            double d = _dMin + (_dMax - _dMin) * i / 5.0, x = X(d);
            Line(x, padT, x, padT + plotH, grid);
            Text(d.ToString("0", Inv), x - 10, padT + plotH + 6);
        }
        Line(padL, padT, padL, padT + plotH, axis);                       // 纵轴(标高)
        Line(padL, padT + plotH, padL + plotW, padT + plotH, axis);       // 横轴(里程)
        Text("标高 m", 6, padT - 2);
        Text("里程 m", padL + plotW - 40, padT + plotH + 6);

        // 剖面折线：遇到缺口(NaN)就断开，另起一段
        var stroke = Brush.Parse("#2F7D32");
        var seg = new List<Point>();
        void Flush()
        {
            if (seg.Count >= 2)
                _canvas.Children.Add(new Polyline { Points = new List<Point>(seg), Stroke = stroke, StrokeThickness = 1.6 });
            else if (seg.Count == 1)
                _canvas.Children.Add(new Ellipse { Width = 3, Height = 3, Fill = stroke, [Canvas.LeftProperty] = seg[0].X - 1.5, [Canvas.TopProperty] = seg[0].Y - 1.5 });
            seg.Clear();
        }
        foreach (var s in _stations)
        {
            if (double.IsNaN(s.Z)) { Flush(); continue; }
            seg.Add(new Point(X(s.Dist), Y(s.Z)));
        }
        Flush();
    }

    /// <summary>非模态弹出（自检模式照弹：非模态不挡脚本）。stations 少于 2 个有效点时不弹。</summary>
    public static void Popup(Window owner, string title, string subtitle, IReadOnlyList<Station> stations)
    {
        if (stations.Count(s => !double.IsNaN(s.Z)) < 2) return;
        try { new PcProfileChartWindow(title, subtitle, stations).Show(owner); }
        catch (Exception ex) { PitMine3D.Kylin.CrashLog.Write("点云", $"剖面图窗打开失败: {ex.Message}"); }
    }
}
