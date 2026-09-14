using System;
using System.Collections.Generic;
using System.Linq;

namespace PitMine3D.Kylin.Cad.Tasks.Scheduling;

/// <summary>花名册里的一个人。</summary>
public sealed class CrewMember
{
    /// <summary>工号。<b>姓名不是主键</b> —— 花名册里两个"张建国"很常见，对号要认它。</summary>
    public string PersonId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>工种：操作手 / 司机 / 辅助。</summary>
    public string Job { get; set; } = "";
    /// <summary>持证类别：电铲 / 液压铲 / 钻机 / 推土机 / 矿卡。空 = 无证。</summary>
    public string CertFor { get; set; } = "";
    public string Phone { get; set; } = "";
    /// <summary>是否在岗（请假/休班置 false，派工时不参与自动匹配）。</summary>
    public bool OnDuty { get; set; } = true;

    public string Caption => $"{Name}（{Job}{(string.IsNullOrWhiteSpace(CertFor) ? "" : "·证:" + CertFor)}{(OnDuty ? "" : "·休")}）";
}

/// <summary>一辆车配到的司机。<b>车号 ↔ 司机是显式配对</b>，不靠"两个列表按下标对齐"。</summary>
public sealed class TruckDriver
{
    public string TruckId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>工号。姓名会重，对号要认它。</summary>
    public string PersonId { get; set; } = "";
}

/// <summary>一个编组一个班次的派工记录。</summary>
public sealed class CrewAssignment
{
    public string PlanDate { get; set; } = "";
    public string Shift { get; set; } = "";
    public string MainEquipment { get; set; } = "";

    public string Operator { get; set; } = "";
    /// <summary>操作手工号（姓名重名时唯一能对准的东西；花名册里认不出来时为空）。</summary>
    public string OperatorId { get; set; } = "";

    /// <summary>司机姓名列表（界面编辑态的形态）。<b>下游别用它</b>：它没有车号，只能靠下标猜配对。</summary>
    public List<string> Drivers { get; set; } = new();

    /// <summary>车号 ↔ 司机的显式配对，<b>下游唯一该读的那份</b>。</summary>
    public List<TruckDriver> TruckDrivers { get; set; } = new();

    /// <summary>持证校核结论（由花名册比对生成，<b>不是手填</b>）。</summary>
    public string CertNote { get; set; } = "";
    public string AttendNote { get; set; } = "";

    public string SavedBy { get; set; } = "";
    public DateTime SavedAt { get; set; } = DateTime.Now;

    /// <summary>派工键：日期 + 班次 + 主设备。</summary>
    public static string KeyOf(string? date, string? shift, string? equip)
        => $"{(date ?? "").Trim()}|{(shift ?? "").Trim()}|{(equip ?? "").Trim().ToLowerInvariant()}";
    public string Key => KeyOf(PlanDate, Shift, MainEquipment);
}

/// <summary>派工行的种类 —— 决定它要不要派人、要不要落盘、要不要校持证。</summary>
public enum CrewRowKind
{
    /// <summary>主设备编组（电铲/钻机/推土机）：操作手 + 配属车司机。</summary>
    Main,
    /// <summary>爆破：按已定口径<b>不指人</b>（没有爆破队台账）。只列一行说明，不派、不存、不校。</summary>
    Blast,
}

/// <summary>派工表的一行。</summary>
public sealed class CrewRow
{
    public CrewRowKind Kind { get; set; } = CrewRowKind.Main;
    /// <summary>这一行要不要派人（爆破行为假）。界面据此不把它算进"待处理"。</summary>
    public bool NeedsCrew => Kind != CrewRowKind.Blast;

    public string Shift { get; set; } = "";
    public string Group { get; set; } = "";
    public string Main { get; set; } = "";
    /// <summary>设备类别（电铲/钻机/推土机…）—— 持证匹配的依据。</summary>
    public string Category { get; set; } = "";
    /// <summary>本编组需要的卡车司机人数（＝实配车数）。</summary>
    public int TruckCount { get; set; }
    /// <summary>本编组的配车车号（顺序即司机列的填写顺序）。保存时按位配成 车号 ↔ 司机。</summary>
    public List<string> TruckIds { get; set; } = new();

    public string Operator { get; set; } = "";
    /// <summary>司机名（顿号分隔，界面编辑态）。</summary>
    public string Drivers { get; set; } = "";

    public string Cert { get; set; } = "";
    public string Attend { get; set; } = "";

    public string OperatorId { get; set; } = "";
    public List<string> DriverIds { get; set; } = new();
}

/// <summary>校核汇总。</summary>
public sealed class CrewCheck
{
    public int OkCount;
    public int IssueCount;
    public int Filled;
    public List<string> Shortfall = new();

