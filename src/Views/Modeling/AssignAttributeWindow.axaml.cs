using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 属性赋值对话框（忠实原 AssignAttributeDialog）：目标块体 / 目标范围（全部·仅筛选可见）/ 目标属性（含「+ 新建」）/
/// 赋值方式（常量 · 公式 <see cref="BlockAttrExpression"/> · 来自另一模型 y=a·x+b）+ 煤质联动两个独立子窗。
/// 应用后可用该属性驱动着色。<c>ShowDialog&lt;bool&gt;</c> true = 已写入。
/// </summary>
public partial class AssignAttributeWindow : Window
{
    private readonly ModelingContext _ctx;

    public AssignAttributeWindow() { _ctx = null!; InitializeComponent(); }

    public AssignAttributeWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        BlockModelStore.Adopt(ctx);
        targetCombo.ItemsSource = BlockModelStore.Models;
        var def = BlockModelStore.PickDefault();
        if (def != null) targetCombo.SelectedItem = def;
        rangeAll.IsCheckedChanged += (_, _) => UpdateFilteredHint();
        rangeFiltered.IsCheckedChanged += (_, _) => UpdateFilteredHint();
        RefreshSourceModelCombo();
    }

    private BlockModelMeta? Target => targetCombo.SelectedItem as BlockModelMeta;

    private void OnTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshAttrCombo();
        RefreshSourceModelCombo();
        UpdateFilteredHint();
    }

    private void OnAttrChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (attrCombo.SelectedItem is BlockPropertyColumn c) attrUnitLabel.Text = string.IsNullOrEmpty(c.Unit) ? "" : $"单位: {c.Unit}";
    }

    private void UpdateFilteredHint()
    {
        if (filteredHint == null) return;
        if (rangeFiltered.IsChecked != true) { filteredHint.Text = "（先用「筛选」对话框筛出要改的块；这里只写入视图中可见的那批，替换其原值）"; return; }
        if (Target is not { } m) { filteredHint.Text = "请先选择目标块体。"; return; }
        bool hasFilter = m.Filter != null && m.Filter.Conditions.Count > 0;
        if (!hasFilter) { filteredHint.Text = "⚠ 当前模型没有任何筛选条件，「筛选/可见」= 全部真实块。请先用「筛选」对话框设定条件。"; return; }
        if (m.BlockCount > 5_000_000) { filteredHint.Text = $"当前筛选：{m.Filter}（块数较多，应用后统计实际影响数）"; return; }
        filteredHint.Text = $"当前筛选：{m.Filter} → 将影响 {m.CountVisibleCells():N0} 块（替换其原值）";
    }

    private void RefreshSourceModelCombo()
    {
        if (sourceModelCombo == null) return;
        var others = BlockModelStore.Models.Where(m => !ReferenceEquals(m, Target)).ToList();
        sourceModelCombo.ItemsSource = others;
        if (others.Count > 0) sourceModelCombo.SelectedIndex = 0; else sourceAttrCombo.ItemsSource = null;
    }

    private void OnSourceModelChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sourceModelCombo.SelectedItem is BlockModelMeta sm) { sourceAttrCombo.ItemsSource = sm.PropertySchema.ToList(); if (sm.PropertySchema.Count > 0) sourceAttrCombo.SelectedIndex = 0; }
        else sourceAttrCombo.ItemsSource = null;
    }

    private void RefreshAttrCombo()
    {
        if (Target is not { } m) { attrCombo.ItemsSource = null; attrUnitLabel.Text = ""; return; }
        var cols = m.PropertySchema.ToList();
        attrCombo.ItemsSource = cols;
        if (cols.Count > 0) attrCombo.SelectedItem = cols.FirstOrDefault(c => c.Name == m.ActiveColormapAttribute) ?? cols[0];
        else attrUnitLabel.Text = "(模型无属性列，请先点 + 新建...)";
    }

    private async void OnCoalEstimate(object? sender, RoutedEventArgs e)
    {
        if (Target is not { } m) { await BlockMsgBox.WarnAsync(this, "煤质估值", "请先选择目标块体。"); return; }
        await new CoalQualityLinkWindow(_ctx, m).ShowDialog(this);
        RefreshAttrCombo();
    }

    private async void OnCoalCrossPlot(object? sender, RoutedEventArgs e)
    {
        if (Target is not { } m) { await BlockMsgBox.WarnAsync(this, "煤质交会", "请先选择目标块体。"); return; }
        await new CoalBlockCrossPlotWindow(_ctx, m).ShowDialog(this);
    }

    private async void OnNewAttr(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Target is not { } m) { await BlockMsgBox.WarnAsync(this, "新建属性", "请先选择目标块体。"); return; }
            var col = await NewAttributeQuickDialog.AskAsync(this);
            if (col == null) return;
            m.PropertySchema.Add(col);
            RefreshAttrCombo();
            attrCombo.SelectedItem = col;
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "新建属性", ex.Message); }
    }

    private async void OnValidateFormula(object? sender, RoutedEventArgs e)
    {
        try
        {
            var engine = BlockAttrExpression.Compile(formulaBox.Text ?? "");
            string vars = string.Join(", ", engine.ReferencedVariables);
            await BlockMsgBox.InfoAsync(this, "公式校验通过", $"语法 OK。\n\n引用变量: {(string.IsNullOrEmpty(vars) ? "(无)" : vars)}");
        }
        catch (BlockExprException ex) { await BlockMsgBox.WarnAsync(this, "公式语法错误", ex.Message); }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "公式校验", ex.Message); }
    }

    private static bool TryParseDouble(string? text, out double v) => double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Target is not { } m) { await BlockMsgBox.WarnAsync(this, "属性赋值", "请先选择目标块体。"); return; }
            if (attrCombo.SelectedItem is not BlockPropertyColumn col) { await BlockMsgBox.WarnAsync(this, "属性赋值", "请选择或新建一个目标属性。"); return; }
            Func<int, bool>? scope = null; string scopeTag = "全部";
            if (rangeFiltered.IsChecked == true) { scope = m.IsCellVisible; scopeTag = "筛选可见"; }

            if (modeConstant.IsChecked == true)
            {
                if (!TryParseDouble(constantBox.Text, out double val)) { await BlockMsgBox.WarnAsync(this, "属性赋值", $"无法解析常量值: \"{constantBox.Text}\""); return; }
                long written = m.SetConstant(col.Name, val, scope);
                if (scope != null && written == 0) { await BlockMsgBox.WarnAsync(this, "属性赋值", "筛选/可见的块为 0，未写入任何值。\n请先用「筛选」对话框筛出要修改的块。"); return; }
                await ApplyResultAndClose(m, col, scope == null ? $"全部设为常量 {val}" : $"{scopeTag} {written:N0} 块设为 {val}（替换原值）");
            }
            else if (modeFormula.IsChecked == true)
            {
                BlockAttrExpression engine;
                try { engine = BlockAttrExpression.Compile(formulaBox.Text ?? ""); }
                catch (BlockExprException ex) { await BlockMsgBox.WarnAsync(this, "公式语法错误", ex.Message); return; }
                int written = m.ApplyFormula(col.Name, engine, scope);
                if (scope != null && written == 0) { await BlockMsgBox.WarnAsync(this, "属性赋值", "筛选/可见的块为 0，未写入任何值。\n请先用「筛选」对话框筛出要修改的块。"); return; }
                await ApplyResultAndClose(m, col, $"按公式 {formulaBox.Text} 写入 {scopeTag} {written:N0} cells");
            }
            else if (modeAnother.IsChecked == true)
            {
                if (sourceModelCombo.SelectedItem is not BlockModelMeta src) { await BlockMsgBox.WarnAsync(this, "属性赋值", "请选择源模型。"); return; }
                if (sourceAttrCombo.SelectedItem is not BlockPropertyColumn srcCol) { await BlockMsgBox.WarnAsync(this, "属性赋值", "请选择源属性。"); return; }
                if (src.Nx != m.Nx || src.Ny != m.Ny || src.Nz != m.Nz || src.Blocks.Count != m.Blocks.Count)
                {
                    await BlockMsgBox.WarnAsync(this, "属性赋值", $"源模型 {src.Name} 的网格 ({src.DimensionsText}) 与目标 ({m.DimensionsText}) 不一致。\nv1 仅支持同尺寸 cell-by-cell 复制；重采样到 v2 实现。");
                    return;
                }
                var srcArr = src.GetAttr(srcCol.Name);
                if (srcArr == null) { await BlockMsgBox.WarnAsync(this, "属性赋值", $"源模型 {src.Name} 的属性 \"{srcCol.Name}\" 没有数据（CellData 为空）。\n请先给源模型赋值。"); return; }
                if (!TryParseDouble(scaleBox.Text, out double a)) a = 1.0;
                if (!TryParseDouble(offsetBox.Text, out double b)) b = 0.0;
                var dst = m.EnsureAttr(col.Name, col.DefaultValue);
                int written = 0;
                int limit = Math.Min(dst.Length, srcArr.Length);
                for (int i = 0; i < limit; i++) { if (scope != null && !scope(i)) continue; dst[i] = a * srcArr[i] + b; written++; }
                if (scope != null && written == 0) { await BlockMsgBox.WarnAsync(this, "属性赋值", "筛选/可见的块为 0，未写入任何值。\n请先用「筛选」对话框筛出要修改的块。"); return; }
                await ApplyResultAndClose(m, col, $"复制自 {src.Name}.{srcCol.Name}, 变换 y = {a} · x + {b}, 写入 {scopeTag} {written:N0} cells");
            }
            else await BlockMsgBox.WarnAsync(this, "属性赋值", "请选择一种赋值方式（常量 / 公式 / 来自另一模型）。");
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "属性赋值", ex.Message); }
    }

    private async System.Threading.Tasks.Task ApplyResultAndClose(BlockModelMeta m, BlockPropertyColumn col, string detail)
    {
        bool cm = useAsColormap.IsChecked == true;
        if (cm) { m.ActiveColormapAttribute = col.Name; m.ColormapRange = null; }
        BlockModelStore.RefreshDisplay(_ctx, m);
        await BlockMsgBox.InfoAsync(this, "属性赋值成功", $"模型 {m.Name} 的 {col.Name}\n  ◦ {detail}\n" + (cm ? $"\n已切换 colormap 着色 → {col.Name}" : ""));
        _ctx.Status($"属性赋值 {m.Name}.{col.Name}：{detail}");
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}

