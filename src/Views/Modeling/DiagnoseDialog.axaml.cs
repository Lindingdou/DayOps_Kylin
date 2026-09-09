using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 格网质量检测结果对话框（模态; 原 MeshEditLib.Dialogs.DiagnoseDialog）。
/// 拾取/选中的三角网经 <see cref="MeshDiagnose.Analyze"/> + <see cref="MeshDiagnoseMarkers.Collect"/> 诊断后,
/// 把 6 类问题逐行展示，每行可单独 [显示](场景「诊断标记」层高亮问题边/面/点) / [修复](焊接/补洞/拓扑修复后
/// <see cref="ModelingContext.ReplaceMesh"/> 并重新检测)。全中文硬编码; 所有错误经主状态栏回显, 绝不抛。
/// </summary>
public partial class DiagnoseDialog : Window
{
    private enum FixKind { None, Weld, FillHoles, Repair }

    private static readonly Color OkColor = Color.FromRgb(0x2E, 0xA0, 0x43);
    private static readonly Color WarnColor = Color.FromRgb(0xE0, 0x8A, 0x00);
    private static readonly Color ErrColor = Color.FromRgb(0xD0, 0x39, 0x2E);
    private static readonly Color InfoColor = Color.FromRgb(0x4F, 0x86, 0xC6);
    private static readonly SolidColorBrush DisabledBrush = new(Color.FromRgb(0x9A, 0x9A, 0x9A));

    private sealed record Category(string Label, int ErrorType, FixKind Fix, Func<MeshDiagnoseMarkers.Result, int> CountOf, Color Accent, string Cause);

    // 6 类（顺序即显示顺序）: 0=开放边 1=非流形 2=退化 3=自相交 4=孤立点 5=重复点
    private static readonly Category[] Categories =
    {
        new("开放边",   0, FixKind.FillHoles, r => r.OpenEdges.Count,          WarnColor, "开放面 / TIN 的天然边界、网格孔洞或删面残留；也可能是顶点未焊接，使同一条共享边被算成两条边界"),
        new("非流形边", 1, FixKind.Repair,    r => r.NonManifoldEdges.Count,   ErrColor,  "一条边被 ≥3 个面共用：T 字接合、重叠面，或合并 / 索引错位造成"),
        new("退化面",   2, FixKind.Repair,    r => r.DegenerateTris.Count,     WarnColor, "三点近共线 / 重合，面积小于容差；容差过大可能误吞细面、过小则漏判"),
        new("自相交",   3, FixKind.None,      r => r.SelfIntersectTris.Count,  ErrColor,  "几何穿模、折叠或翻面后未清理（互穿对方平面但实际不重叠的相邻面误报已修正）"),
        new("孤立顶点", 4, FixKind.Repair,    r => r.IsolatedVerts.Count,      InfoColor, "顶点不被任何面引用：导入残留或删面后未压缩顶点表"),
        new("重复顶点", 5, FixKind.Weld,      r => r.DuplicateVerts.Count,     WarnColor, "顶点位置近重合但索引不同：网格未焊接（点云 TIN 常见）或浮点精度误差"),
    };

    private readonly ModelingContext? _ctx;
    private MeshEntity? _mesh;
    private MeshDiagnoseMarkers.Result _report = new();
    private readonly List<SceneEntity> _markers = new();

    public DiagnoseDialog() { InitializeComponent(); }

    private DiagnoseDialog(ModelingContext ctx, MeshEntity mesh, MeshDiagnoseMarkers.Result report) : this()
    {
        _ctx = ctx; _mesh = mesh; _report = report;
        Closed += (_, _) => ClearMarkers();
        RefreshUi();
    }

