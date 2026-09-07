using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Data.Sqlite;
using PitMine3D.Kylin.Data;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>「煤层结构」编辑(原 MeshEditLib.ModelUpdate.SeamStructureDialog): 增/删/上下移煤层, 顺序 = 上→下。确定后由调用方 Save 记住。
/// ShowDialog&lt;bool&gt; true=确定, 结果在 <see cref="Result"/>。</summary>
public partial class SeamStructureDialog : Window
{
    private readonly ObservableCollection<SeamStructureConfig.SeamItem> _items = new();
    private readonly SqliteConnection? _conn;

    /// <summary>确定后的结果(取消为 null)。</summary>
    public SeamStructureConfig? Result { get; private set; }

    /// <summary>XAML 编译器/设计器用。</summary>
    public SeamStructureDialog() { InitializeComponent(); }

    public SeamStructureDialog(SqliteConnection? conn, SeamStructureConfig current)
    {
        InitializeComponent();
        _conn = conn;
        foreach (var s in current.Seams)
            _items.Add(new SeamStructureConfig.SeamItem { Code = s.Code, Name = s.Name });
        seamList.ItemsSource = _items;
        LoadDict();
    }

    private void LoadDict()
    {
        try
        {
            if (_conn == null) return;
            var dict = LoadSeamDefs(_conn)
                .Select(d => new SeamStructureConfig.SeamItem { Code = d.Code, Name = d.Name })
                .ToList();
            dictCombo.ItemsSource = dict;
            if (dict.Count > 0) dictCombo.SelectedIndex = 0;
        }
        catch { /* 字典不可用: 仅能自定义添加 */ }
    }

    private void OnUp(object? sender, RoutedEventArgs e)
    {
        int i = seamList.SelectedIndex;
        if (i > 0) { _items.Move(i, i - 1); seamList.SelectedIndex = i - 1; }
    }

    private void OnDown(object? sender, RoutedEventArgs e)
    {
        int i = seamList.SelectedIndex;
        if (i >= 0 && i < _items.Count - 1) { _items.Move(i, i + 1); seamList.SelectedIndex = i + 1; }
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        int i = seamList.SelectedIndex;
        if (i >= 0) { _items.RemoveAt(i); seamList.SelectedIndex = Math.Min(i, _items.Count - 1); }
    }

    private void OnAddFromDict(object? sender, RoutedEventArgs e)
    {
        if (dictCombo.SelectedItem is SeamStructureConfig.SeamItem it) AddItem(it.Code, it.Name);
    }

    private void OnAddCustom(object? sender, RoutedEventArgs e)
    {
        string code = customCode.Text?.Trim() ?? "";
        string name = customName.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(code)) return;
        AddItem(code, string.IsNullOrEmpty(name) ? $"{code} 号煤层" : name);
        customCode.Text = ""; customName.Text = "";
    }

    private void AddItem(string code, string name)
    {
        if (_items.Any(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase))) return;  // 去重
        _items.Add(new SeamStructureConfig.SeamItem { Code = code, Name = string.IsNullOrEmpty(name) ? code : name });
        seamList.SelectedIndex = _items.Count - 1;
    }

    private void OnResetDefault(object? sender, RoutedEventArgs e)
    {
        _items.Clear();
        foreach (var it in SeamStructureConfig.Default(_conn).Seams) _items.Add(it);
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var cfg = new SeamStructureConfig();
        cfg.Seams.AddRange(_items);
        Result = cfg;
        Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
