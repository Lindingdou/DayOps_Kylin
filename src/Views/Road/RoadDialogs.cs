using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.Road;

/// <summary>
/// 「基础道路网络构建」参数对话框（模态）：指定名称 + 必要参数（吸附容差/缺口桥接/立交标高差）。
/// 忠实原 RoadLib.Views.BuildNetworkDialog。
/// </summary>
internal sealed class BuildNetworkDialog : Window
{
    /// <summary>自检默认取默认值直通（批量脚本不能卡在模态框上）；PITMINE_SHOWDIALOG=1 时照常弹窗。同 PcForm。</summary>
    internal static bool SelftestBypass
        => Environment.GetEnvironmentVariable("PITMINE_SELFTEST") is { Length: > 0 }
           && Environment.GetEnvironmentVariable("PITMINE_SHOWDIALOG") is not { Length: > 0 };

    /// <summary>弹窗（或自检直通默认参数）。返回 true = 确定。</summary>
    public async Task<bool> AskAsync(Window owner)
    {
        if (SelftestBypass) { NetworkName = _name.Text?.Trim() ?? "路网"; return true; }
        return await ShowDialog<bool?>(owner) == true;
    }

    private readonly TextBox _name, _snap, _bridge, _grade;
    public string NetworkName { get; private set; } = "";
    /// <summary>吸附/打断容差 m（默认 10 —— 5m 会漏掉 5~10m 那一批路口）。</summary>
    public double SnapToleranceM { get; private set; } = 10.0;
    public double BridgeGapM { get; private set; } = 25.0;
    public double GradeSeparationM { get; private set; } = 4.0;

    public BuildNetworkDialog(string defaultName)
    {
        Title = "基础道路网络构建";
        RoadUi.Place(this, 460);
        CanResize = false;
        _name = RoadUi.Box(defaultName, 400);
        _snap = RoadUi.Box("10", 400);
        _bridge = RoadUi.Box("25", 400);
        _grade = RoadUi.Box("4", 400);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(RoadUi.Hint("把所有道路中心线整合成连通的、可供未来规划与寻径的路网，按名称随工程存储（同名覆盖）。", 12.5));
        panel.Children[0].Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(Field("路网名称", _name, "存储用；同名将覆盖该期路网"));
        panel.Children.Add(Field("端点吸附容差 (m)", _snap, "≤此距离的端点并为同一节点"));
        panel.Children.Add(Field("缺口桥接距离 (m)", _bridge, "≤此距离、分属不同片的断口自动接上（0=不桥）"));
        panel.Children.Add(Field("立交标高差 (m)", _grade, "平面相交但标高差超此值视为立交，不连"));
        panel.Children.Add(RoadUi.Foot(RoadUi.Btn("确定", () => _ = OnOk(), 80, primary: true), RoadUi.Btn("取消", () => Close(false), 80)));
        Content = panel;
    }

    private static Control Field(string label, TextBox box, string hint)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        sp.Children.Add(RoadUi.Text(label, 12.5));
        sp.Children.Add(box);
        sp.Children.Add(RoadUi.Hint(hint, 11));
        return sp;
    }

    private async Task OnOk()
    {
        var name = _name.Text?.Trim();
        if (string.IsNullOrEmpty(name)) { await RoadUi.Info(this, "基础道路网络构建", "请填写路网名称。"); return; }
        NetworkName = name;
        SnapToleranceM = ParsePositive(_snap.Text, 10.0);
        BridgeGapM = ParseNonNegative(_bridge.Text, 25.0);
        GradeSeparationM = ParsePositive(_grade.Text, 4.0);
        Close(true);
    }

    internal static double ParsePositive(string? s, double fallback)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : fallback;
    internal static double ParseNonNegative(string? s, double fallback)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;
}

/// <summary>提交方式：就地修正当前网（同名覆盖） / 另存为需新命名的增量网。</summary>
internal enum NetworkUpdateMode { UpdateCurrent, IncrementNew }

