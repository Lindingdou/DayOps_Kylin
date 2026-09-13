using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Plan;
using PitMine3D.Kylin.Views.Road;

namespace PitMine3D.Kylin.Views.Plan;

/// <summary>
/// 「采场参数识别」窗口（短期组；移植原 <c>ShortTermFieldWindow</c>）。从现状台阶线识别采场几何参数：
/// ①「参数自动校核提取」——从坡顶/坡底线反推现状台阶参数(H/α/W/β)并对设计/规范校核（<see cref="BenchParameterExtractor"/> + <see cref="ParameterVerifier"/>）+ 回写验收库 + 写进短期计划基础约束；
/// ②「按平盘宽度提取区域」——圈出平盘宽度 ≥ 目标值的达标平盘条带（<see cref="BenchWidthIdentifier"/>），结果只在本窗口维护一份列表（改名/显隐/删除/清空 + overlay），不写入可采区域库。
/// </summary>
internal sealed class ShortTermFieldWindow : Window
{
    private readonly IPlanEntityHost _host;
    private readonly ObservableCollection<VerifyRowVm> _verifyRows = new();
    private readonly ObservableCollection<WideBenchRow> _benchRows = new();
    private ParameterVerifier.Report? _lastReport;
    private BenchParameterExtractor.Result? _lastMeasure;
    private bool _linesClassified;

