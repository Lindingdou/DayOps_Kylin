using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// ③ 平盘工艺地图窗体(忠实原 GeoDataBase.LocationProcessMapWindow)。
/// 平盘选择 + Tab1 现状视图(参数偏差+适配设备)+ Tab2 方案视图(MVP)。
/// </summary>
public partial class LocationProcessMapWindow : Window
{
    private readonly GeoDbContext _ctx;
    private readonly ObservableCollection<ProcParamCompareRow> _paramRows = new();
    private string? _currentLocation;
    private ProcPhase? _currentPhase;
    private ProcBinding? _currentBinding;
    private bool _suppressTemplateCombo;

    /// <summary>仅供 XAML 编译器/设计器; 运行时用 <see cref="LocationProcessMapWindow(GeoDbContext)"/>。</summary>
    public LocationProcessMapWindow() { _ctx = null!; InitializeComponent(); }

    public LocationProcessMapWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        paramCompareGrid.ItemsSource = _paramRows;
        phaseTree.SelectionChanged += OnPhaseSelected;
        Opened += (_, _) => LoadLocations();
    }

    private void LoadLocations()
    {
        locationCombo.Items.Clear();
        try
        {
            foreach (var loc in ProcLocations(_ctx.Conn, activeOnly: true))
                locationCombo.Items.Add(new ComboBoxItem { Content = $"{loc.LocationCode} · {loc.Name}", Tag = loc.LocationCode });
            if (locationCombo.Items.Count > 0) locationCombo.SelectedIndex = 0;
        }
        catch (Exception ex) { statusText.Text = "加载平盘失败:" + ex.Message; }
    }

    private void OnLocationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (locationCombo.SelectedItem is not ComboBoxItem item) return;
        _currentLocation = item.Tag?.ToString();
        if (_currentLocation == null) return;
        LoadPhaseTree();
    }

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        LoadPhaseTree();
        if (_currentPhase != null) LoadParameters(_currentPhase);
    }

    private void LoadPhaseTree()
    {
        phaseTree.Items.Clear();
        if (_currentLocation == null) return;
        try
        {
            var bindings = ProcActiveBindingsByLocation(_ctx.Conn, _currentLocation);
            var loc = ProcGetLocation(_ctx.Conn, _currentLocation);
            locationInfoText.Text = $"{loc?.Name ?? _currentLocation} · 标高 {loc?.ElevationM:F0} m · {loc?.Team ?? "—"} · {bindings.Count} 个绑定环节";

            var bySystem = new Dictionary<long, List<(ProcPhase ph, ProcBinding bd)>>();
            foreach (var bd in bindings)
            {
                var ph = ProcessGetPhase(_ctx.Conn, bd.PhaseId);
                if (ph == null) continue;
                if (!bySystem.ContainsKey(ph.SystemId)) bySystem[ph.SystemId] = new();
                bySystem[ph.SystemId].Add((ph, bd));
            }

            foreach (var (sysId, items) in bySystem.OrderBy(kv => kv.Key))
            {
                var sys = ProcessGetSystem(_ctx.Conn, sysId);
                if (sys == null) continue;
                var sysNode = new TreeViewItem { Header = $"📦 {sys.Name}", FontWeight = FontWeight.Bold, IsExpanded = true };
                foreach (var (ph, bd) in items.OrderBy(x => x.ph.SequenceOrder))
                {
                    var tplName = bd.BoundTemplateId.HasValue
                        ? ProcGetTemplate(_ctx.Conn, bd.BoundTemplateId.Value)?.Name ?? "(模板已删)"
                        : "(无模板)";
                    sysNode.Items.Add(new TreeViewItem
                    {
                        Header = $"⚙ {ph.Name}  · {tplName}",
                        Tag = new PhaseBindingTag(ph, bd),
                        FontWeight = FontWeight.Normal
                    });
                }
                phaseTree.Items.Add(sysNode);
            }
            statusText.Text = $"已加载 {bindings.Count} 个工艺环节";
        }
        catch (Exception ex) { statusText.Text = "加载失败:" + ex.Message; }
    }

    private void OnPhaseSelected(object? sender, SelectionChangedEventArgs e)
    {
        _paramRows.Clear();
        compatPanel.Children.Clear();
        if (phaseTree.SelectedItem is not TreeViewItem tv) return;
        if (tv.Tag is not PhaseBindingTag tag) { _currentPhase = null; return; }

        _currentPhase = tag.Phase;
        _currentBinding = tag.Binding;
        phaseHeaderText.Text = $"⚙ {_currentPhase.Name}  ·  {_currentBinding.LocationCode} 平盘";
        PopulateTemplateCombo(_currentBinding.BoundTemplateId);
        LoadParameters(_currentPhase);
    }

    private void PopulateTemplateCombo(long? selectedTemplateId)
    {
        _suppressTemplateCombo = true;
        templateCombo.Items.Clear();
        templateCombo.Items.Add(new ComboBoxItem { Content = "(不绑定模板)", Tag = null });
        foreach (var t in ProcTemplates(_ctx.Conn, activeOnly: true))
            templateCombo.Items.Add(new ComboBoxItem { Content = $"{t.Name} {t.Version}", Tag = t.TemplateId });
        foreach (var item in templateCombo.Items)
        {
            if (item is ComboBoxItem ci && (ci.Tag as long?) == selectedTemplateId)
            {
                templateCombo.SelectedItem = ci;
                break;
            }
        }
        _suppressTemplateCombo = false;
    }

    private void OnTemplateSwitched(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressTemplateCombo) return;
        if (_currentBinding == null || _currentLocation == null || _currentPhase == null) return;
        if (templateCombo.SelectedItem is not ComboBoxItem item) return;
        var newTplId = item.Tag as long?;
        try
        {
            ProcRebindTemplate(_ctx.Conn, _currentLocation, _currentPhase.PhaseId, newTplId);
            statusText.Text = $"已切换 {_currentPhase.Name} 的模板";
            _currentBinding = ProcActiveBinding(_ctx.Conn, _currentLocation, _currentPhase.PhaseId) ?? _currentBinding;
            LoadPhaseTree();
            LoadParameters(_currentPhase);
        }
        catch (Exception ex) { statusText.Text = "切换失败:" + ex.Message; }
    }

    private void LoadParameters(ProcPhase ph)
    {
        _paramRows.Clear();
        compatPanel.Children.Clear();
        if (_currentBinding == null || _currentLocation == null) return;
        try
        {
            foreach (var row in ProcParamCompareRows(_ctx.Conn, _currentLocation, ph.PhaseId, _currentBinding.BoundTemplateId))
                _paramRows.Add(row);
        }
        catch (Exception ex) { statusText.Text = "参数加载失败:" + ex.Message; }
        RenderCompatibleEquipment(ph);
    }

    private void RenderCompatibleEquipment(ProcPhase ph)
    {
        compatPanel.Children.Clear();
        List<ProcCompatModel> relevant;
        try { relevant = ProcCompatibleEquipment(_ctx.Conn, ph.PhaseId, _currentBinding?.BoundTemplateId); }
        catch { relevant = new List<ProcCompatModel>(); }

        var typical = ph.TypicalEquipmentCategory;
        if (relevant.Count == 0)
        {
            compatPanel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(typical) ? "(无型号数据)" : $"(无 {typical} 类型号)",
                Foreground = Brushes.Gray, FontStyle = FontStyle.Italic
            });
            return;
        }

        foreach (var m in relevant)
        {
            var icon = m.Pass ? "✅" : "❌";
            var color = m.Pass ? Brushes.SeaGreen : Brushes.Crimson;
            var b = new Border
            {
                Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 4),
                BorderBrush = color, BorderThickness = new Thickness(0, 0, 0, 1)
            };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = $"{icon} {m.Model}", FontWeight = FontWeight.Bold, Foreground = color });
            sp.Children.Add(new TextBlock { Text = m.Detail, FontSize = 10, Foreground = Brushes.Gray });
            b.Child = sp;
            compatPanel.Children.Add(b);
        }
    }

    private async void OnBindNewPhase(object? sender, RoutedEventArgs e)
    {
        if (_currentLocation == null) return;
        var dlg = new BindPhaseDialog(_ctx);
        if (!await dlg.ShowDialog<bool>(this)) return;
        try
        {
            ProcInsertBinding(_ctx.Conn, dlg.SelectedPhaseId, _currentLocation, dlg.SelectedTemplateId);
            LoadPhaseTree();
            statusText.Text = "已绑定";
        }
        catch (Exception ex) { statusText.Text = "绑定失败:" + ex.Message; }
    }

    // ─── 方案视图(MVP)────────────────────────────────────────────────────

    private void OnGeneratePlan(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!double.TryParse(targetStripBox.Text, out var strip)) strip = 0;
            if (!double.TryParse(targetCoalBox.Text, out var coal)) coal = 0;
            if (!int.TryParse(workDaysBox.Text, out var days)) days = 30;
            var matTag = (materialCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "rh-hard";
            var plan = ProcGeneratePlan(strip, coal, days, ProcHardnessOf(matTag));

            planResultPanel.Children.Clear();
            AddPlanCard("月剥离任务", $"{strip:F0} 万 m³", $"日均 {strip / plan.Days:F1} 万 m³ · 工作日 {plan.Days} 天", Colors.SteelBlue);
            AddPlanCard("推荐电铲", $"{plan.ShovelCount} 台 {plan.ShovelModel}", $"单铲日产能 {plan.ShovelDailyCap:F1} 万 m³", Colors.DarkSlateBlue);
            AddPlanCard("推荐卡车", $"{plan.TruckCount} 台 {plan.TruckModel}", $"铲车配比 1:{plan.TruckPerShovel}", Colors.SaddleBrown);

            AddPlanDivider("约束校验");
            try
            {
                var slopeSafety = ProcMinSlopeSafetyFactor(_ctx.Conn);
                if (!slopeSafety.HasValue) AddCheckItem("边坡稳定", false, "查询失败", "—");
                else AddCheckItem("边坡稳定", slopeSafety.Value >= 1.30, $"最小安全系数 F={slopeSafety.Value:F2}", "F ≥ 1.30");
            }
            catch { AddCheckItem("边坡稳定", false, "查询失败", "—"); }

            try
            {
                var inUseShovels = ProcEquipmentCountByCategory(_ctx.Conn, "Shovel");
                AddCheckItem("电铲库存", inUseShovels >= plan.ShovelCount, $"在册 {inUseShovels} 台 / 需 {plan.ShovelCount} 台", "库存充足");

                var inUseTrucks = ProcEquipmentCountByCategory(_ctx.Conn, "Truck");
                AddCheckItem("卡车库存", inUseTrucks >= plan.TruckCount, $"在册 {inUseTrucks} 台 / 需 {plan.TruckCount} 台",
                    inUseTrucks < plan.TruckCount ? $"⚠ 卡车缺口 {plan.TruckCount - inUseTrucks}" : "库存充足");
            }
            catch { }

            statusText.Text = "方案已生成";
        }
        catch (Exception ex) { statusText.Text = "生成失败:" + ex.Message; }
    }

    private void AddPlanCard(string title, string value, string sub, Color color)
    {
        var brush = new SolidColorBrush(color);
        var b = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(15, color.R, color.G, color.B)),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 8),
            BorderBrush = brush, BorderThickness = new Thickness(0, 0, 0, 2)
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = title, FontSize = 11, Foreground = Brushes.Gray });
        sp.Children.Add(new TextBlock { Text = value, FontSize = 16, FontWeight = FontWeight.Bold, Foreground = brush });
        sp.Children.Add(new TextBlock { Text = sub, FontSize = 11, Foreground = Brushes.DimGray, Margin = new Thickness(0, 2, 0, 0) });
        b.Child = sp;
        planResultPanel.Children.Add(b);
    }

    private void AddPlanDivider(string title)
    {
        planResultPanel.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeight.Bold, FontSize = 13,
            Foreground = Brushes.SteelBlue, Margin = new Thickness(0, 12, 0, 8)
        });
    }

    private void AddCheckItem(string title, bool pass, string detail, string conclusion)
    {
        var color = pass ? new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32)) : new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        var b = new Border { BorderBrush = color, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 0, 0, 4) };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = (pass ? "✅ " : "⚠ ") + title, FontWeight = FontWeight.Bold, Foreground = color });
        sp.Children.Add(new TextBlock { Text = detail, FontSize = 11, Foreground = Brushes.DimGray });
        sp.Children.Add(new TextBlock { Text = conclusion, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = color, Margin = new Thickness(0, 2, 0, 0) });
        b.Child = sp;
        planResultPanel.Children.Add(b);
    }

    private sealed class PhaseBindingTag
    {
        public ProcPhase Phase { get; }
        public ProcBinding Binding { get; }
        public PhaseBindingTag(ProcPhase p, ProcBinding b) { Phase = p; Binding = b; }
    }
}

