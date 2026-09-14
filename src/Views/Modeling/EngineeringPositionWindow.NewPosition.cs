using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using PitMine3D.Kylin.Cad;

namespace PitMine3D.Kylin.Views.Modeling;

/// <summary>⑥⑦ 非破坏产物：往新图层写多段线 / 三角网 / 点标记，原图一根不动。</summary>
internal sealed class EpNewPositionRequest
{
    public List<(string Layer, List<(double X, double Y, double Z)> Pts, bool Closed, (float r, float g, float b) Color)> Polylines = new();
    public List<(string Layer, string Name, double[] Verts, int[] Tris, (float r, float g, float b) Color)> Meshes = new();
    public List<(string Layer, double X, double Y, double Z)> Marks = new();
}

/// <summary>
/// 「创建工程位置」⑥⑦（忠实移植原窗口）：
/// ⑥ 生成【新建工程位置】—— **非破坏**：原图一根不动，产物全写进「新建工程位置_*」三层。
///    采场档 = ⑤执行替换之后图上会剩下的那一套老线 + 模板台阶 + 衔接段（判决与 ④⑤ 同一份 <see cref="ComputePlan"/>）；
///    排土档 = 排土台阶线当模板、其余线先被【排土场坡面】沿交线裁一刀（<see cref="LineAboveMeshClipper"/>）再当端帮，裁剩的旧线 + 排土台阶线 + 衔接段。
/// ⑦ 配面 → 生成台阶面：在图上点线成对（第一条坡顶、第二条坡底），各自沿衔接链拼成整条链后放样成三角带，
///    与视口里选中的已有坡面合并成**一个**面模型，写进「新建工程位置_坡面」。
///
/// ── Kylin 侧登记的差异 ──
/// <list type="bullet">
///   <item>排土场坡面下拉：原版 <c>MeshPickList</c> 按层名猜「排土场_坡面」；Kylin 列图上全部三角网（层名/名字含"排土场"或"坡面"的预选），一样不替人猜。</item>
///   <item>三角网写入原版经 PmbiWriter（局部坐标 + 基点）；Kylin 直接 <c>MeshEntity</c>。</item>
/// </list>
/// </summary>
internal sealed partial class EngineeringPositionWindow
{
    private readonly CheckBox _chkDumpMode = new() { Content = "排土场模式", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ComboBox _cmbDumpFace = new() { Width = 200, IsVisible = false, Margin = new Thickness(0, 0, 6, 0), PlaceholderText = "选排土场坡面" };
    private readonly ToggleButton _btnFaceMode = new() { Content = "⑦ 配面", Padding = new Thickness(9, 3), Margin = new Thickness(0, 0, 6, 0) };
    private Button? _btnPreviewReplace, _btnApplyReplace;

    private PitMine3D.Kylin.Cad.SeamOutcrop.MeshZSampler? _dumpFace;
    private readonly List<EpPolyline> _dumpKept = new();
    private readonly List<(ulong Crest, ulong Toe)> _facePairs = new();
    private ulong _pendingCrest;
    private readonly List<ulong> _mergeMeshes = new();

    private bool DumpMode => _chkDumpMode.IsChecked == true;
    private bool FaceMode => _btnFaceMode.IsChecked == true;

    private static readonly Color CFaceCrest = Color.FromRgb(0xFF, 0x9E, 0x2C);
    private static readonly Color CFaceToe   = Color.FromRgb(0x3F, 0xD0, 0xD8);

    private sealed class MeshItem
    {
        public ulong Handle; public string Name = ""; public string Layer = ""; public int Tris;
        public override string ToString() => $"{Name}（{Layer} · {Tris} 面）";
    }

    /// <summary>①行末尾：排土场模式 + 坡面下拉 + ⑥。</summary>
    private void AddNewPositionControls(WrapPanel row1, WrapPanel row2)
    {
        _chkDumpMode.IsCheckedChanged += (_, _) => OnDumpModeChanged();
        row1.Children.Add(_chkDumpMode);
        row1.Children.Add(_cmbDumpFace);
        row1.Children.Add(B("⑥ 生成新建工程位置", BuildNewPosition, tip: "非破坏：把（覆盖范围外的老台阶线 + 模板台阶线 + 衔接段）写进「新建工程位置_*」三层，原图一根不动。内容 = ⑤执行替换之后图上会剩下的那一套，只是不删原实体（两套叠着看，关掉一套即可比对）。"));

        _btnFaceMode.IsCheckedChanged += (_, _) => OnFaceModeChanged();
        ToolTip.SetTip(_btnFaceMode, "配面档：在图上点线成对 —— 第一条 = 坡顶线（橙），第二条 = 坡底线（青）");
        row2.Children.Add(_btnFaceMode);
        row2.Children.Add(B("用选中的面", PickMergeMeshes, tip: "把视口里当前选中的三角网记为「要并进新面模型的已有坡面」"));
        row2.Children.Add(B("清空配面", () => { int n = _facePairs.Count; _facePairs.Clear(); _pendingCrest = 0; Render(); Status(n > 0 ? $"已清空 {n} 对配面。" : "本来就没有配过对。"); }));
        row2.Children.Add(B("⑦ 生成台阶面", BuildBenchFaces, tip: "把成对指定的坡顶/坡底各自沿衔接链拼成整条链，放样成三角带，与选中的已有坡面合并成一个面模型（非破坏，写「新建工程位置_坡面」）"));
    }

    private void OnDumpModeChanged()
    {
        _cmbDumpFace.IsVisible = DumpMode;
        if (DumpMode) FillDumpFaceList();
        // ④⑤ 在排土档下必须禁用 —— ⑤ 按工作线覆盖范围删实体，排土场没有工作线；排土档的前提是原图一根不动
        if (_btnPreviewReplace != null) _btnPreviewReplace.IsEnabled = !DumpMode;
        if (_btnApplyReplace != null) _btnApplyReplace.IsEnabled = !DumpMode;
        _dumpFace = null; _dumpKept.Clear();
        Status(DumpMode
            ? "排土场模式：①提取会把【排土场_*坡顶线/坡底线】当新台阶线，其余线先被【排土场坡面】沿交线裁一刀。这一档不用工作线、不走④⑤，产物由⑥写进新图层。"
            : "已切回采场模式（模板 ⊕ 端帮）。⑤是破坏性的（删原实体）；只想先看这一版工程位置就用⑥ —— 非破坏，写新图层。");
    }

    private void FillDumpFaceList()
    {
        if (_input.Meshes.Count == 0 && _input.Lines.Count == 0) RefreshInput();
        var items = _input.Meshes.Select(m => new MeshItem { Handle = m.Handle, Name = m.Name, Layer = m.Layer, Tris = m.Tris.Length / 3 }).ToList();
        var keep = (_cmbDumpFace.SelectedItem as MeshItem)?.Handle ?? 0;
        _cmbDumpFace.ItemsSource = items;
        var guess = items.FirstOrDefault(i => i.Handle == keep)
                 ?? items.FirstOrDefault(i => i.Layer.Contains("排土场", StringComparison.Ordinal) && (i.Layer.Contains("坡面", StringComparison.Ordinal) || i.Name.Contains("坡面", StringComparison.Ordinal)))
                 ?? items.FirstOrDefault(i => i.Layer.Contains("排土场", StringComparison.Ordinal) || i.Name.Contains("排土场", StringComparison.Ordinal));
        _cmbDumpFace.SelectedItem = guess;
        if (items.Count == 0) Status("图上一张三角网都没有 —— 排土档要用【排土场坡面】裁旧台阶线，请先跑「排土场放坡」。");
        else if (guess == null) Status($"图上 {items.Count} 张三角网，但层名认不出哪张是【排土场坡面】—— 请在下拉里自己选一张。");
        else Status($"已认出排土场坡面「{guess}」，①提取时用它裁旧台阶线。不对就在下拉里换。");
    }

    private bool LoadDumpFace()
    {
        if (_dumpFace != null && !_dumpFace.IsEmpty) return true;
        FillDumpFaceList();
        ulong pick = (_cmbDumpFace.SelectedItem as MeshItem)?.Handle ?? 0;
        if (pick == 0)
        {
            if (_input.Meshes.Count == 0)
                Warn("图上一张三角网都没有。\n排土档要用【排土场坡面】把旧台阶线沿交线裁开 —— 没有这张面就算不出交线，也就无从判断哪些旧台阶线被埋了。请先跑「排土场放坡」。");
            else
                Warn($"还没选【排土场坡面】。图上有 {_input.Meshes.Count} 张三角网：\n  " + string.Join("\n  ", _input.Meshes.Take(12).Select(m => $"{m.Name}（{m.Layer}）")) + (_input.Meshes.Count > 12 ? $"\n  …(共 {_input.Meshes.Count} 张)" : "") + "\n\n请在「排土场模式」右边的下拉里选一张（层名认不出时不替你猜）。");
            return false;
        }
        var mesh = _input.Meshes.FirstOrDefault(m => m.Handle == pick);
        if (mesh.Verts == null || mesh.Tris == null || mesh.Tris.Length < 3) { Warn($"排土场坡面 #{pick} 的几何取不出来。"); return false; }
        _dumpFace = new PitMine3D.Kylin.Cad.SeamOutcrop.MeshZSampler(mesh.Verts, mesh.Tris);
        if (_dumpFace.IsEmpty) { Warn($"排土场坡面 #{pick} 是空网。"); return false; }
        Status($"已载入排土场坡面 #{pick}（{mesh.Tris.Length / 3} 面），旧台阶线将按它沿交线裁。");
        return true;
    }

    /// <summary>①提取的排土档分线：排土台阶线进模板侧；其余线先被排土面沿交线裁一刀再进端帮侧。返回 false = 没坡面，提取中止。</summary>
    private bool SplitLinesDump(List<EpPolyline> tpl, List<EpPolyline> pit)
    {
        if (!LoadDumpFace()) return false;
        foreach (var pl in _input.Lines)
        {
            if (pl.Xyz.Length < 6) continue;
            if (pl.Layer == LinkLayer || pl.Layer == CrossLayer) continue;
            if (EngineeringPositionBuilder.IsDumpBenchLine(pl.Layer)) { tpl.Add(pl); continue; }
            if (pl.Layer.StartsWith(EngineeringPositionBuilder.NewPositionLayerPrefix, StringComparison.Ordinal)) continue;   // 上一批合成产物，不能再当输入
            foreach (var seg in LineAboveMeshClipper.Clip(pl.Xyz, pl.Closed, _dumpFace!, keepUncovered: true))
                pit.Add(new EpPolyline(pl.Handle, pl.Layer, seg, false));
        }
        return true;
    }

    private string DumpNoTemplateMessage()
    {
        var seen = _input.ScanHist.Where(k => k.Value.Polys > 0).Select(k => k.Key).Take(20).ToList();
        string list = seen.Count == 0 ? "（图上一条多段线都没扫到）" : string.Join("\n  ", seen);
        bool hasCrest = false, hasDumpProduct = false;
        foreach (var l in _input.ScanHist.Keys)
        {
            if (l.Contains("堆顶线", StringComparison.Ordinal)) { hasCrest = true; continue; }
            if (l.StartsWith("排土场", StringComparison.Ordinal)) hasDumpProduct = true;
        }
        string tail = "\n\n本次扫到多段线的图层：\n  " + list;
        if (!hasDumpProduct)
            return (hasCrest ? "图上只有【排土场_堆顶线】（「排土场放坡」里手画的那条），没有任何放坡产物。\n\n" : "图上没有任何「排土场」开头的放坡产物。\n\n")
                 + "请重跑「排土场放坡」并走完参数框，确认后应该落下 排土场_坡面（三角网）与 排土场_*_坡顶线 / 坡脚线。" + tail;
        return "图上有「排土场」开头的图层，但没有一层能当排土台阶线。\n\n认的规则：图层名以「排土场」开头，且以「坡顶线 / 坡底线 / 坡脚线」结尾。" + tail;
    }

    // ══════════ ⑥ 生成新建工程位置 ══════════

    private void BuildNewPosition()
    {
        if (DumpMode) BuildNewPositionDump(); else BuildNewPositionPit();
    }

    private async void BuildNewPositionDump()
    {
        if (_scene == null || _tplRaw.Count == 0) { Warn("请先「①提取节点」。"); return; }
        var cons = BuildConnectors();
        double gradePct = TryD(_txtGrade, out double gp) && gp > 0 ? gp : EpConnectorBuilder.DefaultGradePct;
        string pre = EngineeringPositionBuilder.NewPositionLayerPrefix;
        var req = new EpNewPositionRequest();
        int nOld = 0, nNew = 0, nLink = 0;
        foreach (var pl in _dumpKept) if (pl.Xyz.Length >= 6) { req.Polylines.Add((pre + "_原台阶线", ToPts(pl.Xyz), false, (120 / 255f, 170 / 255f, 210 / 255f))); nOld++; }
        foreach (var pl in _tplRaw) if (pl.Xyz.Length >= 6) { req.Polylines.Add((pre + "_排土台阶线", ToPts(pl.Xyz), pl.Closed, (240 / 255f, 192 / 255f, 118 / 255f))); nNew++; }
        foreach (var c in cons) if (c.Pts.Count >= 2) { req.Polylines.Add((pre + "_衔接", new List<(double X, double Y, double Z)>(c.Pts), false, (110 / 255f, 220 / 255f, 130 / 255f))); nLink++; }
        if (nOld + nNew + nLink == 0) { Warn("没有可写的内容（旧线全被埋、也没有排土台阶线）。"); return; }
        if (!await BlockMsgBox.ConfirmAsync(this, "创建工程位置 · 生成新建工程位置（排土档·非破坏）",
                $"将往图上新增 {nOld + nNew + nLink} 条线（不删任何原实体，可 Ctrl+Z）：\n  · 「{pre}_原台阶线」{nOld} 段（被排土面裁剩的旧线）\n  · 「{pre}_排土台阶线」{nNew} 条\n  · 「{pre}_衔接」{nLink} 段\n\n继续？", "生成", "取消"))
        { Status("已取消，图上什么都没动。"); return; }
        _newPosition(req);
        if (_scene.Links.Count > 0) SaveLinks(_scene);
        string msg = $"✓ 已生成【新建工程位置】：原台阶线(裁剩) {nOld} 段 · 排土台阶线 {nNew} 条 · 衔接段 {nLink} 段，写进「{pre}_*」三个图层。原图一根没动，可 Ctrl+Z 撤销这一批。" + (nLink == 0 ? " ⚠ 衔接段为 0 —— 还没在②③里配对过。" : " " + EpConnectorBuilder.Summarize(cons, gradePct));
        Status(msg);
        _echo("创建工程位置 · " + msg, nLink == 0);
    }

    private async void BuildNewPositionPit()
    {
        if (_scene == null || _tplRaw.Count == 0) { Warn("请先「①提取节点」。"); return; }
        if (_workLines.Count == 0)
        {
            Warn("请先加载工作线（「自动查找」或「用选中的」）。\n\n覆盖范围（模板影响区域）是在工作线坐标系下算的 —— 没有工作线就分不出哪些老台阶线被模板取代、哪些是范围外的端帮，这一组也就无从装配。");
            return;
        }
        var plan = ComputePlan(out bool fellBack);
        if (plan == null) { Warn("算不出覆盖范围（模板或工作线为空）。"); return; }
        if (!plan.Success) { Warn("算覆盖范围失败：" + plan.Error); return; }

        _plan = plan;
        AdoptEndWallFromPlan(plan);
        // ②③自动补跑：一处配对都没有时自动跑一遍就近配对（虚线=建议）；手工拖过的照原样用
        double gtol = TryD(_txtGroupTol, out double g0) && g0 > 0 ? g0 : 0.5;
        if (_scene.Links.Count == 0) EngineeringPositionBuilder.AutoPairNearest(_scene, gtol, 400);

        var cons = BuildConnectors();
        double gradePct = TryD(_txtGrade, out double gp) && gp > 0 ? gp : EpConnectorBuilder.DefaultGradePct;
        var eatAt = EatenByCut(cons);
        int overGrade = cons.Count(c => c.OverGrade);

        var byHandle = new Dictionary<ulong, EpPolyline>();
        foreach (var pl in _pitRaw) if (pl.Handle != 0) byHandle[pl.Handle] = pl;

        var oldSegs = new List<(string Layer, List<(double X, double Y, double Z)> Pts, bool Closed)>();
        int nWhole = 0, nUnmatched = 0, nClipSeg = 0, nSuperseded = 0, trimmedSegs = 0, eatenWhole = 0, lost = 0;
        double trimmedLen = 0;
        foreach (var it in plan.Items)
        {
            switch (it.Verdict)
            {
                case BenchReplaceVerdict.Superseded: nSuperseded++; break;
                case BenchReplaceVerdict.Clipped:
                    foreach (var seg in it.Keep)
                    {
                        if (seg.Count < 2) continue;
                        var t = TrimByConnectors(seg, eatAt, out double cut);
                        if (cut > 0) { trimmedSegs++; trimmedLen += cut; }
                        if (t == null) { eatenWhole++; continue; }
                        oldSegs.Add((it.Layer, t, false)); nClipSeg++;
                    }
                    break;
                default:
                    if (it.Verdict == BenchReplaceVerdict.Unmatched) nUnmatched++;
                    if (byHandle.TryGetValue(it.Handle, out var raw) && raw.Xyz.Length >= 6) { oldSegs.Add((it.Layer, ToPts(raw.Xyz), raw.Closed)); nWhole++; }
                    else lost++;
                    break;
            }
        }

        var tplLines = TemplateForBand(out bool tplFellBack);
        string pre = EngineeringPositionBuilder.NewPositionLayerPrefix;
        _selected = null;
        Render(); RebuildLedger(); RebuildCoverLedger();

        int drawn = cons.Count(c => c.HasGeometry), ov = cons.Count(c => c.Overlapped);
        double tl = cons.Where(c => c.Overlapped).Sum(c => c.TrimA + c.TrimB);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"将往图上新增 {oldSegs.Count + tplLines.Count + drawn} 条线（不删任何原实体，可 Ctrl+Z 撤销这一批）：");
        sb.AppendLine($"  · 「{pre}_原台阶线」{oldSegs.Count} 条 = 整条留 {nWhole} 条（其中标高归不进级 {nUnmatched} 条）+ 跨界保留段 {nClipSeg} 段");
        sb.AppendLine($"  · 「{pre}_新台阶线」{tplLines.Count} 条 = 模板台阶" + (tplFellBack ? "（⚠ 一条 台阶_* 都没有，已退回用全部模板侧线）" : ""));
        sb.AppendLine($"  · 「{pre}_衔接」{drawn} 段（来自 {_scene.Links.Count} 处配对）");
        if (ov > 0) sb.AppendLine($"  · 其中 {ov} 处判成【两线已交叉】：不出衔接段，改为把越过交点的尾巴裁掉（共 {tl:0.#} m）—— ③按设计不碰原线，只有这一组会真裁。");
        sb.AppendLine($"整条被模板取代、因而不进这一组的老台阶线：{nSuperseded} 条。");
        if (trimmedSegs > 0) sb.AppendLine($"被过渡段盖住而让位的：{trimmedSegs} 段各裁一截共 {trimmedLen:0.#} m" + (eatenWhole > 0 ? $"，另 {eatenWhole} 段整段让掉" : "") + "。");
        if (lost > 0) sb.AppendLine($"⚠ {lost} 条老线有判决却取不回原几何（提取后图变过？），这一组里缺这几条 —— 请重跑①提取。");
        if (cons.Count == 0) sb.AppendLine("⚠ 衔接段为 0 —— 影响区域边界上没有可配对的断口，或配不出来。这一组的模板与端帮是断开的。");
        else if (overGrade > 0) sb.AppendLine($"⚠ {overGrade}/{cons.Count} 段衔接超过纵坡闸 {gradePct:0.##}%（退路吃到尽头仍缓不下来），几何照出、台账会标。");
        sb.AppendLine();
        sb.AppendLine("内容口径 = ⑤执行替换之后图上会剩下的那一套老线 + 模板台阶 + 衔接段。继续？");
        if (!await BlockMsgBox.ConfirmAsync(this, "创建工程位置 · 生成新建工程位置（非破坏）", sb.ToString(), "生成", "取消"))
        { Status("已取消，图上什么都没动。"); return; }

        var req = new EpNewPositionRequest();
        int nOld = 0, nNew = 0, nLink = 0, nTrimmed = 0; double trimLenSum = 0;
        foreach (var (lay, pts, closed) in oldSegs) if (pts.Count >= 2) { req.Polylines.Add((pre + "_原台阶线", pts, closed, (120 / 255f, 170 / 255f, 210 / 255f))); nOld++; }
        foreach (var pl in tplLines)
        {
            if (pl.Xyz.Length < 6) continue;
            var pts = ToPts(pl.Xyz);
            var cut = TrimCrossTails(pl, pts, cons, out double cutLen);       // 交叉尾巴在这里真的裁掉（③按设计不碰原线）
            if (cutLen > 1e-6) { nTrimmed++; trimLenSum += cutLen; }
            if (cut == null) continue;
            req.Polylines.Add((pre + "_新台阶线", cut, cutLen > 1e-6 ? false : pl.Closed, (0xD9 / 255f, 0x82 / 255f, 0x2B / 255f))); nNew++;
        }
        foreach (var c in cons) if (c.Pts.Count >= 2) { req.Polylines.Add((pre + "_衔接", new List<(double X, double Y, double Z)>(c.Pts), false, (110 / 255f, 220 / 255f, 130 / 255f))); nLink++; }
        int nMark6 = 0;
        foreach (var c in cons) if (c.Overlapped) { req.Marks.Add((pre + "_交点", c.CornerX, c.CornerY, 0.5 * (c.TemplateZ + c.WallZ))); nMark6++; }
        if (nOld + nNew + nLink == 0) { Warn("没有可写的内容（模板、老线、衔接段都是空的）。"); return; }
        _newPosition(req);
        if (_scene.Links.Count > 0) SaveLinks(_scene);
        _selected = null;
        Render(); RebuildLedger(); RebuildCoverLedger();

        string msg = $"✓ 已生成【新建工程位置】：原台阶线 {nOld} 条（整条 {nWhole} + 跨界保留段 {nClipSeg}）· 新台阶线 {nNew} 条" + (nTrimmed > 0 ? $"（其中 {nTrimmed} 条裁掉了交叉尾巴共 {trimLenSum:0.#}m）" : "")
                   + $" · 衔接段 {nLink} 段" + (nMark6 > 0 ? $" · 交点标记 {nMark6} 处" : "") + $"，写进「{pre}_*」图层。原图一根没动，可 Ctrl+Z 撤销这一批；两套叠着看，关掉一套即可比对。"
                   + $" 整条被取代而未进组的老线 {nSuperseded} 条" + (trimmedSegs > 0 ? $"；{trimmedSegs} 段让位共 {trimmedLen:0.#}m" + (eatenWhole > 0 ? $"，另 {eatenWhole} 段整段让掉" : "") : "") + "。"
                   + (cons.Count > 0 ? " " + EpConnectorBuilder.Summarize(cons, gradePct) : " ⚠ 衔接段为 0，模板与端帮是断开的。") + (lost > 0 ? $" ⚠ {lost} 条老线取不回原几何，组里缺这几条。" : "");
        Status(msg);
        _echo("创建工程位置 · " + msg, cons.Count == 0 || lost > 0);
        foreach (var c in cons) _echo("    " + c.Describe(SideName(c.Side)), c.OverGrade);
    }

