using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 删除块体对话框（忠实原 DeleteBlockModelDialog，非模态单例）：
/// Tab1 整体删除（多选模型）/ Tab2 删除单元（按表达式 <see cref="BlockCellPredicate"/> / 按世界 AABB）/
/// Tab3 按多段线范围裁剪（视口逐条拾取或用当前选中；删圈外/圈内；限高程；撤销本次裁剪）。底部「恢复全部」跟随当前 Tab 目标。
/// </summary>
public partial class DeleteBlockModelWindow : Window
{
    private readonly ModelingContext _ctx;
    private BlockPolygonRegion? _region;
    private double _pickedMinZ, _pickedMaxZ;
    private readonly BlockDeleteRecord _polySession = new();
    private bool _closed;

    public DeleteBlockModelWindow() { _ctx = null!; InitializeComponent(); }

    public DeleteBlockModelWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        BlockModelStore.Adopt(ctx);
        modelsList.ItemsSource = BlockModelStore.Models;
        cellTargetCombo.ItemsSource = BlockModelStore.Models;
        polyTargetCombo.ItemsSource = BlockModelStore.Models;
        var def = BlockModelStore.PickDefault();
        if (def != null) { cellTargetCombo.SelectedItem = def; polyTargetCombo.SelectedItem = def; }
        Closed += (_, _) => _closed = true;
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        int tab = mainTab.SelectedIndex;
        if (tab == 0) await DeleteWholeModels();
        else if (tab == 1) await DeleteCells();
        else await ApplyPolyClip();
    }

    // ── Tab 1 ──
    private async System.Threading.Tasks.Task DeleteWholeModels()
    {
        try
        {
            var toDelete = modelsList.SelectedItems?.OfType<BlockModelMeta>().ToList() ?? new List<BlockModelMeta>();
            if (toDelete.Count == 0) { await BlockMsgBox.WarnAsync(this, "删除块体", "请先在列表里选中至少一个模型。"); return; }
            string names = string.Join("\n  - ", toDelete.Select(m => $"{m.Name} ({m.BlockCount:N0} 块)"));
            bool ok = await BlockMsgBox.ConfirmAsync(this, "确认删除", $"将永久删除以下 {toDelete.Count} 个块体模型：\n\n  - {names}\n\n操作不可撤销，确定？");
            if (!ok) return;
            foreach (var m in toDelete)
            {
                var err = BlockModelStore.Remove(_ctx, m);
                if (err != null) await BlockMsgBox.WarnAsync(this, "删除失败", $"{m.Name}: {err}");
            }
            _ctx.Status($"已删除 {toDelete.Count} 个块体模型");
            Close();
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "删除块体", ex.Message); }
    }

    // ── Tab 2 ──
    private async void OnValidateExpr(object? sender, RoutedEventArgs e)
    {
        try
        {
            var eng = BlockCellPredicate.Compile(exprBox.Text ?? "");
            string vars = string.Join(", ", eng.ReferencedVariables);
            await BlockMsgBox.InfoAsync(this, "表达式校验通过", $"语法 OK。\n\n引用变量: {(string.IsNullOrEmpty(vars) ? "(无)" : vars)}");
        }
        catch (BlockExprException ex) { await BlockMsgBox.WarnAsync(this, "表达式语法错误", ex.Message); }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "表达式校验", ex.Message); }
    }

    private async System.Threading.Tasks.Task DeleteCells()
    {
        try
        {
            if (cellTargetCombo.SelectedItem is not BlockModelMeta m) { await BlockMsgBox.WarnAsync(this, "删除单元", "请选择目标块体。"); return; }
            int added;
            if (modeExpr.IsChecked == true)
            {
                BlockCellPredicate pred;
                try { pred = BlockCellPredicate.Compile(exprBox.Text ?? ""); }
                catch (BlockExprException ex) { await BlockMsgBox.WarnAsync(this, "表达式语法错误", ex.Message); return; }
                var ctx = new MutableBlockExprContext(); m.FillConstants(ctx);
                var refAttrs = pred.ReferencedVariables.Where(v => !BlockModelMeta.IsBuiltinVar(v.ToLowerInvariant())).ToList();
                added = m.ApplyKeep((i, _) => { m.FillContext(ctx, i, refAttrs); return !pred.Evaluate(ctx); });
            }
            else
            {
                if (!P(minXBox.Text, out double minX) || !P(minYBox.Text, out double minY) || !P(minZBox.Text, out double minZ)
                 || !P(maxXBox.Text, out double maxX) || !P(maxYBox.Text, out double maxY) || !P(maxZBox.Text, out double maxZ))
                { await BlockMsgBox.WarnAsync(this, "删除单元", "AABB 输入有非法数字。"); return; }
                if (minX > maxX) (minX, maxX) = (maxX, minX); if (minY > maxY) (minY, maxY) = (maxY, minY); if (minZ > maxZ) (minZ, maxZ) = (maxZ, minZ);
                added = m.ApplyKeep((_, b) => !(b.X >= minX && b.X <= maxX && b.Y >= minY && b.Y <= maxY && b.Z >= minZ && b.Z <= maxZ));
            }
            BlockModelStore.RefreshDisplay(_ctx, m);
            await BlockMsgBox.InfoAsync(this, "删除完成", $"模型 {m.Name} 已删除 {added:N0} 个 cell（累计 {m.DeletedBlockCount:N0} / {m.RealBlockCount:N0}）");
            cellStatusLabel.Text = $"上次删除 {added:N0} cell；累计 {m.DeletedBlockCount:N0} / {m.RealBlockCount:N0}";
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "删除单元", ex.Message); }
    }

    private async void OnRestore(object? sender, RoutedEventArgs e)
    {
        try
        {
            bool onPoly = mainTab.SelectedIndex == 2;
            var combo = onPoly ? polyTargetCombo : cellTargetCombo;
            if (combo.SelectedItem is not BlockModelMeta m) { await BlockMsgBox.WarnAsync(this, "恢复 cell", "请选择目标块体。"); return; }
            int restored = m.ClearDeleted();
            _polySession.Clear();
            BlockModelStore.RefreshDisplay(_ctx, m);
            string note = $"已恢复 {restored:N0} cell";
            if (onPoly) polyStatusLabel.Text = note; else cellStatusLabel.Text = note;
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "恢复 cell", ex.Message); }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    // ── Tab 3 ──
    private async void OnPickPolylines(object? sender, RoutedEventArgs e)
    {
        try
        {
            polyStatusLabel.Text = "请在视口逐条点选边界多段线，Esc 结束…";
            var picked = new List<PolylineEntity>();
            while (true)
            {
                var ent = await _ctx.PickEntityAsync($"选边界多段线（已选 {picked.Count} 条，Esc 结束）", x => x is PolylineEntity);
                if (_closed) return;
                if (ent is not PolylineEntity pl) break;
                if (!picked.Contains(pl)) picked.Add(pl);
                _ctx.Select(picked.Cast<SceneEntity>().ToList());
            }
            LoadRegion(picked, "选多段线");
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "选多段线", ex.Message); }
    }

    private void OnUseSelectedPolylines(object? sender, RoutedEventArgs e) => LoadRegion(_ctx.SelectedPolylines(), "用当前选中");

    private void OnClearPolylines(object? sender, RoutedEventArgs e)
    {
        _region = null;
        polyInfoLabel.Text = "尚未拾取。点「在视口选多段线…」后逐条点选边界线，Esc 结束。";
        polyStatusLabel.Text = "已清空边界线。";
    }

    private async void LoadRegion(IReadOnlyList<PolylineEntity> lines, string opName)
    {
        if (_closed) return;
        if (lines.Count == 0) { polyStatusLabel.Text = "未选择任何对象，边界线保持不变。"; return; }
        var rings = new List<double[]>();
        int tooFew = 0, open = 0;
        double minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (var pl in lines)
        {
            var ring = BlockPolygonRegion.RingFromPoints(pl.Points);
            if (ring == null) { tooFew++; continue; }
            if (!pl.Closed) open++;
            for (int i = 0; i < pl.Points.Count; i++) { double z = pl.ZAt(i); if (z < minZ) minZ = z; if (z > maxZ) maxZ = z; }
            rings.Add(ring);
        }
        var region = BlockPolygonRegion.FromRings(rings);
        if (region == null)
        {
            await BlockMsgBox.WarnAsync(this, opName, $"选中的 {lines.Count} 个对象里没有可用的边界多段线（顶点不足 3 的 {tooFew} 个）。\n请选闭合或首尾可直连的多段线作范围边界。");
            return;
        }
        _region = region; _pickedMinZ = minZ; _pickedMaxZ = maxZ;
        string openNote = open > 0 ? $"，其中 {open} 条未闭合(已首尾直连)" : "";
        string skipNote = tooFew > 0 ? $"；跳过 {tooFew} 个退化对象" : "";
        polyInfoLabel.Text = $"已拾取 {region.RingCount} 条边界线（共 {region.VertexCount:N0} 点{openNote}）{skipNote}\n平面范围 X[{region.MinX:0.###}, {region.MaxX:0.###}]  Y[{region.MinY:0.###}, {region.MaxY:0.###}]  Z[{minZ:0.###}, {maxZ:0.###}]";
        polyStatusLabel.Text = "边界线已就绪，选处理方式后点「裁剪」。";
    }

    private async void OnFillZFromPolylines(object? sender, RoutedEventArgs e)
    {
        if (_region == null || _pickedMinZ > _pickedMaxZ) { await BlockMsgBox.WarnAsync(this, "取多段线高程", "请先拾取边界多段线。"); return; }
        polyMinZBox.Text = _pickedMinZ.ToString("0.###", CultureInfo.InvariantCulture);
        polyMaxZBox.Text = _pickedMaxZ.ToString("0.###", CultureInfo.InvariantCulture);
        polyUseZ.IsChecked = true;
    }

    private async void OnApplyPolyClip(object? sender, RoutedEventArgs e) => await ApplyPolyClip();

    private async System.Threading.Tasks.Task ApplyPolyClip()
    {
        try
        {
            if (polyTargetCombo.SelectedItem is not BlockModelMeta m) { await BlockMsgBox.WarnAsync(this, "多段线裁剪", "请选择目标块体。"); return; }
            if (_region is not { } region) { await BlockMsgBox.WarnAsync(this, "多段线裁剪", "请先拾取边界多段线（「在视口选多段线…」或「用当前选中」）。"); return; }
            bool deleteOutside = polyDeleteOutside.IsChecked == true, useZ = polyUseZ.IsChecked == true;
            double zMin = 0, zMax = 0;
            if (useZ)
            {
                if (!P(polyMinZBox.Text, out zMin) || !P(polyMaxZBox.Text, out zMax)) { await BlockMsgBox.WarnAsync(this, "多段线裁剪", "高程范围有非法数字。"); return; }
                if (zMin > zMax) (zMin, zMax) = (zMax, zMin);
            }
            var b = m.Bounds;
            if (!region.OverlapsXY(b.minX, b.minY, b.maxX, b.maxY))
            {
                bool go = await BlockMsgBox.ConfirmAsync(this, "范围不搭界",
                    $"边界线与块体在平面上没有任何交叠：\n\n  边界线  X[{region.MinX:0.#}, {region.MaxX:0.#}]  Y[{region.MinY:0.#}, {region.MaxY:0.#}]\n  块体    X[{b.minX:0.#}, {b.maxX:0.#}]  Y[{b.minY:0.#}, {b.maxY:0.#}]\n\n"
                    + (deleteOutside ? "按「删除范围以外」执行会把整个模型删光。多半是多段线和块体不在同一坐标系。\n\n仍要继续？" : "按「删除范围以内」执行不会删掉任何块。多半是多段线和块体不在同一坐标系。\n\n仍要继续？"));
                if (!go) return;
            }
            int affected = m.ApplyKeep((_, blk) =>
            {
                if (useZ && (blk.Z < zMin || blk.Z > zMax)) return true;
                bool inside = region.ContainsXY(blk.X, blk.Y);
                return deleteOutside ? inside : !inside;
            }, _polySession);
            BlockModelStore.RefreshDisplay(_ctx, m);
            string zNote = useZ ? $"\n  高程限制:   Z ∈ [{zMin:0.###}, {zMax:0.###}]（段外不动）" : "";
            await BlockMsgBox.InfoAsync(this, "裁剪完成", $"模型 {m.Name}\n  边界线:     {region.RingCount} 条 / {region.VertexCount:N0} 点\n  处理:       {(deleteOutside ? "删除范围以外（保留圈内）" : "删除范围以内（保留圈外）")}{zNote}\n  本次新增删除: {affected:N0} 块\n  累计已删:   {m.DeletedBlockCount:N0} / {m.RealBlockCount:N0}");
            polyStatusLabel.Text = $"上次新增删除 {affected:N0}；累计 {m.DeletedBlockCount:N0} / {m.RealBlockCount:N0}";
            long occ = m.RealBlockCount, remaining = Math.Max(0, m.LiveBlockCount());
            if (occ > 0 && remaining <= occ / 1000)
                await BlockMsgBox.WarnAsync(this, "多段线裁剪：模型几乎被删空", $"裁剪后仅剩 {remaining:N0} / {occ:N0} 个可见块。\n\n常见原因：\n① 处理方式选反（想留圈内却选了「删除范围以内」）；\n② 多段线与块体不在同一坐标系（核对上面两行 XY 范围是否重叠）。\n\n点「撤销本次裁剪」即可恢复后重试。");
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "多段线裁剪", ex.Message); }
    }

    private async void OnUndoPolyClip(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (polyTargetCombo.SelectedItem is not BlockModelMeta m) { await BlockMsgBox.WarnAsync(this, "撤销裁剪", "请选择目标块体。"); return; }
            int restored = m.Restore(_polySession);
            BlockModelStore.RefreshDisplay(_ctx, m);
            polyStatusLabel.Text = $"已撤销本次裁剪 {restored:N0} 块；累计仍删 {m.DeletedBlockCount:N0} / {m.RealBlockCount:N0}";
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "撤销裁剪", ex.Message); }
    }

    private static bool P(string? s, out double v) => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
