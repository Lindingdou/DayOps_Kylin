// 忠实移植自原 PitMine3D Modules/TaskLib/Engine/SpaceTimeValidator.cs（逐行对应；仅命名空间/依赖适配）
using System.IO;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.TaskLib.Domain;

namespace PitMine3D.Kylin.TaskLib.Engine;

// ─────────────────────────────────────────────────────────────────────────────
//  采排时空约束校核 —— 露天矿特有的「什么时候能排、排在哪一层、准不准往那儿排」。
//
//  装箱引擎管的是"量"对不对得上（TaskExploder 的采排守恒 + 库容/通过能力）；
//  真正卡住现场的却常常是"时"和"空"，这两类与量正交，故单列一类：
//    · 内排土场要等采空区形成才能启用——启用前只能外排，运距差一倍，成本差一截。
//    · 排土场自下而上分层排弃，底部承载层没形成就上排会整体滑移。
//    · 硬岩/夹矸不爆破铲不动，当日无穿孔爆破接续 = 明天电铲趴窝。
//    · 表土是复垦资源，必须单独堆存，混排即永久损失。
//    · 卸点有开放时窗，班次落在窗外 = 卡车到了也卸不掉。
//
//  设计成 (cfg, res) 的纯校核：重排引擎、计划侧、方案比选都能拿自己的盘子单独调，
//  不依赖装箱结果（任务表只在"爆破前置"和"卸点时窗"两条上用到，缺了也只是这两条不判）。
//  去向登记簿为空时各条自然空转，不会刷屏——该提示由 TaskExploder 统一给一条。
// ─────────────────────────────────────────────────────────────────────────────

public static class SpaceTimeValidator
{
    /// <summary>校核并把结果并入既有 ExploderResult（TaskExploder 装箱后调用）。</summary>
    public static void Validate(ExploderConfig cfg, ExploderResult res)
        => Run(cfg, res.Tasks, res.Violations);

    /// <summary>只有任务表（或压根没有任务表）时的入口：返回独立的校核清单，供重排引擎/计划侧单独调用。</summary>
    public static List<PlanViolation> ValidateTasks(ExploderConfig cfg, IEnumerable<ProductionTask>? tasks = null)
    {
        var list = new List<PlanViolation>();
        Run(cfg, (tasks ?? Enumerable.Empty<ProductionTask>()).ToList(), list);
        return list;
    }

    private static void Run(ExploderConfig cfg, List<ProductionTask> tasks, List<PlanViolation> outp)
    {
        var bal = cfg.Balance();
        var routed = bal.Flows.Where(f => !string.IsNullOrWhiteSpace(f.SinkId) || !string.IsNullOrWhiteSpace(f.SinkName)).ToList();

        // 本期真正"在用"的去向：采装面的流向 + 排土面认领的场（排土面没有流，但它占的是同一个场）
        var used = new HashSet<string>(routed.Select(f => f.SinkId).Where(s => !string.IsNullOrWhiteSpace(s)), StringComparer.OrdinalIgnoreCase);
        foreach (var f in cfg.Faces.Where(f => f.Process == ProcessType.Dump && !string.IsNullOrWhiteSpace(f.DestinationId)))
            used.Add(f.DestinationId);

        CheckInternalDumpOpening(cfg, routed, used, outp);
        CheckDumpBenchOrder(cfg, bal, used, outp);
        CheckBlastingPrecedence(cfg, tasks, outp);
        CheckTopsoilYard(cfg, routed, outp);
        CheckOpenWindow(cfg, tasks, outp);
        CheckInternalHaulSanity(cfg, bal, outp);
    }

