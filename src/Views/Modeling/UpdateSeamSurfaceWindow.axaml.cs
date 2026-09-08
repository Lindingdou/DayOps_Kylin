using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using System.Data.Common;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Controls.Charts;
using PitMine3D.Kylin.Data;
using PitMine3D.Kylin.Views.GeoDb;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 「更新煤层面」(原 MeshEditLib.ModelUpdate.UpdateSeamSurfaceWindow): 用观测点(现状见煤点/补勘顶底板)最小化更新目标顶/底板三角网。
/// ① 拾取目标面(视口选中/下拉的场景三角网) ② 选煤层+顶/底+数据源(可按“期/批次”)→载入观测点 ③ 单/多点+算法+影响半径→
/// 「评估预览」(后台估值 → 视口里贴一层分级着色格 + 色阶/直方) → 「确定更新」(换新面 或 另存新面, 可撤销)。
/// 引擎 <see cref="SurfaceUpdateEngine"/>。Kylin 追加观测点来源: 选中点 / CSV 点文件。
/// </summary>
public partial class UpdateSeamSurfaceWindow : Window
{
    private readonly ModelingContext _ctx;
    private readonly DbConnection? _conn;

    private SeamStructureConfig _config = new();
    private MeshEntity? _mesh;
    private double[]? _verts;
    private int[]? _tris;
    private SurfaceUpdateEngine.Result? _lastResult;
    private CancellationTokenSource? _evalCts;

    // ── 估值上下文(拾取目标面/评估后填充) ──
    private SurfaceUpdateEngine.MeshSampler2D? _sampler;               // 网面 XY→Z 采样器(残差列+预览共用)
    private List<(double x, double y, double z)>? _cloudObs;           // 上次参与评估的观测点
    private SurfaceUpdateEngine.Options? _cloudOpt;

    // ── 影响预览: 贴在目标面上的分级着色格(临时实体, 随时可删) ──
    private const string Overlay3DLayer = "更新影响预览";
    private const string UpdatedLayer = "更新煤层面";

    // ── 撤销: 上次更新 (旧面, 新面, 是否另存) ──
    private (MeshEntity old, MeshEntity @new, bool kept)? _lastApply;

