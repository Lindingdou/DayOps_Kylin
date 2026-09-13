using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「批量扩坑」（按煤层层位分层放坡）= 原 MineAssLib <c>CreateSeamPitCommand</c>：选 1 条闭合境界线 → 面板指定顶板 / 底板面
/// → 内核 <c>BuildSeamPitMultiSeam</c>。内核无源，几何走 <see cref="SeamPitBuilder"/>（托管等价，差异见那里的注释）。
/// 顶/底板：与境界线一起选中的两张三角网（先顶后底，标高反了自动对调）；没选够就在视口里逐张点选。
/// </summary>
public partial class MainWindow
{
    private const string SeamPitRockLayer = "批量扩坑_岩台阶";
    private const string SeamPitCoalLayer = "批量扩坑_煤台阶";
    private const string SeamPitPinchLayer = "批量扩坑_尖灭点";

    private async Task SeamPitRunAsync(PolylineEntity boundary, string cmd)
    {
        var meshes = _selected.Where(e => e is MeshEntity m && m.Tris.Count > 0).Cast<MeshEntity>().ToList();
        MeshEntity? topM = meshes.Count >= 1 ? meshes[0] : null, botM = meshes.Count >= 2 ? meshes[1] : null;
        if (topM == null)
        {
            topM = await PickEntityInViewportAsync<MeshEntity>("批量扩坑：点选【煤层顶板面】（Esc 取消）", m => m.Tris.Count > 0, "没点中三角网，请重选");
            if (topM == null) { StatusMsg.Text = "批量扩坑：已取消（未指定顶板面）。"; return; }
        }
        if (botM == null)
        {
            botM = await PickEntityInViewportAsync<MeshEntity>("批量扩坑：点选【煤层底板面】（Esc 取消）", m => m.Tris.Count > 0 && !ReferenceEquals(m, topM), "没点中三角网，请重选");
            if (botM == null) { StatusMsg.Text = "批量扩坑：已取消（未指定底板面）。"; return; }
        }
        var top = SamplerOf(topM); var bot = SamplerOf(botM);
        if (top == null || bot == null) { StatusMsg.Text = "批量扩坑：顶/底板面无法采样。"; return; }
        var p0 = boundary.Points[0];
        if (top.TrySample(p0.x, p0.y, out double zt0) && bot.TrySample(p0.x, p0.y, out double zb0) && zt0 < zb0)
        { (top, bot) = (bot, top); (topM, botM) = (botM, topM); AppendHistoryLine("  批量扩坑：选中的两张面顶底反了，已按标高对调（高者为顶板）。", EchoBrush(EchoLevel.Warn)); }

        var db = EnsureGeoDb();
        var rp = Data.BenchTemplateResolver.Resolve(db?.Connection, isDump: false);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        double rockH = rp.BenchHeight, coalH = rp.CoalBenchHeight > 0 ? rp.CoalBenchHeight : rp.BenchHeight, alpha = rp.FaceAngleDeg, w = rp.BermWidth;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2)
        {
            if (tk.Length >= 2 && double.TryParse(tk[1], System.Globalization.NumberStyles.Float, inv, out double a1) && a1 > 0) rockH = a1;
            if (tk.Length >= 3 && double.TryParse(tk[2], System.Globalization.NumberStyles.Float, inv, out double a2) && a2 > 0) coalH = a2;
            if (tk.Length >= 4 && double.TryParse(tk[3], System.Globalization.NumberStyles.Float, inv, out double a3) && a3 > 0) alpha = a3;
            if (tk.Length >= 5 && double.TryParse(tk[4], System.Globalization.NumberStyles.Float, inv, out double a4) && a4 >= 0) w = a4;
        }
        else
        {
            var v = await Views.Modeling.PromptDialog.AskAsync(this, "批量扩坑（按煤层层位分层放坡）", new List<Views.Modeling.PromptDialog.Field>
            {
                new("rockH", "岩台阶高 H", rockH.ToString("0.##", inv), "m", Hint: "顶板以上的水平岩台阶"),
                new("coalH", "煤台阶高 Hc", coalH.ToString("0.##", inv), "m", Hint: "顶板以下按煤厚分级；末级钉在底板上"),
                new("alpha", "坡面角 α", alpha.ToString("0.##", inv), "°"),
                new("w", "平盘宽 W", w.ToString("0.##", inv), "m"),
            }, $"依据 {rp.Provenance} ｜ 顶板「{topM.Name}」 底板「{botM.Name}」 境界 {boundary.Points.Count} 点 Z={boundary.Elevation:0.#}");
            if (v == null) { StatusMsg.Text = "批量扩坑：已取消。"; return; }
            rockH = v.D("rockH", rockH); coalH = v.D("coalH", coalH); alpha = v.D("alpha", alpha); w = v.D("w", w);
        }

        var r = SeamPitBuilder.Build(boundary.Points, boundary.Elevation, rockH, coalH, alpha, w, top, bot);
        foreach (var n in r.Notes) AppendHistoryLine("  " + n, EchoBrush(EchoLevel.Info));
        if (!r.Ok) { StatusMsg.Text = "批量扩坑失败：" + r.Error; EditEcho("批量扩坑失败：" + r.Error, EchoLevel.Error); return; }

        BeginChange();
        int made = 0;
        foreach (var run in r.Runs)
        {
            var pl = new PolylineEntity
            {
                Closed = run.FullRing, Elevation = run.Z,
                LayerName = run.IsCoal ? SeamPitCoalLayer : SeamPitRockLayer,
                Cr = run.IsCoal ? 0.20f : 0.85f, Cg = run.IsCoal ? 0.20f : 0.70f, Cb = run.IsCoal ? 0.20f : 0.45f,
            };
            pl.Points.AddRange(run.Points);
            _scene.Add(pl); made++;
        }
        foreach (var (x, y, z) in r.TopPinchPoints) { _scene.Add(new PointEntity { X = x, Y = y, Cr = 0.95f, Cg = 0.55f, Cb = 0.15f, LayerName = SeamPitPinchLayer }); made++; }
        foreach (var (x, y, z) in r.FloorPinchPoints) { _scene.Add(new PointEntity { X = x, Y = y, Cr = 0.95f, Cg = 0.25f, Cb = 0.25f, LayerName = SeamPitPinchLayer }); made++; }
        _selected.Clear();
        RefreshScene();
        int rockRuns = r.Runs.Count(x => !x.IsCoal), coalRuns = r.Runs.Count(x => x.IsCoal);
        StatusMsg.Text = $"✓ 批量扩坑：岩台阶 {r.RockLevels} 级（{rockRuns} 段）· 煤台阶 {r.CoalLevels} 级（{coalRuns} 段）· 顶板交线尖灭点 {r.TopPinchPoints.Count} · 底板尖灭点 {r.FloorPinchPoints.Count}"
                       + $"（H={rockH:0.#}/Hc={coalH:0.#}/α={alpha:0.#}/W={w:0.#}）· 新建 {made} 实体（可 Ctrl+Z）";
    }
}
