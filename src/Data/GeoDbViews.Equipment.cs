using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Dapper;
using Microsoft.Data.Sqlite;

namespace PitMine3D.Kylin.Data;

// ─────────────────────────────────────────────────────────────────────────────
//  地质与工程信息数据库 · 「设备管理」组(9 个窗口)的查询与纯逻辑。
//  忠实移植原 GeoDataBase 插件:
//    Equipment/Equipment.cs · EquipmentRepository.cs · CsvDataStore.cs · EquipmentModel3DFactory.cs(文件键)
//    EquipmentFleetCockpitWindow · EquipmentManagementWindow · EquipmentDispatchWindow · EquipmentAnalysisWindow
//    EquipmentProductionDataWindow · EquipmentShiftForecastWindow · EquipmentForecastWindow · EquipmentCapabilityWindow
//    DataImportCenter(.cs/Window)
//  全部数据来自 SQLite(equipment / equipment_model / capacity_monthly / equipment_kpi_monthly / production_record /
//  fault_event / dispatch_rule / working_face)。方法/类型带 Eq 前缀避免与其他组冲突。
// ─────────────────────────────────────────────────────────────────────────────
public static partial class GeoDbViews
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ═══════════════════════════════════════════════════════════════════════
    //  ① 设备台账(原 Equipment POCO + EquipmentRepository)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>设备分类枚举名(原 EquipmentCategory)。</summary>
    public static readonly string[] EqCategories = { "Shovel", "Truck", "Drill", "Loader", "Dozer", "Grader", "WaterTruck", "Other" };

    /// <summary>分类中文(原 Equipment.CategoryDisplay)。</summary>
    public static string EqCategoryDisplay(string category) => category switch
    {
        "Shovel" => "电铲", "Truck" => "矿用卡车", "Drill" => "钻机", "Loader" => "前装机",
        "Dozer" => "推土机", "Grader" => "平路机", "WaterTruck" => "水车/水鹤", _ => "其他",
    };

    /// <summary>分类简称(原 FleetCockpit/Capability 的 CatCn)。</summary>
    public static string EqCatCn(string c) => c switch
    {
        "Shovel" => "电铲", "Truck" => "矿卡", "Drill" => "钻机", "Loader" => "前装机",
        "Dozer" => "推土机", "Grader" => "平路机", "WaterTruck" => "水车", _ => "其他",
    };

    /// <summary>分类枚举解析(原 EquipmentRepository.ParseCategory: 大小写不敏感, 未知→Other)。</summary>
    public static string EqParseCategory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Other";
        foreach (var c in EqCategories) if (string.Equals(c, raw.Trim(), StringComparison.OrdinalIgnoreCase)) return c;
        return "Other";
    }

    /// <summary>台账一行(原 GeoDataBase.Equipment.Equipment: 可编辑, 变更通知; 型号级参数自 equipment_model 字典)。</summary>
    public sealed class EquipmentLedgerItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private string _id = "", _category = "Other", _model = "", _manufacturer = "", _origin = "", _serialNumber = "", _assetCode = "";
        private string _workingWeightTon = "", _powerKw = "", _bucketCapacityM3 = "", _loadCapacityTon = "", _dimensionsLwh = "", _maxSpeedKmh = "", _drillDiameterMm = "", _tireSpec = "";
        private string _status = "在用", _cumulativeHours = "", _operatingArea = "", _notes = "";
        private DateTimeOffset? _acquisitionDate, _lastOverhaulDate;

        public string Id { get => _id; set => Set(ref _id, value); }
        public string Category { get => _category; set => Set(ref _category, value); }
        public string Model { get => _model; set => Set(ref _model, value); }
        public string Manufacturer { get => _manufacturer; set => Set(ref _manufacturer, value); }
        public string Origin { get => _origin; set => Set(ref _origin, value); }
        public string SerialNumber { get => _serialNumber; set => Set(ref _serialNumber, value); }
        public string AssetCode { get => _assetCode; set => Set(ref _assetCode, value); }
        public string WorkingWeightTon { get => _workingWeightTon; set => Set(ref _workingWeightTon, value); }
        public string PowerKw { get => _powerKw; set => Set(ref _powerKw, value); }
        public string BucketCapacityM3 { get => _bucketCapacityM3; set => Set(ref _bucketCapacityM3, value); }
        public string LoadCapacityTon { get => _loadCapacityTon; set => Set(ref _loadCapacityTon, value); }
        public string DimensionsLwh { get => _dimensionsLwh; set => Set(ref _dimensionsLwh, value); }
        public string MaxSpeedKmh { get => _maxSpeedKmh; set => Set(ref _maxSpeedKmh, value); }
        public string DrillDiameterMm { get => _drillDiameterMm; set => Set(ref _drillDiameterMm, value); }
        public string TireSpec { get => _tireSpec; set => Set(ref _tireSpec, value); }
        public string Status { get => _status; set => Set(ref _status, value); }
        public DateTimeOffset? AcquisitionDate { get => _acquisitionDate; set => Set(ref _acquisitionDate, value); }
        /// <summary>累计工时(h), 文本(空=NULL)。</summary>
        public string CumulativeHours { get => _cumulativeHours; set => Set(ref _cumulativeHours, value); }
        public DateTimeOffset? LastOverhaulDate { get => _lastOverhaulDate; set => Set(ref _lastOverhaulDate, value); }
        public string OperatingArea { get => _operatingArea; set => Set(ref _operatingArea, value); }
        public string Notes { get => _notes; set => Set(ref _notes, value); }

        public string DisplayLabel => string.IsNullOrEmpty(Model) ? Id : $"{Id} · {Model}";
        public string CategoryDisplay => EqCategoryDisplay(Category);

        private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            if (name == nameof(Id) || name == nameof(Model)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
            if (name == nameof(Category)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CategoryDisplay)));
        }
    }

    private sealed class EqLedgerRaw
    {
        public string EquipmentId { get; set; } = "";
        public string? Category { get; set; }
        public string? Model { get; set; }
        public string? Manufacturer { get; set; }
        public string? Origin { get; set; }
        public string? SerialNumber { get; set; }
        public string? AssetCode { get; set; }
        public string? Status { get; set; }
        public string? AcquisitionDate { get; set; }
        public int? CommissionYear { get; set; }
        public double? CumulativeHours { get; set; }
        public string? LastOverhaulDate { get; set; }
        public string? OperatingArea { get; set; }
        public string? Notes { get; set; }
        public double? WorkingWeightT { get; set; }
        public double? PowerKw { get; set; }
        public double? BucketM3 { get; set; }
        public double? LoadT { get; set; }
        public string? DimensionsLwh { get; set; }
        public double? DrillDiameterMm { get; set; }
        public string? TireSpec { get; set; }
    }

    private static DateTimeOffset? EqParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTime.TryParse(s, Inv, DateTimeStyles.AssumeLocal, out var d)) return new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), TimeSpan.Zero);
        return null;
    }
    private static string Num(double? v) => v.HasValue ? v.Value.ToString("0.###", Inv) : "";

    /// <summary>台账全表(原 EquipmentRepository.Load: equipment ⋈ equipment_model 型号参数), 按编号排序。</summary>
    public static List<EquipmentLedgerItem> EqLoadLedger(SqliteConnection conn)
    {
        var rows = conn.Query<EqLedgerRaw>(@"SELECT e.equipment_id AS EquipmentId, e.category AS Category, e.model AS Model, e.manufacturer AS Manufacturer,
                   e.origin AS Origin, e.serial_number AS SerialNumber, e.asset_code AS AssetCode, e.status AS Status,
                   e.acquisition_date AS AcquisitionDate, e.cumulative_hours AS CumulativeHours, e.last_overhaul_date AS LastOverhaulDate,
                   e.operating_area AS OperatingArea, e.notes AS Notes,
                   m.working_weight_t AS WorkingWeightT, m.power_kw AS PowerKw, m.bucket_m3 AS BucketM3, m.load_t AS LoadT,
                   m.dimensions_lwh AS DimensionsLwh, m.drill_diameter_mm AS DrillDiameterMm, m.tire_spec AS TireSpec
            FROM equipment e LEFT JOIN equipment_model m ON m.model = e.model
            ORDER BY e.equipment_id");
        return rows.Select(r => new EquipmentLedgerItem
        {
            Id = r.EquipmentId, Category = EqParseCategory(r.Category), Model = r.Model ?? "", Manufacturer = r.Manufacturer ?? "",
            Origin = r.Origin ?? "", SerialNumber = r.SerialNumber ?? "", AssetCode = r.AssetCode ?? "", Status = r.Status ?? "在用",
            AcquisitionDate = EqParseDate(r.AcquisitionDate),
            CumulativeHours = r.CumulativeHours.HasValue ? ((int)r.CumulativeHours.Value).ToString(Inv) : "",
            LastOverhaulDate = EqParseDate(r.LastOverhaulDate), OperatingArea = r.OperatingArea ?? "", Notes = r.Notes ?? "",
            WorkingWeightTon = Num(r.WorkingWeightT), PowerKw = Num(r.PowerKw), BucketCapacityM3 = Num(r.BucketM3), LoadCapacityTon = Num(r.LoadT),
            DimensionsLwh = r.DimensionsLwh ?? "", DrillDiameterMm = Num(r.DrillDiameterMm), TireSpec = r.TireSpec ?? "",
        }).ToList();
    }

    /// <summary>保存台账(原 EquipmentRepository.Save): 库中不在列表里的编号删除, 其余逐条 upsert(仅 equipment 表字段; 型号级参数来自字典不回写)。返回 (删除数, 写入数)。</summary>
    public static (int deleted, int upserted) EqSaveLedger(SqliteConnection conn, IEnumerable<EquipmentLedgerItem> items)
    {
        var list = items.ToList();
        var keep = new HashSet<string>(list.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        int deleted = 0, upserted = 0;
        using var tx = conn.BeginTransaction();
        foreach (var id in conn.Query<string>("SELECT equipment_id FROM equipment", transaction: tx).ToList())
            if (!keep.Contains(id)) deleted += conn.Execute("DELETE FROM equipment WHERE equipment_id = @id", new { id }, tx);
        foreach (var e in list)
        {
            string? Nz(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            double? cum = double.TryParse(e.CumulativeHours, NumberStyles.Any, Inv, out var ch) ? ch : (double?)null;
            upserted += conn.Execute(@"INSERT INTO equipment (equipment_id, category, model, manufacturer, origin, serial_number, asset_code, status,
                    acquisition_date, cumulative_hours, last_overhaul_date, operating_area, notes)
                VALUES (@Id, @Category, @Model, @Manufacturer, @Origin, @Serial, @Asset, @Status, @Acq, @Cum, @Over, @Area, @Notes)
                ON CONFLICT(equipment_id) DO UPDATE SET category=excluded.category, model=excluded.model, manufacturer=excluded.manufacturer,
                    origin=excluded.origin, serial_number=excluded.serial_number, asset_code=excluded.asset_code, status=excluded.status,
                    acquisition_date=excluded.acquisition_date, cumulative_hours=excluded.cumulative_hours, last_overhaul_date=excluded.last_overhaul_date,
                    operating_area=excluded.operating_area, notes=excluded.notes, updated_at=CURRENT_TIMESTAMP",
                new
                {
                    e.Id, Category = EqParseCategory(e.Category), Model = Nz(e.Model), Manufacturer = e.Manufacturer, Origin = e.Origin,
                    Serial = Nz(e.SerialNumber), Asset = Nz(e.AssetCode), Status = e.Status,
                    Acq = e.AcquisitionDate?.ToString("yyyy-MM-dd", Inv), Cum = cum, Over = e.LastOverhaulDate?.ToString("yyyy-MM-dd", Inv),
                    Area = Nz(e.OperatingArea), e.Notes,
                }, tx);
        }
        tx.Commit();
        return (deleted, upserted);
    }

    /// <summary>联动: 设备负责的现行工作面(原 WorkingFaces.ByEquipment → Status=="active" 首条)。</summary>
    public sealed record EqWorkingFace(string FaceCode, string? LocationCode, double BenchHeightM, double? WorkingPlatformWidthM,
        string? Material, string? RockHardness, double? AdvanceRateMPerMonth);

    public static EqWorkingFace? EqActiveWorkingFace(SqliteConnection conn, string equipmentId)
        => conn.Query<EqWorkingFace>(@"SELECT face_code AS FaceCode, location_code AS LocationCode, bench_height_m AS BenchHeightM,
                   working_platform_width_m AS WorkingPlatformWidthM, material AS Material, rock_hardness AS RockHardness,
                   advance_rate_m_per_month AS AdvanceRateMPerMonth
            FROM working_face WHERE equipment_id = @id AND status = 'active' ORDER BY id LIMIT 1", new { id = equipmentId }).FirstOrDefault();

    /// <summary>工艺适配度评估(原 EvaluateCompatibility): 台阶高度 vs 设备最大挖掘高度。返回 (文案, 颜色#RRGGBB)。</summary>
    public static (string text, string color) EqEvaluateCompatibility(string model, string category, double benchHeightM)
    {
        double maxReach = model switch { "4100XPC" => 15.0, "PH2800" => 12.0, "WK-35" => 13.0, _ => 12.0 };
        if (category != "Shovel" && category != "Loader") return ("✓ 非采装设备,无台阶约束", "#666666");
        if (benchHeightM <= maxReach + 0.2) return ($"✓ 适配 — {model} 最大挖掘 {maxReach:F0}m ≥ 台阶 {benchHeightM:F1}m", "#2E7D32");
        return ($"⚠ 超能力 — {model} 最大挖掘仅 {maxReach:F0}m < 台阶 {benchHeightM:F1}m", "#C62828");
    }

    // ── 三维展示: PNG 转台序列帧目录键(原 EquipmentModel3DFactory.NormalizeKey / CategoryFileKey + 窗口 TryLoadFrameSequence) ──

    /// <summary>型号 → 目录键: 字母数字小写, '-'/'_' → '-', 其余丢弃("4100XPC"→"4100xpc")。</summary>
    public static string EqNormalizeKey(string raw)
    {
        var sb = new StringBuilder(raw?.Length ?? 0);
        foreach (var ch in raw ?? "")
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == '-' || ch == '_') sb.Append('-');
        }
        return sb.ToString();
    }

    /// <summary>分类 → 目录键(原 CategoryFileKey)。</summary>
    public static string EqCategoryFileKey(string category) => category switch
    {
        "Shovel" => "shovel", "Truck" => "truck", "Drill" => "drill", "Loader" => "loader",
        "Dozer" => "dozer", "Grader" => "grader", "WaterTruck" => "watertruck", _ => "other",
    };

    /// <summary>候选目录键: ① 型号精确 ② 分类回退。</summary>
    public static List<string> EqFrameKeyCandidates(string model, string category)
    {
        var keys = new List<string>();
        if (!string.IsNullOrWhiteSpace(model)) keys.Add(EqNormalizeKey(model));
        keys.Add(EqCategoryFileKey(category));
        return keys;
    }

    /// <summary>在 baseDir/Models3D/{key}/ 下按候选顺序找 ≥2 张 frame_*.png 的目录; 返回帧文件(排序)或 null。</summary>
    public static List<string>? EqResolveFrames(string baseDir, string model, string category)
    {
        foreach (var key in EqFrameKeyCandidates(model, category))
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            var dir = System.IO.Path.Combine(baseDir, "Models3D", key);
            if (!System.IO.Directory.Exists(dir)) continue;
            var files = System.IO.Directory.GetFiles(dir, "frame_*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (files.Count >= 2) return files;
        }
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ② 分析数据源(原 CsvDataStore: 产能/KPI/故障/生产/编组规则)
    // ═══════════════════════════════════════════════════════════════════════

    public sealed class EqCapacityRow
    {
        public string EquipmentId { get; set; } = "";
        public string Model { get; set; } = "";
        public string Category { get; set; } = "";
        public int Year { get; set; }
        public int Month { get; set; }
        public double OutputM3 { get; set; }
    }

    /// <summary>产能历史(原 GetCapacityHistory: capacity_monthly ⋈ equipment 型号/类别)。</summary>
    public static List<EqCapacityRow> EqLoadCapacity(SqliteConnection conn)
        => conn.Query<EqCapacityRow>(@"SELECT c.equipment_id AS EquipmentId, COALESCE(e.model,'') AS Model, COALESCE(e.category,'') AS Category,
                   c.year AS Year, c.month AS Month, c.output_m3 AS OutputM3
            FROM capacity_monthly c LEFT JOIN equipment e ON e.equipment_id = c.equipment_id
            ORDER BY c.equipment_id, c.year, c.month").ToList();

    public sealed class EqKpiRow
    {
        public string EquipmentId { get; set; } = "";
        public string Model { get; set; } = "";
        public int Year { get; set; }
        public int Month { get; set; }
        public double PlanHours { get; set; }
        public double WorkHours { get; set; }
        public double FaultHours { get; set; }
        public double IdleHours { get; set; }
        public double Availability { get; set; }
        public double ActualRunRate { get; set; }
        public double UtilizationRate { get; set; }
        public double Oee => Availability * ActualRunRate * UtilizationRate;
        public string YearMonth => $"{Year}-{Month:D2}";
    }

    /// <summary>月度 KPI(原 GetKpiRecords: 仅在籍设备的 KPI, 型号自台账)。</summary>
    public static List<EqKpiRow> EqLoadKpi(SqliteConnection conn)
        => conn.Query<EqKpiRow>(@"SELECT k.equipment_id AS EquipmentId, COALESCE(e.model,'') AS Model, k.year AS Year, k.month AS Month,
                   k.plan_hours AS PlanHours, k.work_hours AS WorkHours, k.fault_hours AS FaultHours, k.idle_hours AS IdleHours,
                   k.availability AS Availability, k.actual_run_rate AS ActualRunRate, k.utilization_rate AS UtilizationRate
            FROM equipment_kpi_monthly k JOIN equipment e ON e.equipment_id = k.equipment_id
            ORDER BY k.equipment_id, k.year, k.month").ToList();

    public sealed class EqFaultRow
    {
        public string EquipmentId { get; set; } = "";
        public DateTime Date { get; set; }
        public string FaultType { get; set; } = "";
        public double DurationHours { get; set; }
        public string Description { get; set; } = "";
    }

    private sealed class EqFaultRaw { public string EquipmentId { get; set; } = ""; public string? Date { get; set; } public string FaultType { get; set; } = ""; public double DurationHours { get; set; } public string? Description { get; set; } }

    /// <summary>故障事件(原 GetFaultRecords)。</summary>
    public static List<EqFaultRow> EqLoadFaults(SqliteConnection conn)
        => conn.Query<EqFaultRaw>(@"SELECT equipment_id AS EquipmentId, date AS Date, fault_type AS FaultType, duration_hours AS DurationHours, description AS Description
                                     FROM fault_event ORDER BY equipment_id, date, id")
            .Select(r => new EqFaultRow { EquipmentId = r.EquipmentId, Date = EqParseDate(r.Date)?.DateTime ?? DateTime.MinValue, FaultType = r.FaultType, DurationHours = r.DurationHours, Description = r.Description ?? "" })
            .ToList();

    /// <summary>班次生产记录(原 ProductionRecordVm: 可编辑, HasFault 派生)。</summary>
    public sealed class EqProductionRecord : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private string _equipmentId = "", _shift = "A", _faultReason = "";
        private DateTime _date = DateTime.Today;
        private double _outputM3, _workHours, _faultHours;
        public string EquipmentId { get => _equipmentId; set => Set(ref _equipmentId, value); }
        public DateTime Date { get => _date; set { if (Set(ref _date, value)) Raise(nameof(DateText)); } }
        /// <summary>日期文本(yyyy-MM-dd), 表格编辑用。</summary>
        public string DateText
        {
            get => _date.ToString("yyyy-MM-dd", Inv);
            set { if (DateTime.TryParse(value, Inv, DateTimeStyles.AssumeLocal, out var d)) Date = d.Date; }
        }
        public string Shift { get => _shift; set => Set(ref _shift, value); }
        public double OutputM3 { get => _outputM3; set => Set(ref _outputM3, value); }
        public double WorkHours { get => _workHours; set => Set(ref _workHours, value); }
        public double FaultHours { get => _faultHours; set { if (Set(ref _faultHours, value)) Raise(nameof(HasFault)); } }
        public string FaultReason { get => _faultReason; set => Set(ref _faultReason, value ?? ""); }
        public bool HasFault => FaultHours > 0;
        private bool Set<T>(ref T f, T v, [CallerMemberName] string? n = null) { if (Equals(f, v)) return false; f = v; Raise(n!); return true; }
        private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    private sealed class EqProdRaw { public string EquipmentId { get; set; } = ""; public string? Date { get; set; } public string Shift { get; set; } = ""; public double OutputM3 { get; set; } public double WorkHours { get; set; } public double FaultHours { get; set; } public string? FaultReason { get; set; } }

    /// <summary>生产记录全表(原 GetProductionRecords)。</summary>
    public static List<EqProductionRecord> EqLoadProduction(SqliteConnection conn)
        => conn.Query<EqProdRaw>(@"SELECT equipment_id AS EquipmentId, date AS Date, shift AS Shift, output_m3 AS OutputM3, work_hours AS WorkHours,
                                    fault_hours AS FaultHours, fault_reason AS FaultReason FROM production_record ORDER BY equipment_id, date, shift")
            .Select(r => new EqProductionRecord
            {
                EquipmentId = r.EquipmentId, Date = EqParseDate(r.Date)?.DateTime ?? DateTime.MinValue, Shift = r.Shift,
                OutputM3 = r.OutputM3, WorkHours = r.WorkHours, FaultHours = r.FaultHours, FaultReason = r.FaultReason ?? "",
            }).ToList();

    /// <summary>保存生产记录(原 SaveProductionRecords: 全删后重灌, 与"整文件覆盖"对齐)。返回写入行数。</summary>
    public static int EqSaveProduction(SqliteConnection conn, IEnumerable<EqProductionRecord> records)
    {
        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM production_record", transaction: tx);
        int n = 0;
        foreach (var r in records)
            n += conn.Execute(@"INSERT INTO production_record (equipment_id, date, shift, output_m3, work_hours, fault_hours, fault_reason)
                                VALUES (@EquipmentId, @Date, @Shift, @OutputM3, @WorkHours, @FaultHours, @FaultReason)",
                new { r.EquipmentId, Date = r.Date.ToString("yyyy-MM-dd", Inv), r.Shift, r.OutputM3, r.WorkHours, r.FaultHours, FaultReason = string.IsNullOrEmpty(r.FaultReason) ? null : r.FaultReason }, tx);
        tx.Commit();
        return n;
    }

    /// <summary>编组规则(原 CsvDataStore.DispatchRule: dispatch_rule ⋈ equipment_model 斗容/单斗负载/载重)。</summary>
    public sealed class EqDispatchRule
    {
        public string ShovelModel { get; set; } = "";
        public double ShovelBucketM3 { get; set; }
        public double ShovelPayloadT { get; set; }
        public string TruckModel { get; set; } = "";
        public double TruckPayloadT { get; set; }
        public double BucketLoadsPerTruck { get; set; }
        public int RecommendedTruckCount { get; set; }
        public double CycleTimeMin { get; set; }
        public int EfficiencyScore { get; set; }
        public FleetDispatchRule ToFleetRule() => new()
        {
            ShovelModel = ShovelModel, TruckModel = TruckModel, CycleTimeMin = CycleTimeMin, RecommendedTruckCount = RecommendedTruckCount,
            TruckPayloadT = TruckPayloadT, BucketLoadsPerTruck = BucketLoadsPerTruck, ShovelBucketM3 = ShovelBucketM3, EfficiencyScore = EfficiencyScore,
        };
    }

    public static List<EqDispatchRule> EqLoadDispatchRules(SqliteConnection conn)
        => conn.Query<EqDispatchRule>(@"SELECT r.shovel_model AS ShovelModel, COALESCE(sm.bucket_m3,0) AS ShovelBucketM3, COALESCE(sm.load_t,0) AS ShovelPayloadT,
                   r.truck_model AS TruckModel, COALESCE(tm.load_t,0) AS TruckPayloadT, r.bucket_loads_per_truck AS BucketLoadsPerTruck,
                   r.recommended_truck_count AS RecommendedTruckCount, r.cycle_time_min AS CycleTimeMin, r.efficiency_score AS EfficiencyScore
            FROM dispatch_rule r
            LEFT JOIN equipment_model sm ON sm.model = r.shovel_model
            LEFT JOIN equipment_model tm ON tm.model = r.truck_model
            WHERE r.is_active = 1 ORDER BY r.id").ToList();

    /// <summary>在籍设备按型号台数(原 GetInventoryByModel, 编组优化的在籍约束)。</summary>
    public static Dictionary<string, int> EqInventoryByModel(SqliteConnection conn)
    {
        var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in conn.Query<(string model, int n)>("SELECT model, COUNT(*) FROM equipment WHERE model IS NOT NULL AND TRIM(model) <> '' GROUP BY model"))
            d[r.model] = r.n;
        return d;
    }

    /// <summary>平均作业效率 η(原 GetAverageEfficiency: KPI 可用率×利用率均值, 夹 0.4~0.95; 无数据 0.8)。</summary>
    public static double EqAverageEfficiency(IReadOnlyList<EqKpiRow> kpis)
    {
        var oee = kpis.Where(k => k.Availability > 0 && k.UtilizationRate > 0).Select(k => k.Availability * k.UtilizationRate).ToList();
        return oee.Count > 0 ? Math.Clamp(oee.Average(), 0.4, 0.95) : 0.8;
    }

    /// <summary>编组效率评分色(原 ScoreColor/EfficiencyBrush): ≥85 绿 / ≥70 橙 / 红。</summary>
    public static string EqScoreColor(int score) => score >= 85 ? "#388E3C" : score >= 70 ? "#F57C00" : "#C62828";

    // ═══════════════════════════════════════════════════════════════════════
    //  ③ 机群总览 · 领导驾驶舱(原 EquipmentFleetCockpitWindow.LoadData)
    // ═══════════════════════════════════════════════════════════════════════

    public sealed class EqFleetWatchRow
    {
        public string Icon { get; set; } = "";
        public string EquipmentId { get; set; } = "";
        public string Model { get; set; } = "";
        public string Category { get; set; } = "";
        public string AvailPct { get; set; } = "";
        public string ServiceYears { get; set; } = "";
        public string KeyIssue { get; set; } = "";
        public int Severity { get; set; }
    }

    public sealed class EqFleetCockpitResult
    {
        public int InRoster, Total, Green, Yellow, Red, Old, PassA, PassR, PassU;
        public double AvgOee, AvailPass, UnlockWan, FleetCapWan, OldPct, NeckPass;
        public string NeckName = "";
        public List<EqFleetWatchRow> Watch = new();
        /// <summary>各类别可用率达标率(≥90%), 按达标率升序。</summary>
        public List<(string Category, double PassPct)> CategoryPass = new();
        public string VerdictIcon = "", VerdictColor = "#37474F", Headline = "", Action = "", StatusText = "", AsOfText = "";
        public bool HasData;
    }

    /// <summary>机群总览(逐字移植原 LoadData): 在用/租赁设备 × 最新月 KPI 三率 × 最新年年化产能 × 役龄 → 红绿灯/瓶颈/可解锁/清单/横幅。</summary>
    public static EqFleetCockpitResult EqComputeFleetCockpit(SqliteConnection conn, int curYear = 2026)
    {
        var res = new EqFleetCockpitResult();
        var equip = conn.Query<(string id, string? model, string? category, int? commissionYear)>(
            "SELECT equipment_id, model, category, commission_year FROM equipment WHERE status = '在用' OR status = '租赁' ORDER BY equipment_id").ToList();
        res.InRoster = equip.Count;
        var kpiByEq = EqLoadKpi(conn).GroupBy(k => k.EquipmentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(k => k.Year).ThenByDescending(k => k.Month).First(), StringComparer.OrdinalIgnoreCase);
        var capByEq = EqLoadCapacity(conn).GroupBy(c => c.EquipmentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                int yr = g.Max(c => c.Year);
                var yg = g.Where(c => c.Year == yr).ToList();
                int months = yg.Select(c => c.Month).Where(m => m >= 1).Distinct().Count();
                double sum = yg.Sum(c => c.OutputM3);
                return months >= 1 && months < 12 ? sum * 12.0 / months / 1e4 : sum / 1e4;
            }, StringComparer.OrdinalIgnoreCase);

        int total = 0, green = 0, yellow = 0, red = 0, old = 0, passA = 0, passR = 0, passU = 0;
        double oeeSum = 0, unlock = 0, fleetCap = 0;
        var byCatPassA = new Dictionary<string, (int pass, int n)>();
        var watch = new List<EqFleetWatchRow>();
        foreach (var e in equip)
        {
            if (!kpiByEq.TryGetValue(e.id, out var k) || k.Availability <= 0) continue;
            total++;
            double A = k.Availability, R = k.ActualRunRate, U = k.UtilizationRate, oee = A * R * U;
            oeeSum += oee;
            if (A >= 0.90) passA++;
            if (R >= 0.70) passR++;
            if (U >= 0.85) passU++;
            int? sy = e.commissionYear.HasValue ? curYear - e.commissionYear.Value : (int?)null;
            bool aged = sy.HasValue && sy.Value >= 15;
            if (aged) old++;
            double cap = capByEq.TryGetValue(e.id, out var cc) ? cc : 0;
            double theo = oee > 1e-6 ? cap / oee : 0;
            double lA = theo * (1 - A), lR = theo * A * (1 - R), lU = theo * A * R * (1 - U);
            double maxLoss = Math.Max(lA, Math.Max(lR, lU));
            unlock += maxLoss; fleetCap += cap;
            string cat = EqCatCn(e.category ?? "");
            var bc = byCatPassA.TryGetValue(cat, out var v) ? v : (0, 0);
            byCatPassA[cat] = (bc.Item1 + (A >= 0.90 ? 1 : 0), bc.Item2 + 1);
            int sev; string icon;
            if (A < 0.80 || (sy.HasValue && sy.Value >= 20 && A < 0.85)) { red++; sev = 2; icon = "🔴"; }
            else if (A < 0.86 || aged) { yellow++; sev = 1; icon = "🟡"; }
            else { green++; sev = 0; icon = "🟢"; }
            if (sev >= 1)
            {
                string issue = A < 0.80 ? $"可用率低（{A * 100:F0}%），建议纳入大修/更新评估"
                    : (sy.HasValue && sy.Value >= 20) ? $"服役 {sy} 年老龄化、可用率 {A * 100:F0}%，评估更新"
                    : aged ? $"服役 {sy} 年，关注可靠性与关键件磨损"
                    : $"可用率偏低（{A * 100:F0}%），加强检修";
                if (maxLoss > 1)
                {
                    string sb = lA >= maxLoss - 1e-9 ? "可用率" : lR >= maxLoss - 1e-9 ? "作业率" : "利用率";
                    issue += $" · 补{sb}可解锁 {maxLoss:F0} 万m³/年";
                }
                watch.Add(new EqFleetWatchRow
                {
                    Icon = icon, EquipmentId = e.id, Model = e.model ?? "", Category = cat, AvailPct = $"{A * 100:F0}%",
                    ServiceYears = sy.HasValue ? $"{sy} 年" : "—", KeyIssue = issue, Severity = sev,
                });
            }
        }
        res.Total = total; res.Green = green; res.Yellow = yellow; res.Red = red; res.Old = old; res.PassA = passA; res.PassR = passR; res.PassU = passU;
        if (total == 0) { res.StatusText = "无可用 KPI 数据"; return res; }
        res.HasData = true;
        res.Watch = watch.OrderByDescending(w => w.Severity)
            .ThenBy(w => double.TryParse(w.AvailPct.TrimEnd('%'), NumberStyles.Any, Inv, out var a) ? a : 100).ToList();
        res.AvgOee = oeeSum / total; res.AvailPass = 100.0 * passA / total; res.UnlockWan = unlock; res.FleetCapWan = fleetCap;
        var rates = new (string name, double pass)[] { ("可用率", 100.0 * passA / total), ("作业率", 100.0 * passR / total), ("利用率", 100.0 * passU / total) };
        var neck = rates.OrderBy(r => r.pass).First();
        res.NeckName = neck.name; res.NeckPass = neck.pass;
        res.OldPct = 100.0 * old / total;
        double avgOee = res.AvgOee, availPass = res.AvailPass;
        if (red > total * 0.15 || availPass < 40)
        {
            res.VerdictIcon = "🔴"; res.VerdictColor = "#C62828";
            res.Headline = $"全机群产能瓶颈在【{neck.name}】(达标率仅 {neck.pass:F0}%),{res.OldPct:F0}% 机队服役超 15 年老龄化";
            res.Action = $"共 {red} 台需大修/重点、{yellow} 台需盯 → 优先投检修与设备更新,补齐瓶颈约可解锁 {unlock / 1e4:F1} 亿m³/年";
        }
        else if (red > 0 || yellow > total * 0.3)
        {
            res.VerdictIcon = "🟡"; res.VerdictColor = "#EF6C00";
            res.Headline = $"机群整体可控,瓶颈在【{neck.name}】(达标率 {neck.pass:F0}%),{red} 台需重点关注";
            res.Action = $"综合 OEE {avgOee * 100:F0}%、{yellow} 台需盯;针对性检修可解锁约 {unlock / 1e4:F1} 亿m³/年";
        }
        else
        {
            res.VerdictIcon = "🟢"; res.VerdictColor = "#2E7D32";
            res.Headline = $"机群健康 · {green} 台可作主力,综合 OEE {avgOee * 100:F0}%";
            res.Action = $"瓶颈环节【{neck.name}】达标率 {neck.pass:F0}%,持续优化可再解锁约 {unlock / 1e4:F1} 亿m³/年";
        }
        res.CategoryPass = byCatPassA.Select(kv => (kv.Key, Math.Round(100.0 * kv.Value.pass / Math.Max(1, kv.Value.n), 0)))
            .OrderBy(x => x.Item2).ToList();
        res.AsOfText = $"在册 {equip.Count} 台 · 有效评估 {total} 台 · 截至 {curYear}";
        res.StatusText = $"机群:{green} 🟢 / {yellow} 🟡 / {red} 🔴 · 瓶颈 {neck.name} 达标率 {neck.pass:F0}% · 可解锁 {unlock / 1e4:F1} 亿m³/年";
        return res;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ④ 设备生产数据 · 统计分析(原 EquipmentProductionDataWindow.RenderAnalytics)
    // ═══════════════════════════════════════════════════════════════════════

    public sealed class EqProductionAnalytics
    {
        public int Rows, Days, EquipmentCount, FaultCount;
        public double TotalOutWan, DailyAvgWan, ShiftAvgWan, EffRate, FaultRate;
        public List<(string Shift, double Wan)> ByShift = new();
        public List<(DateTime Day, double Wan)> ByDay = new();
        public List<(string Reason, double Hours, double CumPct)> FaultPareto = new();
        public List<(string EquipmentId, double Wan)> TopEquip = new();
        public string Scope = "";
    }

    /// <summary>筛选(原 FilterRow): 设备 / 班次 / 仅含故障。</summary>
    public static List<EqProductionRecord> EqFilterProduction(IEnumerable<EqProductionRecord> rows, string equipment, string shift, bool faultOnly)
        => rows.Where(r => (equipment == "全部" || r.EquipmentId == equipment) && (shift == "全部" || r.Shift == shift) && (!faultOnly || r.FaultHours > 0)).ToList();

    public static EqProductionAnalytics EqAnalyzeProduction(IReadOnlyList<EqProductionRecord> rows)
    {
        var a = new EqProductionAnalytics { Rows = rows.Count };
        if (rows.Count == 0) { a.Scope = "统计范围:当前筛选无数据"; return a; }
        double totalOut = rows.Sum(r => r.OutputM3), totalWork = rows.Sum(r => r.WorkHours), totalFault = rows.Sum(r => r.FaultHours);
        a.Days = rows.Select(r => r.Date.Date).Distinct().Count();
        a.EquipmentCount = rows.Select(r => r.EquipmentId).Distinct().Count();
        a.FaultCount = rows.Count(r => r.FaultHours > 0);
        double denom = totalWork + totalFault;
        a.EffRate = denom > 0 ? totalWork / denom : 0;
        a.FaultRate = denom > 0 ? totalFault / denom : 0;
        a.TotalOutWan = totalOut / 1e4;
        a.DailyAvgWan = a.Days > 0 ? totalOut / 1e4 / a.Days : 0;
        a.ShiftAvgWan = totalOut / rows.Count / 1e4;
        a.Scope = $"统计范围:{rows.Count} 条 · {a.Days} 天 · {a.EquipmentCount} 台设备(随上方筛选联动)";
        var byShift = rows.GroupBy(r => r.Shift).ToDictionary(g => g.Key, g => g.Sum(r => r.OutputM3) / 1e4);
        var shiftLabels = new[] { "A", "B", "C" }.Where(byShift.ContainsKey).ToList();
        foreach (var s in byShift.Keys) if (!shiftLabels.Contains(s)) shiftLabels.Add(s);
        a.ByShift = shiftLabels.Select(s => (s, Math.Round(byShift[s], 0))).ToList();
        a.ByDay = rows.GroupBy(r => r.Date.Date).OrderBy(g => g.Key).Select(g => (g.Key, Math.Round(g.Sum(r => r.OutputM3) / 1e4, 0))).ToList();
        var faults = rows.Where(r => r.FaultHours > 0 && !string.IsNullOrWhiteSpace(r.FaultReason)).GroupBy(r => r.FaultReason)
            .Select(g => (Reason: g.Key, Hours: g.Sum(r => r.FaultHours))).OrderByDescending(x => x.Hours).ToList();
        double faultTot = faults.Sum(x => x.Hours), cum = 0;
        foreach (var f in faults) { cum += f.Hours; a.FaultPareto.Add((f.Reason, Math.Round(f.Hours, 0), Math.Round(cum / faultTot * 100, 1))); }
        a.TopEquip = rows.GroupBy(r => r.EquipmentId).Select(g => (g.Key, g.Sum(r => r.OutputM3) / 1e4)).OrderByDescending(x => x.Item2).Take(10)
            .Select(x => (x.Key, Math.Round(x.Item2, 0))).ToList();
        return a;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ⑤ 设备数据分析(原 EquipmentAnalysisWindow 纯核)
    // ═══════════════════════════════════════════════════════════════════════

    public static int EqOrd(int year, int month) => year * 12 + month;

    /// <summary>时间范围下拉项(原 PopulatePeriodOptions): 近 6/12/24 个月 / 全部 / 各年份。</summary>
    public static List<string> EqPeriodOptions(IEnumerable<EqKpiRow> kpis)
    {
        var l = new List<string> { "近 6 个月", "近 12 个月", "近 24 个月", "全部" };
        l.AddRange(kpis.Select(r => r.Year).Distinct().OrderByDescending(y => y).Select(y => $"{y} 年"));
        return l;
    }

    /// <summary>按时间范围筛选(原 ApplyPeriod: 相对最新月份)。</summary>
    public static List<EqKpiRow> EqApplyPeriod(List<EqKpiRow> kpis, string sel)
    {
        if (kpis.Count == 0) return kpis;
        int maxOrd = kpis.Max(k => EqOrd(k.Year, k.Month));
        if (sel.StartsWith("近"))
        {
            int n = sel.Contains("6 个月") ? 6 : sel.Contains("24 个月") ? 24 : 12;
            return kpis.Where(k => EqOrd(k.Year, k.Month) > maxOrd - n).ToList();
        }
        if (sel.Length >= 4 && int.TryParse(sel.Substring(0, 4), out var yr)) return kpis.Where(k => k.Year == yr).ToList();
        return kpis;
    }

    public static double EqStdDev(IReadOnlyList<double> xs)
    {
        if (xs.Count < 2) return 0;
        var m = xs.Average();
        return Math.Sqrt(xs.Sum(x => (x - m) * (x - m)) / (xs.Count - 1));
    }

    /// <summary>Pearson(原 Correlate: 长度不等/&lt;2/零方差 → 0)。</summary>
    public static double EqCorrelate(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        if (xs.Count != ys.Count || xs.Count < 2) return 0;
        double mx = xs.Average(), my = ys.Average(), sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < xs.Count; i++) { double dx = xs[i] - mx, dy = ys[i] - my; sxy += dx * dy; sxx += dx * dx; syy += dy * dy; }
        var d = Math.Sqrt(sxx * syy);
        return d > 0 ? sxy / d : 0;
    }

    public static (double Slope, double Intercept) EqLinearRegression(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        if (xs.Count < 2) return (0, 0);
        double mx = xs.Average(), my = ys.Average(), sxy = 0, sxx = 0;
        for (int i = 0; i < xs.Count; i++) { sxy += (xs[i] - mx) * (ys[i] - my); sxx += (xs[i] - mx) * (xs[i] - mx); }
        var slope = sxx > 0 ? sxy / sxx : 0;
        return (slope, my - slope * mx);
    }

    /// <summary>Weibull 中位秩回归(原 WeibullFit): ln(−ln(1−F))=β·ln t−β·ln η。</summary>
    public static (double beta, double eta, bool ok) EqWeibullFit(IEnumerable<double> times)
    {
        var t = times.Where(x => x > 0).OrderBy(x => x).ToList();
        int n = t.Count;
        if (n < 3) return (0, 0, false);
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (int i = 0; i < n; i++)
        {
            double F = (i + 1 - 0.3) / (n + 0.4), X = Math.Log(t[i]), Y = Math.Log(-Math.Log(1 - F));
            sx += X; sy += Y; sxx += X * X; sxy += X * Y;
        }
        double denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-9) return (0, 0, false);
        double beta = (n * sxy - sx * sy) / denom;
        double intercept = (sy - beta * sx) / n;
        double eta = Math.Exp(-intercept / Math.Max(1e-6, beta));
        return (beta, eta, beta > 0 && !double.IsNaN(eta) && !double.IsInfinity(eta));
    }

    /// <summary>信息卡(标题/正文/底色/前景色), 供可靠性/建议/诊断面板。</summary>
    public sealed record EqCard(string Icon, string Title, string Detail, string Bg, string Fg);

    /// <summary>可靠性与寿命(原 RenderReliability): MTBF/MTTR/A_ss · Weibull β/η 浴盆定位 · 大修预警 · 数据说明。</summary>
    public static List<EqCard> EqReliabilityCards(List<EqKpiRow> kpis, List<EqFaultRow> faults, string model)
    {
        var cards = new List<EqCard>();
        int nf = faults.Count;
        double totalWork = kpis.Sum(k => k.WorkHours), totalFaultH = faults.Sum(f => f.DurationHours);
        double mtbf = nf > 0 ? totalWork / nf : totalWork, mttr = nf > 0 ? totalFaultH / nf : 0;
        double ass = (mtbf + mttr) > 0 ? mtbf / (mtbf + mttr) : 1;
        cards.Add(new EqCard("", "🕒 可靠性刻度 MTBF / MTTR", $"MTBF {mtbf:F0} h · MTTR {mttr:F1} h · 稳态可用率 A_ss {ass * 100:F1}%（{model}）", "#E3F2FD", "#1976D2"));
        var dates = faults.Select(f => f.Date).OrderBy(d => d).ToList();
        var intervals = new List<double>();
        for (int i = 1; i < dates.Count; i++) { double d = (dates[i] - dates[i - 1]).TotalDays; if (d > 0) intervals.Add(d); }
        var (beta, eta, ok) = EqWeibullFit(intervals);
        if (ok)
        {
            string phase = beta < 0.85 ? "早期失效期(磨合/质量)" : beta <= 1.15 ? "随机失效期(偶发)" : "损耗失效期(老化)";
            string adv = beta < 0.85 ? "加强初期检查/调试,过磨合期后趋稳" : beta <= 1.15 ? "失效随机,按定检维持+备件常备即可" : "已进入耗损期,安排役龄相关定检/大修/备机";
            string bg = beta < 0.85 ? "#FFF8E1" : beta <= 1.15 ? "#E8F5E9" : "#FFEBEE";
            string fg = beta < 0.85 ? "#E65100" : beta <= 1.15 ? "#2E7D32" : "#C62828";
            cards.Add(new EqCard("", "📉 Weibull 失效率 λ(t)", $"形状 β={beta:F2} · 尺度 η={eta:F0} 天 · {phase}", bg, fg));
            cards.Add(new EqCard("", "🛁 浴盆曲线定位 → 维修策略", adv, "#F3E5F5", "#6A1B9A"));
        }
        else cards.Add(new EqCard("", "📉 Weibull 失效率 λ(t)", $"故障间隔样本不足(n={intervals.Count}),接入真实故障台账后可拟合 β/η", "#FFF8E1", "#E65100"));
        if (kpis.Count >= 3)
        {
            double n2 = kpis.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
            for (int i = 0; i < kpis.Count; i++) { sx += i; sy += kpis[i].Availability; sxx += (double)i * i; sxy += (double)i * kpis[i].Availability; }
            double slope = (n2 * sxx - sx * sx) > 1e-9 ? (n2 * sxy - sx * sy) / (n2 * sxx - sx * sx) : 0;
            double latestA = kpis[^1].Availability;
            bool warn = slope < -0.002 || latestA < 0.80 || ass < 0.85;
            cards.Add(new EqCard("", warn ? "🔴 大修预警" : "✅ 寿命状态良好",
                warn ? $"可用率趋势 {slope * 100:+0.00;-0.00}pt/月 · 当前 {latestA * 100:F0}% · 建议纳入大修评估"
                     : $"可用率 {latestA * 100:F0}% · 稳态 {ass * 100:F0}%,暂无需大修",
                warn ? "#FFEBEE" : "#E8F5E9", warn ? "#C62828" : "#2E7D32"));
        }
        cards.Add(new EqCard("", "ℹ️ 数据说明", "故障明细为按考核可用率派生(集中单月),Weibull/大修预警为方法落地示例;接入真实故障/检修/工时台账后即出真值。", "#F5F5F5", "#888888"));
        return cards;
    }

    /// <summary>因素矩阵(原 RenderFactorAnalysis 观测构造): 响应=作业小时; 因子=[出动率,作业小时,故障小时,待命小时,故障次数]。</summary>
    public static (double[] outputs, List<(string Factor, double[] Values)> factors) EqFactorObservations(List<EqKpiRow> kpis, List<EqFaultRow> faults)
    {
        var outputs = kpis.Select(k => k.WorkHours).ToArray();
        var factors = new List<(string, double[])>
        {
            ("出动率", kpis.Select(k => k.Availability).ToArray()),
            ("作业小时", kpis.Select(k => k.WorkHours).ToArray()),
            ("故障小时", kpis.Select(k => k.FaultHours).ToArray()),
            ("待命小时", kpis.Select(k => k.IdleHours).ToArray()),
            ("故障次数", kpis.Select(k => (double)faults.Count(f => f.Date.Year == k.Year && f.Date.Month == k.Month)).ToArray()),
        };
        return (outputs, factors);
    }

    /// <summary>影响因子瀑布(原 RenderWaterfall): contribution = corr(x,y)·std(y), 按 |贡献| 降序。</summary>
    public static List<(string Name, double Contribution)> EqFactorContributions(double[] outputs, List<(string Factor, double[] Values)> factors)
        => factors.Select(f => (f.Factor, EqCorrelate(f.Values, outputs) * EqStdDev(outputs))).OrderByDescending(x => Math.Abs(x.Item2)).ToList();

    /// <summary>相关矩阵(原 RenderCorrelationHeatmap): 因子 + 产能 的 N×N Pearson。</summary>
    public static (List<string> labels, double[,] matrix) EqCorrelationMatrix(double[] outputs, List<(string Factor, double[] Values)> factors)
    {
        var all = new List<(string Name, double[] Values)>(factors) { ("产能", outputs) };
        var m = new double[all.Count, all.Count];
        for (int i = 0; i < all.Count; i++) for (int j = 0; j < all.Count; j++) m[i, j] = EqCorrelate(all[i].Values, all[j].Values);
        return (all.Select(a => a.Name).ToList(), m);
    }

    /// <summary>优化建议(原 RenderSuggestions): 可用率/实动率+待命/主控故障/相关性耦合/兜底。</summary>
    public static List<EqCard> EqSuggestionCards(List<EqKpiRow> kpis, List<EqFaultRow> faults, double[] outputs, List<(string Factor, double[] Values)> factors)
    {
        var cards = new List<EqCard>();
        var latest = kpis.OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).First();
        if (latest.Availability < 0.85)
            cards.Add(new EqCard("🔧", $"可用率偏低 ({latest.Availability * 100:F1}%)",
                $"目标 ≥90%,缺口 {(0.90 - latest.Availability) * 100:F1} 个百分点。建议:① 排查故障停机原因(月累 {latest.FaultHours:F0}h);② 评估大修必要性;③ 如改进 5%,预计月产能提升 {latest.WorkHours * 0.05:F0} 小时作业",
                "#FFEBEE", "#C62828"));
        if (latest.ActualRunRate < 0.70 && latest.IdleHours > latest.WorkHours * 0.15)
            cards.Add(new EqCard("⏱️", "实动率不足 + 待命过长",
                $"实动率 {latest.ActualRunRate * 100:F1}%(目标 ≥70%),待命 {latest.IdleHours:F0}h 占总时 {latest.IdleHours / latest.PlanHours * 100:F0}%。建议:① 优化调度,减少铲车不匹配等待;② 与卡调系统联动,缩短装车间隔;③ 如待命减半,预计实动率提升 5-8 个百分点",
                "#FFF8E1", "#E65100"));
        if (faults.Count > 0)
        {
            var top = faults.GroupBy(f => f.FaultType).Select(g => new { Type = g.Key, Hours = g.Sum(f => f.DurationHours), Count = g.Count() }).OrderByDescending(x => x.Hours).First();
            if (top.Hours > 0)
            {
                double tot = faults.Sum(f => f.DurationHours), pct = tot > 0 ? top.Hours / tot * 100 : 0;
                cards.Add(new EqCard("🎯", $"主控故障类型:{top.Type}",
                    $"占总故障时长的 {pct:F0}%({top.Count} 次 × 累计 {top.Hours:F1}h)。建议:① 优先解决 {top.Type} 相关问题;② 若彻底消除该类型,可解锁 {top.Hours:F1} 小时作业时间;③ 相当于月产能提升约 {pct * 0.6:F0}%(经验保守估计)",
                    "#E3F2FD", "#1976D2"));
            }
        }
        if (factors.Count > 0 && outputs.Length >= 3)
        {
            var neg = factors.Select(f => (f.Factor, R: EqCorrelate(f.Values, outputs))).OrderBy(x => x.R).First();
            if (neg.R < -0.3)
                cards.Add(new EqCard("📉", $"关键负向因子:{neg.Factor}",
                    $"{neg.Factor} 与产能呈负相关 r={neg.R:F2}。建议:重点压降此因素,每降低 1 个标准差,预计产能提升 {Math.Abs(neg.R) * 100:F0}%", "#FFEBEE", "#C62828"));
            var pos = factors.Select(f => (f.Factor, R: EqCorrelate(f.Values, outputs))).OrderByDescending(x => x.R).First();
            if (pos.R > 0.5)
                cards.Add(new EqCard("📈", $"关键正向因子:{pos.Factor}",
                    $"{pos.Factor} 与产能强正相关 r={pos.R:F2}。建议:保持该因素在高位,作为产能稳定的核心保障", "#E8F5E9", "#2E7D32"));
        }
        if (cards.Count == 0)
            cards.Add(new EqCard("✅", "状态良好", "可用率、实动率、利用率均接近或超过目标,无突出短板。建议继续保持当前作业模式。", "#E8F5E9", "#2E7D32"));
        return cards;
    }

    /// <summary>班次效率时间轴(原 RenderShiftView)的 KPI 集合(一天 A/B/C 班)。</summary>
    public sealed class EqShiftDayStats
    {
        public double TotalWork, TotalOutput, TotalFault, AvgEff, PeakEff, WaitMin;
        public int LoadCount;
        public bool AnyFault;
        public string Icon = "🏗";
        public double Ineffective => Math.Max(0, 24 - TotalWork - TotalFault);
        public double DispatchPct => (TotalWork + TotalFault) / 24 * 100;
        public double ActualPct => TotalWork / 24 * 100;
        public double UtilPct => TotalWork / (TotalWork + TotalFault + 0.001) * 100;
        public double AvgPerLoad => LoadCount > 0 ? TotalOutput / LoadCount : 0;
        public double AvgLoadTimeMin => LoadCount > 0 ? TotalWork * 60 / LoadCount : 0;
    }

    /// <summary>设备图标(原按型号关键字): 铲 ⛏ / 卡车 🚛 / 钻机 🔩 / 其他 🏗。</summary>
    public static string EqShiftIcon(string model)
        => model.Contains("XPC") || model.Contains("PH") || model.Contains("WK") ? "⛏" : model.Contains("730") || model.Contains("930") ? "🚛" : model.Contains("DMH") ? "🔩" : "🏗";

    public static EqShiftDayStats EqShiftDay(IReadOnlyList<EqProductionRecord> shifts, string model)
    {
        var s = new EqShiftDayStats { Icon = EqShiftIcon(model) };
        s.TotalWork = shifts.Sum(x => x.WorkHours); s.TotalOutput = shifts.Sum(x => x.OutputM3); s.TotalFault = shifts.Sum(x => x.FaultHours);
        s.AvgEff = s.TotalWork > 0 ? s.TotalOutput / s.TotalWork : 0;
        s.PeakEff = shifts.Where(x => x.WorkHours > 0).Select(x => x.OutputM3 / x.WorkHours).DefaultIfEmpty(0).Max();
        s.LoadCount = (int)(s.TotalOutput / 100);   // 假设单车 100 m³
        s.WaitMin = Math.Max(0, (24 - s.TotalWork) * 60 - s.TotalFault * 60);
        s.AnyFault = shifts.Any(x => x.FaultHours > 0);
        return s;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ⑥ 班次生产效能预测(原 EquipmentShiftForecastWindow 纯核)
    // ═══════════════════════════════════════════════════════════════════════

    public sealed record EqShiftStats(double Mean, double Std, double Min, double Max, double Cv, double P5, double P95);

    public static EqShiftStats EqComputeStats(IReadOnlyList<EqProductionRecord> recs)
    {
        var values = recs.Select(r => r.OutputM3).ToList();
        var mean = values.Average();
        var std = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / Math.Max(1, values.Count - 1));
        var cv = mean > 0 ? std / mean : 0;
        var sorted = values.OrderBy(v => v).ToList();
        double P(double p) => sorted.Count == 0 ? 0 : sorted[(int)Math.Max(0, Math.Min(sorted.Count - 1, p / 100.0 * (sorted.Count - 1)))];
        return new EqShiftStats(mean, std, values.Min(), values.Max(), cv, P(5), P(95));
    }

    /// <summary>熵权法(原 EntropyWeights): 指标离散度越大权重越高。</summary>
    public static double[] EqEntropyWeights(List<double[]> rows)
    {
        int m = rows.Count, p = rows[0].Length;
        double kEnt = 1.0 / Math.Log(m);
        var oneMinusE = new double[p]; double sum = 0;
        for (int j = 0; j < p; j++)
        {
            double col = rows.Sum(r => Math.Max(0, r[j])) + 1e-9, e = 0;
            for (int i = 0; i < m; i++) { double pij = Math.Max(0, rows[i][j]) / col; if (pij > 1e-12) e += pij * Math.Log(pij); }
            oneMinusE[j] = Math.Max(0, 1 - (-kEnt * e)); sum += oneMinusE[j];
        }
        var w = new double[p];
        for (int j = 0; j < p; j++) w[j] = sum > 1e-9 ? oneMinusE[j] / sum : 1.0 / p;
        return w;
    }

    /// <summary>五维得分 [产能强度,稳定性,可用率,效率,可靠性](原 FiveDims; 基准 KPI = 2023-12)。</summary>
    public static double[]? EqFiveDims(string eqId, List<EqProductionRecord> records, List<EqKpiRow> kpis, List<EqFaultRow> faults, int baseYear = 2023, int baseMonth = 12)
    {
        var recs = records.Where(r => r.EquipmentId == eqId && r.WorkHours > 0).ToList();
        var kpi = kpis.FirstOrDefault(k => k.EquipmentId == eqId && k.Year == baseYear && k.Month == baseMonth);
        if (recs.Count == 0 || kpi == null) return null;
        var st = EqComputeStats(recs);
        int nf = faults.Count(f => f.EquipmentId == eqId);
        return new[] { st.Max > 0 ? st.Mean / st.Max : 0, 1 - Math.Min(1, st.Cv), kpi.Availability, kpi.ActualRunRate, Math.Max(0, 1 - 0.08 * nf) };
    }

    /// <summary>全机群熵权(原 ComputeFleetEntropyWeights; &lt;3 台回落专家权重 0.25/0.25/0.20/0.15/0.15)。</summary>
    public static double[] EqFleetEntropyWeights(List<EqProductionRecord> records, List<EqKpiRow> kpis, List<EqFaultRow> faults, int baseYear = 2023, int baseMonth = 12)
    {
        var rows = new List<double[]>();
        foreach (var id in kpis.Where(k => k.Year == baseYear && k.Month == baseMonth).Select(k => k.EquipmentId).Distinct())
        {
            var d = EqFiveDims(id, records, kpis, faults, baseYear, baseMonth);
            if (d != null) rows.Add(d);
        }
        return rows.Count >= 3 ? EqEntropyWeights(rows) : new[] { 0.25, 0.25, 0.20, 0.15, 0.15 };
    }

    /// <summary>型号 → 分类(原 CategoryOfModel, 关键字判定)。</summary>
    public static string EqCategoryOfModel(string model)
    {
        if (model.Contains("XPC") || model.Contains("PH") || model.Contains("WK-")) return "Shovel";
        if (model.Contains("730") || model.Contains("930")) return "Truck";
        if (model.Contains("DMH") || model.Contains("DML") || model.Contains("DM")) return "Drill";
        if (model.Contains("994")) return "Loader";
        if (model.Contains("D10") || model.Contains("D11") || model.Contains("D9")) return "Dozer";
        if (model.Contains("GD")) return "Grader";
        if (model.Contains("水鹤") || model.Contains("水车")) return "WaterTruck";
        return "Other";
    }

    public static int EqCategoryOrder(string cat) => cat switch { "Shovel" => 1, "Truck" => 2, "Drill" => 3, "Loader" => 4, "Dozer" => 5, "Grader" => 6, "WaterTruck" => 7, _ => 99 };

    public static (string Name, string Color, string Icon) EqCategoryStyle(string cat) => cat switch
    {
        "Shovel" => ("电铲", "#FF8F00", "⛏"), "Truck" => ("矿用卡车", "#1976D2", "🚛"), "Drill" => ("钻机", "#388E3C", "🔩"),
        "Loader" => ("前装机", "#FB8C00", "🏗"), "Dozer" => ("推土机", "#6D4C41", "🚜"), "Grader" => ("平路机", "#576C6F", "🛣"),
        "WaterTruck" => ("水车/水鹤", "#1E88E5", "💧"), _ => ("其他", "#757575", "🔧"),
    };

    public static string EqUnitFor(string model) => model.Contains("DMH") || model.Contains("DML") ? "m" : "m³";

    /// <summary>风险等级(原 RenderTopKpis): riskScore = CV×50 + 近月故障×8。</summary>
    public static (string text, string hint, double score) EqRiskLevel(double cv, int faultPerMonth)
    {
        double s = cv * 50 + faultPerMonth * 8;
        if (s < 15) return ("🟢 低", "稳定可信赖,可作为主力", s);
        if (s < 35) return ("🟡 中", "波动可控,正常调度", s);
        return ("🔴 高", "建议加强巡检或备机", s);
    }

    /// <summary>综合潜力评分(原): 可用率25 + 实动率25 + 稳定性20 + 预测/峰值15 + 可靠性15。</summary>
    public static (int score, string hint) EqPotentialScore(EqKpiRow kpi, EqShiftStats stats, double forecast, int faultPerMonth)
    {
        int score = (int)Math.Clamp(kpi.Availability * 25 + kpi.ActualRunRate * 25 + (1 - Math.Min(1, stats.Cv)) * 20
            + Math.Min(1, forecast / Math.Max(stats.Max, 1)) * 15 + Math.Max(0, 1 - faultPerMonth / 10.0) * 15, 0, 100);
        string hint = score >= 80 ? "潜力突出,推荐高频调度" : score >= 60 ? "中等潜力,常规使用" : score >= 40 ? "潜力受限,优化空间大" : "需要排查,建议大修评估";
        return (score, hint);
    }

    /// <summary>五维评分(原 RenderScores)。</summary>
    public static (int output, int stability, int avail, int eff, int reliability) EqFiveScores(EqShiftStats stats, EqKpiRow kpi, int faultCount)
        => ((int)Math.Clamp(stats.Mean / Math.Max(stats.Max, 1) * 100, 0, 100), (int)Math.Clamp((1 - Math.Min(1, stats.Cv)) * 100, 0, 100),
            (int)Math.Clamp(kpi.Availability * 100, 0, 100), (int)Math.Clamp(kpi.ActualRunRate * 100, 0, 100), (int)Math.Clamp(100 - faultCount * 8, 0, 100));

    /// <summary>直方图(原 RenderHistogram): 10 桶。</summary>
    public static (string[] labels, int[] counts) EqHistogram(IReadOnlyList<double> values, double min, double max, int bins = 10)
    {
        if (Math.Abs(max - min) < 0.01) max = min + 1;
        var binSize = (max - min) / bins;
        var counts = new int[bins]; var labels = new string[bins];
        for (int i = 0; i < bins; i++)
        {
            double lo = min + i * binSize, hi = lo + binSize;
            counts[i] = values.Count(v => v >= lo && v < hi);
            if (i == bins - 1) counts[i] += values.Count(v => v == max);
            labels[i] = $"{lo:F0}";
        }
        return (labels, counts);
    }

    /// <summary>概率化预测(原 RenderProbabilistic): 经验分布重心平移到点预测 → P10/P50/P90 + 达产概率。</summary>
    public static (double p10, double p50, double p90, double meetProb) EqProbabilistic(IReadOnlyList<double> values, double next)
    {
        var vals = values.OrderBy(v => v).ToList();
        double mean = vals.Average();
        double Q(double p) => vals[(int)Math.Clamp(p / 100.0 * (vals.Count - 1), 0, vals.Count - 1)];
        double shift = next - mean;
        return (Math.Max(0, Q(10) + shift), Q(50) + shift, Q(90) + shift, 100.0 * vals.Count(v => v >= mean) / vals.Count);
    }

    /// <summary>编组建议(原 RenderDispatchSuggestion)。</summary>
    public static (string verdict, string role, string hint) EqDispatchVerdict(double cv, double availability)
    {
        if (cv < 0.2 && availability > 0.85) return ("✅ 适合作为主力机型", "建议优先调度,可承担稳定大产量任务", "搭配新班长 / 新司机时也可保证产能下限");
        if (cv < 0.4) return ("🟡 适合配合主力使用", "可作为辅助力量,与主力机型搭配运行", "高峰期可补位,空闲期可做轮班维护");
        return ("🔴 不建议主力调度", "建议安排到非关键作业 / 准备大修", "波动过大,排产时需预留 20% 以上的备份产能");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ⑦ 设备效能预测 What-if(原 EquipmentForecastWindow 基线与标杆; 模型见 EfficiencyWhatIf)
    // ═══════════════════════════════════════════════════════════════════════

    public sealed record EqEquipmentSummary(string EquipmentId, string Model, string Category)
    {
        public override string ToString() => EquipmentId;
    }

    /// <summary>产能表出现的设备清单(原 LoadData: 按编号排序, 型号/类别取首条)。</summary>
    public static List<EqEquipmentSummary> EqEquipmentSummaries(IEnumerable<EqCapacityRow> capacity)
        => capacity.GroupBy(r => r.EquipmentId).Select(g => new EqEquipmentSummary(g.Key, g.First().Model, g.First().Category)).OrderBy(x => x.EquipmentId).ToList();

    public sealed record EqForecastBaseline(double OutputWan, double Availability, double RunRate, double FaultHours, double PlanHours, string Model);

    /// <summary>基线(原 OnEquipmentSelectionChanged): 最近 12 月产能均值(万m³) + 最新 KPI(无则 0.85/0.70/80h/768h 兜底)。</summary>
    public static EqForecastBaseline EqForecastBaselineOf(List<EqCapacityRow> capacity, List<EqKpiRow> kpis, EqEquipmentSummary sel)
    {
        var recent = capacity.Where(r => r.EquipmentId == sel.EquipmentId && r.OutputM3 > 0).OrderByDescending(r => r.Year).ThenByDescending(r => r.Month).Take(12).ToList();
        double baseOut = recent.Count > 0 ? recent.Average(r => r.OutputM3) / 10000.0 : 0;
        var kpi = kpis.Where(k => k.EquipmentId == sel.EquipmentId).OrderByDescending(k => k.Year).ThenByDescending(k => k.Month).FirstOrDefault();
        return kpi == null
            ? new EqForecastBaseline(baseOut, 0.85, 0.70, 80, 768, sel.Model)
            : new EqForecastBaseline(baseOut, kpi.Availability, kpi.ActualRunRate, kpi.FaultHours, kpi.PlanHours > 0 ? kpi.PlanHours : 768, sel.Model);
    }

    /// <summary>行业标杆对照(原 Recalculate: 同型号 2023 年各台月均 → 上 10% / 均值)。</summary>
    public static (string text, string hint) EqPeerBenchmark(List<EqCapacityRow> capacity, string model, double simulated, int year = 2023)
    {
        var peers = capacity.Where(r => r.Model == model && r.Year == year && r.OutputM3 > 0).GroupBy(r => r.EquipmentId)
            .Select(g => g.Average(r => r.OutputM3) / 10000.0).OrderByDescending(v => v).ToList();
        if (peers.Count < 2) return ("样本不足", "同型号 < 2 台");
        var p90 = peers[(int)(peers.Count * 0.1)];
        if (simulated >= p90) return ("达到型号 Top 10%", $"P90 = {p90:F1} 万 m³");
        if (simulated >= peers.Average()) return ("高于型号均值", $"均值 = {peers.Average():F1}");
        return ("仍低于型号均值", "继续加大杠杆");
    }

    /// <summary>综合结论(原 RenderConclusion)。</summary>
    public static string EqForecastConclusion(double baseOutput, double c1, double c2, double c3, double c4, double l1, double l2, double l3, double l4, double actualGain, double delta, double simulated)
    {
        var contributions = new[] { ("故障降低", c1, l1), ("出动率提升", c2, l2), ("装载效率", c3, l3), ("运距优化", c4, l4) }.OrderByDescending(x => x.Item2).ToArray();
        if (!contributions.Any(x => x.Item2 > 0.001)) return "尚未启动任何提升路径。拉动左侧任一滑块,或点击\"预设:激进 / 保守\" 看典型场景效果。";
        var sb = new StringBuilder();
        sb.Append($"若同时实施 {contributions.Count(c => c.Item3 > 0)} 项措施,月产能将从 ");
        sb.Append($"基线 {baseOutput:F1} 万 m³ 提升到 {simulated:F1} 万 m³,");
        sb.Append($"净增 {delta:F1} 万 m³(+{actualGain * 100:F1}%)。\n\n");
        sb.Append("◆ 优先级建议:\n");
        for (int i = 0; i < contributions.Length; i++)
        {
            var c = contributions[i];
            if (c.Item2 < 0.001) continue;
            sb.Append($"  {i + 1}. {c.Item1}({c.Item3:F0}% 杠杆 → 贡献 +{c.Item2 * 100:F1}%)\n");
        }
        sb.Append($"\n◆ 年化效益估算:+{delta * 12:F0} 万 m³/年");
        if (l1 > 50) sb.Append("\n⚠️ 故障降低 > 50% 较激进,需要配套大修/换新主件,投入较大");
        if (l2 > 20) sb.Append("\n⚠️ 出动率提升 > 20% 通常需要调度系统改造");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ⑧ 设备能力分析(原 EquipmentCapabilityWindow 纯核)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>按年汇总(原 YearlyAnnualized): 不足 12 月(月度数据)按 ×12/月数 年化; month=0 视为年度点。</summary>
    public static List<(int Year, double Total, bool Partial)> EqYearlyAnnualized(IEnumerable<EqCapacityRow> records)
        => records.GroupBy(r => r.Year).Select(g =>
        {
            bool hasAnnual = g.Any(r => r.Month == 0);
            int months = g.Where(r => r.Month >= 1).Select(r => r.Month).Distinct().Count();
            double sum = g.Sum(r => r.OutputM3);
            bool partial = !hasAnnual && months >= 1 && months < 12;
            return (Year: g.Key, Total: partial ? sum * 12.0 / months : sum, Partial: partial);
        }).OrderBy(x => x.Year).ToList();

    private static double EqLatestAnnualWan(IEnumerable<EqCapacityRow> g)
    {
        int yr = g.Max(r => r.Year);
        var yg = g.Where(r => r.Year == yr).ToList();
        int months = yg.Select(r => r.Month).Where(mm => mm >= 1).Distinct().Count();
        double sum = yg.Sum(r => r.OutputM3);
        return (months >= 1 && months < 12 ? sum * 12.0 / months : sum) / 1e4;
    }

    /// <summary>同型号各设备最新年份台年产能(万m³, 原 PeerLatestOutput), 仅 &gt;0。</summary>
    public static List<(string Id, double Wan)> EqPeerLatestOutput(IEnumerable<EqCapacityRow> all, string model)
        => all.Where(r => r.Model == model).GroupBy(r => r.EquipmentId).Select(g => (Id: g.Key, Wan: EqLatestAnnualWan(g))).Where(x => x.Wan > 0).ToList();

    /// <summary>同类各型号对标(原 CategoryModelStats): 台均台年产能 + 可用率均值 + 台数, 按产能降序。</summary>
    public static List<(string Model, double OutWan, double AvailPct, int Units)> EqCategoryModelStats(IEnumerable<EqCapacityRow> all, string category, IReadOnlyDictionary<string, double> availByEq)
    {
        var perUnit = all.Where(r => r.Category == category).GroupBy(r => r.EquipmentId)
            .Select(g => (Id: g.Key, Model: g.First().Model, Wan: EqLatestAnnualWan(g))).Where(x => x.Wan > 0).ToList();
        return perUnit.GroupBy(x => x.Model).Select(g =>
        {
            var avs = g.Select(x => availByEq.TryGetValue(x.Id, out var a) ? a : 0.0).Where(a => a > 0).ToList();
            return (Model: g.Key, OutWan: g.Average(x => x.Wan), AvailPct: (avs.Count > 0 ? avs.Average() : 0) * 100, Units: g.Count());
        }).OrderByDescending(x => x.OutWan).ToList();
    }

    /// <summary>每台最新 KPI 可用率(原 _oeeByEq.A)。</summary>
    public static Dictionary<string, double> EqLatestAvailByEq(IEnumerable<EqKpiRow> kpis)
        => kpis.GroupBy(k => k.EquipmentId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.Year).ThenByDescending(x => x.Month).First().Availability, StringComparer.OrdinalIgnoreCase);

    public sealed class EqCapabilityKpis
    {
        public int PeakYear, LatestYear, SpanYears, Rank, PeerCount;
        public double PeakWan, LatestWan, Ratio, Cagr;
        public bool LatestPartial, HasCagr;
        public string DeclineHint = "";
    }

    /// <summary>顶部 4 卡(原 RenderKpis): 峰值 / 最新年(年化) / CAGR / 同型号排名。</summary>
    public static EqCapabilityKpis EqCapabilityKpisOf(List<EqCapacityRow> all, EqEquipmentSummary sel, List<EqCapacityRow> records)
    {
        var k = new EqCapabilityKpis();
        var yearly = EqYearlyAnnualized(records);
        if (yearly.Count == 0) return k;
        var peak = yearly.OrderByDescending(y => y.Total).First();
        var latest = yearly.OrderByDescending(y => y.Year).First();
        k.PeakYear = peak.Year; k.PeakWan = peak.Total / 1e4; k.LatestYear = latest.Year; k.LatestWan = latest.Total / 1e4; k.LatestPartial = latest.Partial;
        k.Ratio = peak.Total > 0 ? latest.Total / peak.Total : 1;
        if (yearly.Count >= 2)
        {
            var first = yearly.First();
            k.SpanYears = latest.Year - first.Year;
            k.Cagr = k.SpanYears > 0 && first.Total > 0 ? Math.Pow(latest.Total / first.Total, 1.0 / k.SpanYears) - 1 : 0;
            k.HasCagr = true;
            k.DeclineHint = k.Cagr < -0.05 ? "明显衰退,建议大修评估" : k.Cagr < 0 ? "缓慢下滑" : "保持/上升";
        }
        else k.DeclineHint = "样本不足";
        var peers = EqPeerLatestOutput(all, sel.Model).OrderByDescending(x => x.Wan).ToList();
        k.Rank = peers.FindIndex(x => x.Id == sel.EquipmentId) + 1; k.PeerCount = peers.Count;
        return k;
    }

    /// <summary>决策结论横幅(原 RenderVerdict)。</summary>
    public static (string icon, string color, string headline, string action) EqCapabilityVerdict(EqEquipmentSummary sel, List<EqCapacityRow> records, EqKpiRow? kpi)
    {
        var yearly = EqYearlyAnnualized(records);
        if (yearly.Count == 0) return ("⚙", "#37474F", "选择左侧设备,给出一句话能力结论", "");
        var peak = yearly.OrderByDescending(y => y.Total).First();
        var latest = yearly.OrderByDescending(y => y.Year).First();
        var first = yearly.First();
        double ratio = peak.Total > 0 ? latest.Total / peak.Total : 1;
        int span = latest.Year - first.Year;
        double cagr = span > 0 && first.Total > 0 ? Math.Pow(latest.Total / first.Total, 1.0 / span) - 1 : 0;
        double latestWan = latest.Total / 1e4;
        string shortBoard = ""; double unlock = 0;
        if (kpi != null && kpi.Availability > 0)
        {
            double A = kpi.Availability, R = kpi.ActualRunRate, U = kpi.UtilizationRate, oee = A * R * U;
            if (oee > 1e-6)
            {
                double theo = latestWan / oee, lA = theo * (1 - A), lR = theo * A * (1 - R), lU = theo * A * R * (1 - U);
                double mx = Math.Max(lA, Math.Max(lR, lU));
                shortBoard = lA >= mx - 1e-9 ? "可用率" : lR >= mx - 1e-9 ? "作业率" : "利用率";
                unlock = mx;
            }
        }
        string gap = shortBoard != "" ? $" · 补齐{shortBoard}短板可解锁约 {unlock:F0} 万m³/年" : "";
        if (ratio >= 0.9 && cagr >= -0.02)
            return ("🟢", "#2E7D32", $"{sel.Model} 能力健康 · 台年 {latestWan:F0} 万m³(达峰值 {ratio * 100:F0}%)", $"CAGR {cagr * 100:+0.0;-0.0}%,可作主力、无需大修{gap}");
        if (ratio >= 0.7 && cagr >= -0.05)
            return ("🟡", "#EF6C00", $"{sel.Model} 能力轻度下滑 · 台年 {latestWan:F0} 万m³(达峰值 {ratio * 100:F0}%)", $"CAGR {cagr * 100:+0.0;-0.0}%,关注关键件磨损{gap}");
        return ("🔴", "#C62828", $"{sel.Model} 能力明显衰退 · 台年 {latestWan:F0} 万m³(仅峰值 {ratio * 100:F0}%)", $"CAGR {cagr * 100:+0.0;-0.0}%,建议纳入大修评估{gap}");
    }

    /// <summary>损失归因(原 RenderLossWaterfall): 理论上限 → −A/−R/−U → 实际。null=无法分解(附说明)。</summary>
    public static (double theo, double lossA, double lossR, double lossU, double actual, double oee, string summary)? EqLossWaterfall(List<EqCapacityRow> records, EqKpiRow? kpi, out string note)
    {
        note = "";
        if (kpi == null || kpi.Availability <= 0) { note = "该设备暂无 KPI 三率数据,无法做损失分解。"; return null; }
        double A = kpi.Availability, R = kpi.ActualRunRate, U = kpi.UtilizationRate, oee = A * R * U;
        var yearly = EqYearlyAnnualized(records);
        double actual = yearly.Count > 0 ? yearly.OrderByDescending(y => y.Year).First().Total / 1e4 : 0;
        if (oee <= 1e-6 || actual <= 0) { note = "数据不足,无法做损失分解。"; return null; }
        double theo = actual / oee, lossA = theo * (1 - A), lossR = theo * A * (1 - R), lossU = theo * A * R * (1 - U);
        double maxLoss = Math.Max(lossA, Math.Max(lossR, lossU));
        string sb = lossA >= maxLoss - 1e-9 ? "可用率(能不能开动)" : lossR >= maxLoss - 1e-9 ? "作业率(计划内运转占比)" : "利用率(运转时负载)";
        string summary = $"理论上限 {theo:F0} → 实际 {actual:F0} 万m³(OEE {oee * 100:F0}%)。三率损失:可用率 −{lossA:F0}、作业率 −{lossR:F0}、利用率 −{lossU:F0} 万m³。主控短板 = {sb},损失最大、乘性放大,优先补。";
        return (theo, lossA, lossR, lossU, actual, oee, summary);
    }

    /// <summary>自动诊断结论卡片(原 RenderDiagnosis)。</summary>
    public static List<EqCard> EqCapabilityDiagnosis(List<EqCapacityRow> all, EqEquipmentSummary sel, List<EqCapacityRow> records)
    {
        var cards = new List<EqCard>();
        var yearly = EqYearlyAnnualized(records);
        if (yearly.Count < 2) { cards.Add(new EqCard("⚠️", "样本不足", "历史数据少于 2 年,无法做趋势诊断", "#FFF8E1", "#E65100")); return cards; }
        var peak = yearly.OrderByDescending(y => y.Total).First();
        var latest = yearly.OrderByDescending(y => y.Year).First();
        var ratio = peak.Total > 0 ? latest.Total / peak.Total : 1.0;
        var years = latest.Year - yearly.First().Year;
        var cagr = years > 0 && yearly.First().Total > 0 ? Math.Pow(latest.Total / yearly.First().Total, 1.0 / years) - 1 : 0;
        var declines = new List<(int Year, double Rate)>();
        for (int i = 1; i < yearly.Count; i++)
            declines.Add((yearly[i].Year, yearly[i - 1].Total > 0 ? (yearly[i].Total - yearly[i - 1].Total) / yearly[i - 1].Total : 0));
        var worstYear = declines.OrderBy(x => x.Rate).FirstOrDefault();
        var monthly = records.GroupBy(r => r.Month).Select(g => new { Month = g.Key, Avg = g.Average(r => r.OutputM3) }).OrderBy(x => x.Month).ToList();
        var bestMonth = monthly.OrderByDescending(m => m.Avg).FirstOrDefault();
        var worstMonth = monthly.OrderBy(m => m.Avg).FirstOrDefault();
        var peers = EqPeerLatestOutput(all, sel.Model).OrderByDescending(x => x.Wan).ToList();
        int rank = peers.FindIndex(x => x.Id == sel.EquipmentId) + 1;

        if (ratio >= 0.9) cards.Add(new EqCard("✅", "产能保持", $"当前为峰值 {ratio * 100:F0}%,状态良好", "#E8F5E9", "#2E7D32"));
        else if (ratio >= 0.7) cards.Add(new EqCard("⚠️", "轻度衰减", $"当前仅为峰值 {ratio * 100:F0}%,关注关键件磨损", "#FFF8E1", "#E65100"));
        else cards.Add(new EqCard("🔴", "明显衰减", $"当前仅为峰值 {ratio * 100:F0}%,建议安排大修", "#FFEBEE", "#C62828"));
        cards.Add(new EqCard("📉", cagr < 0 ? $"CAGR {cagr * 100:F1}%" : $"CAGR +{cagr * 100:F1}%", $"近 {years} 年年均{(cagr < 0 ? "下降" : "提升")}",
            cagr < -0.05 ? "#FFEBEE" : "#E8F5E9", cagr < -0.05 ? "#C62828" : "#2E7D32"));
        if (worstYear.Rate < -0.1) cards.Add(new EqCard("📌", $"{worstYear.Year} 年拐点", $"环比 {worstYear.Rate * 100:F0}%,排查该年事件", "#FFEBEE", "#C62828"));
        if (bestMonth != null && worstMonth != null)
        {
            var seasonDelta = bestMonth.Avg > 0 ? (bestMonth.Avg - worstMonth.Avg) / bestMonth.Avg : 0;
            if (seasonDelta > 0.3) cards.Add(new EqCard("🌡️", $"季节波动 {seasonDelta * 100:F0}%", $"{bestMonth.Month}月最强,{worstMonth.Month}月最弱", "#FFF8E1", "#E65100"));
            else cards.Add(new EqCard("🌤️", "季节稳定", "全年波动 < 30%", "#E8F5E9", "#2E7D32"));
        }
        if (rank > 0 && peers.Count > 1)
        {
            if (rank == 1) cards.Add(new EqCard("🏆", "型号 #1", "本年同型号产能第一", "#FFF8E1", "#E65100"));
            else if (rank <= peers.Count / 2) cards.Add(new EqCard("📊", $"型号 #{rank}/{peers.Count}", "处于同型号上半区", "#E8F5E9", "#2E7D32"));
            else cards.Add(new EqCard("📊", $"型号 #{rank}/{peers.Count}", "处于同型号下半区,优化空间大", "#FFEBEE", "#C62828"));
        }
        return cards;
    }

    /// <summary>环比增长率(原 RenderDeclineWaterfall): (年, 增长率%)。</summary>
    public static List<(int Year, double RatePct)> EqYoyRates(List<(int Year, double Total, bool Partial)> yearly)
    {
        var l = new List<(int, double)>();
        for (int i = 1; i < yearly.Count; i++)
        {
            var prev = yearly[i - 1].Total;
            if (prev <= 0) continue;
            l.Add((yearly[i].Year, (yearly[i].Total - prev) / prev * 100));
        }
        return l;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ⑨ 数据导入导出中心(原 DataImportCenter.cs 五类 ImportSpec; Excel → CSV)
    // ═══════════════════════════════════════════════════════════════════════

    public sealed class EqImportSpec
    {
        public string Key = "", Name = "", Group = "", Description = "";
        public string[] Headers = Array.Empty<string>(), Example = Array.Empty<string>(), Required = Array.Empty<string>(), Notes = Array.Empty<string>();
    }

    public sealed class EqImportOutcome
    {
        public int Inserted, Updated, Skipped, ErrorRows;
        public readonly List<string> Messages = new();
        public string ToSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"✔ 导入完成:新增 {Inserted} · 更新/覆盖 {Updated} · 跳过 {Skipped} · 错误 {ErrorRows}");
            foreach (var m in Messages.Take(12)) sb.AppendLine("· " + m);
            if (Messages.Count > 12) sb.AppendLine($"… 另有 {Messages.Count - 12} 条提示");
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>全部数据类型规格(原 CreateSpecs: 设备台账 / 月度产能 / 月度可用率(KPI) / 生产班次记录 / 故障记录)。</summary>
    public static List<EqImportSpec> EqImportSpecs() => new()
    {
        new EqImportSpec
        {
            Key = "equipment", Name = "设备台账", Group = "设备域",
            Description = "设备基础信息:编号/类别/型号/产权/服役状态等。去重键=设备编号(已存在按策略跳过或覆盖)。",
            Headers = new[] { "设备编号", "类别", "型号", "制造商", "产地", "出厂编号", "资产编码", "状态", "购置日期", "投产年份", "累计台时", "上次大修日期", "所属矿", "备注" },
            Example = new[] { "3001", "电铲", "2800XPB", "P&H公司", "", "20070311003001", "10084280", "在用", "1999-01-01", "1999", "", "", "安家岭矿", "示例行,导入前删除" },
            Required = new[] { "设备编号", "类别" },
            Notes = new[]
            {
                "类别可填中文(电铲/卡车/钻机/装载机/推土机/平路机/洒水车)或英文枚举,无法识别归为「其他」。",
                "日期格式 2026-01-01;投产年份为四位整数;状态如 在用/封存/退役/大修。",
                "去重键=设备编号:已存在时按上方冲突策略「跳过」或「覆盖」。",
            },
        },
        new EqImportSpec
        {
            Key = "capacity", Name = "月度产能", Group = "设备域",
            Description = "各设备逐月产量(m³)。去重键=设备编号+年+月,重复按策略覆盖。",
            Headers = new[] { "设备编号", "年", "月", "产量_m3" }, Example = new[] { "3001", "2026", "5", "6051898" },
            Required = new[] { "设备编号", "年", "月", "产量_m3" },
            Notes = new[] { "年为四位、月为 1~12;产量单位 m³(立方米)。同一设备同年月只保留一条(覆盖)。" },
        },
        new EqImportSpec
        {
            Key = "kpi", Name = "月度可用率(KPI)", Group = "设备域",
            Description = "逐月三率考核:可用率/作业率/利用率 + 台时构成。去重键=设备编号+年+月。",
            Headers = new[] { "设备编号", "年", "月", "计划台时", "作业台时", "故障台时", "待机台时", "延误台时", "可用率", "作业率", "利用率", "内部故障率%", "外部故障率%" },
            Example = new[] { "3001", "2026", "5", "744", "590", "60", "64", "30", "0.86", "0.82", "0.865", "3.2", "1.1" },
            Required = new[] { "设备编号", "年", "月" },
            Notes = new[]
            {
                "可用率/作业率/利用率 支持三种写法:0.86 或 86 或 86%,统一按 0~1 小数存储。",
                "台时列为小时数;故障率列为百分数。去重键=设备编号+年+月,重复按策略覆盖。",
            },
        },
        new EqImportSpec
        {
            Key = "production", Name = "生产班次记录", Group = "设备域",
            Description = "设备×日×班的班产与故障明细。去重键=设备编号+日期+班次(避免重复导入)。",
            Headers = new[] { "设备编号", "日期", "班次", "班产_m3", "工作小时", "故障小时", "故障原因" },
            Example = new[] { "3001", "2026-05-01", "A", "25000", "7.5", "0.5", "液压系统" },
            Required = new[] { "设备编号", "日期", "班次" },
            Notes = new[] { "日期格式 2026-05-01;班次如 A/B/C(早/中/夜)。去重键=设备编号+日期+班次。" },
        },
        new EqImportSpec
        {
            Key = "fault", Name = "故障记录", Group = "设备域",
            Description = "设备故障台账:类型/时长/描述/检修班组。去重键=设备编号+日期+故障类型。",
            Headers = new[] { "设备编号", "日期", "班次", "故障类型", "故障时长_h", "描述", "是否已修复", "检修班组" },
            Example = new[] { "3001", "2026-05-03", "B", "机械故障", "4.5", "大臂销轴磨损", "是", "机修二班" },
            Required = new[] { "设备编号", "日期", "故障类型" },
            Notes = new[] { "日期格式 2026-05-03;是否已修复填 是/否。去重键=设备编号+日期+故障类型。" },
        },
    };

    // ── 解析工具(原 ImportSpec 保护方法) ──
    /// <summary>表头带的单位后缀(「产量_m3」「故障时长_h」「内部故障率%」)统一剥离。</summary>
    public static string EqStripUnit(string k)
    {
        int p = k.IndexOf('_');
        if (p > 0) k = k[..p];
        return k.TrimEnd('%', '％', ' ');
    }
    private static bool EqTryD(string s, out double v) => double.TryParse(s, NumberStyles.Any, Inv, out v) || double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out v);
    private static double? EqNum(string s) => string.IsNullOrWhiteSpace(s) ? null : (EqTryD(s, out var v) ? v : (double?)null);
    private static double EqNumOr(string s, double d = 0) => EqTryD(s, out var v) ? v : d;
    private static int EqIntOr(string s, int d = 0) { if (int.TryParse(s.Trim(), out var i)) return i; return EqTryD(s, out var v) ? (int)Math.Round(v) : d; }
    private static string? EqNz(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static DateTime? EqDate(string s) => DateTime.TryParse(s, Inv, DateTimeStyles.AssumeLocal, out var d) || DateTime.TryParse(s, out d) ? d : (DateTime?)null;
    private static bool EqBool(string s) { s = s.Trim(); return s == "是" || s == "1" || s == "Y" || s == "y" || s.Equals("true", StringComparison.OrdinalIgnoreCase); }
    /// <summary>可用率类:接受 0.85 / 85 / 85% 三种写法,统一为 0~1 小数。</summary>
    public static double EqRate(string s) { s = s.Trim().TrimEnd('%', '％'); if (!EqTryD(s, out var v)) return 0; return v > 1.5 ? v / 100.0 : v; }

    private static readonly (string cn, string en)[] EqCatMap =
    {
        ("电铲","Shovel"),("铲","Shovel"),("卡车","Truck"),("矿卡","Truck"),("自卸卡车","Truck"),
        ("钻机","Drill"),("装载机","Loader"),("前装机","Loader"),("推土机","Dozer"),
        ("平路机","Grader"),("平地机","Grader"),("洒水车","WaterTruck"),("水车","WaterTruck"),
    };

    /// <summary>导入类别解析(中文或英文枚举, 未知→Other)。</summary>
    public static string EqImportCategory(string s)
    {
        s = s.Trim();
        var m = EqCatMap.FirstOrDefault(x => x.cn == s);
        if (m.en != null) return m.en;
        return EqCategories.FirstOrDefault(e => e.Equals(s, StringComparison.OrdinalIgnoreCase)) ?? "Other";
    }
    /// <summary>导出类别中文(原 CategoryCn)。</summary>
    public static string EqExportCategoryCn(string en) => en switch
    {
        "Shovel" => "电铲", "Truck" => "卡车", "Drill" => "钻机", "Loader" => "装载机",
        "Dozer" => "推土机", "Grader" => "平路机", "WaterTruck" => "洒水车", _ => "其他",
    };
    private static string EqD(double v) => v.ToString("0.####", Inv);
    private static string EqCsvField(string s) => s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>CSV 文本(表头 + 行; 含 BOM)。模板 = 表头 + 示例行。</summary>
    public static string EqCsvOf(string[] headers, IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append('﻿');
        sb.Append(string.Join(",", headers.Select(EqCsvField))).Append('\n');
        foreach (var row in rows)
            sb.Append(string.Join(",", Enumerable.Range(0, headers.Length).Select(c => EqCsvField(c < row.Length ? row[c] : "")))).Append('\n');
        return sb.ToString();
    }

    public static string EqTemplateCsv(EqImportSpec spec) => EqCsvOf(spec.Headers, new[] { spec.Example });

    /// <summary>导出当前库内全部记录(与模板同列, 可再导入)。</summary>
    public static string EqExportCsv(SqliteConnection conn, EqImportSpec spec) => EqCsvOf(spec.Headers, EqExportRows(conn, spec.Key));

    public static List<string[]> EqExportRows(SqliteConnection conn, string key)
    {
        string DT(string? s) => EqParseDate(s)?.ToString("yyyy-MM-dd", Inv) ?? "";
        switch (key)
        {
            case "equipment":
                return conn.Query<EqLedgerRaw>(@"SELECT equipment_id AS EquipmentId, category AS Category, model AS Model, manufacturer AS Manufacturer, origin AS Origin,
                        serial_number AS SerialNumber, asset_code AS AssetCode, status AS Status, acquisition_date AS AcquisitionDate, commission_year AS CommissionYear,
                        cumulative_hours AS CumulativeHours, last_overhaul_date AS LastOverhaulDate, operating_area AS OperatingArea, notes AS Notes
                        FROM equipment ORDER BY equipment_id")
                    .Select(e => new[]
                    {
                        e.EquipmentId, EqExportCategoryCn(e.Category ?? ""), e.Model ?? "", e.Manufacturer ?? "", e.Origin ?? "", e.SerialNumber ?? "", e.AssetCode ?? "",
                        e.Status ?? "", DT(e.AcquisitionDate), e.CommissionYear?.ToString(Inv) ?? "", e.CumulativeHours.HasValue ? EqD(e.CumulativeHours.Value) : "",
                        DT(e.LastOverhaulDate), e.OperatingArea ?? "", e.Notes ?? "",
                    }).ToList();
            case "capacity":
                return conn.Query<(string id, int y, int m, double o)>("SELECT equipment_id, year, month, output_m3 FROM capacity_monthly ORDER BY equipment_id, year, month")
                    .Select(c => new[] { c.id, c.y.ToString(Inv), c.m.ToString(Inv), EqD(c.o) }).ToList();
            case "kpi":
                return conn.Query<(string id, int y, int m, double ph, double wh, double fh, double ih, double dh, double a, double r, double u, double ifr, double efr)>(
                        @"SELECT k.equipment_id, k.year, k.month, k.plan_hours, k.work_hours, k.fault_hours, k.idle_hours, k.delay_hours, k.availability, k.actual_run_rate,
                                 k.utilization_rate, k.internal_fault_rate_pct, k.external_fault_rate_pct
                          FROM equipment_kpi_monthly k JOIN equipment e ON e.equipment_id = k.equipment_id ORDER BY k.equipment_id, k.year, k.month")
                    .Select(k => new[] { k.id, k.y.ToString(Inv), k.m.ToString(Inv), EqD(k.ph), EqD(k.wh), EqD(k.fh), EqD(k.ih), EqD(k.dh), EqD(k.a), EqD(k.r), EqD(k.u), EqD(k.ifr), EqD(k.efr) }).ToList();
            case "production":
                return conn.Query<(string id, string? d, string s, double o, double w, double f, string? reason)>(
                        "SELECT equipment_id, date, shift, output_m3, work_hours, fault_hours, fault_reason FROM production_record ORDER BY equipment_id, date, shift")
                    .Select(p => new[] { p.id, DT(p.d), p.s, EqD(p.o), EqD(p.w), EqD(p.f), p.reason ?? "" }).ToList();
            case "fault":
                return conn.Query<(string id, string? d, string? s, string t, double h, string? desc, int res, string? team)>(
                        "SELECT equipment_id, date, shift, fault_type, duration_hours, description, is_resolved, repair_team FROM fault_event ORDER BY equipment_id, date, id")
                    .Select(f => new[] { f.id, DT(f.d), f.s ?? "", f.t, EqD(f.h), f.desc ?? "", f.res != 0 ? "是" : "否", f.team ?? "" }).ToList();
            default: return new List<string[]>();
        }
    }

    /// <summary>导入 CSV 文本(原 ImportSpec.Import): 表头去 BOM/星号/单位后缀, 缺必填列即拒; 逐行按去重键 + 策略(跳过/覆盖)写库。</summary>
    public static EqImportOutcome EqImportCsv(SqliteConnection conn, EqImportSpec spec, string csvText, bool overwrite)
    {
        var records = GeoDataQueries.ParseCsv(csvText);
        if (records.Count < 1) throw new InvalidOperationException("文件至少需要 1 行表头 + 1 行数据。");
        // 列名规范化: 去星号/括号/单位后缀
        var norm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in records[0].Keys)
        {
            var kk = EqStripUnit(k.Trim().Trim('﻿', '*', '＊', ' ', '(', '（'));
            if (kk.Length > 0 && !norm.ContainsKey(kk)) norm[kk] = k;
        }
        var missing = spec.Required.Where(c => !norm.ContainsKey(EqStripUnit(c))).ToList();
        if (missing.Count > 0) throw new InvalidOperationException("缺少必填列:" + string.Join("、", missing));

        var outcome = new EqImportOutcome();
        var existing = EqExistingKeys(conn, spec.Key);
        using var tx = conn.BeginTransaction();
        for (int r = 0; r < records.Count; r++)
        {
            var row = records[r];
            if (row.Values.All(string.IsNullOrWhiteSpace)) continue;
            string Cell(string name) => norm.TryGetValue(EqStripUnit(name), out var raw) && row.TryGetValue(raw, out var v) ? v.Trim() : "";
            try { EqImportRow(conn, tx, spec.Key, Cell, overwrite, outcome, existing); }
            catch (Exception ex)
            {
                outcome.ErrorRows++;
                if (outcome.Messages.Count < 20) outcome.Messages.Add($"第 {r + 2} 行:{ex.Message}");
            }
        }
        tx.Commit();
        return outcome;
    }

    private static HashSet<string> EqExistingKeys(SqliteConnection conn, string key)
    {
        string sql = key switch
        {
            "equipment" => "SELECT equipment_id FROM equipment",
            "capacity" => "SELECT equipment_id || '|' || year || '|' || month FROM capacity_monthly",
            "kpi" => "SELECT equipment_id || '|' || year || '|' || month FROM equipment_kpi_monthly",
            "production" => "SELECT equipment_id || '|' || date || '|' || shift FROM production_record",
            "fault" => "SELECT equipment_id || '|' || date || '|' || fault_type FROM fault_event",
            _ => throw new ArgumentException(key),
        };
        return conn.Query<string>(sql).ToHashSet(key == "equipment" ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    private static void EqImportRow(SqliteConnection conn, SqliteTransaction tx, string key, Func<string, string> cell, bool overwrite, EqImportOutcome o, HashSet<string> existing)
    {
        switch (key)
        {
            case "equipment":
            {
                var id = cell("设备编号");
                if (id.Length == 0) { o.Skipped++; return; }
                bool exists = existing.Contains(id);
                if (exists && !overwrite) { o.Skipped++; return; }
                conn.Execute(@"INSERT INTO equipment (equipment_id, category, model, manufacturer, origin, serial_number, asset_code, status, acquisition_date, commission_year,
                        cumulative_hours, last_overhaul_date, operating_area, notes)
                    VALUES (@id, @cat, @model, @manu, @origin, @sn, @asset, @status, @acq, @cy, @cum, @over, @area, @notes)
                    ON CONFLICT(equipment_id) DO UPDATE SET category=excluded.category, model=excluded.model, manufacturer=excluded.manufacturer, origin=excluded.origin,
                        serial_number=excluded.serial_number, asset_code=excluded.asset_code, status=excluded.status, acquisition_date=excluded.acquisition_date,
                        commission_year=excluded.commission_year, cumulative_hours=excluded.cumulative_hours, last_overhaul_date=excluded.last_overhaul_date,
                        operating_area=excluded.operating_area, notes=excluded.notes, updated_at=CURRENT_TIMESTAMP",
                    new
                    {
                        id, cat = EqImportCategory(cell("类别")), model = EqNz(cell("型号")), manu = EqNz(cell("制造商")), origin = EqNz(cell("产地")),
                        sn = EqNz(cell("出厂编号")), asset = EqNz(cell("资产编码")), status = EqNz(cell("状态")) ?? "在用",
                        acq = EqDate(cell("购置日期"))?.ToString("yyyy-MM-dd", Inv), cy = string.IsNullOrWhiteSpace(cell("投产年份")) ? (int?)null : EqIntOr(cell("投产年份")),
                        cum = EqNum(cell("累计台时")), over = EqDate(cell("上次大修日期"))?.ToString("yyyy-MM-dd", Inv), area = EqNz(cell("所属矿")), notes = EqNz(cell("备注")),
                    }, tx);
                existing.Add(id);
                if (exists) o.Updated++; else o.Inserted++;
                return;
            }
            case "capacity":
            {
                var id = cell("设备编号"); int y = EqIntOr(cell("年")), m = EqIntOr(cell("月"));
                if (id.Length == 0 || y < 1900 || m < 1 || m > 12) { o.Skipped++; return; }
                var k = $"{id}|{y}|{m}"; bool exists = existing.Contains(k);
                if (exists && !overwrite) { o.Skipped++; return; }
                conn.Execute(@"INSERT INTO capacity_monthly (equipment_id, year, month, output_m3) VALUES (@id, @y, @m, @o)
                               ON CONFLICT(equipment_id, year, month) DO UPDATE SET output_m3 = excluded.output_m3",
                    new { id, y, m, o = EqNumOr(cell("产量_m3")) }, tx);
                existing.Add(k);
                if (exists) o.Updated++; else o.Inserted++;
                return;
            }
            case "kpi":
            {
                var id = cell("设备编号"); int y = EqIntOr(cell("年")), m = EqIntOr(cell("月"));
                if (id.Length == 0 || y < 1900 || m < 1 || m > 12) { o.Skipped++; return; }
                var k = $"{id}|{y}|{m}"; bool exists = existing.Contains(k);
                if (exists && !overwrite) { o.Skipped++; return; }
                conn.Execute(@"INSERT INTO equipment_kpi_monthly (equipment_id, year, month, plan_hours, work_hours, fault_hours, idle_hours, delay_hours, availability,
                        actual_run_rate, utilization_rate, internal_fault_rate_pct, external_fault_rate_pct)
                    VALUES (@id, @y, @m, @ph, @wh, @fh, @ih, @dh, @a, @r, @u, @ifr, @efr)
                    ON CONFLICT(equipment_id, year, month) DO UPDATE SET plan_hours=excluded.plan_hours, work_hours=excluded.work_hours, fault_hours=excluded.fault_hours,
                        idle_hours=excluded.idle_hours, delay_hours=excluded.delay_hours, availability=excluded.availability, actual_run_rate=excluded.actual_run_rate,
                        utilization_rate=excluded.utilization_rate, internal_fault_rate_pct=excluded.internal_fault_rate_pct, external_fault_rate_pct=excluded.external_fault_rate_pct",
                    new
                    {
                        id, y, m, ph = EqNumOr(cell("计划台时")), wh = EqNumOr(cell("作业台时")), fh = EqNumOr(cell("故障台时")), ih = EqNumOr(cell("待机台时")), dh = EqNumOr(cell("延误台时")),
                        a = EqRate(cell("可用率")), r = EqRate(cell("作业率")), u = EqRate(cell("利用率")), ifr = EqNumOr(cell("内部故障率")), efr = EqNumOr(cell("外部故障率")),
                    }, tx);
                existing.Add(k);
                if (exists) o.Updated++; else o.Inserted++;
                return;
            }
            case "production":
            {
                var id = cell("设备编号"); var d = EqDate(cell("日期")); var shift = cell("班次");
                if (id.Length == 0 || d == null || shift.Length == 0) { o.Skipped++; return; }
                string ds = d.Value.ToString("yyyy-MM-dd", Inv);
                var k = $"{id}|{ds}|{shift}";
                if (existing.Contains(k))
                {
                    if (!overwrite) { o.Skipped++; return; }
                    conn.Execute("DELETE FROM production_record WHERE equipment_id = @id AND date = @ds AND shift = @shift", new { id, ds, shift }, tx);
                    o.Updated++;
                }
                else o.Inserted++;
                conn.Execute(@"INSERT INTO production_record (equipment_id, date, shift, output_m3, work_hours, fault_hours, fault_reason) VALUES (@id, @ds, @shift, @o, @w, @f, @reason)",
                    new { id, ds, shift, o = EqNumOr(cell("班产_m3")), w = EqNumOr(cell("工作小时")), f = EqNumOr(cell("故障小时")), reason = EqNz(cell("故障原因")) }, tx);
                existing.Add(k);
                return;
            }
            case "fault":
            {
                var id = cell("设备编号"); var d = EqDate(cell("日期")); var type = cell("故障类型");
                if (id.Length == 0 || d == null || type.Length == 0) { o.Skipped++; return; }
                string ds = d.Value.ToString("yyyy-MM-dd", Inv);
                var k = $"{id}|{ds}|{type}"; bool exists = existing.Contains(k);
                if (exists && !overwrite) { o.Skipped++; return; }
                conn.Execute(@"INSERT INTO fault_event (equipment_id, date, shift, fault_type, duration_hours, description, is_resolved, repair_team)
                               VALUES (@id, @ds, @shift, @type, @h, @desc, @res, @team)",
                    new { id, ds, shift = EqNz(cell("班次")), type, h = EqNumOr(cell("故障时长_h")), desc = EqNz(cell("描述")), res = EqBool(cell("是否已修复")) ? 1 : 0, team = EqNz(cell("检修班组")) }, tx);
                existing.Add(k);
                if (exists) o.Updated++; else o.Inserted++;
                return;
            }
            default: throw new ArgumentException(key);
        }
    }
}
