// 忠实移植自原 PitMine3D Modules/PlanLib/ShortTerm/EquipmentAssignReportChecks.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Linq;
using PitMine3D.Kylin.Cad.Units;
namespace PitMine3D.Kylin.Cad.Plan;

// ─────────────────────────────────────────────────────────────────────────────
//  设备指派【接界面这一层】的判据 —— 全部离线（不碰数据库、不碰 WPF），能重放。
//
//  这一层要保的东西和引擎不一样：
//    引擎保的是「班表本身自洽」（EquipmentAssignResult.Validate 那五类）；
//    这一层保的是「界面上看到的那几个字对得上事实」——
//    台效是哪一级来的、哪几类整类没有实测、欠产有没有如实报、年月是不是猜出来的。
//
//  写判据的纪律（本仓库有前科）：只判"成功"的判据会空过。所以每条都同时钉住
//  【该发生什么】和【不该发生什么】：
//    · 判"整类没实测要点名"的同时判"有实测的类别不许被点名"；
//    · 判"欠产要报"的同时判"全派得出时不许出现欠产字样"；
//    · 判"年月认得出 2025-06"的同时判"认不出的必须返回 false"（恒真的解析器会被杀掉）。
//
//  ★ 变异验证 —— 下面每一条都<b>真的改了源码跑过一遍</b>（不是"跑绿了就算数"）：
//    M1 Coverage 只探可派设备              → K2 挂 2 条（推土机 1+0+0 ≠ 在册 2）  ✔ 杀掉
//    M2 NoMeasuredAtAll 恒 false           → K3 挂 4 条（整类无实测不点名了）     ✔ 杀掉
//    M3 NoMeasuredAtAll 恒 true            → K3 反向挂 2 条 + K4 挂 1 条          ✔ 杀掉
//    M4 UsedThisRun 照抄"引擎排哪几类"      → K5 挂 2 条（还没跑就有数）           ✔ 杀掉
//    M5 TryParseYm 不校验月份（恒真）       → K1 反向挂 2 条（2025-13 / 2025-00）  ✔ 杀掉
//    M6 Compose 吞掉「◆ 欠产」四个字        → K6 挂 1 条                          ✔ 杀掉
//
//  ⚠ <b>没做</b>的一条变异：把引擎里的欠产摊平（那要改 EquipmentAssigner.cs，不是本层的文件）。
//    K6 里那条「已派 + 欠产 = 需求」的守恒是对着结果算的，摊平会让它红 —— 但这一条<b>没验证过</b>，
//    别当成已经杀掉的变异。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary><see cref="EquipmentAssignReport"/> + 指派接线的离线判据。<see cref="RunAll"/> 返回失败描述，空 = 全过。</summary>
public static class EquipmentAssignReportChecks
{
    private const int Y = 2025, M = 6, WD = 25;

    private static Machine Mach(string id, MachineKind k, string model, bool ok = true)
        => new() { MachineId = id, Kind = k, Model = model, Dispatchable = ok };

    /// <summary>一条实测台效（台机级）。</summary>
    private static RateRecord Meas(string id, string model, MachineKind k, double m3PerDay, int year = Y, int month = M)
        => new()
        {
            MachineId = id, Model = model, Kind = k, Year = year, Month = month,
            M3PerDay = m3PerDay, Measured = true,
            RecordKey = $"capacity_monthly[{id},{year},{month:00}] ÷ {WD} 作业日",
        };

    /// <summary>一条型号字典缺省（<b>非实测</b>）。</summary>
    private static RateRecord Def(string model, MachineKind k, double m3PerDay)
        => new()
        {
            MachineId = "", Model = model, Kind = k, Year = 0, Month = 0,
            M3PerDay = m3PerDay, Measured = false,
            RecordKey = $"equipment_model[{model}].std_daily_cap_wan_m3（缺省，非实测）",
        };

