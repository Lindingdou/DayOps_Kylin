using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>
/// 约束块体对话框（忠实原 ConstrainBlockModelDialog）：多 AABB 组合（AND/OR + 保留/删除）粗裁 +
/// Mesh 几何精确约束（保留 mesh 之上/之下/内部/外部；上下靠面高程采样，内外靠广义缠绕数）。
/// 「撤销本次」只回退本窗新增的删除。非模态（原为模态，Kylin 改非模态以便视口选 mesh）。
/// </summary>
public partial class ConstrainBlockModelWindow : Window
{
    public sealed class AabbRow : INotifyPropertyChanged
    {
        private bool _enabled = true;
        public bool Enabled { get => _enabled; set { if (_enabled != value) { _enabled = value; N(nameof(Enabled)); } } }
        public string Label { get; set; } = "AABB";
        public string MinXText { get; set; } = "0";
        public string MinYText { get; set; } = "0";
        public string MinZText { get; set; } = "0";
        public string MaxXText { get; set; } = "0";
        public string MaxYText { get; set; } = "0";
        public string MaxZText { get; set; } = "0";
        public event PropertyChangedEventHandler? PropertyChanged;
        private void N(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

        public (double minX, double minY, double minZ, double maxX, double maxY, double maxZ)? TryParse()
        {
            if (!P(MinXText, out var a) || !P(MinYText, out var b) || !P(MinZText, out var c) || !P(MaxXText, out var d) || !P(MaxYText, out var e) || !P(MaxZText, out var f)) return null;
            if (a > d) (a, d) = (d, a); if (b > e) (b, e) = (e, b); if (c > f) (c, f) = (f, c);
            return (a, b, c, d, e, f);
        }
        private static bool P(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    private readonly ModelingContext _ctx;
    private readonly ObservableCollection<AabbRow> _rows = new();
    private readonly BlockDeleteRecord _session = new();
    private MeshEntity? _mesh;
    private double[]? _meshVerts; private int[]? _meshTris;
    private WindingNumberTester? _gwn;

    public ConstrainBlockModelWindow() { _ctx = null!; InitializeComponent(); }

    public ConstrainBlockModelWindow(ModelingContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        BlockModelStore.Adopt(ctx);
        aabbGrid.ItemsSource = _rows;
        targetCombo.ItemsSource = BlockModelStore.Models;
        var def = BlockModelStore.PickDefault();
        if (def != null) targetCombo.SelectedItem = def;
    }

    private BlockModelMeta? Target => targetCombo.SelectedItem as BlockModelMeta;
    private static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

    private void OnTargetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Target is not { } m) return;
        _rows.Clear();
        _rows.Add(BuildFullRow(m, "整模型"));
        statusLabel.Text = $"目标 {m.Name} 当前已删 {m.DeletedBlockCount:N0} / {m.RealBlockCount:N0}";
    }

    private static AabbRow BuildFullRow(BlockModelMeta m, string label)
    {
        var b = m.Bounds;
        return new AabbRow { Label = label, MinXText = F(b.minX), MinYText = F(b.minY), MinZText = F(b.minZ), MaxXText = F(b.maxX), MaxYText = F(b.maxY), MaxZText = F(b.maxZ) };
    }

    private void OnAddAabb(object? sender, RoutedEventArgs e)
    {
        if (Target is not { } m) { _rows.Add(new AabbRow()); return; }
        var b = m.Bounds;
        double cx = (b.minX + b.maxX) / 2, cy = (b.minY + b.maxY) / 2, cz = (b.minZ + b.maxZ) / 2;
        double sx = b.maxX - b.minX, sy = b.maxY - b.minY, sz = b.maxZ - b.minZ;
        _rows.Add(new AabbRow
        {
            Label = $"AABB_{_rows.Count + 1}",
            MinXText = F(cx - sx * 0.25), MinYText = F(cy - sy * 0.25), MinZText = F(cz - sz * 0.25),
            MaxXText = F(cx + sx * 0.25), MaxYText = F(cy + sy * 0.25), MaxZText = F(cz + sz * 0.25),
        });
    }

    private void OnAddFull(object? sender, RoutedEventArgs e) { if (Target is { } m) _rows.Add(BuildFullRow(m, $"整模型_{_rows.Count + 1}")); }

