using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Selection;

/// <summary>
/// 快速选择（忠实原 <c>PitMineApp.Selection.QuickSelectDialog</c>，对标 AutoCAD QSELECT）。
///
/// 在此之前，Kylin 只有命令行一条路：<c>快速选择 圆 半径 &gt; 5</c>。条件本身是全的
/// （`QuickSelectCatalog`/`QuickSelectFilter` 早就照原版移过来了），但**要用得先知道有哪些类型、
/// 哪些特性叫什么、这张图里有哪几个图层名** —— 全靠记，打错一个字就是空集，还看不出错在哪。
/// 原版就是为这个才做的对话框：几个联动下拉把目录铺开，用户只在候选里挑。
///
/// 与原版口径一致处：
///   · <b>类型下拉只列范围里真有的类型</b>（原 `PresentTypeIds` 的用意）；
///   · <b>换特性必须丢掉旧值</b>（原注释原话：把"台阶线"留在刚切过去的"半径"框里，
///     条件会静静地一条都不中），换运算符则保留；
///   · <b>运算符=全部选择时「值」禁用</b>，免得让人以为填了有用；
///   · <b>「预览」只算不选</b> —— 回写选择集是有副作用的，先让人看见数再决定。
///
/// 登记的差异：原版状态行还报"本条件需逐条查询 N 个对象的详细属性…可能要等若干秒"，那是
/// C++ 引擎按 handle 逐条拉 JSON 的代价；Kylin 的快照是在进程内由 `SceneEntity` 直接造的
/// （`QuickSelectSnapshot.From` 一次填全），没有这趟往返，故不报这句假警。
/// </summary>
internal sealed class QuickSelectWindow : Window
{
    private sealed record TypeItem(int? TypeId, string Label) { public override string ToString() => Label; }
    private sealed record OperatorItem(QuickSelectOperator Op, string Label) { public override string ToString() => Label; }
    private sealed record PropItem(QuickSelectProperty Prop) { public override string ToString() => Prop.DisplayName; }

    private readonly Func<QuickSelectScope, List<EntitySnapshot>> _snapshotsFor;
    /// <summary>确定：按条件跑一次并落到选择集，返回要显示的结果串。</summary>
    private readonly Func<QuickSelectCriteria, string> _apply;
    /// <summary>预览：只算命中数，不碰选择集。</summary>
    private readonly Func<QuickSelectCriteria, int> _preview;

