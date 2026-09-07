using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using PitMine3D.Kylin.Data;
using Xunit;
using static PitMine3D.Kylin.Data.GeoDbViews;

namespace PitMine3D.Kylin.Tests;

/// <summary>「设备管理」组 9 个窗口的数据层回归(GeoDbViews.Equipment, 对 SQLite 种子库)。</summary>
public class GeoDbViewsEquipmentTests
{
    // ─── ① 设备台账(设备信息管理) ───
    [Fact]
    public void Ledger_loads_seed_fleet_with_model_params_and_categories()
    {
        using var db = GeoDatabase.OpenSeeded();
        var items = EqLoadLedger(db.Connection);
        Assert.True(items.Count > 100, $"种子台账 510 台量级, 实得 {items.Count}");
        Assert.All(items, i => Assert.Contains(i.Category, EqCategories));
        Assert.Contains(items, i => i.Category == "Shovel" && i.BucketCapacityM3 != "");   // 型号级斗容自 equipment_model
        Assert.Equal(items.OrderBy(i => i.Id, StringComparer.Ordinal).Select(i => i.Id), items.Select(i => i.Id));
        Assert.Contains(items, i => i.DisplayLabel.Contains(" · "));
    }

    [Fact]
    public void Ledger_save_upserts_new_and_deletes_missing()
    {
        using var db = GeoDatabase.OpenSeeded();
        var items = EqLoadLedger(db.Connection);
        int before = items.Count;
        var removed = items[0];
        items.RemoveAt(0);
        items.Add(new EquipmentLedgerItem { Id = "T-9999", Category = "Truck", Model = "730E", Status = "封存", CumulativeHours = "1234", Notes = "测试", AcquisitionDate = new DateTimeOffset(2020, 5, 1, 0, 0, 0, TimeSpan.Zero) });
        var (deleted, upserted) = EqSaveLedger(db.Connection, items);
        Assert.Equal(1, deleted);
        Assert.Equal(before, upserted);
        var back = EqLoadLedger(db.Connection);
        Assert.Equal(before, back.Count);
        Assert.DoesNotContain(back, i => i.Id == removed.Id);
        var t = back.Single(i => i.Id == "T-9999");
        Assert.Equal("封存", t.Status); Assert.Equal("1234", t.CumulativeHours); Assert.Equal("2020-05-01", t.AcquisitionDate!.Value.ToString("yyyy-MM-dd"));
        Assert.Equal("111", t.BucketCapacityM3);   // 730E 字典斗容 111 → 保存后回读仍取字典
    }

    [Fact]
    public void Compatibility_and_frame_keys_follow_original_rules()
    {
        Assert.Equal("4100xpc", EqNormalizeKey("4100XPC"));
        Assert.Equal("wk-35", EqNormalizeKey("WK-35"));
        Assert.Equal("watertruck", EqCategoryFileKey("WaterTruck"));
        Assert.Equal(new[] { "ph2800", "shovel" }, EqFrameKeyCandidates("PH2800", "Shovel"));
        Assert.Equal(new[] { "truck" }, EqFrameKeyCandidates("", "Truck"));
        Assert.Equal("Other", EqParseCategory("bogus")); Assert.Equal("Shovel", EqParseCategory("shovel"));
        Assert.StartsWith("✓ 适配", EqEvaluateCompatibility("4100XPC", "Shovel", 15.0).text);
        Assert.StartsWith("⚠ 超能力", EqEvaluateCompatibility("PH2800", "Shovel", 13.0).text);
        Assert.Equal("#666666", EqEvaluateCompatibility("730E", "Truck", 30).color);
        Assert.Equal("矿用卡车", EqCategoryDisplay("Truck")); Assert.Equal("矿卡", EqCatCn("Truck"));
    }

