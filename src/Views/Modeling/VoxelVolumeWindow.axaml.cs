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

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 体素格网体积面板（忠实原 VoxelVolumeDialog，非模态）：选封闭三角网体 → 体素尺寸（按标高建议 Z）+ 次级退化 + 标高列表 →
/// 「计算」整体 + 分标高报量（并列散度定理精确体积对比）→ 导出 CSV/HTML、生成块体模型。
/// 次级退化（百分比子块）只进体积报表；「生成块体」与原版一样另跑一遍均匀占位 → 八叉树叶块（<see cref="OctreeLeafBuilder"/>），
/// 不把百分比子块灌进块体模型（薄体在母块间会整列丢失，见 EntityToBlocksWindow）。
/// </summary>
public partial class VoxelVolumeWindow : Window
{
    private const string DefaultModelName = "体素体积";
    private sealed class LevelDisplayRow { public string Range { get; set; } = ""; public string Cells { get; set; } = ""; public string Volume { get; set; } = ""; public string Pct { get; set; } = ""; }

    private readonly ModelingContext _ctx;
    private readonly List<(MeshEntity mesh, BlockVoxelBuilder.MeshInput input)> _meshes = new();
    private BlockVoxelBuilder.Result? _result;
    private BlockVoxelBuilder.ElevationReport? _levels;
    private bool _busy, _ready;

    public VoxelVolumeWindow() { _ctx = null!; InitializeComponent(); }