    // ── 偏差判级阈值(m): |Δz|≤2 轻微 · ≤10 显著 · >10 大幅 · >30 疑似异常(高程体系/笔误) ──
    private const double LvMinor = 2.0, LvMajor = 10.0, LvAnomaly = 30.0;
    private static readonly IBrush RedTint = new SolidColorBrush(Color.FromArgb(0x30, 0xE5, 0x39, 0x35));
    private static readonly IBrush AmberTint = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xB3, 0x00));
    private static readonly IBrush GreenTint = new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0xAF, 0x50));
    private static readonly IBrush GrayTint = new SolidColorBrush(Color.FromArgb(0x22, 0x9E, 0x9E, 0x9E));

    // ── 判级配色(徽章/分布条共用: 绿 轻微 · 黄 显著 · 橙 大幅 · 红 疑似异常 · 灰 离网) ──
    private static IBrush Solid(byte r, byte g, byte b) => new SolidColorBrush(Color.FromRgb(r, g, b));
    private static readonly IBrush LvMinorBg = Solid(0x4C, 0xAF, 0x50);
    private static readonly IBrush LvSigBg = Solid(0xF0, 0xA8, 0x00);
    private static readonly IBrush LvMajorBg = Solid(0xFB, 0x8C, 0x00);
    private static readonly IBrush LvAnomBg = Solid(0xE5, 0x39, 0x35);
    private static readonly IBrush LvOffBg = Solid(0x9E, 0x9E, 0x9E);
    private static readonly IBrush LvWhiteFg = Solid(0xFF, 0xFF, 0xFF);
    private static readonly IBrush LvDarkFg = Solid(0x22, 0x22, 0x22);

    internal static (IBrush bg, IBrush fg) LevelPalette(string level)
        => level.Contains("异常") ? (LvAnomBg, LvWhiteFg)
         : level == "大幅" ? (LvMajorBg, LvWhiteFg)
         : level == "显著" ? (LvSigBg, LvDarkFg)
         : level == "轻微" ? (LvMinorBg, LvWhiteFg)
         : (LvOffBg, LvWhiteFg);

    private const double BarHalfPx = 40.0;      // 数据条半格像素(与 XAML 列宽一致)

    private readonly ObservableCollection<ObsRow> _obs = new();
    private bool _loadingMeshes;

    /// <summary>XAML 编译器/设计器用。</summary>
    public UpdateSeamSurfaceWindow() { _ctx = null!; InitializeComponent(); }

    public UpdateSeamSurfaceWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        _conn = ctx.Conn();
        InitializeComponent();
        _obs.CollectionChanged += (s, e) =>                     // 「用」勾选变化 → 偏差分析带口径实时跟着变
        {
            if (e.NewItems != null) foreach (ObsRow r in e.NewItems) r.PropertyChanged += OnObsRowChanged;
            if (e.OldItems != null) foreach (ObsRow r in e.OldItems) r.PropertyChanged -= OnObsRowChanged;
        };
        Opened += (_, _) =>
        {
            _config = SeamStructureConfig.LoadOrDefault(_conn);
            seamCombo.ItemsSource = _config.Seams;
            if (_config.Seams.Count > 0) seamCombo.SelectedIndex = 0;
            RefreshGrid();
            PopulateBatchCombos();
            PopulateMeshCombo();
            if (_ctx.SelectedMeshes().Count > 0) SetTarget(_ctx.SelectedMeshes()[0]);   // 打开时视口已选中 → 直接作目标面
        };
        Closed += (_, _) => { _evalCts?.Cancel(); Remove3DOverlay(); };   // 临时预览面不留在图里
    }

    private void OnObsRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ObsRow.Use)) UpdateDeviationPanel();
    }

    // ① 目标面
    private void PopulateMeshCombo()
    {
        _loadingMeshes = true;
        var meshes = _ctx.Meshes().ToList();
        meshCombo.ItemsSource = meshes;
        meshCombo.SelectedItem = _mesh != null && meshes.Contains(_mesh) ? _mesh : null;
        _loadingMeshes = false;
    }

    private void OnMeshComboOpened(object? sender, EventArgs e) => PopulateMeshCombo();

    private void OnMeshComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingMeshes) return;
        if (meshCombo.SelectedItem is MeshEntity m && !ReferenceEquals(m, _mesh)) SetTarget(m);
    }

    private async void OnPickTarget(object? sender, RoutedEventArgs e)
    {
        var sel = _ctx.SelectedMeshes();
        MeshEntity? m = sel.Count > 0 ? sel[0] : null;
        if (m == null)
        {
            statusText.Text = "视口未选中三角网：请在视口点一下目标面（Esc 取消）";
            var ent = await _ctx.PickEntityAsync("拾取目标三角网", en => en is MeshEntity);
            Activate();
            m = ent as MeshEntity;
            if (m == null) { statusText.Text = "选中的不是三角网(或已取消)。请选中顶/底板三角网面"; return; }
        }
        SetTarget(m);
    }

    private void SetTarget(MeshEntity m)
    {
        var (verts, tris) = m.Flatten();
        if (verts.Length < 9 || tris.Length < 3) { statusText.Text = "选中的三角网几何为空。请选中顶/底板三角网面"; return; }
        _evalCts?.Cancel();                       // 弃掉旧面在途的评估
        Remove3DOverlay();                        // 换了目标面 → 旧预览面作废
        _mesh = m; _verts = verts; _tris = tris; _lastResult = null;
        _cloudObs = null; _cloudOpt = null;
        _sampler = SurfaceUpdateEngine.MeshSampler2D.Build(verts, tris);   // 残差列+预览共用
        applyBtn.IsEnabled = false; ClearPreview();
        targetInfo.Text = $"「{m.Name}」：{verts.Length / 3} 顶点 / {tris.Length / 3} 面 · 图层「{m.LayerName}」";
        statusText.Text = "已拾取目标面。载入观测点后点「评估预览」";
        _loadingMeshes = true; meshCombo.SelectedItem = _ctx.Meshes().Contains(m) ? m : null; _loadingMeshes = false;
        RefreshResiduals();                       // 已载点的话立刻补算偏差
    }

    // ② 观测数据: 批次下拉
    private void PopulateBatchCombos()
    {
        var cs = new List<BatchOption> { new BatchOption { Label = "全部", Id = null } };
        try
        {
            if (_conn != null)
                foreach (var b in CsAllBatches(_conn))
                    cs.Add(new BatchOption { Label = BatchLabel(b.Name, b.Label, b.PointCount), Id = b.Id });
        }
        catch { }
        csBatchCombo.ItemsSource = cs; csBatchCombo.SelectedIndex = 0;

        var bh = new List<BatchOption> { new BatchOption { Label = "全部", Id = null } };
        try
        {
            if (_conn != null)
                foreach (var b in SupAllBatches(_conn))
                    bh.Add(new BatchOption { Label = BatchLabel(b.Name, b.Label, b.HoleCount), Id = b.Id });
        }
        catch { }
        bhBatchCombo.ItemsSource = bh; bhBatchCombo.SelectedIndex = 0;
    }

    private static string BatchLabel(string? name, string? label, int count) => $"{MuBatchName(name, label)}（{count}）";

    // ② 观测数据: 载入观测点(可按选中的期/批次过滤)
    private void OnLoadObs(object? sender, RoutedEventArgs e)
    {
        if (seamCombo.SelectedItem is not SeamStructureConfig.SeamItem seam)
        { statusText.Text = "请先选煤层（可在补勘/现状写实的「煤层结构…」里预置）"; return; }
        if (_conn == null) { statusText.Text = "✗ 数据库不可用"; return; }
        string horizon = floorRadio.IsChecked == true ? "底板" : "顶板";
        long? csBatch = (csBatchCombo.SelectedItem as BatchOption)?.Id;
        long? bhBatch = (bhBatchCombo.SelectedItem as BatchOption)?.Id;

        ClearObs();
        try
        {
            if (csCheck.IsChecked == true)
                foreach (var (x, y, z, batch) in CsObservations(_conn, seam.Code, horizon, csBatch))
                    _obs.Add(new ObsRow { X = x, Y = y, Z = z, Source = "现状", Batch = batch });
            if (bhCheck.IsChecked == true)
                foreach (var (x, y, z, batch) in SupObservations(_conn, seam.Code, horizon, bhBatch))
                    _obs.Add(new ObsRow { X = x, Y = y, Z = z, Source = "补勘", Batch = batch });
        }
        catch (Exception ex) { statusText.Text = $"载入失败：{ex.Message}"; return; }

        statusText.Text = $"载入 {_obs.Count} 个观测点（{seam.Name} · {horizon}）"
                        + (_obs.Count == 0 ? "：无数据，请先在现状写实/补勘录入，或换个期"
                                           : "：默认都不参与，请在「用」列勾选本次更新要用的点");
        RefreshResiduals();
    }

    private void ClearObs()
    {
        foreach (var r in _obs) r.PropertyChanged -= OnObsRowChanged;   // Clear() 不带 OldItems, 先退订
        _obs.Clear();
        _lastResult = null; applyBtn.IsEnabled = false; ClearPreview(); Remove3DOverlay();
    }

    /// <summary>追加视口选中的点(带高程)为观测点。</summary>
    private void OnAddSelectedPoints(object? sender, RoutedEventArgs e)
    {
        var sel = _ctx.SelectedPoints();
        if (sel.Count == 0) { statusText.Text = "请先在视口选中要作为观测点的点"; return; }
        foreach (var p in sel) _obs.Add(new ObsRow { X = p.X, Y = p.Y, Z = p.Elevation, Source = "选中", Batch = "" });
        _lastResult = null; applyBtn.IsEnabled = false; ClearPreview(); Remove3DOverlay();
        statusText.Text = $"已追加 {sel.Count} 个选中点为观测点（共 {_obs.Count}）；请在「用」列勾选参与点";
        RefreshResiduals();
    }

    /// <summary>从 CSV/TXT/XYZ 点文件追加观测点。</summary>
    private async void OnAddCsvPoints(object? sender, RoutedEventArgs e)
    {
        var path = await _ctx.OpenFileAsync("选观测点文件 (x,y,z)", new[] { "*.csv", "*.txt", "*.xyz" });
        if (path == null) return;
        var pr = PointDataImportService.Load(path);
        if (!pr.Success || pr.Points.Count == 0) { statusText.Text = "观测点文件为空或解析失败"; return; }
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        foreach (var (x, y, z) in pr.Points) _obs.Add(new ObsRow { X = x, Y = y, Z = z, Source = "CSV", Batch = name });
        _lastResult = null; applyBtn.IsEnabled = false; ClearPreview(); Remove3DOverlay();
        statusText.Text = $"已从「{name}」追加 {pr.Points.Count} 个观测点（共 {_obs.Count}）；请在「用」列勾选参与点";
        RefreshResiduals();
    }

    /// <summary>逐点算与现面的偏差(残差): 现面Z/Δz/判级; 随后刷新偏差分析带与排序。</summary>
    private void RefreshResiduals()
    {
        int anomalies = 0;
        foreach (var r in _obs)
        {
            if (_sampler != null && _sampler.TrySample(r.X, r.Y, out double sz))
            {
                sz += _mesh?.Elevation ?? 0;
                r.SurfZ = sz;
                double dz = r.Z - sz; r.Dz = dz;
                double a = Math.Abs(dz);
                r.Level = a > LvAnomaly ? "⚠疑似异常" : a > LvMajor ? "大幅" : a > LvMinor ? "显著" : "轻微";
                if (a > LvAnomaly) anomalies++;
            }
            else
            {
                r.SurfZ = null; r.Dz = null;
                r.Level = _sampler == null ? "" : "离网";
            }
        }
        UpdateDeviationPanel();
        RefreshGrid();
        if (anomalies > 0)
            statusText.Text = $"⚠ {anomalies} 个观测点与现面偏差超 {LvAnomaly:F0}m，疑似数据错误（高程体系/笔误）——建议「剔除异常」或复核后再评估";
    }

    /// <summary>偏差分析带: 一句话结论 + 分级分布条 + 系统性/RMS/中位/最大; 统计口径 = 参与点(「用」勾选)。同时刷新表内数据条尺度。</summary>
    private void UpdateDeviationPanel()
    {
        if (_obs.Count == 0) { deviationBorder.IsVisible = false; return; }
        deviationBorder.IsVisible = true;

        if (_sampler == null)                                   // 还没拾取目标面 → 无从比对
        {
            devDot.Fill = LvOffBg;
            devTitle.Text = "尚未拾取目标面 —— 拾取后立即逐点算出与现面的偏差 Δz";
            devBar.IsVisible = false;
            devLevelText.Text = $"已载入 {_obs.Count} 个观测点，等待比对对象";
            devStatsText.Text = "";
            deviationBorder.Background = GrayTint;
            foreach (var r in _obs) r.SetBar(1, BarHalfPx);
            return;
        }
        devBar.IsVisible = true;

        // 统计口径: 已勾选的参与点; 一个都没勾时先按"全部载入点"给概览
        var used = _obs.Where(r => r.Use).ToList();
        bool allScope = used.Count == 0;
        if (allScope) used = _obs.ToList();
        int excluded = _obs.Count - used.Count;
        int minor = 0, sig = 0, major = 0, anom = 0, off = 0;
        foreach (var r in used)
        {
            if (r.Dz == null) { off++; continue; }
            double a = Math.Abs(r.Dz.Value);
            if (a > LvAnomaly) anom++; else if (a > LvMajor) major++; else if (a > LvMinor) sig++; else minor++;
        }
        SetLevelBar(minor, sig, major, anom, off);

        var dzs = used.Where(r => r.Dz != null).Select(r => r.Dz!.Value).ToList();
        var abs = dzs.Select(Math.Abs).OrderBy(v => v).ToList();

        // 数据条归一尺度 = 参与点 |Δz| 的 P90(下限 1m)
        double scale = abs.Count > 0 ? Math.Max(1.0, abs[(int)Math.Floor(0.9 * (abs.Count - 1))]) : 1.0;
        foreach (var r in _obs) r.SetBar(scale, BarHalfPx);

        if (dzs.Count == 0)
        {
            devDot.Fill = LvOffBg;
            devTitle.Text = "这些点全部离网 —— 不在目标面范围内，换个面或换批数据";
            devLevelText.Text = $"{(allScope ? "载入" : "参与")} {used.Count} 点 · 离网 {off} 点";
            devStatsText.Text = "";
            deviationBorder.Background = GrayTint;
            return;
        }

        double mean = dzs.Average();
        double rms = Math.Sqrt(dzs.Average(d => d * d));
        double maxSigned = dzs.OrderByDescending(Math.Abs).First();
        var sorted = dzs.OrderBy(d => d).ToList();
        double med = sorted[sorted.Count / 2];      // 系统性偏差取中位数: 不被个别超差点带偏

        IBrush dot, bg; string verdict;
        if (anom > 0)
        { dot = LvAnomBg; bg = RedTint; verdict = $"其中 {anom} 点偏差超 {LvAnomaly:F0}m，疑似数据错误（高程体系/笔误），建议先「剔除异常」"; }
        else if (major > 0)
        { dot = LvMajorBg; bg = AmberTint; verdict = $"其中 {major} 点偏差超 {LvMajor:F0}m，该处网面更新后会明显变形，建议先复核"; }
        else if (sig > 0)
        { dot = LvMinorBg; bg = GreenTint; verdict = $"{sig}/{dzs.Count} 点偏差在 {LvMinor:F0}~{LvMajor:F0}m，属常规修正，可直接评估更新"; }
        else
        { dot = LvMinorBg; bg = GreenTint; verdict = $"全部点偏差 ≤{LvMinor:F0}m，现面与实测基本吻合，更新幅度很小"; }

        string head = Math.Abs(med) < 0.05 ? "现面无系统性偏差"
            : med > 0 ? $"现面整体偏低 {med:F2}m（面需抬升）"
                      : $"现面整体偏高 {Math.Abs(med):F2}m（面需下调）";
        devDot.Fill = dot;
        devTitle.Text = (allScope ? "【全部载入点】" : "") + $"{head}；{verdict}";

        var parts = new List<string>();
        if (minor > 0) parts.Add($"轻微(≤{LvMinor:F0}m) {minor}");
        if (sig > 0) parts.Add($"显著({LvMinor:F0}~{LvMajor:F0}m) {sig}");
        if (major > 0) parts.Add($"大幅({LvMajor:F0}~{LvAnomaly:F0}m) {major}");
        if (anom > 0) parts.Add($"疑似异常(>{LvAnomaly:F0}m) {anom}");
        if (off > 0) parts.Add($"离网 {off}");
        devLevelText.Text = "分级：" + string.Join(" · ", parts);

        devStatsText.Text = $"中位Δz {Sign(med)}{med:F2}m（系统性偏差） · 均值Δz {Sign(mean)}{mean:F2}m"
                          + $" · RMS {rms:F2}m（整体离散） · 最大 {Sign(maxSigned)}{maxSigned:F2}m · "
                          + (allScope ? $"统计 {dzs.Count} 点（尚未勾选参与点，先按全部载入点概览）"
                                      : $"参与 {dzs.Count} 点" + (excluded > 0 ? $"（未勾选 {excluded} 点）" : ""));
        deviationBorder.Background = bg;
    }

    private static string Sign(double v) => v >= 0 ? "+" : "";

    /// <summary>分级分布条: 段宽∝点数; 段够宽才写字。</summary>
    private void SetLevelBar(int minor, int sig, int major, int anom, int off)
    {
        double total = minor + sig + major + anom + off;
        var cols = devBar.ColumnDefinitions;   // x:Name 的 ColumnDefinition 不生成字段, 按序取
        cols[0].Width = new GridLength(minor, GridUnitType.Star);
        cols[1].Width = new GridLength(sig, GridUnitType.Star);
        cols[2].Width = new GridLength(major, GridUnitType.Star);
        cols[3].Width = new GridLength(anom, GridUnitType.Star);
        cols[4].Width = new GridLength(off, GridUnitType.Star);
        devSegMinor.Text = SegText("轻微", minor, total);
        devSegSig.Text = SegText("显著", sig, total);
        devSegMajor.Text = SegText("大幅", major, total);
        devSegAnom.Text = SegText("异常", anom, total);
        devSegOff.Text = SegText("离网", off, total);
    }

    private static string SegText(string name, int n, double total)
    {
        if (n <= 0 || total <= 0) return "";
        double share = n / total;
        return share >= 0.22 ? $"{name} {n}" : share >= 0.09 ? n.ToString() : "";
    }

    /// <summary>默认按 |Δz| 降序: 问题点排最前(取消勾选则回到载入顺序)。</summary>
    private void RefreshGrid()
    {
        var selected = obsGrid.SelectedItems.OfType<ObsRow>().ToList();
        IEnumerable<ObsRow> src = _obs;
        if (sortByDzCheck.IsChecked == true) src = _obs.OrderByDescending(r => r.AbsDz);
        obsGrid.ItemsSource = src.ToList();
        foreach (var r in selected) obsGrid.SelectedItems.Add(r);
    }

    private void OnSortByDzChanged(object? sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        RefreshGrid();
    }

    /// <summary>行底色: 疑似异常淡红 / 大幅淡黄(原 DataGrid RowStyle 触发器)。</summary>
    private void OnObsLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is not ObsRow r) { e.Row.Background = null; return; }
        e.Row.Background = r.Level.Contains("异常") ? new SolidColorBrush(Color.Parse("#30E53935"))
                         : r.Level == "大幅" ? new SolidColorBrush(Color.Parse("#22FFB300")) : null;
    }

    private void OnExcludeAnomalies(object? sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var r in _obs)
            if (r.Level.Contains("异常") && r.Use) { r.Use = false; n++; }
        statusText.Text = n > 0
            ? $"已剔除 {n} 个疑似异常点（「用」列可随时勾回）；请重新「评估预览」"
            : "已勾选的点里没有疑似异常点";
    }

    /// <summary>把表格里选中的行设为参与(Ctrl/Shift 可多选)。参与哪些点一律由人明确指定, 不自动勾。</summary>
    private void OnUseSelected(object? sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var item in obsGrid.SelectedItems)
            if (item is ObsRow r && !r.Use) { r.Use = true; n++; }
        statusText.Text = obsGrid.SelectedItems.Count == 0
            ? "请先在表格里选中要参与更新的行（可 Ctrl / Shift 多选），再点「勾选选中行」"
            : $"已勾选 {n} 个点参与更新（当前共 {_obs.Count(o => o.Use)} 个）";
    }

    private void OnUseNone(object? sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var r in _obs) if (r.Use) { r.Use = false; n++; }
        statusText.Text = n > 0 ? $"已清空 {n} 个勾选，请重新挑选参与更新的点" : "当前没有勾选任何点";
    }

    private List<ObsRow>? GatherObsRows()
    {
        if (singleRadio.IsChecked == true)
        {
            if (obsGrid.SelectedItem is not ObsRow r) { statusText.Text = "单点模式：请在左表选中一个观测点"; return null; }
            return new List<ObsRow> { r };
        }
        if (_obs.Count == 0) { statusText.Text = "请先「载入观测点」"; return null; }
        var rows = _obs.Where(o => o.Use).ToList();
        if (rows.Count == 0)
        { statusText.Text = "多点模式：请先在「用」列勾选本次更新要用的观测点（载入后默认都不参与）"; return null; }
        return rows;
    }

    // ③ 评估预览: 后台估值 → 视口分级着色预览(不建面)
    private async void OnEvaluate(object? sender, RoutedEventArgs e)
    {
        if (_verts is null || _tris is null || _mesh == null) { statusText.Text = "请先「拾取目标面」"; return; }
        var rows = GatherObsRows();
        if (rows is null) return;
        var obs = rows.Select(r => (r.X, r.Y, r.Z)).ToList();

        double radius = BoreholeParseNum(radiusBox.Text) ?? 80.0;
        if (radius <= 0) radius = 80.0;
        string algo = (algoCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "IDW";

        _evalCts?.Cancel();
        var cts = new CancellationTokenSource(); _evalCts = cts;
        var verts = _verts; var tris = _tris; var mesh = _mesh;
        var opt = new SurfaceUpdateEngine.Options { Algorithm = algo, InfluenceRadius = radius };
        SetBusy(true, "精算中…（后台并行，窗口不会卡）");

        SurfaceUpdateEngine.Result? res = null; string? err = null;
        var sampler = _sampler;                             // 拾取目标面时已建; 为空则后台补建
        try
        {
            res = await Task.Run(() =>
            {
                sampler ??= SurfaceUpdateEngine.MeshSampler2D.Build(verts, tris);
                return SurfaceUpdateEngine.Evaluate(verts, tris, obs, opt);
            }, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { err = ex.Message; }

        if (_evalCts != cts) return;                        // 已被更晚的评估接管 → 由那次负责恢复 UI
        if (err != null) { SetBusy(false, "✗ 评估异常：" + err); return; }
        if (res == null || !ReferenceEquals(_mesh, mesh))   // 被取消 / 目标面已换 → 仅恢复按钮·光标
        { SetBusy(false, statusText.Text ?? ""); return; }

        _lastResult = res;
        _sampler = sampler; _cloudObs = obs; _cloudOpt = opt;
        if (res.Any) UpdateConclusion(res, rows);
        SetBusy(false, res.Message + (res.Any ? "（影响预览已上图，可在视口里看；点「确定更新」应用）" : ""));
        if (res.Any && overlay3DCheck.IsChecked == true) await Build3DOverlayAsync();
    }

    /// <summary>评估结论横幅: 信号灯 + 影响范围(片区/面积/占比/净体积)。</summary>
    private void UpdateConclusion(SurfaceUpdateEngine.Result res, List<ObsRow> rows)
    {
        var dzs = rows.Where(r => r.Dz != null).Select(r => r.Dz!.Value).ToList();
        int anomalies = rows.Count(r => r.Level.Contains("异常"));
        double maxAbsDz = dzs.Count > 0 ? dzs.Max(d => Math.Abs(d)) : 0;

        IBrush dot, bg; string verdict;
        if (anomalies > 0)
        {
            dot = LvAnomBg; bg = RedTint;
            verdict = $"参与点中有 {anomalies} 个疑似异常（|Δz|>{LvAnomaly:F0}m），建议「剔除异常」复核后再更新";
        }
        else if (maxAbsDz > LvMajor)
        { dot = LvMajorBg; bg = AmberTint; verdict = $"最大偏差 {maxAbsDz:F2}m（超 {LvMajor:F0}m），建议核对观测数据后再「确定更新」"; }
        else
        { dot = LvMinorBg; bg = GreenTint; verdict = "偏差在正常范围，可「确定更新」"; }

        conclusionDot.Fill = dot;
        conclusionTitle.Text = verdict;
        double pct = res.TotalArea > 1e-6 ? res.AffectedArea / res.TotalArea * 100 : 0;
        conclusionLine1.Text = $"影响范围：{res.Clusters.Count} 个片区 · 面积 {FmtArea(res.AffectedArea)}（占全面 {pct:F1}%）"
                             + $" · 净体积 {FmtVol(res.NetVolume)} · 最大抬升 +{res.MaxDisp:F2}m / 最大下沉 {res.MinDisp:F2}m"
                             + $" · 受影响 {res.AffectedVertices} 顶点 · 平均|位移| {res.MeanAbsDisp:F2}m";
        conclusionLine2.Text = string.Join("　", res.Clusters.Select((c, i) =>
            $"片区{Num(i)}（{Compass(res, c.Cx, c.Cy)}）：{c.ObsCount}点 · {FmtArea(c.Area)} · 最大{c.MaxAbs:F2}m · {FmtVol(c.NetVol)}"));
        conclusionLine2.IsVisible = res.Clusters.Count > 1;
        conclusionBorder.Background = bg;
        conclusionBorder.IsVisible = true;
    }

    private static string Num(int i) => i < 20 ? ((char)('①' + i)).ToString() : (i + 1).ToString();

    private static string FmtArea(double m2) => m2 >= 10000 ? $"{m2 / 10000:F1}万m²" : $"{m2:F0}m²";

    private static string FmtVol(double m3)
    {
        double a = Math.Abs(m3); string s = m3 >= 0 ? "+" : "-";
        return a >= 10000 ? $"{s}{a / 10000:F1}万m³" : $"{s}{a:F0}m³";
    }

    /// <summary>片区相对"整个影响范围"中心的方位(东/西/南/北/…/中部)。</summary>
    private static string Compass(SurfaceUpdateEngine.Result res, double x, double y)
    {
        double x0 = res.HasAffBounds ? res.AffMinX : res.MinX, x1 = res.HasAffBounds ? res.AffMaxX : res.MaxX;
        double y0 = res.HasAffBounds ? res.AffMinY : res.MinY, y1 = res.HasAffBounds ? res.AffMaxY : res.MaxY;
        double cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
        double dx = x - cx, dy = y - cy;
        double span = Math.Max(x1 - x0, y1 - y0);
        if (Math.Sqrt(dx * dx + dy * dy) < span * 0.12) return "中部";
        double ang = Math.Atan2(dy, dx) * 180 / Math.PI;    // 0=东 90=北
        string[] dirs = { "东", "东北", "北", "西北", "西", "西南", "南", "东南" };
        int idx = (int)Math.Round(ang / 45.0); idx = ((idx % 8) + 8) % 8;
        return dirs[idx] + "侧";
    }

    // ③ 确定更新: 处理面(建新网在后台, 入场景/换面在 UI 线程)
    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (_verts is null || _tris is null || _mesh == null) { statusText.Text = "请先「拾取目标面」并「评估预览」"; return; }
        if (_lastResult is null || !_lastResult.Any) { statusText.Text = "请先「评估预览」（无受影响区域，无需更新）"; return; }

        bool keep = keepOldCheck.IsChecked == true;
        if (!await BoreholeMsgBox.ConfirmAsync(this, "更新煤层面",
                (keep ? "确认按预览另存更新后的新面（原面保留）？" : "确认按预览更新该面？将删除原面、生成更新后的新面。") + $"\n\n{_lastResult.Message}")) return;

        var verts = _verts; var tris = _tris; var result = _lastResult; var old = _mesh;
        Remove3DOverlay();                          // 预览是贴在旧面上的, 更新前先撤掉
        overlay3DCheck.IsChecked = false;
        SetBusy(true, "处理面中…（生成更新后的新面）");
        try
        {
            var nm = await Task.Run(() => SurfaceUpdateEngine.BuildUpdatedMesh(verts, result.NewZ, tris, keep ? old.Name + "·更新" : old.Name));
            if (keep) { nm.LayerName = UpdatedLayer; _ctx.AddMesh(nm, false); }
            else _ctx.ReplaceMesh(old, nm);
            _lastApply = (old, nm, keep);
            undoBtn.IsEnabled = true;
            _sampler = null; _cloudObs = null; _cloudOpt = null;
            ClearPreview();
            _mesh = null; _verts = null; _tris = null; _lastResult = null;
            RefreshResiduals();                 // 旧面已换 → 偏差列清空, 分析带回到"待拾取目标面"
            targetInfo.Text = "已更新；如需再改请重新选中新面「拾取目标面」";
            PopulateMeshCombo();
            SetBusy(false, keep
                ? $"✓ 已另存更新后的新面「{nm.Name}」（{result.AffectedVertices} 顶点变更），原面保留；新面在图层「{UpdatedLayer}」"
                : $"✓ 已更新面「{nm.Name}」（{result.AffectedVertices} 顶点变更），旧面已换成新面（可「撤销更新」或 Ctrl+Z）");
            _ctx.Status(statusText.Text ?? "");
        }
        catch (Exception ex) { SetBusy(false, "✗ 更新异常：" + ex.Message); }
    }

    /// <summary>撤销上次更新: 换面 → 把新面换回旧面; 另存 → 删掉新面。</summary>
    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        if (_lastApply is not { } la) { statusText.Text = "没有可撤销的更新"; return; }
        try
        {
            if (la.kept) _ctx.RemoveEntities(new SceneEntity[] { la.@new });
            else _ctx.ReplaceMesh(la.@new, la.old);
            _ctx.RefreshScene();
            _lastApply = null; undoBtn.IsEnabled = false;
            PopulateMeshCombo();
            statusText.Text = la.kept ? $"已撤销：删除另存的新面「{la.@new.Name}」" : $"已撤销：面「{la.old.Name}」换回更新前";
        }
        catch (Exception ex) { statusText.Text = "✗ 撤销失败：" + ex.Message; }
    }

    private void OnClearOverlay(object? sender, RoutedEventArgs e)
    {
        Remove3DOverlay();
        overlay3DCheck.IsChecked = false;
        legendBorder.IsVisible = false;
        statusText.Text = "已清除影响预览";
    }

    // ── 影响预览: 把"哪里变、变多少"贴到视口的目标面上 ──
    private async void OnOverlayChanged(object? sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (overlay3DCheck.IsChecked == true) await Build3DOverlayAsync();
        else
        {
            Remove3DOverlay();
            legendBorder.IsVisible = false;
            statusText.Text = "已移除影响预览";
        }
    }

    /// <summary>在目标面上贴一层分级着色格: 逐格算位移(与确定更新同一套公式)→ 按级归桶 → 每格一个按级着色的矩形入场景 + 片区标注。构建在后台, 入图在 UI 线程。</summary>
    private async Task Build3DOverlayAsync()
    {
        Remove3DOverlay();
        if (_lastResult is null || !_lastResult.Any || _sampler is null || _cloudObs is null || _cloudOpt is null)
        { statusText.Text = "请先「拾取目标面」并「评估预览」，再开影响预览"; return; }

        var sampler = _sampler; var obs = _cloudObs; var opt = _cloudOpt; var res = _lastResult;
        double meshZ = _mesh?.Elevation ?? 0;
        var labels = res.Clusters.Select((c, i) => (c.Cx, c.Cy, $"{Num(i)} 最大{c.MaxAbs:F2}m")).ToList();

        SurfaceUpdateEngine.OverlayBuild build;
        try
        {
            build = await Task.Run(() => SurfaceUpdateEngine.BuildOverlay(sampler, obs, opt, res, labels, maxCells: 8000));   // 场景逐格画实体, 格数封顶 8 千
        }
        catch (Exception ex) { statusText.Text = "✗ 影响预览构建失败：" + ex.Message; return; }

        if (_lastResult != res) return;                    // 期间又评估了一次 → 让那次负责
        if (!build.Any) { statusText.Text = "影响预览：" + build.Message; return; }

        BuildLegend(build);
        legendMinText.Text = $"最大下沉 {build.MinDispVal:F2}m";
        legendMaxText.Text = $"最大抬升 +{build.MaxDispVal:F2}m";
        legendBorder.IsVisible = true;

        try
        {
            double w = res.HasAffBounds ? Math.Max(res.AffMaxX - res.AffMinX, res.AffMaxY - res.AffMinY) : Math.Max(res.MaxX - res.MinX, res.MaxY - res.MinY);
            double th = Math.Clamp(w * 0.03, 2.0, 30.0);
            var ents = SurfaceUpdateEngine.OverlayEntities(build, Overlay3DLayer, th);
            if (Math.Abs(meshZ) > 1e-9) foreach (var en in ents) en.Elevation += meshZ;
            _ctx.AddEntities(ents, Overlay3DLayer, null);
        }
        catch (Exception ex) { statusText.Text = "✗ 影响预览入图失败：" + ex.Message; return; }
        statusText.Text = $"✓ 影响预览已上图（图层「{Overlay3DLayer}」）：{build.Message}；颜色对照下方色阶";
    }

    /// <summary>删掉「更新影响预览」图层上的全部临时实体(分级格 + 片区标注)。</summary>
    private void Remove3DOverlay()
    {
        try { if (_ctx.RemoveLayerEntities(Overlay3DLayer) > 0) _ctx.RefreshScene(); } catch { }
    }

    /// <summary>图例: 8 级色块(4 沉 4 抬)+中间"不显著"格; 悬浮每格看区间。旁边直方 = 各级格数(同色)。</summary>
    private void BuildLegend(SurfaceUpdateEngine.OverlayBuild build)
    {
        double step = build.Step;
        legendChips.Children.Clear();
        var cats = new List<string>(); var vals = new List<double?>(); var cols = new List<Color?>();
        for (int b = -4; b <= 4; b++)
        {
            var (r, g, bl) = SurfaceUpdateEngine.BandRgb(b);
            double lo = SurfaceUpdateEngine.BandLow(b, step), hi = SurfaceUpdateEngine.BandLow(Math.Abs(b) + 1, step);
            var chip = new Border { Background = new SolidColorBrush(Color.FromRgb(r, g, bl)), Margin = new Thickness(0, 0, 1, 0) };
            ToolTip.SetTip(chip, b == 0 ? $"|变化| < {step:0.##}m：不显著"
                        : Math.Abs(b) == 4 ? $"{(b > 0 ? "抬升" : "下沉")} ≥ {lo:0.##}m"
                        : $"{(b > 0 ? "抬升" : "下沉")} {lo:0.##}~{hi:0.##}m");
            legendChips.Children.Add(chip);
            cats.Add(b == 0 ? "0" : (b > 0 ? "+" : "-") + Math.Abs(b));
            vals.Add(build.BandCounts[b + 4]);
            cols.Add(Color.FromRgb(r, g, bl));
        }
        legendStepText.Text = $"分级 {step:0.##}／{2 * step:0.##}／{4 * step:0.##}／{8 * step:0.##} m";
        histChart.Categories = cats;
        histChart.Series = new List<ChartSeries> { new ChartSeries { Name = "格数", Kind = SeriesKind.Bar, Values = vals, PointColors = cols, ShowValueLabels = true } };
        histChart.Legend = LegendPlacement.Hidden;
        histChart.Refresh();
    }

    private void ClearPreview()
    {
        legendChips.Children.Clear();
        legendBorder.IsVisible = false;
        legendMinText.Text = "-"; legendMaxText.Text = "+";
        legendStepText.Text = "评估后显示分级";
        conclusionBorder.IsVisible = false;
    }

    private void SetBusy(bool busy, string status)
    {
        evaluateBtn.IsEnabled = !busy;
        loadObsBtn.IsEnabled = !busy;
        applyBtn.IsEnabled = !busy && _lastResult?.Any == true;
        statusText.Text = status;
        Cursor = busy ? new Cursor(StandardCursorType.Wait) : Cursor.Default;
    }

    /// <summary>观测点行(残差列/参与勾选可变 → INPC)。</summary>
    public sealed class ObsRow : INotifyPropertyChanged
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string? Source { get; set; }
        public string? Batch { get; set; }        // 期/批次友好名

        private bool _use;      // 默认不参与: 参与哪些点必须由人明确勾选
        /// <summary>是否参与本次更新(多点模式)。</summary>
        public bool Use { get => _use; set { if (_use != value) { _use = value; OnChanged(nameof(Use)); } } }

        private double? _surfZ;
        /// <summary>当前面在该点 XY 处的高程(离网/未拾取目标面 = null)。</summary>
        public double? SurfZ { get => _surfZ; set { _surfZ = value; OnChanged(nameof(SurfZDisp)); } }

        private double? _dz;
        /// <summary>偏差 Δz = 实测 Z − 现面 Z。</summary>
        public double? Dz
        {
            get => _dz;
            set { _dz = value; OnChanged(nameof(DzDisp)); OnChanged(nameof(AbsDz)); OnChanged(nameof(DzTip)); }
        }

        private string _level = "";
        /// <summary>判级: 轻微/显著/大幅/⚠疑似异常/离网。</summary>
        public string Level
        {
            get => _level;
            set
            {
                if (_level == value) return;
                _level = value;
                OnChanged(nameof(Level)); OnChanged(nameof(LevelRank)); OnChanged(nameof(LevelBg));
                OnChanged(nameof(LevelFg)); OnChanged(nameof(LevelVisible)); OnChanged(nameof(DzTip));
            }
        }

        // ── 双向数据条宽度(px): 零轴居中, 蓝(左)=Δz<0 面需下调, 红(右)=Δz>0 面需抬升 ──
        private double _negW, _posW;
        public double NegW { get => _negW; private set { _negW = value; OnChanged(nameof(NegW)); } }
        public double PosW { get => _posW; private set { _posW = value; OnChanged(nameof(PosW)); } }

        /// <summary>按归一尺度 scale(m→满格 halfPx)刷新数据条; |Δz| 超尺度则封顶。</summary>
        public void SetBar(double scale, double halfPx)
        {
            double w = Dz is double d && scale > 1e-9
                ? Math.Min(1.0, Math.Abs(d) / scale) * halfPx
                : 0;
            if (w > 0 && w < 2) w = 2;                       // 极小偏差也留一点可见宽度
            NegW = Dz is double n && n < 0 ? w : 0;
            PosW = Dz is double p && p >= 0 ? w : 0;
        }

        /// <summary>排序用: |Δz|(离网/未算 = -1 沉底)。</summary>
        public double AbsDz => Dz is double d ? Math.Abs(d) : -1;

        /// <summary>排序用: 判级严重度。</summary>
        public int LevelRank => Level.Contains("异常") ? 4 : Level == "大幅" ? 3 : Level == "显著" ? 2 : Level == "轻微" ? 1 : 0;

        public IBrush LevelBg => LevelPalette(Level).bg;
        public IBrush LevelFg => LevelPalette(Level).fg;
        public bool LevelVisible => !string.IsNullOrEmpty(Level);

        public string DzTip => Dz is double d
            ? $"实测 {Z:F2}m，现面 {SurfZ:F2}m，偏差 {(d >= 0 ? "+" : "")}{d:F2}m"
              + $"（{(d >= 0 ? "实测在现面之上 → 该处面需抬升" : "实测在现面之下 → 该处面需下调")}）"
            : Level == "离网" ? "该点不在目标面范围内，不参与偏差统计" : "尚未拾取目标面";

        public string XDisp => X.ToString("F2", CultureInfo.InvariantCulture);
        public string YDisp => Y.ToString("F2", CultureInfo.InvariantCulture);
        public string ZDisp => Z.ToString("F2", CultureInfo.InvariantCulture);
        public string SurfZDisp => SurfZ?.ToString("F2", CultureInfo.InvariantCulture) ?? "—";
        public string DzDisp => Dz is double d
            ? (d >= 0 ? "+" : "") + d.ToString("F2", CultureInfo.InvariantCulture)
            : "—";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    /// <summary>批次下拉项(Id=null 表示“全部”)。</summary>
    public sealed class BatchOption
    {
        public string Label { get; set; } = "";
        public long? Id { get; set; }
    }
}