    /// <summary>
    /// ① 内排启用时机：内排是往采空区里回填，采空区没形成就无处可排。
    /// 期次按字符串序比（2026-06 &lt; 2026-07 &lt; 2027-01），与 Period 标签写法一致。
    /// </summary>
    private static void CheckInternalDumpOpening(ExploderConfig cfg, List<MaterialFlow> routed, HashSet<string> used, List<PlanViolation> outp)
    {
        foreach (var s in cfg.Sinks.All.Where(x => x.Kind == SinkKind.InternalDump && !string.IsNullOrWhiteSpace(x.OpenFromPeriod)))
        {
            // 两边都得是 "2026…" 这样的期次标签才比得了；IdPrefix 兜底出来的 "D0617" 不参与比较，免得误判。
            if (!IsPeriodLabel(cfg.Period) || !IsPeriodLabel(s.OpenFromPeriod)) continue;
            if (string.CompareOrdinal(cfg.Period, s.OpenFromPeriod) >= 0) continue;

            if (used.Contains(s.Id))
                Flag(outp, ViolationSeverity.Error, ViolationCodes.SinkClosed,
                    $"内排土场「{s.Name}」自 {s.OpenFromPeriod} 起启用（采空区尚未形成），本期（{cfg.Period}）只能外排："
                    + $"当日 {routed.Where(f => Same(f.SinkId, s.Id)).Sum(f => f.InSituM3):0} m³ 实方须改投外排土场，运距与运输功按外排重算");
            else
                Flag(outp, ViolationSeverity.Info, ViolationCodes.SinkClosed,
                    $"内排土场「{s.Name}」自 {s.OpenFromPeriod} 起启用，本期（{cfg.Period}）只能外排——内排运距通常仅为外排的一半，可据此安排采空区形成节奏");
        }
    }

    /// <summary>② 排土台阶承载顺序：排土场自下而上分层排弃，底部承载层未形成即上排会整体滑移。</summary>
    private static void CheckDumpBenchOrder(ExploderConfig cfg, PeriodBalance bal, HashSet<string> used, List<PlanViolation> outp)
    {
        foreach (var s in cfg.Sinks.All.Where(x => x.IsDumping && used.Contains(x.Id)))
        {
            if (s.ActiveBenchLevel < 1)
            {
                Flag(outp, ViolationSeverity.Error, ViolationCodes.ProcessChain,
                    $"{s.Name} 排土台阶层非法（当前第 {s.ActiveBenchLevel} 层）：排土须自下而上分层、最低为第 1 层，"
                    + "底部承载层未形成即上排会整体滑移；请在排土场台账核定当前可排台阶层");
                continue;
            }

            double dump = bal.DumpM3At(s.Id);
            if (dump <= 1) continue;
            double adv = s.AdvanceMetersFor(dump);
            Flag(outp, ViolationSeverity.Info, ViolationCodes.ProcessChain,
                $"{s.Name} 当前在第 {s.ActiveBenchLevel} 层排弃（台阶高 {s.BenchHeightM:0.#} m）：当日入方 {dump / 1e4:0.###} 万m³占容"
                + (adv > 1e-6 ? $" → 工作线推进 {adv:0.#} m" : "（未录排土工作线长，推进距离无法反算）"));
        }
    }

    /// <summary>
    /// ③ 需爆破物料的前置工序：硬岩/夹矸不爆破铲不动。按物料的 NeedsBlasting 逐面判，
    /// 比"有采装就得有穿孔"的全局粗判精确得多——表土/风化岩/煤面不该被这条骚扰。
    /// </summary>
    private static void CheckBlastingPrecedence(ExploderConfig cfg, List<ProductionTask> tasks, List<PlanViolation> outp)
    {
        var prepared = tasks.Where(t => t.Process is ProcessType.Drill or ProcessType.Blast).Select(t => t.WorkZone)
            .Concat(cfg.Drills.Select(d => d.Zone))
            .Where(z => !string.IsNullOrWhiteSpace(z)).Distinct().ToList();

        foreach (var f in cfg.Faces.Where(f => f.Process == ProcessType.Load && f.DayTargetM3 > 1))
        {
            var shares = f.ResolvedMix.Normalized().Shares.Where(s => MaterialCatalog.Resolve(s.MaterialCode).NeedsBlasting).ToList();
            double frac = shares.Sum(s => s.Fraction);
            if (frac <= 1e-6) continue;                                  // 表土/风化岩/煤：直接铲装，无需前置
            if (prepared.Any(z => ZoneMatch(z, f.Zone))) continue;

            Flag(outp, ViolationSeverity.Warn, ViolationCodes.ProcessChain,
                $"{f.Zone} 物料含{string.Join("、", shares.Select(s => MaterialCatalog.Resolve(s.MaterialCode).Name).Distinct())}（需爆破）占 {frac * 100:0.#}%"
                + $"（{f.DayTargetM3 * frac:0} m³ 实方），当日无穿孔/爆破任务接续本区：爆破备采量将见底，请排钻机或核对待爆区归属");
        }
    }