    private readonly RadioButton _catPit = new() { Content = "采场", IsChecked = true, GroupName = "landformCat", Margin = new Thickness(0, 0, 10, 0) };
    private readonly RadioButton _catDump = new() { Content = "排土场", GroupName = "landformCat", Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBox _phi = PlanUi.Box("", 48), _location = PlanUi.Box("现状面", 120), _widthTarget = PlanUi.Box("40", 56), _benchName = PlanUi.Box("", 140);
    private readonly Button _writeBackBtn, _toPlanBtn, _bermWidthBtn;
    private readonly DataGrid _verifyGrid, _benchGrid;
    private readonly TextBlock _extractHint = RoadUi.Hint("先在视口选中台阶线（坡顶+坡底一起选，不分图层），再点「提取并校核」。分得出坡顶线/坡底线（图层 点云_坡顶线 / 点云_坡底线）时平盘宽与帮坡角才是真量的。", 12),
                               _verifyStatus = RoadUi.Hint("", 12),
                               _bermHint = RoadUi.Hint("提取的「达标平盘」只在本窗口的列表里维护（亮品红 overlay），在此改名/显隐/删除；不写入可采区域库。", 12);

    public ShortTermFieldWindow(IPlanEntityHost host)
    {
        _host = host;
        Title = "采场参数识别 — 参数校核 / 按平盘宽度提取区域";
        PlanUi.Place(this, 800, 640);

        _verifyGrid = PlanUi.Table(new (string, string, double)[] { ("参数", "Name", 110), ("实测", "MeasuredText", 80), ("设计", "DesignText", 80), ("规范区间", "RangeText", 104), ("偏差%", "DevText", 76), ("判定", "StatusText", 76), ("来源", "Source", 160) }, multi: false);
        _verifyGrid.ItemsSource = _verifyRows; _verifyGrid.MaxHeight = 170;
        _benchGrid = PlanUi.Table(new (string, string, double)[] { ("名称", "Name", 150), ("代表宽", "RepWidthText", 84), ("面积", "AreaText", 84), ("显隐", "VisibleText", 64) }, multi: false);
        _benchGrid.ItemsSource = _benchRows; _benchGrid.MaxHeight = 150;
        _benchGrid.SelectionChanged += (_, _) => { if (SelectedBench is { } row) _benchName.Text = row.Name; RefreshBenchOverlay(); };

        _writeBackBtn = RoadUi.Btn("回写验收库", OnWriteBack, 96); _writeBackBtn.IsEnabled = false;
        ToolTip.SetTip(_writeBackBtn, "把上次校核结果写进 parameter_acceptance（进现有报警面板/趋势图）");
        _toPlanBtn = RoadUi.Btn("写进短期计划", OnPushToPlanBase, 106); _toPlanBtn.IsEnabled = false;
        ToolTip.SetTip(_toPlanBtn, "把实测的 H/α/W/β 写进「短期生产计划编制」的基础约束（逐项说明覆盖了什么；没量出来的不写）");
        _bermWidthBtn = RoadUi.Btn("按平盘宽度识别区域", OnIdentifyByBermWidth, 140);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        var header = PlanUi.Header("采场参数识别", "从现状台阶线反推台阶参数并校核 · 按平盘宽度圈定达标平盘区域", Color.FromRgb(0xF9, 0x73, 0x16), Color.FromRgb(0xC2, 0x41, 0x0C));
        Grid.SetRow(header, 0); root.Children.Add(header);

        var stack = new StackPanel { Margin = new Thickness(12, 10, 12, 4) };
        var g1 = new StackPanel();
        var r1 = RoadUi.Row(RoadUi.Lbl("区域类别"), _catPit, _catDump, RoadUi.Lbl("摩擦角φ(°)"), _phi, RoadUi.Btn("提取并校核", OnExtractVerify, 96));
        ToolTip.SetTip(_phi, "选填：给了就算无黏聚力稳定性下界 F=tanφ/tanβ");
        g1.Children.Add(r1);
        var r1b = RoadUi.Row(RoadUi.Lbl("部位编号"), _location, _writeBackBtn, _toPlanBtn); r1b.Margin = new Thickness(0, 6, 0, 0);
        ToolTip.SetTip(_location, "验收库 location_code");
        g1.Children.Add(r1b);
        _extractHint.Margin = new Thickness(0, 6, 0, 4); g1.Children.Add(_extractHint);
        g1.Children.Add(_verifyGrid);
        _verifyStatus.Margin = new Thickness(0, 6, 0, 0); g1.Children.Add(_verifyStatus);
        stack.Children.Add(PlanUi.Group("参数自动校核提取（从坡顶/坡底线反推现状台阶参数 + 校核）", g1, new Thickness(0, 0, 0, 8), 8));

        var g2 = new StackPanel();
        var r2 = RoadUi.Row(RoadUi.Lbl("平盘宽度 ≥"), _widthTarget, RoadUi.Lbl("m"), _bermWidthBtn);
        ToolTip.SetTip(_widthTarget, "目标平盘宽度（米）：平盘宽 ≥ 此值的条带算达标");
        g2.Children.Add(r2);
        _benchGrid.Margin = new Thickness(0, 6, 0, 0); g2.Children.Add(_benchGrid);
        var r2b = RoadUi.Row(RoadUi.Lbl("名称"), _benchName, RoadUi.Btn("重命名", OnRenameBench, 70), RoadUi.Btn("显示/隐藏", OnToggleBenchVisible, 78), RoadUi.Btn("删除", OnDeleteBench, 64), RoadUi.Btn("清空", OnClearBenches, 64));
        r2b.Margin = new Thickness(0, 6, 0, 0); g2.Children.Add(r2b);
        _bermHint.Margin = new Thickness(0, 6, 0, 0); g2.Children.Add(_bermHint);
        stack.Children.Add(PlanUi.Group("按平盘宽度提取区域（圈出平盘宽度 ≥ 目标值的条带 · 仅本窗口列表管理，不入库）", g2, new Thickness(0), 8));
        var sv = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(sv, 1); root.Children.Add(sv);

        var foot = new DockPanel();
        var close = RoadUi.Btn("关闭", Close, 70); DockPanel.SetDock(close, Avalonia.Controls.Dock.Right); foot.Children.Add(close);
        var note = RoadUi.Hint("工作历 / 设备等现场作业参数已移到「短期生产计划编制」窗口编辑。", 12); note.VerticalAlignment = VerticalAlignment.Center; foot.Children.Add(note);
        var footB = PlanUi.Footer(foot); Grid.SetRow(footB, 2); root.Children.Add(footB);
        Content = root;
        Closed += (_, _) => { try { _host.ClearMineableAreaOverlay(); } catch { } };
    }

    // ════════════════════ 参数自动校核提取 ════════════════════

    private void OnExtractVerify()
    {
        _verifyRows.Clear();
        var lines = new List<BenchParameterExtractor.Line>();
        ReadSelectedLines(lines);
        if (lines.Count == 0) { _verifyStatus.Text = "请先在视口选中台阶线（坡顶+坡底一起选，不分图层），再点「提取并校核」。"; return; }

        var m = BenchParameterExtractor.Extract(lines);
        if (!m.Ok) { _verifyStatus.Text = m.Message; return; }

        bool isDump = _catDump.IsChecked == true;
        double? phi = double.TryParse(_phi.Text, NumberStyles.Any, CultureInfo.CurrentCulture, out var p) && p > 0 ? p : null;
        var rep = ParameterVerifier.Verify(m, isDump, frictionAngleDeg: phi, conn: _host.Db);
        foreach (var r in rep.Rows) _verifyRows.Add(new VerifyRowVm(r));
        _lastReport = rep; _lastMeasure = m;
        _writeBackBtn.IsEnabled = true; _toPlanBtn.IsEnabled = true;
        // ★ 分不出坡顶/坡底时，量出来的"坡面角"其实是整体帮坡角、平盘宽量不出来 —— 必须说出来。
        _extractHint.Text = m.Message + (_linesClassified
            ? "　· 已按图层分出坡顶线/坡底线，平盘宽与帮坡角是真量的。"
            : "　◆ **选中的线里分不出坡顶线/坡底线**（图层不是 点云_坡顶线 / 点云_坡底线）——这种情况下量出来的「坡面角」实际是**整体帮坡角**，「平盘宽」量不出来。请分别选中两类线，或先用「点云 → 坡顶/坡底断棱线」把它们提出来。");
        string notes = rep.Notes.Count > 0 ? "　" + string.Join("；", rep.Notes) : "";
        _verifyStatus.Text = $"总判定：{VerifyRowVm.StatusZh(rep.OverallStatus)}（设计依据：{rep.DesignProvenance}）{notes}";
        _host.Echo("采场参数识别：" + _verifyStatus.Text, rep.OverallStatus == "fail");
    }

    /// <summary>把实测的台阶几何写进「短期生产计划编制」的基础约束（覆盖必须逐项说出来；没量出来的不写，不拿 0 覆盖）。</summary>
    private void OnPushToPlanBase()
    {
        var m = _lastMeasure;
        if (m == null || !m.Ok) { _verifyStatus.Text = "请先「提取并校核」。"; return; }
        var b = ShortTermSchemeStore.Base;
        var changed = new List<string>(); var skipped = new List<string>();
        void Put(string name, double measured, double old, Action<double> set, string unit, double eps)
        {
            if (!(measured > 1e-6)) { skipped.Add(name); return; }
            if (Math.Abs(measured - old) <= eps) return;
            set(measured);
            changed.Add(old > 1e-6 ? $"{name} {old:0.##}→{measured:0.##}{unit}" : $"{name} {measured:0.##}{unit}（原来是空的）");
        }
        Put("台阶高", m.BenchHeight, b.BenchHeightM, v => b.BenchHeightM = v, "m", 0.05);
        Put("坡面角", m.FaceAngleDeg, b.BenchFaceAngleDeg, v => b.BenchFaceAngleDeg = v, "°", 0.1);
        Put("平盘宽", m.BermWidth, b.BermWidthM, v => b.BermWidthM = v, "m", 0.05);
        Put("帮坡角", m.OverallSlopeAngleDeg, b.OverallSlopeAngleDeg, v => b.OverallSlopeAngleDeg = v, "°", 0.1);
        var sb = new System.Text.StringBuilder();
        sb.Append(changed.Count > 0 ? "✔ 已写进短期计划基础约束：" + string.Join("　", changed) : "· 实测值与基础约束里的现值一致，没有改动。");
        if (skipped.Count > 0) sb.Append($"　◆ 没量出来的没写：{string.Join("、", skipped)}（保持原值，不拿 0 覆盖）");
        sb.Append(b.HasSlopeGeometry ? "　—— 三维现在能建**真台阶**了。" : "　◆ 坡面角或平盘宽仍为空 ⇒ 三维层体只能建**垂直壁**。");
        sb.Append("　⚠ 基础约束是会话内的，关掉软件就没了；要留住请在「短期生产计划编制」里「保存约束」。");
        _verifyStatus.Text = sb.ToString();
    }

    /// <summary>把上次校核结果写进 parameter_acceptance（进现有报警面板/趋势图）。</summary>
    private void OnWriteBack()
    {
        if (_lastReport == null) { _verifyStatus.Text = "请先「提取并校核」。"; return; }
        string loc = (_location.Text ?? "").Trim();
        if (loc.Length == 0) { _verifyStatus.Text = "请填部位编号（验收库 location_code）。"; return; }
        var recs = ParameterVerifier.ToAcceptanceRecords(_lastReport, loc, DateTime.Now);
        if (recs.Count == 0) { _verifyStatus.Text = "无可回写项（GeoDataBase 未就绪或无对应规范参数定义）。"; return; }
        int n = 0;
        try { foreach (var r in recs) { Data.EquipmentDataContext.ParameterAcceptance.Insert(r); n++; } }
        catch (Exception ex) { _verifyStatus.Text = "回写失败：" + ex.Message; return; }
        _verifyStatus.Text = $"已回写 {n} 项到验收库（部位「{loc}」）——可在现有报警面板/趋势图查看。";
    }

    // ════════════════════ 按平盘宽度提取区域 ════════════════════

    private void OnIdentifyByBermWidth()
    {
        if (!double.TryParse((_widthTarget.Text ?? "").Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out double wTarget) || wTarget <= 0)
        { _bermHint.Text = "请输入有效的目标平盘宽度（米，正数）。"; return; }
        var lines = new List<double[]>();
        bool fromSelection = ReadLinesForWidth(lines);
        if (lines.Count == 0) { _bermHint.Text = "图中没有现状线可用（请先有台阶线，或在视口选中要识别的线）。"; return; }
        var r = BenchWidthIdentifier.Identify(lines, wTarget);
        if (!r.Ok) { _bermHint.Text = r.Message; return; }
        _benchRows.Clear();
        var used = new HashSet<string>();
        foreach (var reg in r.Regions)
        {
            string name = NextWideBenchName(used); used.Add(name);
            var flat = new double[reg.Polygon.Count * 3];
            for (int i = 0; i < reg.Polygon.Count; i++) { flat[i * 3] = reg.Polygon[i].x; flat[i * 3 + 1] = reg.Polygon[i].y; flat[i * 3 + 2] = reg.Z; }
            _benchRows.Add(new WideBenchRow(name, flat, reg.RepWidthM, reg.AreaHa, wTarget));
        }
        _benchGrid.ItemsSource = null; _benchGrid.ItemsSource = _benchRows;
        RefreshBenchOverlay();
        _bermHint.Text = $"{r.Message}（用{(fromSelection ? "选中" : "全部")}现状线）已列出 {_benchRows.Count} 块达标平盘——在下方列表管理（改名/显隐/删除/清空），不写入数据库。";
        _host.Echo("采场参数识别：" + _bermHint.Text, false);
    }

    /// <summary>读现状线：视口选中的优先，没选中就用图中全部多段线。返回是否来自选中集。</summary>
    private bool ReadLinesForWidth(List<double[]> outList)
    {
        var sel = _host.SelectedHandles();
        bool fromSelection = sel.Length > 0;
        IEnumerable<long> handles = fromSelection ? sel : _host.ListEntities(PlanEntityType.Polyline).Select(e => e.handle);
        foreach (var h in handles)
            if (_host.TryGetPolylineWorldVertices(h, out var xyz, out _) && xyz != null && xyz.Length >= 6) outList.Add(xyz);
        return fromSelection;
    }

    private WideBenchRow? SelectedBench => _benchGrid.SelectedItem as WideBenchRow;

    private void OnRenameBench()
    {
        if (SelectedBench is not { } row) { _bermHint.Text = "请先在列表里选中一块达标平盘。"; return; }
        string name = (_benchName.Text ?? "").Trim();
        if (name.Length == 0) { _bermHint.Text = "名称不能为空。"; return; }
        row.Name = name; RefreshBenchGrid(row);
        _bermHint.Text = $"已重命名为「{name}」。";
    }

    private void OnToggleBenchVisible()
    {
        if (SelectedBench is not { } row) { _bermHint.Text = "请先在列表里选中一块达标平盘。"; return; }
        row.Visible = !row.Visible; RefreshBenchGrid(row); RefreshBenchOverlay();
        _bermHint.Text = $"「{row.Name}」{(row.Visible ? "已显示" : "已隐藏")}。";
    }

    private void OnDeleteBench()
    {
        if (SelectedBench is not { } row) { _bermHint.Text = "请先在列表里选中一块达标平盘。"; return; }
        _benchRows.Remove(row); RefreshBenchOverlay();
        _bermHint.Text = $"已删除「{row.Name}」。";
    }

    private void OnClearBenches()
    {
        if (_benchRows.Count == 0) { _bermHint.Text = "列表为空，无需清空。"; return; }
        int n = _benchRows.Count; _benchRows.Clear(); RefreshBenchOverlay();
        _bermHint.Text = $"已清空 {n} 块达标平盘。";
    }

    private void RefreshBenchGrid(WideBenchRow keep) { _benchGrid.ItemsSource = null; _benchGrid.ItemsSource = _benchRows; _benchGrid.SelectedItem = keep; }

    /// <summary>把列表里"显示中"的达标平盘推到 overlay（亮品红；选中块亮黄高亮）。</summary>
    private void RefreshBenchOverlay()
    {
        var sel = SelectedBench;
        var rings = new List<double[]>(); var colors = new List<uint>();
        foreach (var row in _benchRows)
        {
            if (!row.Visible || row.PolygonXyz.Length < 9) continue;
            rings.Add(row.PolygonXyz); colors.Add(row == sel ? 0xFFE000u : 0xFF33CCu);
        }
        try { _host.ShowMineableAreaOverlay(rings, colors); } catch { }
    }

    private static string NextWideBenchName(HashSet<string> used)
    {
        for (int i = 1; ; i++) { string nm = $"达标平盘{i}"; if (!used.Contains(nm)) return nm; }
    }

    private static readonly string[] CrestLayers = { "点云_坡顶线", "坡顶线" };
    private static readonly string[] ToeLayers = { "点云_坡底线", "坡底线" };

    /// <summary>读视口选中的多段线作台阶线，按图层分坡顶/坡底；分不出类时退回"同一集兼作两端"并如实说明（W/β 量不了）。</summary>
    private void ReadSelectedLines(List<BenchParameterExtractor.Line> outList)
    {
        _linesClassified = false;
        var sel = _host.SelectedHandles();
        if (sel.Length == 0) return;
        var crestSet = HandlesOf(CrestLayers); var toeSet = HandlesOf(ToeLayers);
        int nc = 0, nt = 0;
        var unknown = new List<double[]>();
        foreach (var h in sel)
        {
            if (!_host.TryGetPolylineWorldVertices(h, out var xyz, out _) || xyz == null || xyz.Length < 6) continue;
            if (crestSet.Contains(h)) { outList.Add(BenchParameterExtractor.Line.From(xyz, isCrest: true)); nc++; }
            else if (toeSet.Contains(h)) { outList.Add(BenchParameterExtractor.Line.From(xyz, isCrest: false)); nt++; }
            else unknown.Add(xyz);
        }
        if (nc > 0 && nt > 0)
        {
            _linesClassified = true;
            foreach (var xyz in unknown) { outList.Add(BenchParameterExtractor.Line.From(xyz, isCrest: true)); outList.Add(BenchParameterExtractor.Line.From(xyz, isCrest: false)); }
            return;
        }
        outList.Clear();
        foreach (var h in sel)
            if (_host.TryGetPolylineWorldVertices(h, out var xyz, out _) && xyz != null && xyz.Length >= 6)
            { outList.Add(BenchParameterExtractor.Line.From(xyz, isCrest: true)); outList.Add(BenchParameterExtractor.Line.From(xyz, isCrest: false)); }
    }

    private HashSet<long> HandlesOf(string[] layers)
    {
        var set = new HashSet<long>();
        foreach (var lay in layers) { try { foreach (var h in _host.GetHandlesByLayer(lay)) set.Add(h); } catch { } }
        return set;
    }

    /// <summary>自检直通：提取并校核当前选集，返回状态。</summary>
    internal string SelftestExtract() { OnExtractVerify(); return _verifyStatus.Text ?? ""; }
    internal int SelftestRowCount => _verifyRows.Count;
    internal string SelftestIdentify(double w) { _widthTarget.Text = w.ToString(CultureInfo.InvariantCulture); OnIdentifyByBermWidth(); return _bermHint.Text ?? ""; }

    /// <summary>达标平盘列表行（仅内存，不入库）。</summary>
    public sealed class WideBenchRow
    {
        public WideBenchRow(string name, double[] polygonXyz, double repWidthM, double areaHa, double targetW)
        { Name = name; PolygonXyz = polygonXyz ?? Array.Empty<double>(); RepWidthM = repWidthM; AreaHa = areaHa; TargetW = targetW; Visible = true; }
        public string Name { get; set; }
        public double[] PolygonXyz { get; }
        public double RepWidthM { get; }
        public double AreaHa { get; }
        public double TargetW { get; }
        public bool Visible { get; set; }
        public string RepWidthText => RepWidthM.ToString("0.#") + "m";
        public string AreaText => AreaHa.ToString("0.#") + "ha";
        public string VisibleText => Visible ? "显示" : "隐藏";
    }

    /// <summary>校核报告行视图模型。</summary>
    public sealed class VerifyRowVm
    {
        private readonly ParameterVerifier.Row _r;
        public VerifyRowVm(ParameterVerifier.Row r) { _r = r; }
        public string Name => _r.Name;
        public string MeasuredText => _r.Measured.ToString("0.#") + _r.Unit;
        public string DesignText => _r.Design.HasValue ? _r.Design.Value.ToString("0.#") + _r.Unit : "—";
        public string RangeText => (_r.StdMin.HasValue || _r.StdMax.HasValue) ? $"{Fmt(_r.StdMin)}~{Fmt(_r.StdMax)}" : "—";
        public string DevText => _r.DeviationPct.HasValue ? _r.DeviationPct.Value.ToString("+0.#;-0.#;0") + "%" : "—";
        public string StatusText => StatusZh(_r.Status);
        public string Source => _r.Source;
        private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0.#") : "";
        public static string StatusZh(string s) => s switch { "pass" => "✔合格", "warning" => "⚠偏差", "fail" => "✘超限", _ => "—" };
    }
}
