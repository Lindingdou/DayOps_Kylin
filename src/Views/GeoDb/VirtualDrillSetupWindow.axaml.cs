using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using PitMine3D.Kylin.Data;

namespace PitMine3D.Kylin.Views.GeoDb;

/// <summary>
/// 虚拟钻孔「地质模型配置」窗: 选择三角网面识别地表 + 各煤层顶/底板, 配置层位颜色与层序(可上下调整),
/// 保存时把三角网节点 + 连接顺序存入数据库(virtual_drill_surface)。
/// 原为"在三维视图中选三角网实体"; Kylin 场景不保留三角网 → 面来源改为 ① 场景图层顶点 Delaunay 重建 ② OFF 文件(见 <see cref="MeshSourceDialog"/>)。
/// </summary>
public partial class VirtualDrillSetupWindow : Window
{
    private readonly GeoDbContext _ctx;
    private readonly ObservableCollection<SeamSetupRow> _rows = new();

    // 地表面(本次选取的几何; null=未重新选取)
    private double[]? _surfVerts;
    private int[]? _surfTris;
    private string _surfSource = "";

    /// <summary>保存/清空后触发, 供主钻孔窗刷新求交模型。</summary>
    public event Action? Saved;

    // 默认煤层色环(自上而下循环取用)
    private static readonly string[] DefaultRamp =
    { "#33333A", "#5B3A1E", "#8A2B2B", "#1F6E8C", "#2E8B57", "#B8860B", "#6A4C93", "#946A38" };

    /// <summary>XAML 编译器/设计器用(运行时一律走带 ctx 的构造)。</summary>
    public VirtualDrillSetupWindow() { _ctx = null!; InitializeComponent(); }

    public VirtualDrillSetupWindow(GeoDbContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        seamGrid.ItemsSource = _rows;
        LoadFromDb();
        UpdateStatus();
    }


    // ── 选面(场景图层 / OFF 文件) ─────────────────────────────────────────

    private async Task PickMeshAsync(string what, Action<double[], int[], int, int, string> onPicked)
    {
        var dlg = new MeshSourceDialog(_ctx, what);
        bool ok = await dlg.ShowDialog<bool>(this);
        if (!ok) { statusText.Text = "已取消选择。"; return; }
        if (dlg.Verts == null || dlg.Tris == null || dlg.Verts.Length < 9 || dlg.Tris.Length < 3)
        { statusText.Text = $"所选来源不含有效三角网面（{what}）——请选择含三角网/顶点的图层或有效 OFF 文件。"; return; }
        onPicked(dlg.Verts, dlg.Tris, dlg.Verts.Length / 3, dlg.Tris.Length / 3, dlg.SourceName);
        statusText.Text = $"已选择{what}：{dlg.SourceName} / {dlg.Verts.Length / 3} 点 / {dlg.Tris.Length / 3} 面。";
    }

    private async void OnPickSurface(object? sender, RoutedEventArgs e)
        => await PickMeshAsync("地表面", (v, t, nv, nt, src) =>
        {
            _surfVerts = v; _surfTris = t; _surfSource = src;
            surfaceInfoText.Text = $"已选地表面：{nv} 点 / {nt} 面";
        });

    private void OnClearSurface(object? sender, RoutedEventArgs e)
    {
        _surfVerts = null; _surfTris = null; _surfSource = "";
        surfaceInfoText.Text = "（未指定；可空。已入库的地表面在保存时不受影响，除非重新选择）";
    }