    /// <summary>
    /// ④ 表土专场：表土是复垦资源，剥离后须单独堆存、复垦时回取；混进岩石排土场等于永久损失。
    /// 与「物料不兼容」是同一件事的两个视角，已被那条覆盖的不重复报（避免刷屏）。
    /// </summary>
    private static void CheckTopsoilYard(ExploderConfig cfg, List<MaterialFlow> routed, List<PlanViolation> outp)
    {
        foreach (var f in routed.Where(x => x.Spec.Kind == MaterialKind.Topsoil && x.SinkKind != SinkKind.TopsoilYard))
        {
            // 解析方式须与「物料不兼容」一致（Id 找不到就按名字找），否则同一条会被两边各报一次
            var sink = FindSink(cfg, f.SinkId, f.SinkName);
            if (cfg.EnforceMassBalance && sink != null && !sink.Accepts(f.Spec)) continue;   // 已按「物料不兼容」报过

            Flag(outp, ViolationSeverity.Error, ViolationCodes.MaterialRejected,
                $"{f.SourceName} 的表土运往{(string.IsNullOrWhiteSpace(f.SinkName) ? f.SinkId : f.SinkName)}（{f.SinkKind.Label()}）："
                + $"表土为复垦资源，须单独堆存于表土堆场以备回填复垦，混入{f.SinkKind.Label()}后无法回取"
                + $"（当日 {f.InSituM3:0} m³ 实方）；请改投表土堆场并单独计量");
        }
    }

    /// <summary>
    /// ⑤ 卸点开放时窗：班次作业时段落在受排点开放时窗之外，卡车到了也卸不掉，只能压车等卸。
    /// **逐去向判**：混采任务同时往破碎站和排土场卸，两个点的开放时窗各不相同，
    /// 只看主去向会把次要物料那条腿的压车风险整条漏掉。
    /// </summary>
    private static void CheckOpenWindow(ExploderConfig cfg, List<ProductionTask> tasks, List<PlanViolation> outp)
    {
        foreach (var t in tasks.Where(t => t.Process is ProcessType.Load or ProcessType.Haul or ProcessType.Dump
                                           && t.EndHour > t.StartHour))
        {
            foreach (var (sink, what) in DestinationsOf(cfg, t))
            {
                double from = sink.OpenFromHour, to = sink.OpenToHour;
                if (to <= from || to - from >= 24 - 1e-6) continue;                  // 全天开放 / 未录时窗
                if (t.StartHour >= from - 0.01 && t.EndHour <= to + 0.01) continue;  // 完全落在窗内

                Flag(outp, ViolationSeverity.Warn, ViolationCodes.SinkClosed,
                    $"{t.Shift} {t.WorkZone}{what} 作业时段 {t.StartHour:0.#}–{t.EndHour:0.#} 时 超出 {sink.Name} 开放时窗 {from:0.#}–{to:0.#} 时："
                    + "窗外到点卸不掉，卡车压车等卸；请调整班次时段、改投他点或延长该点开放时窗", t.Id);
            }
        }
    }

