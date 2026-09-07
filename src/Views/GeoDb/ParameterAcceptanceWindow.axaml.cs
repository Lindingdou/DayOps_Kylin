using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// ④ 现场验收录入窗体(忠实原 GeoDataBase.ParameterAcceptanceWindow)。
/// 按 平盘 + 环节 + 日期 选定上下文后,自动展开该环节所有参数定义,
/// 录入实测值时实时计算偏差和状态,提交时批量写入 parameter_acceptance。
/// </summary>
public partial class ParameterAcceptanceWindow : Window
{
    private readonly GeoDbContext _ctx;
    private readonly ObservableCollection<ProcAcceptanceRow> _rows = new();
    private ProcPhase? _currentPhase;
    private string? _currentLocation;

    /// <summary>仅供 XAML 编译器/设计器; 运行时用 <see cref="ParameterAcceptanceWindow(GeoDbContext)"/>。</summary>
    public ParameterAcceptanceWindow() { _ctx = null!; InitializeComponent(); }

    public ParameterAcceptanceWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        paramsGrid.ItemsSource = _rows;
        measureDatePicker.SelectedDate = new DateTimeOffset(DateTime.Today);
        acceptedByBox.Text = Environment.UserName;
        measureDatePicker.SelectedDateChanged += (_, _) => LoadParameters();
        Opened += (_, _) =>
        {
            LoadLocations();
            LoadPhasesAcrossAllSystems();
        };
    }

    private DateTime MeasureDate => measureDatePicker.SelectedDate?.Date ?? DateTime.Today;

    private void LoadLocations()
    {
        locationCombo.Items.Clear();
        foreach (var loc in ProcLocations(_ctx.Conn, activeOnly: true))
            locationCombo.Items.Add(new ComboBoxItem { Content = $"{loc.LocationCode} · {loc.Name}", Tag = loc.LocationCode });
        if (locationCombo.Items.Count > 0) locationCombo.SelectedIndex = 0;
    }

    private void LoadPhasesAcrossAllSystems()
    {
        phaseCombo.Items.Clear();
        foreach (var sys in ProcessSystems(_ctx.Conn))
            foreach (var ph in ProcessPhasesBySystem(_ctx.Conn, sys.SystemId))
                phaseCombo.Items.Add(new ComboBoxItem { Content = $"{sys.Name} → {ph.Name}", Tag = ph.PhaseId });
        if (phaseCombo.Items.Count > 0) phaseCombo.SelectedIndex = 0;
    }

    private void OnLocationOrPhaseChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (locationCombo.SelectedItem is not ComboBoxItem locItem) return;
        if (phaseCombo.SelectedItem is not ComboBoxItem phItem) return;

        _currentLocation = locItem.Tag?.ToString();
        var phaseId = phItem.Tag as long?;
        if (_currentLocation == null || !phaseId.HasValue) return;

        _currentPhase = ProcessGetPhase(_ctx.Conn, phaseId.Value);
        LoadParameters();
    }

    private void LoadParameters()
    {
        _rows.Clear();
        if (_currentPhase == null || _currentLocation == null) return;
        try
        {
            var (rows, tplName) = ProcLoadAcceptanceRows(_ctx.Conn, _currentLocation, _currentPhase.PhaseId, MeasureDate);
            phaseHeaderText.Text = $"⚙ {_currentPhase.Name}  ·  {_currentLocation} 平盘  ·  模板:{tplName}";
            phaseInfoText.Text = $"{rows.Count} 个参数,其中 {rows.Count(r => r.ParamDef.IsRequired)} 个必填";
            foreach (var r in rows) _rows.Add(r);
        }
        catch (Exception ex) { statusText.Text = "加载失败:" + ex.Message; }
    }

    // ─── 提交 ──────────────────────────────────────────────────────────

    private void OnSubmitAcceptance(object? sender, RoutedEventArgs e)
    {
        if (_currentPhase == null || _currentLocation == null) { statusText.Text = "请先选择平盘和环节"; return; }

        var conclusion = conclusionPassRadio.IsChecked == true ? "合格"
                       : conclusionPartialRadio.IsChecked == true ? "整改后合格"
                       : "不合格";
        try
        {
            var r = ProcSubmitAcceptance(_ctx.Conn, _rows, _currentLocation, _currentPhase.PhaseId, MeasureDate,
                conclusion, acceptedByBox.Text?.Trim(), scopeBox.Text?.Trim(), remarkBox.Text?.Trim());
            statusText.Text = $"✅ 提交成功:新增 {r.Inserted} 条,更新 {r.Updated} 条,跳过空 {r.Skipped} 条";
            LoadParameters();   // 重新拉,带出最新偏差状态
        }
        catch (Exception ex) { statusText.Text = "提交失败:" + ex.Message; }
    }

    private async void OnLoadHistory(object? sender, RoutedEventArgs e)
    {
        if (_currentLocation == null || _currentPhase == null) return;
        try
        {
            var hist = ProcAcceptanceByLocationPhase(_ctx.Conn, _currentLocation, _currentPhase.PhaseId).Take(50).ToList();
            var lines = hist.Select(h =>
                $"{h.MeasureDate}  · 参数 {h.ParamId}  · 实测 {h.MeasuredValue?.ToString("F2") ?? h.MeasuredText ?? "-"}  · 偏差 {h.DeviationPct?.ToString("F1") ?? "-"}%  · {h.Status}  · {h.AcceptedBy}");
            await ProcessDialogs.InfoAsync(this, "验收历史", "近 50 条历史记录:\n\n" + string.Join("\n", lines));
        }
        catch (Exception ex) { statusText.Text = "查询失败:" + ex.Message; }
    }

    private async void OnShowAlerts(object? sender, RoutedEventArgs e)
    {
        try
        {
            var fails = ProcRecentAcceptanceByStatus(_ctx.Conn, "fail", 30);
            var warns = ProcRecentAcceptanceByStatus(_ctx.Conn, "warning", 30);
            var msg = "📊 近 30 天偏差报警\n\n" +
                      $"严重(fail):{fails.Count} 条\n" +
                      $"警告(warning):{warns.Count} 条\n\n" +
                      "Top 5 严重:\n" +
                      string.Join("\n", fails.Take(5).Select(a =>
                          $"  · {a.MeasureDate} · 平盘 {a.LocationCode} · 参数 {a.ParamId} · 实测 {a.MeasuredValue:F2}"));
            await ProcessDialogs.InfoAsync(this, "偏差报警", msg);
        }
        catch (Exception ex) { statusText.Text = "查询失败:" + ex.Message; }
    }
}
