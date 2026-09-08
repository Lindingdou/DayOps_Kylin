using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// CSV 导入的**表格规范**：每种导入的目标表、列（必填/可选、单位、取值）、去重键、示例行。
///
/// 为什么单独立一份：此前规格散在三处 —— Ribbon 里的列提示字符串、
/// <see cref="GeoDataQueries.ImportTemplate"/> 的模板、以及各 Import* 函数里的实际读取逻辑。
/// 三处各写各的必然走样（实测：爆破记录、设备型号两种在模板表里根本没有，用户拿不到模板）。
/// 现在以本类为唯一来源：模板由它生成，文档由它生成，批量导入也由它派发。
///
/// 顺序有讲究：<see cref="All"/> 按外键依赖排列，批量导入照此顺序执行 ——
/// 设备台账不先入库，月度产能/KPI/故障记录里的 equipment_id 就落不到实体上。
/// </summary>
public static class ImportSpec
{
    /// <summary>一列的规格。</summary>
    public sealed class Col
    {
        public required string Name { get; init; }
        public bool Required { get; init; }
        /// <summary>类型提示：文本 / 整数 / 小数 / 日期(yyyy-MM-dd) / 布尔(0|1)。</summary>
        public string Type { get; init; } = "文本";
        /// <summary>单位或取值范围，无则空。</summary>
        public string Note { get; init; } = "";
        public string Example { get; init; } = "";
    }

    /// <summary>一种导入的规格。</summary>
    public sealed class Entry
    {
        public required string Key { get; init; }
        /// <summary>目标表（写进哪张表）。</summary>
        public required string Table { get; init; }
        /// <summary>按哪几列判断"已存在"（重复导入时更新而非重复插入）。</summary>
        public required string UpsertKey { get; init; }
        public required Col[] Cols { get; init; }
        public string Remark { get; init; } = "";
        /// <summary>执行导入。overwrite=true 时同键行更新，false 时跳过。</summary>
        public required Func<DbConnection, IReadOnlyList<IReadOnlyDictionary<string, string>>, bool, GeoDataQueries.ImportOutcome> Run { get; init; }

        public IEnumerable<Col> Required_ => Cols.Where(c => c.Required);
        public IEnumerable<Col> Optional_ => Cols.Where(c => !c.Required);

