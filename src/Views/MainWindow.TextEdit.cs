using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 文字二次编辑（AutoCAD TEXTEDIT / DDEDIT）：双击文字或命令「编辑文字」→ 在文字原位弹出编辑框改内容，
/// 回车确认、Esc 取消、点别处也算确认；改动压一步 Undo。
/// 原版 PitMine3D 没有这条（其文字只能删了重打），按用户要求补上，交互照 AutoCAD 的双击在位编辑。
/// </summary>
public partial class MainWindow
{
    private TextBox? _textEditor;        // 在位编辑框（挂在视口叠层里，编辑期间叠层临时可命中）
    private TextEntity? _textEditing;    // 正在编辑的文字

    /// <summary>正在在位编辑文字（视口级按键/双击要让路）。</summary>
    private bool TextEditing => _textEditor != null;

    /// <summary>
    /// 双击落点处若是文字（2D、空闲态、图层未锁）就开编辑；返回 true 表示已接管这次双击。
    /// 非文字/非空闲不接管，双击仍是原来的语义（多段线收笔 / 范围缩放）。
    /// </summary>
    private bool TryBeginTextEditAt(Avalonia.Point p)
    {
        if (TextEditing) return true;
        if (!Viewport.Is2DView || _tool != null || _editMode != EditMode.None || _measure != null || _gripDrag.Active) return false;
        var w = Viewport.ScreenToWorld(p.X, p.Y);
        if (w == null) return false;
        if (PickWorld2D(w.Value.x, w.Value.y, SnapTolWorld(p)) is not TextEntity t) return false;
        if (IsLayerLocked(t)) { StatusMsg.Text = $"编辑文字：图层「{t.LayerName}」已锁定，不可编辑"; return true; }
        // 编辑的那条就是选中的那条（双击的第一击已点选；命令路径下这里补一次）
        if (!_selected.Contains(t)) { _selected.Clear(); _selected.Add(t); HighlightSelection(); }
        BeginTextEdit(t);
        return true;
    }

    /// <summary>命令「编辑文字」（TEXTEDIT / DDEDIT / ED）：选一条文字 → 在位编辑。动词-名词，选中态直接用选中的那条。</summary>
    private async Task TextEditCommandAsync()
    {
        if (TextEditing) return;
        TextEntity? t = _selected.Count == 1 ? _selected[0] as TextEntity : null;
        if (t == null)
        {
            var e = await PickEntityAsync("编辑文字：选择要编辑的文字（Esc 取消）", x => x is TextEntity);
            if (e is not TextEntity picked) { StatusMsg.Text = "编辑文字：未选到文字"; SyncPrompt(); return; }
            t = picked;
            _selected.Clear(); _selected.Add(t); HighlightSelection();
        }
        if (IsLayerLocked(t)) { StatusMsg.Text = $"编辑文字：图层「{t.LayerName}」已锁定，不可编辑"; return; }
        BeginTextEdit(t);
    }