    private readonly ComboBox _cmbScope = new() { Width = 300 };
    private readonly ComboBox _cmbType = new() { Width = 300 };
    private readonly ComboBox _cmbProperty = new() { Width = 300 };
    private readonly ComboBox _cmbOperator = new() { Width = 300 };
    // 值这一栏原版是「可编辑 ComboBox」(能从候选里挑, 也能自己打)。Avalonia 的 ComboBox 不可编辑,
    // 对应控件是 AutoCompleteBox: 有 Text 又能挂候选。MinimumPrefixLength=0 让它一聚焦就把候选摊开。
    private readonly AutoCompleteBox _cmbValue = new()
    {
        Width = 300, MinimumPrefixLength = 0, IsTextCompletionEnabled = false,
        FilterMode = AutoCompleteFilterMode.Contains,
    };
    private readonly RadioButton _rbInclude = new() { Content = "包括在新选择集中", IsChecked = true, GroupName = "qsApply" };
    private readonly RadioButton _rbExclude = new() { Content = "排除在新选择集之外", GroupName = "qsApply" };
    private readonly CheckBox _chkAppend = new() { Content = "附加到当前选择集" };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 48, Foreground = Brush.Parse("#666") };

    private List<EntitySnapshot> _snapshots = new();
    private bool _suppress;   // 联动重建下拉时抑制回调，免得清空 Items 触发一串 SelectionChanged 打断装配

    public QuickSelectWindow(Func<QuickSelectScope, List<EntitySnapshot>> snapshotsFor,
                             Func<QuickSelectCriteria, string> apply,
                             Func<QuickSelectCriteria, int> preview)
    {
        _snapshotsFor = snapshotsFor; _apply = apply; _preview = preview;

        Title = "快速选择";
        Width = 520; Height = 470;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid
        {
            Margin = new Thickness(16),
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,*,Auto"),
        };
        void Row(int r, string label, Control c)
        {
            var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 8), MinWidth = 76 };
            Grid.SetRow(t, r); Grid.SetColumn(t, 0); grid.Children.Add(t);
            c.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(c, r); Grid.SetColumn(c, 1); grid.Children.Add(c);
        }
        Row(0, "应用到:", _cmbScope);
        Row(1, "对象类型:", _cmbType);
        Row(2, "特性:", _cmbProperty);
        Row(3, "运算符:", _cmbOperator);
        Row(4, "值:", _cmbValue);

        var applyPanel = new StackPanel
        {
            Spacing = 2, Margin = new Thickness(0, 4, 0, 8),
            Children =
            {
                new TextBlock { Text = "如何应用", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 2) },
                _rbInclude, _rbExclude,
            },
        };
        Grid.SetRow(applyPanel, 5); Grid.SetColumn(applyPanel, 0); Grid.SetColumnSpan(applyPanel, 2);
        grid.Children.Add(applyPanel);

        _chkAppend.Margin = new Thickness(0, 0, 0, 8);
        Grid.SetRow(_chkAppend, 6); Grid.SetColumn(_chkAppend, 0); Grid.SetColumnSpan(_chkAppend, 2);
        grid.Children.Add(_chkAppend);

        Grid.SetRow(_status, 7); Grid.SetColumn(_status, 0); Grid.SetColumnSpan(_status, 2);
        grid.Children.Add(_status);

        var btnPreview = new Button { Content = "预览", Padding = new Thickness(16, 4), Margin = new Thickness(0, 0, 8, 0) };
        ToolTip.SetTip(btnPreview, "只算不选：先看看这条条件会命中多少个对象");
        var btnOk = new Button { Content = "确定", Padding = new Thickness(16, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var btnCancel = new Button { Content = "取消", Padding = new Thickness(16, 4), IsCancel = true };
        btnPreview.Click += (_, _) => OnPreview();
        btnOk.Click += (_, _) => OnOk();
        btnCancel.Click += (_, _) => Close();
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { btnPreview, btnOk, btnCancel },
        };
        Grid.SetRow(btns, 8); Grid.SetColumn(btns, 0); Grid.SetColumnSpan(btns, 2);
        grid.Children.Add(btns);

        Content = grid;

        _cmbScope.SelectionChanged += (_, _) => { if (!_suppress) ReloadScope(); };
        _cmbType.SelectionChanged += (_, _) => { if (!_suppress) RebuildPropertyItems(); };
        _cmbProperty.SelectionChanged += (_, _) => { if (!_suppress) RebuildOperatorItems(); };
        _cmbOperator.SelectionChanged += (_, _) => { if (!_suppress) RebuildValueItems(); };

        _suppress = true;
        _cmbScope.ItemsSource = new[] { "整个图形", "当前选择集" };
        _cmbScope.SelectedIndex = 0;
        _suppress = false;
        ReloadScope();
    }

    private QuickSelectScope CurrentScope =>
        _cmbScope.SelectedIndex == 1 ? QuickSelectScope.CurrentSelection : QuickSelectScope.WholeDrawing;

    private void ReloadScope()
    {
        _snapshots = _snapshotsFor(CurrentScope);
        RebuildTypeItems();
    }

    private void RebuildTypeItems()
    {
        _suppress = true;
        try
        {
            var items = new List<TypeItem> { new(null, "所有图元") };
            foreach (int id in QuickSelectSnapshot.PresentTypeIds(_snapshots))
                items.Add(new TypeItem(id, QuickSelectCatalog.TypeName(id)));
            _cmbType.ItemsSource = items;
            _cmbType.SelectedIndex = 0;
        }
        finally { _suppress = false; }
        RebuildPropertyItems();
    }

    private int? SelectedTypeId => (_cmbType.SelectedItem as TypeItem)?.TypeId;

    private void RebuildPropertyItems()
    {
        _suppress = true;
        try
        {
            _cmbProperty.ItemsSource = QuickSelectCatalog.PropertiesFor(SelectedTypeId).Select(p => new PropItem(p)).ToList();
            _cmbProperty.SelectedIndex = 0;
        }
        finally { _suppress = false; }
        RebuildOperatorItems();
    }

    private QuickSelectProperty? SelectedProperty => (_cmbProperty.SelectedItem as PropItem)?.Prop;

    private void RebuildOperatorItems()
    {
        _suppress = true;
        try
        {
            var kind = SelectedProperty?.Kind ?? QuickSelectValueKind.Text;
            _cmbOperator.ItemsSource = QuickSelectCatalog.OperatorsFor(kind)
                .Select(op => new OperatorItem(op, QuickSelectCatalog.OperatorName(op))).ToList();
            _cmbOperator.SelectedIndex = 0;
        }
        finally { _suppress = false; }
        // 换了特性就丢掉旧值：把"台阶线"留在刚切过去的"半径"框里，条件会静静地一条都不中
        RebuildValueItems(keepValue: false);
    }

    private QuickSelectOperator SelectedOperator =>
        (_cmbOperator.SelectedItem as OperatorItem)?.Op ?? QuickSelectOperator.Equals;

    private void RebuildValueItems(bool keepValue = true)
    {
        _suppress = true;
        try
        {
            string previous = keepValue ? (_cmbValue.Text ?? "") : "";
            var items = new List<string>();
            var prop = SelectedProperty;
            if (prop is not null)
            {
                if (prop.Key == "layer") items.AddRange(QuickSelectSnapshot.PresentLayerNames(_snapshots));
                else if (prop.Kind == QuickSelectValueKind.Boolean) { items.Add("true"); items.Add("false"); }
            }
            _cmbValue.ItemsSource = items;

            // 运算符=全部选择时值这一维不参与，禁用以免让人以为填了有用
            bool valueUsed = SelectedOperator != QuickSelectOperator.All;
            _cmbValue.IsEnabled = valueUsed;
            _cmbValue.Text = valueUsed ? previous : "";
            if (valueUsed && string.IsNullOrEmpty(_cmbValue.Text) && items.Count > 0) _cmbValue.Text = items[0];
        }
        finally { _suppress = false; }
        UpdateStatus();
    }

    internal QuickSelectCriteria BuildCriteria() => new()
    {
        Scope = CurrentScope,
        TypeId = SelectedTypeId,
        PropertyKey = SelectedProperty?.Key,
        Operator = SelectedOperator,
        Value = _cmbValue.Text ?? "",
        ApplyMode = _rbExclude.IsChecked == true ? QuickSelectApplyMode.Exclude : QuickSelectApplyMode.Include,
        AppendToCurrentSelection = _chkAppend.IsChecked == true,
    };

    private void UpdateStatus(string? resultLine = null)
    {
        string text = $"范围内 {_snapshots.Count} 个对象。";
        if (!string.IsNullOrEmpty(resultLine)) text += "\n" + resultLine;
        _status.Text = text;
    }

    private void OnPreview()
    {
        int n = _preview(BuildCriteria());
        UpdateStatus($"预览：将选中 {n} 个对象" + (n == 0 ? "（条件没有命中任何对象）" : "。"));
    }

    private void OnOk()
    {
        string msg = _apply(BuildCriteria());
        Close();
        _ = msg;   // 结果由调用方落到状态栏/信息栏(与命令行那条同一句)
    }

    // ── 自检钩子用（脚本点不了下拉，直设选中项再走与窗体完全相同的路径）──
    internal bool SelectScope(int idx) { if (idx < 0 || idx > 1) return false; _cmbScope.SelectedIndex = idx; return true; }
    internal bool SelectType(string label)
    {
        var items = _cmbType.ItemsSource as IEnumerable<TypeItem>;
        var hit = items?.FirstOrDefault(x => x.Label == label);
        if (hit == null) return false;
        _cmbType.SelectedItem = hit; return true;
    }
    internal bool SelectProperty(string displayName)
    {
        var items = _cmbProperty.ItemsSource as IEnumerable<PropItem>;
        var hit = items?.FirstOrDefault(x => x.Prop.DisplayName == displayName || x.Prop.Key == displayName);
        if (hit == null) return false;
        _cmbProperty.SelectedItem = hit; return true;
    }
    internal bool SelectOperator(string label)
    {
        var items = _cmbOperator.ItemsSource as IEnumerable<OperatorItem>;
        var hit = items?.FirstOrDefault(x => x.Label == label);
        if (hit == null) return false;
        _cmbOperator.SelectedItem = hit; return true;
    }
    internal void SetValue(string v) => _cmbValue.Text = v;
    internal void SetExclude(bool on) { _rbExclude.IsChecked = on; _rbInclude.IsChecked = !on; }
    internal void SetAppend(bool on) => _chkAppend.IsChecked = on;
    internal string StatusText => _status.Text ?? "";
    internal void DoPreview() => OnPreview();
    internal void DoOk() => OnOk();
}
