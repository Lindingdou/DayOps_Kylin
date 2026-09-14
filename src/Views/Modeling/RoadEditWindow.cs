using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>编辑面板的动作回调（由主窗接线：取点 + 改会话 + 重绘 diff）。忠实原 RoadEditHandlers。</summary>
internal sealed class RoadEditHandlers
{
    public Action? AddEdge;
    public Action? RemoveEdge;
    public Action? SetStatus;
    public Action? SplitEdge;
    public Action? SetRoadClass;
    public Action? Undo;
    public Action? Clear;
    public Action? Commit;
}

/// <summary>
/// 「路网编辑」非模态面板（暂存式交互编辑，忠实原 RoadEditWindow）：选工具 → 在视口连续编辑 → 变更进会话（不动当前网）→
/// 「更新路网」锁定提交。本窗只管 UI 与工具切换，取点/改图/重绘 diff 全由主窗回调实现。关窗即退出编辑。
/// </summary>
internal sealed class RoadEditWindow : Window
{
    private readonly RoadEditSession _session;
    private readonly RoadEditHandlers _h;
    private readonly ObservableCollection<string> _ops = new();
    private readonly TextBlock _summary;
    private readonly Button _btnUndo, _btnClear, _btnCommit;

    /// <summary>关窗退出编辑（含放弃未提交变更）时通知主窗清理。</summary>
    public event Action? Exited;

    internal RoadEditWindow(RoadEditSession session, RoadEditHandlers handlers)
    {
        _session = session; _h = handlers;
        Title = "路网编辑（暂存 · 更新路网后锁定）";
        Width = 400; Height = 640;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowFit.ClampToScreen(this);

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = _session.BaseArchiveName is null ? "基准：当前图上路网" : $"基准：{_session.BaseArchiveName}", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 2) });
        panel.Children.Add(new TextBlock { Text = "在视口连续编辑，变更先暂存、不动当前网；点「更新路网」才锁定。", TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 10) });

        panel.Children.Add(SectionLabel("编辑工具"));
        var tools = new WrapPanel { Margin = new Thickness(0, 2, 0, 8) };
        tools.Children.Add(Tool("加边", () => _h.AddEdge?.Invoke()));
        tools.Children.Add(Tool("删边", () => _h.RemoveEdge?.Invoke()));
        tools.Children.Add(Tool("插交叉口", () => _h.SplitEdge?.Invoke()));
        tools.Children.Add(Tool("改状态", () => _h.SetStatus?.Invoke()));
        tools.Children.Add(Tool("线路类型", () => _h.SetRoadClass?.Invoke()));
        panel.Children.Add(tools);
        panel.Children.Add(new TextBlock
        {
            Text = "「线路类型」点一条路段循环 自动→干线→支线→孤立段→自动。自动判据只看两端接没接上，答不了这条路重不重要 —— 主运输坡道尽头停在工作面，拓扑上就是一端悬空，该手工改成干线。",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(SectionLabel("变更清单"));
        var list = new ListBox { ItemsSource = _ops, Height = 170, FontFamily = new FontFamily("Consolas, Courier New, monospace"), FontSize = 12, Margin = new Thickness(0, 2, 0, 6) };
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>()) { Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(8, 2)), new Setter(ListBoxItem.MinHeightProperty, 24.0) } });
        panel.Children.Add(list);

        var editBtns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        _btnUndo = SmallBtn("撤销上一步", () => _h.Undo?.Invoke());
        _btnClear = SmallBtn("清空", () => _h.Clear?.Invoke());
        editBtns.Children.Add(_btnUndo); editBtns.Children.Add(_btnClear);
        panel.Children.Add(editBtns);

        _summary = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(_summary);

        // 页脚 Dock 到底：摘要再长也顶不掉「更新路网 / 退出编辑」
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 6, 12, 10) };
        _btnCommit = new Button { Content = "更新路网…", MinWidth = 100, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 8, 0) };
        _btnCommit.Click += (_, _) => _h.Commit?.Invoke();
        var exit = new Button { Content = "退出编辑", MinWidth = 80 };
        exit.Click += (_, _) => Close();
        bottom.Children.Add(_btnCommit); bottom.Children.Add(exit);

        var root = new DockPanel();
        DockPanel.SetDock(bottom, Avalonia.Controls.Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(new ScrollViewer { Content = panel });
        Content = root;
        Closed += (_, _) => Exited?.Invoke();
        RefreshFromSession();
    }

    /// <summary>从会话重读变更清单 + 摘要（主窗每次改完会话后调用）。</summary>
    internal void RefreshFromSession()
    {
        _ops.Clear();
        int i = 1;
        foreach (var c in _session.Journal)
        {
            string tag = string.IsNullOrEmpty(c.Title) ? c.KindText : $"{c.KindText} {c.Title}";
            _ops.Add($"{i,2}. {tag}");
            i++;
        }
        var diff = _session.ComputeDiff();
        var topo = _session.DraftTopology();
        _summary.Text = $"暂存：+{diff.Added.Count} 边 / −{diff.Removed.Count} 边 / {diff.StatusChanged.Count} 改状态 / {diff.Modified.Count} 改线 / {diff.ClassChanged.Count} 改类型\n草稿：{topo.Summary}";
        string structural = _session.TopologyDelta();
        string passable = _session.TopologyDelta(passableOnly: true);
        if (structural.Length > 0) _summary.Text += $"\n变化（结构）：{structural}";
        if (passable.Length > 0) _summary.Text += $"\n变化（可通行）：{passable}";
        _btnUndo.IsEnabled = _session.CanUndo;
        _btnClear.IsEnabled = _session.HasChanges;
        _btnCommit.IsEnabled = _session.HasChanges;
    }

    internal string SummaryText => _summary.Text ?? "";

    private static TextBlock SectionLabel(string t) => new() { Text = t, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, 0) };
    private static Button Tool(string text, Action onClick)
    {
        var b = new Button { Content = text, MinWidth = 76, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3) };
        b.Click += (_, _) => onClick();
        return b;
    }
    private static Button SmallBtn(string text, Action onClick)
    {
        var b = new Button { Content = text, MinWidth = 96, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 2) };
        b.Click += (_, _) => onClick();
        return b;
    }
}
