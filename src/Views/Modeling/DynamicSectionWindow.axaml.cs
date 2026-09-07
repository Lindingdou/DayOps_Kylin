using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 「动态剖面」窗（原 MeshEditLib.Sections.DynamicSectionWindow）：定一条基准剖面线（视口两点 / 手填），对场景三角网竖直切，
/// 在 2D 面板实时预览；拖滑块沿剖面法向平移，剖面实时重算重绘（<see cref="SectionEngine"/> 纯函数当每帧内核）；
/// 「展绘到场景」把当前位置的原位交线 + 展开剖面图落地（<see cref="SectionBuilder"/>）。
/// 厚度分析：鼠标在剖面上移动即在该里程处竖直采各层位标高，算相邻层位间厚度，游标处标注 + 底部读数行。
/// </summary>
public partial class DynamicSectionWindow : Window
{
    private readonly ModelingContext? _ctx;
    private List<(double[] verts, int[] tris)> _meshes = new();
    private readonly List<string> _meshLabels = new();
    private double _sx, _sy, _ex, _ey;
    private bool _hasLine;
    private List<SectionChain> _lastChains = new();
    private double _lastTotalLen;
    private int _scanTotal, _scanMesh;

    private bool _plotValid;
    private double _mL, _mT, _plotW, _plotH, _sScale, _zMin, _zMax, _vex, _zScale;

    public DynamicSectionWindow()
    {
        InitializeComponent();
        previewCanvas.SizeChanged += (_, _) => DrawCanvas();
    }
    public DynamicSectionWindow(ModelingContext ctx) : this() { _ctx = ctx; }

    // ── 基准线拾取 ─────────────────────────────────────────────
    private async void OnPickTwoPoints(object? sender, RoutedEventArgs e)
    {
        if (_ctx == null) { statusText.Text = "当前宿主未提供拾取能力，请手填 X1/Y1/X2/Y2。"; return; }
        statusText.Text = "在视口点第 1 点…（Esc 取消）";
        var p1 = await _ctx.PickPointAsync("动态剖面：在视口点第 1 点（Esc 取消）");
        if (p1 == null) { statusText.Text = "已取消拾取。"; return; }
        statusText.Text = "在视口点第 2 点…";
        var p2 = await _ctx.PickPointAsync("动态剖面：在视口点第 2 点（Esc 取消）");
        if (p2 == null) { statusText.Text = "已取消拾取。"; return; }
        txtX1.Text = p1.Value.x.ToString("0.###", CultureInfo.InvariantCulture);
        txtY1.Text = p1.Value.y.ToString("0.###", CultureInfo.InvariantCulture);
        txtX2.Text = p2.Value.x.ToString("0.###", CultureInfo.InvariantCulture);
        txtY2.Text = p2.Value.y.ToString("0.###", CultureInfo.InvariantCulture);
        statusText.Text = "两点已拾取，点「生成/刷新预览」。";
    }

    // ── 生成/刷新预览 ─────────────────
    private void OnBuildPreview(object? sender, RoutedEventArgs e)
    {
        if (!P(txtX1.Text, out _sx) || !P(txtY1.Text, out _sy) || !P(txtX2.Text, out _ex) || !P(txtY2.Text, out _ey))
        { statusText.Text = "请先定两点（视口拾取或手填 X1/Y1/X2/Y2）。"; return; }
        if (Math.Abs(_ex - _sx) < 1e-6 && Math.Abs(_ey - _sy) < 1e-6) { statusText.Text = "两点重合。"; return; }

        GatherMeshesWithLayers();
        if (_meshes.Count == 0)
        {
            statusText.Text = _scanMesh > 0
                ? $"扫描到 {_scanMesh} 张三角网，但都读不出几何（空网/退化网）。"
                : $"场景没有三角网「面」可切（扫描 {_scanTotal} 个三角网实体）。剖面只切三角网面——点/多段线不算；请先创建/导入三角网。";
            _hasLine = false;
            return;
        }
        _hasLine = true;
        Recompute();
    }

    private void GatherMeshesWithLayers()
    {
        _meshes = new List<(double[] verts, int[] tris)>();
        _meshLabels.Clear();
        var all = _ctx?.Meshes() ?? Array.Empty<MeshEntity>();
        _scanTotal = all.Count; _scanMesh = 0;
        int genericIdx = 0;
        foreach (var m in all)
        {
            _scanMesh++;
            if (m.Verts.Count < 3 || m.Tris.Count < 1) continue;
            _meshes.Add(m.Flatten());
            _meshLabels.Add(!string.IsNullOrWhiteSpace(m.Name) ? m.Name : !string.IsNullOrWhiteSpace(m.LayerName) ? m.LayerName : $"面{++genericIdx}");
        }
    }