/// <summary>"绑定新环节"对话框 — 选系统+环节+模板(原 BindPhaseDialog); ShowDialog&lt;bool&gt; 绑定=true。</summary>
internal sealed class BindPhaseDialog : Window
{
    public long SelectedPhaseId { get; private set; }
    public long? SelectedTemplateId { get; private set; }

    private readonly ComboBox _sysCombo;
    private readonly ComboBox _phaseCombo;
    private readonly ComboBox _tplCombo;
    private readonly GeoDbContext _ctx;

    public BindPhaseDialog(GeoDbContext ctx)
    {
        _ctx = ctx;
        Title = "绑定新工艺环节";
        Width = 460; Height = 280; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("geodb");

        var grid = new Grid { Margin = new Thickness(16) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var sysLab = new TextBlock { Text = "工艺系统", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
        Grid.SetRow(sysLab, 0); Grid.SetColumn(sysLab, 0); grid.Children.Add(sysLab);
        _sysCombo = new ComboBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var s in ProcessSystems(_ctx.Conn))
            _sysCombo.Items.Add(new ComboBoxItem { Content = s.Name, Tag = s.SystemId });
        _sysCombo.SelectionChanged += (_, _) => RefreshPhases();
        Grid.SetRow(_sysCombo, 0); Grid.SetColumn(_sysCombo, 1); grid.Children.Add(_sysCombo);

        var phLab = new TextBlock { Text = "工艺环节", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
        Grid.SetRow(phLab, 1); Grid.SetColumn(phLab, 0); grid.Children.Add(phLab);
        _phaseCombo = new ComboBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(_phaseCombo, 1); Grid.SetColumn(_phaseCombo, 1); grid.Children.Add(_phaseCombo);

        var tplLab = new TextBlock { Text = "套用模板", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
        Grid.SetRow(tplLab, 2); Grid.SetColumn(tplLab, 0); grid.Children.Add(tplLab);
        _tplCombo = new ComboBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
        _tplCombo.Items.Add(new ComboBoxItem { Content = "(不绑定模板)", Tag = null });
        foreach (var t in ProcTemplates(_ctx.Conn))
            _tplCombo.Items.Add(new ComboBoxItem { Content = $"{t.Name} {t.Version}", Tag = t.TemplateId });
        _tplCombo.SelectedIndex = 0;
        Grid.SetRow(_tplCombo, 2); Grid.SetColumn(_tplCombo, 1); grid.Children.Add(_tplCombo);

        var bp = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0), Spacing = 8 };
        var ok = new Button { Content = "绑定", Padding = new Thickness(20, 4, 20, 4), IsDefault = true };
        var cancel = new Button { Content = "取消", Padding = new Thickness(20, 4, 20, 4), IsCancel = true };
        ok.Click += (_, _) =>
        {
            if (_phaseCombo.SelectedItem is ComboBoxItem phi && phi.Tag is long pid)
            {
                SelectedPhaseId = pid;
                SelectedTemplateId = (_tplCombo.SelectedItem as ComboBoxItem)?.Tag as long?;
                Close(true);
            }
        };
        cancel.Click += (_, _) => Close(false);
        bp.Children.Add(ok); bp.Children.Add(cancel);
        Grid.SetRow(bp, 3); Grid.SetColumn(bp, 0); Grid.SetColumnSpan(bp, 2); grid.Children.Add(bp);
        Content = grid;

        if (_sysCombo.Items.Count > 0) _sysCombo.SelectedIndex = 0;
    }

    private void RefreshPhases()
    {
        _phaseCombo.Items.Clear();
        if (_sysCombo.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not long sysId) return;
        foreach (var ph in ProcessPhasesBySystem(_ctx.Conn, sysId))
            _phaseCombo.Items.Add(new ComboBoxItem { Content = ph.Name, Tag = ph.PhaseId });
        if (_phaseCombo.Items.Count > 0) _phaseCombo.SelectedIndex = 0;
    }
}
