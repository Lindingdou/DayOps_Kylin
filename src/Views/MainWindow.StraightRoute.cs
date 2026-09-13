using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Cad.RoadLayout;
using PitMine3D.Kylin.Cad.Transport;
using BenchLine = PitMine3D.Kylin.Cad.RoadLayout.BenchLine;
using StraightRampRouteOptions = PitMine3D.Kylin.Cad.RoadLayout.StraightRampRouteOptions;
using RampForm = PitMine3D.Kylin.Cad.RoadLayout.RampForm;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「直线坑线」= 原 MineAssLib <c>CreateStraightRampRouteCommand</c>：弹 <c>StraightRouteParamsDialog</c>（台阶线来源 设计线/现状线 ·
/// 平盘宽 · 路宽/限坡/回头半径/最小间距 预填自「约束条件设置」· 旋向 · 从坑底起）→ 原 <see cref="Cad.RoadLayout.StraightRampAutoRouter"/>
/// （逐级直腿首尾相接，放不下 → 约束化折返 → 螺旋 逐级兜底；线形/纵断面/横断面后处理）→ 逐级回显 + 预览中线入图 + 写落地缓存。
/// 折返/螺旋段只出中线预览、不逐面切落地（原因命令行如实回显）。
/// 台阶线来源：设计线 = 本会话「批量台阶扩帮」产出（原 _lastBenchLines）；现状线 = 图上选中的那批折线（原 BenchLineSourceResolver 的现状线档）。
/// 【登记的差异】原版第三档「现状面 — 选中的三角网自动提坡面」待接（BenchLineSourceResolver 的面提取段依赖 BenchFaceExtractor 配对）。
/// </summary>
public partial class MainWindow
{
    private const string BenchSourceKindKey = "transport.benchsource";

    private async Task StraightRampRouteDialogCmd()
    {
        var cfg = TransportConstraintProfileStore.LoadMirrorOrDefault(UserSettings.Current);
        var inv = CultureInfo.InvariantCulture;
        string lastSrc = "";
        try { lastSrc = UserSettings.Current.Get<string>(BenchSourceKindKey) ?? ""; } catch { }
        bool haveDesign = _lastBenchLevels != null && _lastBenchLevels.Count >= 2;
        string[] srcChoices = { "设计线 — 批量台阶扩帮产出的 toe/crest", "现状线 — 图上选中的那批折线" };
        string defSrc = lastSrc.StartsWith("现状") || !haveDesign ? srcChoices[1] : srcChoices[0];
        double bw0 = cfg.RoadWidthPreview();
        var v = await Views.Modeling.PromptDialog.AskAsync(this, "直线坑线 — 自动布线参数", new List<Views.Modeling.PromptDialog.Field>
        {
            new("src", "台阶线来源", defSrc, Choices: srcChoices, Numeric: false, Hint: haveDesign ? $"本会话已有批量台阶扩帮产出 {_lastBenchLevels!.Count} 级" : "本会话还没跑过批量台阶扩帮 → 只能用现状线"),
            new("berm", "平盘宽", (haveDesign ? _lastBenchBermW : cfg.MinWorkingBenchWidth).ToString("0.##", inv), "m", Hint: "现状数据没有平盘宽，不猜 —— 判「回头放不放得下」用这个值"),
            new("w", "路面宽度", bw0.ToString("0.##", inv), "m", Hint: "= 车道×车宽 + 间隙 + 安全带"),
            new("g", "限制坡度 i_max", cfg.MaxGradePct.ToString("0.##", inv), "%"),
            new("r", "回头最小半径 R", cfg.TruckTurnRadius.ToString("0.##", inv), "m", Hint: "= 卡车最小转弯半径"),
            new("sp", "坑线最小间距", bw0.ToString("0.##", inv), "m", Hint: "相邻坑线段须 ≥ 此值，防叠加（缺省=路宽）"),
            new("dir", "盘旋旋向", "逆时针", Choices: new[] { "逆时针", "顺时针" }, Numeric: false),
            new("bottom", "从坑底起", "", Bool: true, Hint: "确认后在坑底坡面点取起始位置，自下而上布线"),
        }, "自动布一条从坑底到地表的坑线（直进优先 · 放不下折返 · 折返也折不进螺旋）。参数从「约束条件设置」预填，可在此临时改。路宽与最小间距决定相邻坑线是否叠加。");
        if (v == null) { StatusMsg.Text = "直线坑线：已取消（未布线）。"; EditEcho("直线坑线:已取消(未布线)。"); return; }
        bool designSrc = v.S("src").StartsWith("设计线");
        try { UserSettings.Current.Set(BenchSourceKindKey, designSrc ? "设计线" : "现状线"); UserSettings.Current.Flush(); } catch { }
        double berm = v.D("berm", cfg.MinWorkingBenchWidth), roadW = v.D("w", bw0), grade = v.D("g", cfg.MaxGradePct), turnR = v.D("r", cfg.TruckTurnRadius), minSp = v.D("sp", bw0);
        bool ccw = v.S("dir") != "顺时针", fromBottom = v.B("bottom");
        if (roadW <= 0 || grade <= 0 || turnR < 0 || berm < 0) { StatusMsg.Text = "直线坑线：路宽/限坡须 > 0，回头半径/平盘宽 ≥ 0。"; return; }

        var benches = ResolveBenchLinesForStraightRoute(designSrc, berm, out string note);
        if (benches == null) return;
        EditEcho(note);

        (double X, double Y)? seed = null;
        if (fromBottom)
        {
            var (kind, px, py) = await PickPointOrConfirmAsync("直线坑线：请在【坑底】坡面上左键点一个起始位置（右键/Esc = 用默认地表起点）", true);
            if (kind == PickKind.Picked) { seed = (px, py); EditEcho($"直线坑线:坑底起点 ({px:0.#}, {py:0.#}) → 自下而上布线…"); }
            else EditEcho("直线坑线:未点坑底起点 → 用默认起点(地表起)。");
        }
        RunStraightRoute(benches, cfg, roadW, grade, turnR, minSp, ccw, seed);
    }