    /// <summary>「格网质量检测」入口: 选中的三角网(无则视口点选) → 诊断 → 模态对话框。异常吞掉, 绝不崩主程序。</summary>
    public static async Task RunAsync(ModelingContext ctx)
    {
        try
        {
            // 同原版 DIAGNOSE：命令一激活就进拾取态提示点选，不拿"当前选中"顶替
            // （"先激活命令再提示选择对象"是这一组命令统一的交互规矩）。
            var e = await ctx.PickEntityAsync("格网质量检测：在视口点选要检测的三角网（Esc 取消）", en => en is MeshEntity);
            if (e is not MeshEntity mesh) { ctx.Status("格网质量检测：未拾取到三角网"); return; }
            ctx.Select(new SceneEntity[] { mesh });
            var report = await Task.Run(() => MeshDiagnoseMarkers.Collect(mesh.Verts, mesh.Tris));
            int total = TotalIssues(report);
            ctx.Status(total == 0 ? $"格网质量检测「{mesh.Name}」：未发现问题" : $"格网质量检测「{mesh.Name}」：{Summary(report)}（共 {total} 项问题）");
            var dlg = new DiagnoseDialog(ctx, mesh, report);
            await dlg.ShowDialog(ctx.Owner);
        }
        catch (Exception ex) { ctx.Status($"格网质量检测对话框异常: {ex.Message}"); }
    }

    private static int TotalIssues(MeshDiagnoseMarkers.Result r) => r.OpenEdges.Count + r.NonManifoldEdges.Count + r.DegenerateTris.Count + r.SelfIntersectTris.Count + r.IsolatedVerts.Count + r.DuplicateVerts.Count;

    private static string Summary(MeshDiagnoseMarkers.Result r)
    {
        var parts = new List<string>();
        foreach (var c in Categories) { int n = c.CountOf(r); if (n > 0) parts.Add($"{c.Label} {n}"); }
        return parts.Count == 0 ? "未发现需要处理的问题" : string.Join("    ", parts);
    }

    // ──────────────────────────────── UI 构建 ────────────────────────────────

    private void RefreshUi()
    {
        bool clean = TotalIssues(_report) == 0;
        Color sev; string icon, status;
        if (clean) { sev = OkColor; icon = "✔"; status = "未发现明显问题"; }
        else { sev = WarnColor; icon = "⚠"; status = $"疑似存在以下问题 · 共 {TotalIssues(_report)} 项"; }
        statusBanner.Background = Tint(sev, 0x22);
        statusBanner.BorderBrush = Tint(sev, 0x55);
        statusAccent.Background = Solid(sev);
        statusIcon.Text = icon; statusIcon.Foreground = Solid(sev);
        statusText.Text = status; statusText.Foreground = Solid(sev);
        summaryText.Text = Summary(_report) + (_report.SelfIntersectSkipped ? "（网格过大, 自相交未检）" : "");

        rowsHost.Children.Clear();
        foreach (var c in Categories) rowsHost.Children.Add(BuildRow(c, c.CountOf(_report)));

        causesHost.Children.Clear();
        foreach (var c in Categories) if (c.CountOf(_report) > 0) causesHost.Children.Add(BuildCauseLine(c));
        bool any = causesHost.Children.Count > 0;
        causesDivider.IsVisible = any; causesSection.IsVisible = any;
    }