    /// <summary>
    /// 合成一份设备维：
    /// 电铲 SH-1（自己有实测）· SH-2（同型号、自己没有 ⇒ 借同型号中位数）·
    /// 卡车 TK-1..3（自己有实测）· 推土机 DZ-1/DZ-2（<b>一条实测都没有</b>，只有型号字典缺省）·
    /// 平路机 GR-1（<b>连字典缺省都没有</b> ⇒ 解不出）。
    /// </summary>
    private static FleetResolution Fleet()
    {
        var f = new FleetResolution { DatabaseReady = true };
        f.Machines.Add(Mach("SH-1", MachineKind.Shovel, "WK-35"));
        f.Machines.Add(Mach("SH-2", MachineKind.Shovel, "WK-35"));
        f.Machines.Add(Mach("TK-1", MachineKind.Truck, "TR-100"));
        f.Machines.Add(Mach("TK-2", MachineKind.Truck, "TR-100"));
        f.Machines.Add(Mach("TK-3", MachineKind.Truck, "TR-100"));
        f.Machines.Add(Mach("DZ-1", MachineKind.Dozer, "SD-32"));
        f.Machines.Add(Mach("DZ-2", MachineKind.Dozer, "SD-32", ok: false));   // 不在用，但仍在册
        f.Machines.Add(Mach("GR-1", MachineKind.Grader, "PY-220"));

        f.Rates.Add(Meas("SH-1", "WK-35", MachineKind.Shovel, 12000));
        f.Rates.Add(Meas("TK-1", "TR-100", MachineKind.Truck, 5000));
        f.Rates.Add(Meas("TK-2", "TR-100", MachineKind.Truck, 5000));
        f.Rates.Add(Meas("TK-3", "TR-100", MachineKind.Truck, 5000));
        f.Rates.Add(Def("SD-32", MachineKind.Dozer, 3000));                    // 推土机只有缺省
        // PY-220 什么都没有 ⇒ 平路机解不出
        f.OnRoll = f.Machines.Count;
        f.Dispatchable = f.Machines.Count(m => m.Dispatchable);
        f.WithMeasuredRate = f.Rates.Where(r => r.Measured).Select(r => r.MachineId).Distinct().Count();
        return f;
    }

    /// <summary>合成排产结果：一个煤单元 + 一个岩单元。<paramref name="coalM3"/> 大 ⇒ 派不完 ⇒ 必须报欠产。</summary>
    private static List<UnitAssignment> Plan(double coalM3, double rockM3)
    {
        var a = new UnitAssignment { UnitId = "c4-B1-P1", Kind = UnitKind.Coal, Seq = 1, InSituM3 = coalM3, Fraction = 1, DoneAfter = 1 };
        var b = new UnitAssignment { UnitId = "岩1185-B1-P1", Kind = UnitKind.Rock, Seq = 2, InSituM3 = rockM3, Fraction = 1, DoneAfter = 1 };
        return new List<UnitAssignment> { a, b };
    }

    private static List<UnitSite> Sites() => new()
    {
        new UnitSite { UnitId = "c4-B1-P1", Cx = 1000, Cy = 2000, Cz = 1185 },
        new UnitSite { UnitId = "岩1185-B1-P1", Cx = 1050, Cy = 2050, Cz = 1197 },
    };

    private static EquipmentAssignInput Input(FleetResolution f, List<UnitAssignment> plan)
        => EquipmentFleetProvider.ToInput(f, plan, Sites(), Y, M, WD, 3);

