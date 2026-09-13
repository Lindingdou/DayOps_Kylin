// 忠实移植自原 PitMine3D Modules/MineAssLib/Driving/ShiftInference.cs（逐行对应；仅命名空间适配）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PitMine3D.Kylin.Data;                   // EquipmentDataContext
using PitMine3D.Kylin.Data.Entities;          // ShiftCalendar
namespace PitMine3D.Kylin.Cad.Units;

// ─────────────────────────────────────────────────────────────────────────────
//  排产结果（工日级）→ 班级推断。「这一班，哪台设备、在哪个单元、干多少」。
//
//  ══ 为什么要这一层 ══
//  `EquipmentAssigner` 解到的是「哪台设备、第几个工日到第几个工日、在哪个单元、共多少方」。
//  日常生产组织要的是**班**：这一班谁在哪、干多少。两者差着一次下推，而这次下推此前
//  是各干各的 —— 日常生产组织自己按「日目标 ÷ 班数 × 编组班产」再解一遍，
//  于是同一个月出**两套机号分配**，两边各自都自洽。
//
//  ══ 与「班次生产效能预测」的分工 ══
//  那个窗口吃的是**历史** `production_record`：从实绩统计推下一班能干多少（能力侧）。
//  本类吃的是**计划**：排产已经定了这一班该干多少（计划侧）。
//  两者互补 —— 计划量 ÷ 历史班产分布 = 达产概率，那才是「推断班的结果」的完整形态。
//  **绝不能拿其中一个冒充另一个**：计划量是"应该干多少"，历史均值是"通常能干多少"。
//
//  ══ 五条口径（E 组）══
//  **E1 三个量口径不许混**：采装=原位实方 / 穿孔=控制方量 / 排土=排弃占容。
//      每一行自带口径。混进同一列，谁一 SUM 就得到一个不对应任何真实量的数，且不报错。
//      **运输笔不计入采出量**（它承运的是别人挖出来的那一份），单列。
//  **E2 按班的有效时长占比摊，不是平均摊**。各班时长可以不同（两班倒 vs 三班），
//      而爆破清场落在哪一班，那一班就该少摊。平均摊会让爆破班的计划量高于它干得完的量，
//      而报表上三班一样多，看着还挺整齐。
//  **E3 一台设备同一班只许一笔**。排产是按笔给的，同一天可能跨两个单元 ——
//      逐班分配时按笔的先后填，填满一班换下一班。不管这条，一台铲会同时出现在两个单元上。
//  **E4 工日 → 日历日期走班次日历**（同 <see cref="DrillPlanWriter"/> 的 D2），
//      日历给不出足够作业日就**拒绝推**，不拿自然日顺延顶替。
//  **E5 摊完必须守恒**：各班之和 = 排产给的那一笔的量。差额要点名 ——
//      少摊的那部分不会有任何东西报错，只是月底对不上账。
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>推断出来的一条班级作业。</summary>
public sealed class ShiftDuty
{
    /// <summary>日历日期 yyyy-MM-dd。</summary>
    public string Date = "";
    /// <summary>班次名（取自 <c>shift_calendar.shift</c>）。</summary>
    public string Shift = "";
    /// <summary>本班起止（HH:mm，同日内）。</summary>
    public string StartTime = "", EndTime = "";
    /// <summary>本班有效小时（已扣爆破清场）。</summary>
    public double EffectiveHours;
    /// <summary>这一班是不是爆破班（清场要占时间）。</summary>
    public bool IsBlastShift;

    public string MachineId = "";
    public MachineRole Role;
    /// <summary>作业对象：排土笔装的是去向码，其余装采掘单元号（与排产同一约定）。</summary>
    public string UnitId = "";

    /// <summary>本班承担的量（<b>已按能力削过峰</b>，见 <see cref="CapM3"/>）。</summary>
    public double VolumeM3;

    /// <summary>
    /// 本班的能力上限 = 日台效 × 本班有效小时 ÷ 当日班长之和。<b>NaN = 排产没给台效，无从校核</b>。
    /// <para>「没给台效」与「能力刚好够」在结果上都是不削峰 —— 用 0 表达"没给"会把
    /// 每一班都判成超能力，用 NaN 才分得开。</para>
    /// </summary>
    public double CapM3 = double.NaN;