    /// <summary>
    /// 一条任务涉及的全部去向（去重）。有分项就逐分项取（并带上物料名以便定位是哪条腿），
    /// 没有分项才回落单去向五字段。
    /// </summary>
    private static IEnumerable<(SinkNode Sink, string What)> DestinationsOf(ExploderConfig cfg, ProductionTask t)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (t.HasSplits)
        {
            foreach (var d in t.Splits)
            {
                if (!d.HasDestination) continue;
                var s = FindSink(cfg, d.DestinationId, d.DestinationName);
                if (s == null || !seen.Add(s.Id)) continue;
                yield return (s, t.Splits.Count > 1 ? $" 的{d.Spec.Name}" : "");
            }
            yield break;
        }

        if (!t.HasDestination) yield break;
        var main = FindSink(cfg, t.DestinationId, t.DestinationName);
        if (main != null && seen.Add(main.Id)) yield return (main, "");
    }

    /// <summary>
    /// ⑥ 内排运距合理性：内排在采空区内、多为下坡近运，比外排还远多半是路网解算或手填运距填反了。
    /// 内排降本的全部来源就是这段运距差，填错会让方案比选整体失真，故即使不违规也要提醒核对。
    /// </summary>
    private static void CheckInternalHaulSanity(ExploderConfig cfg, PeriodBalance bal, List<PlanViolation> outp)
    {
        var avg = bal.Flows.Where(f => f.EffectiveHaulKm > 1e-6 && !string.IsNullOrWhiteSpace(f.SinkId))
            .GroupBy(f => f.SinkId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Sink = cfg.Sinks.Find(g.Key), Km = g.Sum(x => x.TransportWorkTKm) / Math.Max(1e-6, g.Sum(x => x.TonnageT)) })
            .Where(a => a.Sink != null)
            .ToList();

        var ext = avg.Where(a => a.Sink!.Kind == SinkKind.ExternalDump).OrderBy(a => a.Km).FirstOrDefault();
        if (ext == null) return;
        double extKm = ext.Km;                      // 与最近的一个外排比：连最近的外排都比内排近，才算反常
        string extName = ext.Sink!.Name;

        foreach (var a in avg.Where(a => a.Sink!.Kind == SinkKind.InternalDump && a.Km > extKm + 1e-6))
            Flag(outp, ViolationSeverity.Info, ViolationCodes.HaulMissing,
                $"内排运距反常：{a.Sink!.Name} {a.Km:0.##} km > 外排 {extName} {extKm:0.##} km。"
                + "内排在采空区内、通常更短（下坡近运），请核对路网解算或面上手填的运距是否填反");
    }

    // ── 小工具 ───────────────────────────────────────────────────────────────

    private static SinkNode? FindSink(ExploderConfig cfg, string? id, string? name)
        => cfg.Sinks.Find(id) ?? cfg.Sinks.All.FirstOrDefault(x => Same(x.Name, name) || Same(x.Name, id));

    /// <summary>作业区匹配：待爆区与采装面的命名常有包含关系（"待爆区B" ⊂ "主采面·东B"），故双向包含即算命中。</summary>
    private static bool ZoneMatch(string? a, string? b)
    {
        string x = (a ?? "").Trim(), y = (b ?? "").Trim();
        if (x.Length == 0 || y.Length == 0) return false;
        return x.Equals(y, StringComparison.OrdinalIgnoreCase)
            || x.Contains(y, StringComparison.OrdinalIgnoreCase)
            || y.Contains(x, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>是不是"2026…"这种可按字符串序比大小的期次标签。</summary>
    private static bool IsPeriodLabel(string? s)
        => !string.IsNullOrWhiteSpace(s) && char.IsDigit(s.Trim()[0]);

    private static bool Same(string? a, string? b)
        => !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void Flag(List<PlanViolation> outp, ViolationSeverity sev, string code, string msg, string taskId = "")
        => outp.Add(new PlanViolation { Severity = sev, Code = code, Message = msg, TaskId = taskId });
}
