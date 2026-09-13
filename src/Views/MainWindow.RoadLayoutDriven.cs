using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad;
using PitMine3D.Kylin.Cad.Draw;
using PitMine3D.Kylin.Cad.Road;
using PitMine3D.Kylin.Cad.RoadLayout;
using PitMine3D.Kylin.Cad.Transport;
using PitMine3D.Kylin.Views.Road;
using BenchLine = PitMine3D.Kylin.Cad.RoadLayout.BenchLine;
using RoadLayoutSolver = PitMine3D.Kylin.Cad.RoadLayout.RoadLayoutSolver;
using StraightRampRouteOptions = PitMine3D.Kylin.Cad.RoadLayout.StraightRampRouteOptions;

namespace PitMine3D.Kylin.Views;

/// <summary>
/// 「运量驱动布线」= 原 MineAssLib <c>CreateRoadLayoutCommand</c>（一键运输系统布置）：
/// 读约束条件设置 + 装卸点台账（源 = 采剥点，汇 = 卸载点）→ 台阶线（批量台阶扩帮缓存 / 图上环线）→ <see cref="RoadLayoutSolver"/>
/// （运量定车道 → 拆线 → 紧凑/均衡/单线三套方案带成本评分）→ <see cref="RoadSchemeCompareWindow"/> 比选（换目标重算）→
/// 「采用」以方案起坡点为种子逐线跑 <see cref="StraightRampAutoRouter"/> 出真中线（路宽按各线车道数算）并写进落地缓存，可直接接【坑线落地】。
/// 求解器/选线器/布线器都是原版逐文件移植（Cad/RoadLayout）。
/// </summary>
public partial class MainWindow
{
    private const string RoadSchemePreviewLayer = "运输方案_预览";
    private const string RoutePreviewLayer = "运输坑线_预览";
    private RoadSchemeCompareWindow? _roadSchemeWin;

    private void RoadLayoutDrivenCmd()
    {
        var cfg = TransportConstraintProfileStore.LoadMirrorOrDefault(UserSettings.Current);
        LoadUnloadPointSet? pts = null;
        try { pts = UserSettings.Current.Get<LoadUnloadPointSet>(LoadUnloadPointSet.SettingsKey); } catch { }
        var sources = new List<LoadingPoint>();
        var sinks = new List<UnloadingPoint>();
        if (pts != null)
            foreach (var p in pts.Points)
            {
                if (p.IsLoading)
                    sources.Add(new LoadingPoint { Id = p.Id, X = p.X, Y = p.Y, Z = p.Z, Level = p.Z, OreTons = p.ThroughputTph * cfg.WorkHoursPerYear, WasteTons = 0 });
                else
                    sinks.Add(new UnloadingPoint { Id = p.Id, X = p.X, Y = p.Y, Z = p.Z, Kind = UnloadKind.Crusher, CapacityTons = 0 });   // 假设②③(无点类型/接收能力可依)
            }
        if (sources.Count == 0)
        {
            StatusMsg.Text = "运量驱动布线：未设采剥点(源)，运量无从估起。";
            EditEcho("运量驱动布线：未设采剥点(源)，运量无从估起。当前版本源没有录入入口（「破碎站位置设置」只管汇），需要源请在「装卸点设置」里补 kind=loading 的点。", EchoLevel.Warn);
            return;
        }
        var benches = ResolveBenchLinesForRouting("运量驱动布线", cfg, out string benchNote);
        if (benches == null) return;
        EditEcho(benchNote);
        EchoSourceSinkAssumptions(sources.Count, sinks.Count, cfg);

        RoadLayoutResult Solve(string objective)
        {
            cfg.Objective = objective;
            return new RoadLayoutSolver().Solve(new RoadLayoutInput { Sources = sources, Sinks = sinks, Constraints = cfg, Benches = benches });
        }
        var result = Solve(cfg.Objective);
        if (!result.Success || result.Schemes.Count == 0)
        {
            StatusMsg.Text = $"运量驱动布线失败：{result.Error ?? "无方案"}";
            EditEcho(StatusMsg.Text, EchoLevel.Warn);
            return;
        }
        EditEcho($"✓ 运量驱动布线：{result.Schemes.Count} 套方案（推荐 {result.Schemes[0].Name}，{result.Schemes[0].Lines.Count} 线）——见比选窗。", EchoLevel.Success);
        StatusMsg.Text = $"运量驱动布线：{result.Schemes.Count} 套方案，推荐「{result.Schemes[0].Name}」（{result.Schemes[0].Lines.Count} 线）· 比选窗可换目标重算 / 采用";
        _roadSchemeWin?.Close();
        var win = new RoadSchemeCompareWindow(result, cfg.Objective, Solve, scheme => AdoptRoadScheme(scheme, cfg, benches));
        _roadSchemeWin = win;
        win.Closed += (_, _) => { if (ReferenceEquals(_roadSchemeWin, win)) _roadSchemeWin = null; };
        win.Show(this);
    }