    /// <summary>按【两线已交叉】那几处把一条线两端越过交点的尾巴裁掉。</summary>
    private List<(double X, double Y, double Z)>? TrimCrossTails(EpPolyline pl, List<(double X, double Y, double Z)> pts, IReadOnlyList<EpConnector> cons, out double cutLen)
    {
        cutLen = 0;
        if (_scene == null || pts.Count < 2) return pts;
        var head = pts[0]; var tail = pts[pts.Count - 1];
        double th = 0, tt = 0;
        foreach (var c in cons)
        {
            if (!c.Overlapped) continue;
            var a = _scene.ById(c.TemplateId); var b = _scene.ById(c.WallId);
            if (a == null || b == null) continue;
            void Eat(EpNode nd, double trim)
            {
                if (trim <= 1e-6) return;
                if (!nd.Members.Any(m => m.Handle == pl.Handle)) return;
                double dh = D2(nd.X, nd.Y, head.X, head.Y), dt = D2(nd.X, nd.Y, tail.X, tail.Y);
                if (dh <= dt) th = Math.Max(th, trim); else tt = Math.Max(tt, trim);
            }
            Eat(a, c.TrimA); Eat(b, c.TrimB);
        }
        if (th <= 1e-6 && tt <= 1e-6) return pts;
        List<(double X, double Y, double Z)>? seg = pts;
        if (th > 1e-6) { seg = EpConnectorBuilder.TrimFrom(seg, fromHead: true, th); cutLen += th; }
        if (seg != null && seg.Count >= 2 && tt > 1e-6) { seg = EpConnectorBuilder.TrimFrom(seg, fromHead: false, tt); cutLen += tt; }
        return seg is { Count: >= 2 } ? seg : null;
    }

