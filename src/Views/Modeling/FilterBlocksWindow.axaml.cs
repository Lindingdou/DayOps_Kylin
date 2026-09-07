using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 筛选块体对话框（忠实原 FilterBlocksDialog v1.4）：每行 = 启用/属性/操作符/值1(分类属性 → 码=名 下拉)/值2；AND/OR 组合；
/// 应用 = 写 model.Filter → 不满足的 cell 隐藏；重置 = 清筛选。<c>ShowDialog&lt;bool&gt;</c> true = 已应用。
/// </summary>
public partial class FilterBlocksWindow : Window
{
    public sealed class OpItem
    {
        public string Label { get; init; } = ""; public BlockFilterOperator Op { get; init; }
        public override string ToString() => Label;
        public override bool Equals(object? obj) => obj is OpItem o && o.Op == Op;
        public override int GetHashCode() => Op.GetHashCode();
    }
    public static readonly OpItem[] Operators =
    {
        new() { Label = ">  大于", Op = BlockFilterOperator.GreaterThan }, new() { Label = "≥  大于等于", Op = BlockFilterOperator.GreaterEqual },
        new() { Label = "<  小于", Op = BlockFilterOperator.LessThan }, new() { Label = "≤  小于等于", Op = BlockFilterOperator.LessEqual },
        new() { Label = "=  等于", Op = BlockFilterOperator.Equal }, new() { Label = "≠  不等于", Op = BlockFilterOperator.NotEqual },
        new() { Label = "BETWEEN 区间内", Op = BlockFilterOperator.Between },
    };

    public sealed class CategoryOption
    {
        public int Code { get; init; } public string Display { get; init; } = "";
        public override string ToString() => Display;
    }

    public sealed class ConditionRow : INotifyPropertyChanged
    {
        private bool _enabled = true;
        public bool Enabled { get => _enabled; set { if (_enabled != value) { _enabled = value; N(nameof(Enabled)); } } }
        private string _attr = "";
        public string AttributeName { get => _attr; set { if (_attr == value) return; _attr = value ?? ""; RefreshCategoryState(); N(nameof(AttributeName)); } }
        public OpItem? Op { get; set; } = Operators[0];
        private string _valueText = "0";
        public string ValueText { get => _valueText; set { if (_valueText == value) return; _valueText = value ?? ""; N(nameof(ValueText)); N(nameof(Value1Option)); } }
        public string Value2Text { get; set; } = "1";
        public ObservableCollection<string> AvailableAttrs { get; } = new();
        public BlockModelMeta? Owner { get; set; }
        public ObservableCollection<OpItem> AvailableOps { get; } = new(Operators);
        private bool _isCat;
        public bool IsCategorical { get => _isCat; private set { if (_isCat != value) { _isCat = value; N(nameof(IsCategorical)); } } }
        public ObservableCollection<CategoryOption> CategoryOptions { get; } = new();
        public CategoryOption? Value1Option
        {
            get => int.TryParse(_valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c) ? CategoryOptions.FirstOrDefault(o => o.Code == c) : null;
            set { if (value != null) ValueText = value.Code.ToString(CultureInfo.InvariantCulture); }
        }
        private void RefreshCategoryState()
        {
            CategoryOptions.Clear();
            var col = Owner?.FindColumn(_attr);
            bool cat = col is { IsCategorical: true };
            if (cat && Owner != null)
                foreach (var (code, label) in Owner.EnumerateCategories(_attr))
                    CategoryOptions.Add(new CategoryOption { Code = code, Display = label.Length > 0 ? $"{code} = {label}" : code.ToString() });
            IsCategorical = cat && CategoryOptions.Count > 0;
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        private void N(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    private readonly ModelingContext _ctx;
    private readonly ObservableCollection<ConditionRow> _rows = new();

    public FilterBlocksWindow() { _ctx = null!; InitializeComponent(); }

    public FilterBlocksWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        BlockModelStore.Adopt(ctx);
        conditionsGrid.ItemsSource = _rows;
        targetCombo.ItemsSource = BlockModelStore.Models;
        var def = BlockModelStore.PickDefault();
        if (def != null) targetCombo.SelectedItem = def;
    }

    private BlockModelMeta? Target => targetCombo.SelectedItem as BlockModelMeta;

    private void OnTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Target is not { } m) return;
        _rows.Clear();
        if (m.Filter is { } fs)
        {
            modeAnd.IsChecked = fs.MatchAll; modeOr.IsChecked = !fs.MatchAll;
            foreach (var c in fs.Conditions)
            {
                var row = new ConditionRow { Enabled = true, Op = Operators.FirstOrDefault(o => o.Op == c.Op), Value2Text = c.Value2.ToString(CultureInfo.InvariantCulture), Owner = m };
                foreach (var col in m.PropertySchema) row.AvailableAttrs.Add(col.Name);
                row.AttributeName = c.AttributeName;
                row.ValueText = c.Value.ToString(CultureInfo.InvariantCulture);
                _rows.Add(row);
            }
            statusLabel.Text = $"当前已有筛选：{fs}";
        }
        else statusLabel.Text = "ⓘ 行的 [启用] 勾选才参与计算；未勾选的条件忽略。";
        if (_rows.Count == 0) AddBlankRow(m);
    }