/// <summary>
/// 「更新路网」统一提交闸门对话框（模态）：当前路网更新（同名覆盖来源存档，旧版转快照留底）/
/// 增量路网更新（另存为新命名的新存档）。顶部显示变更摘要 + 提交前校验提示。忠实原 UpdateNetworkDialog。
/// </summary>
internal sealed class UpdateNetworkDialog : Window
{
    /// <summary>弹窗（或自检直通「当前路网更新」）。返回 true = 应用。</summary>
    public async Task<bool> AskAsync(Window owner)
    {
        if (BuildNetworkDialog.SelftestBypass) { Mode = NetworkUpdateMode.UpdateCurrent; return true; }
        return await ShowDialog<bool?>(owner) == true;
    }

    private readonly RadioButton _rbCurrent, _rbIncrement;
    private readonly TextBox _name;
    public NetworkUpdateMode Mode { get; private set; } = NetworkUpdateMode.UpdateCurrent;
    public string NewName { get; private set; } = "";

    public UpdateNetworkDialog(string summary, string? baseName, string defaultNewName, string? warning)
    {
        Title = "更新路网（锁定变更）";
        RoadUi.Place(this, 500);
        CanResize = false;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(RoadUi.Text("变更摘要", 13, bold: true));
        var sum = RoadUi.Text(summary, 12.5); sum.Margin = new Thickness(0, 2, 0, warning is null ? 12 : 6);
        panel.Children.Add(sum);
        if (!string.IsNullOrWhiteSpace(warning)) { var w = RoadUi.Warn("⚠ " + warning); w.Margin = new Thickness(0, 0, 0, 12); panel.Children.Add(w); }
        _rbCurrent = new RadioButton
        {
            Content = baseName is null ? "当前路网更新（仅会话；工程库未连接，不入档）" : $"当前路网更新（同名覆盖「{baseName}」，旧版转快照留底）",
            IsChecked = true, Margin = new Thickness(0, 0, 0, 6), GroupName = "upd",
        };
        _rbIncrement = new RadioButton { Content = "增量路网更新（另存为新命名，原网保留）", Margin = new Thickness(0, 0, 0, 4), GroupName = "upd" };
        _name = RoadUi.Box(defaultNewName, 420);
        _name.Margin = new Thickness(22, 0, 0, 4);
        _name.IsEnabled = false;
        _rbIncrement.IsCheckedChanged += (_, _) => _name.IsEnabled = _rbIncrement.IsChecked == true;
        panel.Children.Add(_rbCurrent);
        panel.Children.Add(_rbIncrement);
        var lbl = RoadUi.Hint("新路网名称：", 11); lbl.Margin = new Thickness(22, 2, 0, 1);
        panel.Children.Add(lbl);
        panel.Children.Add(_name);
        panel.Children.Add(RoadUi.Foot(RoadUi.Btn("应用", () => _ = OnOk(), 80, primary: true), RoadUi.Btn("取消", () => Close(false), 80)));
        Content = panel;
    }

    private async Task OnOk()
    {
        if (_rbIncrement.IsChecked == true)
        {
            var name = _name.Text?.Trim();
            if (string.IsNullOrEmpty(name)) { await RoadUi.Info(this, "更新路网", "增量路网更新需要填写新路网名称。"); return; }
            Mode = NetworkUpdateMode.IncrementNew;
            NewName = name;
        }
        else Mode = NetworkUpdateMode.UpdateCurrent;
        Close(true);
    }
}

/// <summary>
/// 「点对点寻径」的模式选择窗（模态）：① 按装卸点（源/汇下拉）② 视口取两点；顶部常驻口径行（择路口径可当场改）。
/// 忠实原 PathSearchModeWindow。
/// </summary>
internal sealed class PathSearchModeWindow : Window
{
    public enum PickMode { Cancelled, ByLoadUnload, ByViewportPoints }
    public PickMode Result { get; private set; } = PickMode.Cancelled;
    public string? FromId { get; private set; }
    public string? ToId { get; private set; }
    public WeightMode Mode { get; private set; } = WeightMode.Time;

    private readonly RadioButton _rbByPoints, _rbByLoadUnload;
    private readonly ComboBox _cbFrom, _cbTo, _cbMode;
    internal static readonly (WeightMode Mode, string Text)[] Modes =
    {
        (WeightMode.Time, "时间最短（默认·卡车实际按最快路走）"),
        (WeightMode.Distance, "里程最短"),
        (WeightMode.Cost, "成本最省（按等效运距）"),
    };