    private async void OnPickRoof(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SeamSetupRow row) return;
        await PickMeshAsync($"「{RowLabel(row)}」顶板面", (v, t, nv, nt, src) =>
        { row.RoofVerts = v; row.RoofTris = t; row.RoofSource = src; row.RoofInfo = $"✓ {nv}点/{nt}面"; });
    }

    private async void OnPickFloor(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SeamSetupRow row) return;
        await PickMeshAsync($"「{RowLabel(row)}」底板面", (v, t, nv, nt, src) =>
        { row.FloorVerts = v; row.FloorTris = t; row.FloorSource = src; row.FloorInfo = $"✓ {nv}点/{nt}面"; });
    }

    private static string RowLabel(SeamSetupRow r) => string.IsNullOrWhiteSpace(r.SeamName) ? "未命名煤层" : r.SeamName + "煤";

    // ── 增删 / 层序上下移 ───────────────────────────────────────────────

    private void OnAddSeam(object? sender, RoutedEventArgs e)
    {
        _rows.Add(new SeamSetupRow { SeamName = "", ColorHex = DefaultRamp[_rows.Count % DefaultRamp.Length] });
        Renumber();
        seamGrid.SelectedIndex = _rows.Count - 1;
    }

    private void OnRemoveSeam(object? sender, RoutedEventArgs e)
    {
        if (seamGrid.SelectedItem is SeamSetupRow r) { _rows.Remove(r); Renumber(); }
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e) => MoveSelected(-1);
    private void OnMoveDown(object? sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        int idx = seamGrid.SelectedIndex;
        if (idx < 0) { statusText.Text = "请先在表中选中一行，再上/下移。"; return; }
        int dst = idx + delta;
        if (dst < 0 || dst >= _rows.Count) return;
        _rows.Move(idx, dst);
        Renumber();
        seamGrid.SelectedIndex = dst;
    }

    /// <summary>层序 = 行位置(自上而下 0,1,2,…)。</summary>
    private void Renumber() { for (int i = 0; i < _rows.Count; i++) _rows[i].Order = i; }

    // ── 自动识别(按图层名 + 读图层几何) ─────────────────────────────────

    private void OnAutoDetect(object? sender, RoutedEventArgs e)
    {
        var layers = _ctx.SceneLayerNames().Where(l => _ctx.LayerVertices(l).Count >= 3).ToList();
        if (layers.Count == 0) { statusText.Text = "场景中没有可建三角网的图层——请先加载/建好地质模型面。"; return; }
        var m = GeoDbViews.VdAutoDetect(layers);

        if (!string.IsNullOrEmpty(m.SurfaceLayer) && GeoDbViews.VdMeshFromVertices(_ctx.LayerVertices(m.SurfaceLayer), out var sv, out var st))
        {
            _surfVerts = sv; _surfTris = st; _surfSource = m.SurfaceLayer;
            surfaceInfoText.Text = $"已识别地表面「{m.SurfaceLayer}」：{sv.Length / 3} 点 / {st.Length / 3} 面";
        }

        _rows.Clear();
        int order = 0;
        foreach (var s in m.Seams)
        {
            var row = new SeamSetupRow { SeamName = s.Name, ColorHex = DefaultRamp[order % DefaultRamp.Length] };
            if (GeoDbViews.VdMeshFromVertices(_ctx.LayerVertices(s.RoofLayer), out var rv, out var rt))
            { row.RoofVerts = rv; row.RoofTris = rt; row.RoofSource = s.RoofLayer; row.RoofInfo = $"✓ {rv.Length / 3}点/{rt.Length / 3}面"; }
            if (GeoDbViews.VdMeshFromVertices(_ctx.LayerVertices(s.FloorLayer), out var fv, out var ft))
            { row.FloorVerts = fv; row.FloorTris = ft; row.FloorSource = s.FloorLayer; row.FloorInfo = $"✓ {fv.Length / 3}点/{ft.Length / 3}面"; }
            _rows.Add(row);
            order++;
        }
        Renumber();
        statusText.Text = m.Seams.Count > 0
            ? $"自动识别：地表「{(string.IsNullOrEmpty(m.SurfaceLayer) ? "无" : m.SurfaceLayer)}」+ {m.Seams.Count} 层煤（已读取三角网，可再手动调整/补选后保存）。"
            : "未能自动配对煤层顶/底板（图层名不含 顶板/底板 等关键词）——请「添加煤层」后选面。";
    }

    // ── 保存 ────────────────────────────────────────────────────────────

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            int saved = 0, meta = 0, skipped = 0;
            if (_surfVerts != null && _surfTris != null)
            {
                if (GeoDbViews.VdSaveSurface(_ctx.Conn, "surface", "", 0, "#8B7355", _surfSource, _surfVerts, _surfTris) > 0) saved++;
                else skipped++;
            }
            foreach (var r in _rows)
            {
                string name = (r.SeamName ?? "").Trim();
                if (name.Length == 0) { skipped++; continue; }
                if (r.RoofVerts != null && r.RoofTris != null)
                {
                    if (GeoDbViews.VdSaveSurface(_ctx.Conn, "roof", name, r.Order, r.ColorHex, r.RoofSource, r.RoofVerts, r.RoofTris) > 0) saved++;
                    else skipped++;
                }
                if (r.FloorVerts != null && r.FloorTris != null)
                {
                    if (GeoDbViews.VdSaveSurface(_ctx.Conn, "floor", name, r.Order, r.ColorHex, r.FloorSource, r.FloorVerts, r.FloorTris) > 0) saved++;
                    else skipped++;
                }
                meta += GeoDbViews.VdUpdateSeamMeta(_ctx.Conn, name, r.Order, r.ColorHex);
            }
            string tail = skipped > 0 ? $"（跳过 {skipped} 项：无名行或几何无效）" : "";
            statusText.Text = (saved > 0 || meta > 0)
                ? $"保存完成：新写/更新 {saved} 张面几何，同步 {meta} 项层序/颜色。{tail}"
                : $"未保存：请选择地表或煤层顶/底板面后再保存。{tail}";
            if (saved > 0 || meta > 0) Saved?.Invoke();
        }
        catch (Exception ex) { statusText.Text = "保存失败：" + ex.Message; }
    }

    private async void OnClearModel(object? sender, RoutedEventArgs e)
    {
        bool yes = await BoreholeMsgBox.ConfirmAsync(this, "清空地质模型", "确定清空数据库中已保存的全部虚拟钻孔地质模型面吗？此操作不可撤销。");
        if (!yes) return;
        try
        {
            int n = GeoDbViews.VdClearAll(_ctx.Conn);
            _rows.Clear();
            _surfVerts = null; _surfTris = null; _surfSource = "";
            surfaceInfoText.Text = "（未指定；可空）";
            statusText.Text = $"已清空地质模型（删除 {n} 张面）。";
            Saved?.Invoke();
        }
        catch (Exception ex) { statusText.Text = "清空失败：" + ex.Message; }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ── 从库加载已配置 ──────────────────────────────────────────────────

    private void LoadFromDb()
    {
        List<GeoDbViews.VdSurfaceRow> all;
        try { all = GeoDbViews.VdAllSurfaces(_ctx.Conn); } catch { return; }
        if (all.Count == 0) return;
        var surf = all.FirstOrDefault(s => s.Role == "surface");
        if (surf != null) surfaceInfoText.Text = $"已入库地表面：{surf.VertexCount} 点 / {surf.TriangleCount} 面（如需更换请重新选择）";

        var bySeam = new Dictionary<string, SeamSetupRow>(StringComparer.Ordinal);
        foreach (var s in all.Where(s => s.Role == "roof" || s.Role == "floor").OrderBy(s => s.SeamOrder))
        {
            if (!bySeam.TryGetValue(s.SeamName, out var row))
            {
                bySeam[s.SeamName] = row = new SeamSetupRow { SeamName = s.SeamName, ColorHex = string.IsNullOrWhiteSpace(s.ColorHex) ? "#3C3C3C" : s.ColorHex };
                _rows.Add(row);
            }
            if (!string.IsNullOrWhiteSpace(s.ColorHex)) row.ColorHex = s.ColorHex;
            if (s.Role == "roof") row.RoofInfo = $"已入库 {s.VertexCount}点（点击重选）";
            else row.FloorInfo = $"已入库 {s.VertexCount}点（点击重选）";
        }
        Renumber();
    }

    private void UpdateStatus()
    {
        int surfaces;
        try { surfaces = GeoDbViews.VdAllSurfaces(_ctx.Conn).Count; } catch { surfaces = 0; }
        const string scene = "可从场景图层或 OFF 文件选面";
        statusText.Text = surfaces > 0
            ? $"库中已保存 {surfaces} 张地质面。{scene}。可调整层序/颜色或重选面后保存。"
            : $"库中尚无地质模型。{scene}。请选择地表 + 煤层顶/底板面后保存。";
    }
}

