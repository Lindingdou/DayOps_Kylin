using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 运输工程组的三条"参数化插入"坑线（忠实原 MineAssLib CreateInsertRamp{Spiral,Switchback,Direct}Command）：
/// 各弹自己的参数框（照原 InsertRamp*Dialog 分区）→ 参数化中线（<see cref="RampCenterlines"/>）→ 走统一落地管线
/// <see cref="LandCenterlineAsRoad"/> 一次生成路面 + 边线 + 边坡并落地，整次记为一段（【撤销坑线】可撤）。
/// 被切面 / 采样面 = 点本命令【之前】选中的三角网（原 SnapshotSurfaceSampler）；没选面就只出路面带、不出边坡（命令行如实说）。
/// 平盘联络道见 <see cref="BenchConnectorRampCmd"/>（本文件只补它的参数框）。
/// </summary>
public partial class MainWindow
{
    private const string RampSpiralKey = "mineass.ramp.spiral";
    private const string RampSwitchbackKey = "mineass.ramp.switchback";

    /// <summary>螺旋坑线：中心 cX/cY + 起始 Z、半径/起始角/圈数(可分数)/旋向 + 纵坡·路宽 → 绕中心逐圈下降，一次落地。</summary>
    private async Task SpiralRampInsertCmd(string cmd)
    {
        var sampler = SamplerFromSelectedMeshes(out int meshCount);
        var inv = CultureInfo.InvariantCulture;
        var (vx, vy) = ViewCenterWorld();
        var c = LandingParams();
        var last = UserSettings.Current.Get<Dictionary<string, string>>(RampSpiralKey);
        string L(string k, string d) => last != null && last.TryGetValue(k, out var s) && s.Length > 0 ? s : d;
        double z0 = 0; if (sampler != null && sampler.TrySample(vx, vy, out double zz)) z0 = zz;

        bool isDump = false; double cx = vx, cy = vy, sz = z0, radius = 50, startAng = 0, turns = 2, grade = c.MaxGradePct, width = c.RoadWidth; bool ccw = true;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 4 && double.TryParse(tk[1], NumberStyles.Float, inv, out cx) && double.TryParse(tk[2], NumberStyles.Float, inv, out cy) && double.TryParse(tk[3], NumberStyles.Float, inv, out sz))
        {
            if (tk.Length >= 5) double.TryParse(tk[4], NumberStyles.Float, inv, out radius);
            if (tk.Length >= 6) double.TryParse(tk[5], NumberStyles.Float, inv, out turns);
            if (tk.Length >= 7) double.TryParse(tk[6], NumberStyles.Float, inv, out grade);
            if (tk.Length >= 8) double.TryParse(tk[7], NumberStyles.Float, inv, out width);
            if (Array.IndexOf(tk, "顺时针") >= 0) ccw = false;
        }
        else
        {
            var fields = new List<Views.Modeling.PromptDialog.Field>
            {
                new("type", "所属边坡", L("type", "采场"), Choices: new[] { "采场", "排土场" }, Numeric: false, Hint: "影响图层命名"),
                new("cx", "螺旋中心 X", cx.ToString("0.##", inv), "m", Hint: "预填当前视图中心"),
                new("cy", "螺旋中心 Y", cy.ToString("0.##", inv), "m"),
                new("sz", "起始 Z", sz.ToString("0.##", inv), "m", Hint: sampler != null ? "预填中心处所选面标高" : "未选面：请手填"),
                new("r", "半径", L("r", "50"), "m"),
                new("a0", "起始角", L("a0", "0"), "°", Hint: "自 +X 逆时针量"),
                new("turns", "圈数", L("turns", "2"), "圈", Hint: "可分数，如 1.5"),
                new("dir", "旋向", L("dir", "逆时针"), Choices: new[] { "逆时针", "顺时针" }, Numeric: false),
                new("g", "纵坡", grade.ToString("0.##", inv), "%"),
                new("w", "路宽", width.ToString("0.##", inv), "m"),
            };
            var v = await Views.Modeling.PromptDialog.AskAsync(this, "螺旋坑线（固定半径圆螺旋）", fields,
                sampler != null ? $"已选中 {meshCount} 张面作采样/被切面；确认后中线+左右边线+路面一次落地" : "⚠ 未选中三角网：只出路面带、不出边坡（要贴面请先选面再点）");
            if (v == null) { StatusMsg.Text = "螺旋坑线：用户取消"; EditEcho("螺旋坑线:用户取消"); return; }
            isDump = v.S("type") == "排土场"; cx = v.D("cx", cx); cy = v.D("cy", cy); sz = v.D("sz", sz);
            radius = v.D("r", radius); startAng = v.D("a0", startAng); turns = v.D("turns", turns); ccw = v.S("dir") != "顺时针";
            grade = v.D("g", grade); width = v.D("w", width);
            try { UserSettings.Current.Set(RampSpiralKey, new Dictionary<string, string> { ["type"] = v.S("type"), ["r"] = v.S("r"), ["a0"] = v.S("a0"), ["turns"] = v.S("turns"), ["dir"] = v.S("dir") }); UserSettings.Current.Flush(); } catch { }
        }
        if (radius <= 0 || turns <= 0 || grade <= 0 || width <= 0) { StatusMsg.Text = "螺旋坑线：半径/圈数/纵坡/路宽都须 > 0"; return; }