    public VoxelVolumeWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        BlockModelStore.Adopt(ctx);
        foreach (var tb in new[] { txtVx, txtVy, txtVz }) tb.TextChanged += (_, _) => { if (_ready) UpdateEstimate(); };
        _ready = true;
        LoadSelection(ctx.SelectedMeshes());
    }

    private void LoadSelection(IReadOnlyList<MeshEntity> meshes)
    {
        _meshes.Clear();
        int skip = 0, open = 0;
        foreach (var me in meshes)
        {
            try { var input = BlockVoxelBuilder.FromMesh(me.Name, me.Verts, me.Tris); if (!input.Closed) open++; _meshes.Add((me, input)); }
            catch { skip++; }
        }
        if (_meshes.Count == 0)
            selInfoLabel.Text = meshes.Count == 0 ? "未选择对象。点「重新选择对象」在视口逐个点选封闭三角网体，Esc 结束。" : $"选中 {meshes.Count} 个实体，但无可用三角网（已跳过 {skip} 个）。";
        else
        {
            var b = BlockVoxelBuilder.UnionBounds(_meshes.Select(m => m.input).ToList());
            string aabb = b != null ? $"  并集 AABB X[{b.Value.minX:0.#},{b.Value.maxX:0.#}] Y[{b.Value.minY:0.#},{b.Value.maxY:0.#}] Z[{b.Value.minZ:0.#},{b.Value.maxZ:0.#}]" : "";
            string warn = "";
            if (open > 0) { warn = $"  ⚠ {open} 个非封闭/非流形 → 已自动勾选「高容错算量」(GWN)"; chkHighTolerance.IsChecked = true; }
            string skipped = skip > 0 ? $"（跳过非网格 {skip} 个）" : "";
            selInfoLabel.Text = $"封闭三角网体 {_meshes.Count} 个{skipped}{aabb}{warn}";
            // 体素尺寸不按包围盒自动填（原版固定默认 5；Z 另有「按标高建议」）：对角线/40 对十几米厚的煤层太粗。
        }
        ResetResults();
        UpdateEstimate();
    }

    private void ResetResults()
    {
        _result = null; _levels = null;
        btnGenerate.IsEnabled = btnExportCsv.IsEnabled = btnExportHtml.IsEnabled = false;
        totalLabel.Text = "整体体积：（点「计算」）";
        levelGrid.ItemsSource = null;
    }

    private bool TryParseVoxel(out double x, out double y, out double z)
    {
        x = y = z = 0;
        return P(txtVx.Text, out x) && x > 0 && P(txtVy.Text, out y) && y > 0 && P(txtVz.Text, out z) && z > 0;
    }
    private static bool P(string? s, out double v) => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private void UpdateEstimate()
    {
        if (_meshes.Count == 0 || !TryParseVoxel(out var vx, out var vy, out var vz)) { estimateLabel.Text = "网格规模：—"; estimateLabel.Foreground = Brushes.Gray; btnCompute.IsEnabled = false; return; }
        var est = BlockVoxelBuilder.EstimateGrid(_meshes.Select(m => m.input).ToList(), vx, vy, vz);
        if (est == null) { estimateLabel.Text = "网格规模：—"; btnCompute.IsEnabled = false; return; }
        var (nx, ny, nz, cells, _) = est.Value;
        bool over = cells > BlockVoxelBuilder.MaxCellsCompute;
        estimateLabel.Text = $"网格规模：{nx}×{ny}×{nz} = {cells:N0} cell（计算占用 ≈ {cells / 1024.0 / 1024.0:0.#} MB）" + (over ? $"  ✗ 超上限 {BlockVoxelBuilder.MaxCellsCompute:N0}，请增大体素尺寸" : "");
        estimateLabel.Foreground = over ? Brushes.IndianRed : Brushes.Gray;
        btnCompute.IsEnabled = !over && !_busy;
    }

    private async void OnCompute(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_meshes.Count == 0) { await Warn("请先选择封闭三角网体。"); return; }
        if (!TryParseVoxel(out var vx, out var vy, out var vz)) { await Warn("体素尺寸非法（X/Y/Z 均须 > 0 的数字）。"); return; }
        var boundaries = BlockVoxelBuilder.NormalizeLevels(BlockVoxelBuilder.ParseLevels(txtLevels.Text));
        int depth = chkAdaptive.IsChecked == true ? cmbDepth.SelectedIndex + 1 : 0;
        bool highTol = chkHighTolerance.IsChecked == true;
        var inputs = _meshes.Select(m => m.input).ToList();
        SetBusy(true);
        totalLabel.Text = highTol ? "计算中（高容错 GWN，稍慢）…" : "计算中…";
        _ctx.Status("体素格网体积 — 算量…");
        try
        {
            string err = "";
            var res = await Task.Run(() => BlockVoxelBuilder.Build(inputs, vx, vy, vz, out err, depth, highTolerance: highTol));
            SetBusy(false);
            if (res == null) { totalLabel.Text = "整体体积：（计算失败）"; await Warn(string.IsNullOrEmpty(err) ? "体素化失败。" : err); return; }
            _result = res;
            _levels = BlockVoxelBuilder.BinByElevation(res, boundaries);
            FillResultUi();
        }
        catch (Exception ex) { SetBusy(false); totalLabel.Text = "整体体积：（计算失败）"; await Warn(ex.Message); }
    }

    private void FillResultUi()
    {
        if (_result == null || _levels == null) return;
        string warn = _result.OpenMeshCount > 0 ? "  ⚠ 含非封闭网格，存疑" : "";
        string degrade = _result.SubCells.Count > 0 ? $" + 子块 {_result.SubCells.Count:N0}" : "";
        string exact = _result.ExactVolume > 0 ? $"；散度定理精确 {_result.ExactVolume:N1} m³，偏差 {(_result.TotalVolume - _result.ExactVolume) / _result.ExactVolume * 100:+0.##;-0.##}%" : "";
        totalLabel.Text = $"整体体积：{_result.TotalVolume:N1} m³（实心母块 {_result.KeepCount:N0} / {_result.TotalCells:N0}{degrade}）{warn}{exact}";
        var ci = CultureInfo.InvariantCulture;
        var rows = new List<LevelDisplayRow>();
        double total = _levels.TotalVolume;
        foreach (var lr in _levels.Levels)
            rows.Add(new LevelDisplayRow { Range = $"{lr.ZLow:0.###} ~ {lr.ZHigh:0.###}", Cells = lr.Cells.ToString("N0", ci), Volume = lr.Volume.ToString("N1", ci), Pct = total > 0 ? (lr.Volume / total * 100).ToString("0.##", ci) + "%" : "—" });
        if (_levels.OutOfRangeCells > 0)
            rows.Add(new LevelDisplayRow { Range = "区间外", Cells = _levels.OutOfRangeCells.ToString("N0", ci), Volume = _levels.OutOfRangeVolume.ToString("N1", ci), Pct = "—" });
        levelGrid.ItemsSource = rows;
        btnExportCsv.IsEnabled = btnExportHtml.IsEnabled = true;
        btnGenerate.IsEnabled = _result.KeepCount + _result.SubCells.Count > 0;
        _ctx.Status($"体素格网体积：{_result.TotalVolume:N1} m³（{_result.KeepCount:N0} 母块{degrade}）{exact}");
    }

    private async void OnGenerate(object? sender, RoutedEventArgs e)
    {
        if (_busy || _result == null) return;
        // 不按 _result.KeepCount 预判"体内无 cell"：自适应结果里边界母块都细分走了、KeepCount 可为 0，而均匀占位照样有 cell；以下面那次均匀体素化为准。
        var result = _result;
        var inputs = _meshes.Select(m => m.input).ToList();
        bool highTol = chkHighTolerance.IsChecked == true;
        SetBusy(true);
        _ctx.Status("体素格网体积 — 生成八叉树块体…");
        try
        {
            // 八叉树原生（同原版）：取满占位（逐 cell 中心在体内，均匀）→ 压八叉树，与「实体转块体」同路径。
            // 分层方量报表仍用 _result（含自适应百分比块）独立计算，不受影响。
            string err = "";
            var (res, leaves) = await Task.Run(() =>
            {
                var r = BlockVoxelBuilder.Build(inputs, result.Vx, result.Vy, result.Vz, out err, 0, highTolerance: highTol);
                return (r, r == null ? null : BlockVoxelBuilder.ToLeaves(r));
            });
            if (res == null || leaves == null || leaves.Count == 0) { SetBusy(false); await Warn(string.IsNullOrEmpty(err) ? "体内无 cell，无法生成块体。" : err); return; }
            if (leaves.Count > BlockVoxelBuilder.MaxRenderBlocks)
            {
                bool go = await BlockMsgBox.ConfirmAsync(this, "块数较多", $"将生成 {leaves.Count:N0} 块，超过视口逐块渲染建议上限 {BlockVoxelBuilder.MaxRenderBlocks:N0}，可能卡顿。仍要生成？");
                if (!go) { SetBusy(false); return; }
            }
            var meta = BlockVoxelBuilder.ToBlockModel(res, leaves, BlockModelStore.UniqueName(DefaultModelName));
            meta.DisplayStyle.FillColor = BlockDefaultPalette.Next(BlockModelStore.Models.Count);
            meta.ActiveColormapAttribute = BlockModelMeta.ZElevationSentinel;
            var cerr = BlockModelStore.Create(_ctx, meta);
            if (cerr != null) { SetBusy(false); await Warn($"创建块体失败：{cerr}"); return; }
            // 隐藏源网格并清选择集（高亮层不认可见性，留着会把块体盖住，见 EntityToBlocksWindow）
            int hidden = 0;
            foreach (var (me, _) in _meshes) { me.Visible = false; hidden++; }
            _ctx.Select(Array.Empty<SceneEntity>());
            _ctx.RefreshScene();
            SetBusy(false);
            _ctx.Status($"体素格网体积「{meta.Name}」：八叉树叶块 {leaves.Count:N0} 个 · 体内 cell {res.KeepCount:N0}");
            await BlockMsgBox.InfoAsync(this, "已生成块体模型",
                $"模型「{meta.Name}」\n  八叉树叶块：{leaves.Count:N0} 个（实心内部并大块、边界细到最细格）\n  体内 cell：{res.KeepCount:N0}\n  整体体积：{result.TotalVolume:N1} m³\n  已隐藏 {hidden} 个源网格（块体在体内，隐藏源面才看得见）\n\n注意：块体模型不进 Undo 栈，撤销请用「删除块体」对话框；源网格可在属性面板改回可见。");
        }
        catch (Exception ex) { SetBusy(false); await Warn(ex.Message); }
    }

    private async void OnExportCsv(object? sender, RoutedEventArgs e) => await Export(false);
    private async void OnExportHtml(object? sender, RoutedEventArgs e) => await Export(true);

    private async Task Export(bool html)
    {
        if (_result == null || _levels == null) { await Warn("请先计算。"); return; }
        try
        {
            string content = html ? BlockVoxelBuilder.RenderHtml(_result, _levels, DefaultModelName) : BlockVoxelBuilder.RenderCsv(_result, _levels, DefaultModelName);
            var path = await _ctx.SaveTextAsync(html ? "导出体积报表 (HTML)" : "导出体积报表 (CSV)", $"体素体积报表_{DateTime.Now:yyyyMMdd_HHmmss}." + (html ? "html" : "csv"), content);
            if (path != null) await BlockMsgBox.InfoAsync(this, "导出完成", $"已导出到\n{path}");
        }
        catch (Exception ex) { await Warn(ex.Message); }
    }

    private async void OnReselect(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try
        {
            selInfoLabel.Text = "请在视口逐个点选封闭三角网体，Esc 结束…";
            var picked = new List<MeshEntity>();
            while (true)
            {
                var ent = await _ctx.PickEntityAsync($"选封闭三角网体（已选 {picked.Count} 个，Esc 结束）", x => x is MeshEntity);
                if (ent is not MeshEntity me) break;
                if (!picked.Contains(me)) picked.Add(me);
                _ctx.Select(picked.Cast<SceneEntity>().ToList());
            }
            if (picked.Count > 0) LoadSelection(picked);
            else if (_meshes.Count == 0) selInfoLabel.Text = "未选择对象。";
            else LoadSelection(_meshes.Select(m => m.mesh).ToList());
        }
        catch (Exception ex) { await Warn(ex.Message); }
    }

    private async void OnSuggestZ(object? sender, RoutedEventArgs e)
    {
        var b = BlockVoxelBuilder.NormalizeLevels(BlockVoxelBuilder.ParseLevels(txtLevels.Text));
        if (b.Count < 2) { await Warn("请先在标高列表填至少 2 个标高，再按此自动建议体素 Z。"); return; }
        double minGap = double.PositiveInfinity;
        for (int i = 0; i + 1 < b.Count; i++) minGap = Math.Min(minGap, b[i + 1] - b[i]);
        if (!(minGap > 0) || double.IsInfinity(minGap)) { await Warn("标高间隔无效。"); return; }
        txtVz.Text = (minGap / 4.0).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        _busy = busy;
        btnReselect.IsEnabled = !busy;
        btnGenerate.IsEnabled = !busy && _result != null && _result.KeepCount + _result.SubCells.Count > 0;
        UpdateEstimate();
    }

    private Task Warn(string msg) => BlockMsgBox.WarnAsync(this, "体素格网体积", msg);
}