    public string Summary => IssueCount == 0
        ? $"持证与出勤校核通过（{OkCount} 个编组）"
        : $"{IssueCount} 个编组有问题（持证/出勤/缺人），{OkCount} 个通过";
}

/// <summary>
/// 班组派工：把操作手 / 司机配到设备编组，并做<b>持证与出勤校核</b>
/// （移植原 <c>TaskLib.Features.CrewAssignWindow</c> 的口径部分）。
///
/// <para>
/// 原实现里人员一律显示"（待派）"、保存是个空方法 —— 等于这个窗口从来没有派过工。
/// 现在：<b>自动派工按【设备类别 ↔ 持证类别】匹配</b>并避开休班与重复占用；
/// <b>持证校核是真校核</b> —— 填进去的人若不在花名册、或证件类别与设备对不上、或今天休班，
/// 都会直说，而不是一律打勾。
/// </para>
///
/// <para>
/// <b>★ 姓名不是主键</b>：花名册里两个"张建国"时，随手 <c>First()</c> 会<b>静默</b>挑一个去校持证 ——
/// 挑错了界面上一点异常也看不出来。这里同名的一律<b>不认</b>，当场点名要求用工号区分。
/// </para>
///
/// ── Kylin 侧登记的差异 ──
/// <list type="number">
///   <item><b>不生成样例花名册</b>。原版首次运行会写一份样例 roster.json；Kylin 不编 ——
///     编出来的是<b>人名</b>，会被当成真人派工、写进单据、发到班组。
///     花名册为空时如实说"先录人"并给补法（同 §三五四 那条"样例不兜底"）。</item>
///   <item>辅助设备行（推土/平路/洒水）依赖未移植的辅助设备编组，本轮只出主设备行与爆破说明行。</item>
///   <item>设备类别取自 <c>equipment.category</c>；取不到时按类别为空处理，
///     持证一律判"对不上"并说明是<b>类别未知</b>，不因此放行。</item>
/// </list>
/// </summary>
public static class CrewAssignModel
{
    public const string JobOperator = "操作手";
    public const string JobDriver = "司机";
    public const string JobAux = "辅助";
    public static readonly string[] Jobs = { JobOperator, JobDriver, JobAux };

    /// <summary>矿卡的持证类别名（司机匹配用；只此一份）。</summary>
    public const string CertTruck = "矿卡";

    private static readonly char[] NameSeparators = { '、', ',', '，', '/', ' ' };
    public const string Unassigned = "（待派）";

    /// <summary>把一班的任务折成派工行：一台主设备一行；爆破单出一条说明行。</summary>
    public static List<CrewRow> BuildRows(IEnumerable<ShiftTask>? tasks, string shift,
                                          IReadOnlyDictionary<string, string>? categories = null)
    {
        var rows = new List<CrewRow>();
        var list = (tasks ?? Enumerable.Empty<ShiftTask>())
            .Where(t => t != null && t.Process != ProcessType.Idle)
            .Where(t => string.IsNullOrWhiteSpace(shift) || string.Equals(t.Shift, shift, StringComparison.Ordinal))
            .ToList();

        foreach (var g in list.Where(t => t.Process != ProcessType.Blast)
                              .Where(t => t.Group.MainEquipment.Length > 0)
                              .GroupBy(t => t.Group.MainEquipment, StringComparer.OrdinalIgnoreCase)
                              .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var first = g.First();
            var trucks = g.SelectMany(t => t.Group.Trucks).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            rows.Add(new CrewRow
            {
                Kind = CrewRowKind.Main,
                Shift = first.Shift,
                Main = g.Key,
                Category = categories != null && categories.TryGetValue(g.Key, out var c) ? c : "",
                Group = trucks.Count > 0 ? $"{g.Key}（配 {trucks.Count} 车）" : g.Key,
                TruckCount = trucks.Count,
                TruckIds = trucks,
            });
        }

        if (list.Any(t => t.Process == ProcessType.Blast))
            rows.Add(new CrewRow
            {
                Kind = CrewRowKind.Blast, Shift = shift, Main = "爆破", Group = "爆破",
                Cert = "按已定口径不指人（没有爆破队台账，编一个队号出来是假的）",
                Attend = "—",
            });

        return rows;
    }

