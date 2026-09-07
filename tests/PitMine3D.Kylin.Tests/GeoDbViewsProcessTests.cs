using System;
using System.Linq;
using PitMine3D.Kylin.Data;
using Xunit;
using static PitMine3D.Kylin.Data.GeoDbViews;

/// <summary>
/// 「工艺参数管理」组(工艺架构定义 / 平盘工艺地图 / 参数模板库 / 现场验收录入)数据层回归。
/// 数据: V007 工艺架构种子(8 系统 / 28 环节 / 参数定义)、V010 模板 + 绑定、V012 设备约束、V024 真实验收记录。
/// </summary>
public class GeoDbViewsProcessTests
{
    // ─── 工艺架构 ──────────────────────────────────────────────────────

    [Fact]
    public void Systems_seeded_eight_ordered_by_display_order()
    {
        using var db = GeoDatabase.OpenSeeded();
        var systems = ProcessSystems(db.Connection, activeOnly: false);
        Assert.Equal(8, systems.Count);
        Assert.Equal("blasting", systems[0].Code);
        Assert.Equal("穿爆系统", systems[0].Name);
        Assert.True(systems.Zip(systems.Skip(1), (a, b) => a.DisplayOrder <= b.DisplayOrder).All(x => x));
        Assert.All(systems, s => Assert.True(s.IsActive));
    }

    [Fact]
    public void Phases_by_system_ordered_by_sequence()
    {
        using var db = GeoDatabase.OpenSeeded();
        var phases = ProcessPhasesBySystem(db.Connection, 1);
        Assert.Equal(4, phases.Count);
        Assert.Equal("drilling", phases[0].Code);
        Assert.Equal("Drill", phases[0].TypicalEquipmentCategory);
        Assert.Equal(new[] { 1, 2, 3, 4 }, phases.Select(p => p.SequenceOrder).ToArray());
        // V007 种子 28 环节, 后续迁移停用了少数 → 在用 ≥ 20 且全部 is_active; 全量 ≥ 在用
        var active = ProcessAllPhases(db.Connection);
        Assert.InRange(active.Count, 20, 28);
        Assert.All(active, p => Assert.True(p.IsActive));
        Assert.True(ProcessAllPhases(db.Connection, activeOnly: false).Count >= active.Count);
        Assert.Equal("钻孔", ProcessGetPhase(db.Connection, 101)!.Name);
        Assert.Equal("穿爆系统", ProcessGetSystem(db.Connection, 1)!.Name);
    }

