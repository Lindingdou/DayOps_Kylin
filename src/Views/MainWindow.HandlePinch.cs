using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「处理尖灭」（台阶面收口）= 原 MineAssLib <c>HandlePinchDialog</c>：
/// 手动 · 取尖灭点 + 点坡顶线（该台阶楔形收口，<b>上部台阶位置联动</b>）；
/// 煤层 · 顶/底板双面夹持（① 锁定台阶组 ② 顶板 ③ 底板，缺煤处尖灭）。线和面同步更新，可 Ctrl+Z。
/// 原版截断/联动在内核（StartManualPinch / ApplySeamPinch）；这里托管等价：截断走 <see cref="BenchPinch"/>，
/// 台阶组 = 同图层前缀的台阶线（与「编辑台阶」同一口径），上部联动 = 组内标高更高的线在同一尖灭点（最近弧位）一并截断，
/// 组内坡面/平盘网按截断后的线用 <see cref="BenchBuilder"/> 重算替换（同「编辑台阶」）。不做"强制贯通"。
/// </summary>
public partial class MainWindow
{
    private async Task HandlePinchDialogCmd(string cmd)
    {
        var inv = CultureInfo.InvariantCulture;
        bool seamMode = false, linkUpper = true; double minThick = 0.3;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2)
        {
            for (int i = 1; i < tk.Length; i++)
            {
                if (tk[i] == "手动") seamMode = false;
                else if (tk[i] == "煤层") seamMode = true;
                else if (tk[i] == "不联动") linkUpper = false;
                else if (double.TryParse(tk[i], NumberStyles.Float, inv, out double t) && t > 0) minThick = t;
            }
        }
        else
        {
            var selMeshes = _selected.OfType<MeshEntity>().Count(m => m.Tris.Count > 0);
            var v = await Views.Modeling.PromptDialog.AskAsync(this, "处理尖灭（台阶面收口）", new List<Views.Modeling.PromptDialog.Field>
            {
                new("mode", "尖灭方式", selMeshes >= 2 ? "煤层 · 顶/底板双面夹持" : "手动 · 取尖灭点 + 点坡顶线", Choices: new[] { "手动 · 取尖灭点 + 点坡顶线", "煤层 · 顶/底板双面夹持" }, Numeric: false,
                    Hint: "手动：该台阶楔形收口、上部联动；煤层：crest 夹顶板、toe 夹底板，缺煤处尖灭"),
                new("link", "上部台阶位置联动", "true", Bool: true, Hint: "手动方式：组内标高更高的台阶线在同一尖灭点一并截断"),
                new("thick", "最小煤厚", "0.3", "m", Hint: "煤层方式：顶底板间隔小于此值处视为缺煤"),
            }, "选择尖灭方式 → 按步骤拾取 → 应用。线和面会同步更新，可 Ctrl+Z 撤销。");
            if (v == null) { StatusMsg.Text = "处理尖灭：已取消。"; return; }
            seamMode = v.S("mode").StartsWith("煤层"); linkUpper = v.B("link"); minThick = v.D("thick", 0.3);
        }
        if (seamMode) await SeamPinchAsync(minThick); else await ManualPinchAsync(linkUpper);
    }

    /// <summary>手动：① 红十字拾取尖灭点（坡面上）→ ② 方框点击要收口的坡顶线 → 截断 + 上部联动 + 组面重算。</summary>
    private async Task ManualPinchAsync(bool linkUpper)
    {
        EditEcho("处理尖灭·手动:① 在视口拾取尖灭点(坡面上) → ② 点击要收口的坡顶线;该台阶收成楔形、上部台阶位置联动,自动落地(可 Ctrl+Z;Esc 取消)");
        var (kind, px, py) = await PickPointOrConfirmAsync("处理尖灭·手动：① 拾取【尖灭点】（Esc 取消）", false);
        if (kind != PickKind.Picked) { StatusMsg.Text = "处理尖灭：已取消。"; return; }
        EditEcho($"  尖灭点 ({px:0.#}, {py:0.#})");
        var line = await PickEntityInViewportAsync<PolylineEntity>("处理尖灭·手动：② 点击要收口的【坡顶线】（Esc 取消）", p => p.Points.Count >= 2, "没点中台阶线，请重选");
        if (line == null) { StatusMsg.Text = "处理尖灭：已取消（未选坡顶线）。"; return; }

        var r = BenchPinch.TruncateAtPoint(line.Points, px, py);
        if (!r.Ok) { StatusMsg.Text = "处理尖灭·手动：" + r.Error; EditEcho(StatusMsg.Text, EchoLevel.Warn); return; }
        if (r.CutLengthM < 1e-6) { StatusMsg.Text = "处理尖灭·手动：" + r.Note; return; }

        string prefix = (line.LayerName ?? "").Split('_')[0];
        var group = _scene.Entities.OfType<PolylineEntity>()
            .Where(p => !ReferenceEquals(p, line) && p.Points.Count >= 2 && prefix.Length > 0 && (p.LayerName ?? "").StartsWith(prefix + "_") && p.Elevation > line.Elevation + 1e-6)
            .OrderBy(p => p.Elevation).ToList();

        BeginChange();
        int linked = 0, faces = 0;
        ReplaceLine(line, r.Line);
        if (linkUpper)
            foreach (var up in group)
            {
                var ru = BenchPinch.TruncateAtPoint(up.Points, r.PinchX, r.PinchY);
                if (ru.Ok && ru.CutLengthM > 1e-6) { ReplaceLine(up, ru.Line); linked++; }
            }
        faces = RebuildBenchFacesForGroup(prefix, line.Elevation, (line.LayerName ?? "").StartsWith("排土场"));
        _scene.Add(new PointEntity { X = r.PinchX, Y = r.PinchY, Cr = 0.95f, Cg = 0.25f, Cb = 0.25f, LayerName = "尖灭点" });
        _selected.Clear();
        RefreshScene();
        StatusMsg.Text = $"✓ 处理尖灭·手动：{r.Note}" + (linkUpper ? $"；上部联动截断 {linked} 条" : "") + (faces > 0 ? $"；组内坡面/平盘重算 {faces} 张" : "") + "（尖灭点已标红，可 Ctrl+Z）";
        EditEcho(StatusMsg.Text, EchoLevel.Success);
    }

    /// <summary>煤层：① 锁定台阶组（选中的台阶线所在组）② 顶板 ③ 底板 → 组内每条线沿线煤厚→0 处截断。</summary>
    private async Task SeamPinchAsync(double minThick)
    {
        var selLines = _selected.OfType<PolylineEntity>().Where(p => p.Points.Count >= 2).ToList();
        PolylineEntity? anchor = selLines.Count >= 1 ? selLines[0] : null;
        if (anchor == null)
        {
            anchor = await PickEntityInViewportAsync<PolylineEntity>("处理尖灭·煤层：① 点击一条台阶线以【锁定台阶组】（Esc 取消）", p => p.Points.Count >= 2, "没点中台阶线，请重选");
            if (anchor == null) { StatusMsg.Text = "处理尖灭：已取消（未锁定台阶组）。"; return; }
        }
        string prefix = (anchor.LayerName ?? "").Split('_')[0];
        var group = _scene.Entities.OfType<PolylineEntity>().Where(p => p.Points.Count >= 2 && prefix.Length > 0 && ((p.LayerName ?? "").StartsWith(prefix + "_") || ReferenceEquals(p, anchor))).ToList();
        if (!group.Contains(anchor)) group.Add(anchor);
        EditEcho($"  ① 已锁定台阶组「{prefix}」：{group.Count} 条线");

        var meshes = _selected.OfType<MeshEntity>().Where(m => m.Tris.Count > 0).ToList();
        MeshEntity? topM = meshes.Count >= 1 ? meshes[0] : null, botM = meshes.Count >= 2 ? meshes[1] : null;
        topM ??= await PickEntityInViewportAsync<MeshEntity>("处理尖灭·煤层：② 拾取【顶板面】（Esc 取消）", m => m.Tris.Count > 0, "没点中三角网，请重选");
        if (topM == null) { StatusMsg.Text = "处理尖灭：已取消（未拾取顶板）。"; return; }
        botM ??= await PickEntityInViewportAsync<MeshEntity>("处理尖灭·煤层：③ 拾取【底板面】（Esc 取消）", m => m.Tris.Count > 0 && !ReferenceEquals(m, topM), "没点中三角网，请重选");
        if (botM == null) { StatusMsg.Text = "处理尖灭：已取消（未拾取底板）。"; return; }
        var top = SamplerOf(topM); var bot = SamplerOf(botM);
        if (top == null || bot == null) { StatusMsg.Text = "处理尖灭·煤层：顶/底板面无法采样。"; return; }
        var p0 = anchor.Points[0];
        if (top.TrySample(p0.x, p0.y, out double zt) && bot.TrySample(p0.x, p0.y, out double zb) && zt < zb)
        { (top, bot) = (bot, top); EditEcho("  顶底反了，已按标高对调（高者为顶板）。", EchoLevel.Warn); }

        BeginChange();
        int cut = 0, kept = 0, failed = 0; double cutLen = 0;
        (double x, double y)? lastPinch = null;
        foreach (var ln in group.ToList())
        {
            var r = BenchPinch.TruncateWhereThin(ln.Points, top, bot, minThick);
            if (!r.Ok) { failed++; EditEcho($"  {ln.LayerName} Z={ln.Elevation:0.#}: {r.Error}", EchoLevel.Warn); continue; }
            if (r.CutLengthM < 1e-6) { kept++; continue; }
            ReplaceLine(ln, r.Line); cut++; cutLen += r.CutLengthM; lastPinch = (r.PinchX, r.PinchY);
            _scene.Add(new PointEntity { X = r.PinchX, Y = r.PinchY, Cr = 0.95f, Cg = 0.25f, Cb = 0.25f, LayerName = "尖灭点" });
        }
        int faces = cut > 0 ? RebuildBenchFacesForGroup(prefix, group.Min(p => p.Elevation), prefix.StartsWith("排土场")) : 0;
        _selected.Clear();
        RefreshScene();
        StatusMsg.Text = cut > 0
            ? $"✓ 处理尖灭·煤层（最小煤厚 {minThick:0.##} m）：组「{prefix}」{cut} 条线在缺煤处截断（共去 {cutLen:0.#} m），{kept} 条全线有煤未动" + (failed > 0 ? $"，{failed} 条不在煤里" : "") + (faces > 0 ? $"；组内坡面/平盘重算 {faces} 张" : "") + "（可 Ctrl+Z）"
            : $"处理尖灭·煤层：组「{prefix}」{group.Count} 条线全线煤厚都 ≥ {minThick:0.##} m，没有尖灭，未动" + (failed > 0 ? $"（{failed} 条不在煤里）" : "");
        EditEcho(StatusMsg.Text, cut > 0 ? EchoLevel.Success : EchoLevel.Info);
    }

    /// <summary>用截断后的点列替换一条线（保图层/颜色/标高/线型）。</summary>
    private void ReplaceLine(PolylineEntity old, IReadOnlyList<(double x, double y)> pts)
    {
        var nl = new PolylineEntity { Cr = old.Cr, Cg = old.Cg, Cb = old.Cb, LayerName = old.LayerName, Elevation = old.Elevation, Dash = old.Dash, LineWeight = old.LineWeight };
        nl.Points.AddRange(pts);
        _scene.Remove(old); _selected.Remove(old);
        _scene.Add(nl);
    }

    /// <summary>
    /// 组内坡面/平盘网按截断后的台阶线重算（同「编辑台阶」的替换口径：删掉同前缀的 _坡面/_平盘 网，按每条坡脚线各出一级坡面）。
    /// 只有由本程序放坡生成（层名 前缀_坡脚线）的组才有面可重算；导入的台阶线没有面，返回 0。
    /// </summary>
    private int RebuildBenchFacesForGroup(string prefix, double baseZ, bool isDump)
    {
        if (prefix.Length == 0) return 0;
        var toes = _scene.Entities.OfType<PolylineEntity>().Where(p => (p.LayerName ?? "") == prefix + "_坡脚线" && p.Points.Count >= 2).OrderByDescending(p => p.Elevation).ToList();
        if (toes.Count == 0) return 0;
        var db = EnsureGeoDb();
        var rp = Data.BenchTemplateResolver.Resolve(db?.Connection, isDump: isDump);
        int removed = 0;
        foreach (var e in _scene.Entities.Where(x => (x.LayerName ?? "") == prefix + "_坡面" || (x.LayerName ?? "") == prefix + "_平盘").ToList()) { _scene.Remove(e); removed++; }
        int made = 0;
        foreach (var toe in toes)
        {
            bool closed = toe.Closed || (toe.Points.Count >= 3 && Dist2(toe.Points[0], toe.Points[^1]) < 1e-12);
            // 坡脚线向上一级 = 坡面（坡顶在 toe 上方 H 处，往外 H/tanα）
            var r = BenchBuilder.Build(toe.Points, closed, toe.Elevation, rp.BenchHeight, rp.FaceAngleDeg, 0, 1, downward: false, isDump: isDump);
            if (!r.Ok || r.FaceTris.Count == 0) continue;
            var face = new MeshEntity($"{prefix}·坡面", r.FaceVerts, r.FaceTris) { Cr = isDump ? 0.62f : 0.80f, Cg = isDump ? 0.52f : 0.66f, Cb = isDump ? 0.40f : 0.45f, LayerName = prefix + "_坡面" };
            _scene.Add(face); made++;
        }
        return made;
    }
}