    private async void OnAddFromSelection(object? sender, RoutedEventArgs e)
    {
        try
        {
            int added = 0, skipped = 0;
            foreach (var me in _ctx.SelectedMeshes())
            {
                var b = me.Bounds;
                _rows.Add(new AabbRow { Label = $"mesh_{me.Name}", MinXText = F(b.minX), MinYText = F(b.minY), MinZText = F(b.minZ), MaxXText = F(b.maxX), MaxYText = F(b.maxY), MaxZText = F(b.maxZ) });
                added++;
            }
            foreach (var pl in _ctx.SelectedPolylines())
            {
                if (pl.Points.Count == 0) { skipped++; continue; }
                double minX = pl.Points.Min(p => p.x), maxX = pl.Points.Max(p => p.x), minY = pl.Points.Min(p => p.y), maxY = pl.Points.Max(p => p.y);
                double minZ = pl.Elevation, maxZ = pl.Elevation;
                if (pl.Has3D) for (int i = 0; i < pl.Points.Count; i++) { double z = pl.ZAt(i); if (z < minZ) minZ = z; if (z > maxZ) maxZ = z; }
                _rows.Add(new AabbRow { Label = "polyline", MinXText = F(minX), MinYText = F(minY), MinZText = F(minZ), MaxXText = F(maxX), MaxYText = F(maxY), MaxZText = F(maxZ) });
                added++;
            }
            var pts = _ctx.SelectedPoints();
            if (pts.Count > 0)
            {
                _rows.Add(new AabbRow { Label = "points", MinXText = F(pts.Min(p => p.X)), MinYText = F(pts.Min(p => p.Y)), MinZText = F(pts.Min(p => p.Elevation)), MaxXText = F(pts.Max(p => p.X)), MaxYText = F(pts.Max(p => p.Y)), MaxZText = F(pts.Max(p => p.Elevation)) });
                added++;
            }
            if (added == 0)
            {
                await BlockMsgBox.WarnAsync(this, "从选中实体导入", "当前视口没有选中任何实体。\n请先在视口中点选三角网 / 多段线 / 点等实体，再回来点本按钮。");
                return;
            }
            string msg = $"已加 {added} 个实体 AABB 到列表";
            if (skipped > 0) msg += $"（{skipped} 个无效或无 bbox，已跳过）";
            statusLabel.Text = msg;
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "从选中实体导入", ex.Message); }
    }