/// <summary>"+ 新建" 小弹窗（原 NewAttributeQuickDialog）：属性名 + 类型 + 单位 + 默认值。代码构建。</summary>
internal static class NewAttributeQuickDialog
{
    public static async System.Threading.Tasks.Task<BlockPropertyColumn?> AskAsync(Window owner)
    {
        var win = new Window { Title = "新建属性列", Width = 360, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.Parse("#F4F5F7")) };
        var grid = new Grid { Margin = new Thickness(14), ColumnDefinitions = new ColumnDefinitions("70,*,70,80"), RowDefinitions = new RowDefinitions("Auto,6,Auto,6,Auto,Auto") };
        void Lbl(string t, int r, int c) { var tb = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(tb, r); Grid.SetColumn(tb, c); grid.Children.Add(tb); }
        void Put(Control e, int r, int c, int span = 1) { Grid.SetRow(e, r); Grid.SetColumn(e, c); if (span > 1) Grid.SetColumnSpan(e, span); grid.Children.Add(e); }
        Lbl("名称", 0, 0);
        var nameBox = new TextBox(); Put(nameBox, 0, 1, 3);
        Lbl("类型", 2, 0);
        var typeBox = new ComboBox { ItemsSource = new[] { BlockPropertyType.Float, BlockPropertyType.UInt32, BlockPropertyType.Int32 }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch }; Put(typeBox, 2, 1);
        Lbl("单位", 2, 2);
        var unitBox = new TextBox(); Put(unitBox, 2, 3);
        Lbl("默认值", 4, 0);
        var defBox = new TextBox { Text = "0" }; Put(defBox, 4, 1, 3);
        var err = new TextBlock { Foreground = Brushes.Firebrick, FontSize = 11, IsVisible = false };
        var ok = new Button { Content = "确定", Width = 64, IsDefault = true }; ok.Classes.Add("primary");
        var cancel = new Button { Content = "取消", Width = 64, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        BlockPropertyColumn? result = null;
        cancel.Click += (_, _) => win.Close();
        ok.Click += (_, _) =>
        {
            var name = nameBox.Text?.Trim() ?? "";
            if (!BlockPropertyColumn.IsValidName(name)) { err.Text = $"列名非法：\"{name}\" （需字母/下划线开头，只含字母数字下划线）"; err.IsVisible = true; return; }
            double.TryParse(defBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double def);
            result = new BlockPropertyColumn { Name = name, DataType = (BlockPropertyType)(typeBox.SelectedItem ?? BlockPropertyType.Float), Unit = unitBox.Text?.Trim() ?? "", DefaultValue = def };
            win.Close();
        };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { cancel, ok } };
        var bottom = new StackPanel { Children = { err, btns } };
        Put(bottom, 5, 0, 4);
        win.Content = grid;
        await win.ShowDialog(owner);
        return result;
    }
}
