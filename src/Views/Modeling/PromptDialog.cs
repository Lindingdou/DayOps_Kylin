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
    /// <param name="Bool">true = 复选框项（原 ParameterDialog 的 AddBool）；Default 用 "是"/"否"（或 true/false）。</param>
    public sealed record Field(string Key, string Label, string Default, string? Unit = null, string? Hint = null, bool Numeric = true, string[]? Choices = null, bool Bool = false);

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
            if (f.Bool)
            {
                input = new CheckBox { IsChecked = f.Default is "是" or "true" or "True" or "1", Margin = new Thickness(0, 3) };
            }
            else if (f.Choices != null && f.Choices.Length > 0)
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
            string v = _inputs[f.Key] switch
            {
                CheckBox chk => chk.IsChecked == true ? "是" : "否",
                ComboBox cb => cb.SelectedItem?.ToString() ?? "",
                TextBox tb => tb.Text ?? "",
                _ => "",
            };
            if (f.Numeric && !f.Bool && f.Choices == null && !double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            { _err.Text = $"「{f.Label}」须为数值"; _err.IsVisible = true; return false; }
            Values[f.Key] = v.Trim();
        }
        return true;
    }

    public double D(string key, double fallback = 0) => Result.D(key, fallback);
    public int I(string key, int fallback = 0) => Result.I(key, fallback);
    public string S(string key) => Result.S(key);
    /// <summary>复选框项取值（Field.Bool = true 的项）。</summary>
    public bool B(string key, bool fallback = false) => Result.B(key, fallback);

    private PromptValues Result => PromptValues.From(Values);

    /// <summary>
    /// 取参数：优先交给命令行逐项问答（<see cref="CommandLineAsker"/>，AutoCAD 式），
    /// 它不接管才弹对话框。取消返回 null。
    /// </summary>
    public static async Task<PromptValues?> AskAsync(Window owner, string title, IReadOnlyList<Field> fields, string? description = null)
    {
        // 命令行发起的命令 → 参数在命令行里逐项问（回车取默认、Esc 取消）。返回 null 表示"不接管"。
        var cli = CommandLineAsker?.Invoke(owner, title, fields, description);
        if (cli != null) return await cli;

        var d = new PromptDialog(title, fields, description);
        if (Environment.GetEnvironmentVariable("PITMINE_SELFTEST") is { Length: > 0 })   // 自检：按默认值直接确定(不弹窗)
            return d.Collect() ? PromptValues.From(d.Values) : null;
        var ok = await d.ShowDialog<bool>(owner);
        return ok ? PromptValues.From(d.Values) : null;
    }

    /// <summary>
    /// 命令行问答挂钩。由主窗口装上；返回 null 表示本次不接管（照旧弹对话框），
    /// 否则返回的任务在用户答完（或 Esc 取消 → null）时完成。
    /// </summary>
    public static Func<Window, string, IReadOnlyList<Field>, string?, Task<PromptValues?>?>? CommandLineAsker;
}

/// <summary>
/// 参数取值结果 —— 与来源无关：对话框填的、命令行逐项问的、自检取默认的，拿到的都是它。
/// 键取值方法名与原 PromptDialog 一致（D/I/S/B），调用处不必改。
/// </summary>
public sealed class PromptValues
{
    public Dictionary<string, string> Values { get; } = new();

    public static PromptValues From(Dictionary<string, string> values)
    {
        var v = new PromptValues();
        foreach (var kv in values) v.Values[kv.Key] = kv.Value;
        return v;
    }

    public double D(string key, double fallback = 0) => Values.TryGetValue(key, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;
    public int I(string key, int fallback = 0) => (int)Math.Round(D(key, fallback));
    public string S(string key) => Values.TryGetValue(key, out var s) ? s : "";
    public bool B(string key, bool fallback = false) => Values.TryGetValue(key, out var s) ? s is "是" or "true" or "True" or "1" : fallback;
}
