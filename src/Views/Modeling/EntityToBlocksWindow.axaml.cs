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
/// 块尺寸 + 名称 + 高容错 → 逐 cell 中心在体内的均匀占位（<see cref="BlockVoxelBuilder.Build"/> depth=0）→
/// 压成八叉树叶块（<see cref="OctreeLeafBuilder"/>：实心内部并大块、边界细到最细格）→ 生成块体模型并隐藏源网格。
/// 「次级退化 / 深度」两个控件与原版一样只是摆着：原版这条路早已改走八叉树叶块，不再产百分比子块 ——
/// 此前 Kylin 把它接成了自适应百分比块，煤层这类"比块还薄"的体在母块间两头中心都在外时整列被丢，体积少两成、块体有洞。
/// 内外判定默认奇偶射线，「高容错」且非闭合才换 GWN（同原版）。
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
        // 自检：窗口一开就按默认参数生成一次（同 MeshVolumeWindow 的 PITMINE_SELFTEST 直通），好在批量截图里核对块体
        if (Environment.GetEnvironmentVariable("PITMINE_SELFTEST") is { Length: > 0 })
            Opened += (_, _) => OnGenerate(this, new RoutedEventArgs());
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
            // 块尺寸不按包围盒自动填（原版固定默认 5）：按对角线/40 填出来的 20~30 m 块对煤层这种十几米厚的体太粗，一半的列连一个中心都罩不住。
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
        bool highTol = chkHighTolerance.IsChecked == true;
        var inputs = _meshes.Select(m => m.input).ToList();
        SetBusy(true);
        estimateLabel.Text = highTol ? "体素化中（高容错 GWN，稍慢）…" : "体素化中…";
        _ctx.Status("实体转块体 — 体素算量…");
        try
        {
            // 八叉树原生（同原版）：整网格逐 cell 中心在体内的占位位图（均匀，不做百分比子块）→ 压成八叉树叶块。
            string err = "";
            var (res, leaves) = await Task.Run(() =>
            {
                var r = BlockVoxelBuilder.Build(inputs, vx, vy, vz, out err, 0, highTolerance: highTol);
                return (r, r == null ? null : BlockVoxelBuilder.ToLeaves(r));
            });
            if (res == null || leaves == null) { SetBusy(false); UpdateEstimate(); await Warn(string.IsNullOrEmpty(err) ? "体素化失败。" : err); return; }
            if (leaves.Count == 0) { SetBusy(false); UpdateEstimate(); await Warn("体内无 cell（块尺寸过大或网格非封闭），未生成块体。"); return; }
            if (leaves.Count > BlockVoxelBuilder.MaxRenderBlocks)
            {
                bool go = await BlockMsgBox.ConfirmAsync(this, "块数较多", $"将生成 {leaves.Count:N0} 块，超过视口逐块渲染建议上限 {BlockVoxelBuilder.MaxRenderBlocks:N0}，可能卡顿。仍要生成？");
                if (!go) { SetBusy(false); UpdateEstimate(); return; }
            }
            var meta = BlockVoxelBuilder.ToBlockModel(res, leaves, BlockModelStore.UniqueName(name));
            meta.DisplayStyle.FillColor = BlockDefaultPalette.Next(BlockModelStore.Models.Count);
            meta.ActiveColormapAttribute = BlockModelMeta.ZElevationSentinel;
            var cerr = BlockModelStore.Create(_ctx, meta);
            if (cerr != null) { SetBusy(false); UpdateEstimate(); await Warn($"创建块体失败：{cerr}"); return; }
            // 隐藏源网格：块体在体内，源面不透明会遮住块体。隐藏的同时清掉选择集 —— 高亮层不认可见性，
            // 留着的话整张网仍以青色高亮面盖在块体外头，看着就像"转了个寂寞"。
            int hidden = 0;
            foreach (var (me, _) in _meshes) { me.Visible = false; hidden++; }
            _ctx.Select(Array.Empty<SceneEntity>());
            _ctx.RefreshScene();
            SetBusy(false); UpdateEstimate();
            _ctx.Status($"实体转块体「{meta.Name}」：八叉树叶块 {leaves.Count:N0} 个 · 体内 cell {res.KeepCount:N0} · 体积 {res.TotalVolume:N1} m³");
            await BlockMsgBox.InfoAsync(this, "实体转块体完成",
                $"模型「{meta.Name}」\n  八叉树叶块：{leaves.Count:N0} 个（实心内部并大块、边界细到最细格）\n  体内 cell：{res.KeepCount:N0}\n  块尺寸：{vx:0.###}×{vy:0.###}×{vz:0.###} m\n  已隐藏 {hidden} 个源网格（块体在体内，隐藏源面才看得见）\n\n注意：块体模型不进 Undo 栈，撤销请用「删除块体」对话框；源网格可在属性面板改回可见。");
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
