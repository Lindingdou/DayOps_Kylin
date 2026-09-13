using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  逐月配置表 —— 「所有月度计划编制需要的配置信息」的唯一一张表
//
//  现场原话：「进度计划编制环节中是所有的月度计划编制需要的配置信息，比如每个月的月度剥采量
//  信息等等」。此前 `ShortTermBase` 只有【年】目标 + 月【上限】，逐月量是
//  `NewCandidate(作业组织, 工作历)` 派生出来的，人填不进去；配置窗口 ③ 里那条逐月带是
//  【只读】的作业日展示。本文件把那条只读带升级成一张**可人工覆盖**的逐月表。
//
//  【口径三条，都是硬的】
//   ① **剥采比只有一个来源** —— <see cref="MonthlyTargetRow.Ratio"/> 恒 = 剥离 ÷ 采出，
//      只读、不可填。本仓库有前科：「几何算的」和「形状函数编的」两个剥采比来源打过架。
//      要改剥采比，改剥离量或采出量。
//   ② **派生初值不另写一套算法** —— <see cref="MonthlyTargetTable.BuildDefaults"/> 直接跑
//      `ShortTermBase.NewCandidate(Balanced, Standard) → ShortTermScheduler.Schedule`，
//      读它回填的 `Months`。年→月怎么摊（作业日×设备可用×组织形态 → 均衡收敛 → 月上限回摊 →
//      剥采比剖面）**只有排产器那一份实现**，这里一行都不重写。
//   ③ **逐月之和对不上年目标：如实报、不缩放**（<see cref="MonthlyTargetTable.Reconcile"/>）。
//      缩放会把"人手填的 12 个数"改成"人没填过的 12 个数"，而表面上看不出来。
//
//  【空值不是 0】
//   · 剥离能力 <see cref="MonthlyTargetRow.StripCapWanM3"/> 是 `double?`：null = 未给（不限）。
//     **绝不用 0 代替** —— 下游 `MonthlyMineSchedule` 的口径里 0 = 该月不能剥，<0 才是不限。
//   · 车队能力 <see cref="MonthlyTargetRow.FleetCapWanTKm"/> 同理，null = 未给（不卡这道闸）。
//
//  【谁在消费这张表】三个消费方必须从**同一行**取数，接法见类尾注释与交付说明的 wiring 段：
//   · 月度计划编制      ShortTermScheduler.Schedule
//   · 量驱动采剥接续    MonthlyStripWindow.BuildInput → MonthlyStripSessionInput
//   · 采掘单元排产      MiningUnitPlanWindow.BuildInput → UnitPlanInput
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>逐月配置表某一行某一列的来源。**缺省值也是决定**，所以派生与人工必须分得开。</summary>
public enum MonthlyTargetSource
{
    /// <summary>引擎派生（`ShortTermScheduler` 摊出来的）。</summary>
    Derived,
    /// <summary>人工覆盖（这一行至少有一列被人改过）。</summary>
    Manual,
}

/// <summary>
/// 可被人工覆盖的列。按**列**记而不是按行记 —— 只改了作业日的月份，
/// 重新派生时采出/剥离该跟着新年目标走，整行钉死会把没改过的列一起冻住。
/// </summary>
[Flags]
public enum MonthlyTargetField
{
    None = 0,
    Coal = 1 << 0,
    Strip = 1 << 1,
    Workdays = 1 << 2,
    StripCap = 1 << 3,
    FleetCap = 1 << 4,
    InternalDump = 1 << 5,
    Maintenance = 1 << 6,
    All = Coal | Strip | Workdays | StripCap | FleetCap | InternalDump | Maintenance,
}

/// <summary>
/// 逐月配置表的一行 = 一个月度计划期次的全部配置量。
///
/// <para><b>每一列都存两份</b>：派生值（引擎摊出来的）与生效值（界面上显示、下游拿走的）。
/// 没被覆盖时两者相等；覆盖之后生效值走人填的，派生值仍然留着 ——
/// 否则「重置为派生值」就只能靠再跑一次排产，而排产的输入这时可能已经变了。</para>
///
/// <para><b>WPF 绑定全走属性</b>（本仓库有前科：绑到字段上那一列就是空的，不报错不抛异常
/// 不写日志）。所有对外可见的量都是属性，且实现 <see cref="INotifyPropertyChanged"/>，
/// 人在 DataGrid 里改一格，「来源」「覆盖列」「剥采比」三列会立刻跟着变。</para>
/// </summary>
public sealed class MonthlyTargetRow : INotifyPropertyChanged
{
    // ── 身份 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 期次标签。**与 `ShortTermScheduler` 写进 `MonthPeriod.Label` 的那个字符串逐字一致**
    /// （`{PlanYear}-{month:00}`）—— 月度计划编制那一侧要靠它对上行。
    /// <para>⚠ 排产器的标签**不带跨年进位**：计划月数 &gt; 12 时它会重复出同一批标签
    /// （2027-01 出现两次）。所以本表另给 <see cref="PeriodKey"/> 做唯一键，
    /// 跨年时按它对行，别按 Label。</para>
    /// </summary>
    public string Label { get; internal set; } = "";

    /// <summary>真实年份（带跨年进位）。</summary>
    public int Year { get; internal set; }

    /// <summary>月份 1..12。</summary>
    public int Month { get; internal set; }

    /// <summary>第几个计划月（0 基）—— 下游那些按数组下标取数的入口（逐月煤量/逐月能力）用它。</summary>
    public int Index { get; internal set; }

    /// <summary>唯一期次键 <c>yyyy-MM</c>（带跨年进位）。落盘、跨模块对行一律用它。</summary>
    public string PeriodKey => $"{Year:0000}-{Month:00}";

    // ── 覆盖标记 ────────────────────────────────────────────────────────────

    private MonthlyTargetField _overridden = MonthlyTargetField.None;

    /// <summary>被人工覆盖的列。</summary>
    public MonthlyTargetField Overridden
    {
        get => _overridden;
        internal set
        {
            if (_overridden == value) return;
            _overridden = value;
            Raise(nameof(Overridden)); Raise(nameof(Source)); Raise(nameof(SourceText));
            Raise(nameof(IsManual)); Raise(nameof(OverriddenText));
        }
    }

    /// <summary>行级来源：任何一列被覆盖过即 <see cref="MonthlyTargetSource.Manual"/>。</summary>
    public MonthlyTargetSource Source
        => _overridden == MonthlyTargetField.None ? MonthlyTargetSource.Derived : MonthlyTargetSource.Manual;

    public bool IsManual => Source == MonthlyTargetSource.Manual;

    /// <summary>来源文案（界面「来源」列）。</summary>
    public string SourceText => IsManual ? "人工覆盖" : "引擎派生";

    /// <summary>覆盖了哪几列（界面「覆盖列」列）。空 = 一列都没改。</summary>
    public string OverriddenText => FieldText(_overridden);

    public bool IsOverridden(MonthlyTargetField f) => (_overridden & f) != 0;

    /// <summary>列名文案。</summary>
    public static string FieldText(MonthlyTargetField f)
    {
        if (f == MonthlyTargetField.None) return "";
        var parts = new List<string>();
        if ((f & MonthlyTargetField.Coal) != 0) parts.Add("采出");
        if ((f & MonthlyTargetField.Strip) != 0) parts.Add("剥离");
        if ((f & MonthlyTargetField.Workdays) != 0) parts.Add("作业日");
        if ((f & MonthlyTargetField.StripCap) != 0) parts.Add("剥离能力");
        if ((f & MonthlyTargetField.FleetCap) != 0) parts.Add("车队能力");
        if ((f & MonthlyTargetField.InternalDump) != 0) parts.Add("内排");
        if ((f & MonthlyTargetField.Maintenance) != 0) parts.Add("检修");
        return string.Join("·", parts);
    }

    // ── 量列：生效值 + 派生值 ───────────────────────────────────────────────

    private double _coal, _dCoal;
    private double _strip, _dStrip;
    private double _workdays, _dWorkdays;
    private double? _stripCap, _dStripCap;
    private double? _fleetCap, _dFleetCap;
    private bool _internalDump, _dInternalDump;
    private bool _maint, _dMaint;

    /// <summary>本月采出（万t）。</summary>
    public double CoalWanT
    {
        get => _coal;
        set => SetUser(ref _coal, value, MonthlyTargetField.Coal, nameof(CoalWanT), alsoRaise: nameof(Ratio));
    }

    /// <summary>本月剥离（万m³ 原位实方）。</summary>
    public double StripWanM3
    {
        get => _strip;
        set => SetUser(ref _strip, value, MonthlyTargetField.Strip, nameof(StripWanM3), alsoRaise: nameof(Ratio));
    }

    /// <summary>
    /// 生产剥采比（m³实方/t）—— <b>只读派生</b>：剥离 ÷ 采出。
    /// <para>不给 setter 是有意的：本仓库出过「几何算的」与「形状函数编的」两个剥采比来源打架，
    /// 一个量只能有一个来源。要调剥采比，调 <see cref="StripWanM3"/> 或 <see cref="CoalWanT"/>。</para>
    /// </summary>
    public double Ratio => _coal > 1e-9 ? Math.Round(_strip / _coal, 2) : 0;