    private void OnRemoveRow(object? sender, RoutedEventArgs e)
    {
        if (aabbGrid.SelectedItem is AabbRow r) _rows.Remove(r);
        else if (_rows.Count > 0) _rows.RemoveAt(_rows.Count - 1);
    }
    private void OnClearRows(object? sender, RoutedEventArgs e) => _rows.Clear();

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Target is not { } m) { await BlockMsgBox.WarnAsync(this, "约束块体", "请选择目标块体。"); return; }
            var aabbs = new List<(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)>();
            int rowNum = 0;
            foreach (var r in _rows)
            {
                rowNum++;
                if (!r.Enabled) continue;
                var box = r.TryParse();
                if (box == null) { await BlockMsgBox.WarnAsync(this, "约束块体", $"第 {rowNum} 行 AABB 输入有非法数字。"); return; }
                aabbs.Add(box.Value);
            }
            if (aabbs.Count == 0) { await BlockMsgBox.WarnAsync(this, "约束块体", "至少需要一个启用的 AABB 行。"); return; }
            bool combineOr = combineOr_IsChecked(), keep = actionKeep.IsChecked == true;
            int affected = m.ApplyKeep((_, b) =>
            {
                bool match = combineOr ? MatchAny(b, aabbs) : MatchAll(b, aabbs);
                return keep ? match : !match;
            }, _session);
            BlockModelStore.RefreshDisplay(_ctx, m);

            string zeroNote = "";
            if (affected == 0)
            {
                zeroNote = "\n\n⚠ 本次一个 cell 都没删。";
                if (keep) zeroNote += "\n「保留满足条件」+ 覆盖整个模型的 AABB，按定义就删不掉任何东西。";
                zeroNote += "\n若你要按【地表 mesh 之上/之下】切块体，请点下面「Mesh 几何精确约束」卡片里的『应用 Mesh 约束』——右下角这个「应用」只处理上面的 AABB 列表。";
            }
            string body = $"模型 {m.Name}\n  组合方式:   {(combineOr ? "OR (任一)" : "AND (全部)")}\n  AABB 数:    {aabbs.Count}\n  处理:       {(keep ? "保留满足，删其他" : "删除满足")}\n  本次新增删除: {affected:N0} cell\n  累计已删:   {m.DeletedBlockCount:N0} / {m.RealBlockCount:N0}" + zeroNote;
            if (affected == 0) await BlockMsgBox.WarnAsync(this, "约束未删除任何 cell", body); else await BlockMsgBox.InfoAsync(this, "约束已应用", body);
            statusLabel.Text = $"上次新增删除 {affected:N0}；累计 {m.DeletedBlockCount:N0} / {m.RealBlockCount:N0}";
            await WarnIfModelEmptied(m, "约束块体");
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "约束块体", ex.Message); }
    }

    private bool combineOr_IsChecked() => combineOr.IsChecked == true;

    private static bool In(BlockModel.Block b, (double minX, double minY, double minZ, double maxX, double maxY, double maxZ) a)
        => b.X >= a.minX && b.X <= a.maxX && b.Y >= a.minY && b.Y <= a.maxY && b.Z >= a.minZ && b.Z <= a.maxZ;
    private static bool MatchAny(BlockModel.Block b, List<(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)> aabbs) { foreach (var a in aabbs) if (In(b, a)) return true; return false; }
    private static bool MatchAll(BlockModel.Block b, List<(double minX, double minY, double minZ, double maxX, double maxY, double maxZ)> aabbs) { foreach (var a in aabbs) if (!In(b, a)) return false; return true; }

    private async System.Threading.Tasks.Task WarnIfModelEmptied(BlockModelMeta m, string opName)
    {
        long occ = m.RealBlockCount, remaining = Math.Max(0, m.LiveBlockCount());
        if (occ <= 0 || remaining > occ / 1000) return;
        await BlockMsgBox.WarnAsync(this, opName + "：模型几乎被删空",
            $"应用后仅剩 {remaining:N0} / {occ:N0} 个可见块。\n\n常见原因：\n① 约束模式选反——地下地质块体用「mesh 之上(地表切矿体)」会把地表以下的块几乎删光，通常应选「mesh 之下」；\n② 参考网格与块体不在同一坐标系（块体用绝对大地坐标，注意核对 mesh 的 AABB 与块体范围是否重叠）。\n\n点「撤销本次」即可恢复后重试。");
    }

    private async void OnRestore(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Target is not { } m) { await BlockMsgBox.WarnAsync(this, "约束块体", "请选择目标块体。"); return; }
            int restored = m.Restore(_session);
            BlockModelStore.RefreshDisplay(_ctx, m);
            statusLabel.Text = $"已撤销本次约束 {restored:N0}；累计仍删 {m.DeletedBlockCount:N0} / {m.RealBlockCount:N0}";
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "恢复", ex.Message); }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    // ── Mesh 几何精确约束 ──

    private async void OnPickMesh(object? sender, RoutedEventArgs e)
    {
        try
        {
            var sel = _ctx.SelectedMeshes();
            MeshEntity? me = sel.Count > 0 ? sel[0] : null;
            if (me == null)
            {
                var ent = await _ctx.PickEntityAsync("拾取参考三角网（Esc 取消）", x => x is MeshEntity);
                me = ent as MeshEntity;
            }
            if (me == null) { await BlockMsgBox.WarnAsync(this, "拾取 mesh", "当前视口未选中实体。请先在视口选 1 个三角网，再点本按钮。"); return; }
            var mt = MeshOrient.MakeConsistent(me.Verts, me.Tris);
            _mesh = me;
            _meshVerts = new double[me.Verts.Count * 3];
            for (int i = 0; i < me.Verts.Count; i++) { _meshVerts[i * 3] = me.Verts[i].x; _meshVerts[i * 3 + 1] = me.Verts[i].y; _meshVerts[i * 3 + 2] = me.Verts[i].z; }
            _meshTris = new int[mt.Count * 3];
            for (int i = 0; i < mt.Count; i++) { _meshTris[i * 3] = mt[i].a; _meshTris[i * 3 + 1] = mt[i].b; _meshTris[i * 3 + 2] = mt[i].c; }
            _gwn = null;
            var b = me.Bounds;
            meshInfoLabel.Text = $"{me.Name}  顶点 {me.Verts.Count:N0}  三角 {me.Tris.Count:N0}  AABB X[{b.minX:0.#},{b.maxX:0.#}] Y[{b.minY:0.#},{b.maxY:0.#}] Z[{b.minZ:0.#},{b.maxZ:0.#}]";
            meshInfoLabel.Foreground = Avalonia.Media.Brushes.Black;
            meshStatusLabel.Text = "✓ mesh 已就绪，选模式后点应用";
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "拾取 mesh", ex.Message); }
    }

    private async void OnApplyMesh(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Target is not { } m) { await BlockMsgBox.WarnAsync(this, "Mesh 约束", "请选择目标块体。"); return; }
            if (_mesh == null || _meshVerts == null || _meshTris == null) { await BlockMsgBox.WarnAsync(this, "Mesh 约束", "请先点「拾取选中 mesh」选取一个参考三角网。"); return; }
            var mode = meshKeepAbove.IsChecked == true ? MeshConstraintMode.KeepAboveSurface : meshKeepBelow.IsChecked == true ? MeshConstraintMode.KeepBelowSurface
                : meshKeepInside.IsChecked == true ? MeshConstraintMode.KeepInsideClosed : MeshConstraintMode.KeepOutsideClosed;
            bool closedMode = mode is MeshConstraintMode.KeepInsideClosed or MeshConstraintMode.KeepOutsideClosed;
            bool keepOutsideProj = meshKeepOutsideProj.IsChecked == true;
            var verts = _meshVerts; var tris = _meshTris;
            WindingNumberTester? wn = null;
            if (closedMode) { _gwn ??= new WindingNumberTester(verts, tris); wn = _gwn; }
            var mb = _mesh.Bounds;
            int affected = await System.Threading.Tasks.Task.Run(() => m.ApplyKeep((_, b) =>
            {
                switch (mode)
                {
                    case MeshConstraintMode.KeepAboveSurface:
                    {
                        var s = MineableAreaIdentifier.SampleMeshZ(verts, tris, b.X, b.Y);
                        return s.HasValue ? b.Z >= s.Value : keepOutsideProj;
                    }
                    case MeshConstraintMode.KeepBelowSurface:
                    {
                        var s = MineableAreaIdentifier.SampleMeshZ(verts, tris, b.X, b.Y);
                        return s.HasValue ? s.Value > b.Z : keepOutsideProj;
                    }
                    case MeshConstraintMode.KeepInsideClosed: return wn!.IsInsideClosed(b.X, b.Y, b.Z);
                    default: return !wn!.IsInsideClosed(b.X, b.Y, b.Z);
                }
            }, _session));
            BlockModelStore.RefreshDisplay(_ctx, m);

            string testerNote = closedMode ? "缠绕数(高容错)" : "面高程采样";
            string projNote = closedMode ? "" : $"\n  地表未覆盖区: {(keepOutsideProj ? "保留" : "删除")}{CoverageNote(m, mb, keepOutsideProj)}";
            string diag = affected == 0 && !closedMode ? DiagnoseSurfaceNoop(m, verts, tris, mb, keepOutsideProj) : "";
            string body = $"模型 {m.Name}\n  参考 mesh: {_mesh.Name}, {_mesh.Tris.Count:N0} 三角\n  模式:     {ModeLabel(mode)}（{testerNote}）{projNote}\n  本次新增删除: {affected:N0} cell\n  累计已删:   {m.DeletedBlockCount:N0} / {m.RealBlockCount:N0}" + diag;
            if (affected == 0) await BlockMsgBox.WarnAsync(this, "Mesh 约束未删除任何 cell", body); else await BlockMsgBox.InfoAsync(this, "Mesh 约束已应用", body);
            meshStatusLabel.Text = $"上次新增删除 {affected:N0}；累计 {m.DeletedBlockCount:N0} / {m.RealBlockCount:N0}";
            await WarnIfModelEmptied(m, "Mesh 约束");
        }
        catch (Exception ex) { await BlockMsgBox.WarnAsync(this, "Mesh 约束", ex.Message); }
    }

    private static string CoverageNote(BlockModelMeta m, (double minX, double minY, double maxX, double maxY, double minZ, double maxZ) mb, bool keepOutsideProj)
    {
        var b = m.Bounds;
        double modelArea = Math.Max(0, b.maxX - b.minX) * Math.Max(0, b.maxY - b.minY);
        if (modelArea <= 0) return "";
        double ix = Math.Max(0, Math.Min(b.maxX, mb.maxX) - Math.Max(b.minX, mb.minX));
        double iy = Math.Max(0, Math.Min(b.maxY, mb.maxY) - Math.Max(b.minY, mb.minY));
        double cover = ix * iy / modelArea;
        if (cover >= 0.999) return "";
        string note = $"\n  ⚠ mesh 的 XY 范围只盖住模型 {cover * 100:0.#}%";
        if (keepOutsideProj) note += "，其余部分按「保留」整块留下（不细分不删除）。\n    要连未覆盖区一起切掉，取消勾选「地表未覆盖区：保留」后重跑。";
        return note;
    }

    private static string DiagnoseSurfaceNoop(BlockModelMeta m, double[] verts, int[] tris, (double minX, double minY, double maxX, double maxY, double minZ, double maxZ) mb, bool keepOutsideProj)
    {
        const int MaxSamples = 200_000;
        long outProj = 0, above = 0, below = 0, sampled = 0;
        int stride = Math.Max(1, m.Blocks.Count / MaxSamples);
        for (int i = 0; i < m.Blocks.Count; i += stride)
        {
            if (m.DeletedIds.Contains(i)) continue;
            var b = m.Blocks[i]; sampled++;
            var s = MineableAreaIdentifier.SampleMeshZ(verts, tris, b.X, b.Y);
            if (!s.HasValue) { outProj++; continue; }
            if (s.Value > b.Z) below++; else above++;
        }
        if (sampled == 0) return "\n\n诊断：当前没有可见块可抽样——模型多半已被之前的操作全部删除，先点「撤销本次」或「恢复全部」。";
        double P(long v) => v * 100.0 / sampled;
        var mbb = m.Bounds;
        var sb = new System.Text.StringBuilder();
        sb.Append($"\n\n诊断（抽样 {sampled:N0} 个可见块中心，用的就是本次那套判据）：\n");
        sb.Append($"  投影外（mesh 的 XY 没盖到）: {outProj:N0}  ({P(outProj):0.#}%)\n");
        sb.Append($"  面之上:                      {above:N0}  ({P(above):0.#}%)\n");
        sb.Append($"  面之下:                      {below:N0}  ({P(below):0.#}%)\n");
        sb.Append($"  mesh  Z ∈ [{mb.minZ:0.##}, {mb.maxZ:0.##}]   模型 Z ∈ [{mbb.minZ:0.##}, {mbb.maxZ:0.##}]\n");
        if (outProj > sampled / 2 && keepOutsideProj) sb.Append("→ 大半的块落在 mesh 的 XY 投影【之外】，被「地表未覆盖区：保留」整块留下了。取消那个勾选再跑，或换一张盖得住整个模型的面。");
        else if (above == 0) sb.Append("→ 没有任何可见块落在这张面【之上】——对一下上面两行 Z 区间：要么拾取的不是压在块体上方的那张地表，要么模型本来就整个在面之下（那就确实没得删）。");
        else sb.Append($"→ 判据本身认得出{above:N0} 个面之上的块，却一个都没删；若刚点过「面之上」且没撤销，先点「撤销本次」再重跑。");
        return sb.ToString();
    }

    private static string ModeLabel(MeshConstraintMode m) => m switch
    {
        MeshConstraintMode.KeepAboveSurface => "保留 mesh 之上",
        MeshConstraintMode.KeepBelowSurface => "保留 mesh 之下",
        MeshConstraintMode.KeepInsideClosed => "保留 mesh 内部",
        MeshConstraintMode.KeepOutsideClosed => "保留 mesh 外部",
        _ => m.ToString(),
    };
}