    private void EchoSourceSinkAssumptions(int nSrc, int nSink, TransportConstraintSettings cfg)
    {
        EditEcho($"  源 {nSrc} 个 / 汇 {nSink} 个（读自装卸点台账）。假设①运量 = 吞吐 t/h × 年作业 {cfg.WorkHoursPerYear:0} h，一律按矿石估；"
               + "②装卸点无类型字段 → 汇一律当破碎站；③无接收能力字段 → 汇不参与求解。");
    }

    /// <summary>
    /// 布线用台阶线（原 ResolveBenchLinesForRouting：设计线 = 批量台阶扩帮产出 / 现状线 = 图上折线）。
    /// ① 本会话「批量台阶扩帮」缓存 <see cref="_lastBenchLevels"/>；② 否则选中的 ≥2 条环线；③ 否则全场景 ≥3 点折线。
    /// 图上环线标高相同（二维图）时按包围面积降序合成标高（外圈最高，级差 = 约束里的参考台阶高）。
    /// </summary>
    private List<BenchLine>? ResolveBenchLinesForRouting(string cmdName, TransportConstraintSettings cfg, out string note)
    {
        note = "";
        if (_lastBenchLevels != null && _lastBenchLevels.Count >= 2)
        {
            var list = _lastBenchLevels.Select(lv => new BenchLine
            {
                Level = lv.CrestZ,
                Crest = lv.Crest.Select(p => (p.x, p.y, lv.CrestZ)).ToList(),
                Toe = lv.Toe.Select(p => (p.x, p.y, lv.ToeZ)).ToList(),
                BermWidth = _lastBenchBermW,
                IsWorkingWall = false,
                Closed = lv.Crest.Count >= 3,
            }).OrderByDescending(b => b.Level).ToList();
            note = $"{cmdName}：台阶线取【批量台阶扩帮】缓存（{list.Count} 级，Z {list[^1].Level:0.#}~{list[0].Level:0.#}m，平盘 {_lastBenchBermW:0.#}m）。";
            return list;
        }
        var sel = _selected.OfType<PolylineEntity>().Where(p => p.Points.Count >= 3).ToList();
        var rings = sel.Count >= 2 ? sel : _scene.Entities.OfType<PolylineEntity>().Where(p => p.Visible && _layers.IsShown(p.LayerName) && p.Points.Count >= 3).ToList();
        if (rings.Count < 2)
        {
            StatusMsg.Text = $"{cmdName}：没有台阶线 —— 请先【批量台阶扩帮】（或选中 ≥2 条同心台阶环）。";
            EditEcho(StatusMsg.Text, EchoLevel.Warn);
            return null;
        }
        rings.Sort((a, b) => Math.Abs(BenchLines.SignedArea(b.Points)).CompareTo(Math.Abs(BenchLines.SignedArea(a.Points))));
        bool flat = rings.Select(r => r.Elevation).Distinct().Count() <= 1;
        double h = cfg.RefBenchHeight > 0 ? cfg.RefBenchHeight : 15;
        var outList = new List<BenchLine>();
        for (int k = 0; k < rings.Count; k++)
        {
            double z = flat ? (rings.Count - 1 - k) * h : rings[k].Elevation;
            var pts = rings[k].Points.Select(p => (p.x, p.y, z)).ToList();
            outList.Add(new BenchLine { Level = z, Crest = pts, Toe = pts, BermWidth = cfg.MinWorkingBenchWidth, IsWorkingWall = false, Closed = true });
        }
        note = $"{cmdName}：台阶线取图上 {rings.Count} 条环线（{(sel.Count >= 2 ? "当前选集" : "全场景")}）"
             + (flat ? $"，环无标高差 → 按包围面积降序合成标高（级差取参考台阶高 {h:0.#}m）" : "，标高取各线 Elevation") + "。";
        return outList;
    }

