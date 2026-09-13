// 忠实移植自原 PitMine3D Modules/TaskLib/Reporting/ReportTemplateView.xaml(.cs)（逐行对应；XAML → Avalonia 代码布局）
// 差异仅：MessageBox → CoalMsgBox（async）；WPF dgCols.Items.Refresh() → 行对象实现 INotifyPropertyChanged。
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using PitMine3D.Kylin.TaskLib.Reporting;

namespace PitMine3D.Kylin.Views.TaskLib;

/// <summary>
/// 报表模板设计器 —— 「定制化」页（<see cref="ReportHubWindow"/> 的第二页）。
/// 克隆内置或新建 → 改标题/层级/展开维/列（每列绑指标）→ 保存成模板，供「报表中心」页一键生成。
/// P1 为表单式设计器（可视化拖拽留 P2）。模板的**语义**一行没动（<see cref="BuildDefinition"/> 与原窗逐字段一致）。
/// </summary>
public sealed class ReportTemplateView : UserControl
{
    /// <summary>列编辑行（DataGrid 绑定）。Content 为只读展示（指标名或维度键）。</summary>
    public sealed class ColumnRow : INotifyPropertyChanged
    {
        private string _header = "", _content = ""; private bool _total; private double _width = 90;
        public string Header { get => _header; set { _header = value; Raise(nameof(Header)); } }
        public bool IsIndicator { get; set; } = true;
        public string IndicatorId { get; set; } = "";
        public string Content { get => _content; set { _content = value; Raise(nameof(Content)); } }
        public bool Total { get => _total; set { _total = value; Raise(nameof(Total)); } }
        public double Width { get => _width; set { _width = value; Raise(nameof(Width)); } }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private IndicatorRegistry _indicators = IndicatorRegistry.Load();   // 内置 + 自定义
    private readonly ObservableCollection<ColumnRow> _cols = new();
    private List<ReportDefinition> _templates = new();
    private ReportDefinition _editing = new();

    /// <summary>页签切换会重放 Loaded，初始化只许跑一次（否则切回来就把编辑中的模板冲掉）。</summary>
    private bool _initialized;

    /// <summary>「预览」：把这份未保存的模板交给报表中心页当场生成。</summary>
    public event Action<ReportDefinition>? PreviewRequested;

    /// <summary>模板库有增删改（保存/删除）：报表中心页的模板下拉要跟着刷新。</summary>
    public event Action? TemplatesChanged;

    /// <summary>「指标库…」：切到指标 / 计算规则页。</summary>
    public event Action? IndicatorLibraryRequested;

    /// <summary>与 cboDim 的选项顺序严格对应（新增去向/物料两维时两边必须同步改）。</summary>
    private static readonly GroupDim[] DimByIndex =
    {
        GroupDim.Panel, GroupDim.Equipment, GroupDim.Process, GroupDim.Shift,
        GroupDim.Destination, GroupDim.Material,
    };

