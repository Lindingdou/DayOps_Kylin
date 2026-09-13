using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Cad.Units;             // MineUnit / UnitKind
using PitMine3D.Kylin.Views.Road;
using WorkingFace = PitMine3D.Kylin.Cad.Plan.WorkingFace;

namespace PitMine3D.Kylin.Views.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  归属覆盖 —— 把 FaceUnitResolver 拒绝配的那些单元交给人来定（移植原 PlanLib.ShortTerm.FaceAttributionDialog）。
//  ★ 只列【解算器没配上的】那些单元，不列全部。纯代码窗。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>归属覆盖窗口。<b>只处理解算器配不上的单元</b>。</summary>
internal sealed class FaceAttributionDialog : Window
{
    /// <summary>表里一行 = 一个待定单元。</summary>
    public sealed class Row
    {
        public string UnitId { get; init; } = "";
        public string Why { get; init; } = "";
        public string KindText { get; init; } = "";
        public string ZText { get; init; } = "";
        public double InSituWanM3 { get; init; }
        /// <summary>人选的面名。空 = 仍然不配（<b>合法</b>：不知道就别选，别硬凑）。</summary>
        public string FaceName { get; set; } = "";
    }

    private readonly ObservableCollection<Row> _rows = new();
    private readonly Dictionary<string, string> _store;
    private readonly DataGrid _grid = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Opacity = 0.85, VerticalAlignment = VerticalAlignment.Center };
    public bool DialogResult { get; private set; }

    public FaceAttributionDialog(FaceUnitResolution resolution, IEnumerable<MineUnit>? units, IEnumerable<WorkingFace>? faces, Dictionary<string, string> store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Title = "归属覆盖 — 解算器配不上的单元，由人来定";
        PlanUi.Place(this, 940, 560);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RoadUi.Theme(this, BackgroundProperty, "Theme.Window.Background");

        var faceNames = (faces ?? Enumerable.Empty<WorkingFace>())
                        .Where(f => f != null && !string.IsNullOrWhiteSpace(f.Name))
                        .Select(f => f.Name).Distinct(StringComparer.Ordinal).ToList();
        // 首项留空 = 仍然不配。没有它，人一旦点开下拉就再也退不回"我不知道"。
        var pickList = new List<string> { "" };
        pickList.AddRange(faceNames);

        var byId = new Dictionary<string, MineUnit>(StringComparer.Ordinal);
        foreach (var u in units ?? Enumerable.Empty<MineUnit>())
            if (u != null && u.UnitId.Length > 0) byId[u.UnitId] = u;

        foreach (var (id, why, tag) in Pending(resolution))
        {
            if (id.Length == 0) continue;
            byId.TryGetValue(id, out var u);
            _rows.Add(new Row
            {
                UnitId = id, Why = tag + "：" + why,
                KindText = u == null ? "—" : (u.IsCoal ? "煤" : "岩"),
                ZText = u == null ? "—" : $"{u.ZLo:0}~{u.ZHi:0}m",
                InSituWanM3 = u == null ? 0 : u.InSituM3 / 1e4,
                FaceName = store.TryGetValue(id, out string? had) ? had : "",
            });
        }

        BuildUi(pickList);
        UpdateStatus();
    }

    /// <summary>歧义 + 未匹配，合成一张待定清单。单元号取解算器<b>单独给的那两张 Id 表</b>。</summary>
    private static IEnumerable<(string Id, string Why, string Tag)> Pending(FaceUnitResolution r)
    {
        if (r == null) yield break;
        for (int i = 0; i < r.AmbiguousIds.Count; i++)
            yield return (r.AmbiguousIds[i], i < r.Ambiguous.Count ? r.Ambiguous[i] : "", "歧义");
        for (int i = 0; i < r.UnmatchedIds.Count; i++)
            yield return (r.UnmatchedIds[i], i < r.Unmatched.Count ? r.Unmatched[i] : "", "没命中");
    }

    private void BuildUi(List<string> pickList)
    {
        var root = new DockPanel { Margin = new Thickness(12) };

        var intro = new TextBlock
        {
            Text = "这里只列【解算器配不上的】单元 —— 自动配上的不在此处，也不该在这里改（改了规则就再也校验不出对错）。\n"
                 + "留空 = 仍然不配，这是合法选择：不知道就别选，硬凑一个面比不配更糟 —— 配错的单元会带着错误的设备型号与穿爆工艺去排产，而报表上完全正常。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Opacity = 0.85,
        };
        DockPanel.SetDock(intro, Avalonia.Controls.Dock.Top); root.Children.Add(intro);

        var barDock = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = RoadUi.Btn("确定", OnOk, 92); ok.IsDefault = true; right.Children.Add(ok);
        var cancel = RoadUi.Btn("取消", () => { DialogResult = false; Close(false); }, 92); cancel.IsCancel = true; cancel.Margin = new Thickness(0); right.Children.Add(cancel);
        DockPanel.SetDock(right, Avalonia.Controls.Dock.Right); barDock.Children.Add(right);
        var bar = new DockPanel();
        var clear = RoadUi.Btn("清空全部覆盖", () => { foreach (var r in _rows) r.FaceName = ""; RefreshGrid(); UpdateStatus(); }, 92);
        DockPanel.SetDock(clear, Avalonia.Controls.Dock.Left); bar.Children.Add(clear); bar.Children.Add(_status);
        barDock.Children.Add(bar);
        DockPanel.SetDock(barDock, Avalonia.Controls.Dock.Bottom); root.Children.Add(barDock);

        _grid.AutoGenerateColumns = false; _grid.HeadersVisibility = DataGridHeadersVisibility.Column; _grid.FontSize = 12.5;
        _grid.Columns.Add(new DataGridTextColumn { Header = "单元号", Binding = new Binding(nameof(Row.UnitId)), Width = new DataGridLength(130), IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "类别", Binding = new Binding(nameof(Row.KindText)), Width = new DataGridLength(46), IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "标高", Binding = new Binding(nameof(Row.ZText)), Width = new DataGridLength(92), IsReadOnly = true });
        _grid.Columns.Add(new DataGridTextColumn { Header = "量(万m³)", Binding = new Binding(nameof(Row.InSituWanM3)) { StringFormat = "{0:0.00}" }, Width = new DataGridLength(72), IsReadOnly = true });
        _grid.Columns.Add(new DataGridTemplateColumn
        {
            Header = "归到哪个面（留空=不配）", Width = new DataGridLength(190), IsReadOnly = true,
            CellTemplate = new FuncDataTemplate<Row>((_, _) =>
            {
                var cb = new ComboBox { ItemsSource = pickList, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 0, Padding = new Thickness(6, 2) };
                void Sync() { if (cb.DataContext is Row r) cb.SelectedItem = pickList.Contains(r.FaceName) ? r.FaceName : ""; }
                cb.DataContextChanged += (_, _) => Sync();
                cb.SelectionChanged += (_, _) => { if (cb.DataContext is Row r && cb.SelectedItem is string s && s != r.FaceName) { r.FaceName = s; UpdateStatus(); } };
                Sync();
                return cb;
            }),
        });
        _grid.Columns.Add(new DataGridTextColumn { Header = "为什么没配上", Binding = new Binding(nameof(Row.Why)), Width = new DataGridLength(1, DataGridLengthUnitType.Star), IsReadOnly = true });
        PlanUi.FitHeaders(_grid);
        _grid.ItemsSource = _rows;
        root.Children.Add(_grid);

        Content = root;
    }

    private void RefreshGrid() { _grid.ItemsSource = null; _grid.ItemsSource = _rows; }

    private void UpdateStatus()
    {
        int n = _rows.Count(r => !string.IsNullOrWhiteSpace(r.FaceName));
        double wan = _rows.Where(r => !string.IsNullOrWhiteSpace(r.FaceName)).Sum(r => r.InSituWanM3);
        double left = _rows.Where(r => string.IsNullOrWhiteSpace(r.FaceName)).Sum(r => r.InSituWanM3);
        _status.Text = _rows.Count == 0
            ? "没有待定的单元 —— 解算器这一轮全配上了。"
            : $"待定 {_rows.Count} 个 · 已指定 {n} 个（{wan:0.00} 万m³）· 仍不配 {_rows.Count - n} 个（{left:0.00} 万m³，这些单元不受面级约束）";
    }

    private void OnOk()
    {
        // 就地改覆盖表：选了的写进去、清空的删掉。★ 只动本次列出的这些单元。
        foreach (var r in _rows)
        {
            if (string.IsNullOrWhiteSpace(r.FaceName)) _store.Remove(r.UnitId);
            else _store[r.UnitId] = r.FaceName.Trim();
        }
        DialogResult = true;
        Close(true);
    }

    internal void SelftestPick(int i, string face) { if (i >= 0 && i < _rows.Count) { _rows[i].FaceName = face; UpdateStatus(); } }
    internal int SelftestPendingCount => _rows.Count;
}
