using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// ① 工艺架构定义窗体(忠实原 GeoDataBase.ProcessArchitectureWindow)。
/// 三栏:左 系统树 / 中 参数表 / 右 详情。写 process_system / process_phase / parameter_definition。
/// </summary>
public partial class ProcessArchitectureWindow : Window
{
    private readonly GeoDbContext _ctx;
    private readonly ObservableCollection<ProcParamDef> _currentParams = new();
    private ProcPhase? _selectedPhase;

    /// <summary>仅供 XAML 编译器/设计器; 运行时用 <see cref="ProcessArchitectureWindow(GeoDbContext)"/>。</summary>
    public ProcessArchitectureWindow() { _ctx = null!; InitializeComponent(); }

    public ProcessArchitectureWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        paramsGrid.ItemsSource = _currentParams;
        archTree.SelectionChanged += OnTreeSelectionChanged;
        paramsGrid.SelectionChanged += OnParameterSelectionChanged;
        paramsGrid.CellEditEnded += (_, _) => dirtyHint.Text = "● 有未保存改动";
        Opened += (_, _) => LoadTree();
    }

    private void LoadTree()
    {
        try
        {
            archTree.Items.Clear();
            var systems = ProcessSystems(_ctx.Conn, activeOnly: false);
            foreach (var sys in systems.OrderBy(s => s.DisplayOrder).ThenBy(s => s.SystemId))
            {
                var sysNode = new TreeViewItem
                {
                    Header = $"📦 {sys.Name}  ({sys.Code})",
                    Tag = sys,
                    IsExpanded = true,
                    FontWeight = FontWeight.Bold
                };
                foreach (var ph in ProcessPhasesBySystem(_ctx.Conn, sys.SystemId))
                {
                    sysNode.Items.Add(new TreeViewItem
                    {
                        Header = $"⚙ {ph.Name}  ({ProcessParamCountByPhase(_ctx.Conn, ph.PhaseId)} 参数)",
                        Tag = ph,
                        FontWeight = FontWeight.Normal
                    });
                }
                archTree.Items.Add(sysNode);
            }
            statusText.Text = $"加载完成 · {systems.Count} 个系统";
        }
        catch (Exception ex) { statusText.Text = "加载失败:" + ex.Message; }
    }

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (archTree.SelectedItem is not TreeViewItem tv) { ClearParams(); return; }
        if (tv.Tag is ProcPhase phase)
        {
            _selectedPhase = phase;
            LoadParameters(phase);
        }
        else if (tv.Tag is ProcSystem sys)
        {
            _selectedPhase = null;
            phaseTitleText.Text = $"📦 {sys.Name} —— 选择下属环节查看参数";
            _currentParams.Clear();
            paramCountText.Text = "";
            detailPanel.Children.Clear();
        }
    }

    private void LoadParameters(ProcPhase phase)
    {
        _currentParams.Clear();
        foreach (var p in ProcessParamsByPhase(_ctx.Conn, phase.PhaseId)) _currentParams.Add(p);
        phaseTitleText.Text = $"⚙ {phase.Name}  ·  典型设备 {phase.TypicalEquipmentCategory ?? "—"}";
        paramCountText.Text = $"共 {_currentParams.Count} 个参数";
    }

    private void ClearParams()
    {
        _selectedPhase = null;
        _currentParams.Clear();
        phaseTitleText.Text = "选择左侧环节查看参数";
        paramCountText.Text = "";
        detailPanel.Children.Clear();
    }

    // ─── 系统 / 环节 CRUD ──────────────────────────────────────────────

    private async void OnAddSystem(object? sender, RoutedEventArgs e)
    {
        var code = await ProcessDialogs.PromptTextAsync(this, "新建工艺系统", "请输入英文编码(如 'safety'):");
        if (string.IsNullOrWhiteSpace(code)) return;
        var name = await ProcessDialogs.PromptTextAsync(this, "新建工艺系统", "请输入显示名(如 '安全环保系统'):");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            ProcessInsertSystem(_ctx.Conn, code.Trim(), name.Trim(), 99);
            LoadTree();
            statusText.Text = $"已新增工艺系统 {name.Trim()}";
        }
        catch (Exception ex) { statusText.Text = "新增失败:" + ex.Message; }
    }

    private async void OnAddPhase(object? sender, RoutedEventArgs e)
    {
        var sys = GetSelectedSystem();
        if (sys == null) { statusText.Text = "请先选中工艺系统"; return; }
        var code = await ProcessDialogs.PromptTextAsync(this, $"新建环节 / 系统 {sys.Name}", "环节编码(如 'drilling'):");
        if (string.IsNullOrWhiteSpace(code)) return;
        var name = await ProcessDialogs.PromptTextAsync(this, $"新建环节 / 系统 {sys.Name}", "显示名(如 '钻孔'):");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            int seq = ProcessPhasesBySystem(_ctx.Conn, sys.SystemId).Count + 1;
            ProcessInsertPhase(_ctx.Conn, sys.SystemId, code.Trim(), name.Trim(), seq);
            LoadTree();
            statusText.Text = $"已新增环节 {name.Trim()}";
        }
        catch (Exception ex) { statusText.Text = "新增失败:" + ex.Message; }
    }

    private async void OnDeleteTreeNode(object? sender, RoutedEventArgs e)
    {
        if (archTree.SelectedItem is not TreeViewItem tv) { statusText.Text = "请先选中树节点"; return; }
        if (tv.Tag is ProcPhase ph)
        {
            var paramCount = ProcessParamCountByPhase(_ctx.Conn, ph.PhaseId);
            if (!await ProcessDialogs.ConfirmAsync(this, "确认", $"删除环节「{ph.Name}」?该环节下 {paramCount} 个参数会级联删除。")) return;
            try { ProcessDeletePhase(_ctx.Conn, ph.PhaseId); ClearParams(); LoadTree(); statusText.Text = $"已删除 {ph.Name}"; }
            catch (Exception ex) { statusText.Text = "删除失败:" + ex.Message; }
        }
        else if (tv.Tag is ProcSystem sys)
        {
            var phaseCount = ProcessPhasesBySystem(_ctx.Conn, sys.SystemId).Count;
            if (!await ProcessDialogs.ConfirmAsync(this, "确认", $"删除系统「{sys.Name}」?{phaseCount} 个环节会级联删除(含所有参数)。")) return;
            try { ProcessDeleteSystem(_ctx.Conn, sys.SystemId); ClearParams(); LoadTree(); statusText.Text = $"已删除 {sys.Name}"; }
            catch (Exception ex) { statusText.Text = "删除失败:" + ex.Message; }
        }
    }

    private ProcSystem? GetSelectedSystem()
    {
        var tv = archTree.SelectedItem as TreeViewItem;
        if (tv?.Tag is ProcSystem sys) return sys;
        if (tv?.Tag is ProcPhase ph) return ProcessGetSystem(_ctx.Conn, ph.SystemId);
        return null;
    }

    // ─── 参数 CRUD ─────────────────────────────────────────────────────

    private void OnAddParameter(object? sender, RoutedEventArgs e)
    {
        if (_selectedPhase == null) { statusText.Text = "请先选中工艺环节"; return; }
        var p = new ProcParamDef
        {
            PhaseId = _selectedPhase.PhaseId,
            Code = $"new_param_{DateTime.Now:HHmmss}",
            Name = "新参数",
            Unit = "",
            ValueType = "numeric",
            DisplayOrder = _currentParams.Count + 1,
            IsActive = true,
            IsRequired = false
        };
        try
        {
            p.ParamId = ProcessInsertParam(_ctx.Conn, p);
            _currentParams.Add(p);
            paramsGrid.SelectedItem = p;
            paramsGrid.ScrollIntoView(p, null);
            paramCountText.Text = $"共 {_currentParams.Count} 个参数";
            statusText.Text = "已新增,请填编码/名称等并保存";
        }
        catch (Exception ex) { statusText.Text = "新增失败:" + ex.Message; }
    }

    private async void OnDeleteParameter(object? sender, RoutedEventArgs e)
    {
        if (paramsGrid.SelectedItem is not ProcParamDef p) return;
        if (!await ProcessDialogs.ConfirmAsync(this, "确认", $"删除参数「{p.Name}({p.Code})」?")) return;
        try
        {
            if (p.ParamId > 0) ProcessDeleteParam(_ctx.Conn, p.ParamId);
            _currentParams.Remove(p);
            paramCountText.Text = $"共 {_currentParams.Count} 个参数";
            statusText.Text = $"已删除 {p.Name}";
        }
        catch (Exception ex) { statusText.Text = "删除失败:" + ex.Message; }
    }

    private void OnSaveAllParameters(object? sender, RoutedEventArgs e)
    {
        try
        {
            int upd = 0;
            foreach (var p in _currentParams)
                if (p.ParamId > 0) { ProcessUpdateParam(_ctx.Conn, p); upd++; }
            statusText.Text = $"已保存 {upd} 个参数修改";
            dirtyHint.Text = "";
        }
        catch (Exception ex) { statusText.Text = "保存失败:" + ex.Message; }
    }

    // ─── 右侧详情 ──────────────────────────────────────────────────────

    private void OnParameterSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (paramsGrid.SelectedItem is not ProcParamDef p) { detailPanel.Children.Clear(); return; }
        RenderDetailPanel(p);
    }

    private void RenderDetailPanel(ProcParamDef p)
    {
        detailPanel.Children.Clear();
        AddDetailLine("编码", p.Code);
        AddDetailLine("名称", p.Name);
        AddDetailLine("单位", string.IsNullOrEmpty(p.Unit) ? "—" : p.Unit);
        AddDetailLine("类型", p.ValueType);
        AddDetailDivider("标准范围");
        AddDetailLine("下限", p.StandardMin?.ToString("F2") ?? "—");
        AddDetailLine("上限", p.StandardMax?.ToString("F2") ?? "—");
        AddDetailLine("推荐值", p.StandardDefault?.ToString("F2") ?? "—");
        AddDetailDivider("报警阈值");
        AddDetailLine("报警下限", p.AlarmLow?.ToString("F2") ?? "—");
        AddDetailLine("报警上限", p.AlarmHigh?.ToString("F2") ?? "—");
        if (!string.IsNullOrEmpty(p.CalcFormula))
        {
            AddDetailDivider("派生");
            AddDetailLine("公式", p.CalcFormula);
            AddDetailLine("源表", p.SourceTable ?? "—");
            AddDetailLine("源字段", p.SourceColumn ?? "—");
        }
        AddDetailDivider("设备约束");
        var constraints = ProcessConstraintsByParam(_ctx.Conn, p.ParamId);
        if (constraints.Count == 0)
        {
            detailPanel.Children.Add(new TextBlock
            {
                Text = "(无设备约束,后续可在专门界面添加)",
                FontSize = 11, Foreground = Brushes.DarkGray, Margin = new Thickness(0, 4, 0, 0)
            });
        }
        else
        {
            foreach (var c in constraints)
                AddDetailLine($"  {c.EquipmentModel}", $"{c.ConstraintType} {c.LimitValue?.ToString("F1") ?? "—"} ({c.Consequence})");
        }
    }

    private void AddDetailDivider(string title)
    {
        detailPanel.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeight.Bold, FontSize = 12,
            Foreground = Brushes.SteelBlue, Margin = new Thickness(0, 10, 0, 4)
        });
    }

    private void AddDetailLine(string label, string value)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
        var lab = new TextBlock { Text = label, Width = 90, Foreground = Brushes.Gray, FontSize = 11 };
        var val = new TextBlock { Text = value, FontSize = 12, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(lab, Avalonia.Controls.Dock.Left);
        dp.Children.Add(lab);
        dp.Children.Add(val);
        detailPanel.Children.Add(dp);
    }
}