        /// <summary>表头 + 一行示例（就是"模板"）。</summary>
        public string Template()
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(",", Cols.Select(c => c.Name))).Append('\n');
            sb.Append(string.Join(",", Cols.Select(c => c.Example))).Append('\n');
            return sb.ToString();
        }
    }

    private static Col R(string n, string t, string ex, string note = "") => new() { Name = n, Required = true, Type = t, Example = ex, Note = note };
    private static Col O(string n, string t, string ex, string note = "") => new() { Name = n, Required = false, Type = t, Example = ex, Note = note };

    /// <summary>全部导入类型，**按外键依赖排序**（批量导入照此顺序执行）。</summary>
    public static readonly Entry[] All =
    {
        new()
        {
            Key = "设备型号", Table = "equipment_model", UpsertKey = "model",
            Remark = "设备台账要引用型号，先于台账导入。",
            Run = (c, r, _) => GeoDataQueries.ImportEquipmentModels(c, r),
            Cols = new[]
            {
                R("model", "文本", "WK-35", "型号，唯一"),
                R("category", "文本", "Shovel", "Shovel/Truck/Drill/Dozer/Loader/Grader"),
                O("working_weight_t", "小数", "1350", "工作重量，吨"),
                O("power_kw", "小数", "1600", "功率，千瓦"),
                O("bucket_m3", "小数", "35", "斗容，立方米"),
                O("load_t", "小数", "", "载重，吨"),
                O("dimensions_lwh", "文本", "", "长×宽×高"),
                O("drill_diameter_mm", "小数", "", "钻孔直径，毫米"),
                O("tire_spec", "文本", "", "轮胎规格"),
                O("std_daily_cap_wan_m3", "小数", "", "标准台班产能，万立方米"),
            },
        },
        new()
        {
            Key = "设备台账", Table = "equipment", UpsertKey = "equipment_id",
            Remark = "所有按设备统计的导入（产能/KPI/故障/生产记录）都依赖它，必须最先导。",
            Run = GeoDataQueries.ImportEquipmentLedger,
            Cols = new[]
            {
                R("equipment_id", "文本", "EX-01", "设备编号，唯一"),
                R("category", "文本", "Shovel", "同设备型号的类别"),
                O("model", "文本", "WK-35", "型号，需在设备型号表中"),
                O("manufacturer", "文本", "太重"),
                O("origin", "文本", "国产"),
                O("status", "文本", "在用", "在用/检修/封存/报废"),
            },
        },
        new()
        {
            Key = "生产记录", Table = "production_record", UpsertKey = "equipment_id + date + shift",
            Remark = "班次级原始记录，月度产能可由它汇总。",
            Run = GeoDataQueries.ImportProductionRecords,
            Cols = new[]
            {
                R("equipment_id", "文本", "EX-01"),
                R("date", "日期", "2025-01-15", "yyyy-MM-dd"),
                R("shift", "文本", "A", "班次代号"),
                O("output_m3", "小数", "8500", "产量，立方米"),
                O("work_hours", "小数", "8", "工作小时"),
                O("fault_hours", "小数", "0", "故障小时"),
                O("fault_reason", "文本", "", "故障原因"),
            },
        },
        new()
        {
            Key = "月度产能", Table = "capacity_monthly", UpsertKey = "equipment_id + year + month",
            Run = GeoDataQueries.ImportCapacityMonthly,
            Cols = new[]
            {
                R("equipment_id", "文本", "EX-01"),
                R("year", "整数", "2025"),
                R("month", "整数", "1", "1~12"),
                R("output_m3", "小数", "255000", "月产量，立方米"),
            },
        },
        new()
        {
            Key = "月度KPI", Table = "kpi_monthly", UpsertKey = "equipment_id + year + month",
            Run = GeoDataQueries.ImportKpiMonthly,
            Cols = new[]
            {
                R("equipment_id", "文本", "EX-01"),
                R("year", "整数", "2025"),
                R("month", "整数", "1"),
                R("plan_hours", "小数", "720", "计划小时"),
                R("work_hours", "小数", "610", "实际工作小时"),
                R("fault_hours", "小数", "30", "故障小时"),
                R("availability", "小数", "0.90", "可用率，0~1"),
                R("actual_run_rate", "小数", "0.85", "实际运转率，0~1"),
                R("utilization_rate", "小数", "0.82", "利用率，0~1"),
            },
        },
        new()
        {
            Key = "故障记录", Table = "fault_event", UpsertKey = "equipment_id + date + fault_type",
            Run = (c, r, _) => GeoDataQueries.ImportFaultEvents(c, r),
            Cols = new[]
            {
                R("equipment_id", "文本", "EX-01"),
                R("date", "日期", "2025-01-15"),
                R("fault_type", "文本", "机械故障"),
                O("shift", "文本", "A"),
                O("duration_hours", "小数", "3.5", "停机小时"),
                O("description", "文本", "液压管破裂"),
                O("is_resolved", "布尔", "1", "0=未处理 1=已处理"),
                O("repair_team", "文本", "机修二组"),
            },
        },
        new()
        {
            Key = "爆破记录", Table = "blast_event", UpsertKey = "blast_date + location_code",
            Remark = "此前没有模板可下载，本次补上。",
            Run = (c, r, _) => GeoDataQueries.ImportBlastEvents(c, r),
            Cols = new[]
            {
                R("blast_date", "日期", "2025-01-15"),
                O("location_code", "文本", "N-1200", "爆区编号"),
                O("drill_id", "文本", "DR-02", "钻机编号"),
                O("material", "文本", "岩石"),
                O("diameter_mm", "小数", "250", "孔径，毫米"),
                O("hole_count", "整数", "120", "孔数"),
                O("total_hole_length_m", "小数", "1800", "总孔深，米"),
                O("explosive_kg", "小数", "42000", "炸药量，千克"),
                O("blast_volume_m3", "小数", "95000", "爆破方量，立方米"),
                O("unit_consumption_kg_m3", "小数", "0.44", "单耗，千克/立方米"),
            },
        },
        new()
        {
            Key = "月度计划", Table = "monthly_plan", UpsertKey = "year + month",
            Run = GeoDataQueries.ImportMonthlyPlans,
            Cols = new[]
            {
                R("year", "整数", "2025"),
                R("month", "整数", "1"),
                O("plan_strip_wan_m3", "小数", "1050", "计划剥离，万立方米"),
                O("plan_coal_wan_t", "小数", "120", "计划产煤，万吨"),
                O("plan_outsource_strip_wan_m3", "小数", "0", "外委剥离，万立方米"),
                O("ratio_strip_coal", "小数", "5.5", "剥采比"),
                O("avg_distance_km", "小数", "3.2", "平均运距，千米"),
                O("avg_height_m", "小数", "180", "平均提升高度，米"),
            },
        },
        new()
        {
            Key = "煤质化验", Table = "coal_sample", UpsertKey = "hole_id + seam_code + depth_from",
            Run = GeoDataQueries.ImportCoalSamples,
            Cols = new[]
            {
                R("hole_id", "文本", "1610", "钻孔编号"),
                R("seam_code", "文本", "4-1", "煤层代号"),
                R("depth_from", "小数", "120.5", "起始深度，米"),
                O("depth_to", "小数", "123.7", "终止深度，米"),
                O("sample_thickness", "小数", "3.2", "采样厚度，米"),
                O("z_sample", "小数", "980", "采样高程，米"),
                O("apparent_density", "小数", "1.42", "视密度，吨/立方米"),
                O("ad_raw", "小数", "22.5", "原煤灰分，%"),
                O("ad_clean", "小数", "12.1", "精煤灰分，%"),
                O("std_raw", "小数", "0.8", "原煤全硫，%"),
                O("std_clean", "小数", "0.5", "精煤全硫，%"),
                O("qgr_d", "小数", "24.0", "干基高位发热量，MJ/kg"),
                O("qnet_ad", "小数", "20.5", "空干基低位发热量，MJ/kg"),
                O("vdaf_raw", "小数", "38.0", "原煤挥发分，%"),
                O("vdaf_clean", "小数", "40.0", "精煤挥发分，%"),
                O("caking_g", "小数", "45", "粘结指数"),
                O("plastic_y_mm", "小数", "12", "胶质层厚度，毫米"),
                O("clean_coal_yield", "小数", "72", "精煤回收率，%"),
                O("coal_type", "文本", "1/3焦煤"),
            },
        },
        new()
        {
            Key = "见煤成果", Table = "seam_result", UpsertKey = "hole_id + seam_code",
            Run = GeoDataQueries.ImportSeamResults,
            Cols = new[]
            {
                R("hole_id", "文本", "1610"),
                R("seam_code", "文本", "4-1"),
                O("floor_elevation", "小数", "975.5", "底板高程，米"),
                O("adopted_thickness", "小数", "3.4", "采用厚度，米"),
                O("drill_seam_thickness", "小数", "3.2", "钻孔煤厚，米"),
                O("status", "文本", "正常", "正常/尖灭/缺失"),
            },
        },
        new()
        {
            Key = "观测点", Table = "observation_point", UpsertKey = "point_id",
            Remark = "Ribbon 上叫「见煤观测点」。",
            Run = GeoDataQueries.ImportObservationPoints,
            Cols = new[]
            {
                R("point_id", "文本", "OBS-01"),
                R("seam_code", "文本", "4-1"),
                R("x", "小数", "4512300", "平面坐标"),
                R("y", "小数", "37680500", "平面坐标"),
                O("seam_thickness", "小数", "3.4", "煤厚，米"),
                O("floor_elevation", "小数", "975", "底板高程，米"),
            },
        },
        new()
        {
            Key = "运输道路", Table = "haul_road", UpsertKey = "road_id",
            Run = GeoDataQueries.ImportHaulRoads,
            Cols = new[]
            {
                R("road_id", "文本", "RD-01"),
                R("name", "文本", "主运输道"),
                R("road_type", "文本", "main", "main/branch/dump/temp"),
                R("length_m", "小数", "1200", "长度，米"),
                O("max_slope_pct", "小数", "8", "最大坡度，%"),
                O("avg_slope_pct", "小数", "6", "平均坡度，%"),
                O("road_width_m", "小数", "24", "路宽，米"),
                O("condition", "文本", "good", "good/fair/poor/closed"),
                O("turning_radius_m", "小数", "", "转弯半径，米"),
                O("max_load_t", "小数", "", "限载，吨"),
                O("pavement_type", "文本", "", "路面类型"),
                O("maintenance_team", "文本", "", "养护班组"),
                O("last_maintenance_date", "日期", "", "上次养护"),
                O("start_location", "文本", "采区"),
                O("end_location", "文本", "排土场"),
                O("notes", "文本", ""),
            },
        },
        new()
        {
            Key = "边坡设计", Table = "slope_design", UpsertKey = "side_name",
            Run = (c, r, _) => GeoDataQueries.ImportSlopeDesigns(c, r),
            Cols = new[]
            {
                R("side_name", "文本", "东帮"),
                R("side_type", "文本", "working", "working/final/transition"),
                O("working_slope_angle_deg", "小数", "32", "工作帮坡角，度"),
                O("final_slope_angle_deg", "小数", "45", "最终帮坡角，度"),
                O("max_depth_m", "小数", "300", "最大深度，米"),
                O("safety_factor", "小数", "1.3", "安全系数"),
                O("cohesion_kpa", "小数", "50", "粘聚力，kPa"),
                O("friction_angle_deg", "小数", "28", "内摩擦角，度"),
                O("rock_type", "文本", "砂岩"),
            },
        },
    };

    public static Entry? Find(string key)
        => All.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>文件名 → 导入类型：取文件名里出现的类型名（如 `煤质化验_2025.csv`）。认不出返回 null。</summary>
    public static Entry? FromFileName(string fileName)
    {
        string n = System.IO.Path.GetFileNameWithoutExtension(fileName ?? "");
        // 长名优先，避免「月度产能」被「产能」之类的短名抢先匹配
        foreach (var e in All.OrderByDescending(x => x.Key.Length))
            if (n.Contains(e.Key, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    /// <summary>把全部规格渲染成表格文本（既当 --import-list 的输出，也当交付文档）。</summary>
    public static string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine("CSV 导入表格规范");
        sb.AppendLine("================");
        sb.AppendLine();
        sb.AppendLine("· 首行必须是表头（列名，顺序不限，多余的列会被忽略）");
        sb.AppendLine("· 编码 UTF-8；日期一律 yyyy-MM-dd；小数用点号；不要写千分位分隔符");
        sb.AppendLine("· 重复导入按「去重键」判断：同键的行更新，不会重复插入");
        sb.AppendLine("· 下面按**依赖顺序**列出，批量导入时照此顺序执行");
        sb.AppendLine();
        int i = 0;
        foreach (var e in All)
        {
            sb.AppendLine($"{++i}. {e.Key}   → 表 {e.Table}");
            sb.AppendLine($"   去重键: {e.UpsertKey}");
            if (e.Remark.Length > 0) sb.AppendLine($"   说明  : {e.Remark}");
            sb.AppendLine($"   必填  : {string.Join(", ", e.Required_.Select(c => c.Name))}");
            var opt = e.Optional_.Select(c => c.Name).ToList();
            sb.AppendLine($"   可选  : {(opt.Count > 0 ? string.Join(", ", opt) : "（无）")}");
            foreach (var c in e.Cols)
                sb.AppendLine($"     {(c.Required ? "*" : " ")} {c.Name,-28} {c.Type,-6} {c.Note}");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
