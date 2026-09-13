// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/EquipmentAssignReport.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PitMine3D.Kylin.Cad.Units;      // EquipmentAssigner / RateBook / Machine / MachineKind
namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  设备指派结果 → 界面文案 + 台效来源覆盖表。
//
//  这一层<b>只读不算</b>：一个数都不重新算，全部从
//  EquipmentAssignResult / FleetResolution / EquipmentAssignInput 里取，
//  台效级别一律问 RateBook（引擎用的那一个类）。
//
//  ★ 为什么必须有「覆盖表」这张表：
//    capacity_monthly 覆盖不全（现场是 306/518 台），推土机/平路机/洒水车整类一条实测都没有。
//    只看指派结果是看不出来的 —— 这三类<b>压根不进排班</b>，
//    于是「实测 18/24 笔」这行漂亮数字说的只是被排的那 24 笔，
//    人会以为整个设备维都是实测的。覆盖表是<b>对在册全部设备逐台探一次账本</b>，
//    问的是「如果问它台效，谁来回答」，把落到缺省/解不出的那些台数摆到台面上。
//
//  ⚠ 覆盖表里的「本次排了」是<b>从结果里数出来的</b>（观察值），
//    不是照抄一份「引擎排哪几类」的规则 —— 规则抄第二份，引擎一改就漂，
//    而两边各自看上去都对。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>一类设备的台效来源覆盖。<b>每一列都是数出来的</b>，没有推断。</summary>
public sealed class RateCoverageRow
{
    public MachineKind Kind { get; internal set; }
    /// <summary>类别名。<b>认不出的枚举值原样显示英文名</b>，不并进「其他」——
    /// 并进去之后新加的类别会静默消失在一堆「其他」里。</summary>
    public string KindText { get; internal set; } = "";

    /// <summary>在册台数 / 其中可派（状态「在用」）的台数。</summary>
    public int OnRoll { get; internal set; }
    public int Dispatchable { get; internal set; }

    /// <summary>本次指派实际用上的台数（<b>从结果里数的</b>，0 = 这一类本次一台都没排）。</summary>
    public int UsedThisRun { get; internal set; }
    public string UsedText => UsedThisRun > 0 ? UsedThisRun.ToString() : "—";

    /// <summary><b>这台自己</b>有实测台效记录（capacity_monthly）的台数。</summary>
    public int OwnMeasured { get; internal set; }

    /// <summary>台效账本解析后落在【实测级】的台数（含「同型号别的台的实测中位数」）。</summary>
    public int ResolvedMeasured { get; internal set; }
    /// <summary>落在【缺省级】（型号字典 / 类别兜底）的台数 —— <b>不是实测</b>。</summary>
    public int ResolvedDefault { get; internal set; }
    /// <summary>一路退到底也解不出的台数 —— <b>不可派</b>（不是台效为 0）。</summary>
    public int Unresolved { get; internal set; }

    /// <summary>逐级明细（哪一级各几台）—— 「接了实测」和「接了但一条都没命中」靠它分开。</summary>
    public string LevelText { get; internal set; } = "";

    /// <summary>这一类要不要报。空 = 没什么要说的。</summary>
    public string Verdict { get; internal set; } = "";

    /// <summary>这一类<b>一台实测台效都没有</b>（自有的没有，同型号的也没有）。</summary>
    public bool NoMeasuredAtAll => OnRoll > 0 && OwnMeasured == 0 && ResolvedMeasured == 0;
}

