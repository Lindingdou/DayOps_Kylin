using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>⑤ 执行替换要主窗做的事（删原线 / 写回保留段 / 落衔接段），一次落地。</summary>
internal sealed class EpApplyRequest
{
    public ulong[] Delete = Array.Empty<ulong>();
    public List<(string Layer, List<(double X, double Y, double Z)> Pts, ulong FromHandle)> Keep = new();
    public IReadOnlyList<EpConnector> Connectors = Array.Empty<EpConnector>();
    public EpScene Scene = new();
    public double GradePct;
}

/// <summary>
/// 「创建工程位置」④⑤：**替换台阶线（模板影响区域）** —— 忠实移植原窗口的 ④预览替换 / ⑤执行替换 / 覆盖范围台账 / 改判 / 断口建端帮节点。
///
/// 这一阶段覆盖到的老台阶线由新台阶线取代，跨界的裁到覆盖边界、范围外那几段（=端帮）原样留下。几何在 <see cref="BenchLineReplacer"/>。
/// 影响区域 = 工作线坐标系下【推进区间 × 走向区间】两个一维区间的交，两头都由模板自己定。
///
/// ── Kylin 侧登记的差异 ──
/// <list type="bullet">
///   <item>「读交接单」：驱动量这一跑留下的进程内一次性对象（<see cref="EngineeringPositionHandoff.Latest"/>，带工作线 / 前界 / 每一级的标高与身份）；没有则退回图层解析、标高格从图上新台阶线反推。</item>
///   <item>工作线几何：原版由内核 <c>GetWorkLineGeometryByHandle</c> 给逐段推进方向；Kylin 由「创建工作线」落图的 基线/结束线/回转中心 反算（<see cref="WorkLineSamples.FromWorkLine"/>）。</item>
///   <item>覆盖范围环 / 要删 / 要留 / 归不进级 在平面图上着色（原版最终档的平面拾取图未画这几层，但状态栏文案承诺了"红=要删、蓝=要留、黄=归不进级、琥珀虚线=覆盖范围"，按文案画）。</item>
/// </list>
/// </summary>
internal sealed partial class EngineeringPositionWindow
{
    private readonly List<WorkLineSamples> _workLines = new();
    private readonly List<ulong> _workLineHandles = new();
    private BenchReplacePlan? _plan;
    private readonly Dictionary<ulong, bool> _overrides = new();
    /// <summary>被取消勾选的老台阶线图层（跨次提取记住）。预置「工作线」。</summary>
    private readonly HashSet<string> _uncheckedLayers = new(StringComparer.Ordinal) { "工作线" };

