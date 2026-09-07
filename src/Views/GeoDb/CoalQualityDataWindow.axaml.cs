using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>煤质数据管理窗(忠实原 CoalQualityDataWindow): CRUD 浏览 + 衍生指标 + CSV 导入/导出/模板 + 重算层平均。</summary>
public partial class CoalQualityDataWindow : Window
{
    private readonly GeoDbContext _ctx;
    private readonly ObservableCollection<CoalSampleRow> _rows = new();
    private List<CoalSampleRow> _allRows = new();
    private List<CoalTypeInference.ClassRange> _ranges = new();
    private List<CoalGradeRuleFull> _ashRules = new(), _sulfurRules = new(), _qnetRules = new();
    private bool _suppressFilterChanged;

    /// <summary>仅供 XAML 设计器/编译器使用。</summary>
    public CoalQualityDataWindow() { _ctx = null!; InitializeComponent(); }

    public CoalQualityDataWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        grid.ItemsSource = _rows;
        InitFilters();
        LoadFromDb();
    }

    // ─────────────────────────────────────────────────────────
    private void InitFilters()
    {
        seamFilter.Items.Add("全部");
        foreach (var s in CoalSeamDefs(_ctx.Conn)) seamFilter.Items.Add(s.Code);
        seamFilter.SelectedIndex = 0;

        coalTypeFilter.Items.Add("全部");
        foreach (var c in CoalClassRefs(_ctx.Conn)) coalTypeFilter.Items.Add(c.Code);
        coalTypeFilter.SelectedIndex = 0;

        _ranges = GeoDataQueries.GetCoalClassificationRanges(_ctx.Conn);
        _ashRules = CoalGradeRules(_ctx.Conn, "ash");
        _sulfurRules = CoalGradeRules(_ctx.Conn, "sulfur");
        _qnetRules = CoalGradeRules(_ctx.Conn, "qnet");
    }

    // ─────────────────────────────────────────────────────────
    private void LoadFromDb()
    {
        try
        {
            _allRows = CoalLoadSamples(_ctx.Conn);
            BuildTree();
            ApplyFilter();
            subtitleText.Text = $"  共 {_allRows.Count} 条化验段 / "
                              + $"{_allRows.Select(r => r.BoreholeId).Distinct().Count()} 孔 / "
                              + $"{_allRows.Select(r => r.SeamCode).Distinct().Count()} 煤层";
            statusText.Text = "数据已加载";
        }
        catch (Exception ex)
        {
            _ = CoalMsgBox.ShowAsync(this, "错误", $"加载煤质数据失败：{ex.Message}");
            statusText.Text = "加载失败";
        }
    }

    // ─────────────────────────────────────────────────────────
    private void BuildTree()
    {
        seamTree.Items.Clear();
        foreach (var grp in _allRows.GroupBy(r => r.SeamCode).OrderBy(g => g.Key))
        {
            var seamNode = new TreeViewItem
            {
                Header = $"{grp.Key}  ({grp.Count()} 段 / {grp.Select(r => r.BoreholeId).Distinct().Count()} 孔)",
                Tag = ("seam", grp.Key),
                IsExpanded = false,
            };
            foreach (var hg in grp.GroupBy(r => r.HoleId).OrderBy(h => h.Key))
                seamNode.Items.Add(new TreeViewItem { Header = $"{hg.Key}  ({hg.Count()} 段)", Tag = ("hole_seam", $"{hg.Key}|{grp.Key}") });
            seamTree.Items.Add(seamNode);
        }
    }

    // ─────────────────────────────────────────────────────────
    private void ApplyFilter()
    {
        IEnumerable<CoalSampleRow> q = _allRows;
        if (seamFilter.SelectedIndex > 0) q = q.Where(r => r.SeamCode == seamFilter.SelectedItem?.ToString());
        if (coalTypeFilter.SelectedIndex > 0) q = q.Where(r => r.CoalType == coalTypeFilter.SelectedItem?.ToString());
        var hf = (holeFilter.Text ?? "").Trim();
        if (hf.Length > 0) q = q.Where(r => r.HoleId.Contains(hf, StringComparison.OrdinalIgnoreCase));

        _rows.Clear();
        foreach (var r in q.OrderBy(r => r.HoleId).ThenBy(r => r.SeamCode).ThenBy(r => r.DepthFrom)) _rows.Add(r);
        statusText.Text = $"显示 {_rows.Count} / {_allRows.Count} 条";
        if (_rows.Count == 0) ClearDetail();
    }

    private void OnFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterChanged) return;
        ApplyFilter();
    }

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyFilter();
    }

    // ─────────────────────────────────────────────────────────
    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (seamTree.SelectedItem is not TreeViewItem item || item.Tag is not ValueTuple<string, string> tag) return;
        // 改 filter 时屏蔽其 SelectionChanged 的隐式 ApplyFilter, 最后显式调一次(忠实原)
        _suppressFilterChanged = true;
        try
        {
            if (tag.Item1 == "seam") { holeFilter.Text = ""; SetSeamFilter(tag.Item2); }
            else if (tag.Item1 == "hole_seam")
            {
                var parts = tag.Item2.Split('|');
                if (parts.Length == 2) { holeFilter.Text = parts[0]; SetSeamFilter(parts[1]); }
            }
        }
        finally { _suppressFilterChanged = false; }
        ApplyFilter();
    }

    private void SetSeamFilter(string code)
    {
        for (int i = 0; i < seamFilter.Items.Count; i++)
            if (seamFilter.Items[i]?.ToString() == code) { if (seamFilter.SelectedIndex != i) seamFilter.SelectedIndex = i; return; }
    }

    // ─────────────────────────────────────────────────────────
    private void OnRowSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (grid.SelectedItem is not CoalSampleRow vm) { ClearDetail(); return; }
        BuildDetail(vm);
    }

    private static string NN(double? v) => v.HasValue ? v.Value.ToString("F2") : "—";
    private static string NI(int? v) => v.HasValue ? v.Value.ToString() : "—";

    private void ClearDetail()
    {
        detailTitle.Text = "（未选择样品）";
        MetricsBegin();
        derivedHost.Children.Clear();
    }

    // ═════════════════════════ 详情面板渲染 ═════════════════════════
    private void BuildDetail(CoalSampleRow vm)
    {
        detailTitle.Text = $"{vm.HoleId} · {vm.SeamCode} 煤层     {vm.DepthFrom:F2} – {vm.DepthTo:F2} m  （厚 {vm.SampleThickness:F2} m）";

        MetricsBegin();
        MetricHeader();
        MetricPair("水分 Mad", NN(vm.MadRaw), NN(vm.MadClean), "%");
        MetricPair("灰分 Ad", NN(vm.AdRaw), NN(vm.AdClean), "%");
        MetricPair("挥发分 Vdaf", NN(vm.VdafRaw), NN(vm.VdafClean), "%");
        MetricPair("固定碳 FCd", NN(vm.FcdRaw), NN(vm.FcdClean), "%");
        MetricPair("全硫 St", NN(vm.StdRaw), NN(vm.StdClean), "%");
        MetricPair("焦渣特征", NI(vm.CharResidueRaw), NI(vm.CharResidueClean), "");
        MetricDivider();
        MetricSingle("弹筒发热量 Qb,d", NN(vm.QgrD), "MJ/kg");
        MetricSingle("低位发热量 Qnet", NN(vm.QnetAd), "MJ/kg");
        MetricSingle("胶质层 X / Y", PairStr(vm.PlasticXMm, vm.PlasticYMm), "mm");
        MetricSingle("粘结指数 G", NN(vm.CakingG), "");
        MetricSingle("浮煤回收率", NN(vm.CleanCoalYield), "%");

        BuildDerived(vm);
    }

    private void BuildDerived(CoalSampleRow vm)
    {
        derivedHost.Children.Clear();
        var resolved = CoalTypeInference.ResolveCoalType(vm.VdafRaw, vm.CakingG, vm.PlasticYMm, _ranges);
        var accent = Color.FromRgb(0x00, 0x86, 0xD1);
        var neutral = Color.FromRgb(0x90, 0x9A, 0xA8);

        derivedHost.Children.Add(Caption("煤类反推 · GB 5751"));
        var typeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        typeRow.Children.Add(Chip(resolved ?? "未判定", resolved is not null ? accent : neutral, 14.5));
        typeRow.Children.Add(new TextBlock
        {
            Text = $"原表标注  {vm.CoalType ?? "—"}", FontSize = 12, Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Foreground = Muted,
        });
        derivedHost.Children.Add(typeRow);

        if (resolved is not null && vm.CoalType is not null)
        {
            bool ok = resolved == vm.CoalType;
            var chip = Chip(ok ? "✓ 与原表一致" : "⚠ 与原表不一致", ok ? Color.FromRgb(0x55, 0x8B, 0x2F) : Color.FromRgb(0xF5, 0x7C, 0x00));
            chip.Margin = new Thickness(0, 7, 0, 0);
            chip.HorizontalAlignment = HorizontalAlignment.Left;
            derivedHost.Children.Add(chip);
        }

        derivedHost.Children.Add(ThinDivider());
        derivedHost.Children.Add(Caption("质量分级 · GB/T 15224"));
        GradeRow("灰分 Ad", CoalFindLevel(_ashRules, vm.AdRaw), NN(vm.AdRaw), "%");
        GradeRow("全硫 St", CoalFindLevel(_sulfurRules, vm.StdRaw), NN(vm.StdRaw), "%");
        GradeRow("发热量 Qnet", CoalFindLevel(_qnetRules, vm.QnetAd), NN(vm.QnetAd), "MJ/kg");

        if (!string.IsNullOrWhiteSpace(vm.Remark))
        {
            derivedHost.Children.Add(ThinDivider());
            derivedHost.Children.Add(new TextBlock
            {
                Text = $"备注：{vm.Remark}", FontSize = 11.5, FontStyle = FontStyle.Italic, TextWrapping = TextWrapping.Wrap, Foreground = Muted,
            });
        }
    }

    // ── 指标表构建 ──
    private int _metricRow;
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#6B7280"));
    private static readonly IBrush Primary = new SolidColorBrush(Color.Parse("#1F2937"));
    private static readonly IBrush BorderBr = new SolidColorBrush(Color.Parse("#DCDFE4"));

    private void MetricsBegin()
    {
        metricGrid.Children.Clear();
        metricGrid.RowDefinitions.Clear();
        _metricRow = 0;
    }

    private int NewMetricRow()
    {
        metricGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        return _metricRow++;
    }

    private void PutMetric(Control el, int row, int col, int span = 1)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        if (span > 1) Grid.SetColumnSpan(el, span);
        metricGrid.Children.Add(el);
    }

    private void MetricHeader()
    {
        int r = NewMetricRow();
        PutMetric(HeaderCell("原煤"), r, 1);
        PutMetric(HeaderCell("浮煤"), r, 2);
    }

    private void MetricPair(string label, string raw, string clean, string unit)
    {
        int r = NewMetricRow();
        PutMetric(LabelCell(label), r, 0);
        PutMetric(NumCell(raw), r, 1);
        PutMetric(NumCell(clean), r, 2);
        if (unit.Length > 0) PutMetric(UnitCell(unit), r, 3);
    }

    private void MetricSingle(string label, string value, string unit)
    {
        int r = NewMetricRow();
        PutMetric(LabelCell(label), r, 0);
        PutMetric(NumCell(value), r, 1, 2);
        if (unit.Length > 0) PutMetric(UnitCell(unit), r, 3);
    }

    private void MetricDivider()
    {
        int r = NewMetricRow();
        PutMetric(new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6), Background = BorderBr }, r, 0, 4);
    }

    private static TextBlock LabelCell(string t) => new()
    { Text = t, FontSize = 12.5, Margin = new Thickness(0, 2.5, 0, 2.5), VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };

    private static TextBlock NumCell(string t)
    {
        bool missing = t == "—";
        return new TextBlock
        {
            Text = t, FontSize = 12.5, Margin = new Thickness(0, 2.5, 0, 2.5), TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, FontWeight = missing ? FontWeight.Normal : FontWeight.SemiBold,
            Foreground = missing ? Muted : Primary,
        };
    }

    private static TextBlock UnitCell(string t) => new()
    { Text = t, FontSize = 11, Margin = new Thickness(6, 2.5, 0, 2.5), VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };

    private static TextBlock HeaderCell(string t) => new()
    { Text = t, FontSize = 11, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 3), TextAlignment = TextAlignment.Right, Foreground = Muted };

    // ── 衍生指标：分级行 + chip ──
    private void GradeRow(string label, CoalGradeRuleFull? rule, string value, string unit)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition(80, GridUnitType.Pixel));
        row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        var lab = new TextBlock { Text = label, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Muted };
        Grid.SetColumn(lab, 0);
        var (cr, cg, cb) = CoalHexToRgb(rule?.ColorHex, (0x90, 0x9A, 0xA8));
        var chip = Chip(rule?.LevelName ?? "—", Color.FromRgb(cr, cg, cb));
        chip.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(chip, 1);
        bool missing = value == "—";
        var val = new TextBlock
        {
            Text = missing ? "—" : $"{value} {unit}", FontSize = 12, Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center, Foreground = missing ? Muted : Primary,
        };
        Grid.SetColumn(val, 2);
        row.Children.Add(lab); row.Children.Add(chip); row.Children.Add(val);
        derivedHost.Children.Add(row);
    }

    private static TextBlock Caption(string t) => new() { Text = t, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Muted };

    private static Border ThinDivider() => new() { Height = 1, Margin = new Thickness(0, 10, 0, 8), Background = BorderBr };

    /// <summary>软色标 chip(忠实原: 背景色相淡染, 文字压深保证对比; 浅色主题)。</summary>
    private static Border Chip(string text, Color color, double fontSize = 12.5)
    {
        Color textColor = Mix(color, Colors.Black, 0.34);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x24, color.R, color.G, color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x7A, color.R, color.G, color.B)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 1.5, 9, 2.5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = fontSize, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(textColor) },
        };
    }

    private static string PairStr(double? a, double? b) => (a.HasValue || b.HasValue) ? $"{NN(a)} / {NN(b)}" : "—";

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // ─────────────────────────────────────────────────────────
    private void OnRefreshClick(object? sender, RoutedEventArgs e) => LoadFromDb();

    private async void OnRebuildSummaryClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            int n = CoalRebuildSummary(_ctx.Conn);
            statusText.Text = $"已重算 {n} 条层平均";
            _ctx.Status($"煤质数据管理：已重算 {n} 条层平均");
            await CoalMsgBox.ShowAsync(this, "重算层平均", $"重算完成：写入 {n} 条 (孔, 煤层) 平均行。");
        }
        catch (Exception ex) { await CoalMsgBox.ShowAsync(this, "错误", $"重算失败：{ex.Message}"); }
    }

    // ─── CRUD ───
    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        var dlg = new CoalSampleEditDialog(_ctx);
        if (!await dlg.ShowDialog<bool>(this)) return;
        try
        {
            var id = CoalInsertSample(_ctx.Conn, dlg.Result);
            statusText.Text = $"新增 #{id}";
            LoadFromDb();
        }
        catch (Exception ex) { await CoalMsgBox.ShowAsync(this, "错误", $"新增失败: {ex.Message}"); }
    }

    private void OnEditClick(object? sender, RoutedEventArgs e) => EditSelectedRow();
    private void OnGridDoubleClick(object? sender, TappedEventArgs e) => EditSelectedRow();

    private async void EditSelectedRow()
    {
        if (grid.SelectedItem is not CoalSampleRow vm) return;
        var src = CoalGetSample(_ctx.Conn, vm.Id);
        if (src is null) { await CoalMsgBox.ShowAsync(this, "提示", "该样品已不存在(可能已被删)，请刷新。"); return; }
        var dlg = new CoalSampleEditDialog(_ctx, src);
        if (!await dlg.ShowDialog<bool>(this)) return;
        try
        {
            CoalUpdateSample(_ctx.Conn, dlg.Result);
            statusText.Text = $"已更新 #{src.Id}";
            LoadFromDb();
        }
        catch (Exception ex) { await CoalMsgBox.ShowAsync(this, "错误", $"更新失败: {ex.Message}"); }
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (grid.SelectedItem is not CoalSampleRow vm) { await CoalMsgBox.ShowAsync(this, "提示", "请先选中一行。"); return; }
        if (!await CoalMsgBox.ConfirmAsync(this, "确认删除", $"删除采样段 #{vm.Id}  ({vm.HoleId} / {vm.SeamCode} / {vm.DepthFrom:F2}m) ?")) return;
        try
        {
            CoalDeleteSample(_ctx.Conn, vm.Id);
            statusText.Text = $"已删除 #{vm.Id}";
            LoadFromDb();
        }
        catch (Exception ex) { await CoalMsgBox.ShowAsync(this, "错误", $"删除失败: {ex.Message}"); }
    }

    // ─── 导入 / 导出 / 模板（CSV）───
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var path = await _ctx.OpenFileAsync("选择煤质化验数据（CSV）", new[] { "*.csv" });
        if (path == null) return;
        var ans = await CoalMsgBox.AskAsync(this, "导入冲突策略",
            "遇到重复记录（同 孔号 / 煤层 / 采样起深）时如何处理？\n\n「覆盖」= 覆盖已存在　　「跳过」= 跳过已存在　　「取消」= 放弃导入",
            "覆盖", "跳过", "取消");
        if (ans is null or "取消") return;
        var policy = ans == "覆盖" ? CoalConflict.Overwrite : CoalConflict.Skip;
        try
        {
            var rep = CoalImportCsv(_ctx.Conn, File.ReadAllText(path), policy);
            LoadFromDb();
            statusText.Text = $"导入完成：新增 {rep.Inserted} / 覆盖 {rep.Overwritten} / 跳过 {rep.Skipped}";
            await CoalMsgBox.ShowAsync(this, "导入完成", rep.ToMessage());
        }
        catch (Exception ex) { await CoalMsgBox.ShowAsync(this, "错误", $"导入失败：{ex.Message}"); }
    }

    private async void OnExportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var rows = _rows.Select(CoalRowCells).ToList();
            var path = await _ctx.SaveTextAsync("导出煤质数据 (CSV, UTF-8 BOM)", $"煤质数据_{DateTime.Now:yyyyMMdd_HHmm}.csv", CoalCsvText(rows));
            if (path == null) return;
            statusText.Text = $"已导出 {rows.Count} 条 → {Path.GetFileName(path)}";
            await CoalMsgBox.ShowAsync(this, "导出完成", $"已导出 {rows.Count} 条到\n{path}");
        }
        catch (Exception ex) { await CoalMsgBox.ShowAsync(this, "错误", $"导出失败：{ex.Message}"); }
    }

    private async void OnTemplateClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = await _ctx.SaveTextAsync("下载导入模板 (CSV)", "煤质化验导入模板.csv", CoalTemplateCsv());
            if (path == null) return;
            statusText.Text = "已生成导入模板";
            await CoalMsgBox.ShowAsync(this, "下载模板", $"模板已保存到\n{path}\n\n表头即标准列，填入数据后可从「导入 CSV」回灌。");
        }
        catch (Exception ex) { await CoalMsgBox.ShowAsync(this, "错误", $"生成模板失败：{ex.Message}"); }
    }
}