    public PathSearchModeWindow(IReadOnlyList<string> loadingIds, IReadOnlyList<string> unloadingIds, HaulCaliper caliper)
    {
        Title = "点对点寻径";
        RoadUi.Place(this, 580);
        CanResize = false;
        bool hasLoadUnload = loadingIds.Count > 0 && unloadingIds.Count > 0;
        _rbByLoadUnload = new RadioButton { Content = "① 按装卸点（采剥点 → 卸载点）", IsChecked = hasLoadUnload, IsEnabled = hasLoadUnload, Margin = new Thickness(0, 0, 0, 6), FontWeight = FontWeight.Bold, GroupName = "pm" };
        _rbByPoints = new RadioButton { Content = "② 视口取两点（自己指定起终点）", IsChecked = !hasLoadUnload, Margin = new Thickness(0, 10, 0, 6), FontWeight = FontWeight.Bold, GroupName = "pm" };
        _cbFrom = RoadUi.Combo(loadingIds, 300);
        _cbTo = RoadUi.Combo(unloadingIds, 300);
        if (loadingIds.Count > 0) _cbFrom.SelectedIndex = 0;
        if (unloadingIds.Count > 0) _cbTo.SelectedIndex = 0;
        var pickPanel = new StackPanel { Margin = new Thickness(22, 0, 0, 0) };
        pickPanel.Children.Add(RoadUi.Lbl("起点（采剥点·源）："));
        pickPanel.Children.Add(_cbFrom);
        pickPanel.Children.Add(RoadUi.Lbl("终点（卸载点·汇）："));
        pickPanel.Children.Add(_cbTo);
        void SyncEnable() { bool m1 = _rbByLoadUnload.IsChecked == true; _cbFrom.IsEnabled = m1; _cbTo.IsEnabled = m1; }
        _rbByLoadUnload.IsCheckedChanged += (_, _) => SyncEnable();
        _rbByPoints.IsCheckedChanged += (_, _) => SyncEnable();
        SyncEnable();

        _cbMode = RoadUi.Combo(Modes.Select(m => m.Text), 420);
        _cbMode.SelectedIndex = 0;
        var caliperText = new TextBlock { Text = caliper.SummaryBlock().TrimEnd(), TextWrapping = TextWrapping.Wrap, FontSize = 11, FontFamily = RoadUi.Mono, Margin = new Thickness(0, 0, 0, 4) };
        if (caliper.FromSharedSettings) RoadUi.Theme(caliperText, TextBlock.ForegroundProperty, "Theme.Text.Body");
        else caliperText.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x6A, 0x00));
        var caliperPanel = new StackPanel();
        caliperPanel.Children.Add(RoadUi.Lbl("择路口径（本次生效，不回写共享约束）："));
        caliperPanel.Children.Add(_cbMode);
        caliperPanel.Children.Add(caliperText);
        var caliperBox = new Expander { Header = "口径 ▸ " + caliper.ShortLine(), IsExpanded = false, Content = caliperPanel, Margin = new Thickness(0, 0, 0, 12), FontSize = 12 };

        var hint = RoadUi.Hint((hasLoadUnload
                ? $"提示：模式①直接选已设装卸点；模式②在视口左键点起终点（Esc 取消），吸附上限 {caliper.SnapRadiusM:F0}m，超出即不吸附并报实距。"
                : "提示：路网里尚无源/汇节点，模式①不可用。可先用「破碎站位置设置」定破碎站（汇）、用「去向台账」补排土场/储矿场，或直接用模式②在视口取两点。")
            + "\n一次计算解两条腿：去程重车 src→dst（玫红）、回程空车 dst→src（亮紫，仅画去程没走的段）。"
            + "\n修改全局口径请到「剥采工程辅助设计 → 运输系统 → 约束条件设置」。", 12);
        hint.Margin = new Thickness(0, 10, 0, 0);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(caliperBox);
        root.Children.Add(_rbByLoadUnload);
        root.Children.Add(pickPanel);
        root.Children.Add(_rbByPoints);
        root.Children.Add(hint);
        root.Children.Add(RoadUi.Foot(RoadUi.Btn("开始寻径", () => _ = OnOk(), 96, primary: true), RoadUi.Btn("取消", Close, 80)));
        Content = root;
    }

    private async Task OnOk()
    {
        int mi = _cbMode.SelectedIndex;
        Mode = mi >= 0 && mi < Modes.Length ? Modes[mi].Mode : WeightMode.Time;
        if (_rbByLoadUnload.IsChecked == true)
        {
            if (_cbFrom.SelectedItem is not string from || _cbTo.SelectedItem is not string to)
            { await RoadUi.Info(this, "点对点寻径", "请选择起点（采剥点）和终点（卸载点）。"); return; }
            if (from == to) { await RoadUi.Info(this, "点对点寻径", "起点和终点不能相同。"); return; }
            FromId = from; ToId = to; Result = PickMode.ByLoadUnload;
        }
        else Result = PickMode.ByViewportPoints;
        Close();
    }
}