    private void AddBlankRow(BlockModelMeta m)
    {
        var row = new ConditionRow { Owner = m };
        foreach (var col in m.PropertySchema) row.AvailableAttrs.Add(col.Name);
        if (m.PropertySchema.Count > 0) row.AttributeName = m.PropertySchema[0].Name;
        _rows.Add(row);
    }

    private void OnAddRow(object? sender, RoutedEventArgs e) { if (Target is { } m) AddBlankRow(m); }
    private void OnRemoveRow(object? sender, RoutedEventArgs e)
    {
        if (conditionsGrid.SelectedItem is ConditionRow row) _rows.Remove(row);
        else if (_rows.Count > 0) _rows.RemoveAt(_rows.Count - 1);
    }
    private void OnClearRows(object? sender, RoutedEventArgs e) => _rows.Clear();

    private static bool P(string? s, out double v) => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Target is not { } m) { await BlockMsgBox.WarnAsync(this, "筛选块体", "请选择目标块体。"); return; }
            var fs = new BlockFilterSet { MatchAll = modeAnd.IsChecked == true };
            int rowNum = 0;
            foreach (var row in _rows)
            {
                rowNum++;
                if (!row.Enabled) continue;
                if (string.IsNullOrWhiteSpace(row.AttributeName)) { await BlockMsgBox.WarnAsync(this, "筛选块体", $"第 {rowNum} 行未选择属性。"); return; }
                if (row.Op == null) { await BlockMsgBox.WarnAsync(this, "筛选块体", $"第 {rowNum} 行未选择操作符。"); return; }
                if (!m.HasData(row.AttributeName)) { await BlockMsgBox.WarnAsync(this, "筛选块体", $"第 {rowNum} 行：属性 \"{row.AttributeName}\" 还没有数据。\n请先用 [属性赋值] 给该列写值。"); return; }
                if (!P(row.ValueText, out double v1)) { await BlockMsgBox.WarnAsync(this, "筛选块体", $"第 {rowNum} 行 值1 无法解析: \"{row.ValueText}\""); return; }
                double v2 = 0;
                if (row.Op.Op == BlockFilterOperator.Between && !P(row.Value2Text, out v2)) { await BlockMsgBox.WarnAsync(this, "筛选块体", $"第 {rowNum} 行 值2 无法解析: \"{row.Value2Text}\""); return; }
                fs.Conditions.Add(new BlockFilterCondition { AttributeName = row.AttributeName, Op = row.Op.Op, Value = v1, Value2 = v2 });
            }
            if (fs.Conditions.Count == 0)
            {
                m.Filter = null;
                BlockModelStore.RefreshDisplay(_ctx, m);
                await BlockMsgBox.InfoAsync(this, "筛选已重置", "没有启用任何条件，已清空筛选。");
                statusLabel.Text = "ⓘ 已重置";
                return;
            }
            m.Filter = fs;
            BlockModelStore.RefreshDisplay(_ctx, m);
            await BlockMsgBox.InfoAsync(this, "筛选已应用", $"模型 {m.Name} 现在按 {(fs.MatchAll ? "AND" : "OR")} 组合 {fs.Conditions.Count} 个条件筛选：\n" + string.Join("\n", fs.Conditions.Select(c => "  • " + c)));
            _ctx.Status($"筛选块体 {m.Name}：{fs}（可见 {m.CountVisibleCells():N0} 块）");
            Close(true);
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "筛选块体", ex.Message); }
    }

    private async void OnReset(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Target is not { } m) { await BlockMsgBox.WarnAsync(this, "筛选块体", "请选择目标块体。"); return; }
            m.Filter = null;
            BlockModelStore.RefreshDisplay(_ctx, m);
            await BlockMsgBox.InfoAsync(this, "筛选已重置", $"模型 {m.Name} 的所有 cell 已恢复显示。");
            statusLabel.Text = "ⓘ 已重置";
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "筛选块体", ex.Message); }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