/// <summary>设备指派的报表层（纯函数，脱 GUI —— 判据可以直接打在这里）。</summary>
public static class EquipmentAssignReport
{
    /// <summary>
    /// 逐类别探一次台效账本。
    /// </summary>
    /// <param name="fleet">设备维取数结果（在册清单 + 台效记录）。</param>
    /// <param name="inp">喂给引擎的<b>那一份</b>输入 —— 账本必须用同样的参数建，否则表里的级别和引擎用的不是一回事。</param>
    /// <param name="res">指派结果。null = 还没跑（此时「本次排了」全是 —）。</param>
    public static List<RateCoverageRow> Coverage(FleetResolution? fleet, EquipmentAssignInput inp,
                                                 EquipmentAssignResult? res)
    {
        var rows = new List<RateCoverageRow>();
        if (inp == null) return rows;
        var machines = fleet?.Machines ?? new List<Machine>();
        if (machines.Count == 0) return rows;

        // 与引擎同参建账本 —— 建账本的留条引擎已经报过一遍，这里丢掉不重复刷屏。
        var discard = new List<string>();
        var book = RateBook.Build(inp.Rates, inp.MinRateM3PerDay, inp.CapacityScale, discard);

        // 「这台自己有实测记录」= 台机级且 Measured 的那些 MachineId
        var ownMeasured = new HashSet<string>(
            (inp.Rates ?? new List<RateRecord>())
                .Where(r => r != null && r.Measured && !string.IsNullOrEmpty(r.MachineId))
                .Select(r => r.MachineId), StringComparer.Ordinal);

        // 「本次排了几台」—— 数结果，不抄规则
        var used = (res?.Assignments ?? new List<MachineAssignment>())
            .GroupBy(a => a.MachineKind)
            .ToDictionary(g => g.Key, g => g.Select(a => a.MachineId).Distinct(StringComparer.Ordinal).Count());

        foreach (var g in machines.Where(m => m != null).GroupBy(m => m.Kind).OrderBy(g => (int)g.Key))
        {
            var row = new RateCoverageRow
            {
                Kind = g.Key,
                KindText = KindName(g.Key),
                OnRoll = g.Count(),
                Dispatchable = g.Count(m => m.Dispatchable),
                UsedThisRun = used.TryGetValue(g.Key, out int u) ? u : 0,
                OwnMeasured = g.Count(m => ownMeasured.Contains(m.MachineId)),
            };

            var hist = new Dictionary<RateSource, int>();
            foreach (var m in g)
            {
                var hit = book.Resolve(m, inp.Year, inp.Month);
                var s = hit.Ok ? hit.Source : RateSource.Unresolved;
                hist[s] = (hist.TryGetValue(s, out int c) ? c : 0) + 1;
            }
            foreach (var kv in hist)
            {
                if (kv.Key == RateSource.Unresolved) row.Unresolved += kv.Value;
                else if (RateBook.IsMeasured(kv.Key)) row.ResolvedMeasured += kv.Value;
                else row.ResolvedDefault += kv.Value;
            }
            row.LevelText = string.Join("；", hist.OrderBy(kv => (int)kv.Key)
                                                 .Select(kv => $"{RateBook.Label(kv.Key)}×{kv.Value}"));

            var v = new List<string>();
            if (row.NoMeasuredAtAll)
                v.Add("◆ 一台实测台效都没有（capacity_monthly 里这一类是空的）"
                    + (row.ResolvedDefault > 0 ? $"，{row.ResolvedDefault} 台走型号字典/类别兜底缺省" : "")
                    + (row.Unresolved > 0 ? $"，{row.Unresolved} 台连缺省都没有" : ""));
            else if (row.ResolvedMeasured > row.OwnMeasured)
                v.Add($"· {row.ResolvedMeasured - row.OwnMeasured} 台是借【同型号别的台】的实测中位数，不是它自己的实测");
            if (row.Unresolved > 0)
                v.Add($"◆ {row.Unresolved} 台解不出台效 ⇒ 不可派（这是【没有这条记录】，不是台效为 0）");
            if (row.OnRoll > 0 && row.Dispatchable == 0)
                v.Add("· 这一类没有一台状态是「在用」");
            row.Verdict = string.Join("　", v);

            rows.Add(row);
        }
        return rows;
    }