    private readonly ListBox tplList = new();
    private readonly TextBlock editorHead;
    private readonly TextBox txtName = new() { Margin = new Thickness(0, 0, 16, 6) };
    private readonly ComboBox cboLevel = new() { Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox txtTitle = new() { Margin = new Thickness(0, 0, 16, 6) };
    private readonly ComboBox cboDim = new() { Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly CheckBox chkTotal = new() { Content = "显示合计行", IsChecked = true, Margin = new Thickness(0, 2, 0, 0) };
    private readonly DataGrid dgCols = TaskUi.Grid(readOnly: false, single: true);
    private readonly ComboBox cboIndicator = new() { MinWidth = 150, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock statusText = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    public ReportTemplateView()
    {
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("286,*") };

        // 模板库
        var lib = new DockPanel();
        var libBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        libBtns.Children.Add(Btn("克隆", OnClone, 60, 6)); libBtns.Children.Add(Btn("新建", OnNew, 60, 6)); libBtns.Children.Add(Btn("删除", OnDelete, 60, 0));
        DockPanel.SetDock(libBtns, Avalonia.Controls.Dock.Bottom); lib.Children.Add(libBtns);
        tplList.ItemTemplate = new FuncDataTemplate<ReportDefinition>((_, _) =>
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var n = new TextBlock(); n.Bind(TextBlock.TextProperty, new Binding(nameof(ReportDefinition.Name))); sp.Children.Add(n);
            var b = new TextBlock { Text = "  [内置]", Foreground = ReportViewBuilder.Hex("#9AA5B1"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            b.Bind(IsVisibleProperty, new Binding(nameof(ReportDefinition.BuiltIn))); sp.Children.Add(b);
            return sp;
        });
        CompactList(tplList);
        tplList.SelectionChanged += (_, _) => OnSelectTemplate();
        lib.Children.Add(tplList);
        var libBox = TaskUi.GroupBox("模板库（内置只读 · 克隆后可改）", lib, new Thickness(0, 0, 8, 0), 6);
        Grid.SetColumn(libBox, 0); root.Children.Add(libBox);

        // 编辑器：保存/预览钉在底部
        var ed = new DockPanel();
        var footGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") }; footGrid.ColumnDefinitions[1].MinWidth = 12;
        var footBtns = new StackPanel { Orientation = Orientation.Horizontal };
        var save = TaskUi.Btn("保存模板", OnSave, 90, bold: true); save.Margin = new Thickness(0, 0, 8, 0); save.Padding = new Thickness(8, 3);
        save.Background = ReportViewBuilder.Hex("#1D9E75"); save.Foreground = Brushes.White; save.BorderThickness = new Thickness(0);
        footBtns.Children.Add(save);
        var prev = TaskUi.Btn("预览", OnPreview, 80); prev.Margin = new Thickness(0, 0, 16, 0);
        ToolTip.SetTip(prev, "拿这份（未保存的）模板到「报表中心」页当场生成一份看看");
        footBtns.Children.Add(prev);
        Grid.SetColumn(footBtns, 0); footGrid.Children.Add(footBtns);
        TaskUi.Theme(statusText, TextBlock.ForegroundProperty, "Theme.Text.Muted");
        Grid.SetColumn(statusText, 1); footGrid.Children.Add(statusText);
        var foot = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Margin = new Thickness(0, 10, 0, 0), Child = footGrid };
        TaskUi.Theme(foot, Border.BorderBrushProperty, "Theme.Surface.Border");
        DockPanel.SetDock(foot, Avalonia.Controls.Dock.Bottom); ed.Children.Add(foot);

        var form = new StackPanel();
        var fg = new Grid { Margin = new Thickness(0, 0, 0, 8), ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,160"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };
        Cell(fg, 0, 0, FLbl("模板名称")); Cell(fg, 0, 1, txtName); Cell(fg, 0, 2, FLbl("层级"));
        foreach (var s in new[] { "公司", "矿", "采区", "班组" }) cboLevel.Items.Add(new ComboBoxItem { Content = s });
        cboLevel.SelectedIndex = 1; Cell(fg, 0, 3, cboLevel);
        Cell(fg, 1, 0, FLbl("报表标题")); Cell(fg, 1, 1, txtTitle); Cell(fg, 1, 2, FLbl("明细展开维"));
        foreach (var s in new[] { "作业面", "设备", "工序", "班次", "去向", "物料" }) cboDim.Items.Add(new ComboBoxItem { Content = s });
        cboDim.SelectedIndex = 0; Cell(fg, 1, 3, cboDim);
        Cell(fg, 2, 1, chkTotal);
        form.Children.Add(fg);

        var colHead = new TextBlock { Text = "列定义（表头 / 内容 / 是否进合计 / 宽度）", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 4) };
        TaskUi.Theme(colHead, TextBlock.ForegroundProperty, "Theme.Text.Body");
        form.Children.Add(colHead);
        dgCols.Height = 240; dgCols.Margin = new Thickness(0);
        dgCols.Columns.Add(new DataGridTextColumn { Header = TaskUi.Head("表头"), Binding = new Binding(nameof(ColumnRow.Header)) { Mode = BindingMode.TwoWay }, Width = new DataGridLength(120) });
        dgCols.Columns.Add(new DataGridTextColumn { Header = TaskUi.Head("内容（绑定）"), Binding = new Binding(nameof(ColumnRow.Content)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        dgCols.Columns.Add(new DataGridCheckBoxColumn { Header = TaskUi.Head("合计"), Binding = new Binding(nameof(ColumnRow.Total)) { Mode = BindingMode.TwoWay }, Width = new DataGridLength(52) });
        dgCols.Columns.Add(new DataGridTextColumn { Header = TaskUi.Head("宽"), Binding = new Binding(nameof(ColumnRow.Width)) { Mode = BindingMode.TwoWay }, Width = new DataGridLength(56) });
        form.Children.Add(dgCols);

        // 列操作：绑定一组、增删一组、排序一组，WrapPanel 让它在窄窗下换行
        var ops = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var g1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 6) };
        cboIndicator.DisplayMemberBinding = new Binding(nameof(IndicatorDef.Name));
        g1.Children.Add(cboIndicator);
        var il = Btn("指标库…", () => IndicatorLibraryRequested?.Invoke(), 66, 10);
        ToolTip.SetTip(il, "切到「指标 / 计算规则」页新增或改指标，改完这里的下拉自动刷新");
        g1.Children.Add(il);
        g1.Children.Add(Btn("设为该指标", OnBindIndicator, 82, 6)); g1.Children.Add(Btn("设为维度键", OnBindDimension, 82, 0));
        ops.Children.Add(g1);
        var g2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 6) };
        g2.Children.Add(Btn("＋列", OnAddColumn, 52, 6)); g2.Children.Add(Btn("－列", OnRemoveColumn, 52, 0));
        ops.Children.Add(g2);
        var g3 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        g3.Children.Add(Btn("上移", () => Move(-1), 52, 6)); g3.Children.Add(Btn("下移", () => Move(1), 52, 0));
        ops.Children.Add(g3);
        form.Children.Add(ops);
        ed.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var edBox = TaskUi.GroupBox("模板设计", ed, new Thickness(0), 10);
        editorHead = (TextBlock)((DockPanel)edBox.Child!).Children[0];
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

    private static void Cell(Grid g, int r, int c, Control ctl) { Grid.SetRow(ctl, r); Grid.SetColumn(ctl, c); g.Children.Add(ctl); }

    private static Button Btn(string text, Action onClick, double minWidth, double right)
    {
        var b = TaskUi.Btn(text, onClick, minWidth); b.Margin = new Thickness(0, 0, right, 0);
        return b;
    }

    /// <summary>原 WPF ListBox 行是紧凑的；Fluent 的 ListBoxItem 默认 32px 高，收回去。</summary>
    internal static void CompactList(ListBox lb)
    {
        var st = new Style(x => x.OfType<ListBox>().Descendant().OfType<ListBoxItem>());
        st.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 3)));
        st.Setters.Add(new Setter(ListBoxItem.MinHeightProperty, 0.0));
        lb.Styles.Add(st);
    }