    [Fact]
    public void Params_by_phase_seeded_with_norms()
    {
        using var db = GeoDatabase.OpenSeeded();
        var ps = ProcessParamsByPhase(db.Connection, 101);
        Assert.True(ps.Count >= 3);
        Assert.Equal(ps.Count, ProcessParamCountByPhase(db.Connection, 101));
        var d = ps.First(p => p.Code == "hole_diameter");
        Assert.Equal("孔径", d.Name);
        Assert.Equal("mm", d.Unit);
        Assert.True(d.IsRequired);
        Assert.NotNull(d.StandardMin); Assert.NotNull(d.StandardMax); Assert.NotNull(d.StandardDefault);
        Assert.True(d.StandardMin < d.StandardDefault && d.StandardDefault <= d.StandardMax);
        Assert.InRange(d.StandardDefault!.Value, 100, 400);
        // 文本代理: 与数值同源
        Assert.Equal(d.StandardMin!.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), d.StandardMinText);
        // 全部按 display_order 排序
        Assert.True(ps.Zip(ps.Skip(1), (a, b) => a.DisplayOrder <= b.DisplayOrder).All(x => x));
    }

    [Fact]
    public void ParamDef_text_proxies_parse_and_clear()
    {
        var p = new ProcParamDef { StandardMinText = "12.5", StandardMaxText = "abc", StandardDefaultText = "" };
        Assert.Equal(12.5, p.StandardMin);
        Assert.Null(p.StandardMax);
        Assert.Null(p.StandardDefault);
        Assert.Equal("", p.StandardDefaultText);
    }

    [Fact]
    public void System_phase_param_crud_roundtrip_and_cascade()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        long sysId = ProcessInsertSystem(c, "test_sys", "测试系统");
        Assert.True(sysId > 8);
        Assert.Equal(99, ProcessGetSystem(c, sysId)!.DisplayOrder);
        long phId = ProcessInsertPhase(c, sysId, "test_ph", "测试环节", 1);
        var pd = new ProcParamDef { PhaseId = phId, Code = "test_param_x", Name = "新参数", Unit = "", ValueType = "numeric", DisplayOrder = 1 };
        pd.ParamId = ProcessInsertParam(c, pd);
        Assert.True(pd.ParamId > 0);
        Assert.Equal(1, ProcessParamCountByPhase(c, phId));

        // 更新回读
        pd.Name = "改名"; pd.StandardMinText = "1"; pd.StandardMaxText = "9"; pd.IsRequired = true; pd.CalcFormula = "a/b";
        Assert.Equal(1, ProcessUpdateParam(c, pd));
        var back = ProcessGetParam(c, pd.ParamId)!;
        Assert.Equal("改名", back.Name); Assert.Equal(1, back.StandardMin); Assert.Equal(9, back.StandardMax);
        Assert.True(back.IsRequired); Assert.Equal("a/b", back.CalcFormula);

        // 删参数 → 计数归零; 删系统 → 环节级联(FK ON DELETE CASCADE)
        ProcessDeleteParam(c, pd.ParamId);
        Assert.Equal(0, ProcessParamCountByPhase(c, phId));
        ProcessDeleteSystem(c, sysId);
        Assert.Null(ProcessGetPhase(c, phId));
        Assert.Null(ProcessGetSystem(c, sysId));
    }

    [Fact]
    public void Constraints_by_param_bench_height_has_hard_shovel_limits()
    {
        using var db = GeoDatabase.OpenSeeded();
        var cs = ProcessConstraintsByParam(db.Connection, 2001);   // 台阶高度 → 电铲最大挖掘高度(V012)
        Assert.Equal(4, cs.Count);
        Assert.All(cs, x => { Assert.Equal("max", x.ConstraintType); Assert.Equal("hard", x.Consequence); Assert.True(x.IsActive); });
        Assert.Equal(15.5, cs.First(x => x.EquipmentModel == "4100XPC").LimitValue);
        Assert.Empty(ProcessConstraintsByParam(db.Connection, 999999));
    }

    // ─── 模板库 ────────────────────────────────────────────────────────

    [Fact]
    public void Templates_seeded_and_archive_hides_from_active()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        var all = ProcTemplates(c, activeOnly: false);
        Assert.True(all.Count >= 3);
        var hard = all.First(t => t.Code == "rh_hard_v1.0");
        Assert.Equal("硬岩区标准 v1.0", hard.Name);
        Assert.Equal("rh", hard.ApplicableMaterial); Assert.Equal("hard", hard.ApplicableHardness);
        Assert.True(hard.IsCurrent); Assert.Equal("active", hard.Status);
        int activeBefore = ProcTemplates(c, activeOnly: true).Count;
        ProcArchiveTemplate(c, hard.TemplateId);
        Assert.Equal(activeBefore - 1, ProcTemplates(c, activeOnly: true).Count);
        var t = ProcGetTemplate(c, hard.TemplateId)!;
        Assert.Equal("archived", t.Status); Assert.False(t.IsCurrent);
    }

    [Fact]
    public void Template_values_seeded_and_upsert_updates_in_place()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        var vals = ProcValuesByTemplate(c, 1);
        Assert.True(vals.Count >= 10);
        var hd = vals.First(v => v.ParamId == 1001);       // 硬岩模板 孔径: V010 250(220~280) → V026 校准上限 310
        Assert.Equal(250, hd.RecommendedValue); Assert.Equal(220, hd.MinValue);
        Assert.True(hd.MinValue < hd.RecommendedValue && hd.RecommendedValue <= hd.MaxValue);
        Assert.Equal("硬岩取大孔径", hd.Notes);

        int before = vals.Count;
        ProcUpsertTemplateValue(c, new ProcTemplateValue { TemplateId = 1, ParamId = 1001, RecommendedValue = 240, MinValue = 210, MaxValue = 270, Notes = "改" });
        Assert.Equal(before, ProcValuesByTemplate(c, 1).Count);          // 更新不新增
        Assert.Equal(240, ProcGetTemplateValue(c, 1, 1001)!.RecommendedValue);
        // 模板尚未覆盖的现存参数 → 插入
        long newParam = db.ScalarLong("SELECT param_id FROM parameter_definition WHERE param_id NOT IN (SELECT param_id FROM template_param_value WHERE template_id = 1) LIMIT 1");
        Assert.True(newParam > 0);
        ProcUpsertTemplateValue(c, new ProcTemplateValue { TemplateId = 1, ParamId = newParam, RecommendedValue = 1 });
        Assert.Equal(before + 1, ProcValuesByTemplate(c, 1).Count);
    }

    [Fact]
    public void Template_insert_clone_delete()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        var src = ProcGetTemplate(c, 1)!;
        var clone = new ProcTemplate
        {
            Code = src.Code + "_copy_x", Name = src.Name + " (副本)", Description = src.Description,
            ApplicableMaterial = src.ApplicableMaterial, ApplicableHardness = src.ApplicableHardness,
            Version = ProcBumpVersion(src.Version), IsCurrent = false, Status = "draft"
        };
        clone.TemplateId = ProcInsertTemplate(c, clone);
        Assert.True(clone.TemplateId > 3);
        int copied = ProcCloneTemplateValues(c, src.TemplateId, clone.TemplateId);
        // 只复制参数定义仍存在的值(种子里可能残留已删参数的孤值)
        long live = db.ScalarLong("SELECT COUNT(*) FROM template_param_value v JOIN parameter_definition d ON d.param_id = v.param_id WHERE v.template_id = 1");
        Assert.True(copied >= 10);
        Assert.Equal(live, copied);
        Assert.Equal(copied, ProcValuesByTemplate(c, clone.TemplateId).Count);
        Assert.Equal(250, ProcGetTemplateValue(c, clone.TemplateId, 1001)!.RecommendedValue);
        Assert.Equal("v1.1", ProcGetTemplate(c, clone.TemplateId)!.Version);
        Assert.Equal("draft", ProcGetTemplate(c, clone.TemplateId)!.Status);
        // 草稿不在 activeOnly 列表, 但在全部列表
        Assert.DoesNotContain(ProcTemplates(c, true), t => t.TemplateId == clone.TemplateId);
        Assert.Contains(ProcTemplates(c, false), t => t.TemplateId == clone.TemplateId);

        ProcDeleteTemplate(c, clone.TemplateId);
        Assert.Null(ProcGetTemplate(c, clone.TemplateId));
        Assert.Empty(ProcValuesByTemplate(c, clone.TemplateId));
    }

    [Fact]
    public void BumpVersion_and_ParseRange_and_BuildValue()
    {
        Assert.Equal("v1.3", ProcBumpVersion("v1.2"));
        Assert.Equal("v1.0", ProcBumpVersion(""));
        Assert.Equal("v1.0", ProcBumpVersion(null));
        Assert.Equal("v2.1", ProcBumpVersion("v2"));
        Assert.Equal("v1.10", ProcBumpVersion("v1.9"));

        Assert.Equal((200.0, 250.0), ProcParseRange("200~250"));
        Assert.Equal((200.0, 250.0), ProcParseRange(" 200 - 250 "));
        Assert.Equal((200.0, 250.0), ProcParseRange("200～250"));
        Assert.Equal(((double?)null, (double?)null), ProcParseRange(""));
        Assert.Equal(((double?)null, (double?)null), ProcParseRange("1~2~3"));
        Assert.Equal(((double?)null, 5.0), ProcParseRange("~5"));

        var num = ProcBuildTemplateValue(1, 1001, "245", "220~280", "n")!;
        Assert.Equal(245, num.RecommendedValue); Assert.Null(num.TextValue); Assert.Equal(220, num.MinValue); Assert.Equal(280, num.MaxValue); Assert.Equal("n", num.Notes);
        var txt = ProcBuildTemplateValue(1, 1001, "垂直", "", "  ")!;
        Assert.Null(txt.RecommendedValue); Assert.Equal("垂直", txt.TextValue); Assert.Null(txt.Notes);
        Assert.Null(ProcBuildTemplateValue(1, 1001, "  ", "1~2", "x"));  // 空推荐值 → 跳过
    }

    [Fact]
    public void Template_usage_lists_bound_locations()
    {
        using var db = GeoDatabase.OpenSeeded();
        var usage = ProcTemplateUsage(db.Connection, 1);
        Assert.True(usage.Count > 0);
        Assert.Contains(usage, u => u.LocationCode == "1195" && u.PhaseName == "钻孔");
        Assert.Empty(ProcTemplateUsage(db.Connection, 999999));
    }

    // ─── 平盘 / 绑定 ──────────────────────────────────────────────────

    [Fact]
    public void Locations_active_ordered_by_elevation()
    {
        using var db = GeoDatabase.OpenSeeded();
        var locs = ProcLocations(db.Connection, activeOnly: true);
        Assert.Contains(locs, l => l.LocationCode == "1195");
        var withElev = locs.Where(l => l.ElevationM.HasValue).Select(l => l.ElevationM!.Value).ToList();
        Assert.True(withElev.Count >= 2);
        Assert.True(withElev.Zip(withElev.Skip(1), (a, b) => a <= b).All(x => x));
        var l1195 = ProcGetLocation(db.Connection, "1195")!;
        Assert.Equal(1195, l1195.ElevationM);
        Assert.True(ProcLocations(db.Connection, false).Count >= locs.Count);
    }

    [Fact]
    public void Bindings_seeded_for_1195_and_rebind_switches_template()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        var bindings = ProcActiveBindingsByLocation(c, "1195");
        Assert.True(bindings.Count >= 4);
        Assert.True(bindings.Zip(bindings.Skip(1), (a, b) => a.PhaseId <= b.PhaseId).All(x => x));
        var b = ProcActiveBinding(c, "1195", 101)!;
        Assert.Equal(1, b.BoundTemplateId);
        Assert.True(b.IsActive);

        ProcRebindTemplate(c, "1195", 101, 2, new DateTime(2026, 9, 7));
        var nb = ProcActiveBinding(c, "1195", 101)!;
        Assert.Equal(2, nb.BoundTemplateId);
        Assert.NotEqual(b.Id, nb.Id);
        Assert.Equal("2026-09-07", nb.StartedAt);
        var old = ProcAllBindings(c, activeOnly: false).First(x => x.Id == b.Id);
        Assert.False(old.IsActive); Assert.Equal("2026-09-07", old.EndedAt);
        Assert.Equal(bindings.Count, ProcActiveBindingsByLocation(c, "1195").Count);   // 现行绑定数不变

        // 同日再次切换(撞 UNIQUE(phase,location,started_at)) → 以最新为准; 解绑模板(NULL) 也可
        ProcRebindTemplate(c, "1195", 101, null, new DateTime(2026, 9, 7));
        var nb2 = ProcActiveBinding(c, "1195", 101)!;
        Assert.Null(nb2.BoundTemplateId);
        Assert.Equal(1, ProcAllBindings(c, false).Count(x => x.PhaseId == 101 && x.LocationCode == "1195" && x.StartedAt == "2026-09-07"));

        // 新绑定环节
        int before = ProcActiveBindingsByLocation(c, "1195").Count;
        long id = ProcInsertBinding(c, 803, "1195", null);
        Assert.True(id > 0);
        Assert.Equal(before + 1, ProcActiveBindingsByLocation(c, "1195").Count);
    }

    // ─── 现场验收 ──────────────────────────────────────────────────────

    [Fact]
    public void Acceptance_seeded_1195_drilling_sorted_desc_and_latest()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        var hist = ProcAcceptanceByLocationPhase(c, "1195", 101);
        Assert.True(hist.Count >= 10);
        Assert.True(hist.Zip(hist.Skip(1), (a, b) => string.CompareOrdinal(a.MeasureDate, b.MeasureDate) >= 0).All(x => x));
        Assert.All(hist, h => { Assert.Equal("1195", h.LocationCode); Assert.Equal(101, h.PhaseId); Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", h.MeasureDate); });

        var latest = ProcLatestAcceptance(c, "1195", 101, 1001)!;   // 孔径 最新一条(V024: 2026-05-20 247.5 pass)
        Assert.Equal("2026-05-20", latest.MeasureDate);
        Assert.Equal(247.5, latest.MeasuredValue);
        Assert.Equal(250, latest.TemplateValue);
        Assert.Equal("pass", latest.Status);
        Assert.Equal("陈斌", latest.AcceptedBy);
        Assert.Null(ProcLatestAcceptance(c, "1195", 101, 999999));
    }

    [Fact]
    public void Recent_by_status_window_filters()
    {
        using var db = GeoDatabase.OpenSeeded();
        var fails = ProcRecentAcceptanceByStatus(db.Connection, "fail", 36500);
        var warns = ProcRecentAcceptanceByStatus(db.Connection, "warning", 36500);
        Assert.True(fails.Count > 0);
        Assert.True(warns.Count > fails.Count);
        Assert.All(fails, f => Assert.Equal("fail", f.Status));
        Assert.True(fails.Zip(fails.Skip(1), (a, b) => string.CompareOrdinal(a.MeasureDate, b.MeasureDate) >= 0).All(x => x));
        // 未来窗口(-0 days 之后)为空: 种子日期都在 2026-05 前
        Assert.Empty(ProcRecentAcceptanceByStatus(db.Connection, "fail", 0).Where(f => string.CompareOrdinal(f.MeasureDate, "2026-09-01") < 0));
    }

    [Fact]
    public void Text_helpers_known_values()
    {
        Assert.Equal("✓", ProcStatusIcon("pass")); Assert.Equal("⚠", ProcStatusIcon("warning"));
        Assert.Equal("✗", ProcStatusIcon("fail")); Assert.Equal("❍", ProcStatusIcon("pending")); Assert.Equal("❍", ProcStatusIcon(null));
        Assert.Equal("+12.3%", ProcDeviationText(12.34)); Assert.Equal("-4.0%", ProcDeviationText(-4.04));
        Assert.Equal("0%", ProcDeviationText(0)); Assert.Equal("—", ProcDeviationText(null));
        Assert.Equal("165.0 ~ 250.0", ProcStdRange(165, 250)); Assert.Equal("— ~ 9.5", ProcStdRange(null, 9.5));
        Assert.Equal("250.00", ProcTemplateValText(250, 220)); Assert.Equal("220.00 (默)", ProcTemplateValText(null, 220)); Assert.Equal("—", ProcTemplateValText(null, null));
    }

    [Fact]
    public void AcceptanceRow_recomputes_deviation_and_status_live()
    {
        var def = new ProcParamDef { ParamId = 1, Name = "孔径", Unit = "mm", StandardMin = 165, StandardMax = 250, StandardDefault = 220, AlarmLow = 150, AlarmHigh = 280, IsRequired = true };
        var row = new ProcAcceptanceRow(def, 250, null);
        Assert.Equal(250, row.TemplateValueNumeric);
        Assert.Equal("250.00", row.TemplateValue);
        Assert.Equal("✓", row.IsRequiredText);
        Assert.Equal("—", row.DeviationText); Assert.Equal("❍", row.StatusIcon);

        int changes = 0; row.PropertyChanged += (_, _) => changes++;
        row.MeasuredText = "300";                      // > 报警上限 → fail
        Assert.Equal("✗", row.StatusIcon); Assert.Equal("+20.0%", row.DeviationText);
        row.MeasuredText = "260";                      // > 标准上限 → warning
        Assert.Equal("⚠", row.StatusIcon);
        row.MeasuredText = "247.5";                    // 范围内, 偏差 -1% → pass
        Assert.Equal("✓", row.StatusIcon); Assert.Equal("-1.0%", row.DeviationText);
        row.MeasuredText = "垂直";                     // 文本型
        Assert.Equal("(文本)", row.DeviationText); Assert.Equal("—", row.StatusIcon); Assert.Null(row.MeasuredNumeric);
        row.MeasuredText = "";
        Assert.Equal("❍", row.StatusIcon);
        Assert.True(changes >= 15);                    // 每次赋值触发 MeasuredText/DeviationText/StatusIcon 三个通知

        // 无模板 → 回退参数默认值(原 AcceptanceRow: TemplateValueNumeric 已回退, 故显示不带 "(默)")
        var row2 = new ProcAcceptanceRow(def, null, null);
        Assert.Equal(220, row2.TemplateValueNumeric);
        Assert.Equal("220.00", row2.TemplateValue);
        var row3 = new ProcAcceptanceRow(new ProcParamDef { Name = "x" }, null, null);
        Assert.Null(row3.TemplateValueNumeric); Assert.Equal("—", row3.TemplateValue);
    }

    [Fact]
    public void LoadAcceptanceRows_prefills_existing_and_template_name()
    {
        using var db = GeoDatabase.OpenSeeded();
        var (rows, tplName) = ProcLoadAcceptanceRows(db.Connection, "1195", 101, new DateTime(2026, 5, 20));
        Assert.Equal("硬岩区标准 v1.0", tplName);
        Assert.Equal(ProcessParamCountByPhase(db.Connection, 101), rows.Count);
        var hd = rows.First(r => r.ParamDef.Code == "hole_diameter");
        Assert.NotNull(hd.ExistingRecord);
        Assert.Equal("247.50", hd.MeasuredText);
        Assert.Equal(250, hd.TemplateValueNumeric);
        Assert.Equal("✓", hd.StatusIcon);

        // 无记录日期 → 空行; 无绑定环节 → "(无模板)"
        var (empty, _) = ProcLoadAcceptanceRows(db.Connection, "1195", 101, new DateTime(2030, 1, 1));
        Assert.All(empty, r => Assert.Null(r.ExistingRecord));
        var (_, none) = ProcLoadAcceptanceRows(db.Connection, "1195", 803, DateTime.Today);
        Assert.Equal("(无模板)", none);
    }

    [Fact]
    public void SubmitAcceptance_inserts_then_updates()
    {
        using var db = GeoDatabase.OpenSeeded();
        var c = db.Connection;
        var date = new DateTime(2030, 1, 1);
        var (rows, _) = ProcLoadAcceptanceRows(c, "1195", 101, date);
        var hd = rows.First(r => r.ParamDef.Code == "hole_diameter");
        // 超出报警上限(无报警阈则超标准上限)→ fail / warning
        double big = (hd.ParamDef.AlarmHigh ?? hd.ParamDef.StandardMax ?? 250) + 100;
        string expectStatus = hd.ParamDef.AlarmHigh.HasValue ? "fail" : "warning";
        hd.MeasuredText = big.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var other = rows.First(r => r.ParamDef.Code != "hole_diameter");
        other.MeasuredText = "自由文本";              // 文本型 → pass
        int before = ProcAcceptanceByLocationPhase(c, "1195", 101).Count;

        var r1 = ProcSubmitAcceptance(c, rows, "1195", 101, date, "不合格", "测试员", "炮区A", "整改", new DateTime(2030, 1, 2));
        Assert.Equal(2, r1.Inserted); Assert.Equal(0, r1.Updated); Assert.Equal(rows.Count - 2, r1.Skipped);
        Assert.Equal(before + 2, ProcAcceptanceByLocationPhase(c, "1195", 101).Count);
        var saved = ProcLatestAcceptance(c, "1195", 101, hd.ParamDef.ParamId)!;
        Assert.Equal("2030-01-01", saved.MeasureDate); Assert.Equal(big, saved.MeasuredValue);
        Assert.Equal(expectStatus, saved.Status); Assert.Equal(250, saved.TemplateValue);
        Assert.Equal((big - 250) / 250 * 100, saved.DeviationPct!.Value, 6);
        Assert.Equal("不合格", saved.Conclusion); Assert.Equal("测试员", saved.AcceptedBy); Assert.Equal("炮区A", saved.ScopeCode);
        Assert.Equal("整改", saved.Notes); Assert.Equal("2030-01-02", saved.AcceptanceDate);
        var txt = ProcLatestAcceptance(c, "1195", 101, other.ParamDef.ParamId)!;
        Assert.Equal("自由文本", txt.MeasuredText); Assert.Null(txt.MeasuredValue); Assert.Equal("pass", txt.Status);

        // 同日再提交 → 更新而非新增
        hd.MeasuredText = "248";
        var r2 = ProcSubmitAcceptance(c, rows, "1195", 101, date, "合格", "测试员", null, null);
        Assert.Equal(0, r2.Inserted); Assert.Equal(2, r2.Updated);
        Assert.Equal(before + 2, ProcAcceptanceByLocationPhase(c, "1195", 101).Count);
        var upd = ProcLatestAcceptance(c, "1195", 101, hd.ParamDef.ParamId)!;
        Assert.Equal(saved.Id, upd.Id); Assert.Equal(248, upd.MeasuredValue); Assert.Equal("pass", upd.Status); Assert.Equal("合格", upd.Conclusion);

        // 重新装载同日 → 带出更新后的值
        var (reload, _) = ProcLoadAcceptanceRows(c, "1195", 101, date);
        Assert.Equal("248.00", reload.First(r => r.ParamDef.Code == "hole_diameter").MeasuredText);
    }

    // ─── 平盘工艺地图 ──────────────────────────────────────────────────

    [Fact]
    public void ParamCompareRows_join_template_and_latest_measurement()
    {
        using var db = GeoDatabase.OpenSeeded();
        var rows = ProcParamCompareRows(db.Connection, "1195", 101, 1);
        Assert.Equal(ProcessParamCountByPhase(db.Connection, 101), rows.Count);
        var hd = rows.First(r => r.ParamName == "孔径");
        Assert.Equal("mm", hd.Unit);
        Assert.Equal("250.00", hd.TemplateVal);
        Assert.Equal("247.50", hd.MeasuredVal);
        Assert.Equal("-1.0%", hd.DeviationText);
        Assert.Equal("✓", hd.StatusIcon);
        Assert.Equal("05-20", hd.MeasureDate);
        // 无模板 → 默认值 "(默)"; 无验收平盘 → 未验收
        var noTpl = ProcParamCompareRows(db.Connection, "1195", 101, null).First(r => r.ParamName == "孔径");
        Assert.EndsWith("(默)", noTpl.TemplateVal);
        var none = ProcParamCompareRows(db.Connection, "1270", 803, null);
        Assert.All(none, r => { Assert.Equal("未验收", r.MeasureDate); Assert.Equal("❍", r.StatusIcon); Assert.Equal("—", r.MeasuredVal); });
    }

    [Fact]
    public void CompatibleEquipment_filters_by_typical_category_and_hard_constraints()
    {
        using var db = GeoDatabase.OpenSeeded();
        // 钻孔(101, 典型 Drill) + 硬岩模板 孔径 250 ≤ DMH90 max 251 → 适用; 仅 Drill 类机型
        var drill = ProcCompatibleEquipment(db.Connection, 101, 1);
        Assert.True(drill.Count >= 1);
        Assert.Contains(drill, m => m.Model == "DMH90" && m.Pass && m.Detail == "适用");
        Assert.DoesNotContain(drill, m => m.Model == "4100XPC");
        // 工作面规划(201, 典型 NULL → 全部机型) + 模板 台阶高度 15: PH2800(max 12)/WK-35(13)/994(8) 被排除, 4100XPC(15.5) 适用
        var plan = ProcCompatibleEquipment(db.Connection, 201, 1);
        int total = (int)db.ScalarLong("SELECT COUNT(*) FROM equipment_model");
        Assert.Equal(total, plan.Count);
        Assert.Contains(plan, m => m.Model == "4100XPC" && m.Pass);
        var ph = plan.First(m => m.Model == "PH2800");
        Assert.False(ph.Pass); Assert.StartsWith("排除:台阶高度 max 12", ph.Detail);
        Assert.False(plan.First(m => m.Model == "994").Pass);
    }

    [Fact]
    public void GeneratePlan_known_values()
    {
        Assert.Equal("hard", ProcHardnessOf("rh-hard")); Assert.Equal("medium", ProcHardnessOf("c4-medium"));
        Assert.Equal("soft", ProcHardnessOf("coal-soft")); Assert.Equal("hard", ProcHardnessOf(null));

        var p = ProcGeneratePlan(80, 120, 30, "hard");    // 界面默认值
        Assert.Equal("4100XPC", p.ShovelModel); Assert.Equal(3.0, p.ShovelDailyCap);
        Assert.Equal(1, p.ShovelCount);                    // ceil(80/30/3)=1
        Assert.Equal("930E", p.TruckModel);                // 80+120*1.6=272>100
        Assert.Equal(3, p.TruckPerShovel); Assert.Equal(3, p.TruckCount);

        var q = ProcGeneratePlan(300, 10, 30, "soft");    // 300/30/2=5 铲; 300+16>100 → 930E ×3 = 15
        Assert.Equal("PH2800", q.ShovelModel); Assert.Equal(5, q.ShovelCount); Assert.Equal(15, q.TruckCount);
        var r = ProcGeneratePlan(30, 10, 30, "medium");   // 30+16=46 ≤ 100 → 730E 1:4
        Assert.Equal("WK-35", r.ShovelModel); Assert.Equal(1, r.ShovelCount); Assert.Equal("730E", r.TruckModel); Assert.Equal(4, r.TruckCount);
        Assert.Equal(1, ProcGeneratePlan(0, 0, 30, "hard").ShovelCount);   // 至少 1 台
    }

    [Fact]
    public void Plan_checks_read_slope_and_fleet()
    {
        using var db = GeoDatabase.OpenSeeded();
        var f = ProcMinSlopeSafetyFactor(db.Connection);
        Assert.NotNull(f);
        Assert.InRange(f!.Value, 0.8, 3.0);
        Assert.True(ProcEquipmentCountByCategory(db.Connection, "Shovel") > 0);
        Assert.True(ProcEquipmentCountByCategory(db.Connection, "Truck") > ProcEquipmentCountByCategory(db.Connection, "Shovel"));
        Assert.Equal(0, ProcEquipmentCountByCategory(db.Connection, "NoSuchCategory"));
    }
}