    /// <summary>覆盖表的一句话结论 —— <b>专抓「整类没有实测」</b>。空 = 每一类都至少有实测。</summary>
    public static string CoverageHeadline(IReadOnlyList<RateCoverageRow> cov)
    {
        if (cov == null || cov.Count == 0) return "";
        int onRoll = cov.Sum(c => c.OnRoll), own = cov.Sum(c => c.OwnMeasured);
        var sb = new StringBuilder();
        sb.Append($"台效实测覆盖：在册 {onRoll} 台里有 {own} 台自己有 capacity_monthly 记录");
        int borrowed = cov.Sum(c => Math.Max(0, c.ResolvedMeasured - c.OwnMeasured));
        int def = cov.Sum(c => c.ResolvedDefault), un = cov.Sum(c => c.Unresolved);
        if (borrowed > 0) sb.Append($"，{borrowed} 台借同型号实测");
        if (def > 0) sb.Append($"，{def} 台走缺省【非实测】");
        if (un > 0) sb.Append($"，{un} 台解不出（不可派）");
        sb.Append('。');

        var none = cov.Where(c => c.NoMeasuredAtAll).ToList();
        if (none.Count > 0)
            // ⚠ 这一句里【不要再举别的类别当例子】（比如"本引擎只排电铲/前装机"）——
            //   被点名的清单和举例混在同一行，读的人分不清「电铲」是被点名了还是被举例了。
            sb.Append("\n◆ 整类一条实测台效都没有："
                    + string.Join("、", none.Select(c => $"{c.KindText} {c.OnRoll} 台"))
                    + " —— 这几类的台效【全是缺省或解不出】，别当成实测；"
                    + "本引擎也只排挖装设备 + 与之绑定的卡车，其余类别不进排班。");
        return sb.ToString();
    }

    /// <summary>
    /// 状态区那一段：<b>接在 <c>UnitPlanResult.Report()</c> 后面</b>的设备维。
    /// <para>汇总一律用 <see cref="EquipmentAssignResult.Report"/> 那一份，这里只加
    /// 【口径来源】【自洽校核】【覆盖表结论】三样它没有的。</para>
    /// </summary>
    public static string Compose(EquipmentAssignResult? res, FleetResolution? fleet,
                                 EquipmentAssignInput? inp, IReadOnlyList<RateCoverageRow> cov,
                                 IEnumerable<string>? provenance = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("── 设备指派（排产【之后】算的，不进 UnitPlanEngine）──");
        foreach (var p in provenance ?? Enumerable.Empty<string>()) sb.AppendLine("  " + p);

        if (fleet != null) sb.AppendLine("  设备维：" + fleet.Summary());
        if (fleet != null)
            foreach (var n in fleet.Notes) sb.AppendLine("  " + n);

        if (res == null) { sb.Append("  （没跑起来）"); return sb.ToString(); }
        sb.AppendLine(res.Report().TrimEnd());

        var bad = res.Validate();
        sb.AppendLine(bad.Count == 0
            ? "  自洽校核：通过（设备不冲突 · 量守恒 · 承运量对得上 · 欠产都有原因）"
            : "  ◆ 自洽校核没过 " + bad.Count + " 条：\n    " + string.Join("\n    ", bad));

        string head = CoverageHeadline(cov);
        if (head.Length > 0) sb.AppendLine("  " + head.Replace("\n", "\n  "));
        return Plain(sb.ToString().TrimEnd());
    }

    /// <summary>
    /// 去掉上游留条里的 <c>&lt;b&gt;</c> 标记。
    /// <para>引擎的 Notes 里带 HTML 粗体标记，而状态区是个 <c>TextBlock</c>：它<b>原样显示</b>这几个尖括号。
    /// 只在这一层去，<b>不改上游那些文件</b>（它们不是本次的活儿）；去的只是标记，一个字都没动。</para>
    /// </summary>
    private static string Plain(string s)
        => s.Replace("<b>", "").Replace("</b>", "").Replace("<B>", "").Replace("</B>", "");

