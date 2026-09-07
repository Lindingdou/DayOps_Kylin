using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Controls.Charts;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 「创建剖面」非模态窗（原 MeshEditLib.Sections.SectionCutWindow）：剖面线（选中多段线 / 视口两点 / 两点输入）竖直切
/// 场景全部三角网(<see cref="SectionEngine"/>)，输出原位三维交线（按来源网分色）+ 展开剖面图（里程-标高，可垂直夸大，
/// 带网格/刻度/标注/钻孔投影柱状/图名, <see cref="SectionBuilder"/>），算好后在视口点插入点定图位。
/// 窗内附剖面预览图(ChartView)与 CSV 导出。
/// </summary>
public partial class SectionCutWindow : Window
{
    private readonly ModelingContext? _ctx;
    private double[]? _sectionXyz;
    private readonly List<(double[] verts, int[] tris)> _meshes = new();
    private readonly List<string> _meshNames = new();
    private bool _busy;
    private List<SectionChain> _lastChains = new();

    public SectionCutWindow() { InitializeComponent(); }
    public SectionCutWindow(ModelingContext ctx) : this() { _ctx = ctx; }

    // ── ① 剖面线 ─────────────────────────────────────────────
    private void OnSectionModeChanged(object? sender, RoutedEventArgs e)
    {
        if (txtX1 == null || secManual == null || txtY1 == null || txtX2 == null || txtY2 == null) return;
        bool manual = secManual.IsChecked == true;
        txtX1.IsEnabled = txtY1.IsEnabled = txtX2.IsEnabled = txtY2.IsEnabled = manual;
    }

    private void OnPickSection(object? sender, RoutedEventArgs e)
    {
        if (_ctx == null) return;
        if (secManual.IsChecked == true) { secStat.Text = "两点输入模式：直接在 X1/Y1/X2/Y2 填坐标即可，无需拾取。"; return; }
        try
        {
            var lines = _ctx.SelectedPolylines();
            if (lines.Count == 0) { secStat.Text = "视口无选中实体：请先选中一条多段线。"; return; }
            foreach (var l in lines)
            {
                if (l.Points.Count < 2) continue;
                int n = l.Points.Count + (l.Closed ? 1 : 0);   // 闭合线当开线用, 补回首点让最后一段也参与
                var xyz = new double[n * 3];
                for (int i = 0; i < n; i++) { int k = i % l.Points.Count; xyz[i * 3] = l.Points[k].x; xyz[i * 3 + 1] = l.Points[k].y; xyz[i * 3 + 2] = l.ZAt(k); }
                _sectionXyz = xyz;
                double len = 0;
                for (int k = 0; k + 5 < xyz.Length; k += 3) { double dx = xyz[k + 3] - xyz[k], dy = xyz[k + 4] - xyz[k + 1]; len += Math.Sqrt(dx * dx + dy * dy); }
                secStat.Text = $"剖面线已拾取：{n} 顶点 / {n - 1} 段，总长 {len:0.#} m。";
                return;
            }
            secStat.Text = "选中实体里没有多段线。";
        }
        catch (Exception ex) { secStat.Text = $"拾取异常：{ex.Message}"; }
    }

    private double[]? ResolveSection(out string? error)
    {
        error = null;
        if (secManual.IsChecked == true)
        {
            double x1 = ParseD(txtX1.Text, double.NaN), y1 = ParseD(txtY1.Text, double.NaN);
            double x2 = ParseD(txtX2.Text, double.NaN), y2 = ParseD(txtY2.Text, double.NaN);
            if (double.IsNaN(x1) || double.IsNaN(y1) || double.IsNaN(x2) || double.IsNaN(y2)) { error = "两点输入：X1/Y1/X2/Y2 必须都是数字。"; return null; }
            if (Math.Abs(x2 - x1) < 1e-6 && Math.Abs(y2 - y1) < 1e-6) { error = "两点输入：两点重合。"; return null; }
            return new[] { x1, y1, 0, x2, y2, 0 };
        }
        if (_sectionXyz == null) error = "请先「拾取多段线」或「视口拾取两点」定剖面线。";
        return _sectionXyz;
    }