    [Fact]
    public void Frame_sequence_resolves_from_repo_assets_with_category_fallback()
    {
        // src/Assets/Models3D 随程序输出; 从测试输出目录向上找源码目录
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir, "src", "Assets", "Models3D"))) dir = System.IO.Path.GetDirectoryName(dir);
        if (dir == null) return;   // 环境无资源目录: 跳过
        string assets = System.IO.Path.Combine(dir, "src", "Assets");
        var exact = EqResolveFrames(assets, "4100XPC", "Shovel");
        Assert.NotNull(exact); Assert.True(exact!.Count >= 2); Assert.Contains("4100xpc", exact[0]);
        var fallback = EqResolveFrames(assets, "2800XPB", "Shovel");
        Assert.NotNull(fallback); Assert.Contains("shovel", fallback![0]);
        Assert.Null(EqResolveFrames(assets, "XYZ", "Other"));
    }

    [Fact]
    public void Active_working_face_links_to_equipment_when_present()
    {
        using var db = GeoDatabase.OpenSeeded();
        var linked = db.Connection.Query<string>("SELECT equipment_id FROM working_face WHERE status='active' AND equipment_id IS NOT NULL LIMIT 1").FirstOrDefault();
        if (linked == null) return;
        var face = EqActiveWorkingFace(db.Connection, linked);
        Assert.NotNull(face); Assert.True(face!.BenchHeightM > 0); Assert.False(string.IsNullOrEmpty(face.FaceCode));
        Assert.Null(EqActiveWorkingFace(db.Connection, "NO-SUCH-EQUIPMENT"));
    }

    // ─── ② 数据源 ───
    [Fact]
    public void Capacity_kpi_fault_production_load_seed_magnitudes()
    {
        using var db = GeoDatabase.OpenSeeded();
        var cap = EqLoadCapacity(db.Connection);
        var kpi = EqLoadKpi(db.Connection);
        var faults = EqLoadFaults(db.Connection);
        var prod = EqLoadProduction(db.Connection);
        Assert.True(cap.Count > 1000, $"capacity {cap.Count}");
        Assert.True(kpi.Count > 500, $"kpi {kpi.Count}");
        Assert.True(faults.Count > 50, $"faults {faults.Count}");
        Assert.True(prod.Count > 200, $"prod {prod.Count}");
        Assert.All(cap, c => Assert.True(c.OutputM3 >= 0));
        Assert.Contains(cap, c => c.Category == "Shovel" && c.Model != "");
        Assert.All(kpi, k => Assert.InRange(k.Availability, 0, 1));
        Assert.Contains(kpi, k => k.Year == 2023 && k.Month == 12);   // 班次效能预测基准月
        Assert.All(faults, f => Assert.True(f.Date.Year >= 2020));
        Assert.Contains(prod, p => p.HasFault && p.FaultReason != "");
        Assert.Equal("2026-05-01", prod.First(p => p.EquipmentId == "1662").DateText);
        var r = new EqProductionRecord { DateText = "2026-06-02" };
        Assert.Equal(new DateTime(2026, 6, 2), r.Date);
    }

    [Fact]
    public void Production_save_rewrites_whole_table()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = EqLoadProduction(db.Connection);
        int n = rows.Count;
        rows.RemoveAt(0);
        rows.Add(new EqProductionRecord { EquipmentId = "1662", Date = new DateTime(2030, 1, 1), Shift = "A", OutputM3 = 100, WorkHours = 8, FaultHours = 0.5, FaultReason = "测试" });
        Assert.Equal(n, EqSaveProduction(db.Connection, rows));
        var back = EqLoadProduction(db.Connection);
        Assert.Equal(n, back.Count);
        var t = back.Single(p => p.Date == new DateTime(2030, 1, 1));
        Assert.Equal("测试", t.FaultReason); Assert.True(t.HasFault);
    }

    [Fact]
    public void Production_filter_and_analytics()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = EqLoadProduction(db.Connection);
        var a = EqAnalyzeProduction(rows);
        Assert.Equal(rows.Count, a.Rows);
        Assert.True(a.TotalOutWan > 0 && a.Days > 0 && a.EquipmentCount > 0);
        Assert.InRange(a.EffRate + a.FaultRate, 0.999, 1.001);
        Assert.Equal(new[] { "A", "B", "C" }, a.ByShift.Select(s => s.Shift).ToArray());
        Assert.True(a.TopEquip.Count <= 10 && a.TopEquip.Count > 0);
        Assert.Equal(a.TopEquip.OrderByDescending(t => t.Wan).Select(t => t.EquipmentId), a.TopEquip.Select(t => t.EquipmentId));
        if (a.FaultPareto.Count > 0) { Assert.InRange(a.FaultPareto[^1].CumPct, 99.9, 100.1); Assert.True(a.FaultPareto[0].Hours >= a.FaultPareto[^1].Hours); }
        var onlyA = EqFilterProduction(rows, "全部", "A", false);
        Assert.All(onlyA, r => Assert.Equal("A", r.Shift));
        var faultOnly = EqFilterProduction(rows, "1662", "全部", true);
        Assert.All(faultOnly, r => { Assert.Equal("1662", r.EquipmentId); Assert.True(r.FaultHours > 0); });
        Assert.Equal("统计范围:当前筛选无数据", EqAnalyzeProduction(new List<EqProductionRecord>()).Scope);
    }

    // ─── ③ 编组 ───
    [Fact]
    public void Dispatch_rules_join_model_params_and_inventory()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rules = EqLoadDispatchRules(db.Connection);
        Assert.True(rules.Count >= 10);
        var r = rules.First(x => x.ShovelModel == "2800XPB" && x.TruckModel == "730E");
        Assert.Equal(35.2, r.ShovelBucketM3, 3); Assert.Equal(186, r.TruckPayloadT, 3); Assert.Equal(3, r.RecommendedTruckCount); Assert.Equal(61, r.EfficiencyScore);
        var fr = r.ToFleetRule();
        Assert.Equal(r.CycleTimeMin, fr.CycleTimeMin); Assert.Equal(r.TruckPayloadT, fr.TruckPayloadT);
        var inv = EqInventoryByModel(db.Connection);
        Assert.True(inv.Count > 10); Assert.True(inv.Values.Sum() > 100);
        double eta = EqAverageEfficiency(EqLoadKpi(db.Connection));
        Assert.InRange(eta, 0.4, 0.95);
        Assert.Equal(0.8, EqAverageEfficiency(new List<EqKpiRow>()));
        Assert.Equal("#388E3C", EqScoreColor(85)); Assert.Equal("#F57C00", EqScoreColor(70)); Assert.Equal("#C62828", EqScoreColor(69));
        var opt = FleetOptimizer.Optimize(new FleetOptInput { DailyTargetM3 = 260e4 / 30, HaulDistanceKm = 1.82, Efficiency = eta, Rules = rules.Select(x => x.ToFleetRule()).ToList(), ShovelInventory = inv });
        Assert.True(opt.Groups.Count > 0); Assert.True(opt.TotalDailyM3 > 0);
    }

    // ─── ④ 机群总览 ───
    [Fact]
    public void Fleet_cockpit_traffic_light_and_bottleneck()
    {
        using var db = GeoDatabase.OpenSeeded();
        var r = EqComputeFleetCockpit(db.Connection);
        Assert.True(r.HasData);
        Assert.True(r.InRoster >= r.Total && r.Total > 50);
        Assert.Equal(r.Total, r.Green + r.Yellow + r.Red);
        Assert.InRange(r.AvgOee, 0.2, 1.0);
        Assert.Contains(r.NeckName, new[] { "可用率", "作业率", "利用率" });
        Assert.InRange(r.NeckPass, 0, 100);
        Assert.True(r.Watch.Count == r.Yellow + r.Red);
        Assert.All(r.Watch, w => Assert.True(w.Severity >= 1 && w.KeyIssue.Length > 0 && w.AvailPct.EndsWith("%")));
        // 排序: 严重度降序, 同级可用率升序
        for (int i = 1; i < r.Watch.Count; i++) Assert.True(r.Watch[i - 1].Severity >= r.Watch[i].Severity);
        Assert.True(r.CategoryPass.Count >= 2);
        for (int i = 1; i < r.CategoryPass.Count; i++) Assert.True(r.CategoryPass[i - 1].PassPct <= r.CategoryPass[i].PassPct);
        Assert.Contains(r.VerdictIcon, new[] { "🟢", "🟡", "🔴" });
        Assert.Contains("瓶颈", r.Headline + r.Action);
        Assert.Contains("截至 2026", r.AsOfText);
        Assert.True(r.FleetCapWan > 0);
    }

    // ─── ⑤ 数据分析 ───
    [Fact]
    public void Analysis_period_filter_stats_weibull_and_cards()
    {
        using var db = GeoDatabase.OpenSeeded();
        var kpiAll = EqLoadKpi(db.Connection);
        var opts = EqPeriodOptions(kpiAll);
        Assert.Equal("近 12 个月", opts[1]); Assert.Contains("2026 年", opts);
        var id = kpiAll[0].EquipmentId;
        var dev = kpiAll.Where(k => k.EquipmentId == id).OrderBy(k => k.Year).ThenBy(k => k.Month).ToList();
        Assert.True(EqApplyPeriod(dev, "近 6 个月").Count <= 6);
        Assert.All(EqApplyPeriod(dev, "2025 年"), k => Assert.Equal(2025, k.Year));
        Assert.Equal(dev.Count, EqApplyPeriod(dev, "全部").Count);

        Assert.Equal(1, EqCorrelate(new double[] { 1, 2, 3 }, new double[] { 2, 4, 6 }), 9);
        Assert.Equal(0, EqCorrelate(new double[] { 1, 1, 1 }, new double[] { 2, 4, 6 }));
        var (slope, icpt) = EqLinearRegression(new double[] { 0, 1, 2 }, new double[] { 1, 3, 5 });
        Assert.Equal(2, slope, 9); Assert.Equal(1, icpt, 9);
        Assert.Equal(1, EqStdDev(new double[] { 1, 2, 3 }), 9);
        var (beta, eta, ok) = EqWeibullFit(new double[] { 10, 12, 15, 20, 30, 45 });
        Assert.True(ok); Assert.True(beta > 0 && eta > 0);
        Assert.False(EqWeibullFit(new double[] { 3, 4 }).ok);

        var faults = EqLoadFaults(db.Connection).Where(f => f.EquipmentId == id).ToList();
        var rel = EqReliabilityCards(dev, faults, dev[0].Model);
        Assert.True(rel.Count >= 3); Assert.Contains(rel, c => c.Title.Contains("MTBF")); Assert.Contains(rel, c => c.Title.Contains("Weibull"));
        var (outputs, factors) = EqFactorObservations(dev, faults);
        Assert.Equal(dev.Count, outputs.Length); Assert.Equal(5, factors.Count);
        Assert.Equal(5, EqFactorContributions(outputs, factors).Count);
        var (labels, m) = EqCorrelationMatrix(outputs, factors);
        Assert.Equal(6, labels.Count); Assert.Equal("产能", labels[^1]); Assert.Equal(1, m[1, 1], 9);   // 作业小时 vs 自身
        var sug = EqSuggestionCards(dev, faults, outputs, factors);
        Assert.True(sug.Count >= 1);
        Assert.Equal("⛏", EqShiftIcon("4100XPC")); Assert.Equal("🚛", EqShiftIcon("730E"));
    }

    [Fact]
    public void Shift_day_stats_follow_original_formulas()
    {
        var shifts = new List<EqProductionRecord>
        {
            new() { Shift = "A", OutputM3 = 8000, WorkHours = 8, FaultHours = 0 },
            new() { Shift = "B", OutputM3 = 6000, WorkHours = 6, FaultHours = 2 },
        };
        var s = EqShiftDay(shifts, "4100XPC");
        Assert.Equal(14, s.TotalWork); Assert.Equal(2, s.TotalFault); Assert.Equal(140, s.LoadCount);
        Assert.Equal(1000, s.AvgEff, 6); Assert.Equal(1000, s.PeakEff, 6);
        Assert.Equal((24 - 14) * 60 - 2 * 60, s.WaitMin, 6);
        Assert.Equal(8, s.Ineffective, 6); Assert.True(s.AnyFault); Assert.Equal(100, s.AvgPerLoad, 6);
    }

    // ─── ⑥ 班次效能预测 ───
    [Fact]
    public void Shift_forecast_stats_entropy_scores_and_verdicts()
    {
        using var db = GeoDatabase.OpenSeeded();
        var prod = EqLoadProduction(db.Connection); var kpi = EqLoadKpi(db.Connection); var faults = EqLoadFaults(db.Connection);
        var w = EqFleetEntropyWeights(prod, kpi, faults);
        Assert.Equal(5, w.Length); Assert.Equal(1.0, w.Sum(), 6); Assert.All(w, x => Assert.InRange(x, 0, 1));
        var id = kpi.First(k => k.Year == 2023 && k.Month == 12 && prod.Any(p => p.EquipmentId == k.EquipmentId && p.WorkHours > 0)).EquipmentId;
        var valid = prod.Where(p => p.EquipmentId == id && p.WorkHours > 0).ToList();
        var st = EqComputeStats(valid);
        Assert.True(st.Max >= st.P95 && st.P95 >= st.Mean * 0.5 && st.Min <= st.P5 && st.Cv >= 0);
        var dims = EqFiveDims(id, prod, kpi, faults);
        Assert.NotNull(dims); Assert.All(dims!, d => Assert.InRange(d, 0, 1));
        var (labels, counts) = EqHistogram(valid.Select(v => v.OutputM3).ToList(), st.Min, st.Max);
        Assert.Equal(10, counts.Length); Assert.Equal(valid.Count, counts.Sum());
        var fr = ForecastModels.Forecast(valid.Select(v => v.OutputM3).ToList(), 6);
        var (p10, p50, p90, meet) = EqProbabilistic(valid.Select(v => v.OutputM3).ToList(), fr.Next);
        Assert.True(p10 <= p50 && p50 <= p90); Assert.InRange(meet, 0, 100);
        Assert.Equal("Shovel", EqCategoryOfModel("4100XPC")); Assert.Equal("Truck", EqCategoryOfModel("930E")); Assert.Equal("Drill", EqCategoryOfModel("DMH90"));
        Assert.Equal(1, EqCategoryOrder("Shovel")); Assert.Equal("m", EqUnitFor("DML")); Assert.Equal("m³", EqUnitFor("730E"));
        Assert.Equal("🟢 低", EqRiskLevel(0.1, 0).text); Assert.Equal("🔴 高", EqRiskLevel(0.5, 2).text);
        var kpiRow = new EqKpiRow { Availability = 0.9, ActualRunRate = 0.8 };
        var stats = new EqShiftStats(9000, 900, 7000, 10000, 0.1, 7500, 9800);
        var (score, hint) = EqPotentialScore(kpiRow, stats, 9000, 0);
        Assert.InRange(score, 60, 100); Assert.False(string.IsNullOrEmpty(hint));
        var (o, s2, a, e, r) = EqFiveScores(stats, kpiRow, 2);
        Assert.Equal(90, o); Assert.Equal(90, s2); Assert.Equal(90, a); Assert.Equal(80, e); Assert.Equal(84, r);
        Assert.StartsWith("✅", EqDispatchVerdict(0.1, 0.9).verdict); Assert.StartsWith("🔴", EqDispatchVerdict(0.5, 0.9).verdict);
        var ew = EqEntropyWeights(new List<double[]> { new[] { 0.9, 0.5 }, new[] { 0.9, 0.1 }, new[] { 0.9, 0.9 } });
        Assert.True(ew[1] > ew[0]);   // 离散度大的指标权重高
    }

    // ─── ⑦ 效能预测 What-if ───
    [Fact]
    public void Forecast_baseline_benchmark_and_conclusion()
    {
        using var db = GeoDatabase.OpenSeeded();
        var cap = EqLoadCapacity(db.Connection); var kpi = EqLoadKpi(db.Connection);
        var sums = EqEquipmentSummaries(cap);
        Assert.True(sums.Count > 50);
        var sel = sums.First(s => s.Model == "4100XPC");
        var b = EqForecastBaselineOf(cap, kpi, sel);
        Assert.True(b.OutputWan > 0); Assert.InRange(b.Availability, 0, 1); Assert.True(b.PlanHours > 0); Assert.Equal("4100XPC", b.Model);
        var r = EfficiencyWhatIf.Simulate(b.OutputWan, b.FaultHours / b.PlanHours, 0.4, 0.15, 0.10, 0.15);
        Assert.True(r.Simulated > b.OutputWan);
        var (bench, hint) = EqPeerBenchmark(cap, "4100XPC", r.Simulated * 10);
        Assert.Equal("达到型号 Top 10%", bench); Assert.StartsWith("P90", hint);
        Assert.Equal("样本不足", EqPeerBenchmark(cap, "NO-MODEL", 1).text);
        var text = EqForecastConclusion(b.OutputWan, r.C1, r.C2, r.C3, r.C4, 40, 15, 10, 15, r.ActualGain, r.Delta, r.Simulated);
        Assert.Contains("4 项措施", text); Assert.Contains("年化效益", text); Assert.DoesNotContain("⚠️", text);
        Assert.StartsWith("尚未启动", EqForecastConclusion(b.OutputWan, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, b.OutputWan));
        Assert.Contains("> 50%", EqForecastConclusion(b.OutputWan, 0.1, 0, 0, 0, 60, 0, 0, 0, 0.085, 1, b.OutputWan + 1));
    }

    // ─── ⑧ 设备能力 ───
    [Fact]
    public void Capability_yearly_annualized_peers_verdict_and_diagnosis()
    {
        using var db = GeoDatabase.OpenSeeded();
        var cap = EqLoadCapacity(db.Connection); var kpi = EqLoadKpi(db.Connection);
        var sel = EqEquipmentSummaries(cap).First(s => s.Model == "4100XPC");
        var recs = cap.Where(c => c.EquipmentId == sel.EquipmentId).OrderBy(c => c.Year).ThenBy(c => c.Month).ToList();
        var yearly = EqYearlyAnnualized(recs);
        Assert.True(yearly.Count >= 3);
        Assert.Contains(yearly, y => y.Year == 2026 && y.Partial);   // 2026 仅 1-5 月 → 年化
        var y26 = yearly.First(y => y.Year == 2026);
        int months = recs.Where(r => r.Year == 2026).Select(r => r.Month).Distinct().Count();
        Assert.Equal(recs.Where(r => r.Year == 2026).Sum(r => r.OutputM3) * 12.0 / months, y26.Total, 3);
        var k = EqCapabilityKpisOf(cap, sel, recs);
        Assert.True(k.PeakWan >= k.LatestWan && k.HasCagr && k.Rank >= 1 && k.PeerCount >= k.Rank);
        var peers = EqPeerLatestOutput(cap, "4100XPC");
        Assert.True(peers.Count >= 2); Assert.All(peers, p => Assert.True(p.Wan > 0));
        var stats = EqCategoryModelStats(cap, "Shovel", EqLatestAvailByEq(kpi));
        Assert.Contains(stats, s => s.Model == "4100XPC");
        for (int i = 1; i < stats.Count; i++) Assert.True(stats[i - 1].OutWan >= stats[i].OutWan);
        var latestKpi = kpi.Where(x => x.EquipmentId == sel.EquipmentId).OrderByDescending(x => x.Year).ThenByDescending(x => x.Month).FirstOrDefault();
        var (icon, color, headline, action) = EqCapabilityVerdict(sel, recs, latestKpi);
        Assert.Contains(icon, new[] { "🟢", "🟡", "🔴" }); Assert.Contains("4100XPC", headline); Assert.Contains("CAGR", action);
        var w = EqLossWaterfall(recs, latestKpi, out var note);
        if (latestKpi != null) { Assert.NotNull(w); Assert.True(w!.Value.theo >= w.Value.actual); Assert.Contains("主控短板", w.Value.summary); }
        Assert.Null(EqLossWaterfall(recs, null, out note)); Assert.Contains("暂无 KPI", note);
        var diag = EqCapabilityDiagnosis(cap, sel, recs);
        Assert.True(diag.Count >= 3); Assert.Contains(diag, c => c.Title.StartsWith("CAGR"));
        var rates = EqYoyRates(yearly);
        Assert.Equal(yearly.Count - 1, rates.Count);
        Assert.Single(EqCapabilityDiagnosis(cap, sel, recs.Where(r => r.Year == 2023).ToList()));   // <2 年 → 样本不足
    }

    // ─── ⑨ 数据导入导出中心 ───
    [Fact]
    public void Import_specs_templates_and_export_columns()
    {
        var specs = EqImportSpecs();
        Assert.Equal(new[] { "设备台账", "月度产能", "月度可用率(KPI)", "生产班次记录", "故障记录" }, specs.Select(s => s.Name).ToArray());
        Assert.All(specs, s => { Assert.Equal(s.Headers.Length, s.Example.Length); Assert.All(s.Required, r => Assert.Contains(r, s.Headers)); });
        var tpl = EqTemplateCsv(specs[1]);
        Assert.StartsWith("﻿设备编号,年,月,产量_m3\n3001,2026,5,6051898\n", tpl);
        using var db = GeoDatabase.OpenSeeded();
        foreach (var s in specs)
        {
            var rows = EqExportRows(db.Connection, s.Key);
            Assert.True(rows.Count > 0, s.Name);
            Assert.All(rows, r => Assert.Equal(s.Headers.Length, r.Length));
        }
        var eq = EqExportRows(db.Connection, "equipment");
        Assert.Contains(eq, r => r[1] == "电铲");
        Assert.Equal("产量", EqStripUnit("产量_m3")); Assert.Equal("内部故障率", EqStripUnit("内部故障率%"));
        Assert.Equal(0.86, EqRate("86%"), 9); Assert.Equal(0.86, EqRate("0.86"), 9); Assert.Equal(0.86, EqRate("86"), 9);
        Assert.Equal("Shovel", EqImportCategory("电铲")); Assert.Equal("Truck", EqImportCategory("truck")); Assert.Equal("Other", EqImportCategory("??"));
        Assert.Equal("洒水车", EqExportCategoryCn("WaterTruck"));
    }

    [Fact]
    public void Import_csv_roundtrip_skip_vs_overwrite()
    {
        using var db = GeoDatabase.OpenSeeded();
        var specs = EqImportSpecs();
        // 月度产能: 新增 1 + 既有 1(跳过)
        var capSpec = specs[1];
        string csv = "设备编号,年,月,产量_m3\n1662,2027,1,12345\n1662,2026,5,1\n";
        var o1 = EqImportCsv(db.Connection, capSpec, csv, overwrite: false);
        Assert.Equal(0, o1.ErrorRows); Assert.Equal(1, o1.Inserted); Assert.Equal(1, o1.Skipped); Assert.Equal(0, o1.Updated);
        // 外键: 不在台账的设备编号 → 该行记错误(与原 EF 写库同为异常行)
        var oFk = EqImportCsv(db.Connection, capSpec, "设备编号,年,月,产量_m3\nT-NOPE,2027,1,1\n", false);
        Assert.True(oFk.ErrorRows + oFk.Inserted == 1);
        var o2 = EqImportCsv(db.Connection, capSpec, csv, overwrite: true);
        Assert.Equal(0, o2.Inserted); Assert.Equal(2, o2.Updated);
        Assert.Equal(1.0, db.Connection.ExecuteScalar<double>("SELECT output_m3 FROM capacity_monthly WHERE equipment_id='1662' AND year=2026 AND month=5"));
        Assert.Contains("新增 0", o2.ToSummary());
        // 缺必填列 → 拒绝
        Assert.Throws<InvalidOperationException>(() => EqImportCsv(db.Connection, capSpec, "设备编号,年\nX,2020\n", false));
        // 设备台账: 中文类别 + 日期 + 投产年份
        var o3 = EqImportCsv(db.Connection, specs[0], "设备编号,类别,型号,状态,购置日期,投产年份\nT-NEW,电铲,4100XPC,在用,2021-03-05,2021\n", false);
        Assert.Equal(1, o3.Inserted);
        var row = db.Connection.QuerySingle<(string cat, string model, int cy)>("SELECT category, model, commission_year FROM equipment WHERE equipment_id='T-NEW'");
        Assert.Equal("Shovel", row.cat); Assert.Equal("4100XPC", row.model); Assert.Equal(2021, row.cy);
        // KPI: 三种写法
        var o4 = EqImportCsv(db.Connection, specs[2], "设备编号,年,月,可用率,作业率,利用率\nT-NEW,2027,1,86%,0.8,80\n", false);
        Assert.Equal(1, o4.Inserted);
        var k = db.Connection.QuerySingle<(double a, double r, double u)>("SELECT availability, actual_run_rate, utilization_rate FROM equipment_kpi_monthly WHERE equipment_id='T-NEW'");
        Assert.Equal(0.86, k.a, 9); Assert.Equal(0.8, k.r, 9); Assert.Equal(0.8, k.u, 9);
        // 生产记录覆盖 = 删旧插新; 故障记录去重键
        var o5 = EqImportCsv(db.Connection, specs[3], "设备编号,日期,班次,班产_m3,工作小时\n1662,2026-05-01,A,999,7\n", true);
        Assert.Equal(1, o5.Updated);
        Assert.Equal(999.0, db.Connection.ExecuteScalar<double>("SELECT output_m3 FROM production_record WHERE equipment_id='1662' AND date='2026-05-01' AND shift='A'"));
        var o6 = EqImportCsv(db.Connection, specs[4], "设备编号,日期,故障类型,故障时长_h,是否已修复\nT-NEW,2027-01-02,机械故障,4.5,是\n", false);
        Assert.Equal(1, o6.Inserted);
        Assert.Equal(1L, db.Connection.ExecuteScalar<long>("SELECT is_resolved FROM fault_event WHERE equipment_id='T-NEW'"));
        // 导出 → 再导入(闭环): 与模板同列, 全部跳过
        var exported = EqExportCsv(db.Connection, specs[4]);
        var o7 = EqImportCsv(db.Connection, specs[4], exported, false);
        Assert.Equal(0, o7.Inserted); Assert.True(o7.Skipped > 0); Assert.Equal(0, o7.ErrorRows);
    }
}