    /// <summary>本月有效作业日。</summary>
    public double Workdays
    {
        get => _workdays;
        set => SetUser(ref _workdays, value, MonthlyTargetField.Workdays, nameof(Workdays));
    }

    /// <summary>
    /// 本月剥离能力上限（万m³）。<b>null = 未给（不限）</b>。
    /// <para>⚠ 不许用 0 表示"未给"：下游 <c>MonthlyMineSchedule</c> 的口径里
    /// <b>0 = 该月不能剥</b>，负数才是不限。转数组时见
    /// <see cref="MonthlyTargetTable.StripCapM3Array"/>。</para>
    /// </summary>
    public double? StripCapWanM3
    {
        get => _stripCap;
        set => SetUserN(ref _stripCap, value, MonthlyTargetField.StripCap, nameof(StripCapWanM3));
    }

    /// <summary>
    /// 本月车队运输能力（万t·km）。<b>null = 未给（不卡这道闸）</b>。
    /// <para>本系统里这个量<b>没有台账来源</b>：`equipment_model` 只有载重 `load_t`，
    /// 没有出勤率/循环时间口径，推不出 t·km。所以派生值恒为 null，要卡请手填。</para>
    /// </summary>
    public double? FleetCapWanTKm
    {
        get => _fleetCap;
        set => SetUserN(ref _fleetCap, value, MonthlyTargetField.FleetCap, nameof(FleetCapWanTKm));
    }

    /// <summary>本月是否允许内排。</summary>
    public bool InternalDumpEnabled
    {
        get => _internalDump;
        set => SetUserB(ref _internalDump, value, MonthlyTargetField.InternalDump, nameof(InternalDumpEnabled));
    }

    /// <summary>本月是否集中检修月。</summary>
    public bool IsMaintenance
    {
        get => _maint;
        set => SetUserB(ref _maint, value, MonthlyTargetField.Maintenance, nameof(IsMaintenance));
    }

    /// <summary>行备注（引擎写的降级说明 / 人写的理由）。不参与覆盖标记。</summary>
    public string Note
    {
        get => _note;
        set { if (_note == (value ?? "")) return; _note = value ?? ""; Raise(nameof(Note)); }
    }
    private string _note = "";

    // ── 派生写入（引擎侧）───────────────────────────────────────────────────

    /// <summary>
    /// 写派生值。<b>已被人工覆盖的列只更新"派生值"那一份，不动生效值</b> ——
    /// 这就是「人工覆盖的月份重新派生时不许被冲掉」那条。
    /// </summary>
    internal void SetDerived(MonthlyTargetField f, double v)
    {
        switch (f)
        {
            case MonthlyTargetField.Coal:
                _dCoal = v; if (!IsOverridden(f)) { _coal = v; Raise(nameof(CoalWanT)); Raise(nameof(Ratio)); }
                break;
            case MonthlyTargetField.Strip:
                _dStrip = v; if (!IsOverridden(f)) { _strip = v; Raise(nameof(StripWanM3)); Raise(nameof(Ratio)); }
                break;
            case MonthlyTargetField.Workdays:
                _dWorkdays = v; if (!IsOverridden(f)) { _workdays = v; Raise(nameof(Workdays)); }
                break;
            default: throw new ArgumentException($"{f} 不是 double 列", nameof(f));
        }
    }

    internal void SetDerivedN(MonthlyTargetField f, double? v)
    {
        switch (f)
        {
            case MonthlyTargetField.StripCap:
                _dStripCap = v; if (!IsOverridden(f)) { _stripCap = v; Raise(nameof(StripCapWanM3)); }
                break;
            case MonthlyTargetField.FleetCap:
                _dFleetCap = v; if (!IsOverridden(f)) { _fleetCap = v; Raise(nameof(FleetCapWanTKm)); }
                break;
            default: throw new ArgumentException($"{f} 不是 double? 列", nameof(f));
        }
    }

    internal void SetDerivedB(MonthlyTargetField f, bool v)
    {
        switch (f)
        {
            case MonthlyTargetField.InternalDump:
                _dInternalDump = v; if (!IsOverridden(f)) { _internalDump = v; Raise(nameof(InternalDumpEnabled)); }
                break;
            case MonthlyTargetField.Maintenance:
                _dMaint = v; if (!IsOverridden(f)) { _maint = v; Raise(nameof(IsMaintenance)); }
                break;
            default: throw new ArgumentException($"{f} 不是 bool 列", nameof(f));
        }
    }

    /// <summary>读某一列的派生值（对账/「重置为派生值」预览用）。</summary>
    public double DerivedOf(MonthlyTargetField f) => f switch
    {
        MonthlyTargetField.Coal => _dCoal,
        MonthlyTargetField.Strip => _dStrip,
        MonthlyTargetField.Workdays => _dWorkdays,
        _ => throw new ArgumentException($"{f} 不是 double 列", nameof(f)),
    };

    public double? DerivedNOf(MonthlyTargetField f) => f switch
    {
        MonthlyTargetField.StripCap => _dStripCap,
        MonthlyTargetField.FleetCap => _dFleetCap,
        _ => throw new ArgumentException($"{f} 不是 double? 列", nameof(f)),
    };

    public bool DerivedBOf(MonthlyTargetField f) => f switch
    {
        MonthlyTargetField.InternalDump => _dInternalDump,
        MonthlyTargetField.Maintenance => _dMaint,
        _ => throw new ArgumentException($"{f} 不是 bool 列", nameof(f)),
    };

    // ── 重置 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把指定列**重置为派生值**并清掉覆盖标记。这是**唯一**能冲掉人工值的动作，
    /// 必须由用户显式触发。返回真正被重置的列（一列都没覆盖过时返回 None）。
    /// </summary>
    public MonthlyTargetField ResetToDerived(MonthlyTargetField fields = MonthlyTargetField.All)
    {
        var hit = _overridden & fields;
        if (hit == MonthlyTargetField.None) return MonthlyTargetField.None;

        if ((hit & MonthlyTargetField.Coal) != 0) { _coal = _dCoal; Raise(nameof(CoalWanT)); }
        if ((hit & MonthlyTargetField.Strip) != 0) { _strip = _dStrip; Raise(nameof(StripWanM3)); }
        if ((hit & (MonthlyTargetField.Coal | MonthlyTargetField.Strip)) != 0) Raise(nameof(Ratio));
        if ((hit & MonthlyTargetField.Workdays) != 0) { _workdays = _dWorkdays; Raise(nameof(Workdays)); }
        if ((hit & MonthlyTargetField.StripCap) != 0) { _stripCap = _dStripCap; Raise(nameof(StripCapWanM3)); }
        if ((hit & MonthlyTargetField.FleetCap) != 0) { _fleetCap = _dFleetCap; Raise(nameof(FleetCapWanTKm)); }
        if ((hit & MonthlyTargetField.InternalDump) != 0) { _internalDump = _dInternalDump; Raise(nameof(InternalDumpEnabled)); }
        if ((hit & MonthlyTargetField.Maintenance) != 0) { _maint = _dMaint; Raise(nameof(IsMaintenance)); }

        Overridden = _overridden & ~hit;
        return hit;
    }

    // ── 读盘专用：直接落"人工覆盖值 + 覆盖标记" ─────────────────────────────
    //
    // ⚠ 读盘**不能**走公开 setter：setter 靠"值变了没有"来打标，而存盘那一刻完全可能
    //    人工值 == 派生值（覆盖之后又重新派生过一次，派生值追上来了）。那种行走公开
    //    setter 会因为"值没变"而**不打标**，覆盖就在一次存读之间无声无息地没了。

    internal void LoadOverride(MonthlyTargetField f, double v)
    {
        switch (f)
        {
            case MonthlyTargetField.Coal: _coal = v; Raise(nameof(CoalWanT)); Raise(nameof(Ratio)); break;
            case MonthlyTargetField.Strip: _strip = v; Raise(nameof(StripWanM3)); Raise(nameof(Ratio)); break;
            case MonthlyTargetField.Workdays: _workdays = v; Raise(nameof(Workdays)); break;
            default: throw new ArgumentException($"{f} 不是 double 列", nameof(f));
        }
        Overridden = _overridden | f;
    }

    internal void LoadOverrideN(MonthlyTargetField f, double? v)
    {
        switch (f)
        {
            case MonthlyTargetField.StripCap: _stripCap = v; Raise(nameof(StripCapWanM3)); break;
            case MonthlyTargetField.FleetCap: _fleetCap = v; Raise(nameof(FleetCapWanTKm)); break;
            default: throw new ArgumentException($"{f} 不是 double? 列", nameof(f));
        }
        Overridden = _overridden | f;
    }

    internal void LoadOverrideB(MonthlyTargetField f, bool v)
    {
        switch (f)
        {
            case MonthlyTargetField.InternalDump: _internalDump = v; Raise(nameof(InternalDumpEnabled)); break;
            case MonthlyTargetField.Maintenance: _maint = v; Raise(nameof(IsMaintenance)); break;
            default: throw new ArgumentException($"{f} 不是 bool 列", nameof(f));
        }
        Overridden = _overridden | f;
    }

