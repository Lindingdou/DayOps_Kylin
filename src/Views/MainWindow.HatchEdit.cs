using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「编辑填充」（HATCHEDIT）：双击填充 / 填充 ▾ → 编辑填充 / 命令行 编辑填充，都开 <see cref="HatchEditWindow"/>，
/// 确定后把 图案/角度/比例/十字/颜色 写进选中的填充并就地重算(可撤销)。动词-名词：没选中就先让选一个。
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// 双击落点处若是填充(空闲态、图层未锁)就开编辑对话框；返回 true 表示已接管这次双击。
    /// 非填充/非空闲不接管，双击仍是原来的语义（文字在位编辑 / 多段线收笔 / 范围缩放）。
    /// </summary>
    private bool TryBeginHatchEditAt(Avalonia.Point p)
    {
        if (TextEditing || _tool != null || _editMode != EditMode.None || _measure != null || _gripDrag.Active) return false;
        HatchEntity? h;
        if (Viewport.Is2DView)
        {
            var w = Viewport.ScreenToWorld(p.X, p.Y);
            if (w == null) return false;
            h = PickWorld2D(w.Value.x, w.Value.y, SnapTolWorld(p)) as HatchEntity;
        }
        else
            h = SelectionBox.PickScreen(_scene.Entities, p.X, p.Y, _snapTolPx, Viewport.WorldToScreenDepthProjector(), _layers.IsSelectable) as HatchEntity;
        if (h == null) return false;
        if (IsLayerLocked(h)) { StatusMsg.Text = $"编辑填充：图层「{h.LayerName}」已锁定，不可编辑"; return true; }
        // 双击的第一击已点选它；若它在一个多选里, 就一并改选中的全部填充(同 AutoCAD 对选择集开编辑)
        var targets = _selected.Contains(h) ? SelectedHatches() : new List<HatchEntity> { h };
        if (!_selected.Contains(h)) { _selected.Clear(); _selected.Add(h); HighlightSelection(); }
        _ = OpenHatchEditWindowAsync(targets);
        return true;
    }

    /// <summary>命令「编辑填充」（HATCHEDIT）：选中态直接编辑选中的填充；否则先让选一个。</summary>
    private async Task HatchEditCommandAsync()
    {
        var targets = SelectedHatches();
        if (targets.Count == 0)
        {
            var e = await PickEntityAsync("编辑填充：选择要编辑的填充（点它的图案线；实心填充点内部；Esc 取消）", x => x is HatchEntity);
            if (e is not HatchEntity picked) { StatusMsg.Text = "编辑填充：未选到填充"; SyncPrompt(); return; }
            targets = new List<HatchEntity> { picked };
            _selected.Clear(); _selected.Add(picked); HighlightSelection();
        }
        var locked = targets.Where(IsLayerLocked).ToList();
        if (locked.Count == targets.Count) { StatusMsg.Text = $"编辑填充：图层「{locked[0].LayerName}」已锁定，不可编辑"; return; }
        await OpenHatchEditWindowAsync(targets.Except(locked).ToList());
    }

    /// <summary>开对话框；确定后写回 targets 并重算。自检下非模态打开(好截图、不阻塞脚本)。</summary>
    private async Task OpenHatchEditWindowAsync(List<HatchEntity> targets)
    {
        if (targets.Count == 0) return;
        var sample = targets[0];
        var layer = _layers.Get(sample.LayerName);
        var layerRgb = layer != null ? (layer.Cr, layer.Cg, layer.Cb) : (_layers.Current.Cr, _layers.Current.Cg, _layers.Current.Cb);
        var w = new HatchEditWindow(sample, targets.Count, layerRgb);
        if (System.Environment.GetEnvironmentVariable("PITMINE_SELFTEST") is { Length: > 0 })
        {
            w.Show(this);
            GeoDb.GeoDbWindows.NoteLast(w);   // 让 @页面截图 能整窗渲染它
            w.Closed += (_, _) => { if (w.Accepted) ApplyHatchEdit(targets, w.PatternName, w.Scale, w.Angle, w.Cross, w.Rgb); };   // 同模态那条路
            StatusMsg.Text = $"编辑填充：对话框已打开（{targets.Count} 个填充 · {sample.Describe()}）";
            return;
        }
        bool ok = await w.ShowDialog<bool>(this);
        if (!ok) { StatusMsg.Text = "编辑填充：未改动"; return; }
        ApplyHatchEdit(targets, w.PatternName, w.Scale, w.Angle, w.Cross, w.Rgb);
    }

    /// <summary>把一组参数写进这些填充并就地重算(一步可撤销)；rgb=null 按各自图层色。</summary>
    private void ApplyHatchEdit(List<HatchEntity> targets, string patternName, double scale, double angle, bool cross, (float r, float g, float b)? rgb)
    {
        BeginChange();
        foreach (var h in targets)
        {
            h.PatternName = patternName; h.Scale = scale; h.Angle = angle; h.Cross = cross;
            var c = rgb ?? (_layers.Get(h.LayerName) is { } l ? (l.Cr, l.Cg, l.Cb) : (h.Cr, h.Cg, h.Cb));
            h.Cr = c.r; h.Cg = c.g; h.Cb = c.b;
            h.Invalidate();
        }
        RefreshScene();
        HighlightSelection();
        SyncHatchRibbonFromSelection();   // 功能区 图案/比例/角度/颜色 跟着回显
        string color = rgb is { } v ? AciPalette.DisplayName(v.r, v.g, v.b) : "随层";
        StatusMsg.Text = $"编辑填充：{targets.Count} 个填充 → {targets[0].Describe()} · 颜色 {color}";
    }
}