    /// <summary>本班被能力削掉的量（0 = 没削）。</summary>
    public double CappedM3;
    public bool WasCapped => CappedM3 > 1e-9;
    /// <summary><b>量口径</b>：原位实方 / 控制方量 / 排弃占容 / 承运量。三者不许并成一列。</summary>
    public string Basis = "";
    /// <summary>运输笔不计入采出量。</summary>
    public bool CountsAsOutput => Role is MachineRole.Excavate;

    public string Caption =>
        $"{Date} {Shift}班 {StartTime}-{EndTime}　{MachineId} @ {UnitId}　"
      + $"{VolumeM3:N0} m³（{Basis}）";
}

/// <summary>一次班级推断的结果。</summary>
public sealed class ShiftInferenceResult
{
    public List<ShiftDuty> Duties = new();
    public List<string> Notes = new();
    public string Headline = "";
    public int Skipped;
    public bool Ok => Duties.Count > 0;

    /// <summary>
    /// 被能力削掉、<b>本期排不下</b>的量（按口径分）。
    /// <para><b>必须单列，不能并进 <see cref="ByBasis"/></b>：那是"排下去了多少"，
    /// 这是"没排下去多少"。合成一个数之后，削峰就等于悄悄少摊 —— 月底才发现对不上账。</para>
    /// </summary>
    public List<(string Basis, double M3, string Why)> Shortfalls = new();

    public double ShortfallOf(string basis)
        => Shortfalls.Where(x => x.Basis == basis).Sum(x => x.M3);

    /// <summary>按量口径分开求和 —— <b>不提供"总量"</b>，那个数不对应任何真实量。</summary>
    public IEnumerable<(string Basis, double M3)> ByBasis
        => Duties.GroupBy(d => d.Basis).Select(g => (g.Key, g.Sum(x => x.VolumeM3)));
}

/// <summary>排产结果 → 班级作业。纯计算，不做排产。</summary>
public static class ShiftInference
{
    /// <summary>爆破清场占本班的小时数（E2）。清场是真占时间的 —— 不扣会让爆破班的计划量偏高。</summary>
    public const double BlastClearHours = 1.5;

    /// <summary>一天的班（供离线判据喂算例）。</summary>
    public readonly record struct ShiftSlot(string Name, double StartHour, double EndHour, bool IsBlast)
    {
        public double Hours => Math.Max(0, EndHour - StartHour);
        /// <summary>有效小时 = 班长 − 爆破清场。</summary>
        public double EffectiveHours => Math.Max(0, Hours - (IsBlast ? BlastClearHours : 0));
    }

