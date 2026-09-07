using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Controls.Charts;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 设备生产数据(忠实原 EquipmentProductionDataWindow): 班次记录 增/删/编辑/保存(全量重灌 production_record) +
/// 设备/班次/仅含故障筛选 + 统计分析看板(6 磁贴 + 班次对比/日趋势/故障 Pareto/设备 Top10)。
/// </summary>
public partial class EquipmentProductionDataWindow : Window
{
    private readonly GeoDbContext _ctx;
    private readonly ObservableCollection<EqProductionRecord> _records = new();
    private readonly ObservableCollection<EqProductionRecord> _view = new();
    private bool _ready;

    /// <summary>仅供 XAML 加载器/设计器使用; 运行时请用 (GeoDbContext) 构造。</summary>
    public EquipmentProductionDataWindow() { _ctx = null!; InitializeComponent(); }

    public EquipmentProductionDataWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        dataGrid.ItemsSource = _view;
        LoadData();
        _ready = true;
        RefreshView();
    }

    private void LoadData()
    {
        foreach (var r in _records) r.PropertyChanged -= OnRecordChanged;
        _records.Clear();
        foreach (var r in EqLoadProduction(_ctx.Conn)) { _records.Add(r); r.PropertyChanged += OnRecordChanged; }
        RefreshFilterDropdowns();
        statusText.Text = $"已加载 {_records.Count} 条记录";
    }

    private void OnRecordChanged(object? sender, PropertyChangedEventArgs e) { if (_ready) RenderAnalytics(); }

    private void RefreshFilterDropdowns()
    {
        var items = new List<string> { "全部" };
        items.AddRange(_records.Select(r => r.EquipmentId).Distinct().OrderBy(x => x));
        filterEquipment.ItemsSource = items;
        filterEquipment.SelectedIndex = 0;
    }

    private string EqFilter => filterEquipment.SelectedItem as string ?? "全部";
    private string ShiftFilter => (filterShift.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "全部";

    private void RefreshView()
    {
        var rows = EqFilterProduction(_records, EqFilter, ShiftFilter, filterFaultOnly.IsChecked == true);
        _view.Clear();
        foreach (var r in rows) _view.Add(r);
        UpdateCount();
        RenderAnalytics();
    }

    private void OnFilterChanged(object? sender, RoutedEventArgs e) { if (_ready) RefreshView(); }

    private void OnMainTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || e.Source is not TabControl) return;   // 子 ComboBox 冒泡的 SelectionChanged 需判源
        RenderAnalytics();
    }

    // ─── 统计分析看板(随当前筛选联动) ───
    private void RenderAnalytics()
    {
        if (!_ready) return;
        var a = EqAnalyzeProduction(_view.ToList());
        analyticsScope.Text = a.Scope;
        if (a.Rows == 0)
        {
            foreach (var c in new[] { shiftChart, dailyChart, faultParetoChart, equipTopChart }) { c.Series = new(); c.Categories = new(); c.Refresh(); }
            return;
        }
        kpiTotalOut.Text = $"{a.TotalOutWan:N0}";
        kpiDailyAvg.Text = a.Days > 0 ? $"{a.DailyAvgWan:N1}" : "—";
        kpiShiftAvg.Text = $"{a.ShiftAvgWan:N2}";
        kpiEffRate.Text = $"{a.EffRate * 100:F1}%";
        kpiFaultRate.Text = $"{a.FaultRate * 100:F1}%";
        kpiFaultCount.Text = a.FaultCount.ToString();

        shiftChart.Categories = a.ByShift.Select(s => s.Shift + "班").ToList();
        shiftChart.Series = new List<ChartSeries> { new() { Name = "产量(万m³)", Kind = SeriesKind.Bar, Color = Color.Parse("#42A5F5"), Values = a.ByShift.Select(s => (double?)s.Wan).ToList(), ShowValueLabels = true, ValueFormat = "N0" } };
        shiftChart.Refresh();

        dailyChart.Categories = a.ByDay.Select(d => d.Day.ToString("MM-dd")).ToList();
        dailyChart.Series = new List<ChartSeries> { new() { Name = "日产量(万m³)", Kind = SeriesKind.Line, Color = Color.Parse("#00897B"), StrokeThickness = 3, MarkerSize = 8, Values = a.ByDay.Select(d => (double?)d.Wan).ToList() } };
        dailyChart.Refresh();

        if (a.FaultPareto.Count > 0)
        {
            faultParetoChart.Categories = a.FaultPareto.Select(f => f.Reason).ToList();
            faultParetoChart.Series = new List<ChartSeries>
            {
                new() { Name = "故障时长(h)", Kind = SeriesKind.Bar, Color = Color.Parse("#EF5350"), Values = a.FaultPareto.Select(f => (double?)f.Hours).ToList() },
                new() { Name = "累计占比%", Kind = SeriesKind.Line, Color = Color.Parse("#FB8C00"), StrokeThickness = 3, MarkerSize = 8, Values = a.FaultPareto.Select(f => (double?)f.CumPct).ToList(), SecondaryAxis = true },
            };
        }
        else { faultParetoChart.Categories = new() { "无故障记录" }; faultParetoChart.Series = new(); }
        faultParetoChart.Refresh();

        equipTopChart.Categories = a.TopEquip.Select(t => t.EquipmentId).ToList();
        equipTopChart.Series = new List<ChartSeries> { new() { Name = "产量(万m³)", Kind = SeriesKind.Bar, Color = Color.Parse("#5E35B1"), Values = a.TopEquip.Select(t => (double?)t.Wan).ToList() } };
        equipTopChart.Refresh();
    }

    private void UpdateCount() => resultCountText.Text = $"共 {_records.Count} 条,当前显示 {_view.Count} 条";

    // ─── 工具栏命令(批量导入/导出已收归「数据导入导出中心」)───
    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        var vm = new EqProductionRecord { EquipmentId = "新设备", Date = DateTime.Today, Shift = "A", OutputM3 = 0, WorkHours = 8, FaultHours = 0, FaultReason = "" };
        vm.PropertyChanged += OnRecordChanged;
        _records.Add(vm);
        _view.Add(vm);
        dataGrid.SelectedItem = vm;
        dataGrid.ScrollIntoView(vm, null);
        UpdateCount();
        RenderAnalytics();
    }

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (dataGrid.SelectedItem is not EqProductionRecord vm) { await EquipmentMessageBox.Info(this, "请先选择要删除的记录"); return; }
        if (await EquipmentMessageBox.Confirm(this, $"删除 {vm.EquipmentId} {vm.Date:yyyy-MM-dd} {vm.Shift} 班记录?"))
        {
            vm.PropertyChanged -= OnRecordChanged;
            _records.Remove(vm);
            _view.Remove(vm);
            UpdateCount();
            RenderAnalytics();
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            int n = EqSaveProduction(_ctx.Conn, _records);
            statusText.Text = $"已保存 {n} 条记录";
            await EquipmentMessageBox.Info(this, "已保存到 production_record 表");
        }
        catch (Exception ex) { await EquipmentMessageBox.Info(this, $"保存失败:{ex.Message}", "错误"); }
    }

    private void OnReloadClick(object? sender, RoutedEventArgs e) { LoadData(); RefreshView(); }
}