    private static double D2(double x0, double y0, double x1, double y1) => (x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0);
    private static List<(double X, double Y, double Z)> ToPts(double[] xyz)
    {
        var l = new List<(double X, double Y, double Z)>(xyz.Length / 3);
        for (int i = 0; i + 2 < xyz.Length; i += 3) l.Add((xyz[i], xyz[i + 1], xyz[i + 2]));
        return l;
    }

    // ══════════ ⑦ 台阶面 ══════════

    private void OnFaceModeChanged()
    {
        _pendingCrest = 0;
        Render();
        Status(FaceMode ? "⑦配面档：在图上点线成对 —— 第一条 = 坡顶线（橙），第二条 = 坡底线（青）。点已指的那条可取消。" : "已退出配面档（已配好的对保留）。");
    }

    /// <summary>点中了哪条线（画布坐标，按点到线段的距离判，与节点一样不靠可视化命中）。</summary>
    private bool HitLine(Point p, out EpPolyline hitLine, double r = 7)
    {
        EpPolyline found = default; bool got = false; double bd = r * r;
        void Scan(IReadOnlyList<EpPolyline> src)
        {
            foreach (var pl in src)
            {
                if (pl.Handle == 0 || pl.Xyz.Length < 6) continue;
                var a = PV(pl.Xyz[0], pl.Xyz[1]);
                for (int i = 3; i + 2 < pl.Xyz.Length; i += 3)
                {
                    var b = PV(pl.Xyz[i], pl.Xyz[i + 1]);
                    double d2 = SegDist2(p, a, b);
                    if (d2 <= bd) { bd = d2; found = pl; got = true; }
                    a = b;
                }
            }
        }
        Scan(_tplRaw); Scan(_pitRaw);
        hitLine = found;
        return got;
    }