    /// <summary>
    /// 推断。<b>离线判据喂算例走这一版</b>（日历与作业日都由调用方给）。
    /// </summary>
    /// <param name="assignments">排产笔（<c>EquipmentAssignResult.Assignments</c>）。</param>
    /// <param name="days">作业日清单（第 n 个工日 = days[n-1]）。</param>
    /// <param name="shiftsOf">某一天的班；返回空 = 那天排不出班。</param>
    public static ShiftInferenceResult Infer(
        IReadOnlyList<MachineAssignment>? assignments,
        IReadOnlyList<DateTime>? days,
        Func<DateTime, IReadOnlyList<ShiftSlot>> shiftsOf)
    {
        var res = new ShiftInferenceResult();
        var list = (assignments ?? Array.Empty<MachineAssignment>())
                   .Where(a => a != null && !string.IsNullOrWhiteSpace(a.MachineId)).ToList();
        if (list.Count == 0) { res.Headline = "没有排产笔，推不出班级作业。"; return res; }

        days ??= Array.Empty<DateTime>();
        if (days.Count == 0) { res.Headline = "作业日清单是空的 —— **拒绝推**（不拿自然日顺延顶替）。"; return res; }

        int need = list.Max(a => a.EndDay);
        if (need > days.Count)
        {
            res.Headline = $"排产用了 {need} 个工日，而作业日清单只有 {days.Count} 天 —— **拒绝推**。";
            res.Notes.Add("顺延出来的日期看着完全正常，而设备会被排到本来不上班的那天，谁也看不出来。");
            return res;
        }

        // ── 先摊到「设备 × 日」，再在那一天内部把班分给各笔 ──
        //
        //  **不能逐笔先到先得**：同一台设备同一天可能有两笔（上午在 U1、下午转到 U2）。
        //  先到先得会让第一笔把三个班全占走，第二笔一个班都拿不到 ——
        //  于是它那一天的量**凭空消失**，而两条任务各自都看着对，总量少了也没人报错。
        //  （E5b 就是钉这一条的：写这一层时第一版正是这么错的，判据当场把它抓了出来。）
        var perMachineDay = new Dictionary<(string Machine, int Day), List<(MachineAssignment A, double PerDay)>>();
        foreach (var a in list)
        {
            int nd = a.EndDay - a.StartDay + 1;
            if (nd <= 0) { res.Skipped++; continue; }
            double perDay = a.Role == MachineRole.Relocate ? 0 : a.AssignedM3 / nd;
            for (int d = a.StartDay; d <= a.EndDay; d++)
            {
                var key = (a.MachineId.Trim(), d);
                if (!perMachineDay.TryGetValue(key, out var bucket)) perMachineDay[key] = bucket = new();
                bucket.Add((a, perDay));
            }
        }

        foreach (var kv in perMachineDay.OrderBy(x => x.Key.Day).ThenBy(x => x.Key.Machine, StringComparer.Ordinal))
        {
            var date = days[kv.Key.Day - 1];
            string dk = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var slots = (shiftsOf(date) ?? Array.Empty<ShiftSlot>()).ToList();
            if (slots.Count == 0) { res.Skipped += kv.Value.Count; continue; }

            // 同机同日的多笔按排产序上班（上午干完这个再转下一个）
            var items = kv.Value.OrderBy(x => x.A.Seq).ThenBy(x => x.A.UnitId, StringComparer.Ordinal).ToList();

            // E3：一台设备同一班只许一笔 ⇒ 班数不够时排不下，**点名**而不是悄悄丢
            if (items.Count > slots.Count)
            {
                int drop = items.Count - slots.Count;
                res.Skipped += drop;
                res.Notes.Add($"◆ {dk} 的 {kv.Key.Machine} 被排了 {items.Count} 笔，而当天只有 {slots.Count} 个班 —— "
                            + $"**{drop} 笔排不下**（一台设备同一班不能在两处）。"
                            + "这几笔的量没有落到任何一个班上，月底会对不上账。"
                            + "补法：把排产的笔数压到班数以内，或加班制。");
                items = items.Take(slots.Count).ToList();
            }

            // 班按量的比例分给各笔：量大的多占几个班（每笔至少一个）
            double sumV = items.Sum(x => Math.Max(0, x.PerDay));
            var counts = new int[items.Count];
            for (int i = 0; i < items.Count; i++) counts[i] = 1;
            int rest = slots.Count - items.Count;
            if (rest > 0)
            {
                if (sumV > 1e-9)
                {
                    // 最大余数法分剩下的班：逐个四舍五入会多分或少分，而多分出来的那个班不报错
                    var quota = items.Select(x => rest * Math.Max(0, x.PerDay) / sumV).ToArray();
                    var baseN = quota.Select(q => (int)Math.Floor(q)).ToArray();
                    int used = baseN.Sum();
                    var order = Enumerable.Range(0, items.Count)
                                          .OrderByDescending(i => quota[i] - baseN[i]).ToList();
                    for (int k = 0; used < rest && k < order.Count; k++, used++) baseN[order[k]]++;
                    for (int i = 0; i < items.Count; i++) counts[i] += baseN[i];
                }
                else counts[0] += rest;                 // 全是转场笔：都归第一笔，占满这一天
            }

            int cursor = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var (a, perDay) = items[i];
                var mine = slots.Skip(cursor).Take(counts[i]).ToList();
                cursor += counts[i];
                if (mine.Count == 0) { res.Skipped++; continue; }

                // E2：本笔拿到的这几个班内部，按**有效时长占比**摊，不是平均摊
                double eff = mine.Sum(s => s.EffectiveHours);
                // 当日班长之和 —— 能力折算的分母（不扣爆破清场：台效本来就是按整班标定的）
                double dayHours = slots.Sum(s => s.Hours);

                foreach (var s in mine)
                {
                    double want = eff > 1e-9 ? perDay * s.EffectiveHours / eff : perDay / mine.Count;

                    // E9：本班能力 = 日台效 × 本班有效小时 ÷ 当日班长之和。
                    //     排产是按**整工日**算量的 —— 它不知道那天有爆破班（清场占 1.5h）。
                    //     摊到班之后量会超过这台设备一班干得完的量，而没有任何东西报错：
                    //     计划照出、甘特照画，只是到了现场干不完。
                    double cap = a.RateM3PerDay > 1e-9 && dayHours > 1e-9
                        ? a.RateM3PerDay * s.EffectiveHours / dayHours
                        : double.NaN;

                    double give = want, cut = 0;
                    if (!double.IsNaN(cap) && want > cap + 1e-9) { give = cap; cut = want - cap; }

                    res.Duties.Add(new ShiftDuty
                    {
                        Date = dk,
                        Shift = s.Name,
                        StartTime = DrillPlanWriter.Hhmm(s.StartHour),
                        EndTime = DrillPlanWriter.Hhmm(s.EndHour),
                        EffectiveHours = s.EffectiveHours,
                        IsBlastShift = s.IsBlast,
                        MachineId = a.MachineId.Trim(),
                        Role = a.Role,
                        UnitId = a.UnitId ?? "",
                        VolumeM3 = give,
                        CapM3 = cap,
                        CappedM3 = cut,
                        Basis = BasisOf(a.Role),
                    });

                    if (cut > 1e-9)
                        res.Shortfalls.Add((BasisOf(a.Role), cut,
                            $"{dk} {s.Name}班 {a.MachineId}@{a.UnitId}："
                          + $"按工日摊到 {want:N0} m³，而本班能力只有 {cap:N0} m³"
                          + (s.IsBlast ? $"（爆破班，清场占 {BlastClearHours:0.#}h）" : "")));
                }
            }
        }