/// <summary>配置表一行: 煤层名 + 层序 + 颜色 + 顶/底板面(选取的几何)。</summary>
public sealed class SeamSetupRow : INotifyPropertyChanged
{
    private string _seamName = "";
    private int _order;
    private string _colorHex = "#3C3C3C";
    private string _roofInfo = "选择顶板面…";
    private string _floorInfo = "选择底板面…";

    public string SeamName { get => _seamName; set { _seamName = value ?? ""; OnChanged(); } }
    public int Order { get => _order; set { _order = value; OnChanged(); } }
    public string ColorHex { get => _colorHex; set { _colorHex = value ?? ""; OnChanged(); OnChanged(nameof(Swatch)); } }
    public string RoofInfo { get => _roofInfo; set { _roofInfo = value ?? ""; OnChanged(); } }
    public string FloorInfo { get => _floorInfo; set { _floorInfo = value ?? ""; OnChanged(); } }

    /// <summary>颜色色块(原 HexToBrushConverter): 坏值回退灰。</summary>
    public IBrush Swatch
    {
        get
        {
            var (r, g, b) = GeoDbViews.VdColorParse(_colorHex);
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }
    }

    // 选取的世界几何(不参与绑定); null = 本次未重新选取(保留库中原几何)。
    public double[]? RoofVerts; public int[]? RoofTris; public string RoofSource = "";
    public double[]? FloorVerts; public int[]? FloorTris; public string FloorSource = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>
/// 面来源选择(替代原"在三维视图中选三角网"): ① 场景图层 → 取顶点 Delaunay 重建三角网; ② OFF 文件。
/// 结果: <see cref="Verts"/>(扁平 xyz) / <see cref="Tris"/> / <see cref="SourceName"/>; ShowDialog&lt;bool&gt; true=已选。
/// </summary>
public sealed class MeshSourceDialog : Window
{
    private readonly GeoDbContext _ctx;
    private readonly ComboBox _layerBox;
    private readonly TextBlock _info;

