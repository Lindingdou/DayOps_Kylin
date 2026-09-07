using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 通用参数输入对话框（原 MeshEditLib ParameterDialog 的最小等价：按 schema 列出 标签/默认值/单位/说明, 确定回传值）。
/// 数值项校验失败即提示不关闭。
/// </summary>
public sealed class PromptDialog : Window
{
    public sealed record Field(string Key, string Label, string Default, string? Unit = null, string? Hint = null, bool Numeric = true, string[]? Choices = null);

    private readonly Dictionary<string, Control> _inputs = new();
    private readonly List<Field> _fields;
    private readonly TextBlock _err = new() { Foreground = Avalonia.Media.Brushes.Firebrick, FontSize = 11, IsVisible = false };
    public Dictionary<string, string> Values { get; } = new();

    public PromptDialog(string title, IReadOnlyList<Field> fields, string? description = null)
    {
        _fields = new List<Field>(fields);
        Title = title;
        Width = 420; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;
        Classes.Add("geodb");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 4) };
        int row = 0;
        foreach (var f in fields)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var lbl = new TextBlock { Text = f.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4) };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);
            Control input;
            if (f.Choices != null && f.Choices.Length > 0)
            {
                var cb = new ComboBox { ItemsSource = f.Choices, SelectedItem = Array.IndexOf(f.Choices, f.Default) >= 0 ? f.Default : f.Choices[0], HorizontalAlignment = HorizontalAlignment.Stretch };
                input = cb;
            }
            else input = new TextBox { Text = f.Default, Margin = new Thickness(0, 3) };
            if (!string.IsNullOrEmpty(f.Hint)) ToolTip.SetTip(input, f.Hint);
            Grid.SetRow(input, row); Grid.SetColumn(input, 1); grid.Children.Add(input);
            var unit = new TextBlock { Text = f.Unit ?? "", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = Avalonia.Media.Brushes.Gray };
            Grid.SetRow(unit, row); Grid.SetColumn(unit, 2); grid.Children.Add(unit);
            _inputs[f.Key] = input;
            row++;
        }
        var ok = new Button { Content = "确定", MinWidth = 80, IsDefault = true };
        ok.Classes.Add("primary");
        var cancel = new Button { Content = "取消", MinWidth = 80, IsCancel = true };
        ok.Click += (_, _) => { if (Collect()) Close(true); };
        cancel.Click += (_, _) => Close(false);
        var panel = new StackPanel { Margin = new Thickness(16, 12), Spacing = 8 };
        if (!string.IsNullOrEmpty(description)) panel.Children.Add(new TextBlock { Text = description, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Avalonia.Media.Brushes.Gray, FontSize = 12 });
        panel.Children.Add(grid);
        panel.Children.Add(_err);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } });
        Content = panel;
    }

    private bool Collect()
    {
        Values.Clear();
        foreach (var f in _fields)
        {
            string v = _inputs[f.Key] is ComboBox cb ? (cb.SelectedItem?.ToString() ?? "") : ((TextBox)_inputs[f.Key]).Text ?? "";
            if (f.Numeric && f.Choices == null && !double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            { _err.Text = $"「{f.Label}」须为数值"; _err.IsVisible = true; return false; }
            Values[f.Key] = v.Trim();
        }
        return true;
    }

    public double D(string key, double fallback = 0) => Values.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    public int I(string key, int fallback = 0) => (int)Math.Round(D(key, fallback));
    public string S(string key) => Values.TryGetValue(key, out var s) ? s : "";

    /// <summary>显示并等待; 取消返回 null。</summary>
    public static async Task<PromptDialog?> AskAsync(Window owner, string title, IReadOnlyList<Field> fields, string? description = null)
    {
        var d = new PromptDialog(title, fields, description);
        var ok = await d.ShowDialog<bool>(owner);
        return ok ? d : null;
    }
}