    /// <summary>把另一行的**人工覆盖**照搬过来（合并用）。派生值不动。</summary>
    internal void TakeOverridesFrom(MonthlyTargetRow other)
    {
        if ((other._overridden & MonthlyTargetField.Coal) != 0) LoadOverride(MonthlyTargetField.Coal, other._coal);
        if ((other._overridden & MonthlyTargetField.Strip) != 0) LoadOverride(MonthlyTargetField.Strip, other._strip);
        if ((other._overridden & MonthlyTargetField.Workdays) != 0) LoadOverride(MonthlyTargetField.Workdays, other._workdays);
        if ((other._overridden & MonthlyTargetField.StripCap) != 0) LoadOverrideN(MonthlyTargetField.StripCap, other._stripCap);
        if ((other._overridden & MonthlyTargetField.FleetCap) != 0) LoadOverrideN(MonthlyTargetField.FleetCap, other._fleetCap);
        if ((other._overridden & MonthlyTargetField.InternalDump) != 0) LoadOverrideB(MonthlyTargetField.InternalDump, other._internalDump);
        if ((other._overridden & MonthlyTargetField.Maintenance) != 0) LoadOverrideB(MonthlyTargetField.Maintenance, other._maint);
        if (other.Note.Length > 0 && Note.Length == 0) Note = other.Note;
    }

    // ── INotifyPropertyChanged ──────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p ?? ""));

    // 值没变就不打标 —— DataGrid 进出编辑态会把原值回写一次，那不是"人改过"；
    // 否则光是用键盘在表里走一趟，整表都会变成「人工覆盖」。
    // 代价：照着派生值原样敲一遍不会被记成覆盖（下次重新派生它还是跟着走）。这是有意的。
    private void SetUser(ref double slot, double v, MonthlyTargetField f, string prop, string? alsoRaise = null)
    {
        if (Math.Abs(slot - v) < 1e-9) return;
        slot = v;
        Overridden = _overridden | f;
        Raise(prop);
        if (alsoRaise != null) Raise(alsoRaise);
    }

    private void SetUserN(ref double? slot, double? v, MonthlyTargetField f, string prop)
    {
        if (Nullable.Equals(slot, v)) return;
        slot = v;
        Overridden = _overridden | f;
        Raise(prop);
    }

    private void SetUserB(ref bool slot, bool v, MonthlyTargetField f, string prop)
    {
        if (slot == v) return;
        slot = v;
        Overridden = _overridden | f;
        Raise(prop);
    }

    public override string ToString()
        => $"{PeriodKey} 采出{_coal:0.#}万t 剥离{_strip:0}万m³ 比{Ratio:0.00} 作业{_workdays:0.#}天 [{SourceText}]";
}

/// <summary>逐月之和与年目标的对账结果 —— <b>只报不改</b>。</summary>
public sealed class MonthlyTargetReconcile
{
    public double SumCoalWanT, AnnualCoalTargetWanT;
    public double SumStripWanM3, AnnualStripTargetWanM3;
    public double TolerancePct;
    public double YtdCoalWanT, YtdStripWanM3;
    public int MonthCount, ManualRowCount;

    public double CoalDeltaWanT => SumCoalWanT - AnnualCoalTargetWanT;
    public double StripDeltaWanM3 => SumStripWanM3 - AnnualStripTargetWanM3;
    public double CoalDeltaPct => AnnualCoalTargetWanT > 1e-9 ? CoalDeltaWanT / AnnualCoalTargetWanT * 100 : 0;
    public double StripDeltaPct => AnnualStripTargetWanM3 > 1e-9 ? StripDeltaWanM3 / AnnualStripTargetWanM3 * 100 : 0;

    /// <summary>逐月合计剥采比（由合计量派生，不是各月剥采比的平均 —— 那会把小月放大）。</summary>
    public double SumRatio => SumCoalWanT > 1e-9 ? Math.Round(SumStripWanM3 / SumCoalWanT, 2) : 0;

    public bool CoalOk => AnnualCoalTargetWanT <= 1e-9 || Math.Abs(CoalDeltaPct) <= TolerancePct + 1e-6;
    public bool StripOk => AnnualStripTargetWanM3 <= 1e-9 || Math.Abs(StripDeltaPct) <= TolerancePct + 1e-6;
    public bool Ok => CoalOk && StripOk && Issues.Count == 0;

    /// <summary>结构性问题（负数、剥采比失真、能力口径踩雷…）。与"对不上年目标"分开列。</summary>
    public List<string> Issues { get; } = new();

    public string Report()
    {
        var sb = new StringBuilder();
        sb.Append($"逐月合计：采出 {SumCoalWanT:0.#} 万t / 年目标 {AnnualCoalTargetWanT:0.#} 万t"
                + $"（差 {CoalDeltaWanT:+0.#;-0.#;0} 万t，{CoalDeltaPct:+0.0;-0.0;0.0}%，容差 ±{TolerancePct:0.#}%）"
                + (CoalOk ? " ✔" : " ◆对不上"));
        sb.Append($"　·　剥离 {SumStripWanM3:0} 万m³ / 年目标 {AnnualStripTargetWanM3:0} 万m³"
                + $"（差 {StripDeltaWanM3:+0;-0;0} 万m³，{StripDeltaPct:+0.0;-0.0;0.0}%）"
                + (StripOk ? " ✔" : " ◆对不上"));
        sb.Append($"　·　合计剥采比 {SumRatio:0.00} m³/t　·　{MonthCount} 个月，其中 {ManualRowCount} 个月有人工覆盖");
        if (YtdCoalWanT > 1e-9 || YtdStripWanM3 > 1e-9)
            sb.Append($"\n· 年初已完成 采出 {YtdCoalWanT:0.#} 万t / 剥离 {YtdStripWanM3:0} 万m³ —— "
                    + "**未计入本表口径**（与 ShortTermScheduler 的完成率一致：完成率 = 本表合计 ÷ 年目标）。");
        // 对不上就摆着，绝不缩放：缩放会把"人手填的 12 个数"改成"人没填过的 12 个数"，
        // 而表面上一点看不出来。
        if (!CoalOk || !StripOk)
            sb.Append("\n◆ 逐月之和与年目标对不上 —— 本表**不自动缩放**。"
                    + "要么改年目标、要么改逐月量、要么就这么用（差额会原样传到下游完成率）。");
        foreach (var s in Issues) sb.Append("\n" + s);
        return sb.ToString();
    }

    public override string ToString() => Report();
}

/// <summary>
/// 逐月配置表 —— 月度计划编制、量驱动采剥接续、采掘单元排产**共用的那一张表**。
/// </summary>
public sealed class MonthlyTargetTable
{
    /// <summary>格式版本（落盘头里写一份，将来加列时按它兼容）。</summary>
    public const string FormatVersion = "1";

    public ObservableCollection<MonthlyTargetRow> Rows { get; } = new();

    // ── 派生时的骨架与年目标快照（对账口径的来源，落盘要带走）──
    public int PlanYear { get; private set; } = 2027;
    public int StartMonth { get; private set; } = 1;
    public int MonthCount { get; private set; } = 12;
    public double AnnualCoalTargetWanT { get; private set; }
    public double AnnualStripTargetWanM3 { get; private set; }
    public double CompletionTolerancePct { get; private set; } = 3;
    public double YtdCoalWanT { get; private set; }
    public double YtdStripWanM3 { get; private set; }
    public double CoalDensity { get; private set; } = ShortTermPlan.DefaultCoalDensity;

    /// <summary>上一次派生的时刻（人要能看出表里的数是什么时候摊的）。</summary>
    public DateTime? DerivedAt { get; private set; }

    /// <summary>
    /// <b>引擎替用户做的每一个决定</b>（默认值也是决定）。界面照着显示这一列，别自己再解释一遍。
    /// 每次派生重建。
    /// </summary>
    public List<string> Notes { get; } = new();

    public string Caption => DerivedAt == null
        ? "（尚未派生）"
        : $"{PlanYear}年 · 起始{StartMonth}月 · {MonthCount}个月 · 年采出 {AnnualCoalTargetWanT:0.#}万t / 年剥离 {AnnualStripTargetWanM3:0}万m³"
        + $" · 派生于 {DerivedAt:MM-dd HH:mm}"
        + $" · 人工覆盖 {ManualRowCount} 行";

    public int ManualRowCount => Rows.Count(r => r.IsManual);

    // ══════════════════════════════════════════════════════════════
    //  派生
    // ══════════════════════════════════════════════════════════════

    /// <summary>从基础约束派生一张全新的逐月配置表（表里一列覆盖都没有）。</summary>
    public static MonthlyTargetTable BuildDefaults(ShortTermBase basis)
    {
        var t = new MonthlyTargetTable();
        t.Rebuild(basis);
        return t;
    }

