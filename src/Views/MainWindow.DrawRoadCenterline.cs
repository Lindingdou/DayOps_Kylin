using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「画道路中线」= 原 MineAssLib <c>CreateDrawRoadCenterlineCommand</c>（RS13–RS25）：
/// 【先选中那张现状三角网】再点本命令 → 左键沿坡面依次点中线（每点吸面取精确 Z），右键结束 → 弹「道路参数」（① 基础配置·记住默认 /
/// ② 当前中线参数提取·只读）→ 走统一落地管线 <see cref="LandCenterlineAsRoad"/>（转角圆弧化 → 贴面限坡 → 加宽/超高 → 放样 → 挖填边坡 + 分账）
/// → 整次记为一段（【撤销坑线】可撤）→ 中线登记进路网（「点云_道路中心线」层，寻径/等效运距即算这条）。
/// 不选面则退回首末匀坡老口径，并就「继续（只出路面）/ 取消」弹框拍板（原版的「切全部面」在 Kylin 不存在：不切原网）。
/// </summary>
public partial class MainWindow
{
    private const string RampParamsKey = "mineass.ramp.params";

    /// <summary>三点外接圆半径（转角处的"实际转弯半径"，共线为 +∞，重点为 0）。</summary>
    private static double Circumradius(double ax, double ay, double bx, double by, double cx, double cy)
    {
        double a = Math.Sqrt((bx - cx) * (bx - cx) + (by - cy) * (by - cy));
        double b = Math.Sqrt((ax - cx) * (ax - cx) + (ay - cy) * (ay - cy));
        double c = Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));
        double area2 = Math.Abs((bx - ax) * (cy - ay) - (cx - ax) * (by - ay));   // 2×面积
        if (area2 < 1e-12) return double.PositiveInfinity;
        return a * b * c / (2 * area2);
    }

    private async Task DrawRoadCenterlineCmd()
    {
        // RS5/RS13：进取点前先快照选中的那张现状面 —— 视口点击会清掉选集，过后就取不到了。不自动取"图中最大的三角网"：布路拿错面整条路都错。
        var sampler = SamplerFromSelectedMeshes(out int meshCount);
        if (sampler == null) EditEcho("画道路中线：没选中三角网 —— 中线 Z 取不到面，落地退回「首末匀坡」老口径。要贴面请先选中现状面再点本命令。", EchoLevel.Warn);
        else EditEcho($"画道路中线：已快照 {meshCount} 张面作采样/被切面。");

        var pts = new List<(double x, double y, double z)>();
        EditEcho("画道路中线:左键依次点中线点(自动吸坡面),右键结束(至少 2 点)");
        while (true)
        {
            var (kind, x, y) = await PickPointOrConfirmAsync($"中线点 {pts.Count + 1}（左键取点 · 右键/回车结束 · Esc 取消）", true, quiet: pts.Count > 0);
            if (kind == PickKind.Cancelled) { StatusMsg.Text = "画道路中线：已取消。"; return; }
            if (kind == PickKind.Confirmed) break;
            double z = 0; bool hit = sampler != null && sampler.TrySample(x, y, out z);
            pts.Add((x, y, hit ? z : 0));
            EditEcho($"中线点 {pts.Count}: ({x:F1}, {y:F1}, {(hit ? z.ToString("F1", CultureInfo.InvariantCulture) : "—")})" + (sampler != null && !hit ? "  ⚠ 不在面上" : ""));
        }
        if (pts.Count < 2) { StatusMsg.Text = "画道路中线：点数不足 2，已取消。"; EditEcho("画道路中线:点数不足 2,已取消"); return; }

        // ── 「道路参数」对话框：① 基础配置（记住默认值）② 当前中线参数提取（只读）──
        var p = UserSettings.Current.Get<RampDesignParams>(RampParamsKey) ?? LandingParams();
        var inv = CultureInfo.InvariantCulture;
        double len = 0, minR = double.PositiveInfinity;
        for (int i = 1; i < pts.Count; i++) len += Math.Sqrt((pts[i].x - pts[i - 1].x) * (pts[i].x - pts[i - 1].x) + (pts[i].y - pts[i - 1].y) * (pts[i].y - pts[i - 1].y));
        for (int i = 1; i + 1 < pts.Count; i++)
        {
            double r = Circumradius(pts[i - 1].x, pts[i - 1].y, pts[i].x, pts[i].y, pts[i + 1].x, pts[i + 1].y);
            if (r > 0 && r < minR) minR = r;
        }
        double dh = pts[^1].z - pts[0].z;
        string extract = $"② 当前中线：{pts.Count} 点 · 高差 {dh:+0.#;-0.#;0} m · 平面长 {len:0.#} m · 平均纵坡 {(len > 1e-9 ? Math.Abs(dh) / len * 100 : 0):0.##}% · 最小转角半径 {(double.IsInfinity(minR) ? "—" : minR.ToString("0.#", inv) + " m")}";
        var fields = new List<Views.Modeling.PromptDialog.Field>
        {
            new("type", "类型", p.IsDump ? "排土场" : "采场", Choices: new[] { "采场", "排土场" }, Numeric: false),
            new("w", "路宽", p.RoadWidth.ToString("0.##", inv), "m"),
            new("g", "最大纵坡", p.MaxGradePct.ToString("0.##", inv), "%"),
            new("r", "最小转弯半径", p.MinTurnRadius.ToString("0.##", inv), "m", Hint: "转角按此圆弧化；0 = 不圆弧化"),
            new("widen", "弯道加宽", p.CurveWiden.ToString("0.##", inv), "m", Hint: "0 = 不加宽"),
            new("super", "最大超高", p.SuperElevPct.ToString("0.##", inv), "%"),
            new("lanes", "车道数", p.LaneCount.ToString(inv), "车道"),
            new("wb", "轴距", p.WheelbaseM.ToString("0.##", inv), "m"),
            new("v", "设计速度", p.DesignSpeedKmh.ToString("0.##", inv), "km/h"),
            new("berm", "车挡高", p.BermHeight.ToString("0.##", inv), "m"),
            new("cut", "挖方边坡角", p.CutSlopeDeg.ToString("0.##", inv), "°"),
            new("fill", "填方边坡角", p.FillSlopeDeg.ToString("0.##", inv), "°"),
            new("cg", "弯道纵坡", p.CurveGradePct.ToString("0.##", inv), "%", Hint: "回头弯处的减坡值"),
        };
        var vv = await Views.Modeling.PromptDialog.AskAsync(this, "道路参数（① 基础配置 · 记住默认值）", fields, extract);
        if (vv == null) { StatusMsg.Text = "画道路中线：已取消（未创建斜坡道）"; EditEcho("画道路中线:已取消(未创建斜坡道)"); return; }
        p.IsDump = vv.S("type") == "排土场";
        p.RoadWidth = vv.D("w", p.RoadWidth); p.MaxGradePct = vv.D("g", p.MaxGradePct); p.MinTurnRadius = vv.D("r", p.MinTurnRadius);
        p.CurveWiden = vv.D("widen", p.CurveWiden); p.SuperElevPct = vv.D("super", p.SuperElevPct); p.LaneCount = Math.Max(1, (int)vv.D("lanes", p.LaneCount));
        p.WheelbaseM = vv.D("wb", p.WheelbaseM); p.DesignSpeedKmh = vv.D("v", p.DesignSpeedKmh); p.BermHeight = vv.D("berm", p.BermHeight);
        p.CutSlopeDeg = vv.D("cut", p.CutSlopeDeg); p.FillSlopeDeg = vv.D("fill", p.FillSlopeDeg); p.CurveGradePct = vv.D("cg", p.CurveGradePct);
        if (p.RoadWidth <= 0 || p.MaxGradePct <= 0) { StatusMsg.Text = "画道路中线：路宽与最大纵坡必须 > 0"; return; }
        try { UserSettings.Current.Set(RampParamsKey, p); UserSettings.Current.Flush(); } catch { /* 记不住默认值不挡流程 */ }

        if (sampler == null)
        {
            bool go = await Views.Modeling.BlockMsgBox.ConfirmAsync(this, "画道路中线 · 未指定被切面",
                "本次没有选中任何三角网（现状面 / 台阶坡面）。\n\n"
              + "「继续」= 只出路面带：Z 沿首末匀坡、没有挖填方边坡，路会浮在地形上。\n"
              + "「取消」= 本次不创建。\n\n要贴到面上并出边坡，请取消后先选中那张（或那几张）面，再点一次本命令。",
                "继续", "取消");
            if (!go) { StatusMsg.Text = "画道路中线：未指定被切面，已取消（未落地）。"; return; }
        }

        // ── 统一落地管线（与坑线落地 / 螺旋 / 折返 / 自动布线同一条）──
        var flat = new double[pts.Count * 3];
        for (int i = 0; i < pts.Count; i++) { flat[i * 3] = pts[i].x; flat[i * 3 + 1] = pts[i].y; flat[i * 3 + 2] = pts[i].z; }
        var log = new List<string>();
        var segment = new List<SceneEntity>();
        BeginChange();
        bool ok = LandCenterlineAsRoad(flat, p, sampler, "手拾坑线", segment, log, arcRound: true);
        if (ok)
        {
            // 中线登记进路网：落「点云_道路中心线」层（三维），寻径/等效运距即算这条
            var layer = RoadEnsureLayer(RoadCenterlineLayer, CenterlineAmber, 0.5f);
            var cl = new PolylineEntity { LayerName = layer.Name, Cr = CenterlineAmber.R / 255f, Cg = CenterlineAmber.G / 255f, Cb = CenterlineAmber.B / 255f, Zs = new List<double>() };
            foreach (var (x, y, z) in pts) { cl.Points.Add((x, y)); cl.Zs.Add(z); }
            _scene.Add(cl); segment.Add(cl);
            RS.Graph = null;
            _landedRampSegments.Add(segment);
        }
        _selected.Clear();
        RefreshScene();
        foreach (var l in log) AppendHistoryLine("  " + l, EchoBrush(l.StartsWith("⚠") ? EchoLevel.Warn : EchoLevel.Info));
        if (!ok) { StatusMsg.Text = "画道路失败：见信息栏。"; return; }
        var g = TryBuildRoadGraph();
        StatusMsg.Text = $"✓ 画道路中线·手拾坑线：{pts.Count} 点 · 路宽 {p.RoadWidth:0.#}m · 限坡 {p.MaxGradePct:0.#}% · R≥{p.MinTurnRadius:0.#}m"
                       + (sampler != null ? $" · 贴 {meshCount} 张面出挖填方边坡" : " · 未贴面（首末匀坡，只出路面）")
                       + $" · 已登记进路网{(g != null ? $"（{g.NodeCount} 节点 / {g.EdgeCount} 边）" : "")} · 记为 1 段（【撤销坑线】可撤，共 {_landedRampSegments.Count} 段）";
    }
}
