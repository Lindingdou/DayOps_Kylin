using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「组合工作线」= 原 MineAssLib <c>CreateWorkLineGroupFromSelection</c>（AcDbWorkLineGroup）：选中 ≥2 条工作线 → 组合成一条连续推进 front。
/// 各成员保留自己的推进方式与句柄（不合并几何）；组按最近端点串链，缺口处架「软连接段」（虚线 + 箭头，不是实几何、不出夹点）；
/// 端点本就重合的接缝算硬接缝。可 Ctrl+Z。
/// 【登记的差异】原版组是一个持久实体（存盘重开仍是组、拖成员端点桥自己伸缩）；Kylin 无自定义实体类型，组记在会话内
/// <see cref="_workLineGroups"/>（成员引用 + 软连接实体），软连接段落图层「工作线_软连接」随成员一起选/删。
///
/// 「连接台阶线」= 原 <c>JoinBenchLineDialog</c> + 内核 <c>JoinBenchLines</c>：弹窗选范围（当前选集/全部台阶线）+ 端点容差 + 只同图层 + 自动闭合
/// → 端点重合的段依次拼接；新线继承原线图层/颜色/标高（保台阶语义，「编辑台阶」照样能用），整步一次 Ctrl+Z。
/// </summary>
public partial class MainWindow
{
    private const string WorkLineSoftLinkLayer = "工作线_软连接";
    private readonly List<(List<PolylineEntity> members, List<SceneEntity> softLinks)> _workLineGroups = new();

    private void WorkLineGroupCmd()
    {
        var members = _selected.OfType<PolylineEntity>().Where(p => p.LayerName == WorkLineLayer && p.Points.Count >= 2).ToList();
        if (members.Count < 2)
        {
            StatusMsg.Text = "组合工作线失败：请先选中 ≥2 条工作线（非多段线）";
            EditEcho("组合工作线失败:请先选中 ≥2 条工作线(非多段线) —— 工作线由「创建工作线」画出（图层「工作线」）", EchoLevel.Warn);
            return;
        }
        double tol = Math.Max(1e-6, SnapTolWorld(_lastPointer) * 0.5);
        var ends = members.Select(m => (m.Points[0], m.Points[^1])).ToList();
        var chain = WorkLineGroup.Chain(ends, tol);

        BeginChange();
        // 先撤掉这些成员上一次的软连接（重组）
        foreach (var g in _workLineGroups.Where(g => g.members.Any(members.Contains)).ToList())
        { foreach (var e in g.softLinks) _scene.Remove(e); _workLineGroups.Remove(g); }

        var soft = new List<SceneEntity>();
        foreach (var s in chain.SoftLinks)
        {
            double z = members[s.FromIndex].Elevation;
            var seg = new PolylineEntity { LayerName = WorkLineSoftLinkLayer, Elevation = z, Cr = 0.20f, Cg = 0.80f, Cb = 0.95f, Dash = new[] { 3.0, 2.0 } };
            seg.Points.Add(s.From); seg.Points.Add(s.To);
            _scene.Add(seg); soft.Add(seg);
            // 箭头：指向链的推进次序（From → To）
            double dx = s.To.x - s.From.x, dy = s.To.y - s.From.y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len > 1e-9)
            {
                double ux = dx / len, uy = dy / len, h = Math.Min(len * 0.3, Math.Max(1.0, len * 0.1));
                var arrow = new PolylineEntity { LayerName = WorkLineSoftLinkLayer, Elevation = z, Cr = 0.98f, Cg = 0.75f, Cb = 0.20f };
                arrow.Points.Add((s.To.x - ux * h - uy * h * 0.4, s.To.y - uy * h + ux * h * 0.4));
                arrow.Points.Add(s.To);
                arrow.Points.Add((s.To.x - ux * h + uy * h * 0.4, s.To.y - uy * h - ux * h * 0.4));
                _scene.Add(arrow); soft.Add(arrow);
            }
        }
        var ordered = chain.Order.Select(l => members[l.Index]).ToList();
        _workLineGroups.Add((ordered, soft));
        _selected.Clear(); _selected.AddRange(ordered);
        RefreshScene();
        string seam = chain.SoftLinks.Count > 0
            ? $"{chain.SoftLinks.Count} 处缺口已用软连接段桥接（虚线+箭头，合计 {chain.TotalGapM:0.#} m；不是实几何）"
            : "各段端点本就首尾相接";
        StatusMsg.Text = $"> 工作线组已创建（组合 {members.Count} 条）：各段保留自己的推进方式；{seam}；Ctrl+Z 撤销";
        EditEcho($"> 工作线组已创建(组合 {members.Count} 条, 硬接缝 {chain.HardSeams}):链序 {string.Join(" → ", chain.Order.Select(l => $"#{l.Index + 1}{(l.Reversed ? "↺" : "")}"))}", EchoLevel.Success);
    }