    public double[]? Verts { get; private set; }
    public int[]? Tris { get; private set; }
    public string SourceName { get; private set; } = "";

    public MeshSourceDialog(GeoDbContext ctx, string what)
    {
        _ctx = ctx;
        Title = $"选择{what}";
        Width = 460; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Classes.Add("geodb");

        var root = new StackPanel { Margin = new Thickness(14), Spacing = 8 };
        root.Children.Add(new TextBlock { Text = $"请指定{what}的三角网来源：", FontWeight = FontWeight.Bold });

        var layerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        layerRow.Children.Add(new TextBlock { Text = "场景图层：", VerticalAlignment = VerticalAlignment.Center });
        _layerBox = new ComboBox { Width = 220, ItemsSource = ctx.SceneLayerNames().ToList() };
        layerRow.Children.Add(_layerBox);
        var useLayer = new Button { Content = "取该图层顶点建网" };
        useLayer.Click += (_, _) => UseLayer();
        layerRow.Children.Add(useLayer);
        root.Children.Add(layerRow);

        var fileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fileRow.Children.Add(new TextBlock { Text = "OFF 文件：", VerticalAlignment = VerticalAlignment.Center });
        var useOff = new Button { Content = "从 OFF 文件加载…" };
        useOff.Click += async (_, _) => await UseOffAsync();
        fileRow.Children.Add(useOff);
        root.Children.Add(fileRow);

        _info = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "（尚未选择）" };
        _info.Classes.Add("muted");
        root.Children.Add(_info);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button { Content = "确定", IsDefault = true, Padding = new Thickness(16, 3) };
        ok.Classes.Add("primary");
        ok.Click += (_, _) => { if (Verts == null) { _info.Text = "请先从图层或 OFF 文件取得三角网。"; return; } Close(true); };
        var cancel = new Button { Content = "取消", IsCancel = true, Padding = new Thickness(16, 3) };
        cancel.Click += (_, _) => Close(false);
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        root.Children.Add(buttons);
        Content = root;
    }

    private void UseLayer()
    {
        if (_layerBox.SelectedItem is not string layer) { _info.Text = "请先选择一个场景图层。"; return; }
        var pts = _ctx.LayerVertices(layer);
        if (!GeoDbViews.VdMeshFromVertices(pts, out var v, out var t)) { _info.Text = $"图层「{layer}」顶点不足 3 个（{pts.Count}），无法建三角网。"; return; }
        Verts = v; Tris = t; SourceName = layer;
        _info.Text = $"图层「{layer}」：{pts.Count} 顶点 → 三角网 {v.Length / 3} 点 / {t.Length / 3} 面。";
    }

    private async Task UseOffAsync()
    {
        var path = await _ctx.OpenFileAsync("选择 OFF 三角网文件", new[] { "*.off" });
        if (path == null) return;
        try
        {
            if (!GeoDbViews.VdMeshFromOff(File.ReadAllText(path), out var v, out var t)) { _info.Text = "OFF 文件不含有效三角网。"; return; }
            Verts = v; Tris = t; SourceName = Path.GetFileName(path);
            _info.Text = $"OFF「{SourceName}」：{v.Length / 3} 点 / {t.Length / 3} 面。";
        }
        catch (Exception ex) { _info.Text = "读取失败：" + ex.Message; }
    }
}