/// <summary>
/// 运输结果窗（非模态，单例复用）：等宽文本显示报表 / 路径明细；「清除高亮」+ 动作钮（跳「等效运距」比选）。
/// 忠实原 TransportResultWindow。
/// </summary>
internal sealed class TransportResultWindow : Window
{
    private readonly TextBox _text;
    private readonly Button _clear;
    private readonly StackPanel _extras;
    private Action? _onClear;

    private TransportResultWindow(string title, string content, Action? onClear, IReadOnlyList<(string Label, Action Do)>? actions)
    {
        _onClear = onClear;
        Title = title;
        RoadUi.Place(this, 760, 500);
        _text = RoadUi.Mono2(content, 13);
        _clear = RoadUi.Btn("清除高亮", () => _onClear?.Invoke(), 88);
        _clear.IsVisible = onClear is not null;
        _extras = new StackPanel { Orientation = Orientation.Horizontal };
        SetActions(actions);
        var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 8, 12, 10) };
        foot.Children.Add(_extras);
        foot.Children.Add(_clear);
        foot.Children.Add(RoadUi.Btn("关闭", Close, 80));
        var root = new DockPanel();
        DockPanel.SetDock(foot, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(foot);
        root.Children.Add(new ScrollViewer { Content = _text, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        Content = root;
    }

    public void SetContent(string title, string content, Action? onClear, IReadOnlyList<(string Label, Action Do)>? actions)
    {
        Title = title;
        _text.Text = content;
        _onClear = onClear;
        _clear.IsVisible = onClear is not null;
        SetActions(actions);
    }

    private void SetActions(IReadOnlyList<(string Label, Action Do)>? actions)
    {
        _extras.Children.Clear();
        if (actions is null) return;
        foreach (var (label, act) in actions) _extras.Children.Add(RoadUi.Btn(label, act, 132));
    }

    private static TransportResultWindow? _instance;

    /// <summary>非模态弹出（单例复用）。</summary>
    public static void ShowResult(Window owner, string title, string content, Action? onClear = null, IReadOnlyList<(string Label, Action Do)>? actions = null)
    {
        if (_instance != null) { _instance.SetContent(title, content, onClear, actions); _instance.Activate(); return; }
        var w = new TransportResultWindow(title, content, onClear, actions);
        w.Closed += (_, _) => { if (ReferenceEquals(_instance, w)) _instance = null; };
        _instance = w;
        w.Show(owner);
    }
}

/// <summary>
/// 「分色显示」窗：读「演化对比」最近一次会话结果，勾选类别（延拓/截短/废除/移位/保持）→ 上图 / 清除。
/// 忠实原 EvolutionDisplayWindow。
/// </summary>
internal sealed class EvolutionDisplayWindow : Window
{
    private readonly Func<RoadEvolutionResult?> _getResult;
    private readonly Action<RoadEvolutionResult, ISet<RoadEvolutionClass>> _draw;
    private readonly Action _clear;
    private readonly CheckBox _cbExtend, _cbShorten, _cbAbolish, _cbShift, _cbKeep;
    private readonly TextBlock _summary;

    public EvolutionDisplayWindow(Func<RoadEvolutionResult?> getResult, Action<RoadEvolutionResult, ISet<RoadEvolutionClass>> draw, Action clear)
    {
        _getResult = getResult; _draw = draw; _clear = clear;
        Title = "分色显示（演化 overlay）";
        RoadUi.Place(this, 360);
        _cbExtend = MakeCheck("延拓（绿＋）", Color.FromRgb(40, 200, 90));
        _cbShorten = MakeCheck("截短（红橙×）", Color.FromRgb(235, 120, 70));
        _cbAbolish = MakeCheck("已废除（红×）", Color.FromRgb(216, 90, 48));
        _cbShift = MakeCheck("移位（橙◇）", Color.FromRgb(240, 150, 40));
        _cbKeep = MakeCheck("保持（灰细线）", Color.FromRgb(140, 150, 160));
        var classBox = new StackPanel { Margin = new Thickness(14, 12, 14, 6) };
        classBox.Children.Add(RoadUi.Text("显示哪几类：", 13, bold: true));
        classBox.Children.Add(_cbExtend); classBox.Children.Add(_cbShorten); classBox.Children.Add(_cbAbolish); classBox.Children.Add(_cbShift); classBox.Children.Add(_cbKeep);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 4, 14, 6) };
        tools.Children.Add(RoadUi.Btn("上图 / 刷新", () => _ = Redraw(), 86, bold: true, primary: true));
        tools.Children.Add(RoadUi.Btn("清除", () => { _clear(); _summary.Text = "已清除 overlay。"; }, 86));
        tools.Children.Add(RoadUi.Btn("关闭", Close, 86));
        _summary = RoadUi.Text("勾选类别 → 上图。结果来自「演化对比」；若为空请先运行它。", 12.5);
        _summary.Margin = new Thickness(14, 4, 14, 10);
        var panel = new StackPanel();
        panel.Children.Add(classBox); panel.Children.Add(tools); panel.Children.Add(_summary);
        Content = panel;
    }

    private async Task Redraw()
    {
        var result = _getResult();
        if (result is null || result.Routes.Count == 0) { await RoadUi.Info(this, "分色显示", "尚无演化结果。请先用「演化对比」选两期识别。"); return; }
        var show = new HashSet<RoadEvolutionClass>();
        if (_cbExtend.IsChecked == true) show.Add(RoadEvolutionClass.Extend);
        if (_cbShorten.IsChecked == true) show.Add(RoadEvolutionClass.Shorten);
        if (_cbAbolish.IsChecked == true) show.Add(RoadEvolutionClass.Abolish);
        if (_cbShift.IsChecked == true) show.Add(RoadEvolutionClass.Shift);
        if (_cbKeep.IsChecked == true) show.Add(RoadEvolutionClass.Keep);
        if (show.Count == 0) { _clear(); _summary.Text = "未勾选任何类别，已清空 overlay。"; return; }
        _draw(result, show);
        _summary.Text = $"已上图：{result.Summary}（仅显示勾选类别）。";
    }

    private static CheckBox MakeCheck(string text, Color swatch)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new Border { Width = 14, Height = 14, Background = new SolidColorBrush(swatch), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(RoadUi.Text(text, 12.5, false));
        return new CheckBox { Content = sp, IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
    }
}

