using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 「块体着色」对话框（忠实原 ColoringDialog，非模态）：着色依据 = 关闭(单色) / 高程 Z / 属性（连续色带 + 范围 + 渐变图例，
/// 或 分级区间自定义颜色；分类属性 → 逐类离散色 + 可编辑类别名）；块体边线（模式/色/宽，Kylin 视口只记录）。改动即时应用。
/// </summary>
public partial class ColoringWindow : Window
{
    private enum SourceKind { None, Z, Attr }
    private sealed class ColorSourceItem
    {
        public string Display { get; } public SourceKind Kind { get; } public string? AttrName { get; } public bool IsCategorical { get; }
        public ColorSourceItem(string d, SourceKind k, string? a, bool c) { Display = d; Kind = k; AttrName = a; IsCategorical = c; }
        public override string ToString() => Display;
    }
    private sealed class RampOption
    {
        public BlockColormapPreset Value { get; } public string Label { get; }
        public RampOption(BlockColormapPreset v, string l) { Value = v; Label = l; }
        public override string ToString() => Label;
    }
    private sealed class ClassRow { public double Min, Max; public Color Color; public ColorSwatchPicker? Picker; public TextBox? Upper; public TextBlock? Lower; }
    private sealed class CatRow { public int Code; public string Label = ""; public Color Color; public ColorSwatchPicker? Picker; public TextBox? LabelBox; }

    private readonly ModelingContext _ctx;
    private bool _loading = true;
    private readonly ColorSwatchPicker _fillPicker = new();
    private readonly ColorSwatchPicker _edgePicker = new();
    private List<ClassRow> _classRows = new();
    private List<CatRow> _catRows = new();

    public ColoringWindow() { _ctx = null!; InitializeComponent(); }