    private void OnOffsetChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (offsetLabel != null) offsetLabel.Text = $"{e.NewValue:0.#} m";
        if (_hasLine) Recompute();
    }

    private void OnResetOffset(object? sender, RoutedEventArgs e) => offsetSlider.Value = 0;

    /// <summary>当前滑块偏移下的剖面线（基准线沿左法向平移 offset）。</summary>
    public static double[] SectionAtOffset(double sx, double sy, double ex, double ey, double off)
    {
        double dx = ex - sx, dy = ey - sy;
        double len = Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / len, uy = dy / len;
        double nx = -uy, ny = ux;
        return new[] { sx + nx * off, sy + ny * off, 0.0, ex + nx * off, ey + ny * off, 0.0 };
    }

    private double[] SectionAtOffset() => SectionAtOffset(_sx, _sy, _ex, _ey, offsetSlider.Value);

    private void Recompute()
    {
        if (!_hasLine) return;
        var section = SectionAtOffset();
        _lastChains = SectionEngine.Build(_meshes, section, out _lastTotalLen, out string warn);
        DrawCanvas();
        statusText.Text = _lastChains.Count > 0
            ? $"偏移 {offsetSlider.Value:0.#} m：剖面线 {_lastTotalLen:0.#} m，交线 {_lastChains.Count} 条。{warn}"
            : $"偏移 {offsetSlider.Value:0.#} m：此位置未切到三角网。{warn}";
    }

    // ── 2D 预览绘制 ────────────────────────────────────────────
    private void DrawCanvas()
    {
        previewCanvas.Children.Clear();
        overlayCanvas.Children.Clear();
        _plotValid = false;
        double cw = previewCanvas.Bounds.Width, ch = previewCanvas.Bounds.Height;
        if (cw < 40 || ch < 40 || _lastChains.Count == 0 || _lastTotalLen <= 1e-6) return;

        double vex = Math.Max(0.01, PD(txtVex.Text, 1));
        double zMin = double.MaxValue, zMax = double.MinValue;
        foreach (var c in _lastChains)
            for (int i = 1; i < c.Sz.Count; i += 2) { if (c.Sz[i] < zMin) zMin = c.Sz[i]; if (c.Sz[i] > zMax) zMax = c.Sz[i]; }
        if (zMax - zMin < 1e-6) zMax = zMin + 1;

        const double mL = 46, mR = 16, mT = 14, mB = 28;
        double plotW = cw - mL - mR, plotH = ch - mT - mB;
        if (plotW < 10 || plotH < 10) return;

        double sScale = plotW / _lastTotalLen;
        double zSpan = (zMax - zMin) * vex;
        double zScale = plotH / zSpan;
        double SToX(double s) => mL + s * sScale;
        double ZToY(double z) => mT + plotH - (z - zMin) * vex * zScale;

        var axis = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        AddRect(mL, mT, plotW, plotH, axis);
        AddText(zMin.ToString("0.#"), 2, ZToY(zMin) - 7, 10, axis);
        AddText(((zMin + zMax) * 0.5).ToString("0.#"), 2, ZToY((zMin + zMax) * 0.5) - 7, 10, axis);
        AddText(zMax.ToString("0.#"), 2, ZToY(zMax) - 2, 10, axis);
        AddText("0", mL, mT + plotH + 4, 10, axis);
        AddText(_lastTotalLen.ToString("0.#") + " m", mL + plotW - 30, mT + plotH + 4, 10, axis);

        foreach (var c in _lastChains)
        {
            var (r, g, b) = SectionBuilder.Palette[c.MeshIndex % SectionBuilder.Palette.Length];
            var pl = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(r, g, b)), StrokeThickness = 1.6 };
            var pts = new List<Point>();
            for (int i = 0; i + 1 < c.Sz.Count; i += 2) pts.Add(new Point(SToX(c.Sz[i]), ZToY(c.Sz[i + 1])));
            pl.Points = pts;
            previewCanvas.Children.Add(pl);
        }

        _mL = mL; _mT = mT; _plotW = plotW; _plotH = plotH; _sScale = sScale;
        _zMin = zMin; _zMax = zMax; _vex = vex; _zScale = zScale;
        _plotValid = true;
    }

    // ── 厚度分析 ──────
    private void OnOverlayPointerMoved(object? sender, PointerEventArgs e)
    {
        overlayCanvas.Children.Clear();
        if (chkThickness.IsChecked != true) { readoutText.Text = "（厚度分析已关闭）"; return; }
        if (!_plotValid || _lastChains.Count == 0) { readoutText.Text = ""; return; }

        var p = e.GetPosition(overlayCanvas);
        if (p.X < _mL || p.X > _mL + _plotW || p.Y < _mT || p.Y > _mT + _plotH) { readoutText.Text = ""; return; }

        double s = Math.Clamp((p.X - _mL) / _sScale, 0, _lastTotalLen);
        double xc = _mL + s * _sScale;
        var col = SectionEngine.ColumnAt(_lastChains, s);

        var cursorBrush = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255));
        var gapBrush = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6));
        OvLine(xc, _mT, xc, _mT + _plotH, cursorBrush);
        foreach (var h in col)
        {
            var (r, g, b) = SectionBuilder.Palette[h.mi % SectionBuilder.Palette.Length];
            OvDot(xc, ZToYf(h.z), 3, new SolidColorBrush(Color.FromRgb(r, g, b)));
        }
        for (int i = 0; i + 1 < col.Count; i++)
        {
            double dz = col[i].z - col[i + 1].z;
            if (dz < 1e-6) continue;
            double ym = (ZToYf(col[i].z) + ZToYf(col[i + 1].z)) * 0.5;
            OvText($"{dz:0.##}m", xc + 5, ym - 8, 11, gapBrush);
        }
        readoutText.Text = Readout(s, col, _meshLabels);
    }

    /// <summary>底部读数行：层位标高 + 相邻厚度（自上而下）。</summary>
    public static string Readout(double s, IReadOnlyList<(double z, int mi)> col, IReadOnlyList<string> labels)
    {
        if (col.Count == 0) return $"里程 {s:0.#} m ｜ 此处无层位。";
        var sb = new StringBuilder($"里程 {s:0.#} m ｜ ");
        for (int i = 0; i < col.Count; i++)
        {
            string lab = col[i].mi >= 0 && col[i].mi < labels.Count && !string.IsNullOrWhiteSpace(labels[col[i].mi]) ? labels[col[i].mi] : $"面{col[i].mi + 1}";
            sb.Append($"{lab} {col[i].z:0.#}");
            if (i + 1 < col.Count) sb.Append($"  ↕{col[i].z - col[i + 1].z:0.##}m  ");
        }
        return sb.ToString();
    }

    private void OnOverlayPointerExited(object? sender, PointerEventArgs e)
    {
        overlayCanvas.Children.Clear();
        readoutText.Text = _lastChains.Count > 0 ? "把鼠标移到剖面上查看相邻层位间的厚度。" : "";
    }

    private double ZToYf(double z) => _mT + _plotH - (z - _zMin) * _vex * _zScale;

    // ── 展绘到场景 ───────────
    private async void OnBake(object? sender, RoutedEventArgs e)
    {
        if (_ctx == null) return;
        if (!_hasLine || _lastChains.Count == 0) { statusText.Text = "当前无有效剖面，先「生成/刷新预览」。"; return; }
        statusText.Text = "请在视口点一个点作为剖面图的插入位置（左下角基点）… Esc 用默认基点。";
        var p = await _ctx.PickPointAsync("动态剖面：点剖面图插入位置（左下角基点）；Esc 用默认基点");
        BakeAt(p?.x, p?.y);
    }

    private void BakeAt(double? baseX, double? baseY)
    {
        if (_ctx == null || _lastChains.Count == 0) return;
        var section = SectionAtOffset();
        var o = new SectionBuilder.Options
        {
            Layer3d = "剖面交线", LayerProfile = "剖面图", Vex = Math.Max(0.01, PD(txtVex.Text, 1)), SecName = "A", BaseX = baseX, BaseY = baseY,
        };
        var ents = SectionBuilder.BuildEntities(_lastChains, _lastTotalLen, section, o, null, out double bx, out double by);
        var e3 = ents.Where(en => en.LayerName == o.Layer3d).ToList();
        var ep = ents.Where(en => en.LayerName == o.LayerProfile).ToList();
        if (e3.Count > 0) _ctx.AddEntities(e3, o.Layer3d, null);
        if (ep.Count > 0) _ctx.AddEntities(ep, o.LayerProfile, null);
        statusText.Text = $"已展绘当前剖面（偏移 {offsetSlider.Value:0.#} m）：交线入「{o.Layer3d}」，剖面图入「{o.LayerProfile}」(基点 {bx:0.#}, {by:0.#})。";
        _ctx.Status($"> 动态剖面：已展绘（偏移 {offsetSlider.Value:0.#} m，交线 {_lastChains.Count} 条）");
    }

    // ── canvas 小工具 ─────────────────────────────────────────
    private void AddRect(double x, double y, double w, double h, IBrush stroke)
    {
        var r = new Rectangle { Width = w, Height = h, Stroke = stroke, StrokeThickness = 1 };
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
        previewCanvas.Children.Add(r);
    }

    private void AddText(string text, double x, double y, double size, IBrush brush)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Foreground = brush };
        Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
        previewCanvas.Children.Add(tb);
    }

    private void OvLine(double x1, double y1, double x2, double y2, IBrush b)
        => overlayCanvas.Children.Add(new Line { StartPoint = new Point(x1, y1), EndPoint = new Point(x2, y2), Stroke = b, StrokeThickness = 1 });

    private void OvDot(double x, double y, double r, IBrush b)
    {
        var el = new Ellipse { Width = r * 2, Height = r * 2, Fill = b };
        Canvas.SetLeft(el, x - r); Canvas.SetTop(el, y - r);
        overlayCanvas.Children.Add(el);
    }

    private void OvText(string text, double x, double y, double size, IBrush b)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Foreground = b, FontWeight = FontWeight.SemiBold };
        Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
        overlayCanvas.Children.Add(tb);
    }

    private static bool P(string? s, out double v) => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    private static double PD(string? s, double def) => P(s, out double v) ? v : def;
}