    private async Task JoinBenchLinesCmd(string cmd)
    {
        int selCount = _selected.Count(e => e is PolylineEntity);
        string hint = selCount > 0
            ? $"当前选中 {selCount} 条线；按「当前选集」范围只连这些线。"
            : "当前没有选中实体 —— 请先在场景里选中要连的台阶线，或把范围改成「全部台阶线」。";
        var inv = CultureInfo.InvariantCulture;
        bool scopeAll = false, autoClose = true, sameLayer = true; double tol = 0.01;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 2)
        {
            for (int i = 1; i < tk.Length; i++)
            {
                if (tk[i] == "全部") scopeAll = true;
                else if (tk[i] == "不闭合") autoClose = false;
                else if (tk[i] == "跨图层") sameLayer = false;
                else if (double.TryParse(tk[i], NumberStyles.Float, inv, out double t) && t > 0) tol = t;
            }
        }
        else
        {
            var v = await Views.Modeling.PromptDialog.AskAsync(this, "连接台阶线 — 按首尾连接关系串成整条", new List<Views.Modeling.PromptDialog.Field>
            {
                new("scope", "范围", selCount > 0 ? "当前选集" : "全部台阶线", Choices: new[] { "当前选集", "全部台阶线" }, Numeric: false, Hint: "全部台阶线 = 扫图上所有开口台阶线（图层含 台阶/坡脚/坡顶/坑线）"),
                new("tol", "端点容差", "0.01", "m"),
                new("same", "只在同图层内连接", "true", Bool: true, Hint: "同台阶级 · 同坡顶/坡脚"),
                new("close", "串完首尾重合时收成闭合环", "true", Bool: true),
            }, "端点落在容差内的台阶线段视为首尾相接，按连接关系串成一条；新线继承原线的图层、颜色与标高，整步一次 Ctrl+Z 撤销。\n" + hint);
            if (v == null) { StatusMsg.Text = "连接台阶线：用户取消"; EditEcho("连接台阶线:用户取消"); return; }
            scopeAll = v.S("scope") == "全部台阶线"; tol = v.D("tol", 0.01); sameLayer = v.B("same"); autoClose = v.B("close");
        }
        if (tol <= 0) { StatusMsg.Text = "连接台阶线：端点容差须 > 0"; return; }

        static bool LooksLikeBench(string layer) => layer.Contains("台阶") || layer.Contains("坡脚") || layer.Contains("坡顶") || layer.Contains("坑线") || layer.Contains("toe", StringComparison.OrdinalIgnoreCase) || layer.Contains("crest", StringComparison.OrdinalIgnoreCase);
        var pool = scopeAll
            ? _scene.Entities.OfType<PolylineEntity>().Where(p => p.Visible && _layers.IsShown(p.LayerName) && LooksLikeBench(p.LayerName)).ToList()
            : _selected.OfType<PolylineEntity>().ToList();
        if (pool.Count == 0) { StatusMsg.Text = scopeAll ? "连接台阶线：图上没有台阶线（图层含 台阶/坡脚/坡顶/坑线 的多段线）" : "连接台阶线：当前选集里没有多段线 —— 先选中要连的台阶线，或改用「全部台阶线」"; return; }

        var inputs = pool.Select((p, i) => new BenchLineJoin.Input
        {
            Id = i, Layer = p.LayerName, Z = p.Elevation, Points = p.Points,
            Closed = p.Closed || (p.Points.Count >= 3 && Dist2(p.Points[0], p.Points[^1]) < 1e-12),
        }).ToList();
        var r = BenchLineJoin.Join(inputs, tol, autoClose, sameLayer);
        if (r.NothingJoined)
        {
            StatusMsg.Text = $"连接台阶线：{r.CandidateOpen} 条开口线里没找到首尾相接的段（容差 {tol:0.###} m）" + (sameLayer ? "；若碎段被分到了不同图层，可取消「只在同图层内连接」再试" : "");
            EditEcho(StatusMsg.Text, EchoLevel.Warn);
            return;
        }
        BeginChange();
        foreach (var c in r.Chains)
        {
            var first = pool[c.MemberIds[0]];
            var pl = new PolylineEntity { Closed = c.AutoClosed, LayerName = c.Layer, Elevation = c.Z, Cr = first.Cr, Cg = first.Cg, Cb = first.Cb, Dash = first.Dash, LineWeight = first.LineWeight };
            pl.Points.AddRange(c.Points);
            foreach (int id in c.MemberIds) { _scene.Remove(pool[id]); _selected.Remove(pool[id]); }
            _scene.Add(pl); _selected.Add(pl);
        }
        RefreshScene();
        int final = _scene.Entities.OfType<PolylineEntity>().Count(p => LooksLikeBench(p.LayerName));
        string scope = scopeAll ? "全部台阶线" : "当前选集";
        string closed = r.AutoClosedCount > 0 ? $"，其中 {r.AutoClosedCount} 条收成闭合环" : "";
        string skipped = r.SkippedClosed > 0 ? $"（另有 {r.SkippedClosed} 条已闭合，未参与）" : "";
        StatusMsg.Text = $"✓ 连接台阶线·{scope}：{r.ConsumedCount} 条碎段串成 {r.Chains.Count} 条整线{closed}{skipped}；台阶线总数 {final} 条，图层/颜色/标高已继承（可 Ctrl+Z）";
        EditEcho(StatusMsg.Text, EchoLevel.Success);
    }
}