    private List<BenchLine>? ResolveBenchLinesForStraightRoute(bool designSrc, double berm, out string note)
    {
        note = "";
        if (designSrc)
        {
            if (_lastBenchLevels == null || _lastBenchLevels.Count < 2)
            { StatusMsg.Text = "直线坑线：本会话没有「批量台阶扩帮」产出的台阶线 —— 先跑一遍，或改用现状线。"; EditEcho(StatusMsg.Text, EchoLevel.Warn); return null; }
            var list = _lastBenchLevels.Select(lv => new BenchLine
            {
                Level = lv.CrestZ, Crest = lv.Crest.Select(p => (p.x, p.y, lv.CrestZ)).ToList(), Toe = lv.Toe.Select(p => (p.x, p.y, lv.ToeZ)).ToList(),
                BermWidth = berm, IsWorkingWall = false, Closed = lv.Crest.Count >= 3,
            }).OrderByDescending(b => b.Level).ToList();
            note = $"直线坑线：台阶线 = 设计线（批量台阶扩帮 {list.Count} 级，Z {list[^1].Level:0.#}~{list[0].Level:0.#}m，平盘 {berm:0.#}m）。";
            return list;
        }
        var sel = _selected.OfType<PolylineEntity>().Where(p => p.Points.Count >= 3).ToList();
        if (sel.Count < 2)
        { StatusMsg.Text = "直线坑线：现状线来源需先选中 ≥2 条同心台阶环（坡顶线）。"; EditEcho(StatusMsg.Text, EchoLevel.Warn); return null; }
        sel.Sort((a, b) => Math.Abs(BenchLines.SignedArea(b.Points)).CompareTo(Math.Abs(BenchLines.SignedArea(a.Points))));
        bool flat = sel.Select(r => r.Elevation).Distinct().Count() <= 1;
        var db = EnsureGeoDb();
        double h = Data.BenchTemplateResolver.Resolve(db?.Connection, isDump: false).BenchHeight;
        if (h <= 0) h = 15;
        var outList = new List<BenchLine>();
        for (int k = 0; k < sel.Count; k++)
        {
            double z = flat ? (sel.Count - 1 - k) * h : sel[k].Elevation;
            var pts = sel[k].Points.Select(p => (p.x, p.y, z)).ToList();
            outList.Add(new BenchLine { Level = z, Crest = pts, Toe = pts, BermWidth = berm, IsWorkingWall = false, Closed = true });
        }
        note = $"直线坑线：台阶线 = 现状线（选中 {sel.Count} 条环线" + (flat ? $"，环无标高差 → 按包围面积降序合成标高，级差取模板台阶高 {h:0.#}m" : "，标高取各线 Elevation") + $"，平盘 {berm:0.#}m）。";
        return outList;
    }