    private void OnLoaded()
    {
        if (_initialized) return;
        _initialized = true;

        cboIndicator.ItemsSource = _indicators.All();
        cboIndicator.SelectedIndex = 0;
        dgCols.ItemsSource = _cols;
        ReloadList(selectFirst: true);
    }

    private void ReloadList(bool selectFirst = false, string? selectId = null)
    {
        _templates = ReportLibrary.LoadAll();
        tplList.ItemsSource = _templates;
        if (selectId != null)
            tplList.SelectedItem = _templates.FirstOrDefault(t => t.Id == selectId);
        else if (selectFirst && _templates.Count > 0)
            tplList.SelectedIndex = 0;
    }

    private void OnSelectTemplate()
    {
        if (tplList.SelectedItem is ReportDefinition def)
            LoadEditor(def.Clone(def.Name)); // 编辑副本，避免直接改库内对象；保存时决定新建/覆盖
    }

    // ── 库操作 ──
    private void OnClone()
    {
        if (tplList.SelectedItem is not ReportDefinition def) return;
        LoadEditor(def.Clone(def.Name + "（副本）"));
        statusText.Text = "已克隆到编辑区，改完点「保存模板」。";
    }

    private void OnNew()
    {
        var def = new ReportDefinition
        {
            Name = "自定义报表",
            Title = "自定义生产报表",
            DetailGroup = GroupDim.Panel,
            Columns =
            {
                new ReportColumn { Header = "作业面", Width = 120, Align = CellAlign.Left, Bind = ColBind.Dimension },
                new ReportColumn { Header = "计划m³", Width = 90, Bind = ColBind.Indicator, IndicatorId = "plan_vol", Total = true },
                new ReportColumn { Header = "实绩m³", Width = 90, Bind = ColBind.Indicator, IndicatorId = "actual_vol", Total = true },
                new ReportColumn { Header = "达成%", Width = 70, Bind = ColBind.Indicator, IndicatorId = "attain", Total = true },
            },
        };
        LoadEditor(def);
        statusText.Text = "已新建空白模板，改完点「保存模板」。";
    }