        Summarize(list, res);
        return res;
    }

    /// <summary>按期推断（自己去读班次日历）。</summary>
    public static ShiftInferenceResult Infer(EquipmentAssignResult? result, string period)
    {
        var res = new ShiftInferenceResult();
        if (result == null || !result.Success)
        { res.Headline = "没有可用的排产结果。"; return res; }

        var days = new List<DateTime>();
        var byDate = new Dictionary<DateTime, List<ShiftSlot>>();
        try
        {
            if (!DateTime.TryParseExact((period ?? "").Trim() + "-01", "yyyy-MM-dd",
                                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
            { res.Headline = $"期次「{period}」不是 yyyy-MM。"; return res; }

            var last = first.AddMonths(1).AddDays(-1);
            foreach (var g in EquipmentDataContext.ShiftCalendar.InRange(first, last)
                                                  .Where(s => s != null)
                                                  .GroupBy(s => s.Date.Date).OrderBy(g => g.Key))
            {
                var slots = SlotsOf(g.ToList());
                if (slots.Count == 0) continue;
                days.Add(g.Key);
                byDate[g.Key] = slots;
            }
        }
        catch (Exception ex) { res.Headline = $"班次日历读不到（{ex.GetType().Name}）。"; return res; }

        var r = Infer(result.Assignments, days, d => byDate.TryGetValue(d, out var v) ? v : Array.Empty<ShiftSlot>());
        r.Notes.Insert(0, $"· 作业日取自班次日历（{days.Count} 天）—— 与排产的作业日数须一致，否则拒绝推。");
        return r;
    }

    /// <summary>
    /// 一天的班次日历行 → 班时段。
    /// <para><b>收班时刻是推出来的</b>：<c>shift_calendar</c> 只有开班时刻。
    /// 末班收班按各班开班间隔推，跨零点截到当日 24:00（同 <see cref="DrillPlanWriter"/> 的 D3）。</para>
    /// </summary>
    public static List<ShiftSlot> SlotsOf(IReadOnlyList<ShiftCalendar>? rows)
    {
        var list = new List<ShiftSlot>();
        var ok = (rows ?? Array.Empty<ShiftCalendar>())
                 .Where(r => r != null && DrillPlanWriter.ParseHour(r.StartTime) >= 0)
                 .Select(r => (Name: (r.Shift ?? "").Trim(), H: DrillPlanWriter.ParseHour(r.StartTime), r.IsBlastShift))
                 .OrderBy(x => x.H).ToList();
        if (ok.Count == 0) return list;

        double span = ok.Count >= 2
            ? DrillPlanWriter.Median(Enumerable.Range(1, ok.Count - 1).Select(i => ok[i].H - ok[i - 1].H).ToList())
            : 24.0;

        for (int i = 0; i < ok.Count; i++)
        {
            double end = i + 1 < ok.Count ? ok[i + 1].H : Math.Min(24.0, ok[i].H + span);
            list.Add(new ShiftSlot(ok[i].Name, ok[i].H, end, ok[i].IsBlastShift));
        }
        return list;
    }

    /// <summary>E1：量口径按角色定，<b>三者不许并成一列</b>。</summary>
    public static string BasisOf(MachineRole role) => role switch
    {
        MachineRole.Drill => "控制方量",
        MachineRole.Dump => "排弃占容",
        MachineRole.Haul => "承运量",          // 不计入采出量
        MachineRole.Relocate => "转场（不出量）",
        _ => "原位实方",
    };

    private static void Summarize(List<MachineAssignment> src, ShiftInferenceResult res)
    {
        // E5：逐口径守恒 —— 少摊的那部分不会有任何东西报错，只是月底对不上账
        foreach (var g in src.Where(a => a.Role != MachineRole.Relocate).GroupBy(a => BasisOf(a.Role)))
        {
            double want = g.Sum(a => a.AssignedM3);
            // ★ 守恒的是「摊出去的 + 排不下的」——削峰是**如实记账**，不是少摊。
            //   只判摊出去的那一半，削峰会被当成记账漏洞；只判总量，削峰会被藏起来。
            double got = res.Duties.Where(d => d.Basis == g.Key).Sum(d => d.VolumeM3)
                       + res.ShortfallOf(g.Key);
            if (Math.Abs(want - got) > Math.Max(1.0, want * 1e-6))
                res.Notes.Add($"◆ 「{g.Key}」摊到班之后是 {got:N0} m³，而排产给的是 {want:N0} m³ —— "
                            + $"差 {want - got:N0}。**这是记账漏洞**：少摊的量不会有任何东西报错，"
                            + "只是月底对不上账。请把这条报给开发。");
        }

        var byBasis = res.ByBasis.Select(x => $"{x.Basis} {x.M3 / 1e4:0.##} 万m³").ToList();
        res.Headline = $"推出 {res.Duties.Count} 条班级作业"
                     + $"（{res.Duties.Select(d => d.Date).Distinct().Count()} 天 · "
                     + $"{res.Duties.Select(d => d.MachineId).Distinct().Count()} 台设备）"
                     + (byBasis.Count > 0 ? "：" + string.Join(" · ", byBasis) : "");

        if (res.Shortfalls.Count > 0)
        {
            var byBasisShort = res.Shortfalls.GroupBy(x => x.Basis)
                .Select(x => $"{x.Key} {x.Sum(y => y.M3) / 1e4:0.##} 万m³").ToList();
            res.Notes.Add($"◆ **{res.Shortfalls.Count} 个班超出设备能力**，已削峰，"
                        + $"排不下的量：{string.Join(" · ", byBasisShort)}。"
                        + "根因通常是**排产按整工日算量，而那天有爆破班或班制不满**——"
                        + "台效是按整班标定的，清场占掉的时间干不出量来。"
                        + "这几笔不会自己消失：要么把它们排到后面的作业日，要么加班制，"
                        + "要么承认这个月的量排不下。**削掉不等于干完了**。");
            foreach (var x in res.Shortfalls.Take(5)) res.Notes.Add("　· " + x.Why);
            if (res.Shortfalls.Count > 5) res.Notes.Add($"　· …等 {res.Shortfalls.Count} 条");
        }

        int noCap = res.Duties.Count(d => double.IsNaN(d.CapM3));
        if (noCap > 0)
            res.Notes.Add($"· {noCap} 条**无从校核能力**（排产那一笔没给日台效）—— "
                        + "这些班的量没有被削峰检查过。**「没给台效」不等于「能力够」**。");

        int blast = res.Duties.Count(d => d.IsBlastShift);
        if (blast > 0)
            res.Notes.Add($"· {blast} 条落在**爆破班**上，那几班按扣掉 {BlastClearHours:0.#}h 清场后的"
                        + "有效时长摊量 —— 平均摊会让爆破班的计划量高于它干得完的量，"
                        + "而报表上三班一样多，看着还挺整齐。");
        res.Notes.Add("· **量口径分开列，不给总量**：原位实方 / 控制方量 / 排弃占容 / 承运量 "
                    + "是四本账，加起来那个数不对应任何真实量。运输笔不计入采出量。");
        if (res.Skipped > 0)
            res.Notes.Add($"· {res.Skipped} 笔·日因为那天排不出班、或该设备当班已被别的笔占住而跳过。");
    }
}