    /// <summary>方案链高亮（原 HighlightRoadScheme）：起坡点十字 + 沿帮中心线，落层「运输方案_预览」。</summary>
    private void HighlightRoadScheme(RoadLayoutScheme scheme)
    {
        BeginChange();
        foreach (var e in _scene.Entities.Where(x => x.LayerName == RoadSchemePreviewLayer).ToList()) _scene.Remove(e);
        const double s = 6.0;
        int portals = 0, clPts = 0;
        foreach (var line in scheme.Lines)
        {
            (double X, double Y, double Z)? prev = null;
            foreach (var sg in line.Segments)
            {
                var cl = sg.Centerline;
                if (cl == null || cl.Count == 0) continue;
                var p = cl[0];
                _scene.Add(new LineEntity { X0 = p.X - s, Y0 = p.Y, X1 = p.X + s, Y1 = p.Y, Elevation = p.Z, LayerName = RoadSchemePreviewLayer, Cr = 1f, Cg = 0.35f, Cb = 0f });
                _scene.Add(new LineEntity { X0 = p.X, Y0 = p.Y - s, X1 = p.X, Y1 = p.Y + s, Elevation = p.Z, LayerName = RoadSchemePreviewLayer, Cr = 1f, Cg = 0.35f, Cb = 0f });
                var pl = new PolylineEntity { LayerName = RoadSchemePreviewLayer, Cr = 1f, Cg = 0.35f, Cb = 0f, Zs = new List<double>() };
                if (prev is { } q) { pl.Points.Add((q.X, q.Y)); pl.Zs.Add(q.Z); }
                foreach (var c in cl) { pl.Points.Add((c.X, c.Y)); pl.Zs.Add(c.Z); }
                if (pl.Points.Count >= 2) _scene.Add(pl);
                prev = cl[cl.Count - 1];
                portals++; clPts += cl.Count;
            }
        }
        RefreshScene();
        if (portals > 0)
            EditEcho($"✓ 「{scheme.Name}」方案链已入图「{RoadSchemePreviewLayer}」：{scheme.Lines.Count} 线 / {portals} 起坡点 / 中心线 {clPts} 点" + (clPts > portals ? "（沿帮展线）。" : "（上游只给了起坡点 → 退回起坡点折线画法）。"), EchoLevel.Success);
        else EditEcho($"采用「{scheme.Name}」：方案里没有可画的起坡点/中心线。");
    }

    private static (double X, double Y, double Z)? FirstPortalOf(RoadLine line)
    {
        foreach (var sg in line.Segments)
            if (sg.Centerline != null && sg.Centerline.Count > 0) return sg.Centerline[0];
        return null;
    }

