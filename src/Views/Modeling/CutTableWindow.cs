using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>累积量表窗口要主窗做的事（画台阶面 / 卡阶段块 / 清除 / 隐藏块体 / 缩放）。</summary>
internal sealed class CutTableHandlers
{
    /// <summary>画某刀的台阶面（推进 a，标注文字），整层替换。</summary>
    public Action<double, string>? DrawFace;
    /// <summary>卡阶段：两端台阶面 + 隔离显示这段的块（showCoal/showRock 过滤）。返回 (阶段煤 m³, 阶段岩 m³, 煤块数, 岩块数)。</summary>
    public Func<double, double, double, double, bool, bool, (double coalVol, double rockVol, int coalN, int rockN)>? Stage;
    /// <summary>清台阶面/模板预览层 + 解除隔离。返回清掉的图元数。</summary>
    public Func<int>? ClearFaces;
    public Action<bool>? HideBlocks;
    public Action? Zoom;
    /// <summary>解除隔离（恢复整模型显示）。</summary>
    public Action? RestoreIsolation;
    public Action<string, bool>? Echo;
}

/// <summary>
/// 「切到最后一刀」的累积量表窗口（非模态，忠实原 <c>CutTableWindow</c>）：逐刀列出煤/岩/累积/累积剥采比；
/// 点任意一刀 → 画该刀的分台阶切割台阶面（台阶高度 + 最小工作平盘 + 坡面角 α 的阶梯坡，从最下标高到最上标高）；
/// 起~止刀号「卡阶段块」→ 卡出这两刀之间（台阶面约束）的阶段块，分阶段煤/阶段岩显示 + 统计阶段量；可隐藏全部块体单看。
/// 台阶面 / 阶段块 / 累积量表同一推进坐标（s = colA0 + 台阶退距），口径一致。
/// </summary>
internal sealed class CutTableWindow : Window
{
    internal sealed class CutRow
    {
        public int Index { get; init; }
        public double Advance { get; init; }
        public string AdvanceTo { get; init; } = "";
        public string CoalWanT { get; init; } = "";
        public string RockWanM3 { get; init; } = "";
        public string CumCoalWanT { get; init; } = "";
        public string CumRockWanM3 { get; init; } = "";
        public string StripRatio { get; init; } = "";
    }