    private static double SegDist2(Point p, Point a, Point b)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y, wx = p.X - a.X, wy = p.Y - a.Y;
        double L2 = vx * vx + vy * vy;
        double t = L2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (wx * vx + wy * vy) / L2));
        double dx = wx - vx * t, dy = wy - vy * t;
        return dx * dx + dy * dy;
    }

    /// <summary>配面档下的点线：第一条=坡顶，第二条=坡底。返回 true = 这次按下已被配面消费。</summary>
    private bool HandleFacePick(Point p)
    {
        if (!FaceMode || !HitLine(p, out var line)) return false;
        if (_pendingCrest == 0) { _pendingCrest = line.Handle; Status($"已指【坡顶线】#{line.Handle}（层「{line.Layer}」）—— 再点这一级的【坡底线】即成一对。"); }
        else if (line.Handle == _pendingCrest) { _pendingCrest = 0; Status("已取消这一条坡顶线的指定。"); }
        else
        {
            _facePairs.RemoveAll(q => q.Crest == _pendingCrest || q.Toe == _pendingCrest || q.Crest == line.Handle || q.Toe == line.Handle);
            _facePairs.Add((_pendingCrest, line.Handle));
            _pendingCrest = 0;
            Status($"已配成第 {_facePairs.Count} 对（坡顶 → 坡底）。继续点下一级，或点「⑦ 生成台阶面」。");
        }
        Render();
        return true;
    }

    /// <summary>成对指定过的线单独上色加粗：坡顶橙、坡底青、待配白 —— 配错一对当场看得出来。返回 true = 已画。</summary>
    private bool DrawFaceColored(EpPolyline pl)
    {
        if (pl.Handle != 0 && pl.Handle == _pendingCrest) { AddPvPoly(pl.Xyz, Colors.White, 3.2); return true; }
        if (_facePairs.Any(q => q.Crest == pl.Handle)) { AddPvPoly(pl.Xyz, CFaceCrest, 3.0); return true; }
        if (_facePairs.Any(q => q.Toe == pl.Handle)) { AddPvPoly(pl.Xyz, CFaceToe, 3.0); return true; }
        return false;
    }

    private void PickMergeMeshes()
    {
        RefreshInput();
        _mergeMeshes.Clear();
        int notMesh = 0;
        foreach (var h in _input.SelectedHandles)
        {
            if (_input.Meshes.Any(m => m.Handle == h && m.Tris.Length >= 3)) _mergeMeshes.Add(h); else notMesh++;
        }
        Status(_mergeMeshes.Count > 0
            ? $"已记下 {_mergeMeshes.Count} 张已有坡面，⑦ 会把它们与新放样的面【合并成一个面模型】。" + (notMesh > 0 ? $"（选中的另 {notMesh} 个不是三角网，已跳过）" : "")
            : "选中的实体里没有三角网 —— 请在视口里选中要并进来的坡面，再点这个按钮。");
    }

    internal async void BuildBenchFaces()
    {
        if (_scene == null || _tplRaw.Count == 0) { Warn("请先「①提取节点」。"); return; }
        if (_facePairs.Count == 0)
        {
            Warn("还没有成对指定坡顶/坡底线。\n\n按下「⑦ 配面」，在图上点两条线成一对：第一条 = 坡顶线，第二条 = 坡底线。\n哪两条算同一级，层名和标高都分不出来（扩帮把所有级并到了两层，级号只在颜色里），猜错一对放样出来的面会横跨两级、把平盘整个吞掉，所以这一维只能由你指。");
            return;
        }
        var cons = BuildConnectors();
        var byHandle = new Dictionary<ulong, EpPolyline>();
        foreach (var pl in _tplRaw) if (pl.Handle != 0) byHandle[pl.Handle] = pl;
        foreach (var pl in _pitRaw) if (pl.Handle != 0) byHandle[pl.Handle] = pl;

        var verts = new List<double>(); var tris = new List<int>();
        int madeFaces = 0, skipped = 0; double areaSum = 0;
        var lines = new List<string>();
        foreach (var (ch, th) in _facePairs)
        {
            if (!byHandle.TryGetValue(ch, out var cpl) || !byHandle.TryGetValue(th, out var tpl)) { skipped++; lines.Add($"  · 坡顶#{ch} / 坡底#{th}：线取不回来（提取后图变过？）"); continue; }
            var crest = BenchFaceBuilder.StitchChain(cpl, _scene, cons, byHandle);
            var toe = BenchFaceBuilder.StitchChain(tpl, _scene, cons, byHandle);
            if (crest.Count < 2 || toe.Count < 2) { skipped++; lines.Add($"  · 坡顶#{ch} / 坡底#{th}：拼不出链（点数不足）"); continue; }
            int before = verts.Count / 3;
            double a = BenchFaceBuilder.Ribbon(crest, toe, verts, tris);
            areaSum += a; madeFaces++;
            lines.Add($"  · 第 {madeFaces} 幅：坡顶链 {crest.Count} 点 / 坡底链 {toe.Count} 点 → {verts.Count / 3 - before} 顶点，面积 {a:0.#} m²");
        }
        if (madeFaces == 0) { Warn("一幅面都没放样出来：\n" + string.Join("\n", lines)); return; }

        int mergedMesh = 0; long mergedTri = 0;
        foreach (var h in _mergeMeshes)
        {
            var m = _input.Meshes.FirstOrDefault(x => x.Handle == h);
            if (m.Verts == null || m.Tris == null) continue;
            int off = verts.Count / 3;
            verts.AddRange(m.Verts);
            foreach (var idx in m.Tris) tris.Add(off + idx);
            mergedMesh++; mergedTri += m.Tris.Length / 3;
        }

        string layer = EngineeringPositionBuilder.NewPositionLayerPrefix + "_坡面";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"将生成【一个】新面模型：{madeFaces} 幅放样面（总面积 {areaSum:0.#} m²）" + (mergedMesh > 0 ? $" + 并入 {mergedMesh} 张已有坡面（{mergedTri} 三角面）" : "（没有并入已有坡面）") + $"，合计 {verts.Count / 3} 顶点 / {tris.Count / 3} 三角面。");
        foreach (var l in lines) sb.AppendLine(l);
        if (skipped > 0) sb.AppendLine($"⚠ 有 {skipped} 对没能放样，见上。");
        if (mergedMesh == 0) sb.AppendLine("注：没有并入任何已有坡面 —— 要合并的话，先在视口里选中它们、点「用选中的面」。");
        sb.AppendLine(); sb.AppendLine($"写进「{layer}」，原图一根不动、一张面不删，可 Ctrl+Z。继续？");
        if (!await BlockMsgBox.ConfirmAsync(this, "创建工程位置 · 生成台阶面", sb.ToString(), "生成", "取消")) { Status("已取消，图上什么都没动。"); return; }

        var req = new EpNewPositionRequest();
        req.Meshes.Add((layer, "新建工程位置_坡面", verts.ToArray(), tris.ToArray(), (0xE0 / 255f, 0x8A / 255f, 0x2A / 255f)));
        _newPosition(req);
        string msg = $"✓ 台阶面已生成：{madeFaces} 幅放样面（{areaSum:0.#} m²）" + (mergedMesh > 0 ? $" + {mergedMesh} 张已有坡面" : "") + $" 合并为一个面模型（{verts.Count / 3} 顶点 / {tris.Count / 3} 三角面），写进「{layer}」。原图一根没动，可 Ctrl+Z。";
        Status(msg);
        _echo("创建工程位置 · " + msg, false);
        foreach (var l in lines) _echo("  " + l, false);
    }

    internal void BuildNewPositionForSelftest() => BuildNewPosition();

    /// <summary>自检/脚本用：按句柄配一对面。</summary>
    internal bool PairFaceByHandles(ulong crest, ulong toe)
    {
        if (crest == 0 || toe == 0 || crest == toe) return false;
        _facePairs.RemoveAll(q => q.Crest == crest || q.Toe == crest || q.Crest == toe || q.Toe == toe);
        _facePairs.Add((crest, toe));
        Render();
        return true;
    }
    internal IEnumerable<(ulong Handle, string Layer)> LineHandles() => _tplRaw.Concat(_pitRaw).Select(p => (p.Handle, p.Layer));
}