    private void RunStraightRoute(IReadOnlyList<BenchLine> benches, TransportConstraintSettings cfg, double roadW, double grade, double turnR, double minSp, bool ccw, (double X, double Y)? bottomSeed)
    {
        var opt = new StraightRampRouteOptions
        {
            GradePct = grade, RoadWidth = roadW, RotationDir = ccw ? +1 : -1,
            MaxContinuousDropM = cfg.MaxContinuousDropM, EaseGradePct = cfg.EaseGradePct, EaseMinLengthM = cfg.EaseMinLengthM,
            AllowSwitchbackFallback = true, MinTurnRadius = turnR, CurveGradePct = Math.Min(grade, 4.0),
            AllowSpiralFallback = true, SpiralMinRadius = turnR, MinSpacingM = minSp,
            CenterlineOffsetM = roadW / 2.0 + cfg.SafetyStrip, MinCurveRadiusM = cfg.EffectiveMinCurveRadiusM(),
            CurveMaxGradePct = cfg.CurveMaxGradePct, MaxResultantGradePct = cfg.MaxResultantGradePct, SuperElevationPct = cfg.MaxSuperelevationPct,
            VerticalCurveTriggerPct = cfg.VerticalCurveTriggerDiffPct, VerticalCurveRadiusM = cfg.MinVerticalCurveRadiusM, MinGradeSectionLengthM = cfg.MinGradeSectionLengthM,
            CurveWidenThresholdM = cfg.CurveWidenThresholdM, VehicleWheelbaseM = cfg.VehicleWheelbase, DesignSpeedKmh = cfg.DesignSpeedKmh, LaneCount = cfg.LaneCount,
            StartFromBottom = bottomSeed.HasValue, StartSeedXY = bottomSeed,
        };
        var rr = Cad.RoadLayout.StraightRampAutoRouter.Route(benches, opt);
        if (!rr.Success)
        {
            _lastRouteCenterlines.Clear();
            StatusMsg.Text = $"直线坑线：无法布线 — {rr.Error}";
            EditEcho($"直线坑线:无法布线 — {rr.Error}", EchoLevel.Error);
            return;
        }
        if (rr.Centerline.Count >= 2) RememberRouteForLanding(new[] { Flatten3(rr.Centerline) }, roadW, grade, "直线坑线");
        else _lastRouteCenterlines.Clear();

        if (rr.CenterlineOffsetAppliedM > 1e-9 || rr.MinCurveRadiusAchievedM > 1e-9)
            EditEcho($"  线形:中线内移 {rr.CenterlineOffsetAppliedM:0.#}m，平曲线 R≥{cfg.EffectiveMinCurveRadiusM():0.#}m（实达 {rr.MinCurveRadiusAchievedM:0.#}m{(rr.CurveRadiusViolations > 0 ? $"，{rr.CurveRadiusViolations} 处半径不足" : "")}）", rr.CurveRadiusViolations > 0 ? EchoLevel.Warn : EchoLevel.Info);
        if (rr.CurveGradeUsedPct > 1e-9)
            EditEcho($"  纵断面:直线纵坡 {rr.MaxGradeUsedPct:0.#}%，弯道纵坡 {rr.CurveGradeUsedPct:0.#}%（限{cfg.CurveMaxGradePct:0.#}%），合成坡度 {rr.ResultantGradePct:0.#}%（限{cfg.MaxResultantGradePct:0.#}%）" + (rr.GradeExceedsLimit ? "；⚠ 限坡内降不满总高差，需增长展线" : ""), rr.GradeExceedsLimit ? EchoLevel.Warn : EchoLevel.Info);
        if (rr.VerticalCurveCount > 0 || rr.MinGradeSectionViolations > 0)
            EditEcho($"  竖曲线 {rr.VerticalCurveCount} 条（R≥{cfg.MinVerticalCurveRadiusM:0}m，实达 {rr.MinVerticalRadiusAchievedM:0}m{(rr.VerticalCurveViolations > 0 ? $"，{rr.VerticalCurveViolations} 处不足" : "")}）" + (rr.MinGradeSectionViolations > 0 ? $"；{rr.MinGradeSectionViolations} 处坡长<{cfg.MinGradeSectionLengthM:0}m" : ""), (rr.VerticalCurveViolations > 0 || rr.MinGradeSectionViolations > 0) ? EchoLevel.Warn : EchoLevel.Info);
        if (rr.MaxWideningM > 1e-6 || rr.MaxSuperelevationUsedPct > 1e-6)
            EditEcho($"  横断面:弯道加宽 ≤{rr.MaxWideningM:0.#}m（加宽段 {rr.WidenedLengthM:0}m），超高 ≤{rr.MaxSuperelevationUsedPct:0.#}%");
        string startHint = bottomSeed.HasValue ? "坑底起·自下而上" : "地表起·自上而下";
        string easeHint = cfg.MaxContinuousDropM > 1e-6 ? $",缓坡段 每降{cfg.MaxContinuousDropM:0.#}m 设1段" : "";
        EditEcho($"> 直线坑线({startHint};路宽 B={roadW:0.#}m 限坡 i_max={grade:0.#}% 回头 R≥{turnR:0.#}m 最小间距 {minSp:0.#}m{easeHint};{(ccw ? "逆时针" : "顺时针")}):{benches.Count} 环 → {rr.LevelsTotal} 级");
        foreach (var lv in rr.Levels)
        {
            string ease = lv.EaseSections > 0 ? $" +缓坡×{lv.EaseSections}" : "";
            string tail = !lv.Feasible ? "✗放不下→止步"
                : lv.Form == RampForm.Switchback ? $"↩折返 R={lv.TurnRadius:0.#}m×{lv.Legs}腿" + (lv.PlatformFitsOnBerm ? "" : "(外凸折返台)")
                : lv.Form == RampForm.Spiral ? $"◎螺旋 R={lv.SpiralRadiusM:0.#}m×{lv.SpiralTurns:0.##}圈(圆心 {lv.SpiralCenterX:0.#},{lv.SpiralCenterY:0.#};不落地)"
                : lv.WidthOk ? "✓斜坡道" : "✓斜坡道(平盘窄于路宽)";
            EditEcho($"   L{lv.Index}: ΔH={lv.DeltaH:0.#}m  需展线 {lv.RequiredRunM:0}m{ease} / 下级环周长 {lv.LowerPerimeterM:0}m  → {tail}", lv.Feasible ? EchoLevel.Info : EchoLevel.Warn);
            if (!lv.Feasible && !string.IsNullOrEmpty(lv.Note)) EditEcho($"        止步原因:{lv.Note}", EchoLevel.Warn);
        }
        string spSum = rr.SpiralLegs > 0 ? $" + {rr.SpiralLegs} 段螺旋({rr.SpiralTurnsTotal:0.##} 圈)" : "";
        if (rr.ReachedBottom)
            EditEcho($"✓ 贯通:地表 Z={rr.TopZ:0.#}m ↔ 坑底 Z={rr.BottomZ:0.#}m,共 {rr.StraightLegs} 段斜坡道 + {rr.SwitchbackLegs} 段折返{spSum},总展线≈{rr.TotalRunM:0}m", EchoLevel.Success);
        else
            EditEcho($"⚠ 部分贯通:连通 {rr.LevelsConnected}/{rr.LevelsTotal} 级(止于 Z={rr.ReachedZ:0.#}m;{rr.StraightLegs}斜坡道+{rr.SwitchbackLegs}折返{spSum},展线≈{rr.TotalRunM:0}m)", EchoLevel.Warn);
        if (rr.SpiralLegs > 0) EditEcho($"  注:{rr.SpiralLegs} 段螺旋未入落地缓存 —— {rr.SpiralLandingNote}", EchoLevel.Warn);
        if (rr.OffWallFaceCutOPoints > 0) EditEcho($"  注:{rr.OffWallFaceCutOPoints} 段斜坡道的 O 点承自上一段折返/螺旋的落点,已按最近弧位投影回坡顶线。");

        if (rr.Centerline.Count >= 2)
        {
            BeginChange();
            var pl = new PolylineEntity { Cr = 0.95f, Cg = 0.85f, Cb = 0.30f, LayerName = RoutePreviewLayer, Zs = new List<double>() };
            foreach (var (x, y, z) in rr.Centerline) { pl.Points.Add((x, y)); pl.Zs.Add(z); }
            _scene.Add(pl);
            RefreshScene(); Viewport.ZoomExtents();
            EditEcho("  预览中线已入图(layer『运输坑线_预览』,可选中 / Ctrl+Z 撤销)。");
        }
        StatusMsg.Text = (rr.ReachedBottom ? "✓ 直线坑线：贯通 " : "⚠ 直线坑线：部分贯通 ") + $"{rr.LevelsConnected}/{rr.LevelsTotal} 级 · 斜坡道 {rr.StraightLegs}/折返 {rr.SwitchbackLegs}/螺旋 {rr.SpiralLegs} · 展线≈{rr.TotalRunM:0}m · 中线 {rr.Centerline.Count} 点已入图并入落地缓存（可【坑线落地】）";
    }
}