    /// <summary>
    /// 自动派工：先按【设备类别 == 持证类别】匹配，匹配不上再退到任意在岗同工种。
    /// 避开休班与本班已占用的人；<b>爆破行不塞人</b>；人手不足<b>点名</b>，不静默留空。
    /// </summary>
    public static CrewCheck AutoAssign(IList<CrewRow> rows, IReadOnlyList<CrewMember>? roster)
    {
        var res = new CrewCheck();
        var all = (roster ?? Array.Empty<CrewMember>()).Where(m => m != null).ToList();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (r.Kind == CrewRowKind.Blast) continue;
            if (IsName(r.Operator)) used.Add(r.Operator.Trim());
            foreach (var d in SplitNames(r.Drivers)) used.Add(d);
        }

        var ops = all.Where(m => m.Job == JobOperator && m.OnDuty).ToList();
        var drivers = all.Where(m => m.Job == JobDriver && m.OnDuty).ToList();

        foreach (var r in rows)
        {
            if (r.Kind == CrewRowKind.Blast) continue;   // 按口径不指人，自动派工也不许给它塞一个

            if (!IsName(r.Operator))
            {
                var pick = ops.FirstOrDefault(m => !used.Contains(m.Name) && SameCert(m.CertFor, r.Category))
                        ?? ops.FirstOrDefault(m => !used.Contains(m.Name));
                if (pick != null) { r.Operator = pick.Name; used.Add(pick.Name); res.Filled++; }
                else res.Shortfall.Add($"{r.Main} 无可派操作手");
            }

            if (r.TruckCount > 0)
            {
                var cur = SplitNames(r.Drivers).ToList();
                while (cur.Count < r.TruckCount)
                {
                    var pick = drivers.FirstOrDefault(m => !used.Contains(m.Name) && SameCert(m.CertFor, CertTruck))
                            ?? drivers.FirstOrDefault(m => !used.Contains(m.Name));
                    if (pick == null) { res.Shortfall.Add($"{r.Main} 缺 {r.TruckCount - cur.Count} 名司机"); break; }
                    cur.Add(pick.Name); used.Add(pick.Name); res.Filled++;
                }
                while (cur.Count < r.TruckCount) cur.Add(Unassigned);
                r.Drivers = string.Join("、", cur);
            }
        }