    private readonly TextBlock _txtWorkLine = new() { Text = "未加载", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private readonly TextBlock _txtHandoff = new() { Text = "无交接单", FontSize = 11, Foreground = Brush.Parse("#8A96A8"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    private EpHandoff? _handoff;   // 「驱动量」这一跑的一次性交接单（进程内，不落盘）
    private readonly TextBox _txtMargin = new() { Text = "5", Width = 44 };
    private readonly CheckBox _chkBackToWorkLine = new() { Content = "下界回溯到工作线", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly CheckBox _chkLateralToTemplate = new() { Content = "横向限于模板", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
    private readonly TextBox _txtLatMargin = new() { Text = "", Width = 44, Watermark = "同余量" };
    private readonly CheckBox _chkByLevel = new() { Content = "按标高格逐级替换", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBox _txtLevelTol = new() { Text = "", Width = 44, Watermark = "自动" };
    private readonly Button _btnOldLayers = new() { Content = "老台阶线图层 ▾", Padding = new Thickness(10, 3), Margin = new Thickness(0, 0, 10, 0) };
    private readonly ListBox _lstCover = new() { Background = Brushes.Transparent, BorderThickness = new Thickness(0), IsVisible = false };
    private readonly WrapPanel _pnlCoverActions = new() { IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _txtLedgerHint = new() { Text = "选中后按 Delete 删", FontSize = 10.5, Foreground = Brush.Parse("#8A96A8"), VerticalAlignment = VerticalAlignment.Center };
    private Button? _btnTabLinks, _btnTabCover;

    private static readonly Color CDrop = Color.FromRgb(0xE0, 0x60, 0x5A);
    private static readonly Color CBand = Color.FromRgb(0xF0, 0xC0, 0x76);
    private static readonly Color CUnmatched = Color.FromRgb(0xE6, 0xC8, 0x30);
    private static readonly Color CKeep = Color.FromRgb(0x4D, 0x9B, 0xE8);
    private static readonly Color CSeamRow = Color.FromRgb(0x8A, 0xD1, 0xFA);

    private sealed class CoverRow
    {
        public int Kind;                       // 0=级·已替换 1=级·碰不到 2=老线·归不进级 3=煤层汇总
        public BenchLevelUsage? Level;
        public BenchReplaceItem? Item;
        public BenchSeamTally?   Seam;
    }
    private CoverRow? _coverSel;
    private bool _suppressCoverEvent;

    /// <summary>④ 那一行（放在 ② 行之后）。</summary>
    private Control BuildReplaceRow()
    {
        var row = new WrapPanel { Margin = new Thickness(14, 0, 14, 6) };
        row.Children.Add(T("④ 替换台阶线", 11.5));
        row.Children.Add(B("读交接单", () => TryAdoptHandoff(quiet: false), tip: "读「驱动量」→「生成采区台阶面」留下的交接单：工作线 + 前界 + 覆盖区间 + 标高格 + 每一级的标高与身份（进程内一次性对象，软件重启即无）"));
        row.Children.Add(_txtHandoff);
        row.Children.Add(T("工作线", 11.5));
        row.Children.Add(B("自动查找", FindWorkLines, tip: "「工作线」图层上的全部工作线 + 当前选中的"));
        row.Children.Add(B("用选中的", LoadSelectedWorkLines, tip: "只用视口里选中的工作线"));
        row.Children.Add(_txtWorkLine);
        row.Children.Add(T("覆盖余量 ±", 11.5)); _txtMargin.Margin = new Thickness(0, 0, 2, 0); row.Children.Add(_txtMargin); row.Children.Add(T("m　", 11.5));
        row.Children.Add(_chkBackToWorkLine);
        row.Children.Add(_chkLateralToTemplate);
        row.Children.Add(T("走向余量 ±", 11.5)); _txtLatMargin.Margin = new Thickness(0, 0, 2, 0); row.Children.Add(_txtLatMargin); row.Children.Add(T("m　", 11.5));
        row.Children.Add(_chkByLevel);
        row.Children.Add(T("归格容差 ±", 11.5)); _txtLevelTol.Margin = new Thickness(0, 0, 2, 0); row.Children.Add(_txtLevelTol); row.Children.Add(T("m　", 11.5));
        _btnOldLayers.Click += (_, _) => ShowOldLayerFlyout();
        row.Children.Add(_btnOldLayers);
        _btnPreviewReplace = B("④ 预览替换", PreviewReplace, tip: "只算不落地：红=要删、蓝=要留、黄=归不进级(不删)、琥珀虚线=覆盖范围");
        _btnApplyReplace = B("⑤ 执行替换", ApplyReplace, tip: "删原线 + 写回端帮保留段 + 落衔接段（会改动原实体）");
        row.Children.Add(_btnPreviewReplace);
        row.Children.Add(_btnApplyReplace);
        return row;
    }

    /// <summary>台账区头部：两个页签按钮 + 提示。</summary>
    private Control BuildLedgerHeader()
    {
        var lh = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        DockPanel.SetDock(_txtLedgerHint, Avalonia.Controls.Dock.Right); lh.Children.Add(_txtLedgerHint);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal };
        _btnTabLinks = B("衔接台账", () => ShowLedger(false)); _btnTabLinks.Padding = new Thickness(8, 1);
        _btnTabCover = B("覆盖范围台账", () => ShowLedger(true)); _btnTabCover.Padding = new Thickness(8, 1);
        tabs.Children.Add(_btnTabLinks); tabs.Children.Add(_btnTabCover);
        lh.Children.Add(tabs);
        return lh;
    }

    private Control BuildLedgerBody()
    {
        _pnlCoverActions.Children.Add(B("选中的→强制替换", () => SetOverrideFromSelection(true), tip: "视口里选中的台阶线强制判为替换（删）"));
        _pnlCoverActions.Children.Add(B("选中的→强制保留", () => SetOverrideFromSelection(false)));
        _pnlCoverActions.Children.Add(B("清除改判", ClearOverrides));
        _pnlCoverActions.Children.Add(B("用断口建端帮节点", CutsToNodes, tip: "把 ④ 的裁剪断口灌成端帮节点，替掉按采场范围环切出来的那批 —— 衔接接的才是真断口"));
        foreach (var c in _pnlCoverActions.Children) if (c is Button b) { b.Padding = new Thickness(8, 1); b.FontSize = 11; b.Margin = new Thickness(0, 0, 4, 4); }
        _lstCover.SelectionChanged += (_, _) => OnCoverRowSelected();
        _lstCover.DoubleTapped += (_, _) => OnCoverRowDoubleClick();
        var body = new DockPanel();
        DockPanel.SetDock(_pnlCoverActions, Avalonia.Controls.Dock.Bottom);
        body.Children.Add(_pnlCoverActions);
        var lists = new Grid();
        lists.Children.Add(new ScrollViewer { Content = _lstLinks });
        lists.Children.Add(new ScrollViewer { Content = _lstCover });
        body.Children.Add(new Border { BorderBrush = Brush.Parse("#D5DBE3"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = lists });
        return body;
    }

    private void ShowLedger(bool cover)
    {
        _lstLinks.IsVisible = !cover;
        _lstCover.IsVisible = cover;
        _pnlCoverActions.IsVisible = cover;
        _txtLedgerHint.Text = cover ? (_plan == null ? "先跑 ④预览替换" : "双击「归不进级」行 = 强制替换") : "选中后按 Delete 删";
        if (_btnTabLinks != null) _btnTabLinks.FontWeight = cover ? FontWeight.Normal : FontWeight.SemiBold;
        if (_btnTabCover != null) _btnTabCover.FontWeight = cover ? FontWeight.SemiBold : FontWeight.Normal;
        if (cover) RebuildCoverLedger();
    }

    // ── 老台阶线图层白名单 ──
    private void ShowOldLayerFlyout()
    {
        var panel = new StackPanel { Margin = new Thickness(10), MinWidth = 240 };
        panel.Children.Add(new TextBlock { Text = "哪些图层算「老台阶线」（参与替换）", FontWeight = FontWeight.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 0, 6) });
        var order = new List<string>(); var count = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var pl in _pitRaw) { string lay = pl.Layer ?? ""; if (!count.ContainsKey(lay)) { count[lay] = 0; order.Add(lay); } count[lay]++; }
        order.Sort(StringComparer.Ordinal);
        var boxes = new List<CheckBox>();
        var list = new StackPanel();
        foreach (var lay in order)
        {
            var cb = new CheckBox { Content = $"{(lay.Length == 0 ? "（无图层名）" : lay)}　{count[lay]} 条", Tag = lay, IsChecked = !_uncheckedLayers.Contains(lay), FontSize = 11.5 };
            cb.IsCheckedChanged += (_, _) => { if (cb.IsChecked == true) _uncheckedLayers.Remove(lay); else _uncheckedLayers.Add(lay); _plan = null; Render(); };
            boxes.Add(cb); list.Children.Add(cb);
        }
        if (order.Count == 0) list.Children.Add(new TextBlock { Text = "（还没提取，或图上没有老台阶线）", FontSize = 11, Foreground = Brush.Parse("#8A96A8") });
        panel.Children.Add(new ScrollViewer { MaxHeight = 320, Content = list });
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        bar.Children.Add(B("全选", () => { foreach (var cb in boxes) cb.IsChecked = true; }));
        bar.Children.Add(B("全不选", () => { foreach (var cb in boxes) cb.IsChecked = false; }));
        panel.Children.Add(bar);
        new Flyout { Content = panel, Placement = PlacementMode.Bottom }.ShowAt(_btnOldLayers);
    }

    /// <summary>定覆盖范围用的模板台阶本体：白名单模式下就是人认下的那批；否则只取 台阶_*（「创建工程位置_台阶」装的是端帮衔接产物）。</summary>
    private List<EpPolyline> TemplateForBand(out bool fellBack)
    {
        if (_tplLayerPick.Count > 0) { fellBack = false; return new List<EpPolyline>(_tplRaw); }
        var strict = _tplRaw.Where(pl => EngineeringPositionBuilder.IsTemplateBenchLine(pl.Layer)).ToList();
        fellBack = strict.Count == 0 && _tplRaw.Count > 0;
        return fellBack ? new List<EpPolyline>(_tplRaw) : strict;
    }

    /// <summary>参与替换的老台阶线：勾上的图层 + 排除工作线实体本身。</summary>
    private List<EpPolyline> OldLinesInScope()
    {
        var wl = new HashSet<ulong>(_workLineHandles);
        return _pitRaw.Where(pl => !_uncheckedLayers.Contains(pl.Layer ?? "") && !wl.Contains(pl.Handle)).ToList();
    }

    // ── 工作线 ──
    private void UpdateWorkLineText() => _txtWorkLine.Text = _workLines.Count == 0 ? "未加载" : $"✓ {_workLines.Count} 条";

    /// <summary>
    /// 读「驱动量」交接单：工作线 + 前界 + 覆盖区间 + 标高格 + 每一级的标高与身份。进程内一次性对象：关了软件、或这一版图不是本次驱动量生成的，就是没有 ——
    /// 那时退回按图层解析，覆盖范围照样算得出，只是逐级替换拿不到台阶身份（标签退化成图层名）。
    /// </summary>
    internal bool TryAdoptHandoff(bool quiet)
    {
        var h = EngineeringPositionHandoff.Latest;
        _handoff = h;
        if (h == null)
        {
            _txtHandoff.Text = "无交接单";
            if (!quiet) Status("没有交接单——本会话还没跑过「驱动量」→「生成采区台阶面」，或软件重启过。④仍可用：覆盖范围从图上 台阶_* + 工作线反推，逐级替换的标高从图上台阶线聚（拿不到煤层/级号身份）。");
            return false;
        }
        _txtHandoff.Text = $"✓ {h.SavedAt:HH:mm} · {h.Levels.Count} 级";
        ToolTip.SetTip(_txtHandoff, h.Summary);
        if (h.WorkLines.Count > 0)
        {
            RefreshInput();
            _workLines.Clear(); _workLines.AddRange(h.WorkLines);
            _workLineHandles.Clear(); foreach (var (hd, g) in _input.WorkLines) if (g.Success) _workLineHandles.Add(hd);   // 只作"别把工作线当老台阶线删掉"的排除集
            UpdateWorkLineText();
        }
        _plan = null;
        if (!quiet) Status($"已读交接单：{h.Summary}。工作线已带出 {_workLines.Count} 条 → ④预览替换。");
        return true;
    }
    internal bool HasHandoff => _handoff != null;

    internal void FindWorkLines()
    {
        RefreshInput();
        _workLines.Clear(); _workLineHandles.Clear(); _plan = null;
        foreach (var (h, g) in _input.WorkLines) if (g.Success) { _workLines.Add(g); _workLineHandles.Add(h); }
        UpdateWorkLineText();
        Status(_workLines.Count > 0
            ? $"找到 {_workLines.Count} 条工作线（「工作线」图层 + 当前选中）。若捞到了不相干的，改用「用选中的」。→ ④预览替换。"
            : "没找到工作线。请先用「创建工作线」画一条，或在视口选中后点「用选中的」。");
    }

    private void LoadSelectedWorkLines()
    {
        RefreshInput();
        _workLines.Clear(); _workLineHandles.Clear(); _plan = null;
        foreach (var (h, g) in _input.WorkLines) if (g.Success && _input.SelectedHandles.Contains(h)) { _workLines.Add(g); _workLineHandles.Add(h); }
        UpdateWorkLineText();
        Status(_workLines.Count > 0
            ? $"已用选中的 {_workLines.Count} 条工作线（共选中 {_input.SelectedHandles.Count} 个实体）。→ ④预览替换。"
            : $"选中的 {_input.SelectedHandles.Count} 个实体里没有工作线。请在视口里选中工作线（基线）对象，或点「自动查找」。");
    }

    // ── 预览 / 执行 ──

    private BenchReplacePlan? ComputePlan(out bool fellBack)
    {
        fellBack = false;
        if (_tplRaw.Count == 0 || _workLines.Count == 0) return null;
        double margin = TryD(_txtMargin, out double mg) && mg >= 0 ? mg : 5.0;
        var olds = OldLinesInScope();
        var tpl = TemplateForBand(out fellBack);
        // 标高格：交接单优先（带煤层/级号身份），没有就从图上新台阶线的标高聚（标签退化成图层名）
        BenchLevelGrid? grid = null; bool fromHandoff = false;
        if (_chkByLevel.IsChecked == true)
        {
            double tolIn = TryD(_txtLevelTol, out double lt) && lt > 0 ? lt : 0;
            if (_handoff is { Levels.Count: > 0 }) { grid = BenchLevelGrid.FromHandoff(_handoff, tolIn > 0 ? tolIn : null); fromHandoff = true; }
            else grid = BenchLevelGrid.FromTemplateLines(tpl, tolIn);
            if (grid.Count == 0) grid = null;
        }
        return BenchLineReplacer.Plan(_workLines, tpl, olds, margin, latTol: 5.0,
            backToWorkLine: _chkBackToWorkLine.IsChecked == true,
            levels: grid, levelsFromHandoff: fromHandoff,
            overrides: _overrides.Count > 0 ? _overrides : null,
            seamRoster: _handoff?.SeamRoster, seamReady: _handoff?.SeamReady,
            lateralToTemplate: _chkLateralToTemplate.IsChecked == true,
            lateralMargin: TryD(_txtLatMargin, out double lm) && lm >= 0 ? lm : -1);
    }

    internal void PreviewReplace()
    {
        if (DumpMode) { Warn("排土场模式不走④ —— 它的覆盖范围来自工作线，排土场没有工作线。请用⑥生成新建工程位置。"); return; }
        if (_tplRaw.Count == 0) { Warn("请先「①提取节点」——覆盖范围要用图上的新台阶线（模板）算。"); return; }
        if (_workLines.Count == 0) { Warn("请先加载工作线（「自动查找」或「用选中的」）。"); return; }
        var plan = ComputePlan(out bool fellBack);
        if (plan == null) { Warn("算不出替换方案（模板或工作线为空）。"); return; }
        if (!plan.Success) { _plan = null; _coverSel = null; Render(); RebuildCoverLedger(); Warn("预览失败：" + plan.Error); return; }

        _plan = plan; _coverSel = null;
        int wallRebuilt = AdoptEndWallFromPlan(plan);
        _selected = null;
        Render(); RebuildLedger(); RebuildCoverLedger();

        int del = plan.HandlesToDelete().Length;
        int olds = plan.Items.Count;
        string msg = del == 0
            ? $"覆盖范围内没有要替换的老台阶线（扫过 {olds} 条" + (plan.UnmatchedCount > 0 ? $"，其中 {plan.UnmatchedCount} 条在范围内但归不进任何一级新台阶 —— 见覆盖范围台账）。" : "）。")
              + "要么这一阶段本来就没有老台阶要换，要么工作线/图层筛选选错了 —— 对着平面图看看琥珀色覆盖范围压没压住老线。"
            : $"预览：{plan.SupersededCount} 条整条被取代 + {plan.ClippedCount} 条跨界裁剪 ⇒ 删 {del} 个实体、写回 {plan.KeepSegments().Count()} 段端帮保留段（丢弃 {plan.DroppedLength:0.#}m / 扫过 {plan.ScannedLength:0.#}m）。"
              + "红=要删、蓝=要留、黄=归不进级(不删)、琥珀虚线=覆盖范围。确认无误 → ⑤执行替换。";
        if (plan.UnmatchedCount > 0) msg += $" ⚠ 另有 {plan.UnmatchedCount} 条在覆盖范围内但标高归不进任何一级新台阶，已全部保留（信息栏逐条）。";
        msg += " ｜ " + DescribeLateral(plan);
        msg += wallRebuilt > 0
            ? $" ｜ 端帮节点已按模板影响区域边界重建 {wallRebuilt} 个（连线接的就是图上真会出现的那个断口）。"
            : " ｜ ⚠ 没有裁剪断口 —— 影响区域边界上一条老台阶线都没穿过，两侧接不出端帮节点。多半是模板压根没盖住老台阶线，或图层筛选把它们排掉了。";
        if (fellBack) msg = "⚠ 图上没有 台阶_* 图层，覆盖范围退回用「创建工程位置_台阶」算——那一层若装的是端帮衔接段，范围会被撑到端帮尽头。｜" + msg;
        Status(msg);
        _echo("创建工程位置·替换台阶线 · " + plan.Diag, false);
        foreach (var it in plan.UnmatchedItems())
            _echo($"    归不进级（保留）：{(it.Layer.Length == 0 ? "（无图层名）" : it.Layer)} #{it.Handle} 标高 {it.Z:0.##}m（极差 {it.ZSpread:0.##}m）· 长 {it.Length:0.#}m —— 最近的新台阶级差 {NearestLevelGap(plan, it.Z)}", true);
    }

    private static string DescribeLateral(BenchReplacePlan p)
    {
        if (p.Bands.Count == 0) return "覆盖范围：空";
        double tplW = 0, bandW = 0; bool anyOpen = false;
        foreach (var b in p.Bands)
        {
            if (b.TemplateLatHi > b.TemplateLatLo) tplW = Math.Max(tplW, b.TemplateLatHi - b.TemplateLatLo);
            if (b.LatClipped) bandW = Math.Max(bandW, b.LatHi - b.LatLo); else anyOpen = true;
        }
        if (!anyOpen) return $"横向限在模板影响区域内：走向宽 {bandW:0.#}m（模板 {tplW:0.#}m + 两头余量）。";
        return "⚠ 横向没限住：覆盖范围沿走向吃满整条工作线宽" + (tplW > 0 ? $"（模板自身只有 {tplW:0.#}m 宽）" : "") + " —— 模板左右两侧、同一推进位置上的老台阶线也会被删。要只换模板那一段，请勾上「横向限于模板」。";
    }

    private static string NearestLevelGap(BenchReplacePlan p, double z)
    {
        if (p.Levels is not { Count: > 0 }) return "（未启用逐级）";
        double best = double.MaxValue; string tag = ""; double bz = 0;
        for (int i = 0; i < p.Levels.Count; i++)
        {
            double d = p.Levels.LevelZ[i] - z;
            if (Math.Abs(d) < Math.Abs(best)) { best = d; tag = p.Levels.LevelTag[i]; bz = p.Levels.LevelZ[i]; }
        }
        return $"{best:+0.##;-0.##;0}m 到「{tag}」{bz:0.##}m，超出容差 ±{p.Levels.Tol:0.##}m";
    }

    // ── 过渡段盖住的那截原线要让位 ──
    private static (long, long, long) CutKey(double x, double y, double z) => ((long)Math.Round(x * 1000), (long)Math.Round(y * 1000), (long)Math.Round(z * 1000));

    private static Dictionary<(long, long, long), double> EatenByCut(IReadOnlyList<EpConnector> cons)
    {
        var map = new Dictionary<(long, long, long), double>();
        foreach (var c in cons)
        {
            if (c.WallEaten <= 1e-6) continue;
            var k = CutKey(c.CutX, c.CutY, c.CutZ);
            map[k] = Math.Max(map.TryGetValue(k, out double v) ? v : 0, c.WallEaten);
        }
        return map;
    }

    private static List<(double X, double Y, double Z)>? TrimByConnectors(List<(double X, double Y, double Z)> pts, Dictionary<(long, long, long), double> eatAt, out double cutLen)
    {
        cutLen = 0;
        if (eatAt.Count == 0 || pts.Count < 2) return pts;
        List<(double X, double Y, double Z)>? seg = pts;
        var head = pts[0]; var tail = pts[pts.Count - 1];
        if (eatAt.TryGetValue(CutKey(head.X, head.Y, head.Z), out double eh) && eh > 0) { seg = EpConnectorBuilder.TrimFrom(seg, fromHead: true, eh); cutLen += eh; }
        if (seg != null && seg.Count >= 2 && eatAt.TryGetValue(CutKey(tail.X, tail.Y, tail.Z), out double et) && et > 0) { seg = EpConnectorBuilder.TrimFrom(seg, fromHead: false, et); cutLen += et; }
        return seg is { Count: >= 2 } ? seg : null;
    }

    internal async void ApplyReplace()
    {
        if (DumpMode) { Warn("排土场模式不走⑤ —— 它会删原实体，而排土档的前提是原图一根不动。请用⑥。"); return; }
        if (_plan == null || !_plan.Success) { Warn("请先「④预览替换」。"); return; }
        var del = _plan.HandlesToDelete();
        if (del.Length == 0) { Warn("覆盖范围内没有要替换的老台阶线。"); return; }

        var cons = BuildConnectors();
        double gradePct = TryD(_txtGrade, out double gp) && gp > 0 ? gp : EpConnectorBuilder.DefaultGradePct;

        var keep = new List<(string Layer, List<(double X, double Y, double Z)> Pts, ulong FromHandle)>();
        int trimmedSegs = 0, eatenWhole = 0; double trimmedLen = 0;
        var eatAt = EatenByCut(cons);
        foreach (var it in _plan.Items)
        {
            if (it.Verdict != BenchReplaceVerdict.Clipped) continue;
            foreach (var pts in it.Keep)
            {
                if (pts.Count < 2) continue;
                var seg = TrimByConnectors(pts, eatAt, out double cutLen);
                if (cutLen > 0) { trimmedSegs++; trimmedLen += cutLen; }
                if (seg == null) { eatenWhole++; continue; }
                keep.Add((it.Layer, seg, it.Handle));
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"要删除 {del.Length} 个老台阶线实体（{_plan.SupersededCount} 条整条被新台阶线取代、{_plan.ClippedCount} 条跨界裁剪），并把 {keep.Count} 段范围外保留段写回原图层。");
        sb.AppendLine($"丢弃长度 {_plan.DroppedLength:0.#} m / 扫过 {_plan.ScannedLength:0.#} m。");
        if (cons.Count > 0)
        {
            sb.AppendLine($"同时落地 {cons.Count} 段衔接过渡（图层「{LinkLayer}」，替换上一批）：");
            sb.AppendLine("  " + EpConnectorBuilder.Summarize(cons, gradePct));
            sb.AppendLine(trimmedSegs > 0
                ? $"  被过渡段盖住的原台阶线一并让位：{trimmedSegs} 段各裁掉一截，共 {trimmedLen:0.#} m" + (eatenWhole > 0 ? $"（其中 {eatenWhole} 段整段都在过渡段底下，不再写回）" : "")
                : "  没有原台阶线被过渡段盖住（平距本来就够缓，一米端帮都没吃）。");
        }
        sb.AppendLine(_plan.Levels is { Count: > 0 }
            ? $"归级口径：{_plan.Levels.Count} 级新台阶（图上反推），容差 ±{_plan.Levels.Tol:0.##} m。"
            : "归级口径：未启用逐级 —— 覆盖范围内的老台阶线一律取代，不看标高。");
        if (_plan.UnmatchedCount > 0) sb.AppendLine($"另有 {_plan.UnmatchedCount} 条在覆盖范围内但标高归不进任何一级新台阶：一条都不删，原样留在图上。");
        sb.AppendLine("逐图层：");
        foreach (var t in _plan.ByLayer())
            sb.AppendLine($"  · {(t.Layer.Length == 0 ? "（无图层名）" : t.Layer)}：取代 {t.Superseded} 条 + 裁剪 {t.Clipped} 条" + (t.Unmatched > 0 ? $"（另 {t.Unmatched} 条归不进级·保留）" : "") + $"，丢弃 {t.DropLength:0.#} m");
        sb.AppendLine();
        sb.AppendLine("【会改动原实体】删除 + 写回作为一次编辑落地（Ctrl+Z 可整体撤销）。继续？");
        if (!await BlockMsgBox.ConfirmAsync(this, "创建工程位置 · 替换台阶线", sb.ToString(), "执行替换", "取消"))
        { Status("已取消，图上什么都没动。"); return; }

        try
        {
            int deleted = _apply(new EpApplyRequest { Delete = del, Keep = keep, Connectors = cons, Scene = _scene!, GradePct = gradePct });
            if (cons.Count > 0) SaveLinks(_scene!);
            string msg = $"替换完成✓：删 {deleted}/{del.Length} 个老台阶线实体，写回 {keep.Count} 段端帮保留段"
                       + (trimmedSegs > 0 ? $"（其中 {trimmedSegs} 段让给过渡段共 {trimmedLen:0.#}m" + (eatenWhole > 0 ? $"，另 {eatenWhole} 段整段让掉" : "") + "）" : "") + "。"
                       + (cons.Count > 0 ? $"衔接 {cons.Count} 段入图。" : "")
                       + $"这一阶段覆盖范围内的台阶线已换成新台阶线（{_plan.NewLineCount} 条 / {_plan.NewLength:0.#}m）。";
            if (deleted < del.Length) msg += $" ⚠ 有 {del.Length - deleted} 个没删掉（实体已不在），图上会与写回段重叠，请手动核对。";
            Status(msg);
            _echo("创建工程位置·替换台阶线 · " + msg, deleted != del.Length);
            foreach (var t in _plan.ByLayer())
                _echo($"    {(t.Layer.Length == 0 ? "（无图层名）" : t.Layer)}：取代 {t.Superseded} 条 + 裁剪 {t.Clipped} 条" + (t.Unmatched > 0 ? $"（另 {t.Unmatched} 条归不进级·已保留）" : "") + $"，丢弃 {t.DropLength:0.#} m", false);
            foreach (var c in cons) _echo("    " + c.Describe(SideName(c.Side)), c.OverGrade);
            _plan = null; _overrides.Clear();
            RefreshInput(); Extract();          // 图变了 → 重新提取
            if (_workLines.Count > 0) FindWorkLines();
        }
        catch (Exception ex) { Warn("替换异常：" + ex.Message); }
    }

    // ── 覆盖范围台账 ──
    private void RebuildCoverLedger()
    {
        _suppressCoverEvent = true;
        _lstCover.Items.Clear();
        if (_plan == null)
        {
            _lstCover.Items.Add(NoteItem("还没跑 ④预览替换 —— 覆盖范围里有什么、碰到没碰到，得先算一遍才知道。"));
            _suppressCoverEvent = false; return;
        }
        if (_plan.SeamTally.Count > 0)
        {
            _lstCover.Items.Add(HeadItem($"逐煤层（{_plan.SeamTally.Count} 层）"));
            foreach (var t in _plan.SeamTally)
            {
                string state, tip; Color c;
                if (t.BenchCount == 0) { c = CDrop; state = t.Ready ? "无台阶" : "无台阶·顶底板缺"; tip = "名册里有这一层，但这一阶段覆盖范围内一条台阶线都没建出来。"; }
                else if (t.UntouchedCount == 0) { c = CLink; state = "全部触及"; tip = "这一层每一级新台阶都换掉了对应的老台阶线。"; }
                else { c = CUnmatched; state = $"{t.UntouchedCount}/{t.BenchCount} 级碰不到"; tip = "这几级是新开的：覆盖范围内没有任何老台阶线归到它 —— 两端要自己去接端帮。"; }
                _lstCover.Items.Add(RowItem(state, c, string.IsNullOrEmpty(t.Seam) ? "（未命名层）" : t.Seam,
                    $"{t.BenchCount} 级 · 触及 {t.TouchedCount} 级 · 取代老线 {t.ReplacedLength:0.#} m" + (t.InRoster ? "" : "（图层名反推）"), tip, new CoverRow { Kind = 3, Seam = t }));
            }
        }
        if (_plan.LevelUsage.Count > 0)
        {
            _lstCover.Items.Add(HeadItem($"逐级新台阶（{_plan.LevelUsage.Count} 级 · 碰不到 {_plan.UntouchedLevelCount} 级）"));
            var ordered = new List<BenchLevelUsage>(_plan.LevelUsage);
            ordered.Sort((a, b) => a.Touched != b.Touched ? (a.Touched ? 1 : -1) : b.Z.CompareTo(a.Z));
            foreach (var u in ordered)
                _lstCover.Items.Add(RowItem(u.Touched ? "已替换" : "碰不到", u.Touched ? CLink : CUnmatched, $"{u.Z:0.##} m　{u.Tag}",
                    u.Touched ? $"取代老线 {u.OldCount} 条 / {u.OldDropLength:0.#} m" : "覆盖范围内没有任何老台阶线归到这一级 —— 新开的，两端要接端帮",
                    u.Touched ? null : "不一定是错的：新降的一级本来就没有旧台阶。若这一级本该接上现状台阶却没接到，多半是标高格错开或归格容差太紧。",
                    new CoverRow { Kind = u.Touched ? 0 : 1, Level = u }));
        }
        int un = _plan.UnmatchedItems().Count();
        if (un > 0)
        {
            _lstCover.Items.Add(HeadItem($"归不进级的老台阶线（{un} 条 · 全部保留）"));
            foreach (var it in _plan.UnmatchedItems())
                _lstCover.Items.Add(RowItem(it.Overridden ? "改判·保留" : "归不进级", it.Overridden ? CSeamRow : CUnmatched,
                    $"{(it.Layer.Length == 0 ? "（无图层名）" : it.Layer)}　{it.Z:0.##} m",
                    $"#{it.Handle} · 长 {it.Length:0.#} m · Z 极差 {it.ZSpread:0.##} m · {NearestLevelGap(_plan, it.Z)}",
                    "在覆盖范围内，但标高归不进任何一级新台阶，所以一条都没删。双击 = 强制替换。", new CoverRow { Kind = 2, Item = it }));
        }
        if (_lstCover.Items.Count == 0) _lstCover.Items.Add(NoteItem("覆盖范围内没有任何内容 —— 先确认工作线与模板选对了。"));
        _suppressCoverEvent = false;
    }

    private static ListBoxItem NoteItem(string text) => new() { IsHitTestVisible = false, Content = new TextBlock { Text = text, FontSize = 11.5, Foreground = Brush.Parse("#5B6B80"), TextWrapping = TextWrapping.Wrap } };
    private static ListBoxItem HeadItem(string text) => new() { IsHitTestVisible = false, Margin = new Thickness(0, 6, 0, 2), Content = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#5B6B80") } };
    private static ListBoxItem RowItem(string chip, Color chipColor, string title, string sub, string? tip, CoverRow tag)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x33, chipColor.R, chipColor.G, chipColor.B)),
            Child = new TextBlock { Text = chip, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb((byte)(chipColor.R * 0.7), (byte)(chipColor.G * 0.7), (byte)(chipColor.B * 0.7))), Margin = new Thickness(6, 1, 6, 2) },
        });
        head.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var sp = new StackPanel { Children = { head, new TextBlock { Text = sub, FontSize = 10.5, Foreground = Brush.Parse("#5B6B80"), Margin = new Thickness(0, 1, 0, 0), TextWrapping = TextWrapping.Wrap } } };
        var item = new ListBoxItem { Content = sp, Tag = tag, Padding = new Thickness(6, 3) };
        if (tip != null) ToolTip.SetTip(item, tip);
        return item;
    }

    private void OnCoverRowSelected()
    {
        if (_suppressCoverEvent) return;
        _coverSel = (_lstCover.SelectedItem as ListBoxItem)?.Tag as CoverRow;
        Render();
        if (_coverSel?.Item != null) Status($"选中：{_coverSel.Item.Layer} #{_coverSel.Item.Handle} 标高 {_coverSel.Item.Z:0.##}m —— 平面图已高亮。双击此行 = 强制替换。");
    }

    private void OnCoverRowDoubleClick()
    {
        var row = (_lstCover.SelectedItem as ListBoxItem)?.Tag as CoverRow;
        if (row?.Item == null || row.Item.Handle == 0) return;
        _overrides[row.Item.Handle] = true;
        Status($"已把 #{row.Item.Handle} 强制判为替换。重跑 ④预览替换生效。");
        PreviewReplace(); ShowLedger(true);
    }

    private void SetOverrideFromSelection(bool force)
    {
        RefreshInput();
        var sel = _input.SelectedHandles;
        if (sel.Count == 0) { Warn("请先在视口里选中要改判的台阶线。"); return; }
        foreach (var h in sel) if (h != 0) _overrides[h] = force;
        Status($"已把选中的 {sel.Count} 个实体强制判为{(force ? "替换（删）" : "保留（不删）")}，共 {_overrides.Count} 条改判。重算中…");
        PreviewReplace(); ShowLedger(true);
    }

    private void ClearOverrides()
    {
        if (_overrides.Count == 0) { Status("本来就没有改判。"); return; }
        int n = _overrides.Count;
        _overrides.Clear();
        Status($"已清除 {n} 条改判，全部回到按规则判。重算中…");
        PreviewReplace(); ShowLedger(true);
    }

    /// <summary>端帮 = 模板影响区域边界切出来的断口：裁剪与连线必须共用同一条边界。配对先按侧别+标高+位置记下来再认回去。</summary>
    private int AdoptEndWallFromPlan(BenchReplacePlan? plan)
    {
        if (_scene == null || plan is not { Success: true }) return 0;
        var cuts = new List<EpCutPoint>();
        foreach (var it in plan.Items) foreach (var c in it.Cuts) cuts.Add(new EpCutPoint(c.X, c.Y, c.Z, it.Handle, it.Layer, c.Trail));
        if (cuts.Count == 0) return 0;

        var saved = new List<(int Side, double TZ, double WZ, bool Suggested, double TX, double TY, double WX, double WY, bool WallIsTpl)>();
        foreach (var l in _scene.Links)
        {
            var t = _scene.ById(l.TemplateId); var w = _scene.ById(l.WallId);
            if (t != null && w != null) saved.Add((t.Side, t.Z, w.Z, l.Suggested, t.X, t.Y, w.X, w.Y, w.Kind == EpNodeKind.Template));
        }
        double gtol = TryD(_txtGroupTol, out double g) && g > 0 ? g : 0.5;
        int added = EngineeringPositionBuilder.AddEndWallNodesFromCuts(_scene, cuts, gtol, replaceExisting: true);
        _scene.WallKeep.Clear();
        foreach (var (_, pts) in plan.KeepSegments()) _scene.WallKeep.Add(pts);

        double tol = Math.Max(gtol, 0.5);
        foreach (var (side, tz, wz, sug, tx, ty, wx, wy, wIsTpl) in saved)
        {
            var t = Nearest(_scene, EpNodeKind.Template, side, tz, tol, tx, ty);
            // 端帮节点已重建到新位置：只按侧别+标高认回（位置变了），模板互连的仍按位置
            var w = wIsTpl ? Nearest(_scene, EpNodeKind.Template, side, wz, tol, wx, wy) : Nearest(_scene, EpNodeKind.EndWall, side, wz, tol, double.NaN, double.NaN);
            if (t == null || w == null) continue;
            if (_scene.Links.Exists(x => x.TemplateId == t.Id || x.WallId == w.Id)) continue;
            if (!EngineeringPositionBuilder.IsValidPair(_scene, t, w)) continue;
            _scene.Links.Add(new EpLink { TemplateId = t.Id, WallId = w.Id, Suggested = sug });
        }
        return added;
    }

    private void CutsToNodes()
    {
        if (_scene == null) { Warn("请先「①提取节点」。"); return; }
        if (_plan == null) { Warn("请先「④预览替换」——断口是替换算出来的。"); return; }
        int cutCount = 0; double trailSum = 0;
        foreach (var it in _plan.Items) foreach (var c in it.Cuts) { cutCount++; trailSum += c.TrailLength; }
        if (cutCount == 0) { Warn("这一版方案没有裁剪断口（没有跨界的老台阶线）—— 没有断口可灌。"); return; }
        int added = AdoptEndWallFromPlan(_plan);
        _selected = null;
        _pvFit = false; Render(); RebuildLedger();
        Status($"已用 {cutCount} 个裁剪断口重建端帮节点 {added} 个（按模板影响区域边界切，已有配对按标高认回）。每处断口带着往外的退路（共 {trailSum:0.#}m），③衔接就沿它把高差缓缓爬完。→ 在图上拖 / 就近自动配对 → ③生成衔接并落地。");
        _echo($"创建工程位置 · 端帮节点按模板影响区域重建：断口 {cutCount} 个 → 节点 {added} 个 · 退路合计 {trailSum:0.#}m", false);
    }

    /// <summary>④ 方案在平面图上的着色层：覆盖范围环（琥珀虚线）/ 要删（红）/ 要留（蓝）/ 归不进级（黄）/ 台账选中（白）。</summary>
    private void RenderPlanOverlay()
    {
        if (_plan == null) return;
        foreach (var ring in _plan.BandRings)
        {
            if (ring.Length < 6) continue;
            var pts = new List<Point>(ring.Length / 2 + 1);
            for (int i = 0; i + 1 < ring.Length; i += 2) pts.Add(PV(ring[i], ring[i + 1]));
            pts.Add(pts[0]);
            _canvas.Children.Add(new Polyline { Points = new AvaloniaList<Point>(pts), Stroke = new SolidColorBrush(CBand), StrokeThickness = 1.4, StrokeDashArray = new AvaloniaList<double> { 6, 3 }, IsHitTestVisible = false });
        }
        foreach (var it in _plan.Items)
        {
            bool sel = _coverSel?.Item != null && ReferenceEquals(_coverSel.Item, it);
            if (it.Verdict == BenchReplaceVerdict.Unmatched)
            {
                var pl = _pitRaw.FirstOrDefault(p => p.Handle == it.Handle);
                if (pl.Xyz != null && pl.Xyz.Length >= 6) AddPvPoly(pl.Xyz, sel ? Colors.White : CUnmatched, sel ? 3.2 : 2.2);
                continue;
            }
            foreach (var seg in it.Drop) AddPvSeg(seg, sel ? Colors.White : CDrop, 2.2);
            foreach (var seg in it.Keep) AddPvSeg(seg, sel ? Colors.White : CKeep, 2.2);
        }
    }

    private void AddPvSeg(List<(double X, double Y, double Z)> seg, Color col, double th)
    {
        if (seg.Count < 2) return;
        var pts = new List<Point>(seg.Count);
        foreach (var q in seg) pts.Add(PV(q.X, q.Y));
        _canvas.Children.Add(new Polyline { Points = new AvaloniaList<Point>(pts), Stroke = new SolidColorBrush(col), StrokeThickness = th, IsHitTestVisible = false });
    }

    internal void ShowCoverLedger() => ShowLedger(true);
    internal int PlanDeleteCount => _plan?.HandlesToDelete().Length ?? 0;
    internal string PlanDiag => _plan?.Diag ?? "";
}