    /// <summary>跑全部判据。返回失败描述（空列表 = 全过）。</summary>
    public static List<string> RunAll()
    {
        var fails = new List<string>();
        void Fail(string id, string what) => fails.Add($"[{id}] {what}");

        // ── K1 年月解析：认得出的要认，认不出的必须说认不出（双向）──────────────
        {
            foreach (var (s, y, m) in new[]
            {
                ("2025-06", 2025, 6), ("2025-6", 2025, 6), ("2025/06", 2025, 6),
                ("2025.06", 2025, 6), ("202506", 2025, 6), ("2025年6月", 2025, 6),
                (" 2025-12 ", 2025, 12),
            })
            {
                if (!EquipmentAssignReport.TryParseYm(s, out int gy, out int gm) || gy != y || gm != m)
                    Fail("K1", $"「{s}」应解析成 {y}-{m:00}，实际 {(gy == 0 ? "认不出" : $"{gy}-{gm:00}")}");
            }
            // 反向：这些必须认不出 —— 恒真的解析器会在这儿死
            foreach (var s in new[] { "", "  ", "六月", "2025-13", "2025-00", "abc", "25-06", "2025-6-1", "1899-06" })
                if (EquipmentAssignReport.TryParseYm(s, out int by, out int bm))
                    Fail("K1", $"「{s}」不该解析得出来，却给了 {by}-{bm:00} —— 年月猜出来就是编数");
        }

        var fleet = Fleet();

        // ── K2 覆盖表逐台守恒：实测 + 缺省 + 解不出 = 在册（一台都不许漏）──────────
        {
            var inp = Input(fleet, Plan(1e5, 1e5));
            var cov = EquipmentAssignReport.Coverage(fleet, inp, null);
            if (cov.Sum(c => c.OnRoll) != fleet.Machines.Count)
                Fail("K2", $"覆盖表在册合计 {cov.Sum(c => c.OnRoll)} ≠ 设备清单 {fleet.Machines.Count}");
            foreach (var c in cov)
                if (c.ResolvedMeasured + c.ResolvedDefault + c.Unresolved != c.OnRoll)
                    Fail("K2", $"{c.KindText}：实测{c.ResolvedMeasured} + 缺省{c.ResolvedDefault} + 解不出{c.Unresolved}"
                             + $" ≠ 在册{c.OnRoll} —— 有设备被探针跳过了（不可派的也得探）");
            var dz = cov.FirstOrDefault(c => c.Kind == MachineKind.Dozer);
            if (dz == null) Fail("K2", "推土机整类从覆盖表里消失了");
            else if (dz.OnRoll != 2 || dz.Dispatchable != 1)
                Fail("K2", $"推土机在册应 2 台、可派 1 台，实际 {dz.OnRoll}/{dz.Dispatchable}");
        }

        // ── K3 整类没有实测 ⇒ 必须点名；有实测的类别 ⇒ 不许被点名（双向）──────────
        {
            var inp = Input(fleet, Plan(1e5, 1e5));
            var cov = EquipmentAssignReport.Coverage(fleet, inp, null);
            var dz = cov.First(c => c.Kind == MachineKind.Dozer);
            var gr = cov.First(c => c.Kind == MachineKind.Grader);
            var sh = cov.First(c => c.Kind == MachineKind.Shovel);

            if (!dz.NoMeasuredAtAll) Fail("K3", "推土机一条实测台效都没有，却没标出来");
            if (dz.ResolvedDefault != 2) Fail("K3", $"推土机应有 2 台落到型号字典缺省，实际 {dz.ResolvedDefault}");
            if (dz.Verdict.Length == 0) Fail("K3", "推土机没有结论文字 —— 界面上看不见它是缺省");

            if (!gr.NoMeasuredAtAll) Fail("K3", "平路机一条实测台效都没有，却没标出来");
            if (gr.Unresolved != 1) Fail("K3", $"平路机应有 1 台解不出，实际 {gr.Unresolved}");

            // 反向：电铲有实测，不许被当成"整类没实测"（恒真的标记在这儿死）
            if (sh.NoMeasuredAtAll) Fail("K3", "电铲有实测台效，却被标成了「整类没有实测」");
            if (sh.OwnMeasured != 1) Fail("K3", $"电铲自有实测应 1 台（SH-1），实际 {sh.OwnMeasured}");

            // 只看【被点名的那一段】：整行里还有别的话，拿整行判会把举例当成点名（第一版就是这么假红的）
            string head = EquipmentAssignReport.CoverageHeadline(cov);
            const string mark = "整类一条实测台效都没有：";
            int at = head.IndexOf(mark, StringComparison.Ordinal);
            if (at < 0) Fail("K3", "结论行里根本没有「整类无实测」这一段：" + head);
            else
            {
                string named = head.Substring(at + mark.Length);
                int dash = named.IndexOf(" ——", StringComparison.Ordinal);
                if (dash > 0) named = named.Substring(0, dash);
                if (!named.Contains("推土机") || !named.Contains("平路机"))
                    Fail("K3", "被点名的清单里少了整类无实测的类别：" + named);
                if (named.Contains("电铲") || named.Contains("卡车"))
                    Fail("K3", "有实测的类别被点名进了「整类无实测」：" + named);
            }
        }

        // ── K4 借同型号的实测要说出来（不能冒充"这台自己的实测"）───────────────────
        {
            var inp = Input(fleet, Plan(1e5, 1e5));
            var cov = EquipmentAssignReport.Coverage(fleet, inp, null);
            var sh = cov.First(c => c.Kind == MachineKind.Shovel);
            if (sh.ResolvedMeasured != 2)
                Fail("K4", $"SH-2 应借 WK-35 的同型号实测（实测级 2 台），实际 {sh.ResolvedMeasured}");
            if (sh.ResolvedMeasured <= sh.OwnMeasured || !sh.Verdict.Contains("借"))
                Fail("K4", "SH-2 借的是同型号别的台的实测，界面上没说出来 —— 会被当成它自己的实测");
        }

        // ── K5 「本次排了」是数出来的，不是照抄规则：没跑就必须是 0/「—」──────────
        {
            var inp = Input(fleet, Plan(1e5, 1e5));
            var cov0 = EquipmentAssignReport.Coverage(fleet, inp, null);
            if (cov0.Any(c => c.UsedThisRun != 0))
                Fail("K5", "还没跑指派，「本次排了」就有数了 —— 那是照抄了「引擎排哪几类」的规则，不是数结果");
            if (cov0.Any(c => c.UsedText != "—"))
                Fail("K5", "没跑时「本次排了」应显示「—」，不是 0（0 是个真实的台数）");

            var res = EquipmentAssigner.Assign(inp);
            if (!res.Success) Fail("K5", "合成算例应当能派出来：" + res.Error);
            else
            {
                var cov1 = EquipmentAssignReport.Coverage(fleet, inp, res);
                var sh = cov1.First(c => c.Kind == MachineKind.Shovel);
                int shReal = res.Assignments.Where(a => a.MachineKind == MachineKind.Shovel)
                                            .Select(a => a.MachineId).Distinct().Count();
                if (sh.UsedThisRun != shReal)
                    Fail("K5", $"电铲「本次排了」{sh.UsedThisRun} ≠ 结果里真的用了 {shReal} 台");
                var dz = cov1.First(c => c.Kind == MachineKind.Dozer);
                if (dz.UsedThisRun != 0)
                    Fail("K5", "推土机根本不进排班，「本次排了」却不是 0");
            }
        }

        // ── K6 派不完要如实报欠产，且不许摊平（量守恒 + 引擎自洽）──────────────────
        {
            // 电铲台效 12000 m³/日 × 25 日 = 30 万 m³/台；两台铲最多 60 万。
            // 但卡车只有 3 台 × 5000 = 15000 m³/日 ≥ 铲台效，所以运力不是瓶颈；
            // 给 200 万 m³ ⇒ 一定派不完 ⇒ 必须报欠产。
            var inp = Input(fleet, Plan(1_000_000, 1_000_000));
            var res = EquipmentAssigner.Assign(inp);
            if (!res.Success) { Fail("K6", "应当能出结果（哪怕派不完）：" + res.Error); }
            else
            {
                var bad = res.Validate();
                if (bad.Count > 0) Fail("K6", "引擎自洽校核没过：" + string.Join("；", bad));
                if (res.ShortM3 <= 1e-6)
                    Fail("K6", $"200 万m³ 只有 2 台铲（每台月上限 30 万），必须报欠产，实际欠产 {res.ShortM3}"
                             + " —— 欠产被摊平了，总账是平的而「这个月干不干得完」再也问不出来");
                if (res.Shortfalls.Any(s => s.ShortM3 > 1e-6 && s.Reason.Length == 0))
                    Fail("K6", "有欠产却说不出原因");
                double exc = res.Excavation.Sum(a => a.AssignedM3);
                double sum = exc + res.Shortfalls.Sum(s => s.ShortM3);
                if (Math.Abs(sum - res.DemandM3) > Math.Max(1e-6, res.DemandM3 * 1e-9))
                    Fail("K6", $"量不守恒：已派 {exc:0} + 欠产 {res.Shortfalls.Sum(s => s.ShortM3):0} ≠ 需求 {res.DemandM3:0}");

                // 设备不冲突：从【吐出去的区间】重建逐日占用，不问引擎内部那份日历
                //（照着实现的内部状态判，等于没判）。
                var busy = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
                foreach (var a in res.Assignments)
                {
                    if (!busy.TryGetValue(a.MachineId, out var cal))
                        busy[a.MachineId] = cal = new Dictionary<int, string>();
                    string tag = (a.Role == MachineRole.Relocate ? "↷" : "") + a.UnitId;
                    for (int d = a.StartDay; d <= a.EndDay; d++)
                    {
                        if (cal.TryGetValue(d, out string? had) && !string.Equals(had, tag, StringComparison.Ordinal))
                            Fail("K6", $"设备 {a.MachineId} 第 {d} 日同时在「{had}」和「{tag}」");
                        cal[d] = tag;
                    }
                }

                string text = EquipmentAssignReport.Compose(res, fleet, inp,
                                  EquipmentAssignReport.Coverage(fleet, inp, res));
                if (!text.Contains("◆ 欠产")) Fail("K6", "欠产了，报告文案里却没有把欠产报出来");
                if (!text.Contains("推土机")) Fail("K6", "整类无实测的类别没进报告文案");
            }
        }

        // ── K7 全派得出时，不许出现「欠产」字样（K6 的反向，两条一起才封得住）────────
        {
            var inp = Input(fleet, Plan(50_000, 50_000));    // 10 万 m³，两台铲两天就干完
            var res = EquipmentAssigner.Assign(inp);
            if (!res.Success) Fail("K7", "小算例应当能全部派出：" + res.Error);
            else
            {
                if (res.ShortM3 > 1e-6) Fail("K7", $"10 万m³ 不该欠产，实际欠 {res.ShortM3:0}");
                string text = EquipmentAssignReport.Compose(res, fleet, inp,
                                  EquipmentAssignReport.Coverage(fleet, inp, res));
                if (text.Contains("◆ 欠产"))
                    Fail("K7", "全部派出了，文案里却报了欠产 —— 恒报欠产的文案等于没报");
                if (!text.Contains("全部派出")) Fail("K7", "全部派出这件事没说出来");
            }
        }

        // ── K8 退化输入不许炸：空设备维 / 空排产 / null ───────────────────────────
        {
            var empty = new FleetResolution();               // DatabaseReady = false，三个清单都空
            var inp = Input(empty, Plan(1e5, 1e5));
            var cov = EquipmentAssignReport.Coverage(empty, inp, null);
            if (cov.Count != 0) Fail("K8", "没有设备时覆盖表应当是空的");
            var res = EquipmentAssigner.Assign(inp);
            if (res.Success) Fail("K8", "一台设备都没有却报了成功");
            if (res.Error.Length == 0) Fail("K8", "指派失败却说不出原因");
            string text = EquipmentAssignReport.Compose(res, empty, inp, cov);
            if (text.Length == 0) Fail("K8", "指派失败时文案是空的 —— 界面上什么都看不见");

            if (EquipmentAssignReport.Coverage(null, inp, null).Count != 0)
                Fail("K8", "fleet=null 时覆盖表应为空（不许抛）");
            if (EquipmentAssignReport.CoverageHeadline(Array.Empty<RateCoverageRow>()).Length != 0)
                Fail("K8", "空覆盖表不该编出一句结论");

            var noUnit = Input(fleet, new List<UnitAssignment>());
            var r2 = EquipmentAssigner.Assign(noUnit);
            if (r2.Success) Fail("K8", "没有采掘单元却报了成功");
        }

        return fails;
    }