    /// <summary>视口连点两点定剖面线（切到「两点输入」模式并回填 X1/Y1/X2/Y2）。</summary>
    private async void OnPickTwoPoints(object? sender, RoutedEventArgs e)
    {
        if (_ctx == null) return;
        secManual.IsChecked = true;
        secStat.Text = "在视口点第 1 点…（Esc 取消）";
        var p1 = await _ctx.PickPointAsync("创建剖面：在视口点第 1 点（Esc 取消）");
        if (p1 == null) { secStat.Text = "已取消拾取。"; return; }
        secStat.Text = "在视口点第 2 点…";
        var p2 = await _ctx.PickPointAsync("创建剖面：在视口点第 2 点（Esc 取消）");
        if (p2 == null) { secStat.Text = "已取消拾取。"; return; }
        txtX1.Text = p1.Value.x.ToString("0.###", CultureInfo.InvariantCulture);
        txtY1.Text = p1.Value.y.ToString("0.###", CultureInfo.InvariantCulture);
        txtX2.Text = p2.Value.x.ToString("0.###", CultureInfo.InvariantCulture);
        txtY2.Text = p2.Value.y.ToString("0.###", CultureInfo.InvariantCulture);
        _sectionXyz = null;
        secStat.Text = $"两点已拾取：({p1.Value.x:0.#}, {p1.Value.y:0.#}) → ({p2.Value.x:0.#}, {p2.Value.y:0.#})。可直接「生成剖面」。";
    }

    // ── ② 被切三角网：场景全部三角网 ──
    private int GatherMeshes()
    {
        _meshes.Clear(); _meshNames.Clear();
        int triCount = 0;
        foreach (var m in _ctx?.Meshes() ?? Array.Empty<MeshEntity>())
        {
            if (m.Verts.Count < 3 || m.Tris.Count < 1) continue;
            var (v, t) = m.Flatten();
            _meshes.Add((v, t)); _meshNames.Add(m.Name); triCount += t.Length / 3;
        }
        return triCount;
    }

    // ── 生成 ─────────────────────────────────────────────────
    private async void OnGenerate(object? sender, RoutedEventArgs e)
    {
        if (_busy || _ctx == null) return;
        var section = ResolveSection(out string? secErr);
        if (section == null) { SetStatus(secErr ?? "剖面线无效。"); return; }
        bool want3d = chk3d.IsChecked == true, wantProfile = chkProfile.IsChecked == true;
        if (!want3d && !wantProfile) { SetStatus("原位交线与展开剖面图至少勾一个。"); return; }

        try { GatherMeshes(); }
        catch (Exception ex) { SetStatus($"读取三角网异常：{ex.Message}"); return; }
        if (_meshes.Count == 0) { SetStatus("场景没有可切的三角网。剖面只切三角网「面」——点/多段线不算；请先创建/导入三角网。"); return; }

        var o = new SectionBuilder.Options
        {
            Want3d = want3d, WantProfile = wantProfile,
            Layer3d = string.IsNullOrWhiteSpace(txtLayer3d.Text) ? "剖面交线" : txtLayer3d.Text.Trim(),
            LayerProfile = string.IsNullOrWhiteSpace(txtLayerProfile.Text) ? "剖面图" : txtLayerProfile.Text.Trim(),
            Vex = Math.Max(0.01, ParseD(txtVex.Text, 1)),
            GridZ = Math.Max(0.1, ParseD(txtGridZ.Text, 10)),
            GridS = Math.Max(1, ParseD(txtGridS.Text, 100)),
            TextH = Math.Max(0.1, ParseD(txtTextH.Text, 2)),
            SecName = txtSecName.Text?.Trim() ?? "",
            ColW = Math.Max(0.5, ParseD(txtColW.Text, 4)),
            LabelHoleId = chkHoleId.IsChecked == true, LabelOffset = chkOffsetLabel.IsChecked == true,
        };
        bool clearOld = chkClearOld.IsChecked == true;
        var meshesSnapshot = _meshes.ToList();

        // 钻孔投影（读库；库为空/未就绪自动跳过）
        string boreWarn = "";
        var bores = wantProfile && chkBoreholes.IsChecked == true
            ? GeoDbViews.CollectSectionBoreholes(_ctx.Conn(), section, Math.Max(0, ParseD(txtBand.Text, 100)), out boreWarn)
            : new List<SectionBuilder.BoreProj>();

        _busy = true; btnGenerate.IsEnabled = false;
        SetStatus("计算中…");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        List<SectionChain> chains; double totalLen; string warning;
        try
        {
            var res = await Task.Run(() => { var built = SectionEngine.Build(meshesSnapshot, section, out double len, out string warn); return (built, len, warn); });
            chains = res.built; totalLen = res.len; warning = res.warn;
        }
        catch (Exception ex) { SetStatus($"生成异常：{ex.Message}"); _ctx.Status($"创建剖面异常: {ex.Message}"); _busy = false; btnGenerate.IsEnabled = true; return; }
        sw.Stop();
        if (chains.Count == 0) { SetStatus($"剖面线没有切到任何三角网（平面位置是否相交？）。{warning}"); _busy = false; btnGenerate.IsEnabled = true; return; }
        _lastChains = chains;
        UpdateChart(chains);

        double zMin = SectionBuilder.MinZ(chains), zMax = SectionBuilder.MaxZ(chains);
        foreach (var bp in bores) { if (bp.ZCollar > zMax) zMax = bp.ZCollar; if (bp.ZBottom < zMin) zMin = bp.ZBottom; }
        double autoX = section[0], autoY = section[1] - ((zMax - zMin) * o.Vex + 80);

        try
        {
            double baseX = autoX, baseY = autoY;
            if (wantProfile)
            {
                SetStatus("剖面已算好 —— 请在视口点插入点（展开剖面图左下角基点）… Esc 用自动基点。");
                var p = await _ctx.PickPointAsync("创建剖面：点插入点（展开剖面图左下角基点）；Esc 用自动基点");
                if (p != null) { baseX = p.Value.x; baseY = p.Value.y; }
            }
            if (clearOld)
            {
                int removed = _ctx.RemoveLayerEntities(o.Layer3d) + _ctx.RemoveLayerEntities(o.LayerProfile);
                if (removed > 0) _ctx.Status($"> 创建剖面：已清除旧结果 {removed} 个实体");
            }
            o.BaseX = baseX; o.BaseY = baseY;
            var ents = SectionBuilder.BuildEntities(chains, totalLen, section, o, bores, out double bx, out double by);
            var e3 = ents.Where(en => en.LayerName == o.Layer3d).ToList();
            var ep = ents.Where(en => en.LayerName == o.LayerProfile).ToList();
            if (e3.Count > 0) _ctx.AddEntities(e3, o.Layer3d, null);
            if (ep.Count > 0) _ctx.AddEntities(ep, o.LayerProfile, null);
            string msg = $"剖面线 {totalLen:0.#} m，切到 {chains.Count} 条交线（{meshesSnapshot.Count} 张网），" +
                         (bores.Count > 0 ? $"投影钻孔 {bores.Count} 个，" : "") +
                         $"标高 {zMin:0.#} ~ {zMax:0.#} m，用时 {sw.Elapsed.TotalSeconds:0.0} s。" +
                         (want3d ? $"交线入「{o.Layer3d}」；" : "") +
                         (wantProfile ? $"剖面图入「{o.LayerProfile}」(基点 {bx:0.#}, {by:0.#})。" : "") + warning + boreWarn;
            SetStatus("生成完成 — " + msg);
            _ctx.Status("> 创建剖面：" + msg);
        }
        catch (Exception ex) { SetStatus($"生成异常：{ex.Message}"); _ctx.Status($"创建剖面异常: {ex.Message}"); }
        finally { _busy = false; btnGenerate.IsEnabled = true; }
    }