    private Control BuildCauseLine(Category c)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var dot = new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 6, 8, 0), VerticalAlignment = VerticalAlignment.Top, Background = Solid(c.Accent) };
        Grid.SetColumn(dot, 0); grid.Children.Add(dot);
        var tb = new TextBlock { Text = $"{c.Label}：{c.Cause}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
        tb.Classes.Add("muted");
        Grid.SetColumn(tb, 1); grid.Children.Add(tb);
        return grid;
    }

    private Control BuildRow(Category c, int count)
    {
        bool has = count > 0;
        var grid = new Grid { Margin = new Thickness(8, 5, 8, 5), ColumnDefinitions = new ColumnDefinitions("*,64,58,58") };
        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        namePanel.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Background = has ? Solid(c.Accent) : DisabledBrush });
        var nameBlock = new TextBlock { Text = c.Label, VerticalAlignment = VerticalAlignment.Center };
        if (!has) nameBlock.Foreground = DisabledBrush;
        namePanel.Children.Add(nameBlock);
        Grid.SetColumn(namePanel, 0); grid.Children.Add(namePanel);

        Control countEl;
        if (has)
            countEl = new Border
            {
                CornerRadius = new CornerRadius(9), Background = Tint(c.Accent, 0x26), Padding = new Thickness(8, 1, 8, 1), MinWidth = 30,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = count.ToString(), FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Solid(c.Accent) },
            };
        else countEl = new TextBlock { Text = "0", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = DisabledBrush };
        Grid.SetColumn(countEl, 1); grid.Children.Add(countEl);

        var showBtn = new Button { Content = "显示", IsEnabled = has, Margin = new Thickness(0, 0, 4, 0), Padding = new Thickness(2, 3, 2, 3) };
        showBtn.Click += (_, _) => OnShowMarkers(c);
        Grid.SetColumn(showBtn, 2); grid.Children.Add(showBtn);

        var fixBtn = new Button { Content = "修复", IsEnabled = has, Padding = new Thickness(2, 3, 2, 3) };
        fixBtn.Click += (_, _) => OnFix(c);
        Grid.SetColumn(fixBtn, 3); grid.Children.Add(fixBtn);
        return grid;
    }

    // ──────────────────────────────── 行操作 ────────────────────────────────

    private void OnShowMarkers(Category c)
    {
        if (_ctx == null || _mesh == null) return;
        try
        {
            ClearMarkers();
            var b = _mesh.Bounds;
            double size = Math.Max(Math.Max(b.maxX - b.minX, b.maxY - b.minY) * 0.005, 0.1);
            var ents = MeshDiagnoseMarkers.Entities(_mesh.Verts, _mesh.Tris, _report, c.ErrorType, (c.Accent.R / 255f, c.Accent.G / 255f, c.Accent.B / 255f), size);
            if (ents.Count > 0) { _ctx.AddEntities(ents, MeshDiagnoseMarkers.MarkerLayer, null); _markers.AddRange(ents); }
            _ctx.Status($"已显示 [{c.Label}] 标记（{c.CountOf(_report)} 处, 图层「{MeshDiagnoseMarkers.MarkerLayer}」）");
        }
        catch (Exception ex) { _ctx.Status($"显示 [{c.Label}] 标记异常: {ex.Message}"); }
    }

    private void ClearMarkers()
    {
        if (_ctx == null) return;
        if (_markers.Count > 0) { try { _ctx.RemoveEntities(_markers); } catch { } _markers.Clear(); }
    }

    private void OnFix(Category c)
    {
        if (_ctx == null || _mesh == null) return;
        try
        {
            switch (c.Fix)
            {
                case FixKind.None:
                    _ctx.Status("自相交需手动删除相交面后补洞");
                    return;
                case FixKind.Weld:
                {
                    _ctx.Status($"> 修复 [{c.Label}]：顶点焊接");
                    var w = MeshWeld.Weld(_mesh.Verts, _mesh.Tris, WeldTol(_mesh), dropDuplicateTris: true);
                    int delta = w.InputVerts - w.OutputVerts;
                    Replace(w.Verts, w.Tris);
                    _ctx.Status(delta == 0 ? "✓ 无重复顶点可焊接" : $"✓ 焊接完成：顶点 {w.InputVerts} → {w.OutputVerts}（合并 {delta}）");
                    break;
                }
                case FixKind.FillHoles:
                {
                    _ctx.Status($"> 修复 [{c.Label}]：补洞");
                    var b = _mesh.Bounds;
                    double maxArea = 0.05 * (b.maxX - b.minX) * (b.maxY - b.minY);   // 按面积阈值只填小孔洞(不封外轮廓)
                    var (v, t, holes) = MeshHoleFill.Fill(_mesh.Verts, _mesh.Tris, maxArea > 0 ? maxArea : double.MaxValue);
                    if (holes > 0) Replace(v, t);
                    _ctx.Status($"✓ 补洞已执行（按面积阈值填充小孔洞：{holes} 个）");
                    break;
                }
                case FixKind.Repair:
                {
                    _ctx.Status($"> 修复 [{c.Label}]：拓扑修复");
                    var r = MeshRepair.Repair(_mesh.Verts, _mesh.Tris);
                    bool changed = r.VertsAfter != r.VertsBefore || r.FilledHoles > 0 || r.BoundaryAfter != r.BoundaryBefore || r.Tris.Count != _mesh.Tris.Count;
                    if (changed) Replace(r.Verts, r.Tris);
                    _ctx.Status(!changed ? "✓ mesh 已干净，无需修复" : $"✓ 修复完成：最终 {r.Verts.Count} 顶点 / {r.Tris.Count} 面");
                    break;
                }
            }
            ReDiagnose();
        }
        catch (Exception ex) { _ctx.Status($"修复 [{c.Label}] 异常: {ex.Message}"); }
    }

    private static double WeldTol(MeshEntity m)
    {
        var b = m.Bounds;
        double diag = Math.Sqrt((b.maxX - b.minX) * (b.maxX - b.minX) + (b.maxY - b.minY) * (b.maxY - b.minY) + (b.maxZ - b.minZ) * (b.maxZ - b.minZ));
        return diag > 0 ? diag * 1e-4 : 1e-6;
    }

    private void Replace(List<(double x, double y, double z)> v, List<(int a, int b, int c)> t)
    {
        if (_ctx == null || _mesh == null) return;
        ClearMarkers();
        var nm = new MeshEntity(_mesh.Name, v, t);
        nm.CopyStyleFrom(_mesh);
        _ctx.ReplaceMesh(_mesh, nm);
        _mesh = nm;
        _ctx.Select(new SceneEntity[] { nm });
    }

    // ──────────────────────────────── 底部按钮 ────────────────────────────────

    private void OnRepairAllClick(object? sender, RoutedEventArgs e)
    {
        if (_ctx == null || _mesh == null) return;
        try
        {
            _ctx.Status("> 全部自动修复（拓扑修复）");
            var before = MeshDiagnose.Analyze(_mesh.Verts, _mesh.Tris);
            var r = MeshRepair.Repair(_mesh.Verts, _mesh.Tris);
            int welded = r.VertsBefore - r.VertsAfter;
            bool changed = welded != 0 || r.FilledHoles > 0 || r.Tris.Count != _mesh.Tris.Count;
            if (!changed) _ctx.Status("✓ mesh 已干净，无需修复");
            else
            {
                Replace(r.Verts, r.Tris);
                var parts = new List<string>();
                if (before.DegenerateTriangles > 0) parts.Add($"去退化面: {before.DegenerateTriangles}");
                if (welded > 0) parts.Add($"焊接顶点: {welded}");
                if (r.FilledHoles > 0) parts.Add($"填充孔洞: {r.FilledHoles} 个");
                if (before.IsolatedVertices > 0) parts.Add($"删除孤立顶点: {before.IsolatedVertices}");
                _ctx.Status($"✓ 修复完成：{string.Join(" · ", parts)} · 最终 {r.Verts.Count} 顶点 / {r.Tris.Count} 面");
            }
            ReDiagnose();
        }
        catch (Exception ex) { _ctx.Status($"全部自动修复异常: {ex.Message}"); }
    }

    private void OnClearMarkersClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            ClearMarkers();
            int n = _ctx?.RemoveLayerEntities(MeshDiagnoseMarkers.MarkerLayer) ?? 0;
            _ctx?.Status($"已清理诊断标记{(n > 0 ? $"（{n} 个）" : "")}");
        }
        catch (Exception ex) { _ctx?.Status($"清理标记异常: {ex.Message}"); }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>修复后重新诊断当前三角网并刷新对话框。</summary>
    private void ReDiagnose()
    {
        if (_ctx == null || _mesh == null) return;
        try
        {
            _report = MeshDiagnoseMarkers.Collect(_mesh.Verts, _mesh.Tris);
            RefreshUi();
            int total = TotalIssues(_report);
            _ctx.Status(total == 0 ? "↻ 重新检测：未发现问题" : $"↻ 重新检测：{Summary(_report)}（共 {total} 项问题）");
        }
        catch (Exception ex) { _ctx.Status($"重新检测异常: {ex.Message}"); }
    }

    private static SolidColorBrush Solid(Color c) => new(c);
    private static SolidColorBrush Tint(Color c, byte alpha) => new(Color.FromArgb(alpha, c.R, c.G, c.B));
}