    /// <summary>
    /// 把<b>界面上真会显示的那几段字</b>用同一个合成算例打出来（台架/日志用）。
    /// <para>判据只能证伪：全绿也可能是盲区（本仓库有前科 —— 数值全对、形态是错的）。
    /// 这个方法不判任何东西，只负责让人用眼睛过一遍文案。</para>
    /// </summary>
    public static string SampleReport()
    {
        var fleet = Fleet();
        var inp = Input(fleet, Plan(1_000_000, 1_000_000));   // 故意派不完，好把欠产那一段也打出来
        var res = EquipmentAssigner.Assign(inp);
        var cov = EquipmentAssignReport.Coverage(fleet, inp, res);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("【面板标题（折叠时也看得见）】");
        sb.AppendLine("  " + EquipmentAssignReport.Headline(res, cov));
        sb.AppendLine();
        sb.AppendLine("【状态区那一段】");
        sb.AppendLine(EquipmentAssignReport.Compose(res, fleet, inp, cov, new[]
        {
            $"年月 {Y}-{M:00}（从期次「{Y}-{M:00}」解析）",
            $"作业日 {WD} 天，取自 【逐月配置表】{Y}-{M:00} 行（引擎派生）",
            "每日 3 班（【现场参数】的每日班次）· 台班数 = 工日 × 它",
        }));
        sb.AppendLine();
        sb.AppendLine("【台效来源覆盖（逐类别）】");
        sb.AppendLine($"  {"类别",-6}{"在册",4}{"可派",4}{"本次",5}{"自有实测",9}{"实测级",7}{"缺省级",7}{"解不出",7}  逐级明细｜结论");
        foreach (var c in cov)
            sb.AppendLine($"  {c.KindText,-6}{c.OnRoll,4}{c.Dispatchable,4}{c.UsedText,5}{c.OwnMeasured,9}"
                        + $"{c.ResolvedMeasured,7}{c.ResolvedDefault,7}{c.Unresolved,7}  {c.LevelText}｜{c.Verdict}");
        sb.AppendLine();
        sb.AppendLine($"【逐笔指派 —— 共 {res.Assignments.Count} 笔，前 8 笔】");
        foreach (var a in res.Assignments.Take(8)) sb.AppendLine("  " + a.Caption);
        return sb.ToString();
    }

    /// <summary>跑一遍并拼成可读报告（界面/日志直接贴）。</summary>
    public static string Report()
    {
        var f = RunAll();
        return f.Count == 0
            ? "设备指派接线判据：全部通过（K1 年月双向解析 · K2 覆盖表逐台守恒 · K3 整类无实测双向点名 · "
            + "K4 借同型号要说出来 · K5「本次排了」数结果不抄规则 · K6 欠产如实报不摊平 · "
            + "K7 全派出时不许假报欠产 · K8 退化输入不炸）"
            : $"设备指派接线判据：{f.Count} 条没过\n  " + string.Join("\n  ", f);
    }
}