    /// <summary>
    /// 按当前基础约束**重新派生**。<b>人工覆盖过的列一律保住</b>（只更新它们的"派生值"那一份，
    /// 供「重置为派生值」用）；要冲掉人工值只能显式调 <see cref="ResetToDerived"/>。
    ///
    /// <para><b>派生初值不另写算法</b>：直接跑 `basis.NewCandidate(均衡型, 标准工作历)` +
    /// `ShortTermScheduler.Schedule`，读它回填的 `Months`。年→月怎么摊只有排产器那一份实现。</para>
    /// </summary>
    public void Rebuild(ShortTermBase basis)
    {
        if (basis == null) throw new ArgumentNullException(nameof(basis));
        Notes.Clear();

        PlanYear = basis.PlanYear;
        StartMonth = Math.Clamp(basis.StartMonth, 1, 12);
        MonthCount = Math.Clamp(basis.MonthCount, 1, 24);
        AnnualCoalTargetWanT = basis.AnnualCoalTargetWanT;
        AnnualStripTargetWanM3 = basis.AnnualStripTargetWanM3;
        CompletionTolerancePct = basis.CompletionTolerancePct > 0 ? basis.CompletionTolerancePct : 3;
        YtdCoalWanT = basis.Field.YtdActualCoalWanT;
        YtdStripWanM3 = basis.Field.YtdActualStripWanM3;

        // ① 跑排产器要初值 —— 这是全表唯一的年→月摊分来源。
        List<MonthPeriod> src;
        try
        {
            var probe = basis.NewCandidate(DispatchStrategy.Balanced, CalendarScenario.Standard, "（逐月配置表·派生初值）");
            // ★ 必须传 NoOverride()：排产器默认会去取 MonthlyTargetStore.Current。
            //   ① Current 是懒建的 —— 建的过程里再访问它就是无限递归（栈溢出，不是异常）；
            //   ② 派生值要是把本表自己的人工覆盖吃进去，「重置为派生值」就再也回不到引擎摊的那个数，
            //      而表面上完全看不出来（那一格有数、来源也写着"引擎派生"）。
            ShortTermScheduler.Schedule(probe, NoOverride());
            src = probe.Months.ToList();
            Notes.Add("· 逐月采出/剥离/作业日的**派生初值**来自「月度计划编制」的排产器"
                    + "（ShortTermScheduler，均衡型 × 标准工作历）—— 本表不另写一套摊分算法。"
                    + "作业组织/工作历换了轴，派生初值会跟着变。");
        }
        catch (Exception ex)
        {
            // 派生不出来就如实空着。绝不退回一套"自己摊一遍"的兜底 ——
            // 那正是"两份实现迟早漂"的来源，而且第二份还只在出故障时才跑，永远没人验。
            Notes.Add($"◆ 派生失败，本表没有初值（排产器抛了：{ex.Message}）。"
                    + "逐月量请手工填，或先在上面把基础约束改到能试算通过。");
            src = new List<MonthPeriod>();
        }

        if (src.Count > 0 && src.Count != MonthCount)
            Notes.Add($"◆ 排产器回填了 {src.Count} 个月，与时间骨架的 {MonthCount} 个月不一致 —— "
                    + "按较小的那个建表，多出来的月份没有初值。");

        // ② 建/对行：按唯一期次键 PeriodKey 对，跨年也不会串行。
        var old = Rows.ToDictionary(r => r.PeriodKey, r => r, StringComparer.Ordinal);
        var keep = new List<MonthlyTargetRow>();
        int n = src.Count > 0 ? Math.Min(MonthCount, src.Count) : MonthCount;

        // ③ 内排是否允许：上游中长远只有【年】粒度的开关与起转年，本表逐月同值。
        bool innerOk = ResolveInternalDump(basis, out string innerNote);
        Notes.Add(innerNote);

        // ④ 剥离能力：本月总作业能力 − 采出折方。总作业能力的公式与排产器设备利用率那条同源
        //    （台数 × 单台满月能力 × 作业日折算 × 完好率）。⚠ 这是**这张表里唯一重写的一条公式**，
        //    判据 M6 把它与排产器回填的 EquipUtilPct 钉在一起，那边改了这边会红。
        double avail = Math.Clamp(basis.Field.EquipmentAvailabilityPct / 100.0, 0.3, 1.0);
        double stdWd = Math.Max(1, basis.Field.StandardWorkdays);
        double rho = CoalDensity;
        int capMissing = 0;
        var capMissingKeys = new List<string>();

        for (int i = 0; i < n; i++)
        {
            int m = ((StartMonth - 1 + i) % 12) + 1;
            int year = PlanYear + (StartMonth - 1 + i) / 12;
            string key = $"{year:0000}-{m:00}";

            if (!old.TryGetValue(key, out var row))
                row = new MonthlyTargetRow();
            old.Remove(key);

            row.Index = i;
            row.Year = year;
            row.Month = m;
            // 标签逐字沿用排产器的（月度计划编制那侧靠它对行）；没有初值时按同一格式自己拼。
            row.Label = i < src.Count ? src[i].Label : $"{PlanYear}-{m:00}";

            if (i < src.Count)
            {
                var s = src[i];
                row.SetDerived(MonthlyTargetField.Coal, s.CoalWanT);
                row.SetDerived(MonthlyTargetField.Strip, s.StripWanM3);
                row.SetDerived(MonthlyTargetField.Workdays, s.Workdays);
                row.SetDerivedB(MonthlyTargetField.Maintenance, s.IsMaintenance);

                double capWan = basis.Field.EquipmentCount * basis.Field.EquipMonthlyCapacityWanM3
                                * (s.Workdays / stdWd) * avail;
                double coalVol = rho > 1e-9 ? s.CoalWanT / rho : 0;
                double stripCap = capWan - coalVol;
                if (stripCap > 1e-9) row.SetDerivedN(MonthlyTargetField.StripCap, Math.Round(stripCap, 0));
                else
                {
                    // 算出来 ≤0 就是「设备能力还不够把煤挖出来」。**不填 0** ——
                    // 0 在下游是「该月不能剥」，把一个算不出来的量写成一条硬约束是最坏的一种冒充。
                    row.SetDerivedN(MonthlyTargetField.StripCap, null);
                    capMissing++;
                    capMissingKeys.Add($"{key}(能力{capWan:0}<采出折方{coalVol:0})");
                }
            }
            else
            {
                row.SetDerived(MonthlyTargetField.Coal, 0);
                row.SetDerived(MonthlyTargetField.Strip, 0);
                row.SetDerived(MonthlyTargetField.Workdays, basis.Field.WorkdaysFor(m, CalendarScenario.Standard));
                row.SetDerivedB(MonthlyTargetField.Maintenance, basis.Field.MaintenanceMonth == m);
                row.SetDerivedN(MonthlyTargetField.StripCap, null);
            }

            // 车队能力没有台账来源 —— 派生值恒 null（不是 0）。
            row.SetDerivedN(MonthlyTargetField.FleetCap, null);
            row.SetDerivedB(MonthlyTargetField.InternalDump, innerOk);

            keep.Add(row);
        }

        // ⑤ 掉出骨架的行：**带着人工覆盖消失是要报的**，不静默丢。
        var lostManual = old.Values.Where(r => r.IsManual).Select(r => r.PeriodKey).ToList();
        if (lostManual.Count > 0)
            Notes.Add($"◆ 有 {lostManual.Count} 个月已不在时间骨架内、它们身上的人工覆盖随之丢弃："
                    + string.Join("、", lostManual.Take(6))
                    + (lostManual.Count > 6 ? $" …等 {lostManual.Count} 个" : "")
                    + "。改回起始月/月数可以让它们回来，但覆盖值已经没了。");

        Rows.Clear();
        foreach (var r in keep) Rows.Add(r);

        if (capMissing > 0)
            Notes.Add($"· 有 {capMissing} 个月的【剥离能力】派生不出来（设备总能力不够覆盖采出折方），已留空："
                    + string.Join("、", capMissingKeys.Take(6)) + (capMissing > 6 ? " …" : "")
                    + "。留空 = 不限；下游口径里 0 是「该月不能剥」，所以这里**没有填 0**。");
        Notes.Add("· 【剥离能力】= 台数 × 单台满月能力 × (本月作业日 ÷ 月标准作业日) × 完好率 − 采出折方"
                + $"（煤视密度 {rho:0.###} t/m³）。这是引擎替你选的口径，改现场参数会跟着变。");
        Notes.Add("· 【车队能力(万t·km)】**没有台账来源**：equipment_model 只有载重 load_t，"
                + "没有出勤率/循环时间口径，推不出 t·km —— 全部留空（= 不卡这道闸）。要卡请手填。");
        Notes.Add("· 【剥采比】是只读派生列（剥离 ÷ 采出），不接受手填 —— 一个量只能有一个来源。");

        int manual = ManualRowCount;
        if (manual > 0)
            Notes.Add($"· 本次重新派生保住了 {manual} 行的人工覆盖（只更新了它们的派生值）。"
                    + "要让它们回到派生值，点「重置为派生值」。");

        DerivedAt = DateTime.Now;
    }