/// <summary>
/// 「把候选卸点存进台账」模态窗：逐条补上身份（名称 / 子类 / 挂哪个排土场 / 吞吐），再落库。忠实原 SaveSinkCandidatesDialog。
/// </summary>
internal sealed class SaveSinkCandidatesDialog : Window
{
    public sealed class Row
    {
        public required string CandidateId { get; init; }
        public required double X { get; init; }
        public required double Y { get; init; }
        public required double Z { get; init; }
        public string Name { get; set; } = "";
    }

    public IReadOnlyList<SinkSpec> Result { get; private set; } = Array.Empty<SinkSpec>();
    private static readonly (string Text, string Code)[] Subs = { ("排土场", "dump"), ("破碎站", "crusher"), ("储矿场", "stockpile") };
    private readonly List<(Row R, TextBox Name, ComboBox Sub, ComboBox Site, TextBox Tph)> _rows = new();
    private readonly IReadOnlyList<(string Id, string Name)> _dumpSites;

    public SaveSinkCandidatesDialog(IReadOnlyList<Row> candidates, IReadOnlyList<(string Id, string Name)> dumpSites)
    {
        _dumpSites = dumpSites;
        Title = "把候选卸点存进台账";
        RoadUi.Place(this, 780);
        MaxHeight = 620;
        var head = RoadUi.Hint("候选只活在本次比选里，不算数。要让它成为真卸载点（进台账、进路网、被运距报表统计），得先补上身份：叫什么、是哪一类、对应库里哪个排土场、能力多少。", 12);
        head.Margin = new Thickness(0, 0, 0, 10);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6), ColumnDefinitions = new ColumnDefinitions("130,160,100,180,96") };
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        string[] headers = { "候选点", "名称（台账里的名字）", "类别", "挂接排土场档案", "吞吐 t/h" };
        for (int c = 0; c < headers.Length; c++)
        {
            var t = RoadUi.Text(headers[c], 12, false, true); t.Margin = new Thickness(2, 0, 6, 4);
            Grid.SetColumn(t, c); grid.Children.Add(t);
        }
        int r = 1;
        foreach (var cand in candidates)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var lbl = new TextBlock { Text = cand.CandidateId, FontSize = 11, FontFamily = RoadUi.Mono, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 2, 6, 2) };
            var name = new TextBox { Text = cand.Name, Height = 26, Margin = new Thickness(2), FontSize = 12 };
            var sub = new ComboBox { Height = 26, Margin = new Thickness(2), FontSize = 12 };
            foreach (var (text, _) in Subs) sub.Items.Add(text);
            sub.SelectedIndex = 0;
            var site = new ComboBox { Height = 26, Margin = new Thickness(2), FontSize = 12 };
            site.Items.Add("（不挂）");
            foreach (var (id, nm) in dumpSites) site.Items.Add($"{id} · {nm}");
            site.SelectedIndex = 0;
            var tph = new TextBox { Text = "0", Height = 26, Margin = new Thickness(2), FontSize = 12 };
            site.SelectionChanged += (_, _) =>
            {
                int i = site.SelectedIndex - 1;
                if (i >= 0 && i < dumpSites.Count && string.IsNullOrWhiteSpace(name.Text)) name.Text = dumpSites[i].Name;
            };
            foreach (var (ctl, col) in new (Control, int)[] { (lbl, 0), (name, 1), (sub, 2), (site, 3), (tph, 4) })
            { Grid.SetRow(ctl, r); Grid.SetColumn(ctl, col); grid.Children.Add(ctl); }
            _rows.Add((cand, name, sub, site, tph));
            r++;
        }
        var hint = RoadUi.Hint("存完会立刻按坐标接进当前路网（投影到最近道路 + 一条接入支线）。附近 80m 内没有路的会落成孤立点并报出来 —— 那种点寻径过不去，位置要重指。\n只增不删：同名的更新坐标，其余台账记录一条都不动。", 11);
        hint.Margin = new Thickness(0, 6, 0, 0);
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(head);
        root.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 380 });
        root.Children.Add(hint);
        root.Children.Add(RoadUi.Foot(RoadUi.Btn("存进台账", () => _ = OnOk(), 100, primary: true), RoadUi.Btn("取消", () => Close(false), 80)));
        Content = root;
    }

    private async Task OnOk()
    {
        var specs = new List<SinkSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, name, sub, site, tph) in _rows)
        {
            string nm = (name.Text ?? "").Trim();
            if (nm.Length == 0) { await RoadUi.Info(this, Title, $"{row.CandidateId} 还没起名字。台账里的卸载点是按名字认的，不能空。"); name.Focus(); return; }
            if (!seen.Add(nm)) { await RoadUi.Info(this, Title, $"名称「{nm}」重复了。台账按名字匹配，重名会互相覆盖。"); name.Focus(); return; }
            if (!double.TryParse((tph.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double t) || t < 0)
            { await RoadUi.Info(this, Title, $"「{nm}」的吞吐填得不对（要一个 ≥0 的数；不知道就填 0）。"); tph.Focus(); return; }
            int si = site.SelectedIndex - 1;
            specs.Add(new SinkSpec(nm, row.X, row.Y, row.Z, Subs[Math.Clamp(sub.SelectedIndex, 0, Subs.Length - 1)].Code, t,
                si >= 0 && si < _dumpSites.Count ? _dumpSites[si].Id : null));
        }
        Result = specs;
        Close(true);
    }
}