    /// <summary>在文字原位（其屏幕包围盒左上角）放一个编辑框，字号按当前缩放下的字高折算，看起来就是在改那行字。</summary>
    private void BeginTextEdit(TextEntity t)
    {
        EndTextEdit(commit: false);
        if (_active.DragTip?.Parent is not Panel overlay) return;

        // 文字的屏幕包围盒：字形局部笔画 → 旋转/平移到锚点(世界系) → 逐点投影。
        // 用 LocalStrokes 而不是 TessellatePick：后者出的是渲染局部坐标(减过原点), 这里要的是世界点。
        double sx0 = double.MaxValue, sy0 = double.MaxValue, sx1 = double.MinValue, sy1 = double.MinValue;
        double rc = System.Math.Cos(t.Rotation), rs = System.Math.Sin(t.Rotation);
        void Acc(double lx, double ly)
        {
            var sp = Viewport.WorldToScreen(t.X + lx * rc - ly * rs, t.Y + lx * rs + ly * rc, t.Elevation);
            if (sp == null) return;
            if (sp.Value.sx < sx0) sx0 = sp.Value.sx; if (sp.Value.sx > sx1) sx1 = sp.Value.sx;
            if (sp.Value.sy < sy0) sy0 = sp.Value.sy; if (sp.Value.sy > sy1) sy1 = sp.Value.sy;
        }
        foreach (var (lx0, ly0, lx1, ly1) in t.LocalStrokes()) { Acc(lx0, ly0); Acc(lx1, ly1); }
        var anchor = Viewport.WorldToScreen(t.X, t.Y, t.Elevation) ?? (0, 0);
        if (sx0 > sx1) { sx0 = sx1 = anchor.sx; sy0 = sy1 = anchor.sy; }   // 空串/投不出来：落在锚点
        double wpp = SnapTolWorld(new Avalonia.Point(anchor.sx, anchor.sy)) / System.Math.Max(_snapTolPx, 1);   // 该处每像素的世界长度
        double fontPx = wpp > 1e-12 ? System.Math.Clamp(t.Height / wpp, 11, 96) : 14;   // 字号跟着屏幕上的字高走, 太小看不清、太大占满视口
        bool multi = t.Text.Contains('\n');

        double w = ViewportHost.Bounds.Width, h = ViewportHost.Bounds.Height;
        double left = System.Math.Clamp(sx0 - 6, 0, System.Math.Max(0, w - 160));
        double top = System.Math.Clamp(sy0 - 6, 0, System.Math.Max(0, h - fontPx * 2));
        var tb = new TextBox
        {
            Text = t.Text,
            FontSize = fontPx,
            AcceptsReturn = multi,
            MinWidth = System.Math.Max(160, sx1 - sx0 + 28),
            MaxWidth = System.Math.Max(200, w - left),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Avalonia.Thickness(left, top, 0, 0),
            Cursor = new Cursor(StandardCursorType.Ibeam),
        };
        if (!string.IsNullOrEmpty(GlyphFontHost.ActiveFamily)) tb.FontFamily = new Avalonia.Media.FontFamily(GlyphFontHost.ActiveFamily);
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { EndTextEdit(commit: false); e.Handled = true; }
            else if (e.Key == Key.Enter && (!multi || e.KeyModifiers.HasFlag(KeyModifiers.Control))) { EndTextEdit(commit: true); e.Handled = true; }
        };
        tb.LostFocus += (s, _) => { if (ReferenceEquals(s, _textEditor)) EndTextEdit(commit: true); };   // 点别处 = 确认(同 AutoCAD 在位编辑)

        overlay.IsHitTestVisible = true;   // 叠层平时命中透传; 编辑期间要接住键鼠
        overlay.Children.Add(tb);
        overlay.InvalidateVisual();
        _textEditor = tb; _textEditing = t;
        tb.Focus(); tb.SelectAll();
        StatusMsg.Text = multi ? "编辑文字：修改内容，Ctrl+回车确认 · Esc 取消 · 点别处也算确认"
                               : "编辑文字：修改内容后回车确认 · Esc 取消 · 点别处也算确认";
    }

    /// <summary>收编辑框：commit 且内容有变才落地（一步 Undo）；空串不落地（同 AutoCAD 不允许空文字）。</summary>
    private void EndTextEdit(bool commit)
    {
        var tb = _textEditor; var t = _textEditing;
        if (tb == null || t == null) return;
        _textEditor = null; _textEditing = null;
        string newText = (tb.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (tb.Parent is Panel overlay)
        {
            overlay.Children.Remove(tb);
            overlay.IsHitTestVisible = false;
            overlay.InvalidateVisual();
        }
        if (commit && newText.Length > 0 && newText != t.Text)
        {
            BeginChange();
            t.Text = newText;
            RefreshScene();
            HighlightSelection();
            StatusMsg.Text = $"文字已改为「{newText.Replace('\n', '|')}」";
        }
        else StatusMsg.Text = commit ? (newText.Length == 0 ? "编辑文字：内容为空，未改动" : "编辑文字：内容未变") : "编辑文字：已取消";
        Viewport.Focus();
        SyncPrompt();
    }
}