    /// <summary>
    /// 内排是否允许 —— 与 `ShortTermScheduler.ApplyInternalDumpTiming` 同一条上游规则：
    /// 总开关关着 → 全年只能外排；计划年早于「起始年 + 内排起转年」→ 本年也只能外排。
    /// 无上游来源时不限制（以去向台账 dump_site 为准）。
    /// </summary>
    private static bool ResolveInternalDump(ShortTermBase basis, out string note)
    {
        var lt = basis.SourceLongTerm;
        if (lt == null)
        {
            note = "· 【内排】无中长远来源，逐月按**允许**给（以去向台账 dump_site 的启用/关闭为准）。"
                 + "这是引擎替你做的决定，不对请逐月改。";
            return true;
        }
        if (!lt.InnerDumpEnabled)
        {
            note = $"· 【内排】上游「{lt.Name}」未启用内排 → 逐月按**不允许**给（本年剥离全部外排）。";
            return false;
        }
        int openYear = Math.Clamp(lt.StartYear + Math.Max(0, lt.InnerDumpStartYear), 1, 9999);
        bool ok = basis.PlanYear >= openYear;
        note = ok
            ? $"· 【内排】上游内排起转年 {openYear}，本计划年 {basis.PlanYear} 已到 → 逐月按**允许**给。"
            : $"· 【内排】上游内排起转年 {openYear}，本计划年 {basis.PlanYear} 未到（采空区还没形成）→ 逐月按**不允许**给。";
        // 上游只有年粒度，所以逐月同值 —— 月中起转要人工改。
        note += "上游只有【年】粒度，本表逐月同值；要月中起转请手工覆盖那几行。";
        return ok;
    }

    // ══════════════════════════════════════════════════════════════
    //  覆盖 / 重置
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 把另一张表的**人工覆盖**合并进来（读盘、或从别的方案搬覆盖）。
    /// 按 <see cref="MonthlyTargetRow.PeriodKey"/> 对行；对不上的行如实报，不静默丢。
    /// </summary>
    public int ApplyOverrides(MonthlyTargetTable from, out List<string> issues)
    {
        issues = new List<string>();
        if (from == null) { issues.Add("没给来源表。"); return 0; }

        var mine = new Dictionary<string, MonthlyTargetRow>(StringComparer.Ordinal);
        foreach (var r in Rows) mine[r.PeriodKey] = r;

        int applied = 0;
        var orphan = new List<string>();
        foreach (var s in from.Rows)
        {
            if (!s.IsManual && s.Note.Length == 0) continue;
            if (!mine.TryGetValue(s.PeriodKey, out var d)) { if (s.IsManual) orphan.Add(s.PeriodKey); continue; }
            d.TakeOverridesFrom(s);
            if (s.IsManual) applied++;
        }
        if (orphan.Count > 0)
            issues.Add($"◆ 来源表里有 {orphan.Count} 个月不在当前时间骨架内，它们的人工覆盖没有落进来："
                     + string.Join("、", orphan.Take(6)) + (orphan.Count > 6 ? " …" : "")
                     + "。改起始月/月数覆盖到那些月份后再读一次。");
        return applied;
    }

    /// <summary>全表重置为派生值（清掉所有人工覆盖）。返回被重置的行数。</summary>
    public int ResetToDerived(MonthlyTargetField fields = MonthlyTargetField.All)
    {
        int n = 0;
        foreach (var r in Rows) if (r.ResetToDerived(fields) != MonthlyTargetField.None) n++;
        return n;
    }

    /// <summary>重置指定几行。返回被重置的行数。</summary>
    public int ResetToDerived(IEnumerable<MonthlyTargetRow> rows, MonthlyTargetField fields = MonthlyTargetField.All)
    {
        int n = 0;
        foreach (var r in rows) if (r != null && r.ResetToDerived(fields) != MonthlyTargetField.None) n++;
        return n;
    }

    // ══════════════════════════════════════════════════════════════
    //  取数（三个消费方共用的入口）
    // ══════════════════════════════════════════════════════════════

    /// <summary>按唯一期次键取行（<c>yyyy-MM</c>）。取不到返回 null —— 别用"第一行"顶上。</summary>
    public MonthlyTargetRow? Find(string? periodKey)
        => string.IsNullOrWhiteSpace(periodKey) ? null
         : Rows.FirstOrDefault(r => string.Equals(r.PeriodKey, periodKey!.Trim(), StringComparison.Ordinal));

    /// <summary>
    /// 按排产器标签取行。<b>标签在计划月数 &gt; 12 时会重复</b>（排产器不带跨年进位），
    /// 重复时 <paramref name="ambiguous"/> 为真、返回第一条 —— 调用方要显式处理，别当成唯一。
    /// </summary>
    public MonthlyTargetRow? FindByLabel(string? label, out bool ambiguous)
    {
        ambiguous = false;
        if (string.IsNullOrWhiteSpace(label)) return null;
        var hit = Rows.Where(r => string.Equals(r.Label, label!.Trim(), StringComparison.Ordinal)).ToList();
        ambiguous = hit.Count > 1;
        return hit.FirstOrDefault();
    }

    /// <summary>按第几个计划月取行（0 基）。</summary>
    public MonthlyTargetRow? At(int index) => index >= 0 && index < Rows.Count ? Rows[index] : null;

    /// <summary>逐月采出（万t）—— 「量驱动采剥接续」的 <c>CoalTargetWt</c> 就是它。</summary>
    public double[] CoalTargetWt() => Rows.Select(r => r.CoalWanT).ToArray();

    /// <summary>逐月剥离（万m³ 实方）。</summary>
    public double[] StripWanM3Array() => Rows.Select(r => r.StripWanM3).ToArray();

    /// <summary>逐月有效作业日 —— 「量驱动采剥接续」的 <c>Workdays</c>。</summary>
    public double[] WorkdaysArray() => Rows.Select(r => r.Workdays).ToArray();

    /// <summary>
    /// 逐月剥离能力（<b>m³，不是万m³</b>）—— 「量驱动采剥接续」的 <c>StripCapM3</c>。
    /// <para>口径转换在这里做一次，别让每个调用方各转一遍：
    /// <b>未给（null）→ −1（下游口径里负数 = 该月不限）</b>；<b>绝不写 0</b>（0 = 该月不能剥）。</para>
    /// <para><paramref name="allMissing"/> 为真时整列都没给 —— 调用方可以干脆传空数组（= 全程不限），
    /// 效果一样但下游日志里更清楚。</para>
    /// </summary>
    public double[] StripCapM3Array(out bool allMissing)
    {
        var a = Rows.Select(r => r.StripCapWanM3.HasValue ? r.StripCapWanM3.Value * 1e4 : -1.0).ToArray();
        allMissing = a.Length == 0 || a.All(v => v < 0);
        return a;
    }

    /// <summary>
    /// 「本次不取任何覆盖」的空表。
    /// <para>用在<b>派生初值那一跑</b>（<see cref="Rebuild"/> → <c>ShortTermScheduler.Schedule</c>）：
    /// 空表 = 一行都对不上 = 一列覆盖都不生效。见 <see cref="Rebuild"/> 里那两条理由。</para>
    /// </summary>
    public static MonthlyTargetTable NoOverride() => new();

    // ── ★ 剥离能力：两个引擎对 0 的读法是【相反】的，转换各只做一次，都在本类里 ──────
    //
    //   MonthlyMineSchedule（量驱动采剥接续）：`>= 0 ? 值 : +∞` ⇒ **0 = 该月不能剥**，<0 才是不限。
    //                                          未给 → −1，见 StripCapM3Array。
    //   UnitPlanEngine     （采掘单元排产）  ：`> 0 才卡`        ⇒ **0 = 不卡**。
    //                                          未给 → 0，见下面两个方法。
    //   两条链的转换写在别处就是第二个口径 —— 而这种错不会报，只会让某个月悄悄地"剥不了"或"随便剥"。

    /// <summary>
    /// 「采掘单元排产」口径的本月剥离能力（<b>m³</b>，<c>UnitPlanInput.StripCapM3</c>）。
    /// 未给 → <b>0（= 不卡这道闸）</b>。
    /// <para><paramref name="note"/> 非 null 时<b>必须原样报给用户</b>：
    /// 「未给」和「人手填了 0」在这条链上转出来是同一个 0，可它们在<b>另一条链</b>上意思正相反
    /// （0 = 该月不能剥）。不说的话，用户在表里填的那个 0 就被无声地读成了"不限"。</para>
    /// </summary>
    public static double StripCapM3ForUnitEngine(MonthlyTargetRow? row, out string? note)
    {
        note = null;
        double? v = row?.StripCapWanM3;
        if (v == null)
        {
            note = "· 本月【剥离能力】表里没给（留空 = 不限）→ 采掘单元排产按【不卡】传（该引擎里 0 = 不卡）。";
            return 0;
        }
        if (v.Value <= 1e-9)
        {
            note = $"◆ 本月【剥离能力】表里填的是 {v.Value:0.###} 万m³ —— 逐月配置表的口径里 **0 = 该月不能剥**，"
                 + "可采掘单元排产那个引擎里 **0 = 不卡这道闸**，两边对 0 的读法相反。"
                 + "这里按【不卡】传（该引擎表达不了「本月一方都不许剥」）。要卡请填一个正数。";
            return 0;
        }
        return v.Value * 1e4;
    }

