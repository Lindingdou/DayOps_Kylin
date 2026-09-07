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
/// 实体转块体（忠实原 EntityToBlocksDialog，非模态）：选封闭三角网体（打开时取当前选中；「重新选择对象」逐个拾取，Esc 结束）→
/// 块尺寸 + 名称 + 次级退化深度 + 高容错 → 体素化（<see cref="BlockVoxelBuilder"/>）→ 生成块体模型并隐藏源网格。
/// 内外判定统一 GWN（Kylin 无奇偶射线测试器；「高容错」勾选只影响提示）。
/// </summary>
public partial class EntityToBlocksWindow : Window
{
    private readonly ModelingContext _ctx;
    private readonly List<(MeshEntity mesh, BlockVoxelBuilder.MeshInput input)> _meshes = new();
    private bool _busy, _ready;

    public EntityToBlocksWindow() { _ctx = null!; InitializeComponent(); }

    public EntityToBlocksWindow(ModelingContext ctx)
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
            try
            {
                var input = BlockVoxelBuilder.FromMesh(me.Name, me.Verts, me.Tris);
                if (!input.Closed) open++;
                _meshes.Add((me, input));
            }
            catch { skip++; }
        }
        if (_meshes.Count == 0)
            selInfoLabel.Text = meshes.Count == 0 ? "未选择对象。点「重新选择对象」在视口逐个点选封闭三角网体，Esc 结束。" : $"选中 {meshes.Count} 个实体，但无可用三角网（已跳过 {skip} 个）。";
        else
        {
            var b = BlockVoxelBuilder.UnionBounds(_meshes.Select(m => m.input).ToList());
            string aabb = b != null ? $"  并集 AABB X[{b.Value.minX:0.#},{b.Value.maxX:0.#}] Z[{b.Value.minZ:0.#},{b.Value.maxZ:0.#}]" : "";
            string warn = "";
            if (open > 0) { warn = $"  ⚠ {open} 个非封闭/非流形 → 已自动勾选「高容错算量」(GWN)"; chkHighTolerance.IsChecked = true; }
            string skipped = skip > 0 ? $"（跳过非网格 {skip} 个）" : "";
            selInfoLabel.Text = $"封闭三角网体 {_meshes.Count} 个{skipped}{aabb}{warn}";
            if (b != null)
            {
                double diag = Math.Sqrt(Math.Pow(b.Value.maxX - b.Value.minX, 2) + Math.Pow(b.Value.maxY - b.Value.minY, 2) + Math.Pow(b.Value.maxZ - b.Value.minZ, 2));
                double v = diag > 0 ? Math.Round(diag / 40, 3) : 5;
                if (v > 0 && (txtVx.Text ?? "5") == "5") { _ready = false; txtVx.Text = txtVy.Text = txtVz.Text = v.ToString("0.###", CultureInfo.InvariantCulture); _ready = true; }
            }
        }
        UpdateEstimate();
    }

    private bool TryParseVoxel(out double x, out double y, out double z)
    {
        x = y = z = 0;
        return P(txtVx.Text, out x) && x > 0 && P(txtVy.Text, out y) && y > 0 && P(txtVz.Text, out z) && z > 0;
    }
    private static bool P(string? s, out double v) => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private void UpdateEstimate()
    {
        if (_meshes.Count == 0 || !TryParseVoxel(out var vx, out var vy, out var vz)) { estimateLabel.Text = "网格规模：—"; estimateLabel.Foreground = Brushes.Gray; btnGenerate.IsEnabled = false; return; }
        var est = BlockVoxelBuilder.EstimateGrid(_meshes.Select(m => m.input).ToList(), vx, vy, vz);
        if (est == null) { estimateLabel.Text = "网格规模：—"; btnGenerate.IsEnabled = false; return; }
        var (nx, ny, nz, cells, _) = est.Value;
        bool over = cells > BlockVoxelBuilder.MaxCellsCompute;
        estimateLabel.Text = $"网格规模：{nx}×{ny}×{nz} = {cells:N0} cell（计算占用 ≈ {cells / 1024.0 / 1024.0:0.#} MB）" + (over ? $"  ✗ 超上限 {BlockVoxelBuilder.MaxCellsCompute:N0}，请增大块尺寸" : "");
        estimateLabel.Foreground = over ? Brushes.IndianRed : Brushes.Gray;
        btnGenerate.IsEnabled = !over && !_busy;
    }

    private async void OnGenerate(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_meshes.Count == 0) { await Warn("请先选择封闭三角网体。"); return; }
        if (!TryParseVoxel(out var vx, out var vy, out var vz)) { await Warn("块尺寸非法（X/Y/Z 均须 > 0）。"); return; }
        string name = string.IsNullOrWhiteSpace(txtName.Text) ? "实体块体" : txtName.Text!.Trim();
        int depth = chkAdaptive.IsChecked == true ? cmbDepth.SelectedIndex + 1 : 0;
        bool highTol = chkHighTolerance.IsChecked == true;
        var inputs = _meshes.Select(m => m.input).ToList();
        SetBusy(true);
        estimateLabel.Text = highTol ? "体素化中（高容错 GWN，稍慢）…" : "体素化中…";
        _ctx.Status("实体转块体 — 体素算量…");
        try
        {
            string err = "";
            var res = await Task.Run(() => BlockVoxelBuilder.Build(inputs, vx, vy, vz, out err, depth));
            if (res == null) { SetBusy(false); UpdateEstimate(); await Warn(string.IsNullOrEmpty(err) ? "体素化失败。" : err); return; }
            if (res.KeepCount + res.SubCells.Count == 0) { SetBusy(false); UpdateEstimate(); await Warn("体内无 cell（块尺寸过大或网格非封闭），未生成块体。"); return; }
            long n = res.KeepCount + res.SubCells.Count;
            if (n > BlockVoxelBuilder.MaxRenderBlocks)
            {
                bool go = await BlockMsgBox.ConfirmAsync(this, "块数较多", $"将生成 {n:N0} 块，超过视口逐块渲染建议上限 {BlockVoxelBuilder.MaxRenderBlocks:N0}，可能卡顿。仍要生成？");
                if (!go) { SetBusy(false); UpdateEstimate(); return; }
            }
            var meta = BlockVoxelBuilder.ToBlockModel(res, BlockModelStore.UniqueName(name));
            meta.DisplayStyle.FillColor = BlockDefaultPalette.Next(BlockModelStore.Models.Count);
            meta.ActiveColormapAttribute = BlockModelMeta.ZElevationSentinel;
            var cerr = BlockModelStore.Create(_ctx, meta);
            if (cerr != null) { SetBusy(false); UpdateEstimate(); await Warn($"创建块体失败：{cerr}"); return; }
            int hidden = 0;
            foreach (var (me, _) in _meshes) { me.Visible = false; hidden++; }
            _ctx.RefreshScene();
            SetBusy(false); UpdateEstimate();
            _ctx.Status($"实体转块体「{meta.Name}」：{n:N0} 块 · 体积 {res.TotalVolume:N1} m³");
            await BlockMsgBox.InfoAsync(this, "实体转块体完成",
                $"模型「{meta.Name}」\n  块：{n:N0} 个（实心母块 {res.KeepCount:N0} + 边界子块 {res.SubCells.Count:N0}）\n  体内 cell：{res.KeepCount:N0}\n  块尺寸：{vx:0.###}×{vy:0.###}×{vz:0.###} m\n  体积：{res.TotalVolume:N1} m³{(res.ExactVolume > 0 ? $"（散度定理精确 {res.ExactVolume:N1}）" : "")}\n  已隐藏 {hidden} 个源网格（块体在体内，隐藏源面才看得见）\n\n注意：块体模型不进 Undo 栈，撤销请用「删除块体」对话框；源网格可在属性面板改回可见。");
        }
        catch (Exception ex) { SetBusy(false); UpdateEstimate(); await Warn(ex.Message); }
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

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy) { _busy = busy; btnReselect.IsEnabled = !busy; btnGenerate.IsEnabled = !busy; }
    private Task Warn(string msg) => BlockMsgBox.WarnAsync(this, "实体转块体", msg);
}
