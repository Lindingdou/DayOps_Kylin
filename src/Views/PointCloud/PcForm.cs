using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Views.Modeling;

namespace PitMine3D.Kylin.Views.PointCloud;

/// <summary>
/// 点云各算子对话框的**窗体组织**（忠实原 PointCloudLib 的一众 Dialog）：
/// 说明文字 → 蓝底要点框 → 参数分组框 → 预设按钮行 → 折叠的「高级」区 → 自定文案的确认按钮。
///
/// 为什么不继续用通用 <see cref="PromptDialog"/>：原版每个算子的窗体都是"讲清这条算子怎么用"的
/// 一屏说明书 —— 顶上一段说明、关键差异用蓝框点出来（如 SOR/ROR 的相对 vs 绝对离群）、
/// 常用参数组合做成预设按钮（矿卡 / 电铲…）、次要项收进「高级」。摊成一列"标签 + 输入框"就把
/// 这些都丢了，用户看着一样的几行数字，不知道该填什么。
///
/// 命令行发起的命令仍走原来的逐项问答（<see cref="PromptDialog.CommandLineAsker"/>），
/// 参数定义只有这一份，不会两处漂移。
/// </summary>
internal sealed class PcForm
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public string Title = "";
    public string OkText = "确定";
    public double Width = 480;

    /// <summary>命令行问答时用的一句话描述（对话框里由 <see cref="Text"/> 块承担）。</summary>
    public string? CliDescription;

    /// <summary>额外校验：返回非空串则不关闭并把它显示为错误。</summary>
    public Func<PromptValues, string?>? Validate;

    /// <summary>
    /// 控件改动的联动（原版 PointAttribDialog 切换分析项时会把显示区间一起切到该项量程）。
    /// 返回要写回的 键→值；返回 null 表示不改。
    /// </summary>
    public Func<string, PromptValues, Dictionary<string, string>?>? OnChanged;

    /// <summary>动态提示行的取值（键 = 提示块 id）。每次控件改动后重算。</summary>
    public Func<PromptValues, Dictionary<string, string>>? Hints;

    internal readonly List<object> Blocks = new();

    // ── 块 ──
    internal sealed record HeadBlk(string Text);
    internal sealed record TextBlk(string Text, bool Small);
    internal sealed record InfoBlk(string Text);
    internal sealed record HintBlk(string Id, string Text);
    internal sealed record GroupBlk(string? Header, List<PcRow> Rows, bool Collapsed);
    internal sealed record PresetBlk(string Label, List<PcPreset> Presets);

    /// <summary>标题行（14 号粗体，同原版对话框顶部那行）。</summary>
    public PcForm Head(string t) { Blocks.Add(new HeadBlk(t)); return this; }
    public PcForm Text(string t) { Blocks.Add(new TextBlk(t, false)); return this; }
    public PcForm Small(string t) { Blocks.Add(new TextBlk(t, true)); return this; }
    public PcForm Info(string t) { Blocks.Add(new InfoBlk(t)); return this; }
    public PcForm Hint(string id, string initial) { Blocks.Add(new HintBlk(id, initial)); return this; }
    public PcForm Rows(params PcRow[] rows) { Blocks.Add(new GroupBlk(null, rows.ToList(), false)); return this; }
    public PcForm Group(string header, params PcRow[] rows) { Blocks.Add(new GroupBlk(header, rows.ToList(), false)); return this; }
    public PcForm Advanced(string header, params PcRow[] rows) { Blocks.Add(new GroupBlk(header, rows.ToList(), true)); return this; }
    public PcForm Presets(string label, params PcPreset[] presets) { Blocks.Add(new PresetBlk(label, presets.ToList())); return this; }

    internal IEnumerable<PcRow> AllRows() => Blocks.OfType<GroupBlk>().SelectMany(g => g.Rows);

    internal List<PromptDialog.Field> AllFields() => AllRows().Select(r => r.Field).ToList();

    internal Dictionary<string, string> Defaults()
    {
        var d = new Dictionary<string, string>();
        foreach (var r in AllRows()) d[r.Field.Key] = ParamPrompt.DefaultOf(r.Field);
        return d;
    }

    /// <summary>数值项校验（同 <see cref="PromptDialog"/> 口径）+ 自定校验。返回错误串或 null。</summary>
    internal string? Check(Dictionary<string, string> values)
    {
        foreach (var r in AllRows())
        {
            var f = r.Field;
            if (!f.Numeric || f.Bool || f.Choices != null) continue;
            if (!double.TryParse(values[f.Key].Trim(), NumberStyles.Float, Inv, out _)) return $"「{f.Label}」须为数值";
        }
        return Validate?.Invoke(PromptValues.From(values));
    }

    /// <summary>
    /// 取参数：命令行发起 → 逐项问答；自检 → 取默认值直通；否则弹本窗体。取消返回 null。
    /// </summary>
    public async Task<PromptValues?> AskAsync(Window owner)
    {
        var cli = PromptDialog.CommandLineAsker?.Invoke(owner, Title, AllFields(), CliDescription);
        if (cli != null) return await cli;

        var def = Defaults();
        // 自检默认取默认值直通(批量脚本不能卡在模态框上)；PITMINE_SHOWDIALOG=1 时照常弹窗 —— 截图核对窗体组织用。
        if (Environment.GetEnvironmentVariable("PITMINE_SELFTEST") is { Length: > 0 }
            && Environment.GetEnvironmentVariable("PITMINE_SHOWDIALOG") is not { Length: > 0 })
            return Check(def) == null ? PromptValues.From(def) : null;

        var win = new PcFormWindow(this);
        return await win.ShowDialog<PromptValues?>(owner);
    }
}