    /// <summary>
    /// 「采掘单元排产」口径的本月车队能力（<b>t·km</b>，<c>UnitPlanInput.FleetCapTKm</c>）。
    /// 未给 → <b>0（= 不卡，U4 的回环不启用）</b>。
    /// <para>这个量<b>没有台账来源</b>（<c>equipment_model</c> 只有载重 <c>load_t</c>），派生值恒 null ——
    /// 所以"没卡运输闸"是常态，得说出来，别让人以为算过了。</para>
    /// </summary>
    public static double FleetCapTKmForUnitEngine(MonthlyTargetRow? row, out string? note)
    {
        note = null;
        double? v = row?.FleetCapWanTKm;
        if (v == null || v.Value <= 1e-9)
        {
            note = "· 本月【车队能力】表里没给 → 采掘单元排产**不卡运输闸**（U4 的回环不启用）。"
                 + "这个量没有台账来源（equipment_model 只有载重，推不出 t·km），要卡请在逐月配置表里手填。";
            return 0;
        }
        return v.Value * 1e4;
    }

    /// <summary>本表跨的期次键清单（界面/日志用）。</summary>
    public IReadOnlyList<string> PeriodKeys => Rows.Select(r => r.PeriodKey).ToList();

    /// <summary>
    /// 本表是不是照着<b>当前</b>基础约束派生的。
    ///
    /// <para><b>为什么要有这一问</b>：基础约束改了、表没重派，两边的数就开始各说各的 ——
    /// 而表面上一点看不出来（表里全是数，对账也照跑，只是对的是<b>旧年目标</b>）。
    /// 这时候<b>不许自动重派</b>（那会不打招呼冲掉人的判断），只能把差异摆出来让人自己点。</para>
    /// </summary>
    public bool MatchesBasis(ShortTermBase basis, out string why)
    {
        why = "";
        if (basis == null) return true;
        var d = new List<string>();
        if (basis.PlanYear != PlanYear) d.Add($"计划年度 {PlanYear}→{basis.PlanYear}");
        if (Math.Clamp(basis.StartMonth, 1, 12) != StartMonth) d.Add($"起始月 {StartMonth}→{basis.StartMonth}");
        if (Math.Clamp(basis.MonthCount, 1, 24) != MonthCount) d.Add($"月数 {MonthCount}→{basis.MonthCount}");
        if (Math.Abs(basis.AnnualCoalTargetWanT - AnnualCoalTargetWanT) > 1e-6)
            d.Add($"年采出 {AnnualCoalTargetWanT:0.#}→{basis.AnnualCoalTargetWanT:0.#} 万t");
        if (Math.Abs(basis.AnnualStripTargetWanM3 - AnnualStripTargetWanM3) > 1e-6)
            d.Add($"年剥离 {AnnualStripTargetWanM3:0}→{basis.AnnualStripTargetWanM3:0} 万m³");
        if (d.Count == 0) return true;
        why = "◆ 基础约束已改、逐月表还是上次派生的（" + string.Join("；", d)
            + "）—— 点「按当前参数派生」刷新（人工覆盖不会被冲掉）。**没有自动重派**：那会不打招呼地改掉你填的数。";
        return false;
    }

    // ══════════════════════════════════════════════════════════════
    //  对账（如实报、不缩放）
    // ══════════════════════════════════════════════════════════════

    /// <summary>逐月之和 vs 年目标。<b>只报不改</b>。</summary>
    public MonthlyTargetReconcile Reconcile()
    {
        var r = new MonthlyTargetReconcile
        {
            SumCoalWanT = Math.Round(Rows.Sum(z => z.CoalWanT), 3),
            SumStripWanM3 = Math.Round(Rows.Sum(z => z.StripWanM3), 3),
            AnnualCoalTargetWanT = AnnualCoalTargetWanT,
            AnnualStripTargetWanM3 = AnnualStripTargetWanM3,
            TolerancePct = CompletionTolerancePct,
            YtdCoalWanT = YtdCoalWanT,
            YtdStripWanM3 = YtdStripWanM3,
            MonthCount = Rows.Count,
            ManualRowCount = ManualRowCount,
        };

        if (Rows.Count == 0) { r.Issues.Add("◆ 表是空的 —— 先「按当前参数派生」。"); return r; }

        foreach (var z in Rows)
        {
            if (z.CoalWanT < 0) r.Issues.Add($"◆ {z.PeriodKey} 采出是负数（{z.CoalWanT:0.##}）。");
            if (z.StripWanM3 < 0) r.Issues.Add($"◆ {z.PeriodKey} 剥离是负数（{z.StripWanM3:0.##}）。");
            if (z.Workdays < 0) r.Issues.Add($"◆ {z.PeriodKey} 作业日是负数（{z.Workdays:0.##}）。");
            if (z.CoalWanT <= 1e-9 && z.StripWanM3 > 1e-9)
                r.Issues.Add($"· {z.PeriodKey} 只剥不采（采出 0、剥离 {z.StripWanM3:0}）—— 剥采比这一格按 0 显示，不是真的 0。");
            if (z.StripCapWanM3.HasValue && z.StripCapWanM3.Value <= 1e-9)
                // 0 在下游是硬约束「该月不能剥」，人手填 0 多半是想写「不限」。
                r.Issues.Add($"◆ {z.PeriodKey} 剥离能力填了 {z.StripCapWanM3.Value:0.##} —— "
                           + "下游口径里 **0 = 该月不能剥**（不是「不限」）。要「不限」请把这一格清空。");
            if (z.StripCapWanM3.HasValue && z.StripWanM3 > z.StripCapWanM3.Value + 1e-6)
                r.Issues.Add($"◆ {z.PeriodKey} 剥离量 {z.StripWanM3:0} 万m³ 超过本月剥离能力 {z.StripCapWanM3.Value:0} 万m³。");
        }

        var dupLabels = Rows.GroupBy(z => z.Label, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupLabels.Count > 0)
            r.Issues.Add($"· 期次标签重复 {dupLabels.Count} 个（{string.Join("、", dupLabels.Take(4))}）—— "
                       + "排产器的 Label 不带跨年进位。跨模块对行请用 PeriodKey，别用 Label。");

        return r;
    }

    // ══════════════════════════════════════════════════════════════
    //  落盘（CSV）
    // ══════════════════════════════════════════════════════════════
    //
    //  【为什么单开一份，不跟着 ShortTermBase 走】——读代码判的，理由三条：
    //   ① `ShortTermBase` **今天根本没有落盘**：全仓库只有 `ShortTermSchemeStore.Base`
    //      这一个 static 字段，会话级、进程一关就没。"跟着它走"等于要先给它造一整套序列化，
    //      而那要动 ShortTermPlan.cs（现场共用文件）。
    //   ② 三个消费方里有两个（量驱动采剥接续、采掘单元排产）**根本不认识 ShortTermBase**，
    //      它们要的是"本月那一行"。一份独立的 CSV 是这三方唯一都够得着的形式。
    //   ③ 覆盖标记必须显式落盘。挂在 Base 上就要连 Base 一起序列化，改一个字段两边都要改；
    //      独立一份，列就是列。
    //  存放位置沿用现场既有约定：桌面 / 短期计划配置 /（与「采掘单元台账」「排土条带_位置清单」同级）。

    private const string ColHeader =
        "期次,标签,年,月,序,采出万t,剥离万m³,剥采比,作业日,剥离能力万m³,车队能力万tkm,内排,检修,来源,覆盖列,"
        + "派生采出,派生剥离,派生作业日,派生剥离能力,派生车队能力,派生内排,派生检修,备注";