        var pts = RampCenterlines.Spiral(cx, cy, sz, radius, startAng, turns, ccw, grade);
        if (pts.Count < 2) { StatusMsg.Text = "螺旋坑线：参数无效"; return; }
        var p = LandingParams(); p.IsDump = isDump; p.RoadWidth = width; p.MaxGradePct = grade;
        await LandInsertedRampAsync("螺旋坑线", pts, p, sampler, meshCount, arcRound: false,
            $"中心 ({cx:0.#},{cy:0.#}) Z0={sz:0.#} · R={radius:0.#}m · {turns:0.##} 圈 {(ccw ? "逆时针" : "顺时针")} · 纵坡 {grade:0.#}% · 降 {sz - pts[^1].Z:0.#}m");
    }

    /// <summary>折返坑线：顶部起点（手填 / 坡面点取）+ 腿数/腿长/直线纵坡 + 回头半径·减坡·加宽·超高·挡墙，甩向自动(按坡面梯度)/左/右。</summary>
    private async Task SwitchbackRampInsertCmd(string cmd)
    {
        var sampler = SamplerFromSelectedMeshes(out int meshCount);
        var inv = CultureInfo.InvariantCulture;
        var (vx, vy) = ViewCenterWorld();
        var c = LandingParams();
        var last = UserSettings.Current.Get<Dictionary<string, string>>(RampSwitchbackKey);
        string L(string k, string d) => last != null && last.TryGetValue(k, out var s) && s.Length > 0 ? s : d;

        // 起点：命令行给 / 对话框手填 / 在坡面上点取（原 btnPickStart）
        double sx = vx, sy = vy, sz = 0; bool picked = false;
        var tk = cmd.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length >= 4 && double.TryParse(tk[1], NumberStyles.Float, inv, out sx) && double.TryParse(tk[2], NumberStyles.Float, inv, out sy) && double.TryParse(tk[3], NumberStyles.Float, inv, out sz)) picked = true;
        else if (Array.IndexOf(tk, "点取") >= 0 || (tk.Length == 1 && sampler != null))
        {
            var (kind, px, py) = await PickPointOrConfirmAsync("折返坑线：在坡面上点取【顶部起点】（Esc = 改为手填）", false);
            if (kind == PickKind.Picked)
            {
                sx = px; sy = py; picked = true;
                if (sampler != null && sampler.TrySample(px, py, out double pz)) { sz = pz; EditEcho($"折返坑线：起点 ({px:0.#}, {py:0.#}) 面标高 Z={pz:0.#}"); }
                else EditEcho($"折返坑线：起点 ({px:0.#}, {py:0.#}) 不在所选面上，Z 请手填", EchoLevel.Warn);
            }
        }
        if (!picked && sampler != null && sampler.TrySample(sx, sy, out double z0)) sz = z0;

        // 甩向/走向：自动 = 按坡面梯度（AutoTurnSide），采不到面/平地不许猜 → 报出来让人选
        double azAuto = 0; int sideAuto = 0; bool autoOk = sampler != null && RampCenterlines.AutoTurnSide(sampler, sx, sy, 10, out azAuto, out sideAuto);

        bool isDump = false; double az = autoOk ? azAuto : 0; string sideText = autoOk ? "自动(按坡面)" : "向左"; int legs = 3, arcSeg = 18;
        double legLen = 100, grade = c.MaxGradePct, curveGrade = c.CurveGradePct, radius = Math.Max(c.MinTurnRadius, 15), width = c.RoadWidth, widen = c.CurveWiden, super = c.SuperElevPct, berm = c.BermHeight;
        if (tk.Length >= 5)
        {
            if (tk.Length >= 5) int.TryParse(tk[4], out legs);
            if (tk.Length >= 6) double.TryParse(tk[5], NumberStyles.Float, inv, out legLen);
            if (tk.Length >= 7) double.TryParse(tk[6], NumberStyles.Float, inv, out grade);
            if (tk.Length >= 8) double.TryParse(tk[7], NumberStyles.Float, inv, out radius);
            if (tk.Length >= 9) double.TryParse(tk[8], NumberStyles.Float, inv, out az);
            if (Array.IndexOf(tk, "右") >= 0) sideText = "向右"; else if (Array.IndexOf(tk, "左") >= 0) sideText = "向左";
        }
        else
        {
            var fields = new List<Views.Modeling.PromptDialog.Field>
            {
                new("type", "所属边坡", L("type", "采场"), Choices: new[] { "采场", "排土场" }, Numeric: false, Hint: "影响图层命名"),
                new("sx", "顶部起点 X", sx.ToString("0.##", inv), "m", Hint: picked ? "已在坡面上点取" : "预填视图中心；命令行 折返坑线 点取 可在坡面上点"),
                new("sy", "顶部起点 Y", sy.ToString("0.##", inv), "m"),
                new("sz", "顶部起点 Z", sz.ToString("0.##", inv), "m"),
                new("az", "第一腿走向", az.ToString("0.##", inv), "°", Hint: autoOk ? $"自动：按坡面梯度 = {azAuto:0.#}°" : "自动判不出（未选面/平地），请手填"),
                new("side", "甩向", sideText, Choices: new[] { "自动(按坡面)", "向左", "向右" }, Numeric: false, Hint: autoOk ? $"自动 = {(sideAuto > 0 ? "向左" : "向右")}（马步朝下坡侧）" : "自动不可用：采不到坡面梯度"),
                new("legs", "直腿数", L("legs", "3"), "腿", Hint: "≥2"),
                new("len", "每腿长", L("len", "100"), "m"),
                new("g", "直线纵坡", grade.ToString("0.##", inv), "%"),
                new("cg", "回头弧减坡", curveGrade.ToString("0.##", inv), "%"),
                new("r", "回头半径", radius.ToString("0.##", inv), "m"),
                new("w", "路宽", width.ToString("0.##", inv), "m"),
                new("widen", "弯道加宽", widen.ToString("0.##", inv), "m"),
                new("super", "超高", super.ToString("0.##", inv), "%"),
                new("berm", "挡墙高", berm.ToString("0.##", inv), "m"),
            };
            var v = await Views.Modeling.PromptDialog.AskAsync(this, "折返坑线（回头曲线 · 外凸折返台）", fields,
                sampler != null ? $"已选中 {meshCount} 张面作采样/被切面；直腿 + 180° 回头弧往返逐腿下降，一次落地" : "⚠ 未选中三角网：甩向无法自动、只出路面带不出边坡");
            if (v == null) { StatusMsg.Text = "折返坑线：用户取消"; EditEcho("折返坑线:用户取消"); return; }
            isDump = v.S("type") == "排土场"; sx = v.D("sx", sx); sy = v.D("sy", sy); sz = v.D("sz", sz); az = v.D("az", az); sideText = v.S("side");
            legs = Math.Max(2, (int)v.D("legs", legs)); legLen = v.D("len", legLen); grade = v.D("g", grade); curveGrade = v.D("cg", curveGrade);
            radius = v.D("r", radius); width = v.D("w", width); widen = v.D("widen", widen); super = v.D("super", super); berm = v.D("berm", berm);
            try { UserSettings.Current.Set(RampSwitchbackKey, new Dictionary<string, string> { ["type"] = v.S("type"), ["legs"] = v.S("legs"), ["len"] = v.S("len") }); UserSettings.Current.Flush(); } catch { }
        }
        int side;
        if (sideText.StartsWith("自动"))
        {
            if (!autoOk) { StatusMsg.Text = "折返坑线：甩向「自动」需能在起点四周采到坡面梯度（未选面 / 起点不在面上 / 平地）—— 请改选向左/向右"; EditEcho("折返坑线:甩向自动判不出,不许猜 —— 请改选向左/向右", EchoLevel.Warn); return; }
            side = sideAuto;
        }
        else side = sideText == "向右" ? -1 : +1;
        if (legs < 2 || legLen <= 0 || grade <= 0 || radius <= 0 || width <= 0) { StatusMsg.Text = "折返坑线：腿数≥2，腿长/纵坡/回头半径/路宽都须 > 0"; return; }

        var pts = RampCenterlines.Switchback(sx, sy, sz, az, side, legs, legLen, grade, curveGrade, radius, 8.0, arcSeg);
        if (pts.Count < 2) { StatusMsg.Text = "折返坑线：参数无效"; return; }
        var p = LandingParams(); p.IsDump = isDump; p.RoadWidth = width; p.MaxGradePct = grade; p.CurveGradePct = curveGrade; p.MinTurnRadius = radius; p.CurveWiden = widen; p.SuperElevPct = super; p.BermHeight = berm;
        await LandInsertedRampAsync("折返坑线", pts, p, sampler, meshCount, arcRound: false,
            $"起点 ({sx:0.#},{sy:0.#},{sz:0.#}) · 走向 {az:0.#}° 甩{(side > 0 ? "左" : "右")}{(sideText.StartsWith("自动") ? "(自动)" : "")} · {legs} 腿 × {legLen:0.#}m · 纵坡 {grade:0.#}%/弯 {curveGrade:0.#}% · R={radius:0.#}m · 降 {sz - pts[^1].Z:0.#}m");
    }

    /// <summary>参数化中线 → 统一落地管线；成功记为一段。</summary>
    private async Task LandInsertedRampAsync(string source, List<(double X, double Y, double Z)> pts, RampDesignParams p,
                                             IRoadZSampler? sampler, int meshCount, bool arcRound, string summary)
    {
        var flat = Flatten3(pts);
        RememberRouteForLanding(new[] { flat }, roadWidth: p.RoadWidth, gradePct: p.MaxGradePct, source: source);
        var log = new List<string>();
        var segment = new List<SceneEntity>();
        BeginChange();
        // 中线本身也入图（原版：中线 + 左右边线 + 路面）
        var cl = new PolylineEntity { Cr = 0.30f, Cg = 0.95f, Cb = 0.95f, LayerName = (p.IsDump ? "排土场" : "采场") + "_" + source + "_中线", Zs = new List<double>() };
        foreach (var (x, y, z) in pts) { cl.Points.Add((x, y)); cl.Zs.Add(z); }
        _scene.Add(cl); segment.Add(cl);
        bool ok = LandCenterlineAsRoad(flat, p, sampler, source, segment, log, arcRound);
        if (segment.Count > 0) _landedRampSegments.Add(segment);
        _selected.Clear();
        RefreshScene();
        foreach (var l in log) AppendHistoryLine("  " + l, EchoBrush(l.StartsWith("⚠") ? EchoLevel.Warn : EchoLevel.Info));
        StatusMsg.Text = ok
            ? $"✓ {source}：{summary} · 中线 {pts.Count} 点 · 路宽 {p.RoadWidth:0.#}m" + (sampler != null ? $" · 贴 {meshCount} 张面出挖填方边坡" : " · 未贴面（只出路面带）") + $" · 记为 1 段（【撤销坑线】可撤，共 {_landedRampSegments.Count} 段）"
            : $"{source}：中线已入图，路面落地失败（见信息栏）";
        await Task.CompletedTask;
    }
}