/// <summary>一行参数（标签 + 输入 + 右侧灰字说明）。Choices 非空时是下拉，Radio=true 时改成竖排单选钮。</summary>
internal sealed class PcRow
{
    public PromptDialog.Field Field = null!;
    public string? Note;          // 右侧灰字
    public string? Tip;           // ToolTip
    public bool Radio;
    /// <summary>false = 灰掉不可改（原版对话框里"运行前没选中多段线"时把「补充线路」勾选框禁掉那种）。</summary>
    public bool Enabled = true;
    public double LabelWidth = 110;
    public double BoxWidth = 80;

    public static PcRow Num(string key, string label, string def, string? unit = null, string? note = null,
                            string? tip = null, double labelWidth = 110, double boxWidth = 80)
        => new()
        {
            Field = new PromptDialog.Field(key, label, def, unit, tip),
            Note = note, Tip = tip, LabelWidth = labelWidth, BoxWidth = boxWidth,
        };

    public static PcRow Combo(string key, string label, string[] choices, string def, string? note = null,
                              string? tip = null, double labelWidth = 110, double boxWidth = 240)
        => new()
        {
            Field = new PromptDialog.Field(key, label, def, null, tip, false, choices),
            Note = note, Tip = tip, LabelWidth = labelWidth, BoxWidth = boxWidth,
        };

    /// <summary>单选组（每项一行，同原版 RadioButton 竖排）。label 只在命令行问答里出现。</summary>
    public static PcRow Radios(string key, string label, string[] choices, string def, string? tip = null)
        => new()
        {
            Field = new PromptDialog.Field(key, label, def, null, tip, false, choices),
            Radio = true, Tip = tip,
        };

    public static PcRow Check(string key, string text, bool def, string? tip = null)
        => new()
        {
            Field = new PromptDialog.Field(key, text, def ? "是" : "否", null, tip, false, null, true),
            Tip = tip,
        };
}

/// <summary>预设按钮：一次把若干参数设成一组常用值（原版「矿卡」「高密度 (1m)」这类）。</summary>
internal sealed class PcPreset
{
    public string Name = "";
    public string? Tip;
    public (string key, string val)[] Set = Array.Empty<(string, string)>();

    public static PcPreset Of(string name, string? tip, params (string key, string val)[] set)
        => new() { Name = name, Tip = tip, Set = set };
}

/// <summary>按 <see cref="PcForm"/> 拼出来的对话框窗体。代码构建，无 XAML。</summary>
internal sealed class PcFormWindow : Window
{
    private readonly PcForm _form;
    private readonly Dictionary<string, Control> _inputs = new();
    private readonly Dictionary<string, List<RadioButton>> _radios = new();
    private readonly Dictionary<string, TextBlock> _hints = new();
    private readonly TextBlock _err = new() { Foreground = Brushes.Firebrick, FontSize = 11, IsVisible = false, TextWrapping = TextWrapping.Wrap };
    private bool _loaded;