    /// <summary>写成 CSV（UTF-8）。<b>覆盖标记与派生值一并写</b>，读回来才能原样重置。</summary>
    public string ToCsv(string? title = null)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"# 逐月配置表 v{FormatVersion}{(string.IsNullOrWhiteSpace(title) ? "" : " · " + title)}"
                    + $" · 导出 {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine("# 一行 = 一个计划月。三个消费方（月度计划编制 / 量驱动采剥接续 / 采掘单元排产）从同一行取数。");
        sb.AppendLine("# 【剥采比】是派生列（剥离 ÷ 采出），读回时按量重算，写在这里只为看得懂。");
        sb.AppendLine("# 【剥离能力】【车队能力】留空 = 未给（不限）。**空不等于 0** —— 下游口径里 0 是「该月不能剥」。");
        sb.AppendLine("# 【覆盖列】列出这一行哪几列是人填的；重新派生时它们不会被冲掉。要冲掉请用「重置为派生值」。");
        sb.AppendLine($"# 骨架,{PlanYear},起始月,{StartMonth},月数,{MonthCount}");
        sb.AppendLine($"# 年目标,采出万t,{AnnualCoalTargetWanT.ToString("0.###", ci)},剥离万m³,{AnnualStripTargetWanM3.ToString("0.###", ci)},"
                    + $"完成率容差%,{CompletionTolerancePct.ToString("0.###", ci)},煤视密度,{CoalDensity.ToString("0.###", ci)}");
        sb.AppendLine($"# 年初已完成,采出万t,{YtdCoalWanT.ToString("0.###", ci)},剥离万m³,{YtdStripWanM3.ToString("0.###", ci)}"
                    + " （未计入逐月合计口径）");
        sb.AppendLine(ColHeader);

        foreach (var r in Rows)
        {
            sb.Append(ci, $"{Esc(r.PeriodKey)},{Esc(r.Label)},{r.Year},{r.Month},{r.Index},");
            sb.Append(ci, $"{r.CoalWanT.ToString("0.####", ci)},{r.StripWanM3.ToString("0.####", ci)},{r.Ratio.ToString("0.##", ci)},");
            sb.Append(ci, $"{r.Workdays.ToString("0.###", ci)},{N(r.StripCapWanM3)},{N(r.FleetCapWanTKm)},");
            sb.Append(ci, $"{(r.InternalDumpEnabled ? 1 : 0)},{(r.IsMaintenance ? 1 : 0)},");
            sb.Append(ci, $"{(r.IsManual ? "人工覆盖" : "引擎派生")},{Esc(FlagsToText(r.Overridden))},");
            sb.Append(ci, $"{r.DerivedOf(MonthlyTargetField.Coal).ToString("0.####", ci)},"
                        + $"{r.DerivedOf(MonthlyTargetField.Strip).ToString("0.####", ci)},"
                        + $"{r.DerivedOf(MonthlyTargetField.Workdays).ToString("0.###", ci)},");
            sb.Append(ci, $"{N(r.DerivedNOf(MonthlyTargetField.StripCap))},{N(r.DerivedNOf(MonthlyTargetField.FleetCap))},");
            sb.Append(ci, $"{(r.DerivedBOf(MonthlyTargetField.InternalDump) ? 1 : 0)},{(r.DerivedBOf(MonthlyTargetField.Maintenance) ? 1 : 0)},");
            sb.AppendLine(Esc(r.Note));
        }
        return sb.ToString();
    }

    /// <summary>
    /// 读回 CSV。<b>读不懂的行不静默跳过</b>，逐条记进 <paramref name="issues"/>。
    /// 剥采比列按量重算，与文件里写的不一致时报出来（那说明文件被手改过、两个数打架）。
    /// </summary>
    public static bool TryRead(string text, out MonthlyTargetTable table, out List<string> issues)
    {
        table = new MonthlyTargetTable();
        issues = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) { issues.Add("◆ 文件是空的。"); return false; }

        var ci = CultureInfo.InvariantCulture;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // 头注释里的骨架/年目标（对账口径要跟着走，不然读回来的表对不出账）
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (!l.StartsWith("#", StringComparison.Ordinal)) continue;
            var c = SplitCsv(l);
            if (c.Length >= 6 && c[0].Contains("骨架"))
            {
                if (int.TryParse(c[1], NumberStyles.Integer, ci, out int y)) table.PlanYear = y;
                if (int.TryParse(c[3], NumberStyles.Integer, ci, out int sm)) table.StartMonth = Math.Clamp(sm, 1, 12);
                if (int.TryParse(c[5], NumberStyles.Integer, ci, out int mc)) table.MonthCount = Math.Clamp(mc, 1, 24);
            }
            else if (c.Length >= 5 && c[0].Contains("年目标"))
            {
                if (double.TryParse(c[2], NumberStyles.Float, ci, out double a)) table.AnnualCoalTargetWanT = a;
                if (double.TryParse(c[4], NumberStyles.Float, ci, out double b)) table.AnnualStripTargetWanM3 = b;
                if (c.Length >= 7 && double.TryParse(c[6], NumberStyles.Float, ci, out double t)) table.CompletionTolerancePct = t;
                if (c.Length >= 9 && double.TryParse(c[8], NumberStyles.Float, ci, out double d) && d > 1e-6) table.CoalDensity = d;
            }
            else if (c.Length >= 5 && c[0].Contains("年初已完成"))
            {
                if (double.TryParse(c[2], NumberStyles.Float, ci, out double a)) table.YtdCoalWanT = a;
                if (double.TryParse(c[4], NumberStyles.Float, ci, out double b)) table.YtdStripWanM3 = b;
            }
        }

        int hi = Array.FindIndex(lines, l => !l.TrimStart().StartsWith("#", StringComparison.Ordinal) && l.Contains("期次") && l.Contains("采出"));
        if (hi < 0) { issues.Add("◆ 找不到表头行（要含「期次」和「采出」）—— 这多半不是逐月配置表。"); return false; }

        var head = SplitCsv(lines[hi]);
        int Col(string name) => Array.FindIndex(head, h => string.Equals(h.Trim(), name, StringComparison.Ordinal));
        int cKey = Col("期次"), cLabel = Col("标签"), cYear = Col("年"), cMonth = Col("月"), cIdx = Col("序");
        int cCoal = Col("采出万t"), cStrip = Col("剥离万m³"), cWd = Col("作业日");
        int cCap = Col("剥离能力万m³"), cFleet = Col("车队能力万tkm");
        int cInner = Col("内排"), cMaint = Col("检修"), cOver = Col("覆盖列"), cNote = Col("备注");
        int cRatio = Col("剥采比");
        int cdCoal = Col("派生采出"), cdStrip = Col("派生剥离"), cdWd = Col("派生作业日");
        int cdCap = Col("派生剥离能力"), cdFleet = Col("派生车队能力"), cdInner = Col("派生内排"), cdMaint = Col("派生检修");

        if (cKey < 0 || cCoal < 0 || cStrip < 0) { issues.Add("◆ 表头缺「期次」「采出万t」「剥离万m³」中的某一列。"); return false; }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = hi + 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
            var c = SplitCsv(line);
            if (c.Length <= cCoal) { issues.Add($"◆ 第 {i + 1} 行列数不够（{c.Length} 列），跳过。"); continue; }

            string key = Get(c, cKey).Trim();
            if (key.Length == 0) { issues.Add($"◆ 第 {i + 1} 行没有期次，跳过。"); continue; }
            if (!seen.Add(key)) { issues.Add($"◆ 第 {i + 1} 行期次「{key}」重复，跳过后一条。"); continue; }

            var row = new MonthlyTargetRow { Label = Get(c, cLabel).Trim() };
            int year = 0, month = 0;
            if (cYear >= 0) int.TryParse(Get(c, cYear), NumberStyles.Integer, ci, out year);
            if (cMonth >= 0) int.TryParse(Get(c, cMonth), NumberStyles.Integer, ci, out month);
            if (year <= 0 || month is < 1 or > 12)
            {
                var parts = key.Split('-');
                if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, ci, out int y2)
                                      && int.TryParse(parts[1], NumberStyles.Integer, ci, out int m2))
                { year = y2; month = m2; }
                else { issues.Add($"◆ 第 {i + 1} 行的期次「{key}」不是 yyyy-MM，也没有可用的年/月列，跳过。"); continue; }
            }
            row.Year = year; row.Month = month;
            if (row.Label.Length == 0) row.Label = $"{year}-{month:00}";
            row.Index = cIdx >= 0 && int.TryParse(Get(c, cIdx), NumberStyles.Integer, ci, out int ix) ? ix : table.Rows.Count;

            // 先落派生值（不打标），再按「覆盖列」落人工值（打标）—— 顺序反了标记就全乱。
            row.SetDerived(MonthlyTargetField.Coal, D(c, cdCoal, D(c, cCoal, 0)));
            row.SetDerived(MonthlyTargetField.Strip, D(c, cdStrip, D(c, cStrip, 0)));
            row.SetDerived(MonthlyTargetField.Workdays, D(c, cdWd, D(c, cWd, 0)));
            row.SetDerivedN(MonthlyTargetField.StripCap, ND(c, cdCap >= 0 ? cdCap : cCap));
            row.SetDerivedN(MonthlyTargetField.FleetCap, ND(c, cdFleet >= 0 ? cdFleet : cFleet));
            row.SetDerivedB(MonthlyTargetField.InternalDump, B(c, cdInner >= 0 ? cdInner : cInner, true));
            row.SetDerivedB(MonthlyTargetField.Maintenance, B(c, cdMaint >= 0 ? cdMaint : cMaint, false));

            // ⚠ 走 LoadOverride*（**总是打标**），不走公开 setter（那个靠"值变了没有"打标）。
            //   人工值恰好 == 派生值的行在公开 setter 下会因"值没变"而不打标，覆盖就此丢失。
            var over = TextToFlags(Get(c, cOver));
            if ((over & MonthlyTargetField.Coal) != 0) row.LoadOverride(MonthlyTargetField.Coal, D(c, cCoal, 0));
            if ((over & MonthlyTargetField.Strip) != 0) row.LoadOverride(MonthlyTargetField.Strip, D(c, cStrip, 0));
            if ((over & MonthlyTargetField.Workdays) != 0) row.LoadOverride(MonthlyTargetField.Workdays, D(c, cWd, 0));
            if ((over & MonthlyTargetField.StripCap) != 0) row.LoadOverrideN(MonthlyTargetField.StripCap, ND(c, cCap));
            if ((over & MonthlyTargetField.FleetCap) != 0) row.LoadOverrideN(MonthlyTargetField.FleetCap, ND(c, cFleet));
            if ((over & MonthlyTargetField.InternalDump) != 0) row.LoadOverrideB(MonthlyTargetField.InternalDump, B(c, cInner, true));
            if ((over & MonthlyTargetField.Maintenance) != 0) row.LoadOverrideB(MonthlyTargetField.Maintenance, B(c, cMaint, false));
            if (cNote >= 0) row.Note = Get(c, cNote);

