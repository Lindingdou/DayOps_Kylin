using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Dapper;
using System.Data.Common;

namespace PitMine3D.Kylin.Data;

/// <summary>
/// 「工艺参数管理」组(工艺架构定义 / 平盘工艺地图 / 参数模板库 / 现场验收录入)的查询、写库与纯逻辑。
/// 忠实原 GeoDataBase 插件 ProcessArchitectureService / ProcessTemplateService /
/// PhaseLocationBindingService / ParameterAcceptanceService 及四个窗体 .xaml.cs 里的计算。
/// 表: process_system / process_phase / parameter_definition / equipment_constraint(V006)
///     process_template / template_param_value(V008)  phase_location_binding(V009)  parameter_acceptance(V011)。
/// </summary>
public static partial class GeoDbViews
{
    private const string DateFmt = "yyyy-MM-dd";
    private static string D(DateTime d) => d.ToString(DateFmt, CultureInfo.InvariantCulture);

    // ═══════════════════════════════════════════════════════════════════════
    // 行类型
    // ═══════════════════════════════════════════════════════════════════════

    public sealed class ProcSystem
    {
        public long SystemId { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Category { get; set; }
        public string? Description { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public sealed class ProcPhase
    {
        public long PhaseId { get; set; }
        public long SystemId { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public int SequenceOrder { get; set; }
        public string? TypicalEquipmentCategory { get; set; }
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
    }

    /// <summary>参数定义行(工艺架构定义表格可编辑, 数值列以文本暴露便于清空)。</summary>
    public sealed class ProcParamDef : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public long ParamId { get; set; }
        public long PhaseId { get; set; }
        private string _code = ""; public string Code { get => _code; set { _code = value; On(); } }
        private string _name = ""; public string Name { get => _name; set { _name = value; On(); } }
        private string? _unit; public string? Unit { get => _unit; set { _unit = value; On(); } }
        private string _valueType = "numeric"; public string ValueType { get => _valueType; set { _valueType = value; On(); } }
        public double? StandardMin { get; set; }
        public double? StandardMax { get; set; }
        public double? StandardDefault { get; set; }
        public double? AlarmLow { get; set; }
        public double? AlarmHigh { get; set; }
        private bool _isRequired; public bool IsRequired { get => _isRequired; set { _isRequired = value; On(); } }
        private string? _calcFormula; public string? CalcFormula { get => _calcFormula; set { _calcFormula = value; On(); } }
        public string? SourceTable { get; set; }
        public string? SourceColumn { get; set; }
        private string? _description; public string? Description { get => _description; set { _description = value; On(); } }
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; } = true;

        // 表格文本代理(空串=NULL)
        public string StandardMinText { get => Fmt(StandardMin); set { StandardMin = ParseD(value); On(); } }
        public string StandardMaxText { get => Fmt(StandardMax); set { StandardMax = ParseD(value); On(); } }
        public string StandardDefaultText { get => Fmt(StandardDefault); set { StandardDefault = ParseD(value); On(); } }

        private static string Fmt(double? v) => v.HasValue ? v.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";
        private static double? ParseD(string? s)
            => double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public sealed class ProcConstraint
    {
        public long Id { get; set; }
        public long ParamId { get; set; }
        public string EquipmentModel { get; set; } = "";
        public string ConstraintType { get; set; } = "max";
        public double? LimitValue { get; set; }
        public double? LimitMin { get; set; }
        public double? LimitMax { get; set; }
        public string? TextValue { get; set; }
        public string Consequence { get; set; } = "hard";
        public int Priority { get; set; } = 3;
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public sealed class ProcTemplate
    {
        public long TemplateId { get; set; }
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string? ApplicableMaterial { get; set; }
        public string? ApplicableHardness { get; set; }
        public string Version { get; set; } = "v1.0";
        public bool IsCurrent { get; set; } = true;
        public string Status { get; set; } = "active";
        public string? CreatedBy { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class ProcTemplateValue
    {
        public long Id { get; set; }
        public long TemplateId { get; set; }
        public long ParamId { get; set; }
        public double? RecommendedValue { get; set; }
        public double? MinValue { get; set; }
        public double? MaxValue { get; set; }
        public string? TextValue { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class ProcBinding
    {
        public long Id { get; set; }
        public long PhaseId { get; set; }
        public string LocationCode { get; set; } = "";
        public long? BoundTemplateId { get; set; }
        public string StartedAt { get; set; } = "";
        public string? EndedAt { get; set; }
        public bool IsActive { get; set; } = true;
        public string? Notes { get; set; }
    }

    public sealed class ProcLocation
    {
        public string LocationCode { get; set; } = "";
        public string? Name { get; set; }
        public double? ElevationM { get; set; }
        public string? Team { get; set; }
    }

    public sealed class ProcAcceptance
    {
        public long Id { get; set; }
        public long ParamId { get; set; }
        public string LocationCode { get; set; } = "";
        public long PhaseId { get; set; }
        /// <summary>yyyy-MM-dd</summary>
        public string MeasureDate { get; set; } = "";
        public double? MeasuredValue { get; set; }
        public string? MeasuredText { get; set; }
        public double? TemplateValue { get; set; }
        public double? DeviationPct { get; set; }
        public string Status { get; set; } = "pending";
        public string? EquipmentId { get; set; }
        public string? AcceptedBy { get; set; }
        public string? AcceptanceDate { get; set; }
        public string? Conclusion { get; set; }
        public string? ScopeCode { get; set; }
        public string? Notes { get; set; }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 工艺架构: 系统 / 环节 / 参数 / 约束 (原 ProcessArchitectureService)
    // ═══════════════════════════════════════════════════════════════════════

    private const string SystemCols = "system_id AS SystemId, code AS Code, name AS Name, category AS Category, description AS Description, display_order AS DisplayOrder, is_active AS IsActive FROM process_system";
    private const string PhaseCols = "phase_id AS PhaseId, system_id AS SystemId, code AS Code, name AS Name, sequence_order AS SequenceOrder, typical_equipment_category AS TypicalEquipmentCategory, description AS Description, is_active AS IsActive FROM process_phase";
    private const string ParamCols = "param_id AS ParamId, phase_id AS PhaseId, code AS Code, name AS Name, unit AS Unit, value_type AS ValueType, standard_min AS StandardMin, standard_max AS StandardMax, standard_default AS StandardDefault, alarm_low AS AlarmLow, alarm_high AS AlarmHigh, is_required AS IsRequired, calc_formula AS CalcFormula, source_table AS SourceTable, source_column AS SourceColumn, description AS Description, display_order AS DisplayOrder, is_active AS IsActive FROM parameter_definition";
    private const string ConstraintCols = "id AS Id, param_id AS ParamId, equipment_model AS EquipmentModel, constraint_type AS ConstraintType, limit_value AS LimitValue, limit_min AS LimitMin, limit_max AS LimitMax, text_value AS TextValue, consequence AS Consequence, priority AS Priority, description AS Description, is_active AS IsActive FROM equipment_constraint";

    /// <summary>全部工艺系统(activeOnly=true 仅 is_active), ORDER BY display_order, system_id。</summary>
    public static List<ProcSystem> ProcessSystems(DbConnection conn, bool activeOnly = true)
        => conn.Query<ProcSystem>("SELECT " + SystemCols + (activeOnly ? " WHERE is_active = 1" : "") + " ORDER BY display_order, system_id").ToList();

    public static ProcSystem? ProcessGetSystem(DbConnection conn, long systemId)
        => conn.QueryFirstOrDefault<ProcSystem>("SELECT " + SystemCols + " WHERE system_id = @id", new { id = systemId });

    public static long ProcessInsertSystem(DbConnection conn, string code, string name, int displayOrder = 99)
        => conn.ExecuteScalar<long>("INSERT INTO process_system (code, name, display_order, is_active) VALUES (@c, @n, @o, 1); SELECT last_insert_rowid();",
            new { c = code, n = name, o = displayOrder });

    /// <summary>删除系统(FK ON DELETE CASCADE: 环节→参数→约束/模板值/绑定/验收 级联)。</summary>
    public static int ProcessDeleteSystem(DbConnection conn, long systemId)
        => conn.Execute("DELETE FROM process_system WHERE system_id = @id", new { id = systemId });

    /// <summary>某系统下环节, ORDER BY sequence_order, phase_id。</summary>
    public static List<ProcPhase> ProcessPhasesBySystem(DbConnection conn, long systemId)
        => conn.Query<ProcPhase>("SELECT " + PhaseCols + " WHERE system_id = @s ORDER BY sequence_order, phase_id", new { s = systemId }).ToList();

    /// <summary>全部环节(activeOnly=true 仅 is_active), ORDER BY system_id, sequence_order。</summary>
    public static List<ProcPhase> ProcessAllPhases(DbConnection conn, bool activeOnly = true)
        => conn.Query<ProcPhase>("SELECT " + PhaseCols + (activeOnly ? " WHERE is_active = 1" : "") + " ORDER BY system_id, sequence_order").ToList();

    public static ProcPhase? ProcessGetPhase(DbConnection conn, long phaseId)
        => conn.QueryFirstOrDefault<ProcPhase>("SELECT " + PhaseCols + " WHERE phase_id = @id", new { id = phaseId });

    public static long ProcessInsertPhase(DbConnection conn, long systemId, string code, string name, int sequenceOrder)
        => conn.ExecuteScalar<long>("INSERT INTO process_phase (system_id, code, name, sequence_order, is_active) VALUES (@s, @c, @n, @o, 1); SELECT last_insert_rowid();",
            new { s = systemId, c = code, n = name, o = sequenceOrder });

    public static int ProcessDeletePhase(DbConnection conn, long phaseId)
        => conn.Execute("DELETE FROM process_phase WHERE phase_id = @id", new { id = phaseId });

    /// <summary>某环节下参数定义, ORDER BY display_order, param_id。</summary>
    public static List<ProcParamDef> ProcessParamsByPhase(DbConnection conn, long phaseId)
        => conn.Query<ProcParamDef>("SELECT " + ParamCols + " WHERE phase_id = @p ORDER BY display_order, param_id", new { p = phaseId }).ToList();

    public static int ProcessParamCountByPhase(DbConnection conn, long phaseId)
        => conn.ExecuteScalar<int>("SELECT COUNT(*) FROM parameter_definition WHERE phase_id = @p", new { p = phaseId });

    public static ProcParamDef? ProcessGetParam(DbConnection conn, long paramId)
        => conn.QueryFirstOrDefault<ProcParamDef>("SELECT " + ParamCols + " WHERE param_id = @id", new { id = paramId });

    public static long ProcessInsertParam(DbConnection conn, ProcParamDef p)
        => conn.ExecuteScalar<long>(@"INSERT INTO parameter_definition
            (phase_id, code, name, unit, value_type, standard_min, standard_max, standard_default, alarm_low, alarm_high,
             is_required, calc_formula, source_table, source_column, description, display_order, is_active)
            VALUES (@PhaseId, @Code, @Name, @Unit, @ValueType, @StandardMin, @StandardMax, @StandardDefault, @AlarmLow, @AlarmHigh,
             @IsRequired, @CalcFormula, @SourceTable, @SourceColumn, @Description, @DisplayOrder, @IsActive);
            SELECT last_insert_rowid();", p);

    public static int ProcessUpdateParam(DbConnection conn, ProcParamDef p)
        => conn.Execute(@"UPDATE parameter_definition SET
            phase_id=@PhaseId, code=@Code, name=@Name, unit=@Unit, value_type=@ValueType,
            standard_min=@StandardMin, standard_max=@StandardMax, standard_default=@StandardDefault,
            alarm_low=@AlarmLow, alarm_high=@AlarmHigh, is_required=@IsRequired, calc_formula=@CalcFormula,
            source_table=@SourceTable, source_column=@SourceColumn, description=@Description,
            display_order=@DisplayOrder, is_active=@IsActive
            WHERE param_id=@ParamId", p);

    public static int ProcessDeleteParam(DbConnection conn, long paramId)
        => conn.Execute("DELETE FROM parameter_definition WHERE param_id = @id", new { id = paramId });

    /// <summary>某参数的在用设备约束(is_active=1)。</summary>
    public static List<ProcConstraint> ProcessConstraintsByParam(DbConnection conn, long paramId)
        => conn.Query<ProcConstraint>("SELECT " + ConstraintCols + " WHERE param_id = @p AND is_active = 1", new { p = paramId }).ToList();

    // ═══════════════════════════════════════════════════════════════════════
    // 模板库 (原 ProcessTemplateService)
    // ═══════════════════════════════════════════════════════════════════════

    private const string TemplateCols = "template_id AS TemplateId, code AS Code, name AS Name, description AS Description, applicable_material AS ApplicableMaterial, applicable_hardness AS ApplicableHardness, version AS Version, is_current AS IsCurrent, status AS Status, created_by AS CreatedBy, notes AS Notes FROM process_template";
    private const string TplValueCols = "id AS Id, template_id AS TemplateId, param_id AS ParamId, recommended_value AS RecommendedValue, min_value AS MinValue, max_value AS MaxValue, text_value AS TextValue, notes AS Notes FROM template_param_value";

    /// <summary>模板列表(activeOnly=true 仅 status='active'), ORDER BY applicable_material, name。</summary>
    public static List<ProcTemplate> ProcTemplates(DbConnection conn, bool activeOnly = true)
        => conn.Query<ProcTemplate>("SELECT " + TemplateCols + (activeOnly ? " WHERE status = 'active'" : "") + " ORDER BY applicable_material, name").ToList();

    public static ProcTemplate? ProcGetTemplate(DbConnection conn, long templateId)
        => conn.QueryFirstOrDefault<ProcTemplate>("SELECT " + TemplateCols + " WHERE template_id = @id", new { id = templateId });

    public static long ProcInsertTemplate(DbConnection conn, ProcTemplate t)
        => conn.ExecuteScalar<long>(@"INSERT INTO process_template
            (code, name, description, applicable_material, applicable_hardness, version, is_current, status, created_by, notes)
            VALUES (@Code, @Name, @Description, @ApplicableMaterial, @ApplicableHardness, @Version, @IsCurrent, @Status, @CreatedBy, @Notes);
            SELECT last_insert_rowid();", t);

    /// <summary>归档: status='archived', is_current=0。</summary>
    public static int ProcArchiveTemplate(DbConnection conn, long templateId)
        => conn.Execute("UPDATE process_template SET status = 'archived', is_current = 0 WHERE template_id = @id", new { id = templateId });

    /// <summary>彻底删除模板(参数值级联)。</summary>
    public static int ProcDeleteTemplate(DbConnection conn, long templateId)
    {
        conn.Execute("DELETE FROM template_param_value WHERE template_id = @id", new { id = templateId });
        return conn.Execute("DELETE FROM process_template WHERE template_id = @id", new { id = templateId });
    }

    public static List<ProcTemplateValue> ProcValuesByTemplate(DbConnection conn, long templateId)
        => conn.Query<ProcTemplateValue>("SELECT " + TplValueCols + " WHERE template_id = @t", new { t = templateId }).ToList();

    public static ProcTemplateValue? ProcGetTemplateValue(DbConnection conn, long templateId, long paramId)
        => conn.QueryFirstOrDefault<ProcTemplateValue>("SELECT " + TplValueCols + " WHERE template_id = @t AND param_id = @p", new { t = templateId, p = paramId });

    /// <summary>按 (template_id, param_id) 有则更新、无则插入。</summary>
    public static void ProcUpsertTemplateValue(DbConnection conn, ProcTemplateValue v)
    {
        var existing = ProcGetTemplateValue(conn, v.TemplateId, v.ParamId);
        if (existing != null)
        {
            v.Id = existing.Id;
            conn.Execute(@"UPDATE template_param_value SET recommended_value=@RecommendedValue, min_value=@MinValue, max_value=@MaxValue,
                text_value=@TextValue, notes=@Notes WHERE id=@Id", v);
        }
        else
        {
            v.Id = conn.ExecuteScalar<long>(@"INSERT INTO template_param_value (template_id, param_id, recommended_value, min_value, max_value, text_value, notes)
                VALUES (@TemplateId, @ParamId, @RecommendedValue, @MinValue, @MaxValue, @TextValue, @Notes); SELECT last_insert_rowid();", v);
        }
    }

    /// <summary>
    /// 复制模板参数值到另一模板(原 OnCloneTemplate 逐行 UpsertValue)。
    /// 只复制参数定义仍存在的值(种子迁移期外键关闭, 历史模板可能残留已删参数的孤值, 逐行插入会违反外键)。返回复制条数。
    /// </summary>
    public static int ProcCloneTemplateValues(DbConnection conn, long srcTemplateId, long dstTemplateId)
        => conn.Execute(@"INSERT OR REPLACE INTO template_param_value (template_id, param_id, recommended_value, min_value, max_value, text_value, notes)
            SELECT @dst, v.param_id, v.recommended_value, v.min_value, v.max_value, v.text_value, v.notes
            FROM template_param_value v JOIN parameter_definition d ON d.param_id = v.param_id
            WHERE v.template_id = @src", new { src = srcTemplateId, dst = dstTemplateId });

    /// <summary>版本号 +1: v1.2 → v1.3; 空 → v1.0; 无点 → 追加 .1(原 BumpVersion)。</summary>
    public static string ProcBumpVersion(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "v1.0";
        var idx = v.LastIndexOf('.');
        if (idx > 0 && int.TryParse(v.AsSpan(idx + 1), out var n))
            return v[..(idx + 1)] + (n + 1);
        return v + ".1";
    }

    /// <summary>解析 "下限~上限"(也接受 - / ～)为 (min,max); 非两段返回 (null,null)(原 OnSaveAll)。</summary>
    public static (double? min, double? max) ProcParseRange(string? rangeText)
    {
        if (string.IsNullOrWhiteSpace(rangeText)) return (null, null);
        var parts = rangeText.Split('~', '-', '～');
        if (parts.Length != 2) return (null, null);
        double? mn = double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : null;
        double? mx = double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : null;
        return (mn, mx);
    }

    /// <summary>
    /// 把一行编辑文本转成待 upsert 的模板参数值(原 OnSaveAll 单行逻辑):
    /// 推荐值可解析为数 → RecommendedValue; 非空文本 → TextValue; 空 → null(跳过)。
    /// </summary>
    public static ProcTemplateValue? ProcBuildTemplateValue(long templateId, long paramId, string? recommendedText, string? rangeText, string? notes)
    {
        var entity = new ProcTemplateValue
        {
            TemplateId = templateId, ParamId = paramId,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes
        };
        if (double.TryParse(recommendedText, NumberStyles.Float, CultureInfo.InvariantCulture, out var rec)) entity.RecommendedValue = rec;
        else if (!string.IsNullOrWhiteSpace(recommendedText)) entity.TextValue = recommendedText;
        else return null;
        var (mn, mx) = ProcParseRange(rangeText);
        entity.MinValue = mn; entity.MaxValue = mx;
        return entity;
    }

    /// <summary>引用此模板的现行绑定: (平盘编码, 环节名)。</summary>
    public static List<(string LocationCode, string PhaseName)> ProcTemplateUsage(DbConnection conn, long templateId)
        => conn.Query<(string, string)>(@"SELECT b.location_code, COALESCE(p.name, '未知') FROM phase_location_binding b
            LEFT JOIN process_phase p ON p.phase_id = b.phase_id
            WHERE b.is_active = 1 AND b.bound_template_id = @t ORDER BY b.location_code, b.phase_id", new { t = templateId }).ToList();

    // ═══════════════════════════════════════════════════════════════════════
    // 平盘 / 绑定 (原 MineLocationService.All + PhaseLocationBindingService)
    // ═══════════════════════════════════════════════════════════════════════

    private const string BindingCols = "id AS Id, phase_id AS PhaseId, location_code AS LocationCode, bound_template_id AS BoundTemplateId, started_at AS StartedAt, ended_at AS EndedAt, is_active AS IsActive, notes AS Notes FROM phase_location_binding";
    // 日期比较写成 CAST(CURRENT_DATE AS TEXT): SQLite 的 date('now') 在 PG 里不存在(date 是类型名,
    // 报 syntax error at or near "("); 而直接用 CURRENT_DATE 又会变成 date 类型, 与 TEXT 列比较类型不匹配。
    private const string ActiveBindingWhere = "is_active = 1 AND (ended_at IS NULL OR ended_at >= CAST(CURRENT_DATE AS TEXT))";

    /// <summary>平盘列表(activeOnly=true 仅 is_active, ORDER BY elevation_m)。</summary>
    public static List<ProcLocation> ProcLocations(DbConnection conn, bool activeOnly = true)
        => conn.Query<ProcLocation>("SELECT location_code AS LocationCode, name AS Name, elevation_m AS ElevationM, team AS Team FROM mine_location"
            + (activeOnly ? " WHERE is_active = 1 ORDER BY elevation_m" : "")).ToList();

    public static ProcLocation? ProcGetLocation(DbConnection conn, string locationCode)
        => conn.QueryFirstOrDefault<ProcLocation>("SELECT location_code AS LocationCode, name AS Name, elevation_m AS ElevationM, team AS Team FROM mine_location WHERE location_code = @c", new { c = locationCode });

    /// <summary>某平盘现行绑定(is_active 且未到期), ORDER BY phase_id。</summary>
    public static List<ProcBinding> ProcActiveBindingsByLocation(DbConnection conn, string locationCode)
        => conn.Query<ProcBinding>("SELECT " + BindingCols + " WHERE location_code = @c AND " + ActiveBindingWhere + " ORDER BY phase_id", new { c = locationCode }).ToList();

    public static ProcBinding? ProcActiveBinding(DbConnection conn, string locationCode, long phaseId)
        => conn.QueryFirstOrDefault<ProcBinding>("SELECT " + BindingCols + " WHERE location_code = @c AND phase_id = @p AND " + ActiveBindingWhere, new { c = locationCode, p = phaseId });

    public static List<ProcBinding> ProcAllBindings(DbConnection conn, bool activeOnly = true)
        => conn.Query<ProcBinding>("SELECT " + BindingCols + (activeOnly ? " WHERE is_active = 1" : "")).ToList();

    public static long ProcInsertBinding(DbConnection conn, long phaseId, string locationCode, long? templateId, DateTime? startedAt = null)
        => conn.ExecuteScalar<long>(@"INSERT INTO phase_location_binding (phase_id, location_code, bound_template_id, started_at, is_active)
            VALUES (@p, @c, @t, @s, 1); SELECT last_insert_rowid();",
            new { p = phaseId, c = locationCode, t = templateId, s = D(startedAt ?? DateTime.Today) });

    /// <summary>
    /// 切换模板: 事务内关闭旧绑定(is_active=0, ended_at=今日)并插入新绑定(原 RebindTemplate)。
    /// 同日二次切换会撞 UNIQUE(phase_id, location_code, started_at): 用 INSERT OR REPLACE 以当日最新一次为准。
    /// </summary>
    public static void ProcRebindTemplate(DbConnection conn, string locationCode, long phaseId, long? newTemplateId, DateTime? today = null)
    {
        var d = D(today ?? DateTime.Today);
        using var tx = conn.BeginTransaction();
        conn.Execute("UPDATE phase_location_binding SET is_active = 0, ended_at = @d WHERE location_code = @c AND phase_id = @p AND is_active = 1",
            new { d, c = locationCode, p = phaseId }, tx);
        conn.Execute("INSERT OR REPLACE INTO phase_location_binding (phase_id, location_code, bound_template_id, started_at, is_active) VALUES (@p, @c, @t, @d, 1)",
            new { p = phaseId, c = locationCode, t = newTemplateId, d }, tx);
        tx.Commit();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 现场验收 (原 ParameterAcceptanceService)
    // ═══════════════════════════════════════════════════════════════════════

    private const string AcceptCols = "id AS Id, param_id AS ParamId, location_code AS LocationCode, phase_id AS PhaseId, measure_date AS MeasureDate, measured_value AS MeasuredValue, measured_text AS MeasuredText, template_value AS TemplateValue, deviation_pct AS DeviationPct, status AS Status, equipment_id AS EquipmentId, accepted_by AS AcceptedBy, acceptance_date AS AcceptanceDate, conclusion AS Conclusion, scope_code AS ScopeCode, notes AS Notes FROM parameter_acceptance";

    /// <summary>某平盘+环节全部验收记录, ORDER BY measure_date DESC, id DESC。</summary>
    public static List<ProcAcceptance> ProcAcceptanceByLocationPhase(DbConnection conn, string locationCode, long phaseId)
        => conn.Query<ProcAcceptance>("SELECT " + AcceptCols + " WHERE location_code = @c AND phase_id = @p ORDER BY measure_date DESC, id DESC", new { c = locationCode, p = phaseId }).ToList();

    /// <summary>某参数最新一条实测。</summary>
    public static ProcAcceptance? ProcLatestAcceptance(DbConnection conn, string locationCode, long phaseId, long paramId)
        => conn.QueryFirstOrDefault<ProcAcceptance>("SELECT " + AcceptCols + " WHERE location_code = @c AND phase_id = @p AND param_id = @pid ORDER BY measure_date DESC, id DESC LIMIT 1",
            new { c = locationCode, p = phaseId, pid = paramId });

    /// <summary>近 N 天某状态记录, ORDER BY measure_date DESC。</summary>
    public static List<ProcAcceptance> ProcRecentAcceptanceByStatus(DbConnection conn, string status, int days = 30)
        => conn.Query<ProcAcceptance>("SELECT " + AcceptCols + " WHERE status = @s AND measure_date >= date('now', @d) ORDER BY measure_date DESC",
            new { s = status, d = $"-{days} days" }).ToList();

    public static long ProcInsertAcceptance(DbConnection conn, ProcAcceptance a)
        => conn.ExecuteScalar<long>(@"INSERT INTO parameter_acceptance
            (param_id, location_code, phase_id, measure_date, measured_value, measured_text, template_value, deviation_pct, status,
             equipment_id, accepted_by, acceptance_date, conclusion, scope_code, notes)
            VALUES (@ParamId, @LocationCode, @PhaseId, @MeasureDate, @MeasuredValue, @MeasuredText, @TemplateValue, @DeviationPct, @Status,
             @EquipmentId, @AcceptedBy, @AcceptanceDate, @Conclusion, @ScopeCode, @Notes); SELECT last_insert_rowid();", a);

    public static int ProcUpdateAcceptance(DbConnection conn, ProcAcceptance a)
        => conn.Execute(@"UPDATE parameter_acceptance SET param_id=@ParamId, location_code=@LocationCode, phase_id=@PhaseId, measure_date=@MeasureDate,
            measured_value=@MeasuredValue, measured_text=@MeasuredText, template_value=@TemplateValue, deviation_pct=@DeviationPct, status=@Status,
            equipment_id=@EquipmentId, accepted_by=@AcceptedBy, acceptance_date=@AcceptanceDate, conclusion=@Conclusion, scope_code=@ScopeCode, notes=@Notes
            WHERE id=@Id", a);

    /// <summary>状态图标: pass ✓ / warning ⚠ / fail ✗ / 其它 ❍。</summary>
    public static string ProcStatusIcon(string? status) => status switch
    {
        "pass" => "✓", "warning" => "⚠", "fail" => "✗", _ => "❍"
    };

    /// <summary>偏差文本 "+12.3%" / "-4.0%" / "0%"; null → "—"。</summary>
    public static string ProcDeviationText(double? dev)
        => dev.HasValue ? dev.Value.ToString("+0.0;-0.0;0", CultureInfo.InvariantCulture) + "%" : "—";

    private static string F(double? v, string fmt) => v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "—";

    /// <summary>"下限 ~ 上限"(F1, 空为 —)。</summary>
    public static string ProcStdRange(double? min, double? max) => $"{F(min, "F1")} ~ {F(max, "F1")}";

    /// <summary>模板值文本: 模板推荐值 F2; 无则参数默认值 "x (默)"; 都无 "—"。</summary>
    public static string ProcTemplateValText(double? tplVal, double? standardDefault)
        => tplVal.HasValue ? tplVal.Value.ToString("F2", CultureInfo.InvariantCulture)
         : standardDefault.HasValue ? standardDefault.Value.ToString("F2", CultureInfo.InvariantCulture) + " (默)" : "—";

    // ═══════════════════════════════════════════════════════════════════════
    // 平盘工艺地图: 参数对照 / 适配设备 / 方案(原 LocationProcessMapWindow)
    // ═══════════════════════════════════════════════════════════════════════

    public sealed class ProcParamCompareRow
    {
        public long ParamId { get; set; }
        public string ParamName { get; set; } = "";
        public string Unit { get; set; } = "";
        public string StdRange { get; set; } = "";
        public string TemplateVal { get; set; } = "";
        public string MeasuredVal { get; set; } = "";
        public string DeviationText { get; set; } = "";
        public string StatusIcon { get; set; } = "";
        public string MeasureDate { get; set; } = "";
    }

    /// <summary>
    /// 现状视图参数对照: 每个参数 → 标准范围 / 模板值(无则默认值) / 最新实测 / 偏差 / 状态 / 测量日期(MM-dd, 未验收)。
    /// </summary>
    public static List<ProcParamCompareRow> ProcParamCompareRows(DbConnection conn, string locationCode, long phaseId, long? boundTemplateId)
    {
        var paramDefs = ProcessParamsByPhase(conn, phaseId);
        var templateValues = boundTemplateId.HasValue
            ? ProcValuesByTemplate(conn, boundTemplateId.Value).ToDictionary(v => v.ParamId)
            : new Dictionary<long, ProcTemplateValue>();
        var rows = new List<ProcParamCompareRow>();
        foreach (var p in paramDefs)
        {
            double? tplVal = templateValues.TryGetValue(p.ParamId, out var tv) ? tv.RecommendedValue : null;
            var row = new ProcParamCompareRow
            {
                ParamId = p.ParamId, ParamName = p.Name, Unit = p.Unit ?? "",
                StdRange = ProcStdRange(p.StandardMin, p.StandardMax),
                TemplateVal = ProcTemplateValText(tplVal, p.StandardDefault),
                MeasuredVal = "—", DeviationText = "—", StatusIcon = "❍", MeasureDate = "未验收"
            };
            var latest = ProcLatestAcceptance(conn, locationCode, phaseId, p.ParamId);
            if (latest != null)
            {
                row.MeasuredVal = latest.MeasuredValue.HasValue ? latest.MeasuredValue.Value.ToString("F2", CultureInfo.InvariantCulture) : latest.MeasuredText ?? "—";
                row.DeviationText = ProcDeviationText(latest.DeviationPct);
                row.StatusIcon = ProcStatusIcon(latest.Status);
                row.MeasureDate = latest.MeasureDate.Length >= 10 ? latest.MeasureDate.Substring(5, 5) : latest.MeasureDate;
            }
            rows.Add(row);
        }
        return rows;
    }

    public sealed record ProcCompatModel(string Model, bool Pass, string Detail);

    /// <summary>
    /// 适配设备: 用模板推荐值(无则参数默认值)逐参数检查 hard 约束, 被违反的机型标记「排除:参数 类型 限值」;
    /// 只列环节典型设备类别(typical_equipment_category)的机型, 类别为空则列全部。Pass=适用。
    /// 无候选机型返回空表(界面按 typical 提示「(无型号数据)」/「(无 X 类型号)」)。
    /// </summary>
    public static List<ProcCompatModel> ProcCompatibleEquipment(DbConnection conn, long phaseId, long? boundTemplateId)
    {
        var phase = ProcessGetPhase(conn, phaseId);
        var paramDefs = ProcessParamsByPhase(conn, phaseId);
        var templateValues = boundTemplateId.HasValue
            ? ProcValuesByTemplate(conn, boundTemplateId.Value).ToDictionary(v => v.ParamId)
            : new Dictionary<long, ProcTemplateValue>();
        var blocked = new Dictionary<string, string>();
        foreach (var p in paramDefs)
        {
            double? testVal = null;
            if (templateValues.TryGetValue(p.ParamId, out var tv) && tv.RecommendedValue.HasValue) testVal = tv.RecommendedValue.Value;
            else if (p.StandardDefault.HasValue) testVal = p.StandardDefault.Value;
            if (!testVal.HasValue) continue;
            foreach (var c in ProcessConstraintsByParam(conn, p.ParamId))
            {
                if (c.Consequence != "hard" || !c.IsActive) continue;
                if (GeoDataQueries.ViolatesConstraint(c.ConstraintType, c.LimitValue, c.LimitMin, c.LimitMax, testVal.Value) && !blocked.ContainsKey(c.EquipmentModel))
                    blocked[c.EquipmentModel] = $"{p.Name} {c.ConstraintType} {F(c.LimitValue, "F0")}";
            }
        }
        var typical = phase?.TypicalEquipmentCategory;
        var models = conn.Query<(string model, string category)>("SELECT model, category FROM equipment_model").ToList();
        var relevant = string.IsNullOrEmpty(typical) ? models : models.Where(m => m.category == typical).ToList();
        return relevant.Select(m => blocked.TryGetValue(m.model, out var why)
            ? new ProcCompatModel(m.model, false, "排除:" + why)
            : new ProcCompatModel(m.model, true, "适用")).ToList();
    }

    public sealed record ProcPlanResult(double Strip, double Coal, int Days, string Hardness,
        string ShovelModel, double ShovelDailyCap, int ShovelCount, string TruckModel, int TruckPerShovel, int TruckCount);

    /// <summary>物料标签 → 硬度: 以 hard/medium 结尾判定, 否则 soft(原 OnGeneratePlan)。</summary>
    public static string ProcHardnessOf(string? materialTag)
    {
        var t = materialTag ?? "rh-hard";
        return t.EndsWith("hard") ? "hard" : t.EndsWith("medium") ? "medium" : "soft";
    }

    /// <summary>
    /// 方案视图(MVP)推荐配置(原 OnGeneratePlan 硬编码规则):
    /// 硬岩 4100XPC(3.0 万m³/日) / 中硬 WK-35(2.3) / 软 PH2800(2.0); 电铲数 = ceil(剥离/天/单铲日产能) ≥ 1;
    /// 剥离 + 采煤×1.6 &gt; 100 → 930E(1:3) 否则 730E(1:4); 卡车数 = 电铲数 × 配比。
    /// </summary>
    public static ProcPlanResult ProcGeneratePlan(double strip, double coal, int days, string hardness)
    {
        if (days <= 0) days = 30;
        var shovelModel = hardness == "hard" ? "4100XPC" : hardness == "medium" ? "WK-35" : "PH2800";
        double shovelDailyCap = shovelModel switch { "4100XPC" => 3.0, "WK-35" => 2.3, _ => 2.0 };
        int shovelCount = Math.Max(1, (int)Math.Ceiling(strip / days / shovelDailyCap));
        string truckModel = strip + coal * 1.6 > 100 ? "930E" : "730E";
        int truckPerShovel = truckModel == "930E" ? 3 : 4;
        int truckCount = shovelCount * truckPerShovel;
        return new ProcPlanResult(strip, coal, days, hardness, shovelModel, shovelDailyCap, shovelCount, truckModel, truckPerShovel, truckCount);
    }

    /// <summary>现行边坡设计的最小安全系数(effective_to 为空或未到期; 无记录返回 null; safety_factor NULL 按 0)。</summary>
    public static double? ProcMinSlopeSafetyFactor(DbConnection conn)
    {
        var vals = conn.Query<double?>("SELECT safety_factor FROM slope_design WHERE effective_to IS NULL OR effective_to >= CAST(CURRENT_DATE AS TEXT)").ToList();
        if (vals.Count == 0) return null;
        return vals.Min(v => v ?? 0);
    }

    /// <summary>设备台账某类别(Shovel/Truck/...)在册台数。</summary>
    public static int ProcEquipmentCountByCategory(DbConnection conn, string category)
        => conn.ExecuteScalar<int>("SELECT COUNT(*) FROM equipment WHERE category = @c", new { c = category });

    // ═══════════════════════════════════════════════════════════════════════
    // 现场验收录入行(原 AcceptanceRow: 实测值变更即时算偏差/状态)
    // ═══════════════════════════════════════════════════════════════════════

    public sealed class ProcAcceptanceRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public ProcParamDef ParamDef { get; }
        public ProcAcceptance? ExistingRecord { get; set; }
        public double? TemplateValueNumeric { get; }

        public string ParamName => ParamDef.Name;
        public string Unit => ParamDef.Unit ?? "—";
        public string StdRange => ProcStdRange(ParamDef.StandardMin, ParamDef.StandardMax);
        public string TemplateValue => ProcTemplateValText(TemplateValueNumeric, ParamDef.StandardDefault);
        public string IsRequiredText => ParamDef.IsRequired ? "✓" : "";

        private string? _measuredText;
        public string? MeasuredText
        {
            get => _measuredText;
            set
            {
                _measuredText = value; Recompute();
                OnPropertyChanged(); OnPropertyChanged(nameof(DeviationText)); OnPropertyChanged(nameof(StatusIcon));
            }
        }

        private string? _notes;
        public string? Notes { get => _notes; set { _notes = value; OnPropertyChanged(); } }

        public string DeviationText { get; private set; } = "—";
        public string StatusIcon { get; private set; } = "❍";

        /// <param name="tplVal">模板推荐值; 空则回退参数 standard_default。</param>
        public ProcAcceptanceRow(ProcParamDef def, double? tplVal, ProcAcceptance? existing)
        {
            ParamDef = def;
            TemplateValueNumeric = tplVal ?? def.StandardDefault;
            ExistingRecord = existing;
            if (existing != null)
            {
                _measuredText = existing.MeasuredValue.HasValue ? existing.MeasuredValue.Value.ToString("F2", CultureInfo.InvariantCulture) : existing.MeasuredText;
                _notes = existing.Notes;
                Recompute();
            }
        }

        /// <summary>解析实测数值(不可解析 → null, 文本型)。</summary>
        public double? MeasuredNumeric
            => double.TryParse(_measuredText, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

        private void Recompute()
        {
            if (string.IsNullOrWhiteSpace(_measuredText)) { DeviationText = "—"; StatusIcon = "❍"; return; }
            var v = MeasuredNumeric;
            if (!v.HasValue) { DeviationText = "(文本)"; StatusIcon = "—"; return; }
            var (dev, status) = GeoDataQueries.ComputeAcceptanceStatus(ParamDef.AlarmLow, ParamDef.AlarmHigh,
                ParamDef.StandardMin, ParamDef.StandardMax, TemplateValueNumeric, v);
            DeviationText = ProcDeviationText(dev);
            StatusIcon = ProcStatusIcon(status);
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// 现场验收录入: 装载某平盘+环节+日期的参数行(模板值来自现行绑定模板, 当日已有记录带出)。
    /// 返回 (行, 模板名文本)。
    /// </summary>
    public static (List<ProcAcceptanceRow> rows, string templateName) ProcLoadAcceptanceRows(DbConnection conn, string locationCode, long phaseId, DateTime measureDate)
    {
        var binding = ProcActiveBinding(conn, locationCode, phaseId);
        var paramDefs = ProcessParamsByPhase(conn, phaseId);
        var templateValues = binding?.BoundTemplateId.HasValue == true
            ? ProcValuesByTemplate(conn, binding.BoundTemplateId!.Value).ToDictionary(v => v.ParamId)
            : new Dictionary<long, ProcTemplateValue>();
        var dateKey = D(measureDate);
        var existingByParam = ProcAcceptanceByLocationPhase(conn, locationCode, phaseId)
            .Where(a => a.MeasureDate == dateKey)
            .GroupBy(a => a.ParamId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Id).First());
        var tplName = binding?.BoundTemplateId.HasValue == true
            ? ProcGetTemplate(conn, binding.BoundTemplateId!.Value)?.Name ?? "(模板已删)"
            : "(无模板)";
        var rows = new List<ProcAcceptanceRow>();
        foreach (var p in paramDefs)
        {
            templateValues.TryGetValue(p.ParamId, out var tv);
            existingByParam.TryGetValue(p.ParamId, out var existing);
            rows.Add(new ProcAcceptanceRow(p, tv?.RecommendedValue, existing));
        }
        return (rows, tplName);
    }

    public sealed record ProcSubmitResult(int Inserted, int Updated, int Skipped);

    /// <summary>
    /// 提交验收(原 OnSubmitAcceptance): 实测为空跳过; 数值型算偏差/状态, 文本型 status=pass;
    /// 已有当日记录则 UPDATE 否则 INSERT(并回填 ExistingRecord)。
    /// </summary>
    public static ProcSubmitResult ProcSubmitAcceptance(DbConnection conn, IEnumerable<ProcAcceptanceRow> rows,
        string locationCode, long phaseId, DateTime measureDate, string conclusion, string? acceptedBy, string? scope, string? remark, DateTime? acceptanceDate = null)
    {
        int inserted = 0, updated = 0, skipped = 0;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.MeasuredText)) { skipped++; continue; }
            var entity = row.ExistingRecord ?? new ProcAcceptance { ParamId = row.ParamDef.ParamId, LocationCode = locationCode, PhaseId = phaseId };
            entity.MeasureDate = D(measureDate);
            entity.TemplateValue = row.TemplateValueNumeric;
            entity.AcceptedBy = acceptedBy;
            entity.AcceptanceDate = D(acceptanceDate ?? DateTime.Today);
            entity.Conclusion = conclusion;
            entity.ScopeCode = string.IsNullOrEmpty(scope) ? null : scope;
            entity.Notes = string.IsNullOrEmpty(row.Notes) ? remark : row.Notes;
            var v = row.MeasuredNumeric;
            if (v.HasValue)
            {
                entity.MeasuredValue = v;
                entity.MeasuredText = null;
                var (dev, status) = GeoDataQueries.ComputeAcceptanceStatus(row.ParamDef.AlarmLow, row.ParamDef.AlarmHigh,
                    row.ParamDef.StandardMin, row.ParamDef.StandardMax, row.TemplateValueNumeric, v);
                entity.DeviationPct = dev;
                entity.Status = status;
            }
            else
            {
                entity.MeasuredText = row.MeasuredText;
                entity.MeasuredValue = null;
                entity.Status = "pass";
            }
            if (entity.Id == 0) { entity.Id = ProcInsertAcceptance(conn, entity); row.ExistingRecord = entity; inserted++; }
            else { ProcUpdateAcceptance(conn, entity); updated++; }
        }
        return new ProcSubmitResult(inserted, updated, skipped);
    }
}