        var chk = Recheck(rows, all);
        res.OkCount = chk.OkCount;
        res.IssueCount = chk.IssueCount;
        return res;
    }

    /// <summary>逐行比对花名册，写出持证与出勤结论（<b>真校核，不是一律打勾</b>）。</summary>
    public static CrewCheck Recheck(IList<CrewRow> rows, IReadOnlyList<CrewMember>? roster)
    {
        var res = new CrewCheck();
        var all = (roster ?? Array.Empty<CrewMember>()).Where(m => m != null).ToList();

        // ★ 姓名不是主键：同名的一律不认，当场点名要求用工号区分 —— 不猜
        var groups = all.GroupBy(m => (m.Name ?? "").Trim(), StringComparer.OrdinalIgnoreCase).ToList();
        var byName = groups.Where(g => g.Count() == 1)
                           .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var dupNames = groups.Where(g => g.Count() > 1)
                             .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        CrewMember? Resolve(string name, List<string> notes)
        {
            string n = (name ?? "").Trim();
            if (dupNames.TryGetValue(n, out int c))
            {
                notes.Add($"{n} 花名册里有 {c} 个同名，按姓名对不准（请在花名册里去重或用工号区分）");
                return null;
            }
            return byName.TryGetValue(n, out var m) ? m : null;
        }

        foreach (var r in rows)
        {
            if (r.Kind == CrewRowKind.Blast) continue;   // 说明行：不校持证、不算"待处理"

            var certNotes = new List<string>();
            var absent = new List<string>();

            r.OperatorId = "";
            if (!IsName(r.Operator)) certNotes.Add("操作手待派");
            else
            {
                int before = certNotes.Count;
                var op = Resolve(r.Operator, certNotes);
                if (op == null)
                {
                    if (certNotes.Count == before) certNotes.Add($"{r.Operator} 不在花名册");
                }
                else
                {
                    r.OperatorId = op.PersonId;          // 工号存下来，下游按它对号
                    if (!op.OnDuty) absent.Add(op.Name);
                    if (op.Job != JobOperator) certNotes.Add($"{op.Name} 工种为{op.Job}");
                    else if (!SameCert(op.CertFor, r.Category))
                        certNotes.Add($"{op.Name} 持{(string.IsNullOrWhiteSpace(op.CertFor) ? "无" : op.CertFor)}证 ≠ "
                                    + (r.Category.Length > 0 ? r.Category : "该设备类别未知"));
                }
            }

            var ds = SplitNames(r.Drivers).ToList();
            r.DriverIds = new List<string>();
            if (r.TruckCount > 0)
            {
                int pending = r.TruckCount - ds.Count;
                if (pending > 0) certNotes.Add($"缺 {pending} 名司机");
                foreach (var d in ds)
                {
                    int before = certNotes.Count;
                    var dm = Resolve(d, certNotes);
                    if (dm == null)
                    {
                        if (certNotes.Count == before) certNotes.Add($"{d} 不在花名册");
                        r.DriverIds.Add("");
                        continue;
                    }
                    r.DriverIds.Add(dm.PersonId);
                    if (!dm.OnDuty) absent.Add(dm.Name);
                    if (dm.Job != JobDriver) certNotes.Add($"{dm.Name} 工种为{dm.Job}");
                }
            }

            r.Cert = certNotes.Count == 0 ? "✓ 持证齐全" : "⚠ " + string.Join("；", certNotes);
            r.Attend = absent.Count == 0 ? "全勤" : "⚠ 休班：" + string.Join("、", absent);
            if (certNotes.Count == 0 && absent.Count == 0) res.OkCount++; else res.IssueCount++;
        }
        return res;
    }

    /// <summary>
    /// 一行 → 一条派工记录。<b>车号 ↔ 司机按位显式配对</b>（不靠下标对齐去猜）；
    /// 爆破行返回 null（不落盘）。
    /// </summary>
    public static CrewAssignment? ToAssignment(CrewRow r, string planDate, string shift, string by)
    {
        if (r == null || r.Kind == CrewRowKind.Blast) return null;
        var names = SplitNames(r.Drivers).ToList();
        var pairs = new List<TruckDriver>();
        for (int i = 0; i < r.TruckIds.Count && i < names.Count; i++)
        {
            if (!IsName(names[i])) continue;
            pairs.Add(new TruckDriver
            {
                TruckId = r.TruckIds[i], Name = names[i],
                PersonId = i < r.DriverIds.Count ? r.DriverIds[i] : "",
            });
        }
        return new CrewAssignment
        {
            PlanDate = planDate, Shift = shift, MainEquipment = r.Main,
            Operator = IsName(r.Operator) ? r.Operator.Trim() : "",
            OperatorId = r.OperatorId,
            Drivers = names.Where(IsName).ToList(),
            TruckDrivers = pairs,
            CertNote = r.Cert, AttendNote = r.Attend,
            SavedBy = by, SavedAt = DateTime.Now,
        };
    }

    /// <summary>把落盘的派工回填到行上（重开窗口后接着改）。</summary>
    public static void Apply(IList<CrewRow> rows, IReadOnlyDictionary<string, CrewAssignment> saved,
                             string planDate, string shift)
    {
        foreach (var r in rows)
        {
            if (r.Kind == CrewRowKind.Blast) continue;
            if (!saved.TryGetValue(CrewAssignment.KeyOf(planDate, shift, r.Main), out var a)) continue;
            r.Operator = a.Operator;
            r.OperatorId = a.OperatorId;
            r.Drivers = a.TruckDrivers.Count > 0
                ? string.Join("、", a.TruckDrivers.Select(td => td.Name))
                : string.Join("、", a.Drivers);
        }
    }

    /// <summary>本班某台主设备派到的人（给任务书那一列用）。</summary>
    public static string CrewTextOf(IReadOnlyDictionary<string, CrewAssignment>? saved,
                                    string planDate, string shift, string equip)
    {
        if (saved == null || equip.Length == 0) return "—";
        if (!saved.TryGetValue(CrewAssignment.KeyOf(planDate, shift, equip), out var a)) return "—";
        var parts = new List<string>();
        if (a.Operator.Length > 0) parts.Add(a.Operator);
        var names = a.TruckDrivers.Count > 0 ? a.TruckDrivers.Select(t => t.Name).ToList() : a.Drivers;
        if (names.Count > 0) parts.Add(string.Join("、", names));
        return parts.Count == 0 ? "—" : string.Join(" / ", parts);
    }

    /// <summary>持证类别与设备类别是否相容（同名即可；"液压铲/电铲"这类同族称谓互认）。</summary>
    public static bool SameCert(string? cert, string? category)
    {
        string c = (cert ?? "").Trim(), k = (category ?? "").Trim();
        if (c.Length == 0 || k.Length == 0) return false;
        if (string.Equals(c, k, StringComparison.OrdinalIgnoreCase)) return true;
        bool shovelC = c.Contains('铲'), shovelK = k.Contains('铲');
        return shovelC && shovelK;
    }

    /// <summary>是个真名字（不是空、也不是"（待派）"占位）。</summary>
    public static bool IsName(string? s)
    {
        string v = (s ?? "").Trim();
        return v.Length > 0 && v != Unassigned;
    }

    public static IEnumerable<string> SplitNames(string? s)
        => (s ?? "").Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0);
}
