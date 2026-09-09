using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 三角网体积面板（忠实原「三角网体积」：解析散度法 + 拓扑容错 → 整体 + 分标高报量，可导 HTML/CSV，不转块体）。
///
/// 与「体素格网体积」的分工同原版：这边**不体素化**，直接对面做散度定理积分，
/// 分标高量用 <see cref="MeshVolumeLevels"/> 的"某标高以下体积"相减得到，逐层求和与整体量对得上（面板里给残差自检）。
/// </summary>
public partial class MeshVolumeWindow : Window
{
    private sealed class LevelRow
    {
        public string Range { get; set; } = "";
        public string Thickness { get; set; } = "";
        public string Volume { get; set; } = "";
        public string Pct { get; set; } = "";
    }

    private readonly ModelingContext _ctx;
    private readonly List<MeshEntity> _meshes = new();
    private readonly List<(MeshEntity mesh, MeshVolumeLevels.Report rep)> _reports = new();
    private bool _busy;

    public MeshVolumeWindow() { _ctx = null!; InitializeComponent(); }

    public MeshVolumeWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        LoadSelection(ctx.SelectedMeshes().Count > 0 ? ctx.SelectedMeshes() : ctx.Meshes());
        // 自检：窗口一开就算一遍（同 PromptDialog 的 PITMINE_SELFTEST 取默认值直通），好在批量截图里核对报量
        if (Environment.GetEnvironmentVariable("PITMINE_SELFTEST") is { Length: > 0 })
            Opened += (_, _) => OnCompute(this, new RoutedEventArgs());
    }

    private void LoadSelection(IReadOnlyList<MeshEntity> meshes)
    {
        _meshes.Clear();
        _meshes.AddRange(meshes.Where(m => m.Tris.Count > 0));
        _reports.Clear();
        levelGrid.ItemsSource = null;
        btnExportCsv.IsEnabled = btnExportHtml.IsEnabled = false;
        totalLabel.Text = "整体体积：（点「计算」）";
        detailLabel.Text = "";
        if (_meshes.Count == 0)
        {
            selInfoLabel.Text = "未选择三角网。点「重新选择对象」在视口逐个点选，Esc 结束。";
            btnCompute.IsEnabled = false;
            return;
        }
        btnCompute.IsEnabled = true;
        double minZ = _meshes.Min(m => m.Bounds.minZ), maxZ = _meshes.Max(m => m.Bounds.maxZ);
        int open = _meshes.Count(m => !MeshDiagnose.Analyze(m.Verts, m.Tris).IsClosed);
        string warn = open > 0 ? $"  ⚠ {open} 个为开放面（无封闭体积，只报面积/分层界面量）" : "";
        selInfoLabel.Text = $"三角网 {_meshes.Count} 个 · 三角 {_meshes.Sum(m => m.Tris.Count):N0} · Z [{minZ:0.###}, {maxZ:0.###}]{warn}";
        if (string.IsNullOrWhiteSpace(txtLevels.Text)) FillLevels(minZ, maxZ);
    }

    private void FillLevels(double minZ, double maxZ)
    {
        if (!double.TryParse((txtStep.Text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double step) || step <= 0)
        {
            step = Math.Max(Math.Round((maxZ - minZ) / 10, 3), 0.001);
            txtStep.Text = step.ToString("0.###", CultureInfo.InvariantCulture);
        }
        var levels = MeshVolumeLevels.EvenLevels(minZ, maxZ, step);
        txtLevels.Text = string.Join(", ", levels.Select(z => z.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    private void OnFillLevels(object? sender, RoutedEventArgs e)
    {
        if (_meshes.Count == 0) { _ = Warn("请先选择三角网。"); return; }
        FillLevels(_meshes.Min(m => m.Bounds.minZ), _meshes.Max(m => m.Bounds.maxZ));
    }

    private static List<double> ParseLevels(string? text)
    {
        var r = new List<double>();
        if (string.IsNullOrWhiteSpace(text)) return r;
        foreach (var tok in text.Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            if (double.TryParse(tok.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double z)) r.Add(z);
        return r;
    }

    private async void OnCompute(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_meshes.Count == 0) { await Warn("请先选择三角网。"); return; }
        var levels = ParseLevels(txtLevels.Text);
        var inputs = _meshes.Select(m => (m, (IReadOnlyList<(double x, double y, double z)>)m.Verts,
                                              (IReadOnlyList<(int a, int b, int c)>)m.Tris)).ToList();
        SetBusy(true);
        totalLabel.Text = "计算中…";
        _ctx.Status("三角网体积 — 算量…");
        try
        {
            var res = await Task.Run(() => inputs
                .Select(t => (t.m, MeshVolumeLevels.Compute(t.Item2, t.Item3, levels)))
                .ToList());
            _reports.Clear(); _reports.AddRange(res);
            SetBusy(false);
            FillResultUi();
        }
        catch (Exception ex) { SetBusy(false); totalLabel.Text = "整体体积：（计算失败）"; await Warn(ex.Message); }
    }

    private void FillResultUi()
    {
        if (_reports.Count == 0) return;
        var ci = CultureInfo.InvariantCulture;
        double totalV = _reports.Sum(r => r.rep.Closed ? r.rep.TotalVolume : 0);
        double area = _reports.Sum(r => r.rep.SurfaceArea);
        double proj = _reports.Sum(r => r.rep.ProjectedAreaXY);
        int openN = _reports.Count(r => !r.rep.Closed);
        totalLabel.Text = $"整体体积：{totalV:N1} m³（{_reports.Count(r => r.rep.Closed)} 个闭合体"
                        + (openN > 0 ? $"，{openN} 个开放面不计体积" : "") + "）";
        double residual = _reports.Sum(r => r.rep.Closed ? Math.Abs(r.rep.BandSum - r.rep.TotalVolume) : 0);
        detailLabel.Text = $"表面积 {area:N1} m² · XY 投影面积 {proj:N1} m² · "
                         + $"三角 {_reports.Sum(r => r.rep.TriangleCount):N0} / 顶点 {_reports.Sum(r => r.rep.VertexCount):N0}"
                         + (totalV > 0 ? $" · 分层求和残差 {residual:G3} m³（{residual / Math.Max(totalV, 1e-9) * 100:0.###}%，越小越可信）" : "");

        var bands = new Dictionary<(double z0, double z1), double>();
        foreach (var (_, rep) in _reports)
            foreach (var b in rep.Bands)
                bands[(b.Z0, b.Z1)] = bands.TryGetValue((b.Z0, b.Z1), out var v) ? v + b.Volume : b.Volume;
        double bandTotal = bands.Values.Sum();
        var rows = bands.OrderBy(k => k.Key.z0).Select(k => new LevelRow
        {
            Range = $"{k.Key.z0:0.###} ~ {k.Key.z1:0.###}",
            Thickness = (k.Key.z1 - k.Key.z0).ToString("0.###", ci),
            Volume = k.Value.ToString("N1", ci),
            Pct = Math.Abs(bandTotal) > 1e-9 ? (k.Value / bandTotal * 100).ToString("0.##", ci) + "%" : "—",
        }).ToList();
        levelGrid.ItemsSource = rows;
        btnExportCsv.IsEnabled = btnExportHtml.IsEnabled = true;
        _ctx.Status($"三角网体积：{totalV:N1} m³ · 表面积 {area:N1} m² · {rows.Count} 个标高区间（解析散度法，未转块体）");
    }

    private async void OnExportCsv(object? sender, RoutedEventArgs e) => await Export(false);
    private async void OnExportHtml(object? sender, RoutedEventArgs e) => await Export(true);

    private async Task Export(bool html)
    {
        if (_reports.Count == 0) { await Warn("请先计算。"); return; }
        try
        {
            string content = html ? RenderHtml() : RenderCsv();
            var path = await _ctx.SaveTextAsync(html ? "导出三角网体积报表 (HTML)" : "导出三角网体积报表 (CSV)",
                $"三角网体积报表_{DateTime.Now:yyyyMMdd_HHmmss}." + (html ? "html" : "csv"), content);
            if (path != null) await BlockMsgBox.InfoAsync(this, "导出完成", $"已导出到\n{path}");
        }
        catch (Exception ex) { await Warn(ex.Message); }
    }

    private string RenderCsv()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("三角网体积报表（解析散度法）");
        sb.AppendLine($"生成时间,{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("网格,闭合,体积_m3,表面积_m2,XY投影面积_m2,顶点,三角,开放边,Zmin,Zmax");
        foreach (var (m, r) in _reports)
            sb.AppendLine($"{Esc(m.Name)},{(r.Closed ? "是" : "否")},{(r.Closed ? r.TotalVolume : 0).ToString("0.###", ci)},"
                        + $"{r.SurfaceArea.ToString("0.###", ci)},{r.ProjectedAreaXY.ToString("0.###", ci)},"
                        + $"{r.VertexCount},{r.TriangleCount},{r.BoundaryEdges},"
                        + $"{r.MinZ.ToString("0.###", ci)},{r.MaxZ.ToString("0.###", ci)}");
        sb.AppendLine();
        sb.AppendLine("网格,标高下界,标高上界,层高_m,体积_m3");
        foreach (var (m, r) in _reports)
            foreach (var b in r.Bands)
                sb.AppendLine($"{Esc(m.Name)},{b.Z0.ToString("0.###", ci)},{b.Z1.ToString("0.###", ci)},"
                            + $"{(b.Z1 - b.Z0).ToString("0.###", ci)},{b.Volume.ToString("0.###", ci)}");
        return sb.ToString();
    }

    private static string Esc(string s) => s.Contains(',') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string Html(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private string RenderHtml()
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><meta charset=\"utf-8\"><title>三角网体积报表</title>");
        sb.AppendLine("<style>body{font:13px/1.6 'Microsoft YaHei',sans-serif;margin:24px;color:#222}"
                    + "h1{font-size:18px}h2{font-size:15px;margin-top:22px}"
                    + "table{border-collapse:collapse;margin-top:8px}td,th{border:1px solid #ccc;padding:4px 10px;text-align:right}"
                    + "th{background:#f2f4f7}td:first-child,th:first-child{text-align:left}"
                    + ".muted{color:#666;font-size:12px}</style>");
        sb.AppendLine($"<h1>三角网体积报表</h1><p class=muted>解析散度法（不转块体） · 生成于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}</p>");
        sb.AppendLine("<h2>整体</h2><table><tr><th>网格</th><th>闭合</th><th>体积 (m³)</th><th>表面积 (m²)</th>"
                    + "<th>XY 投影 (m²)</th><th>顶点</th><th>三角</th><th>开放边</th><th>Zmin</th><th>Zmax</th></tr>");
        foreach (var (m, r) in _reports)
            sb.AppendLine($"<tr><td>{Html(m.Name)}</td><td>{(r.Closed ? "是" : "否")}</td>"
                        + $"<td>{(r.Closed ? r.TotalVolume : 0).ToString("N1", ci)}</td><td>{r.SurfaceArea.ToString("N1", ci)}</td>"
                        + $"<td>{r.ProjectedAreaXY.ToString("N1", ci)}</td><td>{r.VertexCount:N0}</td><td>{r.TriangleCount:N0}</td>"
                        + $"<td>{r.BoundaryEdges:N0}</td><td>{r.MinZ.ToString("0.###", ci)}</td><td>{r.MaxZ.ToString("0.###", ci)}</td></tr>");
        sb.AppendLine("</table>");
        foreach (var (m, r) in _reports)
        {
            if (r.Bands.Count == 0) continue;
            double tot = r.Bands.Sum(b => b.Volume);
            sb.AppendLine($"<h2>分标高报量 — {Html(m.Name)}</h2>");
            sb.AppendLine("<table><tr><th>标高区间 (m)</th><th>层高 (m)</th><th>体积 (m³)</th><th>占比</th></tr>");
            foreach (var b in r.Bands)
                sb.AppendLine($"<tr><td>{b.Z0.ToString("0.###", ci)} ~ {b.Z1.ToString("0.###", ci)}</td>"
                            + $"<td>{(b.Z1 - b.Z0).ToString("0.###", ci)}</td><td>{b.Volume.ToString("N1", ci)}</td>"
                            + $"<td>{(Math.Abs(tot) > 1e-9 ? (b.Volume / tot * 100).ToString("0.##", ci) + "%" : "—")}</td></tr>");
            sb.AppendLine("</table>");
            sb.AppendLine($"<p class=muted>分层求和 {tot.ToString("N1", ci)} m³，整体 {(r.Closed ? r.TotalVolume : 0).ToString("N1", ci)} m³，"
                        + $"残差 {Math.Abs(tot - (r.Closed ? r.TotalVolume : 0)).ToString("G3", ci)} m³</p>");
        }
        return sb.ToString();
    }

    private async void OnReselect(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            selInfoLabel.Text = "请在视口逐个点选三角网，Esc 结束…";
            var picked = new List<MeshEntity>();
            while (true)
            {
                var ent = await _ctx.PickEntityAsync($"选三角网（已选 {picked.Count} 个，Esc 结束）", x => x is MeshEntity);
                if (ent is not MeshEntity me) break;
                if (!picked.Contains(me)) picked.Add(me);
                _ctx.Select(picked.Cast<SceneEntity>().ToList());
            }
            if (picked.Count > 0) LoadSelection(picked);
            else if (_meshes.Count == 0) selInfoLabel.Text = "未选择对象。";
            else LoadSelection(_meshes.ToList());
        }
        catch (Exception ex) { await Warn(ex.Message); }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        btnReselect.IsEnabled = !busy;
        btnCompute.IsEnabled = !busy && _meshes.Count > 0;
    }

    private Task Warn(string msg) => BlockMsgBox.WarnAsync(this, "三角网体积", msg);
}