    private void UpdateChart(List<SectionChain> chains)
    {
        chart.Series.Clear();
        var byMesh = chains.GroupBy(c => c.MeshIndex).OrderBy(g => g.Key);
        foreach (var g in byMesh)
        {
            var (r, gg, b) = SectionBuilder.Palette[g.Key % SectionBuilder.Palette.Length];
            string name = g.Key < _meshNames.Count ? _meshNames[g.Key] : $"面{g.Key + 1}";
            foreach (var c in g)
            {
                var s = new ChartSeries { Name = name, Kind = SeriesKind.Line, Color = Color.FromRgb(r, gg, b), ShowMarkers = false, StrokeThickness = 1.6 };
                for (int i = 0; i + 1 < c.Sz.Count; i += 2) s.Points.Add((c.Sz[i], c.Sz[i + 1]));
                chart.Series.Add(s);
            }
        }
        chart.Refresh();
    }

    private async void OnExportCsv(object? sender, RoutedEventArgs e)
    {
        if (_ctx == null) return;
        if (_lastChains.Count == 0) { SetStatus("尚无剖面结果可导出，先「生成剖面」。"); return; }
        var name = await _ctx.SaveTextAsync("导出剖面 (CSV)", "剖面.csv", SectionBuilder.ToCsv(_lastChains, _meshNames));
        if (name != null) SetStatus($"已导出 {name}");
    }

    private void OnClearResult(object? sender, RoutedEventArgs e)
    {
        if (_ctx == null) return;
        try
        {
            string l3 = string.IsNullOrWhiteSpace(txtLayer3d.Text) ? "剖面交线" : txtLayer3d.Text.Trim();
            string lp = string.IsNullOrWhiteSpace(txtLayerProfile.Text) ? "剖面图" : txtLayerProfile.Text.Trim();
            int removed = _ctx.RemoveLayerEntities(l3) + _ctx.RemoveLayerEntities(lp);
            SetStatus(removed > 0 ? $"已清除「{l3}」「{lp}」共 {removed} 个实体。" : "目标图层没有可清除的实体。");
        }
        catch (Exception ex) { SetStatus($"清除异常：{ex.Message}"); }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void SetStatus(string text) => statusText.Text = text;

    private static double ParseD(string? s, double fallback) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
}