            // 覆盖标记落完之后，生效值必须与文件里那一列对得上。对不上说明标记与值打架
            // （文件被手改过），**如实报**——这正是"覆盖被冲掉"最容易溜过去的地方。
            double fileCoal = D(c, cCoal, 0);
            if (Math.Abs(row.CoalWanT - fileCoal) > 1e-6)
                issues.Add($"◆ {key} 的采出：表里写 {fileCoal:0.###}，按覆盖标记还原出 {row.CoalWanT:0.###} —— "
                         + "文件被手改过？已按标记还原，没按写的那个数。");
            double fileStrip = D(c, cStrip, 0);
            if (Math.Abs(row.StripWanM3 - fileStrip) > 1e-6)
                issues.Add($"◆ {key} 的剥离：表里写 {fileStrip:0.###}，按覆盖标记还原出 {row.StripWanM3:0.###} —— "
                         + "文件被手改过？已按标记还原。");

            // 剥采比是派生列，文件里那一格只是给人看的。不一致说明文件被改过。
            if (cRatio >= 0 && double.TryParse(Get(c, cRatio), NumberStyles.Float, ci, out double fileRatio)
                && Math.Abs(fileRatio - row.Ratio) > 0.011)
                issues.Add($"· {key} 文件里的剥采比 {fileRatio:0.00} 与按量重算的 {row.Ratio:0.00} 不一致 —— "
                         + "剥采比是派生列，已按量重算（一个量只能有一个来源）。");

            table.Rows.Add(row);
        }

        if (table.Rows.Count == 0) { issues.Add("◆ 一行都没读出来。"); return false; }
        table.DerivedAt = null;   // 读回来的表没有"这次派生的时刻"，别冒充
        table.Notes.Add($"· 从文件读回 {table.Rows.Count} 行（其中 {table.ManualRowCount} 行带人工覆盖）。"
                      + "派生值是**存盘那一刻**的，现场参数改过之后要点「按当前参数派生」刷新（覆盖不会被冲掉）。");
        return true;

        static string Get(string[] c, int i) => i >= 0 && i < c.Length ? c[i] : "";
        static double D(string[] c, int i, double dflt)
            => double.TryParse(Get(c, i), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : dflt;
        static double? ND(string[] c, int i)
        {
            string s = Get(c, i).Trim();
            if (s.Length == 0) return null;   // 空 = 未给，不是 0
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
        }
        static bool B(string[] c, int i, bool dflt)
        {
            string s = Get(c, i).Trim();
            if (s.Length == 0) return dflt;
            return s is "1" or "是" or "true" or "True" or "TRUE" or "Y" or "y";
        }
    }

    // ── CSV 小工具 ─────────────────────────────────────────────────────────

    private static string N(double? v) => v.HasValue ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";

    private static string Esc(string? v)
    {
        string s = v ?? "";
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>覆盖标记 → 稳定的英文串（落盘用；显示用 <see cref="MonthlyTargetRow.FieldText"/>）。</summary>
    internal static string FlagsToText(MonthlyTargetField f)
    {
        if (f == MonthlyTargetField.None) return "";
        var parts = new List<string>();
        if ((f & MonthlyTargetField.Coal) != 0) parts.Add("Coal");
        if ((f & MonthlyTargetField.Strip) != 0) parts.Add("Strip");
        if ((f & MonthlyTargetField.Workdays) != 0) parts.Add("Workdays");
        if ((f & MonthlyTargetField.StripCap) != 0) parts.Add("StripCap");
        if ((f & MonthlyTargetField.FleetCap) != 0) parts.Add("FleetCap");
        if ((f & MonthlyTargetField.InternalDump) != 0) parts.Add("InternalDump");
        if ((f & MonthlyTargetField.Maintenance) != 0) parts.Add("Maintenance");
        return string.Join("|", parts);
    }

    internal static MonthlyTargetField TextToFlags(string? s)
    {
        var f = MonthlyTargetField.None;
        if (string.IsNullOrWhiteSpace(s)) return f;
        foreach (var t in s.Split(new[] { '|', ';', '+' }, StringSplitOptions.RemoveEmptyEntries))
            f |= t.Trim() switch
            {
                "Coal" => MonthlyTargetField.Coal,
                "Strip" => MonthlyTargetField.Strip,
                "Workdays" => MonthlyTargetField.Workdays,
                "StripCap" => MonthlyTargetField.StripCap,
                "FleetCap" => MonthlyTargetField.FleetCap,
                "InternalDump" => MonthlyTargetField.InternalDump,
                "Maintenance" => MonthlyTargetField.Maintenance,
                _ => MonthlyTargetField.None,
            };
        return f;
    }

    internal static string[] SplitCsv(string line)
    {
        var res = new List<string>();
        var cur = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (q)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else q = false;
                }
                else cur.Append(ch);
            }
            else if (ch == '"') q = true;
            else if (ch == ',') { res.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        res.Add(cur.ToString());
        return res.ToArray();
    }
}

/// <summary>
/// 逐月配置表的**会话级唯一实例 + 落盘**。
///
/// <para>三个消费方（月度计划编制 / 量驱动采剥接续 / 采掘单元排产）一律从
/// <see cref="Current"/> 取本月那一行 —— <b>这是"同一张表"这条要求的落点</b>。
/// 谁要是自己再读一份文件或自己再摊一次，两个来源立刻开始漂。</para>
///
/// <para>与 <see cref="ShortTermSchemeStore"/> 平级：那个管方案，这个管配置。
/// 之所以不并进去，见 <see cref="MonthlyTargetTable"/> 落盘段的三条理由。</para>
/// </summary>
public static class MonthlyTargetStore
{
    public const string FileName = "逐月配置表.csv";

    private static MonthlyTargetTable? _current;

    /// <summary>
    /// 当前逐月配置表。<b>首次访问按 <see cref="ShortTermSchemeStore.Base"/> 自动派生一份</b>
    /// —— 空表会让下游拿到 0 个月，而"0 个月"和"这一年不生产"在数组上长得一样。
    /// </summary>
    public static MonthlyTargetTable Current
        => _current ??= MonthlyTargetTable.BuildDefaults(ShortTermSchemeStore.Base);

    /// <summary>换一张表（读盘后调）。传 null = 下次访问重新派生。</summary>
    public static void Set(MonthlyTargetTable? t) => _current = t;

    /// <summary>按当前基础约束重新派生（人工覆盖保住）。</summary>
    public static MonthlyTargetTable Rebuild()
    {
        var t = Current;
        t.Rebuild(ShortTermSchemeStore.Base);
        return t;
    }

    /// <summary>
    /// 落盘根目录：<b>软件目录</b> <c>Data\短期计划配置</c>（2026-08-18 现场令「中间文件都放软件目录」）。
    /// <para>桌面上的老目录首次运行自动迁过来（复制、不覆盖同名、老目录留指引），
    /// 与采掘单元台账、采矿模型中间文件走<b>同一套</b>解析（<see cref="AppDataRoot"/>）。</para>
    /// </summary>
    public static string DefaultRoot => AppDataRoot.For("短期计划配置");

    public static string DefaultPath => Path.Combine(DefaultRoot, FileName);

    /// <summary>原子写：先写 .tmp 再替换，中途失败原文件一个字节都没动。</summary>
    public static bool Save(MonthlyTargetTable t, out string path, out string error, string? file = null)
    {
        path = string.IsNullOrWhiteSpace(file) ? DefaultPath : file!;
        error = "";
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, t.ToCsv($"{t.PlanYear}年"), new UTF8Encoding(true));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return true;
        }
        catch (Exception ex) { error = "保存失败：" + ex.Message; return false; }
    }

    /// <summary>读回。读不出来如实报，<b>不返回一张空表冒充成功</b>。</summary>
    public static bool TryLoad(out MonthlyTargetTable table, out List<string> issues, string? file = null)
    {
        table = new MonthlyTargetTable();
        issues = new List<string>();
        string path = string.IsNullOrWhiteSpace(file) ? DefaultPath : file!;
        if (!File.Exists(path)) { issues.Add($"还没有逐月配置表（{path}）—— 先「按当前参数派生」再「保存」。"); return false; }

        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex) { issues.Add("◆ 读文件失败：" + ex.Message); return false; }

        string text;
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        try { text = StripBom(strict.GetString(bytes)); }
        catch (DecoderFallbackException)
        {
            try
            {
                text = StripBom(Encoding.GetEncoding("GB18030").GetString(bytes));
                issues.Add("· 这份文件不是 UTF-8，已按 GB18030 读回（多半用 Excel 另存过）。存回去时写 UTF-8。");
            }
            catch (Exception ex)
            {
                issues.Add($"◆ 这份文件既不是 UTF-8 也读不成 GB18030（{ex.Message}）—— 编码不认识。");
                return false;
            }
        }
        bool ok = MonthlyTargetTable.TryRead(text, out table, out var more);
        issues.AddRange(more);      // 成功也要把提示带出去（编码降级、剥采比对不上…）
        return ok;

        static string StripBom(string s) => s.Length > 0 && s[0] == '﻿' ? s.Substring(1) : s;
    }
}