    private async void OnDelete()
    {
        if (tplList.SelectedItem is not ReportDefinition def) return;
        if (def.BuiltIn || !ReportLibrary.IsCustom(def.Id)) { statusText.Text = "内置模板不可删除（可克隆后改）。"; return; }
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner != null && !await TaskUi.Confirm(owner, "确认", $"删除模板「{def.Name}」？")) return;
        ReportLibrary.Delete(def.Id);
        ReloadList(selectFirst: true);
        TemplatesChanged?.Invoke();
        statusText.Text = "已删除。";
    }

    // ── 编辑区 ──
    private void LoadEditor(ReportDefinition def)
    {
        _editing = def;
        txtName.Text = def.Name;
        txtTitle.Text = def.Title;
        SelectComboByContent(cboLevel, def.Level);
        cboDim.SelectedIndex = Math.Max(0, Array.IndexOf(DimByIndex, def.DetailGroup));
        chkTotal.IsChecked = def.ShowTotalRow;

        _cols.Clear();
        foreach (var c in def.Columns)
        {
            _cols.Add(new ColumnRow
            {
                Header = c.Header,
                IsIndicator = c.Bind == ColBind.Indicator,
                IndicatorId = c.IndicatorId,
                Content = c.Bind == ColBind.Indicator ? IndicatorName(c.IndicatorId) : "（维度键）",
                Total = c.Total,
                Width = c.Width,
            });
        }
        editorHead.Text = def.BuiltIn ? $"模板设计（源：内置「{def.Name}」，保存将另存为自定义）" : "模板设计";
    }

    private void OnBindIndicator()
    {
        if (dgCols.SelectedItem is not ColumnRow row) { statusText.Text = "请先在列表选中一列。"; return; }
        if (cboIndicator.SelectedItem is not IndicatorDef ind) return;
        row.IsIndicator = true;
        row.IndicatorId = ind.Id;
        row.Content = ind.Name;
    }

    private void OnBindDimension()
    {
        if (dgCols.SelectedItem is not ColumnRow row) { statusText.Text = "请先在列表选中一列。"; return; }
        row.IsIndicator = false;
        row.IndicatorId = "";
        row.Content = "（维度键）";
    }

    /// <summary>指标库变更后刷新下拉（新增自定义指标即可在此绑定）。由宿主在指标页保存/删除后调。</summary>
    public void RefreshIndicators()
    {
        string? prev = (cboIndicator.SelectedItem as IndicatorDef)?.Id;
        _indicators = IndicatorRegistry.Load();
        var list = _indicators.All();
        cboIndicator.ItemsSource = list;
        cboIndicator.SelectedItem = list.FirstOrDefault(i => i.Id == prev) ?? list.FirstOrDefault();
    }

    private void OnAddColumn()
    {
        var ind = cboIndicator.SelectedItem as IndicatorDef ?? _indicators.All().FirstOrDefault();
        _cols.Add(new ColumnRow { Header = ind?.Name ?? "新列", IsIndicator = true, IndicatorId = ind?.Id ?? "", Content = ind?.Name ?? "", Total = true, Width = 90 });
        dgCols.SelectedIndex = _cols.Count - 1;
    }

    private void OnRemoveColumn()
    {
        if (dgCols.SelectedItem is ColumnRow row) _cols.Remove(row);
    }

    private void Move(int delta)
    {
        int i = dgCols.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= _cols.Count) return;
        _cols.Move(i, j);
        dgCols.SelectedIndex = j;
    }

    // ── 保存 / 预览 ──
    private void OnSave()
    {
        dgCols.CommitEdit(DataGridEditingUnit.Cell, true); dgCols.CommitEdit(DataGridEditingUnit.Row, true);
        var def = BuildDefinition();
        if (def.Columns.Count == 0) { statusText.Text = "至少要有一列。"; return; }
        if (_editing.BuiltIn || !ReportLibrary.IsCustom(_editing.Id))
            def.Id = Guid.NewGuid().ToString("N");   // 从内置派生 → 另存为新自定义
        else
            def.Id = _editing.Id;                    // 覆盖同一自定义

        ReportLibrary.Save(def);
        _editing = def;
        ReloadList(selectId: def.Id);
        TemplatesChanged?.Invoke();                  // 报表中心页的模板下拉当场刷新（原来要关窗重开）
        statusText.Text = $"已保存：{def.Name}（「报表中心」页已可一键生成）";
    }

    private void OnPreview()
    {
        dgCols.CommitEdit(DataGridEditingUnit.Cell, true); dgCols.CommitEdit(DataGridEditingUnit.Row, true);
        var def = BuildDefinition();
        if (def.Columns.Count == 0) { statusText.Text = "至少要有一列才能预览。"; return; }
        PreviewRequested?.Invoke(def);
        statusText.Text = "已在「报表中心」页预览这份未保存的模板。";
    }

    /// <summary>把编辑区收成一份 ReportDefinition。</summary>
    private ReportDefinition BuildDefinition()
    {
        var def = new ReportDefinition
        {
            Id = _editing.Id,
            Name = string.IsNullOrWhiteSpace(txtName.Text) ? "自定义报表" : txtName.Text!.Trim(),
            Title = string.IsNullOrWhiteSpace(txtTitle.Text) ? "生产报表" : txtTitle.Text!.Trim(),
            Level = (cboLevel.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "矿",
            DetailGroup = DimByIndex[Math.Max(0, cboDim.SelectedIndex)],
            ShowTotalRow = chkTotal.IsChecked == true,
            BuiltIn = false,
            Kind = _editing.Kind,                          // 报表/报告/矩阵种类沿用源模板
            Charts = _editing.Charts,                      // 图表带 P1 沿用源模板（设计器暂不改图）
            Matrix = _editing.Matrix,                      // 交叉表带沿用源模板
            Footnotes = _editing.Footnotes,                // 口径脚注沿用源模板
            ShowConclusion = _editing.ShowConclusion,
            NarrativeSections = _editing.NarrativeSections, // 报告叙述段沿用
            SignatureLine = _editing.SignatureLine,
        };
        foreach (var r in _cols)
        {
            def.Columns.Add(new ReportColumn
            {
                Header = r.Header,
                Width = r.Width <= 0 ? 90 : r.Width,
                Align = r.IsIndicator ? CellAlign.Right : CellAlign.Left,
                Bind = r.IsIndicator ? ColBind.Indicator : ColBind.Dimension,
                IndicatorId = r.IsIndicator ? r.IndicatorId : "",
                Total = r.IsIndicator && r.Total,
            });
        }
        return def;
    }

    private string IndicatorName(string id) => _indicators.Resolve(id)?.Name ?? id;

    private static void SelectComboByContent(ComboBox cbo, string content)
    {
        foreach (var item in cbo.Items)
            if (item is ComboBoxItem ci && (ci.Content?.ToString() == content))
            { cbo.SelectedItem = ci; return; }
    }
}
