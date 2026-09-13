// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/MiningUnitLink.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.UnitLedger;            // MiningUnitLedger / MonthlyUnitLedgerStore
using PitMine3D.Kylin.TaskLib.Domain;                  // ProcessType

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  作业面 ↔ 采掘单元 的对号 —— 日计划这一侧读采掘单元台账的唯一入口。
//
//  采矿模型那边早就把基表做出来了（一行 = 一个采掘单元 = 图上一个体，带 UnitId /
//  中心坐标 / 尺寸 / 方量 / 期次 / 状态），但**日计划侧一直没人读它**：
//  TaskLib 里唯一用到 MiningUnitLedger 的地方是三维推演的设备符号定位，
//  编制链一次都没碰过。于是作业面台账上填的 UnitId 是个自由文本，填错了没人知道。
//
//  本类只做两件小事，都刻意做得很轻：
//    ① Note(unitId)  —— 这个号在基表里认不认得（界面上当场给个 ✓ / ?）；
//    ② Find(unitId)  —— 取回那一行（后续"点任务定位到图上的体"、按单元核销备采要用）。
//
//  ★ 认不出来一律**不拦**，只标记。理由：基表是跟着采矿模型重算刷新的，
//    模型重算后单元号会变（B12-P03 可能被重划成 B12-P04）；此时台账里的旧号确实
//    对不上基表，但那不代表用户填错了，而是模型动了。拦下来会逼人去改一个
//    本来就该由"重新绑单元"来解决的问题。界面标记 + 计划校核里如实列出，足够。
//
//  ★ 基表读一次缓存住：装配盘子时每个面都要问一次，几十个面各读一遍 CSV 太蠢。
//    台账目录被别的窗口改过时调 Invalidate()（「采掘单元清单」窗口保存基表后应当调）。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>作业面与采掘单元台账的对号。</summary>
public static class MiningUnitLink
{
    private static Dictionary<string, MiningUnitLedger.Row>? _cache;
    private static string _loadLabel = "";

    /// <summary>基表的来源文案（读到几行 / 为什么没读到）。</summary>
    public static string SourceLabel { get { EnsureLoaded(); return _loadLabel; } }

    /// <summary>基表里有没有可用数据（没有基表时全部标记都退化为"—"，不误报"填错了"）。</summary>
    public static bool HasBase { get { EnsureLoaded(); return _cache is { Count: > 0 }; } }

    /// <summary>丢弃缓存（采掘单元台账改过后调用）。</summary>
    public static void Invalidate() { _cache = null; _loadLabel = ""; }

    /// <summary>按单元号取基表那一行；没有基表或对不上返回 null。</summary>
    public static MiningUnitLedger.Row? Find(string? unitId)
    {
        string id = (unitId ?? "").Trim();
        if (id.Length == 0) return null;
        EnsureLoaded();
        return _cache != null && _cache.TryGetValue(id, out var r) ? r : null;
    }

    /// <summary>
    /// 界面上的对号标记：<br/>
    ///  "—" 没填单元号，或者根本没有基表（无从判起，<b>不显示成错</b>）；<br/>
    ///  "✓" 基表里有这个号；<br/>
    ///  "?" 基表里没有这个号（多半是模型重算后单元被重划，不是填错）。
    /// </summary>
    public static string Note(string? unitId)
    {
        string id = (unitId ?? "").Trim();
        if (id.Length == 0) return "—";
        EnsureLoaded();
        if (_cache is not { Count: > 0 }) return "—";
        return _cache.ContainsKey(id) ? "✓" : "?";
    }

    /// <summary>
    /// 逐面核对单元号，把问题写进 <paramref name="violations"/>，返回来源文案。
    /// 没有基表时**一条都不报**（同 <see cref="EquipmentAvailability"/> 的纪律：
    /// 宁可不查，也不整屏假告警）。
    /// </summary>

    /// <summary>
    /// 上一次装配用的是不是真数据（false = 走了兜底）。
    /// <para><b>链路体检读它，不读来源文案</b> —— 文案一改就静默抠空，
    /// 而抠空之后「兜底」会被当成「真实」，体检朝着让人放心的方向失效。</para>
    /// </summary>
    public static bool LastFromLedger { get; private set; }

    public static string Check(ExploderConfig cfg, List<PlanViolation>? violations = null)
    {
        if (cfg == null || cfg.Faces.Count == 0) return "";
        EnsureLoaded();
        if (_cache is not { Count: > 0 }) return _loadLabel;

        var loads = cfg.Faces.Where(f => f.Process == ProcessType.Load).ToList();
        int bound = 0, unknown = 0, unbound = 0;

        foreach (var f in loads)
        {
            if (!f.HasUnit) { unbound++; continue; }
            if (_cache.ContainsKey(f.UnitId.Trim())) { bound++; continue; }

            unknown++;
            violations?.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Warn, Code = ViolationCodes.UnitLink,
                Message = $"{f.Zone} 的单元号 {f.UnitId} 不在采掘单元基表里 —— "
                        + "多半是采矿模型重算后单元被重划，需要重新绑；任务落不回图上的体",
            });
        }

        if (unbound > 0)
            violations?.Add(new PlanViolation
            {
                Severity = ViolationSeverity.Info, Code = ViolationCodes.UnitLink,
                Message = $"{unbound}/{loads.Count} 个采装面没绑采掘单元 —— "
                        + "任务点不回图上的体，备采核销与期次覆盖率算不了（在「作业面台账」填「单元号」）",
            });

        // 一个面都没绑上单元号 = 这一段没起作用（任务点不回图上的体）
        LastFromLedger = bound > 0 && unbound == 0 && unknown == 0;
        return $"采掘单元：{bound} 面已绑" + (unknown > 0 ? $" · {unknown} 面号对不上" : "")
             + (unbound > 0 ? $" · {unbound} 面未绑" : "") + $"（基表 {_cache.Count} 个单元）";
    }

    private static void EnsureLoaded()
    {
        if (_cache != null) return;
        try
        {
            var store = new MonthlyUnitLedgerStore();
            if (!store.HasBase)
            {
                _cache = new Dictionary<string, MiningUnitLedger.Row>(StringComparer.OrdinalIgnoreCase);
                _loadLabel = $"采掘单元：还没有基表（{store.BasePath}）——单元号无从核对";
                return;
            }

            store.TryLoadBase(out var rows, out _);
            _cache = (rows ?? new List<MiningUnitLedger.Row>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.UnitId))
                .GroupBy(r => r.UnitId.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            _loadLabel = $"采掘单元：基表 {_cache.Count} 个单元（{store.Root}）";
        }
        catch (Exception ex)
        {
            _cache = new Dictionary<string, MiningUnitLedger.Row>(StringComparer.OrdinalIgnoreCase);
            _loadLabel = $"采掘单元：基表读取失败（{Short(ex)}）——单元号无从核对";
        }
    }

    private static string Short(Exception ex)
    {
        string m = ex.Message ?? ex.GetType().Name;
        return m.Length <= 60 ? m : m[..60] + "…";
    }
}