    public PcFormWindow(PcForm form)
    {
        _form = form;
        Title = form.Title;
        Width = form.Width;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("geodb");

        var panel = new StackPanel { Margin = new Thickness(14), Spacing = 6 };
        foreach (var b in form.Blocks) panel.Children.Add(Build(b));
        panel.Children.Add(_err);

        var ok = new Button { Content = form.OkText, MinWidth = 90, IsDefault = true };
        ok.Classes.Add("primary");
        ok.Click += (_, _) =>
        {
            var v = Collect();
            string? e = form.Check(v);
            if (e != null) { _err.Text = e; _err.IsVisible = true; return; }
            Close(PromptValues.From(v));
        };
        var cancel = new Button { Content = "取消", MinWidth = 72, IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0),
            Children = { ok, cancel },
        });

        Content = new ScrollViewer { Content = panel, MaxHeight = 760 };
        _loaded = true;
        RefreshHints();
    }

    private Control Build(object block) => block switch
    {
        PcForm.HeadBlk h => new TextBlock
        {
            Text = h.Text, FontSize = 14, FontWeight = FontWeight.Bold,
            Foreground = Brush.Parse("#1A2430"), Margin = new Thickness(0, 0, 0, 6),
        },
        PcForm.TextBlk t => new TextBlock
        {
            Text = t.Text, TextWrapping = TextWrapping.Wrap,
            FontSize = t.Small ? 11 : 12,
            Foreground = t.Small ? Brushes.Gray : Brush.Parse("#2A2F36"),
            Margin = new Thickness(0, 0, 0, 4),
        },
        PcForm.InfoBlk i => new Border
        {
            Background = Brush.Parse("#EFF5FF"), BorderBrush = Brush.Parse("#B8CCE8"), BorderThickness = new Thickness(1),
            Padding = new Thickness(8), Margin = new Thickness(0, 2, 0, 6),
            Child = new TextBlock { Text = i.Text, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#2A4A70"), FontSize = 11 },
        },
        PcForm.HintBlk h => Hint(h),
        PcForm.PresetBlk p => Presets(p),
        PcForm.GroupBlk g => Group(g),
        _ => new TextBlock(),
    };

    private Control Hint(PcForm.HintBlk h)
    {
        var tb = new TextBlock { Text = h.Text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 2, 0, 2) };
        _hints[h.Id] = tb;
        return tb;
    }

    private Control Presets(PcForm.PresetBlk p)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        wrap.Children.Add(new TextBlock { Text = p.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), FontSize = 12 });
        foreach (var preset in p.Presets)
        {
            var b = new Button { Content = preset.Name, FontSize = 11, Margin = new Thickness(0, 2, 6, 2), MinHeight = 26 };
            if (preset.Tip != null) ToolTip.SetTip(b, preset.Tip);
            b.Click += (_, _) => { foreach (var (k, v) in preset.Set) SetValue(k, v); RefreshHints(); };
            wrap.Children.Add(b);
        }
        return wrap;
    }

    private Control Group(PcForm.GroupBlk g)
    {
        var inner = new StackPanel { Spacing = 4 };
        foreach (var r in g.Rows) inner.Children.Add(Row(r));

        if (g.Header == null) return inner;
        if (g.Collapsed) return new Expander { Header = g.Header, Content = new Border { Padding = new Thickness(4, 6, 0, 0), Child = inner }, Margin = new Thickness(0, 4, 0, 0) };
        return new Border
        {
            BorderBrush = Brush.Parse("#DCDFE4"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(8, 6), Margin = new Thickness(0, 2, 0, 6),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = g.Header, FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 0, 2) },
                    inner,
                },
            },
        };
    }

    private Control Row(PcRow r)
    {
        var f = r.Field;
        if (f.Bool)
        {
            var chk = new CheckBox { Content = f.Label, IsChecked = f.Default is "是" or "true" or "True" or "1", Margin = new Thickness(0, 2), IsEnabled = r.Enabled };
            if (r.Tip != null) ToolTip.SetTip(chk, r.Tip);
            chk.IsCheckedChanged += (_, _) => Changed(f.Key);
            _inputs[f.Key] = chk;
            return chk;
        }
        if (r.Radio && f.Choices is { Length: > 0 })
        {
            var sp = new StackPanel { Spacing = 2, Margin = new Thickness(0, 2) };
            var list = new List<RadioButton>();
            string group = "g_" + f.Key;
            foreach (var c in f.Choices)
            {
                var rb = new RadioButton { Content = c, GroupName = group, IsChecked = c == f.Default, FontSize = 12 };
                if (r.Tip != null) ToolTip.SetTip(rb, r.Tip);
                rb.IsCheckedChanged += (_, _) => { if (rb.IsChecked == true) Changed(f.Key); };
                list.Add(rb); sp.Children.Add(rb);
            }
            if (list.All(x => x.IsChecked != true) && list.Count > 0) list[0].IsChecked = true;
            _radios[f.Key] = list;
            return sp;
        }

        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2) };
        line.Children.Add(new TextBlock { Text = f.Label, VerticalAlignment = VerticalAlignment.Center, Width = r.LabelWidth, FontSize = 12 });
        Control input;
        if (f.Choices is { Length: > 0 })
        {
            var cb = new ComboBox
            {
                ItemsSource = f.Choices, Width = r.BoxWidth, FontSize = 12,
                SelectedItem = Array.IndexOf(f.Choices, f.Default) >= 0 ? f.Default : f.Choices[0],
            };
            cb.SelectionChanged += (_, _) => Changed(f.Key);
            input = cb;
        }
        else
        {
            var tb = new TextBox { Text = f.Default, Width = r.BoxWidth, FontSize = 12, MinHeight = 26, Padding = new Thickness(4, 2) };
            tb.LostFocus += (_, _) => Changed(f.Key);
            input = tb;
        }
        if (r.Tip != null) ToolTip.SetTip(input, r.Tip);
        _inputs[f.Key] = input;
        line.Children.Add(input);
        if (!string.IsNullOrEmpty(f.Unit))
            line.Children.Add(new TextBlock { Text = f.Unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), FontSize = 12 });
        if (!string.IsNullOrEmpty(r.Note))
            line.Children.Add(new TextBlock { Text = r.Note, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        return line;
    }

    private void Changed(string key)
    {
        if (!_loaded) return;
        var upd = _form.OnChanged?.Invoke(key, PromptValues.From(Collect()));
        if (upd != null) foreach (var kv in upd) SetValue(kv.Key, kv.Value);
        RefreshHints();
    }

    private void RefreshHints()
    {
        if (_hints.Count == 0 || _form.Hints == null) return;
        var texts = _form.Hints(PromptValues.From(Collect()));
        foreach (var kv in texts) if (_hints.TryGetValue(kv.Key, out var tb)) tb.Text = kv.Value;
    }

    private void SetValue(string key, string val)
    {
        if (_radios.TryGetValue(key, out var list))
        {
            foreach (var rb in list) rb.IsChecked = (rb.Content as string) == val;
            return;
        }
        if (!_inputs.TryGetValue(key, out var c)) return;
        switch (c)
        {
            case CheckBox chk: chk.IsChecked = val is "是" or "true" or "True" or "1"; break;
            case ComboBox cb: cb.SelectedItem = val; break;
            case TextBox tb: tb.Text = val; break;
        }
    }

    private Dictionary<string, string> Collect()
    {
        var v = new Dictionary<string, string>();
        foreach (var r in _form.AllRows())
        {
            string key = r.Field.Key;
            if (_radios.TryGetValue(key, out var list))
            {
                var hit = list.FirstOrDefault(x => x.IsChecked == true);
                v[key] = (hit?.Content as string) ?? r.Field.Default;
                continue;
            }
            v[key] = _inputs.TryGetValue(key, out var c)
                ? c switch
                {
                    CheckBox chk => chk.IsChecked == true ? "是" : "否",
                    ComboBox cb => cb.SelectedItem?.ToString() ?? r.Field.Default,
                    TextBox tb => (tb.Text ?? "").Trim(),
                    _ => r.Field.Default,
                }
                : r.Field.Default;
        }
        return v;
    }
}