    public ColoringWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        BlockModelStore.Adopt(ctx);
        fillPickerHost.Content = _fillPicker;
        edgePickerHost.Content = _edgePicker;
        var ramps = new List<RampOption>();
        foreach (BlockColormapPreset p in Enum.GetValues(typeof(BlockColormapPreset))) ramps.Add(new RampOption(p, p.ToChineseLabel()));
        rampCombo.ItemsSource = ramps;
        rampCombo.SelectedItem = ramps.First(r => r.Value == BlockColormapPreset.RdYlBu);
        _fillPicker.SelectedColorChanged += (_, _) => { if (!_loading) Apply(); };
        _edgePicker.SelectedColorChanged += (_, _) => { if (!_loading) ApplyEdge(); };
        autoRangeCheck.IsCheckedChanged += OnAutoRangeToggled;
        continuousModeRadio.IsCheckedChanged += OnRampModeChanged;
        classifiedModeRadio.IsCheckedChanged += OnRampModeChanged;
        BlockModelStore.ActiveChanged += OnStoreChanged;
        BlockModelStore.Models.CollectionChanged += OnModelsChanged;
        Closed += (_, _) => { BlockModelStore.ActiveChanged -= OnStoreChanged; BlockModelStore.Models.CollectionChanged -= OnModelsChanged; };
        PopulateModels();
    }

    private void OnModelsChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => PopulateModels();

    private void OnStoreChanged(object? s, EventArgs e) { if (!_loading && !ReferenceEquals(Current, BlockModelStore.Active) && BlockModelStore.Active != null) { _loading = true; modelCombo.SelectedItem = BlockModelStore.Active; _loading = false; LoadModel(Current); } }

    private BlockModelMeta? Current => modelCombo.SelectedItem as BlockModelMeta;
    private ColorSourceItem? Src => sourceCombo.SelectedItem as ColorSourceItem;
    private BlockColormapPreset CurrentRamp() => (rampCombo.SelectedItem as RampOption)?.Value ?? BlockColormapPreset.RdYlBu;

    private void PopulateModels()
    {
        _loading = true;
        try
        {
            modelCombo.ItemsSource = BlockModelStore.Models.ToList();
            if (BlockModelStore.Models.Count == 0) { ShowOnly(panelEmpty); statusLabel.Text = "无块体模型"; return; }
            modelCombo.SelectedItem = BlockModelStore.PickDefault();
        }
        finally { _loading = false; }
        LoadModel(Current);
    }

    private void LoadModel(BlockModelMeta? model)
    {
        if (model == null) { ShowOnly(panelEmpty); return; }
        _loading = true;
        try
        {
            var items = new List<ColorSourceItem>
            {
                new("关闭着色（单色填充）", SourceKind.None, null, false),
                new("高程 Z（按高度，无需属性）", SourceKind.Z, null, false),
            };
            foreach (var col in model.PropertySchema)
            {
                string tag = col.IsCategorical ? " [分类]" : " [连续]";
                string unit = string.IsNullOrEmpty(col.Unit) ? "" : $"  ({col.Unit})";
                items.Add(new ColorSourceItem(col.Name + tag + unit, SourceKind.Attr, col.Name, col.IsCategorical));
            }
            sourceCombo.ItemsSource = items;
            var pick = items[0];
            if (model.IsZElevationColoring) pick = items[1];
            else if (!string.IsNullOrEmpty(model.ActiveColormapAttribute)) pick = items.FirstOrDefault(it => it.Kind == SourceKind.Attr && it.AttrName == model.ActiveColormapAttribute) ?? pick;
            sourceCombo.SelectedItem = pick;
            _fillPicker.SelectedColor = ColorSwatchPicker.ToColor(model.DisplayStyle.FillColor);
            rampCombo.SelectedItem = (rampCombo.ItemsSource as List<RampOption>)!.First(r => r.Value == model.DisplayStyle.DefaultColormap);
            edgeModeCombo.SelectedIndex = (int)model.DisplayStyle.EdgeMode;
            _edgePicker.SelectedColor = ColorSwatchPicker.ToColor(model.DisplayStyle.EdgeColor);
            edgeWidthBox.Text = model.DisplayStyle.EdgeWidthPx.ToString("0.##", CultureInfo.InvariantCulture);
            ShowModeFor(pick, model);
        }
        finally { _loading = false; }
    }

    private void ShowModeFor(ColorSourceItem src, BlockModelMeta model)
    {
        bool isZ = src.Kind == SourceKind.Z, isAttr = src.Kind == SourceKind.Attr, isCat = isAttr && src.IsCategorical, isCont = isAttr && !isCat;
        if (src.Kind == SourceKind.None) { ShowOnly(panelSingle); return; }
        if (isCat) { BuildCategoryRows(model, src.AttrName!); ShowOnly(panelCategorical); return; }
        ShowOnly(panelRamp);
        zNote.IsVisible = isZ; rangeRow.IsVisible = !isZ; modeRow.IsVisible = isCont;
        bool classified = isCont && model.DisplayStyle.ClassBreaks.TryGetValue(src.AttrName!, out var br) && br.Count > 0;
        continuousModeRadio.IsChecked = !classified; classifiedModeRadio.IsChecked = classified;
        if (!classified && !isZ)
        {
            bool auto = model.ColormapRange == null;
            autoRangeCheck.IsChecked = auto;
            var (lo, hi) = ResolveContinuousRange(model, src.AttrName!);
            minBox.Text = Fmt(lo); maxBox.Text = Fmt(hi);
            minBox.IsEnabled = maxBox.IsEnabled = !auto;
        }
        UpdateRampSubMode(src, model, classified, false);
    }

    private void UpdateRampSubMode(ColorSourceItem src, BlockModelMeta model, bool classified, bool seedIfEmpty)
    {
        continuousSub.IsVisible = !classified; classifiedSub.IsVisible = classified;
        if (classified)
        {
            if (src.AttrName != null && model.DisplayStyle.ClassBreaks.TryGetValue(src.AttrName, out var existing) && existing.Count > 0) SetClassRows(existing);
            else if (seedIfEmpty || _classRows.Count == 0) SetClassRows(model.GenerateClasses(src.AttrName!, ReadClassCount(), methodCombo.SelectedIndex == 1, CurrentRamp()));
        }
        else UpdateGradientPreview(src, model);
    }

    private void BuildCategoryRows(BlockModelMeta model, string attr)
    {
        model.DisplayStyle.CategoryColors.TryGetValue(attr, out var existing);
        _catRows = new List<CatRow>();
        catList.Children.Clear();
        foreach (var (code, label) in model.EnumerateCategories(attr))
        {
            var c = existing != null && existing.TryGetValue(code, out var cc) ? cc : BlockCategoricalPalette.ColorForCode(code);
            var row = new CatRow { Code = code, Label = label, Color = ColorSwatchPicker.ToColor(c) };
            var picker = new ColorSwatchPicker { SelectedColor = row.Color };
            picker.SelectedColorChanged += (_, col) => { row.Color = col; if (!_loading) Apply(); };
            var codeText = new TextBlock { Text = $"[{code}]", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), MinWidth = 42, Foreground = new SolidColorBrush(Color.Parse("#0D47A1")) };
            var labelBox = new TextBox { Text = label, VerticalAlignment = VerticalAlignment.Center, MinWidth = 120 };
            ToolTip.SetTip(labelBox, "类别名（如 SEAM9 / 9煤 / 岩）。留空则只显示类别码。");
            labelBox.LostFocus += (_, _) => { row.Label = labelBox.Text ?? ""; if (!_loading) Apply(); };
            row.Picker = picker; row.LabelBox = labelBox;
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), Margin = new Thickness(0, 2) };
            Grid.SetColumn(codeText, 1); Grid.SetColumn(labelBox, 2);
            g.Children.Add(picker); g.Children.Add(codeText); g.Children.Add(labelBox);
            catList.Children.Add(g);
            _catRows.Add(row);
        }
    }

    // ── 事件 ──
    private void OnModelChanged(object? s, SelectionChangedEventArgs e) { if (_loading) return; LoadModel(Current); }

    private void OnSourceChanged(object? s, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var model = Current; var src = Src;
        if (model == null || src == null) return;
        _loading = true; try { ShowModeFor(src, model); } finally { _loading = false; }
        Apply();
    }

    private void OnRampChanged(object? s, SelectionChangedEventArgs e) { if (!_loading) Apply(); }

    private void OnAutoRangeToggled(object? s, RoutedEventArgs e)
    {
        if (_loading) return;
        bool auto = autoRangeCheck.IsChecked == true;
        minBox.IsEnabled = maxBox.IsEnabled = !auto;
        var model = Current; var src = Src;
        if (!auto && model != null && src?.AttrName != null)
        {
            var (lo, hi) = ResolveContinuousRange(model, src.AttrName);
            if (string.IsNullOrWhiteSpace(minBox.Text)) minBox.Text = Fmt(lo);
            if (string.IsNullOrWhiteSpace(maxBox.Text)) maxBox.Text = Fmt(hi);
        }
        Apply();
    }

    private void OnRangeCommitted(object? s, RoutedEventArgs e) { if (_loading || autoRangeCheck.IsChecked == true) return; Apply(); }
    private void OnRangeKeyDown(object? s, KeyEventArgs e) { if (e.Key == Key.Enter) OnRangeCommitted(s, e); }

    private void OnResetCategoryColors(object? s, RoutedEventArgs e)
    {
        _loading = true;
        foreach (var r in _catRows) { r.Color = ColorSwatchPicker.ToColor(BlockCategoricalPalette.ColorForCode(r.Code)); if (r.Picker != null) r.Picker.SelectedColor = r.Color; }
        _loading = false;
        Apply();
    }

    private void OnRampModeChanged(object? s, RoutedEventArgs e)
    {
        if (_loading) return;
        var model = Current; var src = Src;
        if (model == null || src == null || src.Kind != SourceKind.Attr || src.IsCategorical) return;
        bool classified = classifiedModeRadio.IsChecked == true;
        _loading = true; try { UpdateRampSubMode(src, model, classified, true); } finally { _loading = false; }
        Apply();
    }

    private void OnGenerateClasses(object? s, RoutedEventArgs e)
    {
        if (_loading) return;
        var model = Current; var src = Src;
        if (model == null || src?.AttrName == null) return;
        SetClassRows(model.GenerateClasses(src.AttrName, ReadClassCount(), methodCombo.SelectedIndex == 1, CurrentRamp()));
        Apply();
    }

    private void OnAddClass(object? s, RoutedEventArgs e)
    {
        double lo = _classRows.Count > 0 ? _classRows[^1].Max : 0;
        var list = _classRows.Select(r => new BlockColorClass(r.Min, r.Max, ColorSwatchPicker.ToTuple(r.Color))).ToList();
        list.Add(new BlockColorClass(lo, lo + 1, BlockCategoricalPalette.ColorForCode(_classRows.Count)));
        SetClassRows(list);
        NormalizeAndApplyClasses();
    }

    private void SetClassRows(IEnumerable<BlockColorClass> source)
    {
        _classRows = new List<ClassRow>();
        classList.Children.Clear();
        foreach (var c in source)
        {
            var row = new ClassRow { Min = c.Min, Max = c.Max, Color = ColorSwatchPicker.ToColor(c.Color) };
            var picker = new ColorSwatchPicker { SelectedColor = row.Color };
            picker.SelectedColorChanged += (_, col) => { row.Color = col; if (!_loading) Apply(); };
            var lower = new TextBlock { Text = "≥ " + Fmt(c.Min), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0) };
            var lt = new TextBlock { Text = "<", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), Foreground = Brushes.Gray };
            var upper = new TextBox { Width = 74, Text = Fmt(c.Max) };
            upper.LostFocus += (_, _) => CommitUpper(row, upper);
            upper.KeyDown += (_, ke) => { if (ke.Key == Key.Enter) CommitUpper(row, upper); };
            var del = new Button { Content = "✕", Padding = new Thickness(6, 1), FontSize = 11, Margin = new Thickness(6, 0, 0, 0) };
            del.Click += (_, _) => { _classRows.Remove(row); SetClassRows(_classRows.Select(r => new BlockColorClass(r.Min, r.Max, ColorSwatchPicker.ToTuple(r.Color))).ToList()); NormalizeAndApplyClasses(); };
            row.Picker = picker; row.Upper = upper; row.Lower = lower;
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto"), Margin = new Thickness(0, 2) };
            Grid.SetColumn(lower, 1); Grid.SetColumn(lt, 2); Grid.SetColumn(upper, 3); Grid.SetColumn(del, 4);
            g.Children.Add(picker); g.Children.Add(lower); g.Children.Add(lt); g.Children.Add(upper); g.Children.Add(del);
            classList.Children.Add(g);
            _classRows.Add(row);
        }
    }

    private void CommitUpper(ClassRow row, TextBox upper)
    {
        if (_loading) return;
        if (double.TryParse(upper.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) && d != row.Max) row.Max = d;
        else upper.Text = Fmt(row.Max);
        NormalizeAndApplyClasses();
    }

    private void NormalizeAndApplyClasses()
    {
        _loading = true;
        for (int i = 1; i < _classRows.Count; i++) { _classRows[i].Min = _classRows[i - 1].Max; if (_classRows[i].Lower != null) _classRows[i].Lower!.Text = "≥ " + Fmt(_classRows[i].Min); }
        _loading = false;
        Apply();
    }

    private int ReadClassCount() => int.TryParse(classCountBox.Text, out var n) ? Math.Clamp(n, 1, 64) : 5;

    private void OnApplyClicked(object? s, RoutedEventArgs e) => Apply();
    private void OnEdgeChanged(object? s, RoutedEventArgs e) => ApplyEdge();
    private void OnCloseClicked(object? s, RoutedEventArgs e) => Close();

    private void ApplyEdge()
    {
        if (_loading || Current is not { } model) return;
        var ds = model.DisplayStyle;
        int mi = edgeModeCombo.SelectedIndex;
        if (mi >= 0 && mi <= 2) ds.EdgeMode = (BlockEdgeMode)mi;
        ds.EdgeColor = ColorSwatchPicker.ToTuple(_edgePicker.SelectedColor);
        if (double.TryParse(edgeWidthBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && w > 0) ds.EdgeWidthPx = Math.Min(w, 10.0);
        BlockModelStore.RefreshDisplay(_ctx, model);
        statusLabel.Text = "边线样式已记录（Kylin 平面视口按块轮廓线显示）";
    }

    private void Apply()
    {
        if (_loading) return;
        var model = Current; var src = Src;
        if (model == null || src == null) return;
        try
        {
            switch (src.Kind)
            {
                case SourceKind.None:
                    model.ActiveColormapAttribute = null;
                    model.DisplayStyle.FillColor = ColorSwatchPicker.ToTuple(_fillPicker.SelectedColor);
                    statusLabel.Text = "单色填充";
                    break;
                case SourceKind.Z:
                    model.DisplayStyle.DefaultColormap = CurrentRamp();
                    model.ColormapRange = null;
                    model.ActiveColormapAttribute = BlockModelMeta.ZElevationSentinel;
                    UpdateGradientPreview(src, model);
                    statusLabel.Text = $"高程 Z · {CurrentRamp().ToChineseLabel().Split(' ')[0]}";
                    break;
                case SourceKind.Attr when src.IsCategorical:
                    WriteCategoryColors(model, src.AttrName!);
                    WriteCategoryLabels(model, src.AttrName!);
                    model.ActiveColormapAttribute = src.AttrName;
                    statusLabel.Text = $"分类着色 · {src.AttrName}";
                    break;
                case SourceKind.Attr:
                    model.DisplayStyle.DefaultColormap = CurrentRamp();
                    model.ActiveColormapAttribute = src.AttrName;
                    if (classifiedModeRadio.IsChecked == true)
                    {
                        if (_classRows.Count == 0) model.DisplayStyle.ClassBreaks.Remove(src.AttrName!);
                        else model.DisplayStyle.ClassBreaks[src.AttrName!] = _classRows.Select(r => new BlockColorClass(r.Min, r.Max, ColorSwatchPicker.ToTuple(r.Color))).ToList();
                        statusLabel.Text = $"{src.AttrName} · 分级 {_classRows.Count} 级";
                    }
                    else
                    {
                        model.DisplayStyle.ClassBreaks.Remove(src.AttrName!);
                        model.ColormapRange = ReadManualRange();
                        UpdateGradientPreview(src, model);
                        statusLabel.Text = $"{src.AttrName} · {CurrentRamp().ToChineseLabel().Split(' ')[0]}";
                    }
                    break;
            }
            BlockModelStore.RefreshDisplay(_ctx, model);
        }
        catch (Exception ex) { statusLabel.Text = "应用块体着色失败：" + ex.Message; }
    }

    private void WriteCategoryColors(BlockModelMeta model, string attr)
    {
        var map = model.DisplayStyle.EnsureCategoryColors(attr);
        map.Clear();
        foreach (var r in _catRows) map[r.Code] = ColorSwatchPicker.ToTuple(r.Color);
    }

    private void WriteCategoryLabels(BlockModelMeta model, string attr)
    {
        if (_catRows.Count == 0) return;
        var col = model.FindColumn(attr);
        if (col == null) return;
        bool anyNamed = _catRows.Any(r => !string.IsNullOrWhiteSpace(r.Label));
        int maxCode = _catRows.Max(r => r.Code);
        if (!anyNamed || maxCode < 0) return;
        var labels = new List<string>(maxCode + 1);
        for (int i = 0; i <= maxCode; i++) labels.Add("");
        foreach (var r in _catRows) if (r.Code >= 0) labels[r.Code] = r.Label.Trim();
        col.CategoryLabels = labels;
    }

    private (double Min, double Max)? ReadManualRange()
    {
        if (autoRangeCheck.IsChecked == true) return null;
        if (double.TryParse(minBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double lo) && double.TryParse(maxBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double hi) && hi > lo) return (lo, hi);
        return null;
    }

    private void UpdateGradientPreview(ColorSourceItem src, BlockModelMeta model)
    {
        var preset = CurrentRamp();
        var brush = new LinearGradientBrush { StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative) };
        const int stops = 16;
        for (int i = 0; i <= stops; i++) { double t = (double)i / stops; brush.GradientStops.Add(new GradientStop(ColorSwatchPicker.ToColor(BlockColormap.Sample(preset, t)), t)); }
        gradientRect.Background = brush;
        double lo, hi;
        if (src.Kind == SourceKind.Z) { var b = model.Bounds; lo = b.minZ; hi = b.maxZ; }
        else (lo, hi) = ResolveContinuousRange(model, src.AttrName!);
        minLabel.Text = Fmt(lo); midLabel.Text = Fmt((lo + hi) * 0.5); maxLabel.Text = Fmt(hi);
    }

    private (double Min, double Max) ResolveContinuousRange(BlockModelMeta model, string attr)
    {
        var manual = ReadManualRange();
        if (manual is { } m) return (m.Min, m.Max);
        if (model.GetAttributeRange(attr) is { } r && r.Max > r.Min) return r;
        return (0, 1);
    }

    private void ShowOnly(Control panel)
    {
        panelSingle.IsVisible = panelRamp.IsVisible = panelCategorical.IsVisible = panelEmpty.IsVisible = false;
        panel.IsVisible = true;
    }

    private static string Fmt(double v)
    {
        double a = Math.Abs(v);
        if (a != 0 && (a < 0.01 || a >= 1e6)) return v.ToString("0.###E+0", CultureInfo.InvariantCulture);
        return v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
