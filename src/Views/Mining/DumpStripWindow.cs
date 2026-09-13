using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Dump;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Data.Entities;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Mining;

/// <summary>「排土条带」窗口读图的那一侧：一条候选线（图上多段线）。</summary>
internal sealed class DumpSourceLine
{
    public ulong Id;
    public string Layer = "";
    public double[] Xyz = Array.Empty<double>();
    public bool Closed;
    public bool Selected;
}

/// <summary>窗口向主窗口要的东西（取线 / 取范围 / 入图 / 库连接）。</summary>
internal sealed class DumpStripHost
{
    public Func<System.Data.Common.DbConnection?> Db = () => null;
    public Func<List<DumpSourceLine>> Lines = () => new();
    public Func<List<string>> LayerNames = () => new();
    /// <summary>把壳子体入图：(层名, 每格网格) → 每格的实体编号（0 = 没建）。</summary>
    public Func<string, IReadOnlyList<DumpStripShell.Mesh?>, ulong[]> AddShells = (_, _) => Array.Empty<ulong>();
    public Action<string, bool> Echo = (_, _) => { };
}

/// <summary>
/// 「排土条带」（忠实原 BlockModelLib <c>DumpStripDialog</c>）：选排土场 → 按台阶坡顶/坡底线配出台阶壳子 →
/// 按【分割长度 × 排土条带宽度】切网格 → 所有潜在排土位置（级/幅/带 + 质心 + 库容）；壳子体入图、位置清单落库
/// （表 dump_strip，寻径/排产取用）。极性 = 堆排（往坡脚外向上），与采场镜像。
/// 排土模板定参数 → 排土场放坡出台阶线 → <b>排土条带切位置</b> → 按量推进吃带序出形态 → 容量校核吃这张位置表。
/// 算法全在 <see cref="DumpStripPlanner"/>（原版逐行移植）；本窗只负责取线、归级配对、入图、落库。
/// 【登记的差异】原版 6 种台阶线来源，这里接 4 种（区域内全部多段线 / 指定图层 / 当前选集 / 工作帮范围内坡顶↔坡底直接配对）；
/// 「从现状面切台阶带」「从现状面提台阶坡面」两种走面的来源、以及「套设计台账 mine_location」归级方式待接。
/// 壳子体由 <see cref="DumpStripShell"/> 托管放样（原版内核 CarveDumpStripsByRails），库容口径改用它的闭合网格实测体积。
/// </summary>
internal sealed class DumpStripWindow : Window
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly DumpStripHost _host;

    private readonly ComboBox _region = new() { MinWidth = 260 };
    private readonly ComboBox _lineSource = new() { MinWidth = 320 };
    private readonly ComboBox _layer = new() { MinWidth = 200 };
    private readonly TextBox _benchH = RoadUi.NumBox(25, 64);
    private readonly TextBox _minLineLen = RoadUi.NumBox(150, 64);
    private readonly TextBox _maxFaceRun = RoadUi.NumBox(0, 64);
    private readonly TextBox _panelLen = RoadUi.NumBox(50, 64);
    private readonly TextBox _stripW = RoadUi.NumBox(40, 64);
    private readonly TextBox _minLen = RoadUi.NumBox(5, 64);
    private readonly TextBox _maxSteps = RoadUi.NumBox(1, 64);
    private readonly ComboBox _workSlope = new() { MinWidth = 260 };
    private readonly CheckBox _buildSolids = new() { Content = "建壳子体入图", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _saveDb = new() { Content = "位置清单落库（dump_strip）", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly TextBlock _status = RoadUi.Text("", 12);
    private readonly TextBlock _emptyHint = RoadUi.Hint("");
    private readonly DataGrid _list = RoadUi.Table(new (string, string, double)[]
    {
        ("编号", "Code", 150), ("级", "Level", 40), ("幅", "Panel", 60), ("带", "Step", 40), ("子", "Sub", 40),
        ("坡顶Z", "CrestZ", 64), ("坡底Z", "ToeZ", 64), ("走向长 m", "Strike", 72), ("宽 m", "Width", 56),
        ("库容 m³", "Capacity", 90), ("质心 X", "Cx", 90), ("质心 Y", "Cy", 90), ("备注", "Note", 120),
    });

    private sealed class RegionItem
    {
        public long Id; public string Name = ""; public string Category = ""; public double[] RingXy = Array.Empty<double>();
        public override string ToString() => $"{Name}（{MineableRegions.DisplayName(Category)}）";
    }
    private sealed class SlopeItem { public string Label = ""; public double[]? RingXy; public override string ToString() => Label; }
    internal sealed class Row
    {
        public DumpStripPlanner.Cell C = null!;
        public string Code => C.Code; public int Level => C.LevelIndex; public string Panel => $"{C.PanelIndex}/{C.PanelCount}";
        public int Step => C.StepIndex; public string Sub => C.SubCount > 1 ? $"{C.SubIndex}/{C.SubCount}" : "";
        public string CrestZ => C.CrestZ.ToString("0.#", Inv); public string ToeZ => C.ToeZ.ToString("0.#", Inv);
        public string Strike => C.StrikeLenM.ToString("0", Inv); public string Width => C.StripWidthM.ToString("0.#", Inv);
        public string Capacity => C.CapacityM3.ToString("N0", Inv); public string Cx => C.Cx.ToString("0.#", Inv); public string Cy => C.Cy.ToString("0.#", Inv);
        public string Note => C.IsClamped ? "凹弯夹窄" : (C.IsWorkingFace ? "工作面" : "");
    }

    private readonly Dictionary<ulong, (double[] Xyz, bool Closed)> _lineGeom = new();
    private StandardLevelModel.Result? _levels;
    private List<DumpStripPlanner.BenchInput>? _benches;
    private DumpStripPlanner.Result? _plan;
    private double[]? _workSlopeRing;
    private const double PairLevelTolM = 1.0;

    public DumpStripWindow(DumpStripHost host)
    {
        _host = host;
        Title = "排土条带 — 潜在排土位置";
        RoadUi.Place(this, 1040, 760);

        _lineSource.ItemsSource = new[]
        {
            "区域内全部多段线（按排土场范围裁线）",
            "指定图层（再按区域裁线）",
            "当前选集（再按区域裁线）",
            "工作帮范围内：坡顶线 ↔ 坡底线直接配对（无预设）",
        };
        _lineSource.SelectedIndex = 0;
        _lineSource.SelectionChanged += (_, _) => { _layer.IsEnabled = _lineSource.SelectedIndex == 1; _benches = null; _levels = null; };
        _layer.IsEnabled = false;

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(10, 8, 10, 4) };
        int r = 0;
        void Add(string label, Control c)
        {
            form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var tb = RoadUi.Lbl(label); tb.Margin = new Thickness(0, 4, 8, 4); tb.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(tb, r); Grid.SetColumn(tb, 0); form.Children.Add(tb);
            c.Margin = new Thickness(0, 2, 0, 2);
            Grid.SetRow(c, r); Grid.SetColumn(c, 1); form.Children.Add(c);
            r++;
        }
        Add("排土场:", _region);
        Add("台阶线来源:", _lineSource);
        Add("图层:", _layer);
        Add("排土台阶高 (m):", RoadUi.Row(_benchH, RoadUi.Hint("　只作归级容差参考（归级按图上线聚类）"), RoadUi.Lbl("　最短线长 (m):"), _minLineLen, RoadUi.Lbl("　坡面投影上限 (m):"), _maxFaceRun, RoadUi.Hint("0 = 不限")));
        Add("分割长度 L (m):", RoadUi.Row(_panelLen, RoadUi.Lbl("　每级最多推进带数:"), _maxSteps, RoadUi.Hint("（默认由排土场边界决定推到哪，此项仅防呆兜底）")));
        Add("排土条带宽度 W (m):", RoadUi.Row(_stripW, RoadUi.Lbl("　最短幅长 (m):"), _minLen));
        Add("工作帮范围:", RoadUi.Row(_workSlope, RoadUi.Hint("　本期实际排弃那一片；不选 = 整个排土场")));
        Add("产物:", RoadUi.Row(_buildSolids, _saveDb));

        var btns = RoadUi.Row(
            RoadUi.Btn("识别台阶", AnalyzeBenches, 96),
            RoadUi.Btn("生成位置", Generate, 96, primary: true),
            RoadUi.Btn("导出清单", ExportCsv, 96),
            RoadUi.Btn("关闭", Close, 84));
        btns.Margin = new Thickness(10, 4, 10, 4);

        var listCell = new Grid();
        listCell.Children.Add(_list);
        _emptyHint.HorizontalAlignment = HorizontalAlignment.Center; _emptyHint.VerticalAlignment = VerticalAlignment.Center; _emptyHint.TextWrapping = TextWrapping.Wrap; _emptyHint.MaxWidth = 720;
        listCell.Children.Add(_emptyHint);

        var statusScroll = new ScrollViewer { Content = _status, MaxHeight = 200, Margin = new Thickness(10, 0, 10, 8) };
        _status.TextWrapping = TextWrapping.Wrap;

        var top = new StackPanel(); top.Children.Add(form); top.Children.Add(btns);
        Content = RoadUi.Rows(top, listCell, statusScroll, new Panel());

        _region.SelectionChanged += (_, _) => { _benches = null; _levels = null; ReloadWorkSlopes(); };
        ReloadRegions(); ReloadLayers(); ReloadWorkSlopes();
        SetEmptyHint("先选排土场与台阶线来源，点「识别台阶」。");
    }

    public void Reload() { ReloadRegions(); ReloadLayers(); ReloadWorkSlopes(); }

    private void SetEmptyHint(string? reason) { _emptyHint.Text = reason ?? ""; _emptyHint.IsVisible = reason != null; _list.IsVisible = reason == null; }

    private void ReloadRegions()
    {
        var items = new List<RegionItem>();
        foreach (var rr in MineableRegions.List(_host.Db()))
            if (MineableRegions.IsDumpSite(rr.Category))
                items.Add(new RegionItem { Id = rr.Id, Name = rr.Name, Category = rr.Category, RingXy = MineableRegions.RingXy(rr) });
        var keep = (_region.SelectedItem as RegionItem)?.Id;
        _region.ItemsSource = items;
        _region.SelectedIndex = items.Count == 0 ? -1 : Math.Max(0, items.FindIndex(i => i.Id == keep));
        if (items.Count == 0) _status.Text = "工程库里没有类别为 外排土场/内排土场 的区域 —— 先在「采场/排土场圈定」里把排土场圈出来并定类别。";
    }

    private void ReloadLayers()
    {
        var names = _host.LayerNames();
        var keep = _layer.SelectedItem as string;
        _layer.ItemsSource = names;
        _layer.SelectedIndex = names.Count == 0 ? -1 : Math.Max(0, names.IndexOf(keep ?? ""));
    }

    private void ReloadWorkSlopes()
    {
        var items = new List<SlopeItem> { new() { Label = "（不限制：整个排土场）", RingXy = null } };
        foreach (var rr in MineableRegions.List(_host.Db()))
            if (MineableRegions.IsDumpGate(rr.Category) && rr.RingUsable)
                items.Add(new SlopeItem { Label = $"{rr.Name}（排土工作帮）", RingXy = MineableRegions.RingXy(rr) });
        _workSlope.ItemsSource = items; _workSlope.SelectedIndex = 0;
        _workSlope.SelectionChanged += (_, _) => _workSlopeRing = (_workSlope.SelectedItem as SlopeItem)?.RingXy;
        _workSlopeRing = null;
    }

    private bool PairMode => _lineSource.SelectedIndex == 3;

    // ── 取线（忠实原 CollectLines：按区域裁线、剔短段、虚句柄记裁开的段）──
    private List<BenchLevelInventory.SourceLine> CollectLines(RegionItem reg, double[]? ring, out string note)
    {
        var lines = new List<BenchLevelInventory.SourceLine>();
        _lineGeom.Clear();
        double minLineLen = Num(_minLineLen, 0);
        var all = _host.Lines();
        IEnumerable<DumpSourceLine> src; string srcName;
        switch (_lineSource.SelectedIndex)
        {
            case 1: { string lay = _layer.SelectedItem as string ?? ""; src = all.Where(l => l.Layer == lay); srcName = $"图层「{lay}」"; break; }
            case 2: src = all.Where(l => l.Selected); srcName = "当前选集"; break;
            default: src = all; srcName = "图上全部多段线"; break;
        }
        int raw = 0, outRegion = 0, tooShort = 0, clipped = 0;
        ulong virt = 1UL << 62;
        ring ??= reg.RingXy;
        foreach (var l in src)
        {
            if (l.Xyz.Length < 6) continue;
            raw++;
            var segs = DumpStripPlanner.ClipToRing(l.Xyz, l.Closed, ring);
            if (segs.Count == 0) { outRegion++; continue; }
            if (segs.Count > 1 || segs[0].Xyz.Length != l.Xyz.Length) clipped++;
            foreach (var (segXyz, segClosed) in segs)
            {
                if (minLineLen > 0 && DumpStripPlanner.PlanLength(segXyz) < minLineLen) { tooShort++; continue; }
                ulong id = segXyz.Length == l.Xyz.Length ? l.Id : virt++;
                lines.Add(new BenchLevelInventory.SourceLine { Handle = id, Xyz = segXyz, Layer = l.Layer });
                _lineGeom[id] = (segXyz, segClosed);
            }
        }
        note = $"取线来源：{srcName} —— 多段线 {raw} 条，整条落在「{reg.Name}」外 {outRegion} 条"
             + (clipped > 0 ? $"，被区域边界裁开 {clipped} 条" : "")
             + (tooShort > 0 ? $"，短于 {minLineLen:0} m 的碎段剔除 {tooShort} 段" : "")
             + $"，实际参与归级 {lines.Count} 段。";
        return lines;
    }

    private void AnalyzeBenches()
    {
        if (_region.SelectedItem is not RegionItem reg) { _status.Text = "先选一个排土场。"; return; }
        if (PairMode) { AnalyzeFromPairs(reg); return; }
        var lines = CollectLines(reg, null, out string pickNote);
        if (lines.Count < 2)
        {
            _status.Text = pickNote + "\n凑不出一幅台阶（一幅需要相邻两级：上级坡顶线 + 下级坡底线）。\n本功能只用【图上已有的排土台阶线】，不按参数凭空造线 —— 图上没画就是没有，不静默补。";
            _levels = null; _benches = null; _list.ItemsSource = null;
            SetEmptyHint("这个排土场里没取到足够的线，配不出一幅台阶。\n\n换「台阶线来源」再试；若图上本来就没画排土台阶线，先用同组的「排土场放坡」把台阶生成出来 —— 排土条带是【吃】台阶线的，不造台阶线。");
            return;
        }
        double maxRun = Num(_maxFaceRun, 0);
        _levels = StandardLevelModel.Analyze(lines, sampler: null, new StandardLevelModel.Options { MergeTolM = 1.0, FlatTolM = 1.5, MaxFaceRunM = maxRun });
        var sb = new StringBuilder();
        sb.Append(pickNote).Append('\n').Append("标准水平按【图上台阶线聚类】——这一档挡不住等高线，务必核对下面的标高分布。\n").Append(ElevationHistogram(lines)).Append('\n').Append(_levels.Message);
        foreach (var w in _levels.Warnings) sb.Append("\n⚠ " + w);
        if (_levels.Ok)
        {
            sb.Append("\n各幅：" + string.Join(" / ", _levels.Pairs.Select(p => $"L{p.Index} {p.CrestZ:0.#}→{p.ToeZ:0.#}（高 {p.BenchHeightM:0.#}m，坡面投影 {p.FaceRunM:0.#}m，坡顶线长 {p.CrestLengthM:0}m）")));
            foreach (var w in SanityWarnings(_levels)) sb.Append("\n⚠ " + w);
        }
        _status.Text = sb.ToString();
        _list.ItemsSource = null; _benches = null;
        SetEmptyHint(_levels.Ok && _levels.Pairs.Count > 0
            ? $"已配出 {_levels.Pairs.Count} 幅台阶，点「生成位置」按分割长度 × 条带宽度切网格。"
            : "识别完了，但相邻级之间【一幅都没配出来】，所以没有位置可切。\n\n先看上面的「标高分布」：级距中位数只有几米、标高一长串 = 图上是【等高线】不是台阶线，调什么参数都配不出幅。\n\n排土条带是【吃】台阶线的，不造台阶线 —— 请先用同组的「排土场放坡」把排土台阶生成出来，再回到这里切位置。");
    }

    private void AnalyzeFromPairs(RegionItem reg)
    {
        double[] ring = _workSlopeRing is { Length: >= 6 } ? _workSlopeRing : reg.RingXy;
        string ringNote = _workSlopeRing is { Length: >= 6 } ? "范围 = 排土工作帮" : $"范围 = 整个排土场「{reg.Name}」（没选工作帮）";
        var lines = CollectLines(reg, ring, out string pickNote);
        if (lines.Count < 2)
        {
            _levels = null; _benches = null; _list.ItemsSource = null;
            _status.Text = pickNote + $"\n{ringNote}。\n范围内不足两条线，配不出一级（一级 = 一条坡顶线 + 一条坡底线）。";
            SetEmptyHint("这个范围里没取到足够的线。换「台阶线来源」的取线方式，或确认工作帮范围圈对了地方。");
            return;
        }
        var items = lines.Select(l => (l.Handle, l.Xyz, Z: MedianZ(l.Xyz))).Where(x => !double.IsNaN(x.Z)).OrderByDescending(x => x.Z).ToList();
        var levels = new List<List<(ulong Handle, double[] Xyz, double Z)>>();
        foreach (var it in items)
        {
            if (levels.Count == 0 || levels[^1][0].Z - it.Z > PairLevelTolM) levels.Add(new());
            levels[^1].Add(it);
        }
        var benches = new List<DumpStripPlanner.BenchInput>();
        var heights = new List<double>(); var runs = new List<double>();
        int railFail = 0, noMate = 0;
        for (int i = 0; i + 1 < levels.Count; i++)
            foreach (var c in levels[i])
            {
                double bestD = double.MaxValue; double[]? mate = null; double mateZ = 0;
                foreach (var t in levels[i + 1])
                {
                    double d = DumpStripPlanner.MeanGapXY(c.Xyz, t.Xyz);
                    if (d < bestD) { bestD = d; mate = t.Xyz; mateZ = t.Z; }
                }
                if (mate == null) { noMate++; continue; }
                double hi = c.Z, lo = mateZ;
                if (hi - lo <= 1e-6) { noMate++; continue; }
                heights.Add(hi - lo); runs.Add(bestD);
                var segs = Rails(c.Xyz, mate, hi, lo, ref railFail);
                foreach (var (crestRail, toeRail) in segs)
                    benches.Add(new DumpStripPlanner.BenchInput { LevelIndex = i + 1, CrestZ = hi, ToeZ = lo, CrestXyz = crestRail, ToeXyz = toeRail, CrestClosed = false, ToeClosed = false });
            }
        _levels = null; _benches = benches; _list.ItemsSource = null;
        var sb = new StringBuilder();
        sb.Append(pickNote).Append('\n').Append(ringNote).Append($"　线 {items.Count} 段 → 聚成 {levels.Count} 级（容差 {PairLevelTolM:0.#} m）。\n");
        if (heights.Count > 0)
        {
            heights.Sort(); runs.Sort();
            sb.Append($"配出 {heights.Count} 幅台阶、成壳子 {benches.Count} 条，坡顶线合计 {benches.Sum(b => DumpStripPlanner.PlanLength(b.CrestXyz)):N0} m。\n")
              .Append($"　台阶高（量出来的）：中位 {heights[heights.Count / 2]:0.##} m，范围 {heights[0]:0.##}~{heights[^1]:0.##} m\n")
              .Append($"　坡顶↔坡底水平间距：中位 {runs[runs.Count / 2]:0.#} m，范围 {runs[0]:0.#}~{runs[^1]:0.#} m");
            if (heights[heights.Count / 2] < 3.0) sb.Append($"\n⚠ 台阶高中位数只有 {heights[heights.Count / 2]:0.##} m —— 这个范围里取到的多半是【等高线】而不是台阶线，换「指定图层」只取台阶线那一层，或把最短线长调大滤掉碎线。");
            if (runs[runs.Count / 2] > 200) sb.Append($"\n⚠ 坡顶↔坡底水平间距中位 {runs[runs.Count / 2]:0.#} m —— 配到隔着很远的线上了，多半是这个范围里同一级的线不成对。");
        }
        else sb.Append("一幅都没配出来 —— 所有线都聚到了同一级（标高全一样？）或相邻级之间找不到对面那条线。");
        if (noMate > 0) sb.Append($"\n{noMate} 条坡顶线在下一级里找不到对面的线，已跳过。");
        if (railFail > 0) sb.Append($"\n{railFail} 幅做不出竖直断面轨（尖灭/自交/太碎），已跳过。");
        _status.Text = sb.ToString();
        SetEmptyHint(benches.Count > 0 ? $"已配出 {benches.Count} 条台阶壳子，点「生成位置」按分割长度 × 条带宽度切网格。" : "一条台阶壳子都没配出来 —— 见上面的诊断。");
    }

    private static List<(double[] Crest, double[] Toe)> Rails(double[] crest, double[] toe, double hi, double lo, ref int railFail)
    {
        MiningRailBuilder.SampleZ roofZ = (double x, double y, out double z) => { z = hi; return true; };
        MiningRailBuilder.SampleZ floorZ = (double x, double y, out double z) => { z = lo; return true; };
        List<(double[] Crest, double[] Toe)> segs;
        try { segs = MiningRailBuilder.BuildMiningRailSegments(new MiningRailBuilder.Band { CrestXyz = crest, ToeXyz = toe, ParentCrestXyz = crest }, roofZ, floorZ); }
        catch { railFail++; return new(); }
        if (segs.Count == 0) { railFail++; return segs; }
        var ok = new List<(double[], double[])>();
        foreach (var s in segs) { if (s.Crest.Length < 6 || s.Toe.Length < 6) railFail++; else ok.Add(s); }
        return ok;
    }

    private void Generate()
    {
        if (_region.SelectedItem is not RegionItem reg) { _status.Text = "先选一个排土场。"; return; }
        if (_benches == null || _benches.Count == 0)
        {
            AnalyzeBenches();
            if (PairMode) { if (_benches == null || _benches.Count == 0) return; }
            else if (_levels == null || !_levels.Ok) return;
        }
        double L = Num(_panelLen, 50), W = Num(_stripW, 40), minLen = Num(_minLen, 5);
        int maxSteps = (int)Math.Max(1, Num(_maxSteps, 1));
        var benches = _benches;
        if (benches == null || benches.Count == 0)
        {
            benches = new List<DumpStripPlanner.BenchInput>();
            int rawPairs = 0, railFail = 0;
            foreach (var p in _levels?.Pairs ?? new List<StandardLevelModel.BenchPair>())
            {
                if (!_lineGeom.TryGetValue(p.CrestHandle, out var c) || !_lineGeom.TryGetValue(p.ToeHandle, out var t)) continue;
                rawPairs++;
                foreach (var (crestRail, toeRail) in Rails(c.Xyz, t.Xyz, p.CrestZ, p.ToeZ, ref railFail))
                    benches.Add(new DumpStripPlanner.BenchInput { LevelIndex = p.Index, CrestZ = p.CrestZ, ToeZ = p.ToeZ, CrestXyz = crestRail, ToeXyz = toeRail, CrestClosed = false, ToeClosed = false });
            }
            if (railFail > 0) _host.Echo($"排土条带: {rawPairs} 对台阶线里有 {railFail} 处做不出竖直断面轨（尖灭/自交/太碎），已跳过。", true);
        }
        if (benches.Count == 0) { _status.Text = "没有可切的台阶壳子 —— 先点「识别台阶」，看它配出/切出几级。"; SetEmptyHint("还没有台阶壳子可切。先点「识别台阶」。"); return; }

        DumpStripPlanner.Result plan;
        try
        {
            plan = DumpStripPlanner.Plan(benches, new DumpStripPlanner.Options
            {
                PanelLengthM = L, StripWidthM = W, MinStrikeLenM = minLen, MaxSteps = maxSteps, BandMajorPerLevel = true,
                BoundaryRingXy = reg.RingXy, WorkSlopeRingXy = _workSlopeRing,
            }, reg.Name);
        }
        catch (Exception ex) { _status.Text = "切分异常：" + ex.Message; return; }
        _plan = plan;
        var sb = new StringBuilder(plan.Message);
        foreach (var n in plan.Notes) sb.Append("\n" + n);
        if (!plan.Ok)
        {
            DumpStripStore.Put(plan, reg.Name);
            _list.ItemsSource = null;
            SetEmptyHint("切完一个位置都没出来 —— 原因见下面的诊断。");
            _status.Text = sb.ToString();
            _host.Echo("排土条带：" + plan.Message, true);
            return;
        }

        var handles = new ulong[plan.Cells.Count];
        double[] realVols = Array.Empty<double>();
        if (_buildSolids.IsChecked == true)
        {
            var meshes = new List<DumpStripShell.Mesh?>(plan.Cells.Count);
            var vols = new double[plan.Cells.Count];
            int built = 0, bad = 0; double real = 0, est = 0;
            for (int i = 0; i < plan.Cells.Count; i++)
            {
                var c = plan.Cells[i];
                var m = DumpStripShell.Build(c.CrestXyz, c.ToeXyz, c.StripWidthM > 1e-6 ? c.StripWidthM : W);
                meshes.Add(m.Ok ? m : null);
                if (!m.Ok) continue;
                built++; vols[i] = m.VolumeM3; real += m.VolumeM3; est += c.CapacityM3;
                if (c.CapacityM3 > 1 && (m.VolumeM3 < c.CapacityM3 * 0.4 || m.VolumeM3 > c.CapacityM3 * 2.5)) bad++;
            }
            var hs = _host.AddShells($"排土条带-{reg.Name}", meshes);
            for (int i = 0; i < handles.Length && i < hs.Length; i++) handles[i] = hs[i];
            realVols = vols;
            sb.Append($"\n壳子体入图 {built}/{plan.Cells.Count} 个（层「排土条带-{reg.Name}」）。实体方量 {real:N0} m³　估算 {est:N0} m³　比 {(est > 1 ? real / est : 0):0.00}");
            if (bad > 0) sb.Append($"\n⚠ {bad} 条体积与估算差 2 倍以上 —— 那几条的放样有问题，不是尺寸口径的事。");
            if (built < plan.Cells.Count) sb.Append($"\n⚠ {plan.Cells.Count - built} 个位置没建出体（清单里仍在，只是图上没有）。");
            int measured = 0;
            for (int i = 0; i < plan.Cells.Count; i++) if (vols[i] > 1e-6) { plan.Cells[i].CapacityM3 = vols[i]; measured++; }
            if (measured > 0) sb.Append($"\n库容口径：{measured}/{plan.Cells.Count} 个位置已改用【壳体实测体积】{(measured < plan.Cells.Count ? "，其余回落几何公式估算" : "（几何公式仅作对账）")}；合计库容按实测 {plan.TotalCapacityM3:N0} m³（公式估算是 {est:N0} m³）。");
        }
        DumpStripStore.Put(plan, reg.Name);
        _list.ItemsSource = plan.Cells.Select(c => new Row { C = c }).ToList();
        SetEmptyHint(plan.Cells.Count > 0 ? null : "切完一个位置都没出来 —— 原因见下面的诊断。");

        if (_saveDb.IsChecked == true)
        {
            var rows = plan.Cells.Select((c, i) => new DumpStrip
            {
                RegionId = reg.Id, RegionName = reg.Name, Category = reg.Category, Code = c.Code,
                LevelIndex = c.LevelIndex, PanelIndex = c.PanelIndex, PanelCount = c.PanelCount, StepIndex = c.StepIndex, SubIndex = c.SubIndex, SubCount = c.SubCount,
                CrestZ = c.CrestZ, ToeZ = c.ToeZ, BenchHeightM = c.BenchHeightM, StrikeLenM = c.StrikeLenM, StripWidthM = c.StripWidthM, CapacityM3 = c.CapacityM3,
                CentroidX = c.Cx, CentroidY = c.Cy, CentroidZ = c.Cz,
                CrestJson = System.Text.Json.JsonSerializer.Serialize(c.CrestXyz), ToeJson = System.Text.Json.JsonSerializer.Serialize(c.ToeXyz),
                EntityHandle = (long)(i < handles.Length ? handles[i] : 0UL),
                Notes = $"L={L:0.#}m W={W:0.#}m" + (c.IsClamped ? $" 【凹弯夹窄→有效宽{c.StripWidthM:0.#}m】" : "") + " 库容口径=" + ((i < realVols.Length && realVols[i] > 1e-6) ? "壳体实测体积" : "几何公式估算"),
            }).ToList();
            var conn = _host.Db();
            if (conn == null) sb.Append("\n⚠ 未连接工程库：位置清单没落库（寻径/排产按表取用会拿不到这一批）。");
            else
            {
                var rep = DumpStripRepo.ReplaceForRegion(conn, reg.Id, null, rows);
                sb.Append($"\n位置清单已落库 {rep.Inserted} 行（表 dump_strip，寻径/排产可直接取用）。");
                if (!rep.AllOk) sb.Append($"\n⚠ 有 {rep.Failed} 个位置没能落库（清单 {rows.Count} 个）—— 下游按表取用会少这一批。首条：{rep.FirstError}");
            }
        }
        _status.Text = sb.ToString();
        _host.Echo($"排土条带：{plan.Cells.Count} 个潜在排土位置，库容 {plan.TotalCapacityM3:N0} m³", false);
    }

    private async void ExportCsv()
    {
        if (_plan == null || _plan.Cells.Count == 0) { _status.Text = "还没有位置清单可导出 —— 先「生成位置」。"; return; }
        var f = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions { Title = "导出排土位置清单", SuggestedFileName = "排土条带.csv", DefaultExtension = "csv" });
        if (f == null) return;
        var sb = new StringBuilder("编号,级,幅,幅数,带,子号,坡顶Z,坡底Z,台阶高,走向长m,条带宽m,库容m3,质心X,质心Y,质心Z\n");
        foreach (var c in _plan.Cells)
            sb.Append(string.Join(",", c.Code, c.LevelIndex, c.PanelIndex, c.PanelCount, c.StepIndex, c.SubIndex, c.CrestZ.ToString("0.##", Inv), c.ToeZ.ToString("0.##", Inv), c.BenchHeightM.ToString("0.##", Inv),
                c.StrikeLenM.ToString("0.##", Inv), c.StripWidthM.ToString("0.##", Inv), c.CapacityM3.ToString("0", Inv), c.Cx.ToString("0.##", Inv), c.Cy.ToString("0.##", Inv), c.Cz.ToString("0.##", Inv))).Append('\n');
        await System.IO.File.WriteAllTextAsync(f.Path.LocalPath, sb.ToString(), new UTF8Encoding(true));
        _status.Text = $"清单已导出：{f.Path.LocalPath}（{_plan.Cells.Count} 行）";
    }

    // ── 诊断辅助（忠实原窗）──
    private static double MedianZ(double[] xyz)
    {
        var zs = new List<double>();
        for (int i = 2; i < xyz.Length; i += 3) zs.Add(xyz[i]);
        if (zs.Count == 0) return double.NaN;
        zs.Sort(); return zs[zs.Count / 2];
    }

    private static string ElevationHistogram(IReadOnlyList<BenchLevelInventory.SourceLine> lines, int topN = 12)
    {
        var zs = lines.Select(l => MedianZ(l.Xyz)).Where(z => !double.IsNaN(z)).Select(z => Math.Round(z)).ToList();
        if (zs.Count == 0) return "标高分布：（无）";
        var groups = zs.GroupBy(z => z).OrderByDescending(g => g.Key).ToList();
        var drops = new List<double>();
        for (int i = 1; i < groups.Count; i++) drops.Add(groups[i - 1].Key - groups[i].Key);
        drops.Sort();
        string med = drops.Count > 0 ? $"，级距中位 {drops[drops.Count / 2]:0.#} m" : "";
        return $"标高分布（{groups.Count} 档{med}）：" + string.Join(" ", groups.Take(topN).Select(g => $"{g.Key:0}×{g.Count()}")) + (groups.Count > topN ? " …" : "");
    }

    private static IEnumerable<string> SanityWarnings(StandardLevelModel.Result r)
    {
        var hs = r.Pairs.Select(p => p.BenchHeightM).OrderBy(h => h).ToList();
        if (hs.Count > 0 && hs[hs.Count / 2] < 3.0) yield return $"台阶高中位数只有 {hs[hs.Count / 2]:0.##} m —— 多半是等高线不是台阶线。";
        var runs = r.Pairs.Select(p => p.FaceRunM).OrderBy(x => x).ToList();
        if (runs.Count > 0 && runs[runs.Count / 2] > 200) yield return $"坡面投影中位 {runs[runs.Count / 2]:0.#} m —— 配到隔着很远的线上了。";
    }

    private static double Num(TextBox tb, double fallback) => double.TryParse(tb.Text, NumberStyles.Float, Inv, out double v) && v >= 0 ? v : fallback;
}
