// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/IndicatorLibraryView.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局；MessageBox → CoalMsgBox）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.TaskLib.Reporting;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 指标 / 计算规则库 —— 「定制化计算规则」页（<see cref="ReportHubWindow"/> 的第三页）。内置指标只读；自定义指标可增删改：
///   · 结构化：字段 + 聚合 + 物料过滤；· 公式：对其它指标 Id 四则运算。
/// 保存后进 <see cref="IndicatorLibrary"/>，报表模板即可按 Id 绑定。带「试算」在样例数据上实时验证。
/// </summary>
public sealed class IndicatorLibraryView : UserControl
{
    /// <summary>指标库列表行。<b>必须是属性，不能是字段</b>——Binding 只认属性。</summary>
    internal sealed class IndicatorRow
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Tag { get; set; } = "";
        public bool IsCustom { get; set; }
        public CustomIndicatorDef? Custom { get; set; }
    }

    private static readonly FactField[] Fields =
    {
        FactField.PlanVol, FactField.ActualVol, FactField.PlanTonnage, FactField.ActualTonnage,
        FactField.PlannedHours, FactField.ActualHours, FactField.Shortfall,
        FactField.Fuel, FactField.Power, FactField.HaulTKm, FactField.HaulDistance,
        FactField.Ash, FactField.Calorific, FactField.Sulfur, FactField.Moisture, FactField.Count,
        FactField.DumpVolume, FactField.LooseVol,
    };
    private static readonly string[] FieldLabels =
    {
        "计划量(m³实方)", "实绩量(m³实方)", "计划量(吨)", "实绩量(吨)",
        "计划工时", "实际工时", "欠产量",
        "柴油耗", "电耗", "运输功(t·km)", "有效运距(km)",
        "灰分", "发热量", "硫分", "水分", "计数",
        "排弃占容方(×Kr)", "运输松方(×Ks)",
    };
    private static readonly AggOp[] Aggs = { AggOp.Sum, AggOp.Avg, AggOp.WeightedAvgByActualVol, AggOp.Min, AggOp.Max, AggOp.Count };
    private static readonly string[] AggLabels = { "求和", "平均", "按实绩量加权平均", "最小", "最大", "计数" };
    private static readonly MaterialFilter[] Filters = { MaterialFilter.All, MaterialFilter.Coal, MaterialFilter.Waste };
    private static readonly string[] FilterLabels = { "全部", "仅煤", "仅岩" };

    private readonly List<ProductionFact> _facts = FactSource.FromCurrentBoard();
    private CustomIndicatorDef _editing = new();
    private bool _isBuiltin;

    /// <summary>页签切换会重放 Loaded，初始化只许跑一次。</summary>
    private bool _initialized;

    /// <summary>库变更（保存/删除）后触发，供宿主转给报表模板设计器刷新指标下拉。</summary>
    public event Action? LibraryChanged;

    private readonly ListBox indList = new();
    private readonly StackPanel actionPanel = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock lblEval = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel editorPanel = new();
    private readonly TextBox txtName = new() { Margin = new Thickness(0, 0, 16, 6) };
    private readonly TextBox txtUnit = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly TextBox txtId = new() { IsReadOnly = true, Margin = new Thickness(0, 0, 16, 6) };
    private readonly TextBox txtFormat = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly ComboBox cboMode = new() { Margin = new Thickness(0, 0, 16, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel pnlStructured = new();
    private readonly StackPanel pnlFormula = new() { IsVisible = false };
    private readonly ComboBox cboField = new() { MinWidth = 150 };
    private readonly ComboBox cboAgg = new() { MinWidth = 150 };
    private readonly ComboBox cboFilter = new() { MinWidth = 90 };
    private readonly TextBox txtFormula = new() { Height = 46, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
    private readonly TextBlock txtFormulaHelp = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox txtTarget = new() { MinWidth = 70 };
    private readonly CheckBox chkHigher = new() { Content = "越大越好", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 6) };
    private readonly TextBox txtWarn = new() { MinWidth = 60, Text = "10" };

    public IndicatorLibraryView()
    {
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("286,*") };

        var lib = new DockPanel();
        var libBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var nb = TaskUi.Btn("新建", OnNew, 60); nb.Margin = new Thickness(0, 0, 6, 0); libBtns.Children.Add(nb);
        var db = TaskUi.Btn("删除", OnDelete, 60); db.Margin = new Thickness(0); libBtns.Children.Add(db);
        DockPanel.SetDock(libBtns, Avalonia.Controls.Dock.Bottom); lib.Children.Add(libBtns);
        indList.ItemTemplate = new FuncDataTemplate<IndicatorRow>((_, _) =>
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var n = new TextBlock(); n.Bind(TextBlock.TextProperty, new Binding(nameof(IndicatorRow.Name))); sp.Children.Add(n);
            var t = new TextBlock { Foreground = ReportViewBuilder.Hex("#9AA5B1"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            t.Bind(TextBlock.TextProperty, new Binding(nameof(IndicatorRow.Tag))); sp.Children.Add(t);
            return sp;
        });
        ReportTemplateView.CompactList(indList);
        indList.SelectionChanged += (_, _) => OnSelect();
        lib.Children.Add(indList);
        var libBox = TaskUi.GroupBox("指标库（内置只读 · 自定义可增删改）", lib, new Thickness(0, 0, 8, 0), 6);
        Grid.SetColumn(libBox, 0); root.Children.Add(libBox);

        var ed = new DockPanel();
        // 保存/试算钉底；内置指标只读时动作组跟着 editorPanel 一起禁
        var footGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") }; footGrid.ColumnDefinitions[1].MinWidth = 12;
        var save = TaskUi.Btn("保存指标", OnSave, 90, bold: true); save.Margin = new Thickness(0, 0, 8, 0); save.Padding = new Thickness(8, 3);
        save.Background = ReportViewBuilder.Hex("#1D9E75"); save.Foreground = Brushes.White; save.BorderThickness = new Thickness(0);
        actionPanel.Children.Add(save);
        var ev = TaskUi.Btn("试算", Evaluate, 70); ev.Margin = new Thickness(0, 0, 12, 0);
        ToolTip.SetTip(ev, "在当前作业日的样例事实上算一遍，看这条规则出不出得来数");
        actionPanel.Children.Add(ev);
        Grid.SetColumn(actionPanel, 0); footGrid.Children.Add(actionPanel);
        TaskUi.Theme(lblEval, TextBlock.ForegroundProperty, "Theme.Text.Body");
        Grid.SetColumn(lblEval, 1); footGrid.Children.Add(lblEval);
        var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 10, 0, 0), Child = footGrid };
        TaskUi.Theme(foot, Border.BorderBrushProperty, "Theme.Surface.Border");
        DockPanel.SetDock(foot, Avalonia.Controls.Dock.Bottom); ed.Children.Add(foot);

        var fg = new Grid { Margin = new Thickness(0, 0, 0, 6), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,140"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        Cell(fg, 0, 0, FLbl("名称")); Cell(fg, 0, 1, txtName); Cell(fg, 0, 2, FLbl("单位")); Cell(fg, 0, 3, txtUnit);
        Cell(fg, 1, 0, FLbl("Id")); TaskUi.Theme(txtId, TextBox.ForegroundProperty, "Theme.Text.Muted"); Cell(fg, 1, 1, txtId); Cell(fg, 1, 2, FLbl("数字格式")); Cell(fg, 1, 3, txtFormat);
        Cell(fg, 2, 0, FLbl("计算方式"));
        foreach (var s in new[] { "结构化（字段+聚合+过滤）", "公式（指标间四则运算）" }) cboMode.Items.Add(new ComboBoxItem { Content = s });
        cboMode.SelectedIndex = 0; cboMode.SelectionChanged += (_, _) => UpdateModePanels();
        Cell(fg, 2, 1, cboMode);
        editorPanel.Children.Add(fg);

        // 结构化
        pnlStructured.Children.Add(SubHead("结构化计算规则"));
        var wp = new WrapPanel { Orientation = Orientation.Horizontal };
        wp.Children.Add(Pair("字段", cboField, 16)); wp.Children.Add(Pair("聚合", cboAgg, 16)); wp.Children.Add(Pair("物料", cboFilter, 0));
        pnlStructured.Children.Add(wp);
        editorPanel.Children.Add(pnlStructured);

        // 公式
        pnlFormula.Children.Add(SubHead("公式（引用其它指标 Id，支持 + - * / 和括号）"));
        pnlFormula.Children.Add(txtFormula);
        TaskUi.Theme(txtFormulaHelp, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        pnlFormula.Children.Add(txtFormulaHelp);
        editorPanel.Children.Add(pnlFormula);

        var th = SubHead("评价阈值（可选，留空则不出红黄绿）"); th.Margin = new Thickness(0, 12, 0, 4);
        editorPanel.Children.Add(th);
        var wp2 = new WrapPanel { Orientation = Orientation.Horizontal };
        wp2.Children.Add(Pair("目标值", txtTarget, 16)); wp2.Children.Add(chkHigher); wp2.Children.Add(Pair("黄灯带宽", txtWarn, 0));
        editorPanel.Children.Add(wp2);

        ed.Children.Add(new ScrollViewer { Content = editorPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var edBox = TaskUi.GroupBox("指标定义", ed, new Thickness(0), 10);
        Grid.SetColumn(edBox, 1); root.Children.Add(edBox);

        Content = root;
        AttachedToVisualTree += (_, _) => OnLoaded();
    }

    private static TextBlock FLbl(string t)
    {
        var l = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 6) };
        TaskUi.Theme(l, TextBlock.ForegroundProperty, "Theme.Text.Body");
        return l;
    }

    private static TextBlock SubHead(string t)
    {
        var l = new TextBlock { Text = t, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 4) };
        TaskUi.Theme(l, TextBlock.ForegroundProperty, "Theme.Text.Body");
        return l;
    }

    private static StackPanel Pair(string label, Control ctl, double right)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, right, 6) };
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        TaskUi.Theme(l, TextBlock.ForegroundProperty, "Theme.Text.Body");
        sp.Children.Add(l); sp.Children.Add(ctl);
        return sp;
    }

    private static void Cell(Grid g, int r, int c, Control ctl) { Grid.SetRow(ctl, r); Grid.SetColumn(ctl, c); g.Children.Add(ctl); }

    private void OnLoaded()
    {
        if (_initialized) return;
        _initialized = true;

        cboField.ItemsSource = FieldLabels;
        cboAgg.ItemsSource = AggLabels;
        cboFilter.ItemsSource = FilterLabels;
        cboField.SelectedIndex = 1; cboAgg.SelectedIndex = 0; cboFilter.SelectedIndex = 0;

        txtFormulaHelp.Text = "可用指标：" + string.Join("　", IndicatorRegistry.Load().All().Select(i => $"{i.Name}={i.Id}"));
        BuildList();
        if (indList.ItemCount > 0) indList.SelectedIndex = 0;
    }

    private void BuildList(string? selectId = null)
    {
        var rows = new List<IndicatorRow>();
        foreach (var i in IndicatorRegistry.BuiltIn().All().OrderBy(x => x.Category))
            rows.Add(new IndicatorRow { Id = i.Id, Name = i.Name, Tag = "[内置]", IsCustom = false });
        foreach (var c in IndicatorLibrary.LoadAll())
            rows.Add(new IndicatorRow { Id = c.Id, Name = c.Name, Tag = "[自定义]", IsCustom = true, Custom = c });
        indList.ItemsSource = rows;
        if (selectId != null)
            indList.SelectedItem = rows.FirstOrDefault(r => r.Id == selectId);
    }

    private void OnSelect()
    {
        if (indList.SelectedItem is not IndicatorRow row) return;
        if (row.IsCustom && row.Custom != null) LoadEditor(row.Custom.ShallowCopy());
        else LoadBuiltin(row.Id);
    }

    private void LoadBuiltin(string id)
    {
        _isBuiltin = true;
        SetEditable(false);
        var ind = IndicatorRegistry.BuiltIn().Resolve(id);
        txtName.Text = ind?.Name ?? id;
        txtUnit.Text = ind?.Unit ?? "";
        txtId.Text = id;
        txtFormat.Text = ind?.Format ?? "0";
        cboMode.SelectedIndex = 0;
        double v = ind?.Evaluate(_facts) ?? double.NaN;
        // 内置指标要把「聚合方式 + 这个指标回答什么问题」摆出来，避免"有指标不知道拿来干嘛"
        string spec = ind == null ? "" : $"聚合：{ind.Agg}" + (ind.Question.Length > 0 ? $"　·　{ind.Question}" : "") + Environment.NewLine;
        lblEval.Text = spec + $"内置指标（代码定义，只读）。可在公式中引用 Id：{id}　·　试算值(全矿)= {(double.IsNaN(v) ? "—" : v.ToString(ind?.Format ?? "0.##"))} {ind?.Unit}";
        lblEval.Foreground = ReportViewBuilder.Hex("#6C757D");
    }

    private void LoadEditor(CustomIndicatorDef d)
    {
        _isBuiltin = false;
        _editing = d;
        SetEditable(true);
        txtName.Text = d.Name;
        txtUnit.Text = d.Unit;
        txtId.Text = d.Id;
        txtFormat.Text = d.Format;
        cboMode.SelectedIndex = d.Mode == IndicatorMode.Formula ? 1 : 0;
        cboField.SelectedIndex = Math.Max(0, Array.IndexOf(Fields, d.Field));
        cboAgg.SelectedIndex = Math.Max(0, Array.IndexOf(Aggs, d.Agg));
        cboFilter.SelectedIndex = Math.Max(0, Array.IndexOf(Filters, d.Filter));
        txtFormula.Text = d.Formula;
        txtTarget.Text = d.Target?.ToString(CultureInfo.InvariantCulture) ?? "";
        chkHigher.IsChecked = d.HigherIsBetter;
        txtWarn.Text = d.WarnBand.ToString(CultureInfo.InvariantCulture);
        UpdateModePanels();
        Evaluate();
    }

    /// <summary>只读/可改。<b>动作组必须跟着一起禁</b>。</summary>
    private void SetEditable(bool on)
    {
        editorPanel.IsEnabled = on;
        actionPanel.IsEnabled = on;
    }

    private void UpdateModePanels()
    {
        bool formula = cboMode.SelectedIndex == 1;
        pnlStructured.IsVisible = !formula;
        pnlFormula.IsVisible = formula;
    }

    private void OnNew()
    {
        LoadEditor(new CustomIndicatorDef { Name = "自定义指标", Unit = "", Category = "自定义" });
        indList.SelectedItem = null;
        lblEval.Text = "填好后点「保存指标」。";
        lblEval.Foreground = ReportViewBuilder.Hex("#6C757D");
        txtName.Focus();
    }

    private async void OnDelete()
    {
        if (indList.SelectedItem is not IndicatorRow row || !row.IsCustom)
        { lblEval.Text = "内置指标不可删除。"; lblEval.Foreground = ReportViewBuilder.Hex("#B7791F"); return; }
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null && !await TaskUi.Confirm(owner, "确认", $"删除指标「{row.Name}」？引用它的报表模板将显示为空。")) return;
        IndicatorLibrary.Delete(row.Id);
        BuildList();
        LibraryChanged?.Invoke();
        lblEval.Text = "已删除。";
    }

    private void Evaluate()
    {
        if (_isBuiltin) return;
        var d = ReadEditor();
        if (d.Mode == IndicatorMode.Formula && !ExprEval.IsValid(d.Formula, out var err))
        { lblEval.Text = "公式语法错误：" + err; lblEval.Foreground = ReportViewBuilder.Hex("#C0392B"); return; }

        var reg = IndicatorRegistry.Load();
        reg.Register(reg.CompileCustom(d));
        double v = reg.Resolve(d.Id)!.Evaluate(_facts);
        string status = double.IsNaN(v) ? " ⚠结果为空(检查公式引用/口径)" : " ✔可用";
        lblEval.Text = $"试算值(全矿)= {(double.IsNaN(v) ? "—" : v.ToString(d.Format))} {d.Unit}{status}";
        lblEval.Foreground = double.IsNaN(v) ? ReportViewBuilder.Hex("#B7791F") : ReportViewBuilder.Hex("#0F6E56");
    }

    private void OnSave()
    {
        if (_isBuiltin) return;
        var d = ReadEditor();
        if (string.IsNullOrWhiteSpace(d.Name)) { Warn("名称不能为空。"); return; }
        if (IndicatorRegistry.BuiltIn().Resolve(d.Id) != null) { Warn("Id 与内置指标冲突，请新建以获得新 Id。"); return; }
        if (d.Mode == IndicatorMode.Formula && !ExprEval.IsValid(d.Formula, out var err)) { Warn("公式语法错误：" + err); return; }

        IndicatorLibrary.Save(d);
        _editing = d;
        BuildList(selectId: d.Id);
        LibraryChanged?.Invoke();
        Evaluate();
        lblEval.Text = $"已保存：{d.Name}（「报表模板」页里可绑定 Id={d.Id}）";
        lblEval.Foreground = ReportViewBuilder.Hex("#0F6E56");
    }

    private CustomIndicatorDef ReadEditor()
    {
        _editing.Name = (txtName.Text ?? "").Trim();
        _editing.Unit = (txtUnit.Text ?? "").Trim();
        _editing.Format = string.IsNullOrWhiteSpace(txtFormat.Text) ? "0.##" : txtFormat.Text!.Trim();
        _editing.Mode = cboMode.SelectedIndex == 1 ? IndicatorMode.Formula : IndicatorMode.Structured;
        _editing.Field = Fields[Math.Max(0, cboField.SelectedIndex)];
        _editing.Agg = Aggs[Math.Max(0, cboAgg.SelectedIndex)];
        _editing.Filter = Filters[Math.Max(0, cboFilter.SelectedIndex)];
        _editing.Formula = (txtFormula.Text ?? "").Trim();
        _editing.Target = double.TryParse(txtTarget.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var t) ? t : null;
        _editing.HigherIsBetter = chkHigher.IsChecked == true;
        _editing.WarnBand = double.TryParse(txtWarn.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var w) ? w : 10;
        return _editing;
    }

    private void Warn(string msg) { lblEval.Text = msg; lblEval.Foreground = ReportViewBuilder.Hex("#C0392B"); }
}