    /// <summary>采用方案（原 AdoptRoadScheme）：逐线以起坡点为种子跑自动布线出真中线 → 预览入图 + 写落地缓存。</summary>
    private void AdoptRoadScheme(RoadLayoutScheme scheme, TransportConstraintSettings cfg, IReadOnlyList<BenchLine> benches)
    {
        HighlightRoadScheme(scheme);
        if (benches == null || benches.Count < 2) { EditEcho("采用方案:台阶线不可用,出不了真中线(请先【批量台阶扩帮】)。", EchoLevel.Warn); return; }
        int stale = _scene.Entities.Count(x => x.LayerName == RoutePreviewLayer);
        BeginChange();
        if (stale > 0)
        {
            foreach (var e in _scene.Entities.Where(x => x.LayerName == RoutePreviewLayer).ToList()) _scene.Remove(e);
            EditEcho($"  (已清掉「{RoutePreviewLayer}」上 {stale} 个旧中线预览实体：落地缓存换成本方案了)");
        }
        double RoadWidthForLanes(int lanes) { int save = cfg.LaneCount; try { cfg.LaneCount = Math.Max(1, lanes); return cfg.RoadWidthPreview(); } finally { cfg.LaneCount = save; } }

        var cAll = new List<double[]>();
        var cached = new List<((double X, double Y) Seed, double Width)>();
        int routed = 0, through = 0, sbLegs = 0, spLegs = 0, overlapped = 0, straightSegs = 0;
        double spTurns = 0, totalRunM = 0, baseWidth = 0, gradePct = cfg.MaxGradePct;
        string spNote = "";
        EditEcho($"> 采用「{scheme.Name}」：{scheme.Lines.Count} 条线逐条出真中线(限坡 i_max={gradePct:0.#}%，路宽按各线车道数算，其余约束取自「约束条件设置」)…");
        foreach (var line in scheme.Lines)
        {
            var seed = FirstPortalOf(line);
            if (seed == null) { EditEcho($"   {line.Id}: 方案里没有起坡点(各段中心线都空) → 出不了中线,跳过。", EchoLevel.Warn); continue; }
            double bw = RoadWidthForLanes(line.LaneCount);
            var opt = new StraightRampRouteOptions
            {
                GradePct = gradePct, RoadWidth = bw, RotationDir = +1,
                MaxContinuousDropM = cfg.MaxContinuousDropM, EaseGradePct = cfg.EaseGradePct, EaseMinLengthM = cfg.EaseMinLengthM,
                AllowSwitchbackFallback = true, MinTurnRadius = cfg.TruckTurnRadius, CurveGradePct = Math.Min(gradePct, 4.0),
                AllowSpiralFallback = true, SpiralMinRadius = cfg.TruckTurnRadius, MinSpacingM = bw,
                CenterlineOffsetM = bw / 2.0 + cfg.SafetyStrip, MinCurveRadiusM = cfg.EffectiveMinCurveRadiusM(),
                CurveMaxGradePct = cfg.CurveMaxGradePct, MaxResultantGradePct = cfg.MaxResultantGradePct, SuperElevationPct = cfg.MaxSuperelevationPct,
                VerticalCurveTriggerPct = cfg.VerticalCurveTriggerDiffPct, VerticalCurveRadiusM = cfg.MinVerticalCurveRadiusM, MinGradeSectionLengthM = cfg.MinGradeSectionLengthM,
                CurveWidenThresholdM = cfg.CurveWidenThresholdM, VehicleWheelbaseM = cfg.VehicleWheelbase, DesignSpeedKmh = cfg.DesignSpeedKmh,
                LaneCount = Math.Max(1, line.LaneCount), StartFromBottom = false, StartSeedXY = (seed.Value.X, seed.Value.Y),
            };
            var rr = Cad.RoadLayout.StraightRampAutoRouter.Route(benches, opt);
            if (!rr.Success) { EditEcho($"   {line.Id}: 出中线失败 — {rr.Error}", EchoLevel.Error); continue; }
            routed++;
            if (rr.ReachedBottom) through++;
            totalRunM += rr.TotalRunM;
            if (rr.Centerline.Count >= 2)
            {
                var pl = new PolylineEntity { Cr = 0.95f, Cg = 0.85f, Cb = 0.30f, LayerName = RoutePreviewLayer, Zs = new List<double>() };
                foreach (var (x, y, z) in rr.Centerline) { pl.Points.Add((x, y)); pl.Zs.Add(z); }
                _scene.Add(pl);
            }
            string status = rr.ReachedBottom ? $"✓贯通(地表 Z={rr.TopZ:0.#}m ↔ 坑底 Z={rr.BottomZ:0.#}m)" : $"⚠部分贯通 {rr.LevelsConnected}/{rr.LevelsTotal} 级(止于 Z={rr.ReachedZ:0.#}m)";
            bool overlap = cached.Any(c => Math.Sqrt((c.Seed.X - seed.Value.X) * (c.Seed.X - seed.Value.X) + (c.Seed.Y - seed.Value.Y) * (c.Seed.Y - seed.Value.Y)) < Math.Max(1.0, Math.Max(c.Width, bw)));
            if (overlap)
            {
                overlapped++;
                EditEcho($"   {line.Id}: {line.LaneCount} 车道 · 路宽 {bw:0.#}m · {status} · 展线≈{rr.TotalRunM:0}m —— 起坡点与已缓存线重合，只出中线预览、不入落地缓存(免同一坡面被切两遍)。", EchoLevel.Warn);
                continue;
            }
            sbLegs += rr.SwitchbackLegs; spLegs += rr.SpiralLegs; spTurns += rr.SpiralTurnsTotal; straightSegs += rr.StraightLegs;
            if (rr.SpiralLegs > 0 && spNote.Length == 0) spNote = rr.SpiralLandingNote;
            if (cached.Count == 0) baseWidth = bw;
            if (rr.Centerline.Count >= 2) cAll.Add(Flatten3(rr.Centerline));
            cached.Add(((seed.Value.X, seed.Value.Y), bw));
            EditEcho($"   {line.Id}: {line.LaneCount} 车道 · 路宽 {bw:0.#}m · {status} · 展线≈{rr.TotalRunM:0}m · 斜坡道 {rr.StraightLegs} 段 + 折返 {rr.SwitchbackLegs} 段"
                   + (rr.SpiralLegs > 0 ? $" + 螺旋 {rr.SpiralLegs} 段/{rr.SpiralTurnsTotal:0.##} 圈" : ""), rr.ReachedBottom ? EchoLevel.Info : EchoLevel.Warn);
        }
        RefreshScene();
        if (routed == 0)
        {
            _lastRouteCenterlines.Clear();
            StatusMsg.Text = $"采用「{scheme.Name}」：一条中线都没出来，落地缓存已清空。";
            EditEcho(StatusMsg.Text + "(【坑线落地】暂无可落地几何)", EchoLevel.Error);
            return;
        }
        RememberRouteForLanding(cAll, baseWidth > 1e-9 ? baseWidth : cfg.RoadWidthPreview(), gradePct, "运量驱动布线·" + scheme.Name);
        StatusMsg.Text = $"✓ 已采用「{scheme.Name}」：{scheme.Lines.Count} 线 → 出中线 {routed} 条(贯通 {through} 条)，总展线≈{totalRunM:0}m；落地缓存 {cAll.Count} 条中线(基宽 {_lastRouteRoadWidth:0.#}m 纵坡 {gradePct:0.#}%)"
                       + (cAll.Count > 0 ? "，现在可直接点【坑线落地】。" : "。");
        EditEcho(StatusMsg.Text + $" 斜坡道 {straightSegs} 段 / 折返 {sbLegs} 段 / 螺旋 {spLegs} 段{(spLegs > 0 ? $"({spTurns:0.##} 圈)" : "")}", cAll.Count > 0 ? EchoLevel.Success : EchoLevel.Warn);
        if (overlapped > 0) EditEcho($"  另有 {overlapped} 条线起坡点与已缓存线重合，只画了预览。", EchoLevel.Warn);
        if (spNote.Length > 0) EditEcho("  " + spNote);
    }
}