    private readonly CutTableHandlers _h;
    private readonly List<CutRow> _rows;
    private readonly double _sliceWidth, _density;
    private readonly DataGrid _grid = new() { AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, CanUserSortColumns = false };
    private readonly TextBox _txtFaceCut = new() { Width = 52 }, _txtStageFrom = new() { Width = 52 }, _txtStageTo = new() { Width = 52 };
    private readonly CheckBox _chkShowCoal = new() { Content = "阶段煤", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly CheckBox _chkShowRock = new() { Content = "阶段岩", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly ToggleButton _btnHideBlocks = new() { Content = "隐藏全部块体", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3) };
    private readonly TextBlock _txtStage = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 2, 0) };
    private CutRow? _lastLo, _lastHi;

    private static TextBlock Head(string t) => new() { Text = t, TextTrimming = TextTrimming.None, TextWrapping = TextWrapping.NoWrap };

    internal CutTableWindow(DriveOutput drv, double alphaDeg, double benchStepHeight, double minBermWidth, CutTableHandlers h)
    {
        _h = h;
        _sliceWidth = drv.SliceWidth > 0 ? drv.SliceWidth : 10;
        _density = drv.CoalDensity > 0 ? drv.CoalDensity : 2.5;
        Title = "刀量切割 · 累积量表（切到最后一刀）";
        Width = 900; Height = 640;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowFit.ClampToScreen(this);

        _rows = drv.Cuts.Select(c => new CutRow
        {
            Index = c.Index, Advance = c.AdvanceTo,
            AdvanceTo = c.AdvanceTo.ToString("0.#", CultureInfo.InvariantCulture),
            CoalWanT = (c.CoalVolM3 * _density / 1e4).ToString("0.00", CultureInfo.InvariantCulture),
            RockWanM3 = (c.RockVolM3 / 1e4).ToString("0.00", CultureInfo.InvariantCulture),
            CumCoalWanT = (c.CumCoalVolM3 * _density / 1e4).ToString("0.0", CultureInfo.InvariantCulture),
            CumRockWanM3 = (c.CumRockVolM3 / 1e4).ToString("0.0", CultureInfo.InvariantCulture),
            StripRatio = c.StripRatioCum.ToString("0.00", CultureInfo.InvariantCulture),
        }).ToList();

        DataGridTextColumn C(string hd, string p, double w) => new() { Header = Head(hd), Binding = new Binding(p), Width = new DataGridLength(w) };
        _grid.Columns.Add(C("刀#", nameof(CutRow.Index), 60));
        _grid.Columns.Add(C("推进至(m)", nameof(CutRow.AdvanceTo), 96));
        _grid.Columns.Add(new DataGridTextColumn { Header = Head("本刀煤(万t)"), Binding = new Binding(nameof(CutRow.CoalWanT)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = Head("本刀岩(万m³)"), Binding = new Binding(nameof(CutRow.RockWanM3)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = Head("累积煤(万t)"), Binding = new Binding(nameof(CutRow.CumCoalWanT)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = Head("累积岩(万m³)"), Binding = new Binding(nameof(CutRow.CumRockWanM3)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(C("累积剥采比", nameof(CutRow.StripRatio), 100));
        _grid.ItemsSource = _rows;
        _grid.SelectionChanged += (_, _) => { if (_grid.SelectedItem is CutRow row) { _txtFaceCut.Text = row.Index.ToString(CultureInfo.InvariantCulture); DrawFace(row); } };

        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(2, 0, 2, 8),
            Text = $"{drv.Provenance} ｜ 共 {drv.Cuts.Count} 刀(刀距 {drv.SliceWidth:0.#}m) ｜ 总推进 {drv.TotalAdvance:0.#}m ｜ 煤 {drv.TotalCoalTonnage / 1e4:0.0}万t ｜ 岩 {drv.TotalRockVolM3 / 1e4:0.0}万m³ ｜ 综合剥采比 {drv.OverallStripRatio:0.00} ｜ 标高 {drv.MinZ:0.#}~{drv.MaxZ:0.#}m ｜ 台阶 {benchStepHeight:0.#}m/平盘 {minBermWidth:0.#}m · α={alphaDeg:0.#}°",
        };
        var bar = new WrapPanel();
        _btnHideBlocks.IsCheckedChanged += (_, _) => { bool hide = _btnHideBlocks.IsChecked == true; _h.HideBlocks?.Invoke(hide); _btnHideBlocks.Content = hide ? "显示全部块体" : "隐藏全部块体"; };
        bar.Children.Add(_btnHideBlocks);
        bar.Children.Add(Btn("清除", ClearFaces, 56));
        bar.Children.Add(Btn("缩放", () => _h.Zoom?.Invoke(), 56));
        bar.Children.Add(_chkShowCoal); bar.Children.Add(_chkShowRock);
        _chkShowCoal.IsCheckedChanged += (_, _) => { if (_lastLo != null && _lastHi != null) RenderStage(_lastLo, _lastHi); };
        _chkShowRock.IsCheckedChanged += (_, _) => { if (_lastLo != null && _lastHi != null) RenderStage(_lastLo, _lastHi); };
        bar.Children.Add(Lbl("刀号")); bar.Children.Add(_txtFaceCut);
        bar.Children.Add(Btn("显示台阶面", () => { var row = FindRow(_txtFaceCut.Text); if (row == null) { Echo($"没有第 {_txtFaceCut.Text} 刀(范围 1~{_rows.Count})。", true); return; } _grid.SelectedItem = row; _grid.ScrollIntoView(row, null); DrawFace(row); }, 88));
        bar.Children.Add(Lbl("　起刀号")); bar.Children.Add(_txtStageFrom); bar.Children.Add(Lbl("~")); bar.Children.Add(_txtStageTo);
        bar.Children.Add(Btn("卡阶段块", () =>
        {
            var lo = FindRow(_txtStageFrom.Text); var hi = FindRow(_txtStageTo.Text);
            if (lo == null || hi == null) { Echo($"请填有效的起/止刀号(1~{_rows.Count})。", true); return; }
            if (lo.Index > hi.Index) (lo, hi) = (hi, lo);
            _lastLo = lo; _lastHi = hi;
            RenderStage(lo, hi);
        }, 88));

        var hint = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#5B6B80"), Margin = new Thickness(2, 8, 2, 0), Text = "点表格任意一行 = 显示该刀的分台阶切割台阶面（图层「刀量切割_台阶面」）；起~止刀号「卡阶段块」= 只显示两刀之间（台阶面约束）的块并统计阶段量；「清除」清台阶面并恢复整模型。" };

        var top = new StackPanel { Margin = new Thickness(12, 10, 12, 4), Children = { summary, bar, _txtStage } };
        var root = new DockPanel();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(hint, Avalonia.Controls.Dock.Bottom);
        hint.Margin = new Thickness(12, 4, 12, 10);
        root.Children.Add(top); root.Children.Add(hint);
        root.Children.Add(new Border { Margin = new Thickness(12, 0), BorderBrush = Brush.Parse("#D5DBE3"), BorderThickness = new Thickness(1), Child = _grid });
        Content = root;
        Closed += (_, _) => { _h.RestoreIsolation?.Invoke(); if (_btnHideBlocks.IsChecked == true) _h.HideBlocks?.Invoke(false); };
    }

    private static Button Btn(string t, Action a, double minW) { var b = new Button { Content = t, MinWidth = minW, Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(8, 3) }; b.Click += (_, _) => a(); return b; }
    private static TextBlock Lbl(string t) => new() { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0), FontSize = 12 };
    private void Echo(string msg, bool warn) => _h.Echo?.Invoke(msg, warn);

    private CutRow? FindRow(string? text)
    {
        if (!int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) return null;
        return _rows.FirstOrDefault(r => r.Index == n);
    }

    private void DrawFace(CutRow row)
    {
        try
        {
            _h.DrawFace?.Invoke(row.Advance, $"刀{row.Index}");
            _lastLo = _lastHi = null;
            _h.RestoreIsolation?.Invoke();
            Echo($"已显示 刀{row.Index} 台阶面(推进 {row.Advance:0.#}m)。", false);
        }
        catch (Exception ex) { Echo($"画台阶面失败: {ex.Message}", true); }
    }

    private void RenderStage(CutRow lo, CutRow hi)
    {
        double a0 = lo.Advance - _sliceWidth, a1 = hi.Advance;
        try
        {
            var r = _h.Stage?.Invoke(lo.Advance, hi.Advance, a0, a1, _chkShowCoal.IsChecked == true, _chkShowRock.IsChecked == true) ?? (0, 0, 0, 0);
            bool empty = r.coalN + r.rockN == 0;
            double coalT = r.coalVol * _density;
            double ratio = coalT > 1e-6 ? r.rockVol / coalT : 0;
            _txtStage.Text = (empty ? "⚠ 区间内未卡到任何块体(检查刀号范围 / 工作线是否压在矿体上)！ " : "")
                + $"卡阶段 刀{lo.Index}~{hi.Index}(推进 {a0:0.#}→{a1:0.#}m)：阶段煤 {coalT / 1e4:0.0}万t({r.coalVol / 1e4:0.0}万m³) ｜ 阶段岩 {r.rockVol / 1e4:0.0}万m³ ｜ 阶段剥采比 {ratio:0.00} ｜ 煤 {r.coalN:N0} 块 / 岩 {r.rockN:N0} 块"
                + (empty ? "" : "（已隔离显示这段块体，可三维浏览）");
            Echo(_txtStage.Text, empty);
        }
        catch (Exception ex) { Echo($"卡阶段块失败: {ex.Message}", true); }
    }

    private void ClearFaces()
    {
        int n = _h.ClearFaces?.Invoke() ?? 0;
        _grid.SelectedItem = null;
        _lastLo = _lastHi = null;
        _txtStage.Text = "";
        Echo($"已清除 {n} 个台阶面/模板图元;隔离已解除,整模型块体恢复显示。", false);
    }

    internal int RowCount => _rows.Count;
    internal string StageText => _txtStage.Text ?? "";
    internal void SelftestFace(int idx) { var r = _rows.FirstOrDefault(x => x.Index == idx); if (r != null) DrawFace(r); }
    internal void SelftestStage(int lo, int hi) { var a = _rows.FirstOrDefault(x => x.Index == lo); var b = _rows.FirstOrDefault(x => x.Index == hi); if (a != null && b != null) { _lastLo = a; _lastHi = b; RenderStage(a, b); } }
}
