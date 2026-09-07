using System;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 虚拟钻孔主窗: 拾取/输入一个平面位置 → 从数据库中已保存的地质模型面(地表 + 煤层顶底板三角网)
/// 竖直求交 → 表格 + 汇总(累计煤厚/岩厚/剥采比) + 2D 柱状预览; 「生成三维柱」把结果以分色柱状图导入主场景。
/// </summary>
public partial class VirtualDrillWindow : Window
{
    private readonly GeoDbContext _ctx;
    private readonly ObservableCollection<DisplayRow> _rows = new();
    private GeoDbViews.VdModel? _model;
    private GeoDbViews.VdResult? _last;
    private bool _picking;

    /// <summary>XAML 编译器/设计器用(运行时一律走带 ctx 的构造)。</summary>
    public VirtualDrillWindow() { _ctx = null!; InitializeComponent(); }

    public VirtualDrillWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        grid.ItemsSource = _rows;
        gen3dBtn.IsEnabled = false;
        colCanvas.SizeChanged += (_, _) => DrawColumn(_last);
        RefreshModelStatus();
    }


    // ── 求交模型(从库构建, 配置变更后重建) ──────────────────────────────

    private GeoDbViews.VdModel EnsureModel() => _model ??= GeoDbViews.VdBuildModel(_ctx.Conn);

    private void RefreshModelStatus()
    {
        bool has;
        try { has = GeoDbViews.VdHasAnySurface(_ctx.Conn); } catch { has = false; }
        if (!has)
        {
            summaryText.Text = "尚未配置地质模型——请点「配置地质模型」，指定地表与各煤层顶/底板三角网并保存到数据库。";
            statusText.Text = "首次使用：先配置地质模型。";
            return;
        }
        var model = EnsureModel();
        string surf = model.HasSurface ? "含地表面" : "无地表面";
        summaryText.Text = $"地质模型已就绪（{surf}，{model.SeamCount} 层煤）。拾取孔位即自动计算，或输入 X/Y 后点「手动计算」。";
        statusText.Text = "就绪。";
    }

    // ── 拾取 / 求交 ─────────────────────────────────────────────────────

    private async void OnPick(object? sender, RoutedEventArgs e)
    {
        if (!EnsureReady()) return;
        if (_picking) return;
        _picking = true;
        statusText.Text = "请在视口中点击一个位置（Esc 取消）…";
        try
        {
            var p = await _ctx.PickPointAsync("虚拟钻孔：在视口中点击孔位（Esc 取消）");
            _picking = false;
            if (p == null) { statusText.Text = "已取消拾取。"; return; }
            txtX.Text = p.Value.x.ToString("0.###", CultureInfo.InvariantCulture);
            txtY.Text = p.Value.y.ToString("0.###", CultureInfo.InvariantCulture);
            Activate();
            RunDrill(p.Value.x, p.Value.y);
        }
        catch (Exception ex) { _picking = false; statusText.Text = "拾取失败：" + ex.Message; }
    }

    private void OnDrillManual(object? sender, RoutedEventArgs e)
    {
        if (!EnsureReady()) return;
        if (!TryParse(txtX.Text, out double x) || !TryParse(txtY.Text, out double y))
        { statusText.Text = "请填写有效的 X / Y 坐标。"; return; }
        RunDrill(x, y);
    }

    /// <summary>确认地质模型已配置且可求交。</summary>
    private bool EnsureReady()
    {
        bool has;
        try { has = GeoDbViews.VdHasAnySurface(_ctx.Conn); } catch { has = false; }
        if (!has) { statusText.Text = "尚未配置地质模型——请先点「配置地质模型」。"; return false; }
        if (EnsureModel().SamplerCount == 0) { statusText.Text = "地质模型几何为空（保存的面无有效三角网）——请重新「配置地质模型」。"; return false; }
        return true;
    }

    private void RunDrill(double x, double y)
    {
        var result = GeoDbViews.VdDrill(EnsureModel(), x, y);
        _last = result;
        PopulateGrid(result);
        PopulateSummary(result);
        DrawColumn(result);
        gen3dBtn.IsEnabled = result.SeamsPresent > 0;
        statusText.Text = result.SeamsPresent > 0
            ? $"计算完成：({x:0.##}, {y:0.##}) 见煤 {result.SeamsPresent} 层。"
            : $"计算完成：({x:0.##}, {y:0.##}) 此处未见煤（无煤层面覆盖或点在模型外）。";
    }

    // ── 生成三维柱 ─────────────────────────────────────────────────────

    private void OnGenerate3D(object? sender, RoutedEventArgs e)
    {
        if (_last == null || _last.SeamsPresent == 0) { statusText.Text = "请先计算出至少一层煤，再生成三维柱。"; return; }
        try
        {
            var ents = GeoDbViews.VdBuildColumnEntities(_last, out var bounds);
            if (ents.Count == 0) { statusText.Text = "生成失败（载荷为空）。"; return; }
            _ctx.AddToScene(ents, GeoDbViews.VdLayerName, bounds);
            statusText.Text = $"已在「{GeoDbViews.VdLayerName}」图层生成三维钻孔柱（({_last.X:0.##}, {_last.Y:0.##})，{_last.SeamsPresent} 层煤）。";
            _ctx.Status(statusText.Text);
        }
        catch (Exception ex) { statusText.Text = "生成三维柱异常：" + ex.Message; }
    }

    // ── 配置地质模型 ───────────────────────────────────────────────────

    private void OnConfig(object? sender, RoutedEventArgs e)
    {
        var win = new VirtualDrillSetupWindow(_ctx);
        win.Saved += () =>
        {
            _model = null;
            RefreshModelStatus();
            if (_last != null) RunDrill(_last.X, _last.Y);
        };
        win.Show(this);
    }

    // ── 结果 → UI ───────────────────────────────────────────────────────

    private void PopulateGrid(GeoDbViews.VdResult r)
    {
        _rows.Clear();
        foreach (var s in r.Seams)
        {
            _rows.Add(new DisplayRow
            {
                Swatch = new SolidColorBrush(Color.FromRgb(s.R, s.G, s.B)),
                Name = s.Name + "煤",
                RoofZ = s.Present ? s.RoofZ.ToString("0.##") : "——",
                FloorZ = s.Present ? s.FloorZ.ToString("0.##") : "——",
                Thickness = s.Present ? s.Thickness.ToString("0.##") : "——",
                Interval = s.Interval.HasValue ? s.Interval.Value.ToString("0.##") : "",
                State = s.Present ? "见煤" : "缺失/尖灭",
            });
        }
    }

    private void PopulateSummary(GeoDbViews.VdResult r)
    {
        string collar = r.SurfaceZ.HasValue ? r.SurfaceZ.Value.ToString("0.##") + " m" : "（无地表面）";
        string strip = r.StripRatio.HasValue ? r.StripRatio.Value.ToString("0.##") : "—";
        summaryText.Text =
            $"孔位：X={r.X:0.###}  Y={r.Y:0.###}   孔口高程：{collar}\n" +
            $"见煤层数：{r.SeamsPresent}    累计煤厚：{r.TotalCoal:0.##} m    累计岩厚：{r.TotalRock:0.##} m    钻孔剥采比（岩:煤）：{strip}";
    }

    // ── 2D 柱状预览 ─────────────────────────────────────────────────────

    private void DrawColumn(GeoDbViews.VdResult? r)
    {
        colCanvas.Children.Clear();
        if (r == null || r.ColumnTopZ == null || r.ColumnBottomZ == null) return;
        double cw = colCanvas.Bounds.Width, ch = colCanvas.Bounds.Height;
        if (cw < 20 || ch < 40) return;
        double top = r.ColumnTopZ.Value, bottom = r.ColumnBottomZ.Value, zr = top - bottom;
        if (zr <= 1e-6) return;

        const double topM = 22, botM = 18, leftM = 14, colW = 52;
        double scale = (ch - topM - botM) / zr;
        double ZToY(double z) => topM + (top - z) * scale;

        var rockLabelBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0xB8, 0xA0));
        foreach (var layer in GeoDbViews.VdBuildColumnLayers(r))
        {
            double zb = layer.BottomZ, zt = layer.TopZ;
            if (zt <= zb) continue;
            AddRect(leftM, ZToY(zt), colW, (zt - zb) * scale, new SolidColorBrush(Color.FromRgb(layer.R, layer.G, layer.B)));
            if (layer.IsCoal) AddLabel($"{layer.Name}煤 {layer.Thickness:0.##}m", leftM + colW + 4, ZToY((zb + zt) * 0.5) - 8, 10);
            else if (layer.Thickness >= 0.5) AddLabel($"{layer.Name} {layer.Thickness:0.##}m", leftM + colW + 4, ZToY((zb + zt) * 0.5) - 8, 9, rockLabelBrush);
        }
        AddRect(leftM, ZToY(top), colW, (top - bottom) * scale, null, Brushes.Gray);
        AddLabel("地表 " + (r.SurfaceZ?.ToString("0.#") ?? top.ToString("0.#")), leftM, ZToY(top) - 16, 10, Brushes.Goldenrod);
        AddLabel("底 " + bottom.ToString("0.#"), leftM, ZToY(bottom) + 2, 9, Brushes.Gray);
    }

    private void AddRect(double x, double y, double w, double h, IBrush? fill, IBrush? stroke = null)
    {
        if (h < 0) h = 0;
        var rect = new Rectangle { Width = w, Height = h, Fill = fill };
        if (stroke != null) { rect.Stroke = stroke; rect.StrokeThickness = 1; }
        Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y);
        colCanvas.Children.Add(rect);
    }

    private void AddLabel(string text, double x, double y, double size, IBrush? brush = null)
    {
        var tb = new TextBlock { Text = text, FontSize = size, Foreground = brush ?? Brushes.White };
        Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y);
        colCanvas.Children.Add(tb);
    }

    private static bool TryParse(string? s, out double v)
        => double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>结果表一行(显示用)。</summary>
    public sealed class DisplayRow
    {
        public IBrush? Swatch { get; set; }
        public string Name { get; set; } = "";
        public string RoofZ { get; set; } = "";
        public string FloorZ { get; set; } = "";
        public string Thickness { get; set; } = "";
        public string Interval { get; set; } = "";
        public string State { get; set; } = "";
    }
}
