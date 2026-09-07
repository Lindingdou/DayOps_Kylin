using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 创建块体模型对话框（忠实原 CreateBlockModelDialog + CreateBlockModelViewModel）：
/// 基本信息 / 几何范围（范围↔网格数 双向联动）/ 存储模式（Adaptive 子参数）/ 属性 Schema（模板+增删）/ 显示样式。
/// 「创建」= 校验 → 生成规则网格 → 入 <see cref="BlockModelStore"/> 并渲染。<c>ShowDialog&lt;bool&gt;</c> true = 已创建。
/// </summary>
public partial class CreateBlockModelWindow : Window
{
    /// <summary>属性 Schema 表格一行（原 PropertyColumnRowViewModel）。</summary>
    public sealed class PropertyRow : INotifyPropertyChanged
    {
        private string _name = "", _unit = "", _desc = "", _def = "0", _type = "浮点 (Float)";
        public string Name { get => _name; set { _name = value; N(nameof(Name)); } }
        public string Unit { get => _unit; set { _unit = value; N(nameof(Unit)); } }
        public string Description { get => _desc; set { _desc = value; N(nameof(Description)); } }
        public string DefaultValueText { get => _def; set { _def = value; N(nameof(DefaultValueText)); } }
        public string TypeLabel { get => _type; set { _type = value; N(nameof(TypeLabel)); } }
        public string[] TypeChoices { get; } = { BlockPropertyType.Float.ToChineseLabel(), BlockPropertyType.UInt32.ToChineseLabel(), BlockPropertyType.Int32.ToChineseLabel() };
        public BlockPropertyType DataType
        {
            get => TypeLabel == BlockPropertyType.UInt32.ToChineseLabel() ? BlockPropertyType.UInt32 : TypeLabel == BlockPropertyType.Int32.ToChineseLabel() ? BlockPropertyType.Int32 : BlockPropertyType.Float;
            set => TypeLabel = value.ToChineseLabel();
        }
        public double DefaultValue => double.TryParse(DefaultValueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        public BlockPropertyColumn ToDomain() => new() { Name = Name.Trim(), DataType = DataType, Unit = Unit, DefaultValue = DefaultValue, Description = Description };
        public event PropertyChangedEventHandler? PropertyChanged;
        private void N(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private readonly ModelingContext _ctx;
    private readonly ObservableCollection<PropertyRow> _rows = new();
    private readonly ColorSwatchPicker _fillPicker = new();
    private readonly ColorSwatchPicker _edgePicker = new();
    private bool _suppress;
    public BlockModelMeta? CreatedModel { get; private set; }

    /// <summary>XAML 编译器/设计器用。</summary>
    public CreateBlockModelWindow() { _ctx = null!; InitializeComponent(); }

    public CreateBlockModelWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        BlockModelStore.Adopt(ctx);
        int count = BlockModelStore.Models.Count;
        nameBox.Text = $"BlockModel_{count + 1}";
        _fillPicker.SelectedColor = ColorSwatchPicker.ToColor(BlockDefaultPalette.Next(count));
        _edgePicker.SelectedColor = Color.FromRgb(0x3A, 0x48, 0x56);
        _edgePicker.IsEnabled = false;
        fillPickerHost.Content = _fillPicker;
        edgePickerHost.Content = _edgePicker;
        colormapCombo.ItemsSource = new[] { BlockColormapPreset.RdYlBu, BlockColormapPreset.Viridis, BlockColormapPreset.Magma, BlockColormapPreset.Plasma, BlockColormapPreset.Jet, BlockColormapPreset.Gray, BlockColormapPreset.Turbo }
            .Select(p => p.ToString()).ToList();
        colormapCombo.SelectedIndex = 0;
        schemaGrid.ItemsSource = _rows;
        _rows.CollectionChanged += (_, _) => Recompute();

        foreach (var tb in new[] { oxBox, oyBox, ozBox, sxBox, syBox, szBox }) tb.LostFocus += (_, _) => RecomputeFromSize();
        foreach (var tb in new[] { maxXBox, maxYBox, maxZBox }) tb.LostFocus += (_, _) => RecomputeFromExtent();
        foreach (var tb in new[] { nxBox, nyBox, nzBox }) tb.LostFocus += (_, _) => RecomputeFromGrid();
        nameBox.TextChanged += (_, _) => Recompute();
        modeExtent.IsCheckedChanged += (_, _) => ApplyInputMode();
        modeGrid.IsCheckedChanged += (_, _) => ApplyInputMode();
        foreach (var rb in new[] { modeDense, modeSparse, modeAdaptive }) rb.IsCheckedChanged += (_, _) => { adaptivePanel.IsVisible = modeAdaptive.IsChecked == true; Recompute(); };
        edgeFixed.IsCheckedChanged += (_, _) => _edgePicker.IsEnabled = edgeFixed.IsChecked == true;
        templateCombo.SelectionChanged += (_, _) => ApplyTemplate();
        RecomputeFromExtent();
    }

    private static double D(TextBox tb, double fallback = 0) => double.TryParse((tb.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static int I(TextBox tb, int fallback = 0) => int.TryParse((tb.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private void ApplyInputMode()
    {
        bool extent = modeExtent.IsChecked == true;
        extentLabel.IsVisible = maxXBox.IsVisible = maxYBox.IsVisible = maxZBox.IsVisible = extentUnit.IsVisible = extent;
        gridLabel.IsVisible = nxBox.IsVisible = nyBox.IsVisible = nzBox.IsVisible = gridUnit.IsVisible = !extent;
    }

    /// <summary>范围 ÷ 块尺寸 → 网格数（向上取整），再把 max 校正到整数网格终点（原 RecomputeFromExtent）。</summary>
    private void RecomputeFromExtent()
    {
        if (_suppress) return;
        double sx = D(sxBox), sy = D(syBox), sz = D(szBox);
        if (sx <= 0 || sy <= 0 || sz <= 0) { Recompute(); return; }
        _suppress = true;
        try
        {
            double ox = D(oxBox), oy = D(oyBox), oz = D(ozBox);
            int nx = Math.Max(1, (int)Math.Ceiling((D(maxXBox) - ox) / sx));
            int ny = Math.Max(1, (int)Math.Ceiling((D(maxYBox) - oy) / sy));
            int nz = Math.Max(1, (int)Math.Ceiling((D(maxZBox) - oz) / sz));
            nxBox.Text = nx.ToString(); nyBox.Text = ny.ToString(); nzBox.Text = nz.ToString();
            maxXBox.Text = F(ox + nx * sx); maxYBox.Text = F(oy + ny * sy); maxZBox.Text = F(oz + nz * sz);
        }
        finally { _suppress = false; }
        Recompute();
    }

    private void RecomputeFromGrid()
    {
        if (_suppress) return;
        _suppress = true;
        try
        {
            maxXBox.Text = F(D(oxBox) + I(nxBox) * D(sxBox));
            maxYBox.Text = F(D(oyBox) + I(nyBox) * D(syBox));
            maxZBox.Text = F(D(ozBox) + I(nzBox) * D(szBox));
        }
        finally { _suppress = false; }
        Recompute();
    }

    private void RecomputeFromSize() { if (modeExtent.IsChecked == true) RecomputeFromExtent(); else RecomputeFromGrid(); }

    private long BlockCount => (long)I(nxBox) * I(nyBox) * I(nzBox);
    private BlockStorageMode StorageMode => modeDense.IsChecked == true ? BlockStorageMode.Dense : modeAdaptive.IsChecked == true ? BlockStorageMode.Adaptive : BlockStorageMode.Sparse;

    private void Recompute()
    {
        if (gridInfo == null) return;
        gridInfo.Text = $"→ 网格数: {I(nxBox)} × {I(nyBox)} × {I(nzBox)} = {BlockCount:N0} 块";
        aabbInfo.Text = $"→ AABB: ({D(oxBox):0.##}, {D(oyBox):0.##}, {D(ozBox):0.##}) – ({D(maxXBox):0.##}, {D(maxYBox):0.##}, {D(maxZBox):0.##})";
        int colBytes = _rows.Sum(r => r.DataType.ByteSize());
        long bytes = BlockCount * (1 + colBytes);
        if (StorageMode == BlockStorageMode.Sparse) bytes /= 2;
        estimateText.Text = $"ⓘ 预计 {BlockCount:N0} 块 · {StorageMode} 存储 ≈ {BlockModelMeta.BytesToHuman(bytes)}";
        string msg = Validate();
        string warn = BlockCount > 500_000_000 ? "⚠ 块数过大（>5 亿），将拒绝创建" : BlockCount > 100_000_000 ? "⚠ 块数较大，操作可能较慢" : BlockCount > 10_000_000 ? "ⓘ 大型模型，建议使用 Sparse 存储" : "";
        validationText.Text = msg.Length > 0 ? msg : warn;
        createBtn.IsEnabled = msg.Length == 0;
        ToolTip.SetTip(createBtn, msg.Length > 0 ? msg : null);
    }

    private string Validate()
    {
        if (string.IsNullOrWhiteSpace(nameBox.Text)) return "请输入模型名称";
        if (D(sxBox) <= 0 || D(syBox) <= 0 || D(szBox) <= 0) return "块尺寸必须 > 0";
        if (I(nxBox) <= 0 || I(nyBox) <= 0 || I(nzBox) <= 0) return "网格数必须 ≥ 1";
        if (BlockCount > 500_000_000) return $"块数 {BlockCount:N0} 超过上限 5 亿";
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in _rows)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) return "存在空的属性列名";
            if (!BlockPropertyColumn.IsValidName(c.Name.Trim())) return $"属性列名非法: '{c.Name}' (需为字母/下划线开头)";
            if (!names.Add(c.Name.Trim())) return $"属性列名重复: '{c.Name}'";
        }
        return "";
    }

    private void ApplyTemplate()
    {
        _rows.Clear();
        switch (templateCombo.SelectedIndex)
        {
            case 1:
                _rows.Add(new PropertyRow { Name = "calorific_value", Unit = "MJ/kg", DefaultValueText = "0", Description = "发热量" });
                _rows.Add(new PropertyRow { Name = "ash", Unit = "%", DefaultValueText = "0", Description = "灰分" });
                _rows.Add(new PropertyRow { Name = "sulfur", Unit = "%", DefaultValueText = "0", Description = "硫分" });
                _rows.Add(new PropertyRow { Name = "moisture", Unit = "%", DefaultValueText = "0", Description = "水分" });
                _rows.Add(new PropertyRow { Name = "density", Unit = "t/m³", DefaultValueText = "1.35", Description = "视密度" });
                _rows.Add(new PropertyRow { Name = "seam_thickness", Unit = "m", DefaultValueText = "0", Description = "煤厚" });
                break;
            case 2:
                _rows.Add(new PropertyRow { Name = "grade_au", Unit = "g/t", DefaultValueText = "0", Description = "金品位" });
                _rows.Add(new PropertyRow { Name = "grade_cu", Unit = "%", DefaultValueText = "0", Description = "铜品位" });
                _rows.Add(new PropertyRow { Name = "density", Unit = "t/m³", DefaultValueText = "2.7", Description = "密度" });
                _rows.Add(new PropertyRow { Name = "rock_type", DataType = BlockPropertyType.UInt32, DefaultValueText = "0", Description = "岩性 id" });
                break;
            case 3:
                _rows.Add(new PropertyRow { Name = "grade", DefaultValueText = "0", Description = "品位" });
                _rows.Add(new PropertyRow { Name = "domain", DataType = BlockPropertyType.UInt32, DefaultValueText = "0", Description = "域 id" });
                _rows.Add(new PropertyRow { Name = "density", Unit = "t/m³", DefaultValueText = "2.5", Description = "密度" });
                break;
        }
        Recompute();
    }

    private void OnAddProperty(object? sender, RoutedEventArgs e)
    {
        _rows.Add(new PropertyRow { Name = $"col_{_rows.Count + 1}" });
        Recompute();
    }

    private void OnRemoveProperty(object? sender, RoutedEventArgs e)
    {
        if (schemaGrid.SelectedItem is PropertyRow r) { _rows.Remove(r); Recompute(); }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private async void OnCreate(object? sender, RoutedEventArgs e)
    {
        try
        {
            string msg = Validate();
            if (msg.Length > 0) { await BlockMsgBox.WarnAsync(this, "创建块体失败", msg); return; }
            if (BlockCount > BlockVoxelBuilder.MaxRenderBlocks)
            {
                bool go = await BlockMsgBox.ConfirmAsync(this, "块数较多", $"预计 {BlockCount:N0} 块超过视口逐块渲染建议上限 {BlockVoxelBuilder.MaxRenderBlocks:N0}，创建后视口可能卡顿。\n仍要创建？");
                if (!go) return;
            }
            var m = BlockModelMeta.CreateRegular(nameBox.Text!.Trim(), D(oxBox), D(oyBox), D(ozBox), D(sxBox), D(syBox), D(szBox), I(nxBox), I(nyBox), I(nzBox));
            m.Description = descBox.Text ?? "";
            m.RotationZDeg = D(rotBox);
            m.StorageMode = StorageMode;
            m.SubBlockDepthMax = subDepthCombo.SelectedIndex + 1;
            m.SubMinX = D(subMinXBox, 5); m.SubMinY = D(subMinYBox, 5); m.SubMinZ = D(subMinZBox, 2.5);
            foreach (var r in _rows) m.PropertySchema.Add(r.ToDomain());
            m.DisplayStyle = new BlockDisplayStyle
            {
                FillColor = ColorSwatchPicker.ToTuple(_fillPicker.SelectedColor),
                EdgeColor = ColorSwatchPicker.ToTuple(_edgePicker.SelectedColor),
                EdgeMode = edgeFixed.IsChecked == true ? BlockEdgeMode.Fixed : edgeHidden.IsChecked == true ? BlockEdgeMode.HiddenUnlessSelected : BlockEdgeMode.AutoFromFill,
                EdgeWidthPx = new[] { 0.5, 1.0, 1.2, 1.5, 2.0 }[Math.Max(0, edgeWidthCombo.SelectedIndex)],
                DefaultColormap = Enum.TryParse<BlockColormapPreset>(colormapCombo.SelectedItem?.ToString(), out var p) ? p : BlockColormapPreset.RdYlBu,
            };
            var err = BlockModelStore.Create(_ctx, m);
            if (err != null) { await BlockMsgBox.WarnAsync(this, "创建块体失败", err); return; }
            CreatedModel = m;
            _ctx.Status($"已创建块体模型 {m.Name}（块数 {m.BlockCount:N0}, 存储 {m.StorageMode}）");
            Close(true);
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "创建块体", ex.Message); }
    }
}
