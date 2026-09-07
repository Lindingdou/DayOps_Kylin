using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 「构建等值线」非模态窗（原 MeshEditLib.Contour.ContourBuilderWindow）。
/// 拾取（UI 线程, 场景快照几何）→ 引擎计算（Task.Run 后台, <see cref="ContourEngine"/>）→ 三维多段线(Zs=层值)+标注入场景（UI 线程）。
/// 数据源：视口选择 / 按图层(勾选) / 全部实体；点/线源经 Delaunay 统一构网后切割, 面(三角网)源逐三角形直接切割。
/// </summary>
public partial class ContourBuilderWindow : Window
{
    private readonly ModelingContext? _ctx;

    public sealed class LayerItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        private bool _sel;
        public bool IsSelected { get => _sel; set { if (_sel != value) { _sel = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly ObservableCollection<LayerItem> _layers = new();

    // 拾取快照（世界坐标）
    private readonly List<(double[] verts, int[] tris)> _meshes = new();
    private readonly List<(double[] xyz, bool closed)> _lines = new();
    private readonly List<double> _points = new();
    private bool _busy;

    public ContourBuilderWindow() { InitializeComponent(); layerList.ItemsSource = _layers; }
    public ContourBuilderWindow(ModelingContext ctx) : this() { _ctx = ctx; RefreshLayers(); }

    // ── ① 数据源 ─────────────────────────────────────────────
    private void OnSourceModeChanged(object? sender, RoutedEventArgs e)
    {
        if (layerList == null || srcLayer == null) return;
        layerList.IsEnabled = srcLayer.IsChecked == true;
    }

    private void OnRefreshLayers(object? sender, RoutedEventArgs e) => RefreshLayers();

    private void RefreshLayers()
    {
        var keep = new HashSet<string>(_layers.Where(l => l.IsSelected).Select(l => l.Name));
        _layers.Clear();
        foreach (string name in _ctx?.LayerNames() ?? Array.Empty<string>())
            _layers.Add(new LayerItem { Name = name, IsSelected = keep.Contains(name) });
    }

    private void OnPickData(object? sender, RoutedEventArgs e)
    {
        if (_ctx == null) return;
        try
        {
            IEnumerable<MeshEntity> meshes; IEnumerable<PolylineEntity> lines; IEnumerable<PointEntity> points;
            if (srcPick.IsChecked == true) { meshes = _ctx.SelectedMeshes(); lines = _ctx.SelectedPolylines(); points = _ctx.SelectedPoints(); }
            else if (srcLayer.IsChecked == true)
            {
                var picked = new HashSet<string>(_layers.Where(l => l.IsSelected).Select(l => l.Name));
                if (picked.Count == 0) { SetStatus("按图层拾取：请先在列表勾选至少一个图层。"); return; }
                meshes = _ctx.Meshes().Where(m => picked.Contains(m.LayerName));
                lines = _ctx.Polylines().Where(l => picked.Contains(l.LayerName));
                points = _ctx.Points().Where(p => picked.Contains(p.LayerName));
            }
            else { meshes = _ctx.Meshes(); lines = _ctx.Polylines(); points = _ctx.Points(); }

            _meshes.Clear(); _lines.Clear(); _points.Clear();
            int lineVerts = 0, triCount = 0;
            double zMin = double.MaxValue, zMax = double.MinValue;
            void SeeZ(double z) { if (z < zMin) zMin = z; if (z > zMax) zMax = z; }
            foreach (var m in meshes)
            {
                if (m.Verts.Count < 3 || m.Tris.Count < 1) continue;
                var (v, t) = m.Flatten();
                _meshes.Add((v, t)); triCount += t.Length / 3;
                for (int k = 2; k < v.Length; k += 3) SeeZ(v[k]);
            }
            foreach (var l in lines)
            {
                if (l.Points.Count < 2) continue;
                var xyz = new double[l.Points.Count * 3];
                for (int i = 0; i < l.Points.Count; i++) { xyz[i * 3] = l.Points[i].x; xyz[i * 3 + 1] = l.Points[i].y; xyz[i * 3 + 2] = l.ZAt(i); SeeZ(xyz[i * 3 + 2]); }
                _lines.Add((xyz, l.Closed)); lineVerts += l.Points.Count;
            }
            foreach (var p in points) { _points.Add(p.X); _points.Add(p.Y); _points.Add(p.Elevation); SeeZ(p.Elevation); }

            int pc = _points.Count / 3;
            if (pc == 0 && _lines.Count == 0 && _meshes.Count == 0)
            {
                SetStatus(srcPick.IsChecked == true ? "视口无选中实体：请先在视口框选/点选点、多段线或三角网，再点「拾取数据」。" : "拾取的实体里没有点 / 多段线 / 三角网。");
                return;
            }
            if (zMin <= zMax)
            {
                txtRangeMin.Text = zMin.ToString("0.##", CultureInfo.InvariantCulture);
                txtRangeMax.Text = zMax.ToString("0.##", CultureInfo.InvariantCulture);
            }
            pickStat.Text = $"已拾取：{pc:N0} 点 · {_lines.Count:N0} 条线({lineVerts:N0} 顶点) · {_meshes.Count:N0} 张网({triCount:N0} 三角形) — 值域 {zMin:0.##} ~ {zMax:0.##} m";
            SetStatus("拾取完成，调整参数后点「生成等值线」。");
        }
        catch (Exception ex) { SetStatus($"拾取异常：{ex.Message}"); }
    }

    // ── ②④ 参数联动 ─────────────────────────────────────────
    private void OnLevelModeChanged(object? sender, RoutedEventArgs e)
    {
        if (txtLevels == null || lvList == null || txtInterval == null || txtBase == null) return;
        bool list = lvList.IsChecked == true;
        txtLevels.IsEnabled = list; txtInterval.IsEnabled = !list; txtBase.IsEnabled = !list;
    }

    private void OnOutputModeChanged(object? sender, RoutedEventArgs e)
    {
        if (cmbChaikin == null || txtSplineStep == null || outChaikin == null || outSpline == null) return;
        cmbChaikin.IsEnabled = outChaikin.IsChecked == true;
        txtSplineStep.IsEnabled = outSpline.IsChecked == true;
    }

    // ── 生成 ─────────────────────────────────────────────────
    private async void OnGenerate(object? sender, RoutedEventArgs e)
    {
        if (_busy || _ctx == null) return;
        if (_points.Count == 0 && _lines.Count == 0 && _meshes.Count == 0) { SetStatus("请先点「拾取数据」。"); return; }

        var opt = new ContourOptions
        {
            RangeMin = ParseD(txtRangeMin.Text, double.NegativeInfinity),
            RangeMax = ParseD(txtRangeMax.Text, double.PositiveInfinity),
            Interval = ParseD(txtInterval.Text, 5),
            BaseLevel = ParseD(txtBase.Text, 0),
            IndexEvery = (int)ParseD(txtIndexEvery.Text, 5),
            SimplifyTolerance = ParseD(txtSimplify.Text, 0.2),
            MinLengthMeters = ParseD(txtMinLen.Text, 10),
            ChaikinIterations = cmbChaikin.SelectedIndex + 1,
            SplineStepMeters = ParseD(txtSplineStep.Text, 2),
            Smooth = outChaikin.IsChecked == true ? ContourSmoothMode.Chaikin : outSpline.IsChecked == true ? ContourSmoothMode.Spline : ContourSmoothMode.None,
        };
        if (lvList.IsChecked == true)
        {
            opt.ExplicitLevels = ParseLevelList(txtLevels.Text);
            if (opt.ExplicitLevels.Length == 0) { SetStatus("指定值列表为空或无法解析（逗号/分号/空格分隔）。"); return; }
            opt.IndexEvery = 0;
        }
        else if (opt.Interval <= 0) { SetStatus("等值距必须 > 0。"); return; }

        double densify = ParseD(txtDensify.Text, 5);
        double longEdge = ParseD(txtLongEdge.Text, 50);
        string layer = string.IsNullOrWhiteSpace(txtLayer.Text) ? "等值线" : txtLayer.Text.Trim();
        string labelLayer = layer + "标注";
        int colorMode = cmbColor.SelectedIndex;
        bool wantLabel = chkLabel.IsChecked == true;
        bool indexOnly = chkIndexOnly.IsChecked == true;
        double textH = Math.Max(0.1, ParseD(txtTextH.Text, 2));
        double labelGap = Math.Max(textH * 4, ParseD(txtLabelGap.Text, 150));
        bool clearOld = chkClearOld.IsChecked == true;

        var meshesSnapshot = _meshes.ToList();
        var linesSnapshot = _lines.ToList();
        var pointsSnapshot = _points.ToList();

        _busy = true; btnGenerate.IsEnabled = false;
        SetStatus("计算中…（散点构网 + 逐层切割在后台执行）");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (lines, warning) = await Task.Run(() =>
            {
                var meshes = new List<(double[] verts, int[] tris)>(meshesSnapshot);
                string warn = "";
                var scatter = new List<double>(pointsSnapshot);
                foreach (var (xyz, closed) in linesSnapshot) ContourEngine.DensifyInto(scatter, xyz, closed, densify);
                if (scatter.Count >= 9)
                {
                    var tris = ContourEngine.TriangulateScatter(scatter, longEdge, out int used);
                    if (tris.Length >= 3) meshes.Add((scatter.ToArray(), tris));
                    else warn += "散点构网失败（点数不足或全共线），点/线源被跳过；";
                    if (used < scatter.Count / 3) warn += $"散点去重 {scatter.Count / 3 - used} 个；";
                }
                else if (scatter.Count > 0) warn += "点/线源合计不足 3 个点，跳过；";
                var built = ContourEngine.Build(meshes, opt, out string buildWarn);
                return (built, warn + buildWarn);
            });
            sw.Stop();
            if (lines.Count == 0) { SetStatus($"没有生成任何等值线（值域 / 等值距 / 碎线过滤是否过严？）。{warning}"); return; }

            if (clearOld)
            {
                int removed = _ctx.RemoveLayerEntities(layer) + _ctx.RemoveLayerEntities(labelLayer);
                if (removed > 0) _ctx.Status($"> 构建等值线：已清除旧结果 {removed} 个实体");
            }
            var ents = ContourEngine.BuildEntities(lines, layer, labelLayer, colorMode, opt, wantLabel, indexOnly, textH, labelGap);
            var lineEnts = ents.Where(en => en.LayerName == layer).ToList();
            var labelEnts = ents.Where(en => en.LayerName == labelLayer).ToList();
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var pl in lineEnts.OfType<PolylineEntity>())
                foreach (var (x, y) in pl.Points) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
            _ctx.AddEntities(lineEnts, layer, maxX > minX && maxY > minY ? new[] { minX, minY, maxX, maxY } : null);
            if (labelEnts.Count > 0) _ctx.AddEntities(labelEnts, labelLayer, null);

            int closedCount = lines.Count(l => l.Closed);
            int indexCount = lines.Count(l => l.IsIndex);
            double lvMin = lines.Min(l => l.Level), lvMax = lines.Max(l => l.Level);
            string msg = $"共 {lines.Count} 条（闭合 {closedCount}，计曲线 {indexCount}），值 {lvMin:0.##} ~ {lvMax:0.##} m，用时 {sw.Elapsed.TotalSeconds:0.0} s，已入图层「{layer}」。{warning}";
            SetStatus("生成完成 — " + msg);
            _ctx.Status("> 构建等值线：" + msg);
        }
        catch (Exception ex) { SetStatus($"生成异常：{ex.Message}"); _ctx.Status($"构建等值线异常: {ex.Message}"); }
        finally { _busy = false; btnGenerate.IsEnabled = true; }
    }

    // ── 清除 / 关闭 ──────────────────────────────────────────
    private void OnClearResult(object? sender, RoutedEventArgs e)
    {
        if (_ctx == null) return;
        try
        {
            string layer = string.IsNullOrWhiteSpace(txtLayer.Text) ? "等值线" : txtLayer.Text.Trim();
            int removed = _ctx.RemoveLayerEntities(layer) + _ctx.RemoveLayerEntities(layer + "标注");
            SetStatus(removed > 0 ? $"已清除图层「{layer}」及标注共 {removed} 个实体。" : "目标图层没有可清除的实体。");
        }
        catch (Exception ex) { SetStatus($"清除异常：{ex.Message}"); }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void SetStatus(string text) => statusText.Text = text;

    private static double ParseD(string? s, double fallback) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

    public static double[] ParseLevelList(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<double>();
        return s.Split(new[] { ',', '，', ';', '；', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(tok => double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN)
                .Where(v => !double.IsNaN(v)).ToArray();
    }
}
