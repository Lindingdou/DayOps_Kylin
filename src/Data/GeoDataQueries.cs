using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// §四/§八 数据查询/分析（读 SQLite 数据基座）。忠实原 GeoDataBase 各 Service 的读侧口径,
/// 以只读聚合报表形式呈现（不做 CRUD 对话框）。纯查询、可对种子库单测。
/// </summary>
public static class GeoDataQueries
{
    public sealed record CategoryCount(string Category, int Count);
    public sealed record EquipmentRoster(int Total, IReadOnlyList<CategoryCount> ByCategory, int InService);

    /// <summary>设备台账概览：总数 / 分类计数 / 在役数。</summary>
    public static EquipmentRoster GetEquipmentRoster(SqliteConnection conn)
    {
        var byCat = new List<CategoryCount>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT category, COUNT(*) FROM equipment GROUP BY category ORDER BY COUNT(*) DESC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) byCat.Add(new CategoryCount(rd.IsDBNull(0) ? "(未分类)" : rd.GetString(0), rd.GetInt32(1)));
        }
        int total = (int)Scalar(conn, "SELECT COUNT(*) FROM equipment");
        // 种子 status 词表: 在用/待报废/租赁/报废/退租(见原 EquipmentStatus 枚举 InUse+实际库)。
        // 在役 = 在用 + 租赁(排除 待报废/报废/退租); NULL 按默认在用计。
        int inSvc = (int)Scalar(conn, "SELECT COUNT(*) FROM equipment WHERE status IN ('在用','租赁') OR status IS NULL");
        return new EquipmentRoster(total, byCat, inSvc);
    }

    public sealed record ProductionStats(int Records, double OutputM3, double WorkHours, double FaultHours, double UtilizationPct);

    /// <summary>生产数据统计：记录数 / 总产量 / 工时 / 故障工时 / 作业率(工时/(工时+故障))。</summary>
    public static ProductionStats GetProductionStats(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*), COALESCE(SUM(output_m3),0), COALESCE(SUM(work_hours),0), COALESCE(SUM(fault_hours),0)
                            FROM production_record";
        using var rd = cmd.ExecuteReader();
        rd.Read();
        int n = rd.GetInt32(0);
        double outp = rd.GetDouble(1), wh = rd.GetDouble(2), fh = rd.GetDouble(3);
        double util = (wh + fh) > 1e-9 ? wh / (wh + fh) * 100.0 : 0;
        return new ProductionStats(n, outp, wh, fh, util);
    }

    public sealed record CapacityRow(string EquipmentId, string Model, double TotalOutputM3);

    /// <summary>产能排名：按设备累计产量降序(join 型号)。取前 topN。</summary>
    public static List<CapacityRow> GetCapacityRanking(SqliteConnection conn, int topN = 10)
    {
        var rows = new List<CapacityRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT c.equipment_id, COALESCE(e.model,''), SUM(c.output_m3) AS tot
                            FROM capacity_monthly c LEFT JOIN equipment e ON e.equipment_id = c.equipment_id
                            GROUP BY c.equipment_id ORDER BY tot DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@n", topN);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new CapacityRow(rd.GetString(0), rd.GetString(1), rd.GetDouble(2)));
        return rows;
    }

    public sealed record CapacityCategoryRow(string Category, int Units, double TotalOutputM3, double SharePct);

    /// <summary>产能分类对比：按设备类型(铲/车/钻…)聚合累计产量 + 台数 + 占比，降序。</summary>
    public static List<CapacityCategoryRow> GetCapacityByCategory(SqliteConnection conn)
    {
        var raw = new List<(string cat, int units, double tot)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT COALESCE(e.category,'(未分类)'), COUNT(DISTINCT c.equipment_id), SUM(c.output_m3)
                                FROM capacity_monthly c LEFT JOIN equipment e ON e.equipment_id = c.equipment_id
                                GROUP BY e.category ORDER BY SUM(c.output_m3) DESC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) raw.Add((rd.GetString(0), rd.GetInt32(1), rd.GetDouble(2)));
        }
        double grand = 0; foreach (var r in raw) grand += r.tot;
        var rows = new List<CapacityCategoryRow>();
        foreach (var r in raw) rows.Add(new CapacityCategoryRow(r.cat, r.units, r.tot, grand > 0 ? r.tot / grand * 100 : 0));
        return rows;
    }

    public sealed record FaultStats(int Events, double DowntimeHours, int Unresolved, string TopType, int TopTypeCount);

    /// <summary>故障分析（设备状态·故障报修）：事件数 / 累计停机时 / 未修复数 / 最多故障类型。</summary>
    public static FaultStats GetFaultStats(SqliteConnection conn)
    {
        int events = (int)Scalar(conn, "SELECT COUNT(*) FROM fault_event");
        double downtime = ScalarDouble(conn, "SELECT COALESCE(SUM(duration_hours),0) FROM fault_event");
        int unresolved = (int)Scalar(conn, "SELECT COUNT(*) FROM fault_event WHERE is_resolved = 0");
        string topType = "(无)"; int topCount = 0;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(fault_type,'(未分类)'), COUNT(*) c FROM fault_event GROUP BY fault_type ORDER BY c DESC LIMIT 1";
            using var rd = cmd.ExecuteReader();
            if (rd.Read()) { topType = rd.GetString(0); topCount = rd.GetInt32(1); }
        }
        return new FaultStats(events, downtime, unresolved, topType, topCount);
    }

    public sealed record KpiStats(int Records, double AvgAvailabilityPct, double AvgUtilizationPct, int LatestYear, int LatestMonth);

    /// <summary>KPI 分析：equipment_kpi_monthly 平均可用率/利用率 + 最新期。</summary>
    public static KpiStats GetKpiStats(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*), COALESCE(AVG(availability),0), COALESCE(AVG(utilization_rate),0),
                            COALESCE(MAX(year),0), COALESCE(MAX(month),0) FROM equipment_kpi_monthly";
        using var rd = cmd.ExecuteReader();
        rd.Read();
        int n = rd.GetInt32(0);
        double av = rd.GetDouble(1), ut = rd.GetDouble(2);
        // availability/utilization 可能存为 0..1 或 0..100，统一按 <=1 视为比率×100。
        double avPct = av <= 1.0 ? av * 100 : av;
        double utPct = ut <= 1.0 ? ut * 100 : ut;
        return new KpiStats(n, avPct, utPct, rd.GetInt32(3), rd.GetInt32(4));
    }

    public sealed record EfficiencyForecast(int ActiveEquipment, int ProducingUnits, double BaselineMonthlyWanM3, double AvgAvailabilityPct, double AvgRunRatePct, double ProjectedAnnualWanM3);

    /// <summary>
    /// 设备效能预测（基线 + 投影）：基线月产量 = 产出设备月均(万m³); 可用率/作业率取 KPI 均值;
    /// 投影年产 = 基线月产 × 12 × **产出设备数**(非全在役——基线口径是"每产出设备月均"，须乘产出设备
    /// 数才口径一致；乘全在役含卡车/钻机等非独立产出者会高估)。ActiveEquipment=在役总数(参考)。
    /// </summary>
    public static EfficiencyForecast GetEfficiencyForecast(SqliteConnection conn)
    {
        double baseMonthly = ScalarDouble(conn, "SELECT COALESCE(AVG(output_m3),0)/10000.0 FROM capacity_monthly WHERE output_m3 > 0");
        // 在役 = 在用 + 租赁(种子词表 在用/待报废/租赁/报废/退租); NULL 按默认在用计。
        int active = (int)Scalar(conn, "SELECT COUNT(*) FROM equipment WHERE status IS NULL OR status IN ('在用','租赁')");
        // 产出设备数 = capacity_monthly 中有产出记录的设备(与 baseMonthly 口径一致)。
        int producing = (int)Scalar(conn, "SELECT COUNT(DISTINCT equipment_id) FROM capacity_monthly WHERE output_m3 > 0");
        double av, rr;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(AVG(availability),0), COALESCE(AVG(actual_run_rate),0) FROM equipment_kpi_monthly";
            using var rd = cmd.ExecuteReader(); rd.Read();
            av = rd.GetDouble(0); rr = rd.GetDouble(1);
        }
        double avPct = av <= 1.0 ? av * 100 : av;
        double rrPct = rr <= 1.0 ? rr * 100 : rr;
        double projAnnual = baseMonthly * 12 * (producing > 0 ? producing : 1);
        return new EfficiencyForecast(active, producing, baseMonthly, avPct, rrPct, projAnnual);
    }

    public sealed record BoreholeStats(int Holes, double TotalDepthM, double AvgDepthM, int SeamResults, IReadOnlyList<CategoryCount> ByCategory);

    /// <summary>钻孔管理概览：孔数 / 总孔深 / 均深 / 见煤结果数 / 按类别。</summary>
    public static BoreholeStats GetBoreholeStats(SqliteConnection conn)
    {
        int holes = (int)Scalar(conn, "SELECT COUNT(*) FROM borehole");
        double totDepth = ScalarDouble(conn, "SELECT COALESCE(SUM(depth_total),0) FROM borehole");
        int seamRes = (int)Scalar(conn, "SELECT COUNT(*) FROM borehole_seam_result");
        var byCat = new List<CategoryCount>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(category,'(未分类)'), COUNT(*) c FROM borehole GROUP BY category ORDER BY c DESC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) byCat.Add(new CategoryCount(rd.GetString(0), rd.GetInt32(1)));
        }
        return new BoreholeStats(holes, totDepth, holes > 0 ? totDepth / holes : 0, seamRes, byCat);
    }

    public sealed record CoalQualityStats(int Samples, int Seams, double AvgAshPct, double AvgVolatilePct, double AvgCalorificMJ, double AvgSulfurPct);

    /// <summary>煤质统计：样本数 / 煤层数 / 平均 灰分Ad / 挥发分Vdaf / 发热量Qnet / 全硫St。</summary>
    public static CoalQualityStats GetCoalQualityStats(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*), COUNT(DISTINCT seam_code),
                            COALESCE(AVG(ad_raw),0), COALESCE(AVG(vdaf_raw),0),
                            COALESCE(AVG(qnet_ad),0), COALESCE(AVG(std_raw),0)
                            FROM coal_sample WHERE ad_raw IS NOT NULL";
        using var rd = cmd.ExecuteReader();
        rd.Read();
        return new CoalQualityStats(rd.GetInt32(0), rd.GetInt32(1), rd.GetDouble(2), rd.GetDouble(3), rd.GetDouble(4), rd.GetDouble(5));
    }

    public sealed record SeamRow(string SeamCode, string Name, int SampleCount);

    /// <summary>煤层管理：各煤层定义 + 煤样计数。</summary>
    public static List<SeamRow> GetCoalSeams(SqliteConnection conn)
    {
        var rows = new List<SeamRow>();
        using var cmd = conn.CreateCommand();
        // coal_seam_def.code ↔ coal_sample.seam_code；名称在 coal_seam_def.name。
        cmd.CommandText = @"SELECT d.code, d.name,
                            (SELECT COUNT(*) FROM coal_sample s WHERE s.seam_code = d.code)
                            FROM coal_seam_def d ORDER BY d.sort_order, d.code";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new SeamRow(rd.GetString(0), rd.IsDBNull(1) ? "" : rd.GetString(1), rd.GetInt32(2)));
        return rows;
    }

    public sealed record DispatchRuleRow(string Shovel, string Truck, double Loads, int Trucks, double CycleMin, double Score);
    public sealed record DispatchSummary(int Active, IReadOnlyList<DispatchRuleRow> Top);

    /// <summary>设备智能编组 / 调度规则：铲-车配比(装载次数/建议车数/循环时间/评分), 取评分高的在役规则。</summary>
    public static DispatchSummary GetDispatchRules(SqliteConnection conn, int topN = 8)
    {
        int active = (int)Scalar(conn, "SELECT COUNT(*) FROM dispatch_rule WHERE is_active = 1");
        var top = new List<DispatchRuleRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COALESCE(shovel_model,''), COALESCE(truck_model,''), COALESCE(bucket_loads_per_truck,0),
                            COALESCE(recommended_truck_count,0), COALESCE(cycle_time_min,0), COALESCE(efficiency_score,0)
                            FROM dispatch_rule WHERE is_active = 1 ORDER BY efficiency_score DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@n", topN);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) top.Add(new DispatchRuleRow(rd.GetString(0), rd.GetString(1), rd.GetDouble(2), (int)rd.GetDouble(3), rd.GetDouble(4), rd.GetDouble(5)));
        return new DispatchSummary(active, top);
    }

    public sealed record ProcessArchitecture(int Systems, int Phases, int Templates, IReadOnlyList<string> SystemNames);

    /// <summary>工艺架构：系统数 / 工序数 / 模板数 + 系统名。</summary>
    public static ProcessArchitecture GetProcessArchitecture(SqliteConnection conn)
    {
        int sys = (int)Scalar(conn, "SELECT COUNT(*) FROM process_system");
        int ph = (int)Scalar(conn, "SELECT COUNT(*) FROM process_phase");
        int tpl = (int)Scalar(conn, "SELECT COUNT(*) FROM process_template");
        var names = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM process_system ORDER BY COALESCE(display_order,0), name";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) if (!rd.IsDBNull(0)) names.Add(rd.GetString(0));
        }
        return new ProcessArchitecture(sys, ph, tpl, names);
    }

    public sealed record AcceptanceStats(int Records, double PassPct, double AvgAbsDeviationPct, IReadOnlyList<CategoryCount> ByStatus);

    /// <summary>现场验收 / 参数验收：记录数 / 合格率 / 平均绝对偏差 / 按状态。</summary>
    public static AcceptanceStats GetAcceptanceStats(SqliteConnection conn)
    {
        int n = (int)Scalar(conn, "SELECT COUNT(*) FROM parameter_acceptance");
        // status 枚举为英文 pass/warning/fail/pending（见 V011 CHECK 约束）。合格=pass。
        int pass = (int)Scalar(conn, "SELECT COUNT(*) FROM parameter_acceptance WHERE status = 'pass'");
        double avgDev = ScalarDouble(conn, "SELECT COALESCE(AVG(ABS(deviation_pct)),0) FROM parameter_acceptance WHERE deviation_pct IS NOT NULL");
        var byStatus = new List<CategoryCount>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COALESCE(status,'(无)'), COUNT(*) c FROM parameter_acceptance GROUP BY status ORDER BY c DESC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) byStatus.Add(new CategoryCount(rd.GetString(0), rd.GetInt32(1)));
        }
        return new AcceptanceStats(n, n > 0 ? pass * 100.0 / n : 0, avgDev, byStatus);
    }

    public sealed record AcceptancePhaseRow(string Phase, int Records, int Passed, double PassPct);

    /// <summary>分工序验收合格率：parameter_acceptance join process_phase，按工序统计合格率(升序找薄弱环节)。</summary>
    public static List<AcceptancePhaseRow> GetAcceptanceByPhase(SqliteConnection conn)
    {
        var rows = new List<AcceptancePhaseRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COALESCE(p.name,'(未知工序)') ph, COUNT(*) n,
                                   SUM(CASE WHEN a.status='pass' THEN 1 ELSE 0 END) pass
                            FROM parameter_acceptance a LEFT JOIN process_phase p ON p.phase_id = a.phase_id
                            GROUP BY a.phase_id ORDER BY (pass*1.0/COUNT(*)) ASC, n DESC";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            int n = rd.GetInt32(1), pass = rd.GetInt32(2);
            rows.Add(new AcceptancePhaseRow(rd.GetString(0), n, pass, n > 0 ? pass * 100.0 / n : 0));
        }
        return rows;
    }

    public sealed record WorkingFaceRow(string FaceCode, double BenchHeight, double SlopeAngle, double MiningWidth, double AdvanceRate);

    /// <summary>作业面台账：各工作面台阶/坡角/采宽/推进度。</summary>
    public static List<WorkingFaceRow> GetWorkingFaces(SqliteConnection conn)
    {
        var rows = new List<WorkingFaceRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COALESCE(face_code,''), COALESCE(bench_height_m,0), COALESCE(bench_slope_angle_deg,0),
                            COALESCE(mining_width_m,0), COALESCE(advance_rate_m_per_month,0)
                            FROM working_face ORDER BY face_code";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new WorkingFaceRow(rd.GetString(0), rd.GetDouble(1), rd.GetDouble(2), rd.GetDouble(3), rd.GetDouble(4)));
        return rows;
    }

    public sealed record ParamTemplateStats(int Definitions, int TemplateValues, int Phases, int Required);

    /// <summary>参数模板库 / 参数化模板：参数定义数 / 模板取值数 / 涉及工序 / 必填数。</summary>
    public static ParamTemplateStats GetParamTemplates(SqliteConnection conn)
    {
        int defs = (int)Scalar(conn, "SELECT COUNT(*) FROM parameter_definition");
        int vals = (int)Scalar(conn, "SELECT COUNT(*) FROM template_param_value");
        int phases = (int)Scalar(conn, "SELECT COUNT(DISTINCT phase_id) FROM parameter_definition WHERE phase_id IS NOT NULL");
        int req = (int)Scalar(conn, "SELECT COUNT(*) FROM parameter_definition WHERE is_required = 1");
        return new ParamTemplateStats(defs, vals, phases, req);
    }

    public sealed record MonthlyPlanRow(int Year, int Month, double PlanStripWanM3, double PlanCoalWanT, double StripRatio, double AvgDistanceKm, double AvgHeightM);

    /// <summary>月度计划：各期 计划剥离(万m³)/计划煤量(万t)/剥采比/平均运距/平均台阶高。
    /// 剥采比: 存值>0 用存值, 否则由 剥离量/煤量 推导(单位 万m³÷万t=m³/t), 均无则 0。</summary>
    public static List<MonthlyPlanRow> GetMonthlyPlans(SqliteConnection conn)
    {
        var rows = new List<MonthlyPlanRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT year, month, COALESCE(plan_strip_wan_m3,0), COALESCE(plan_coal_wan_t,0),
                            CASE WHEN COALESCE(ratio_strip_coal,0) > 0 THEN ratio_strip_coal
                                 WHEN COALESCE(plan_coal_wan_t,0) > 0 THEN plan_strip_wan_m3 / plan_coal_wan_t
                                 ELSE 0 END,
                            COALESCE(avg_distance_km,0), COALESCE(avg_height_m,0)
                            FROM monthly_plan ORDER BY year, month";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new MonthlyPlanRow(rd.GetInt32(0), rd.GetInt32(1), rd.GetDouble(2), rd.GetDouble(3), rd.GetDouble(4), rd.GetDouble(5), rd.GetDouble(6)));
        return rows;
    }

    public sealed record HaulRoadRow(string RoadId, string Name, double LengthM, double MaxSlopePct, double WidthM, string Condition);

    /// <summary>路况显示 / 运输道路：各路段 长度/最大坡度/宽度/路况。</summary>
    public static List<HaulRoadRow> GetHaulRoads(SqliteConnection conn)
    {
        var rows = new List<HaulRoadRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COALESCE(road_id,''), COALESCE(name,''), COALESCE(length_m,0),
                            COALESCE(max_slope_pct,0), COALESCE(road_width_m,0), COALESCE(condition,'')
                            FROM haul_road ORDER BY road_id";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new HaulRoadRow(rd.GetString(0), rd.GetString(1), rd.GetDouble(2), rd.GetDouble(3), rd.GetDouble(4), rd.GetString(5)));
        return rows;
    }

    public sealed record SlopeDesignRow(string Side, double WorkingAngle, double FinalAngle, double MaxDepth, double SafetyFactor);

    /// <summary>边坡设计：各帮 工作帮坡角/最终帮坡角/最大深度/安全系数。</summary>
    public static List<SlopeDesignRow> GetSlopeDesigns(SqliteConnection conn)
    {
        var rows = new List<SlopeDesignRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COALESCE(side_name,''), COALESCE(working_slope_angle_deg,0), COALESCE(final_slope_angle_deg,0),
                            COALESCE(max_depth_m,0), COALESCE(safety_factor,0) FROM slope_design ORDER BY side_name";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new SlopeDesignRow(rd.GetString(0), rd.GetDouble(1), rd.GetDouble(2), rd.GetDouble(3), rd.GetDouble(4)));
        return rows;
    }

    public sealed record FleetOverview(int Total, IReadOnlyList<CategoryCount> ByStatus, IReadOnlyList<CategoryCount> ByModel);

    /// <summary>机群总览：设备总数 + 按状态 + 按型号(Top)。</summary>
    public static FleetOverview GetFleetOverview(SqliteConnection conn)
    {
        int total = (int)Scalar(conn, "SELECT COUNT(*) FROM equipment");
        var byStatus = GroupCount(conn, "SELECT COALESCE(status,'(未填)'), COUNT(*) c FROM equipment GROUP BY status ORDER BY c DESC");
        var byModel = GroupCount(conn, "SELECT COALESCE(model,'(未填)'), COUNT(*) c FROM equipment GROUP BY model ORDER BY c DESC LIMIT 8");
        return new FleetOverview(total, byStatus, byModel);
    }

    public sealed record CoalClassRow(string Code, string NameCn, double VdafMin, double VdafMax);

    /// <summary>煤种分类：各煤种 代码/名称/挥发分区间(Vdaf)。</summary>
    public static List<CoalClassRow> GetCoalClassification(SqliteConnection conn)
    {
        var rows = new List<CoalClassRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COALESCE(code,''), COALESCE(name_cn,''), COALESCE(vdaf_min,0), COALESCE(vdaf_max,0)
                            FROM coal_classification ORDER BY COALESCE(sort_order,0), code";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new CoalClassRow(rd.GetString(0), rd.GetString(1), rd.GetDouble(2), rd.GetDouble(3)));
        return rows;
    }

    public sealed record SeamBenchRow(string SeamCode, double BenchHeight, double SlopeAngle, double BermWidth, double MinThick);

    /// <summary>煤层台阶参数：各煤层 台阶高/坡角/平台宽/最小可采厚。</summary>
    public static List<SeamBenchRow> GetSeamBenchParams(SqliteConnection conn)
    {
        var rows = new List<SeamBenchRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COALESCE(seam_code,''), COALESCE(bench_height_m,0), COALESCE(bench_slope_angle_deg,0),
                            COALESCE(berm_width_m,0), COALESCE(min_mineable_thick_m,0)
                            FROM seam_bench_param WHERE is_active = 1 ORDER BY seam_code";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new SeamBenchRow(rd.GetString(0), rd.GetDouble(1), rd.GetDouble(2), rd.GetDouble(3), rd.GetDouble(4)));
        return rows;
    }

    public sealed record ConstraintStats(int Total, int Active, IReadOnlyList<CategoryCount> ByType);

    /// <summary>设备约束条件：约束总数 / 在役 / 按约束类型。</summary>
    public static ConstraintStats GetEquipmentConstraints(SqliteConnection conn)
    {
        int total = (int)Scalar(conn, "SELECT COUNT(*) FROM equipment_constraint");
        int active = (int)Scalar(conn, "SELECT COUNT(*) FROM equipment_constraint WHERE is_active = 1");
        var byType = GroupCount(conn, "SELECT COALESCE(constraint_type,'(无)'), COUNT(*) c FROM equipment_constraint GROUP BY constraint_type ORDER BY c DESC");
        return new ConstraintStats(total, active, byType);
    }

    public sealed record GradeRuleRow(string Type, string LevelCode, string LevelName, double Min, double Max);

    /// <summary>煤质分级规则：各分级(类型/级别/区间)。</summary>
    public static List<GradeRuleRow> GetCoalGradeRules(SqliteConnection conn)
    {
        var rows = new List<GradeRuleRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COALESCE(rule_type,''), COALESCE(level_code,''), COALESCE(level_name,''),
                            COALESCE(value_min,0), COALESCE(value_max,0) FROM coal_grade_rule ORDER BY rule_type, COALESCE(sort_order,0)";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new GradeRuleRow(rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.GetDouble(3), rd.GetDouble(4)));
        return rows;
    }

    /// <summary>把一张表整表导出为 CSV 文本(表头 + 数据行, 逗号分隔, 值内含逗号/引号/换行则加引号转义)。可单测。</summary>
    public static string ExportTableToCsv(SqliteConnection conn, string tableName)
    {
        // 表名只允许标识符字符, 防注入。
        foreach (char c in tableName) if (!char.IsLetterOrDigit(c) && c != '_') throw new System.ArgumentException($"非法表名: {tableName}");
        var sb = new System.Text.StringBuilder();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{tableName}\"";
        using var rd = cmd.ExecuteReader();
        int fc = rd.FieldCount;
        for (int i = 0; i < fc; i++) { if (i > 0) sb.Append(','); sb.Append(CsvCell(rd.GetName(i))); }
        sb.Append('\n');
        while (rd.Read())
        {
            for (int i = 0; i < fc; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(CsvCell(rd.IsDBNull(i) ? "" : rd.GetValue(i)?.ToString() ?? ""));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string CsvCell(string v)
    {
        if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }

    public sealed record KpiTrendRow(int Year, double AvgAvailabilityPct, double AvgUtilizationPct);

    /// <summary>KPI 趋势：equipment_kpi_monthly 按年平均 可用率/利用率（比率自适应 0..1 或 0..100）。</summary>
    public static List<KpiTrendRow> GetKpiTrend(SqliteConnection conn)
    {
        var rows = new List<KpiTrendRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT year, COALESCE(AVG(availability),0), COALESCE(AVG(utilization_rate),0)
                            FROM equipment_kpi_monthly GROUP BY year ORDER BY year";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            double av = rd.GetDouble(1), ut = rd.GetDouble(2);
            rows.Add(new KpiTrendRow(rd.GetInt32(0), av <= 1.0 ? av * 100 : av, ut <= 1.0 ? ut * 100 : ut));
        }
        return rows;
    }

    public sealed record ShiftOutputRow(string Shift, int Records, double OutputM3, double WorkHours, double UtilizationPct);

    /// <summary>班次产量对比：各班次 记录数/产量/工时/作业率（production_record 按 shift 分组）。</summary>
    public static List<ShiftOutputRow> GetProductionByShift(SqliteConnection conn)
    {
        var rows = new List<ShiftOutputRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COALESCE(shift,'(无)'), COUNT(*), COALESCE(SUM(output_m3),0),
                            COALESCE(SUM(work_hours),0), COALESCE(SUM(fault_hours),0)
                            FROM production_record GROUP BY shift ORDER BY SUM(output_m3) DESC";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            double wh = rd.GetDouble(3), fh = rd.GetDouble(4);
            double util = (wh + fh) > 1e-9 ? wh / (wh + fh) * 100 : 0;
            rows.Add(new ShiftOutputRow(rd.GetString(0), rd.GetInt32(1), rd.GetDouble(2), wh, util));
        }
        return rows;
    }

    public sealed record FaultRankRow(string EquipmentId, int Events, double DowntimeHours);

    /// <summary>设备故障排名：按累计停机时降序取 topN（找最需检修的设备）。</summary>
    public static List<FaultRankRow> GetFaultByEquipment(SqliteConnection conn, int topN = 8)
    {
        var rows = new List<FaultRankRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT equipment_id, COUNT(*), COALESCE(SUM(duration_hours),0) dt
                            FROM fault_event GROUP BY equipment_id ORDER BY dt DESC LIMIT @n";
        cmd.Parameters.AddWithValue("@n", topN);
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new FaultRankRow(rd.GetString(0), rd.GetInt32(1), rd.GetDouble(2)));
        return rows;
    }

    public sealed record FaultTypeRow(string FaultType, int Events, double DowntimeHours, double DowntimeSharePct);

    /// <summary>故障类型分布：按 fault_type 统计事件数 + 累计停机时 + 停机占比，按停机时降序（看故障构成）。</summary>
    public static List<FaultTypeRow> GetFaultByType(SqliteConnection conn)
    {
        var raw = new List<(string t, int n, double dt)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"SELECT COALESCE(fault_type,'(未分类)'), COUNT(*), COALESCE(SUM(duration_hours),0) dt
                                FROM fault_event GROUP BY fault_type ORDER BY dt DESC";
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) raw.Add((rd.GetString(0), rd.GetInt32(1), rd.GetDouble(2)));
        }
        double tot = 0; foreach (var r in raw) tot += r.dt;
        var rows = new List<FaultTypeRow>();
        foreach (var r in raw) rows.Add(new FaultTypeRow(r.t, r.n, r.dt, tot > 0 ? r.dt / tot * 100 : 0));
        return rows;
    }

    /// <summary>煤质化验段（join borehole 取坐标/孔号）——供 CoalAnalytics 商品煤符合性等分析。</summary>
    public static List<CoalSample> GetCoalSamples(SqliteConnection conn)
    {
        var rows = new List<CoalSample>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT cs.id, COALESCE(b.hole_id,''), cs.seam_code, COALESCE(b.x,0), COALESCE(b.y,0), cs.z_sample,
                                   cs.ad_raw, cs.ad_clean, cs.std_raw, cs.std_clean, cs.qgr_d, cs.qnet_ad, cs.vdaf_raw, cs.vdaf_clean,
                                   cs.sample_thickness, cs.apparent_density, cs.clean_coal_yield, cs.caking_g, cs.plastic_y_mm, cs.coal_type
                            FROM coal_sample cs LEFT JOIN borehole b ON b.id = cs.borehole_id";
        using var rd = cmd.ExecuteReader();
        double? D(int i) => rd.IsDBNull(i) ? (double?)null : rd.GetDouble(i);
        while (rd.Read())
            rows.Add(new CoalSample(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetDouble(3), rd.GetDouble(4), D(5),
                D(6), D(7), D(8), D(9), D(10), D(11), D(12), D(13), D(14), D(15),
                D(16), D(17), D(18), rd.IsDBNull(19) ? null : rd.GetString(19)));
        return rows;
    }

    /// <summary>编组优化规则：dispatch_rule join equipment_model 取 卡车载重/电铲斗容（供 FleetOptimizer）。</summary>
    public static List<FleetDispatchRule> GetFleetDispatchRules(SqliteConnection conn)
    {
        var rows = new List<FleetDispatchRule>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT r.shovel_model, r.truck_model, COALESCE(r.cycle_time_min,0), COALESCE(r.recommended_truck_count,0),
                                   COALESCE(tm.load_t,0), COALESCE(r.bucket_loads_per_truck,0), COALESCE(sm.bucket_m3,0),
                                   COALESCE(r.efficiency_score,0)
                            FROM dispatch_rule r
                            LEFT JOIN equipment_model tm ON tm.model = r.truck_model
                            LEFT JOIN equipment_model sm ON sm.model = r.shovel_model
                            WHERE r.is_active = 1";
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
            rows.Add(new FleetDispatchRule
            {
                ShovelModel = rd.GetString(0), TruckModel = rd.GetString(1),
                CycleTimeMin = rd.GetDouble(2), RecommendedTruckCount = rd.GetInt32(3),
                TruckPayloadT = rd.GetDouble(4), BucketLoadsPerTruck = rd.GetDouble(5),
                ShovelBucketM3 = rd.GetDouble(6), EfficiencyScore = rd.GetInt32(7),
            });
        return rows;
    }

    /// <summary>月度总产量时间序列（万m³，按年月升序）——供 ForecastModels 时序预测。</summary>
    public static List<double> GetMonthlyOutputSeries(SqliteConnection conn)
    {
        var series = new List<double>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SUM(output_m3)/1e4 FROM capacity_monthly GROUP BY year, month ORDER BY year, month";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) series.Add(rd.IsDBNull(0) ? 0 : rd.GetDouble(0));
        return series;
    }

    public sealed record AnnualOutputRow(int Year, double OutputWanM3);

    /// <summary>年度产量趋势：capacity_monthly 按年聚合总产量（万m³）。</summary>
    public static List<AnnualOutputRow> GetAnnualOutput(SqliteConnection conn)
    {
        var rows = new List<AnnualOutputRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT year, SUM(output_m3)/1e4 FROM capacity_monthly GROUP BY year ORDER BY year";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new AnnualOutputRow(rd.GetInt32(0), rd.GetDouble(1)));
        return rows;
    }

    public sealed record SeamQualityRow(string SeamCode, int Samples, double AvgAshPct, double AvgVolatilePct, double AvgCalorificMJ);

    /// <summary>分煤层煤质：各煤层 煤样数 / 平均 灰分Ad / 挥发分Vdaf / 发热量Qnet（coal_sample 按 seam_code 分组）。</summary>
    public static List<SeamQualityRow> GetCoalQualityBySeam(SqliteConnection conn)
    {
        var rows = new List<SeamQualityRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT seam_code, COUNT(*), COALESCE(AVG(ad_raw),0), COALESCE(AVG(vdaf_raw),0), COALESCE(AVG(qnet_ad),0)
                            FROM coal_sample WHERE seam_code IS NOT NULL AND ad_raw IS NOT NULL
                            GROUP BY seam_code ORDER BY seam_code";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new SeamQualityRow(rd.GetString(0), rd.GetInt32(1), rd.GetDouble(2), rd.GetDouble(3), rd.GetDouble(4)));
        return rows;
    }

    public sealed record SeamIntersectRow(string SeamCode, int Holes, double AvgThicknessM, int PinchCount);

    /// <summary>见煤统计 / 煤层对比：各煤层 见煤钻孔数 / 平均采用厚度 / 尖灭孔数（borehole_seam_result 778 行）。</summary>
    public static List<SeamIntersectRow> GetSeamIntersections(SqliteConnection conn)
    {
        var rows = new List<SeamIntersectRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT seam_code, COUNT(*),
                            COALESCE(AVG(NULLIF(adopted_thickness,0)),0),
                            SUM(CASE WHEN status LIKE '%尖灭%' THEN 1 ELSE 0 END)
                            FROM borehole_seam_result WHERE seam_code IS NOT NULL
                            GROUP BY seam_code ORDER BY seam_code";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new SeamIntersectRow(rd.GetString(0), rd.GetInt32(1), rd.GetDouble(2), rd.GetInt32(3)));
        return rows;
    }

    private static List<CategoryCount> GroupCount(SqliteConnection conn, string sql)
    {
        var list = new List<CategoryCount>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) list.Add(new CategoryCount(rd.IsDBNull(0) ? "(无)" : rd.GetString(0), rd.GetInt32(1)));
        return list;
    }

    /// <summary>煤层观测点坐标 + 煤厚：供展绘 + 统计。</summary>
    public static List<(string pointId, double x, double y, double thickness, string seam)> GetObservationPoints(SqliteConnection conn)
    {
        var rows = new List<(string, double, double, double, string)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COALESCE(point_id,''), x, y, COALESCE(seam_thickness,0), COALESCE(seam_code,'')
                            FROM coal_observation_point WHERE x IS NOT NULL AND y IS NOT NULL ORDER BY point_id";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add((rd.GetString(0), rd.GetDouble(1), rd.GetDouble(2), rd.GetDouble(3), rd.GetString(4)));
        return rows;
    }

    public sealed record MineLocationRow(string Code, string Name, double Elevation, string Team, bool Active);

    /// <summary>采区/采场位置列表。</summary>
    public static List<MineLocationRow> GetMineLocations(SqliteConnection conn)
    {
        var rows = new List<MineLocationRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT location_code, COALESCE(name,''), COALESCE(elevation_m,0), COALESCE(team,''), COALESCE(is_active,1)
                            FROM mine_location ORDER BY location_code";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add(new MineLocationRow(rd.GetString(0), rd.GetString(1), rd.GetDouble(2), rd.GetString(3), rd.GetInt64(4) != 0));
        return rows;
    }

    /// <summary>开孔坐标：读所有有平面坐标的钻孔 (hole_id, x, y, z_collar)。供展绘点位。</summary>
    public static List<(string holeId, double x, double y, double z)> GetBoreholeCoords(SqliteConnection conn)
    {
        var rows = new List<(string, double, double, double)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT hole_id, x, y, COALESCE(z_collar,0) FROM borehole
                            WHERE x IS NOT NULL AND y IS NOT NULL ORDER BY hole_id";
        using var rd = cmd.ExecuteReader();
        while (rd.Read()) rows.Add((rd.GetString(0), rd.GetDouble(1), rd.GetDouble(2), rd.GetDouble(3)));
        return rows;
    }

    private static double ScalarDouble(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == null || v is System.DBNull ? 0 : System.Convert.ToDouble(v);
    }

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = cmd.ExecuteScalar();
        return v == null || v is System.DBNull ? 0 : System.Convert.ToInt64(v);
    }
}