    /// <summary>
    /// 一行摘要（折叠时的面板标题）。
    /// <para><b>「实测 N/M 笔」只说被排上的那几笔</b> —— 只有 1 台铲上工时它就是「1/1」，
    /// 看着像全是实测。所以把 <paramref name="cov"/> 一起传进来：整类没有实测的那几类
    /// <b>在标题上就要能看见</b>（面板默认是折叠的，标题往往是唯一看得到的一行）。</para>
    /// </summary>
    public static string Headline(EquipmentAssignResult? res, IReadOnlyList<RateCoverageRow>? cov = null)
    {
        string tail = "";
        if (cov != null)
        {
            var none = cov.Where(c => c.NoMeasuredAtAll).ToList();
            if (none.Count > 0)
                tail = " · ◆ " + string.Join("/", none.Select(c => c.KindText)) + " 无实测台效";
        }
        if (res == null) return "还没指派 —— 先「按目标排产」" + tail;
        if (!res.Success) return "◆ 指派失败：" + res.Error;
        // ★「全部派出」这四个字必须看【三个工序】：只看采装的话，
        //   穿孔欠了 50 万方、这个月根本开不了挖，标题上照样写着"全部派出"。
        //   面板缺省是折叠的，标题常常是唯一看得到的一行。
        string shortText = res.ShortM3 > 1e-6 ? $"◆ 欠产 {res.ShortM3 / 1e4:0.0}万m³" : "";
        if (res.DrillShortM3 > 1e-6)
            shortText += (shortText.Length > 0 ? " · " : "") + $"◆ 穿孔欠 {res.DrillShortM3 / 1e4:0.0}万m³";
        if (res.DozeShortM3 > 1e-6)
            shortText += (shortText.Length > 0 ? " · " : "") + $"◆ 排土欠 {res.DozeShortM3 / 1e4:0.0}万m³";
        if (shortText.Length == 0) shortText = "全部派出";

        return $"挖装 {res.LoadersUsed} 台 / 卡车 {res.TrucksUsed} 台"
             + (res.DrillingScheduled ? $" / 钻机 {res.DrillsUsed} 台" : "")
             + (res.DozingScheduled ? $" / 推土机 {res.DozersUsed} 台" : "")
             + $" · 台班 {res.Excavation.Sum(a => a.Shifts):0.#} · "
             + shortText
             + $" · 上工的这几笔台效实测 {res.MeasuredRateHits}/{res.RateTotal}"
             + tail;
    }

    /// <summary>类别名。<b>认不出的原样显示枚举名</b>（不并进「其他」——并进去等于新类别静默消失）。</summary>
    public static string KindName(MachineKind k) => k switch
    {
        MachineKind.Shovel => "电铲",
        MachineKind.Truck => "卡车",
        MachineKind.Drill => "钻机",
        MachineKind.Loader => "前装机",
        MachineKind.Dozer => "推土机",
        MachineKind.Grader => "平路机",
        MachineKind.WaterTruck => "洒水车",
        MachineKind.Other => "其他",
        _ => k.ToString(),
    };

    /// <summary>角色名（与 <c>EquipmentAssigner.ToCsv</c> 同一套字眼）。</summary>
    public static string RoleName(MachineRole r) => r switch
    {
        MachineRole.Excavate => "挖装",
        MachineRole.Haul => "运输",
        MachineRole.Relocate => "转场",
        MachineRole.Drill => "穿孔",
        MachineRole.Dump => "排土",
        _ => r.ToString(),
    };

    // ── 期次 → 年月 ───────────────────────────────────────────────────────────

    private static readonly System.Text.RegularExpressions.Regex YmRe = new(
        @"^\s*(\d{4})\s*(?:[-/.年_]\s*)?(\d{1,2})\s*月?\s*$",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 认 <c>2025-06 / 2025-6 / 2025/06 / 2025.06 / 202506 / 2025年6月</c>。
    /// <para><b>认不出就返回 false</b>，调用方必须停下来问人，<b>绝不退到"当前系统月"</b> ——
    /// 那会让台效按今天的年月去挑记录，一条都命中不了，然后静默退到「各月中位数」，
    /// 而排出来的班表和真的命中了本月长得一模一样，事后谁也分不出。</para>
    /// </summary>
    public static bool TryParseYm(string? s, out int year, out int month)
    {
        year = 0; month = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var m = YmRe.Match(s.Trim());
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, out year)) return false;
        if (!int.TryParse(m.Groups[2].Value, out month)) return false;
        if (year < 1900 || year > 2999) return false;
        return month is >= 1 and <= 12;
    }
}
