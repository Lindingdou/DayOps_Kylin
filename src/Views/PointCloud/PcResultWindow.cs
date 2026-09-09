using System;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PitMine3D.Kylin.Views.PointCloud;

/// <summary>
/// 点云统计结果窗（忠实原 QualityStatsDialog / C2cResultWindow 的角色）：
/// 上半是「指标名 — 值」表，下半是分布直方图。非模态 —— 用户要一边转视口看着色图、
/// 一边对着数字判断（原版 C2C 统计窗就是刻意非模态的）。代码构建，无 XAML。
/// </summary>
internal sealed class PcResultWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly string _title;
    private readonly IReadOnlyList<(string k, string v)> _rows;
    private readonly string? _note;
    private readonly IReadOnlyList<double>? _hist;
    private readonly double _histLo, _histHi;
    private readonly string? _histTitle;

    public PcResultWindow(string title, IReadOnlyList<(string k, string v)> rows,
                          IReadOnlyList<double>? histogram = null, double histLo = 0, double histHi = 0,
                          string? histTitle = null, string? note = null)
    {
        _title = title; _rows = rows; _note = note;
        _hist = histogram; _histLo = histLo; _histHi = histHi; _histTitle = histTitle;
        Title = title;
        Width = 460; Height = histogram is { Count: > 0 } ? 520 : 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("geodb");

        var panel = new StackPanel { Margin = new Thickness(16, 12), Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.Bold, FontSize = 14 });
        if (!string.IsNullOrEmpty(note))
            panel.Children.Add(new TextBlock { Text = note, Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), Margin = new Thickness(0, 6) };
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var k = new TextBlock { Text = rows[i].k, Foreground = Brushes.Gray, Margin = new Thickness(0, 3), FontSize = 12 };
            var v = new TextBlock { Text = rows[i].v, Margin = new Thickness(0, 3), FontSize = 12, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(k, i); Grid.SetColumn(k, 0); grid.Children.Add(k);
            Grid.SetRow(v, i); Grid.SetColumn(v, 1); grid.Children.Add(v);
        }
        panel.Children.Add(grid);

        if (histogram is { Count: > 0 })
        {
            panel.Children.Add(new TextBlock
            {
                Text = histTitle ?? "分布直方图",
                FontWeight = FontWeight.Bold, FontSize = 12, Margin = new Thickness(0, 8, 0, 2),
            });
            double max = 0;
            foreach (double h in histogram) if (h > max) max = h;
            var bars = new StackPanel { Height = 140, Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Bottom };
            foreach (double h in histogram)
            {
                double frac = max > 0 ? h / max : 0;
                bars.Children.Add(new Border
                {
                    Width = 14,
                    Height = Math.Max(1, frac * 138),
                    Background = new SolidColorBrush(Color.Parse("#4D8FE0")),
                    VerticalAlignment = VerticalAlignment.Bottom,
                });
            }
            panel.Children.Add(new Border { Height = 145, Child = bars });
            panel.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                Children =
                {
                    new TextBlock { Text = histLo.ToString("0.###", Inv), FontSize = 11, Foreground = Brushes.Gray },
                    new TextBlock
                    {
                        Text = histHi.ToString("0.###", Inv), FontSize = 11, Foreground = Brushes.Gray,
                        HorizontalAlignment = HorizontalAlignment.Right, [Grid.ColumnProperty] = 1,
                    },
                },
            });
        }

        // 底部动作条：忠实原版两个结果窗的按钮 —— C2C 统计窗的「复制统计」、算量结果窗的「导出 Excel / 导出 PDF」。
        // Excel 落到 CSV：全项目一致的替换口径（Excel 互操作在麒麟上不可用，CSV 两边都能开）。
        var copy = new Button { Content = "复制统计", MinWidth = 86 };
        copy.Click += async (_, _) => { await CopyAsync(); _status.Text = "已复制到剪贴板"; };
        var csv = new Button { Content = "导出 CSV", MinWidth = 92 };
        csv.Click += async (_, _) => await ExportAsync(false);
        var pdf = new Button { Content = "导出 PDF", MinWidth = 92 };
        pdf.Click += async (_, _) => await ExportAsync(true);
        var close = new Button { Content = "关闭", MinWidth = 72, IsCancel = true };
        close.Click += (_, _) => Close();
        panel.Children.Add(new DockPanel
        {
            Margin = new Thickness(0, 10, 0, 0),
            Children =
            {
                _status,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { copy, csv, pdf, close },
                },
            },
        });
        Content = new ScrollViewer { Content = panel };
    }

    private readonly TextBlock _status = new()
    {
        FontSize = 11, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>统计文本（复制/CSV 共用）：标题 + 说明 + 逐行 键,值 + 直方图桶。</summary>
    private string ToText(bool csv)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(csv ? $"# {_title}" : _title);
        if (!string.IsNullOrEmpty(_note)) sb.AppendLine(csv ? "# " + _note : _note);
        sb.AppendLine(csv ? "项,值" : "");
        foreach (var (k, v) in _rows)
            sb.AppendLine(csv ? $"{Csv(k)},{Csv(v)}" : $"{k}\t{v}");
        if (_hist is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine(csv ? $"# {_histTitle ?? "分布直方图"}（区间 {_histLo.ToString("0.###", Inv)} ~ {_histHi.ToString("0.###", Inv)}）" : _histTitle ?? "分布直方图");
            if (csv) sb.AppendLine("桶下限,桶上限,计数");
            double step = _hist.Count > 0 ? (_histHi - _histLo) / _hist.Count : 0;
            for (int i = 0; i < _hist.Count; i++)
            {
                double lo = _histLo + step * i, hi = lo + step;
                sb.AppendLine(csv
                    ? $"{lo.ToString("0.####", Inv)},{hi.ToString("0.####", Inv)},{_hist[i].ToString("0.###", Inv)}"
                    : $"{lo.ToString("0.###", Inv)} ~ {hi.ToString("0.###", Inv)}\t{_hist[i].ToString("0.###", Inv)}");
            }
        }
        return sb.ToString();
    }

    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private async Task CopyAsync()
    {
        try
        {
            var cb = GetTopLevel(this)?.Clipboard;
            if (cb != null) await cb.SetTextAsync(ToText(false));
        }
        catch (Exception ex) { _status.Text = "复制失败：" + ex.Message; }
    }

    private async Task ExportAsync(bool pdf)
    {
        try
        {
            string safe = string.Join("_", _title.Split(System.IO.Path.GetInvalidFileNameChars()));
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = pdf ? "导出 PDF" : "导出 CSV",
                SuggestedFileName = safe + (pdf ? ".pdf" : ".csv"),
                DefaultExtension = pdf ? "pdf" : "csv",
                FileTypeChoices = new[]
                {
                    pdf ? new FilePickerFileType("PDF 文档") { Patterns = new[] { "*.pdf" } }
                        : new FilePickerFileType("CSV 表格") { Patterns = new[] { "*.csv" } },
                },
            });
            if (file == null) return;
            string path = file.Path.LocalPath;
            if (pdf) PcReportPdf.Write(_title, _note, _rows, _hist, _histLo, _histHi, _histTitle, path);
            else System.IO.File.WriteAllText(path, ToText(true), new System.Text.UTF8Encoding(true));   // BOM: Excel 打开不乱码
            _status.Text = "已导出：" + System.IO.Path.GetFileName(path);
        }
        catch (Exception ex) { _status.Text = "导出失败：" + ex.Message; }
    }

    /// <summary>
    /// 非模态显示 —— 用户要一边转视口看着色图、一边对着数字判断（原版 C2C 统计窗即为此刻意非模态）。
    /// 非模态不挡自检脚本，故自检模式照开，截图可核对窗口内容本身。
    /// </summary>
    public static void Popup(Window owner, string title, IReadOnlyList<(string k, string v)> rows,
                             IReadOnlyList<double>? histogram = null, double lo = 0, double hi = 0,
                             string? histTitle = null, string? note = null)
    {
        try
        {
            var w = new PcResultWindow(title, rows, histogram, lo, hi, histTitle, note);
            w.Show(owner);
        }
        catch (Exception ex) { PitMine3D.Kylin.CrashLog.Write("点云", $"{title} 结果窗打开失败: {ex.Message}"); }
    }
}
